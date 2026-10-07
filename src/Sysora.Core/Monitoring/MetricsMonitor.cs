using Microsoft.Extensions.Logging;
using Sysora.Core.Interfaces;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Settings;

namespace Sysora.Core.Monitoring;

/// <summary>
/// Default <see cref="IMetricsMonitor"/>: a single background loop that sleeps until the next metric is due,
/// collects all due metrics concurrently, publishes a new immutable snapshot and records chart history.
/// </summary>
/// <remarks>
/// Design goals: no busy loop, no timer per metric, nothing left running after <see cref="StopAsync"/>,
/// and a failing provider never stops the others (its metric is reported as unavailable instead).
/// </remarks>
public sealed class MetricsMonitor : IMetricsMonitor, IAsyncDisposable
{
    /// <summary>A provider taking longer than this is reported as unavailable for the round.</summary>
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Samples kept per chart series: the longest chart window at the fastest interval.</summary>
    public static readonly int HistoryCapacity =
        (int)(SettingsValidator.MaxChartWindow.TotalMilliseconds / SettingsValidator.MinIntervalMs);

    /// <summary>
    /// Consecutive skipped samples (<see cref="MetricSampleSkippedException"/>) tolerated before the metric is reported
    /// as unavailable. A single skip is normal (a counter wrapping around); repeated skips are not.
    /// </summary>
    public const int MaxConsecutiveSkips = 3;

    /// <summary>Uptime changes slowly; it does not need its own setting.</summary>
    private static readonly TimeSpan SystemInterval = TimeSpan.FromSeconds(5);

    private readonly MetricSource[] _sources;
    private readonly SettingsService _settings;
    private readonly ILogger<MetricsMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly MetricSchedule _schedule = new();
    private readonly SelfUsageGovernor _governor;
    private readonly Lock _lock = new();
    private readonly Dictionary<MetricKind, int> _failures = [];
    private readonly Dictionary<MetricKind, int> _skips = [];

    /// <summary>Consecutive measurements above the CPU budget before Sysora warns about its own usage (one minute).</summary>
    public const int SustainedOverBudgetMeasurements = 6;

    private SystemSnapshot _current = SystemSnapshot.Empty;
    private SelfUsage _selfUsage = new(null, 0, 1.0);
    private MonitoringActivity _activity = MonitoringActivity.Foreground;
    private MonitoringScheduleInfo _scheduleInfo = MonitoringScheduleInfo.Unknown;
    private bool _investigating;
    private long _rounds;
    private SelfCounters? _lastSelf;
    private int _overBudget;
    private bool _overBudgetLogged;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _sleepCts;
    private TaskCompletionSource _resumed = NewSignal();
    private Task _loop = Task.CompletedTask;
    private int _lastMeasurement;
    private bool _running;
    private bool _paused;

    public MetricsMonitor(
        MetricProviders providers,
        SettingsService settings,
        ILogger<MetricsMonitor> logger,
        TimeProvider? timeProvider = null,
        Func<TimeSpan>? processCpuTime = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _settings = settings;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _origin = _time.GetTimestamp();
        _governor = new SelfUsageGovernor(processCpuTime ?? (() => Environment.CpuUsage.TotalTime), Environment.ProcessorCount);

        _sources =
        [
            new MetricSource<CpuMetrics>(MetricKind.Cpu, providers.Cpu, (s, v) => s with { Cpu = v }),
            new MetricSource<MemoryMetrics>(MetricKind.Memory, providers.Memory, (s, v) => s with { Memory = v }),
            new MetricSource<IReadOnlyList<GpuMetrics>>(MetricKind.Gpu, providers.Gpu, (s, v) => s with { Gpus = v }),
            new MetricSource<IReadOnlyList<StorageMetrics>>(MetricKind.Storage, providers.Storage, (s, v) => s with { Storage = v }),
            new MetricSource<IReadOnlyList<DiskActivityMetrics>>(MetricKind.DiskActivity, providers.DiskActivity, (s, v) => s with { DiskActivity = v }),
            new MetricSource<NetworkMetrics>(MetricKind.Network, providers.Network, (s, v) => s with { Network = v }),
            new MetricSource<ProcessSnapshot>(MetricKind.Processes, providers.Processes, (s, v) => s with { Processes = v }),
            new MetricSource<SystemMetrics>(MetricKind.System, providers.System, (s, v) => s with { System = v }),
        ];

        _settings.Changed += OnSettingsChanged;
    }

    public event EventHandler<SystemMetricsUpdatedEventArgs>? MetricsUpdated;

    public event EventHandler? StateChanged;

    public SystemSnapshot Current => Volatile.Read(ref _current);

    public MetricHistory History { get; } = new(HistoryCapacity);

    public SelfUsage SelfUsage => Volatile.Read(ref _selfUsage);

    public MonitoringScheduleInfo ScheduleInfo => Volatile.Read(ref _scheduleInfo);

    public bool IsInvestigating
    {
        get
        {
            lock (_lock)
            {
                return _investigating;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    private TimeSpan Elapsed => _time.GetElapsedTime(_origin);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_running)
            {
                return Task.CompletedTask;
            }

            _running = true;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runCts = cts;
            ConfigureSchedule(_settings.Current.Monitoring);
            _loop = Task.Run(() => RunAsync(cts.Token), CancellationToken.None);
        }

        _logger.LogInformation("Monitoring started.");
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task loop;
        lock (_lock)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            cts = _runCts;
            _runCts = null;
            loop = _loop;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            cts?.Dispose();
        }

        _logger.LogInformation("Monitoring stopped.");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_paused)
            {
                return;
            }

            _paused = true;
            _resumed = NewSignal();
        }

        Wake();
        _logger.LogInformation("Monitoring paused.");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        TaskCompletionSource resumed;
        lock (_lock)
        {
            if (!_paused)
            {
                return;
            }

            _paused = false;
            resumed = _resumed;
        }

        resumed.TrySetResult();
        _logger.LogInformation("Monitoring resumed.");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetActivity(MonitoringActivity activity)
    {
        lock (_lock)
        {
            if (_activity == activity)
            {
                return;
            }

            _activity = activity;
            ConfigureSchedule(_settings.Current.Monitoring);
            if (activity == MonitoringActivity.Foreground)
            {
                // Bring on-screen metrics up to date immediately when the window comes back.
                _schedule.MarkDue(MetricKind.All, Elapsed);
            }
        }

        Wake();
    }

    public void SetInvestigationMode(bool enabled)
    {
        lock (_lock)
        {
            if (_investigating == enabled)
            {
                return;
            }

            _investigating = enabled;
            ConfigureSchedule(_settings.Current.Monitoring);
            if (enabled)
            {
                _schedule.MarkDue(MetricKind.All, Elapsed);
            }
        }

        _logger.LogInformation(enabled ? "Investigation started: detailed collection." : "Investigation ended: back to the {Intensity} intensity.", _settings.Current.Monitoring.Intensity);
        Wake();
    }

    public void RequestRefresh(MetricKind kinds)
    {
        lock (_lock)
        {
            _schedule.MarkDue(kinds, Elapsed);
        }

        Wake();
    }

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Task? resumed;
                lock (_lock)
                {
                    resumed = _paused ? _resumed.Task : null;
                }

                if (resumed is not null)
                {
                    await resumed.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                MetricKind due;
                lock (_lock)
                {
                    due = _schedule.TakeDue(Elapsed);
                }

                if (due != MetricKind.None)
                {
                    await CollectRoundAsync(due, cancellationToken).ConfigureAwait(false);
                }

                UpdateSelfUsage();

                TimeSpan wait;
                lock (_lock)
                {
                    wait = _schedule.TimeUntilNextDue(Elapsed) ?? TimeSpan.FromSeconds(1);
                }

                if (wait > TimeSpan.Zero)
                {
                    await SleepAsync(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Only a bug can get here: providers' exceptions are handled per metric.
            _logger.LogCritical(ex, "The monitoring loop stopped unexpectedly.");
            lock (_lock)
            {
                _running = false;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task CollectRoundAsync(MetricKind due, CancellationToken cancellationToken)
    {
        var sources = _sources.Where(s => (due & s.Kind) != 0 && !s.IsBusy).ToArray();
        if (sources.Length == 0)
        {
            return;
        }

        var results = await Task.WhenAll(sources.Select(s => CollectSafeAsync(s, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = Current;
        var unavailable = snapshot.Unavailable;
        var updated = MetricKind.None;
        for (var i = 0; i < sources.Length; i++)
        {
            var source = sources[i];
            switch (results[i])
            {
                case { Apply: { } apply }:
                    snapshot = apply(snapshot);
                    unavailable &= ~source.Kind;
                    break;
                case { Skipped: true }:
                    // No valid value this time: keep the metric as it is and do not report it as updated.
                    continue;
                default:
                    snapshot = source.Clear(snapshot);
                    unavailable |= source.Kind;
                    break;
            }

            updated |= source.Kind;
        }

        if (updated == MetricKind.None)
        {
            return;
        }

        Interlocked.Increment(ref _rounds);
        var timestamp = _time.GetUtcNow();
        snapshot = snapshot with { Timestamp = timestamp, Unavailable = unavailable };
        Volatile.Write(ref _current, snapshot);
        RecordHistory(snapshot, updated, timestamp);
        Publish(new SystemMetricsUpdatedEventArgs(snapshot, updated));
    }

    private async Task<CollectResult> CollectSafeAsync(MetricSource source, CancellationToken cancellationToken)
    {
        try
        {
            var apply = await source.CollectAsync(cancellationToken)
                .WaitAsync(ProviderTimeout, _time, cancellationToken)
                .ConfigureAwait(false);
            OnCollected(source.Kind);
            return new CollectResult(apply, Skipped: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MetricSampleSkippedException ex) when (OnSampleSkipped(source.Kind, ex))
        {
            return new CollectResult(null, Skipped: true);
        }
        catch (Exception ex)
        {
            OnCollectFailed(source.Kind, ex);
            return new CollectResult(null, Skipped: false);
        }
    }

    /// <summary>Returns true while skips are tolerated; false once they repeat too often (then handled as a failure).</summary>
    private bool OnSampleSkipped(MetricKind kind, MetricSampleSkippedException exception)
    {
        int skips;
        lock (_failures)
        {
            skips = _skips.GetValueOrDefault(kind) + 1;
            _skips[kind] = skips;
        }

        if (skips > MaxConsecutiveSkips)
        {
            return false;
        }

        _logger.LogDebug("{Metric} sample skipped: {Message}", kind, exception.Message);
        return true;
    }

    private void OnCollected(MetricKind kind)
    {
        int previousFailures;
        lock (_failures)
        {
            _failures.Remove(kind, out previousFailures);
            _skips.Remove(kind);
        }

        if (previousFailures > 0)
        {
            _logger.LogInformation("{Metric} metrics are available again after {Failures} failed attempt(s).", kind, previousFailures);
        }
    }

    private void OnCollectFailed(MetricKind kind, Exception exception)
    {
        int failures;
        lock (_failures)
        {
            failures = _failures.GetValueOrDefault(kind) + 1;
            _failures[kind] = failures;
        }

        // Log the first failure in full, then stay quiet: a metric missing on this machine
        // must not flood the log every second.
        if (failures == 1)
        {
            _logger.LogWarning(exception, "{Metric} metrics are not available.", kind);
        }
        else
        {
            _logger.LogDebug("{Metric} metrics still not available ({Failures} attempts): {Message}", kind, failures, exception.Message);
        }
    }

    private void RecordHistory(SystemSnapshot snapshot, MetricKind updated, DateTimeOffset timestamp)
    {
        if ((updated & MetricKind.Cpu) != 0 && snapshot.Cpu is { } cpu)
        {
            History.Record(SeriesKeys.Cpu, timestamp, cpu.UsagePercent);
        }

        if ((updated & MetricKind.Memory) != 0 && snapshot.Memory is { } memory)
        {
            History.Record(SeriesKeys.Memory, timestamp, memory.UsedPercent);
        }

        if ((updated & MetricKind.Gpu) != 0 && snapshot.Gpus is { } gpus)
        {
            foreach (var gpu in gpus)
            {
                if (gpu.UsagePercent is { } usage)
                {
                    History.Record(SeriesKeys.Gpu(gpu.AdapterId), timestamp, usage);
                }
            }
        }

        if ((updated & MetricKind.DiskActivity) != 0 && snapshot.DiskActivity is { } disks)
        {
            foreach (var disk in disks)
            {
                if (disk.ActiveTimePercent is { } active)
                {
                    History.Record(SeriesKeys.DiskActive(disk.Drive), timestamp, active);
                }
            }
        }

        if ((updated & MetricKind.Network) != 0 && snapshot.Network is { } network)
        {
            History.Record(SeriesKeys.NetworkReceive, timestamp, network.ReceiveBitsPerSecond);
            History.Record(SeriesKeys.NetworkSend, timestamp, network.SendBitsPerSecond);
        }
    }

    private void Publish(SystemMetricsUpdatedEventArgs args)
    {
        if (MetricsUpdated is not { } handlers)
        {
            return;
        }

        // One faulty subscriber must not prevent the others from being notified or break the loop.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<SystemMetricsUpdatedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A metrics subscriber failed.");
            }
        }
    }

    private void UpdateSelfUsage()
    {
        bool factorChanged;
        double factor;
        lock (_lock)
        {
            factorChanged = _governor.Update(Elapsed, _settings.Current.Monitoring.MaxSelfCpuPercent);
            factor = _governor.Factor;
            if (factorChanged)
            {
                ConfigureSchedule(_settings.Current.Monitoring);
            }

            if (_governor.MeasurementCount == _lastMeasurement)
            {
                return;
            }

            _lastMeasurement = _governor.MeasurementCount;
        }

        Volatile.Write(ref _selfUsage, MeasureSelf(factor));
        if (factorChanged)
        {
            _logger.LogInformation(
                "Sysora used {Cpu:0.00}% CPU (budget {Budget}%); collection intervals are now x{Factor:0.##}.",
                _governor.LastCpuPercent,
                _settings.Current.Monitoring.MaxSelfCpuPercent,
                factor);
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Monitoring == e.Current.Monitoring)
        {
            return;
        }

        var cleared = MetricKind.None;
        lock (_lock)
        {
            var before = _schedule.Enabled;
            ConfigureSchedule(e.Current.Monitoring);
            cleared = before & ~_schedule.Enabled;
            if (cleared != MetricKind.None)
            {
                var snapshot = Current;
                foreach (var source in _sources.Where(s => (cleared & s.Kind) != 0))
                {
                    snapshot = source.Clear(snapshot);
                }

                Volatile.Write(ref _current, snapshot with { Unavailable = snapshot.Unavailable & ~cleared });
            }
        }

        if (cleared != MetricKind.None)
        {
            Publish(new SystemMetricsUpdatedEventArgs(Current, cleared));
        }

        Wake();
    }

    /// <summary>
    /// Applies settings, intensity, background mode, an ongoing investigation and the self-usage factor to the schedule.
    /// Caller holds the lock.
    /// </summary>
    private void ConfigureSchedule(MonitoringSettings settings)
    {
        var now = Elapsed;
        var profile = MonitoringProfile.For(_investigating ? MonitoringIntensity.Detailed : settings.Intensity);

        // An investigation needs its context even while the window is hidden: no background slowdown then.
        var slowDown = _activity == MonitoringActivity.Background && settings.ReduceActivityWhenHidden && !_investigating;
        var intervals = new Dictionary<MetricKind, TimeSpan>();

        TimeSpan? Configure(MetricKind kind, TimeSpan? interval, double backgroundFactor)
        {
            TimeSpan? effective = interval is { } value
                ? profile.Apply(kind, value) * _governor.Factor * (slowDown ? backgroundFactor : 1.0)
                : null;
            _schedule.Configure(kind, effective, now);
            if (effective is { } e)
            {
                intervals[kind] = e;
            }

            return effective;
        }

        Configure(MetricKind.Cpu, TimeSpan.FromMilliseconds(settings.CpuIntervalMs), 1);
        Configure(MetricKind.Memory, TimeSpan.FromMilliseconds(settings.MemoryIntervalMs), 1);
        Configure(MetricKind.Processes, TimeSpan.FromMilliseconds(settings.ProcessIntervalMs), 5);
        Configure(MetricKind.DiskActivity, TimeSpan.FromMilliseconds(settings.DiskActivityIntervalMs), 5);
        Configure(MetricKind.Storage, TimeSpan.FromSeconds(settings.StorageIntervalSeconds), 2);
        Configure(MetricKind.System, SystemInterval, 2);
        Configure(MetricKind.Gpu, settings.GpuEnabled ? TimeSpan.FromMilliseconds(settings.GpuIntervalMs) : null, 5);
        Configure(MetricKind.Network, settings.NetworkEnabled ? TimeSpan.FromMilliseconds(settings.NetworkIntervalMs) : null, 5);
        Volatile.Write(ref _scheduleInfo, new MonitoringScheduleInfo(profile.Intensity, _investigating, slowDown, _governor.Factor, intervals));
    }

    /// <summary>
    /// Sysora's own usage over the last measurement period: CPU (from the governor), memory, .NET allocations and garbage
    /// collections, collection rounds, and its own entry in the latest process sample. Called once per period, so it costs
    /// a few counter reads every ten seconds.
    /// </summary>
    private SelfUsage MeasureSelf(double factor)
    {
        var elapsed = Elapsed;
        var counters = new SelfCounters(elapsed, GC.GetTotalAllocatedBytes(precise: false), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), Interlocked.Read(ref _rounds));
        var previous = _lastSelf;
        _lastSelf = counters;
        var seconds = previous is { } p ? (elapsed - p.Elapsed).TotalSeconds : 0;

        var budget = _settings.Current.Monitoring.MaxSelfCpuPercent;
        var cpu = _governor.LastCpuPercent;
        _overBudget = budget > 0 && cpu > budget ? _overBudget + 1 : 0;
        var sustained = _overBudget >= SustainedOverBudgetMeasurements;
        if (sustained && !_overBudgetLogged)
        {
            _overBudgetLogged = true;
            _logger.LogWarning("Sysora has used more than its CPU budget ({Budget}%) for a minute ({Cpu:0.00}% now); collection intervals are stretched.", budget, cpu);
        }
        else if (!sustained)
        {
            _overBudgetLogged = false;
        }

        var self = Current.Processes?.Processes.FirstOrDefault(process => process.ProcessId == Environment.ProcessId);
        return new SelfUsage(cpu, Environment.WorkingSet, factor)
        {
            MeasuredAt = _time.GetUtcNow(),
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            AllocatedBytesPerSecond = previous is { } a && seconds > 0 ? (counters.Allocated - a.Allocated) / seconds : null,
            Gen0Collections = previous is { } g0 ? counters.Gen0 - g0.Gen0 : 0,
            Gen1Collections = previous is { } g1 ? counters.Gen1 - g1.Gen1 : 0,
            Gen2Collections = previous is { } g2 ? counters.Gen2 - g2.Gen2 : 0,
            CollectionRoundsPerMinute = previous is { } r && seconds > 0 ? (counters.Rounds - r.Rounds) * 60 / seconds : null,
            ProcessesAnalyzed = Current.Processes?.ProcessCount,
            WriteBytesPerSecond = self?.IoWriteBytesPerSecond,
            ThreadCount = self?.ThreadCount is > 0 and var threads ? threads : null,
            OverBudgetSustained = sustained,
        };
    }

    private async Task SleepAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        using var sleep = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lock)
        {
            _sleepCts = sleep;
        }

        try
        {
            await Task.Delay(duration, _time, sleep.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Woken early by Wake().
        }
        finally
        {
            lock (_lock)
            {
                _sleepCts = null;
            }
        }
    }

    /// <summary>Interrupts the current sleep so the loop re-evaluates the schedule.</summary>
    private void Wake()
    {
        CancellationTokenSource? sleep;
        lock (_lock)
        {
            sleep = _sleepCts;
        }

        if (sleep is null)
        {
            return;
        }

        // Cancel on the thread pool: cancelling inline would run the loop's continuation on the
        // caller's thread, which is usually the UI thread.
        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                try
                {
                    state.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The sleep already ended.
                }
            },
            sleep,
            preferLocal: false);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Counters read at a self-usage measurement, to compute rates over the next period.</summary>
    private readonly record struct SelfCounters(TimeSpan Elapsed, long Allocated, int Gen0, int Gen1, int Gen2, long Rounds);

    /// <summary>Outcome of one provider call: a value to apply, a skipped sample, or a failure (neither).</summary>
    private readonly record struct CollectResult(Func<SystemSnapshot, SystemSnapshot>? Apply, bool Skipped);
}
