using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Sysora.Core.Interfaces;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;
using Sysora.Core.Settings;
using Sysora.Core.Simulation;

namespace Sysora.Tests.Services;

public sealed class MetricsMonitorTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly SimulatedMachine _machine;
    private readonly SettingsService _settings;
    private readonly CountingCpuProvider _cpu;
    private MetricsMonitor _monitor = null!;

    public MetricsMonitorTests()
    {
        _machine = new SimulatedMachine(_time);
        _cpu = new CountingCpuProvider(_machine);
        _settings = new SettingsService(new NullStore(), NullLogger<SettingsService>.Instance, _time);
    }

    public ValueTask InitializeAsync()
    {
        _monitor = CreateMonitor(SimulatedProviders.Create(_machine) with { Cpu = _cpu });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _monitor.DisposeAsync();

    [Fact]
    public async Task Start_CollectsEveryMetricImmediately()
    {
        var updated = WaitForUpdate(s => s.Processes is not null && s.Cpu is not null);
        await _monitor.StartAsync(TestContext.Current.CancellationToken);
        var snapshot = await updated;

        Assert.True(_monitor.IsRunning);
        Assert.NotNull(snapshot.Memory);
        Assert.NotNull(snapshot.Storage);
        Assert.NotNull(snapshot.Gpus);
        Assert.NotNull(snapshot.Network);
        Assert.NotNull(snapshot.System);
        Assert.Equal(MetricKind.None, snapshot.Unavailable);
        Assert.Single(_monitor.History.GetSamples(SeriesKeys.Cpu, DateTimeOffset.MinValue));
    }

    [Fact]
    public async Task Collection_FollowsConfiguredInterval()
    {
        await StartAndWaitForFirstRoundAsync();
        var before = _cpu.Calls;

        await AdvanceUntilAsync(() => _cpu.Calls >= before + 3);

        Assert.True(_time.GetUtcNow() - new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) >= TimeSpan.FromSeconds(3));
        Assert.True(_monitor.History.GetSamples(SeriesKeys.Cpu, DateTimeOffset.MinValue).Length >= 4);
    }

    [Fact]
    public async Task FailingProvider_IsReportedUnavailable_WithoutStoppingOthers()
    {
        await _monitor.DisposeAsync();
        var failingGpu = new FailingGpuProvider();
        _monitor = CreateMonitor(SimulatedProviders.Create(_machine) with { Gpu = failingGpu });

        var updated = WaitForUpdate(s => s.IsUnavailable(MetricKind.Gpu));
        await _monitor.StartAsync(TestContext.Current.CancellationToken);
        var snapshot = await updated;

        Assert.Null(snapshot.Gpus);
        Assert.NotNull(snapshot.Cpu);

        failingGpu.Fail = false;
        await AdvanceUntilAsync(() => _monitor.Current is { Gpus: not null } current && !current.IsUnavailable(MetricKind.Gpu));
        Assert.True(_monitor.IsRunning);
    }

    [Fact]
    public async Task SkippedSample_KeepsMetricAvailable_AndPublishesNoCpuUpdate()
    {
        await _monitor.DisposeAsync();
        var cpu = new SkippingCpuProvider(_machine);
        _monitor = CreateMonitor(SimulatedProviders.Create(_machine) with { Cpu = cpu });
        await StartAndWaitForFirstRoundAsync();
        var samples = _monitor.History.GetSamples(SeriesKeys.Cpu, DateTimeOffset.MinValue).Length;

        var cpuUpdates = 0;
        _monitor.MetricsUpdated += (_, e) =>
        {
            if ((e.Updated & MetricKind.Cpu) != 0)
            {
                Interlocked.Increment(ref cpuUpdates);
            }
        };
        cpu.SkipNext = 1;
        await AdvanceUntilAsync(() => cpu.Skipped == 1 && cpu.Calls >= 3);

        Assert.False(_monitor.Current.IsUnavailable(MetricKind.Cpu));
        Assert.NotNull(_monitor.Current.Cpu);
        // The skipped sample produced no chart point and no CPU update; the following ones did (the loop may still be
        // finishing a round, so wait for both counts to settle).
        await WaitUntilAsync(() => Volatile.Read(ref cpuUpdates) == cpu.Calls - 2
            && _monitor.History.GetSamples(SeriesKeys.Cpu, DateTimeOffset.MinValue).Length == samples + Volatile.Read(ref cpuUpdates));
    }

    [Fact]
    public async Task RepeatedSkips_AreReportedUnavailable()
    {
        await _monitor.DisposeAsync();
        var cpu = new SkippingCpuProvider(_machine);
        _monitor = CreateMonitor(SimulatedProviders.Create(_machine) with { Cpu = cpu });
        await StartAndWaitForFirstRoundAsync();

        cpu.SkipNext = MetricsMonitor.MaxConsecutiveSkips + 1;
        await AdvanceUntilAsync(() => _monitor.Current.IsUnavailable(MetricKind.Cpu));
        Assert.Null(_monitor.Current.Cpu);

        await AdvanceUntilAsync(() => !_monitor.Current.IsUnavailable(MetricKind.Cpu) && _monitor.Current.Cpu is not null);
    }

    [Fact]
    public async Task Pause_StopsCollection_ResumeRestartsIt()
    {
        await StartAndWaitForFirstRoundAsync();
        _monitor.Pause();
        Assert.True(_monitor.IsPaused);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var callsWhilePaused = _cpu.Calls;

        for (var i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(callsWhilePaused, _cpu.Calls);

        _monitor.Resume();
        await AdvanceUntilAsync(() => _cpu.Calls > callsWhilePaused);
    }

    [Fact]
    public async Task Stop_EndsLoop_AndNoMoreUpdatesArrive()
    {
        await StartAndWaitForFirstRoundAsync();
        await _monitor.StopAsync();
        var calls = _cpu.Calls;

        _time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(_monitor.IsRunning);
        Assert.Equal(calls, _cpu.Calls);
    }

    [Fact]
    public async Task RequestRefresh_CollectsBeforeNextScheduledTime()
    {
        await _monitor.DisposeAsync();
        var storage = new CountingStorageProvider(_machine);
        _monitor = CreateMonitor(SimulatedProviders.Create(_machine) with { Storage = storage });
        await StartAndWaitForFirstRoundAsync();
        Assert.Equal(1, storage.Calls);

        _monitor.RequestRefresh(MetricKind.Storage);

        await WaitUntilAsync(() => storage.Calls == 2);
    }

    [Fact]
    public async Task DisablingGpuInSettings_ClearsAndStopsGpuCollection()
    {
        await StartAndWaitForFirstRoundAsync();
        Assert.NotNull(_monitor.Current.Gpus);

        _settings.Update(s => s with { Monitoring = s.Monitoring with { GpuEnabled = false } });

        Assert.Null(_monitor.Current.Gpus);
        Assert.False(_monitor.Current.IsUnavailable(MetricKind.Gpu));
    }

    [Fact]
    public async Task FaultySubscriber_DoesNotBreakMonitoring()
    {
        _monitor.MetricsUpdated += (_, _) => throw new InvalidOperationException("subscriber bug");
        await StartAndWaitForFirstRoundAsync();
        var calls = _cpu.Calls;

        await AdvanceUntilAsync(() => _cpu.Calls > calls);

        Assert.True(_monitor.IsRunning);
    }

    private MetricsMonitor CreateMonitor(MetricProviders providers) =>
        new(providers, _settings, NullLogger<MetricsMonitor>.Instance, _time, () => TimeSpan.Zero);

    private async Task StartAndWaitForFirstRoundAsync()
    {
        var updated = WaitForUpdate(s => s.Cpu is not null);
        await _monitor.StartAsync(TestContext.Current.CancellationToken);
        await updated;
    }

    private Task<SystemSnapshot> WaitForUpdate(Func<SystemSnapshot, bool> predicate)
    {
        var tcs = new TaskCompletionSource<SystemSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _monitor.MetricsUpdated += (_, e) =>
        {
            if (predicate(e.Snapshot))
            {
                tcs.TrySetResult(e.Snapshot);
            }
        };

        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Advances fake time in small steps, letting the background loop run between steps.</summary>
    private async Task AdvanceUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Condition not met while advancing time.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Condition not met in time.");
    }

    private sealed class CountingCpuProvider(SimulatedMachine machine) : ICpuMetricProvider
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<CpuMetrics> CollectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(machine.SampleCpu());
        }
    }

    private sealed class CountingStorageProvider(SimulatedMachine machine) : IStorageMetricProvider
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<StorageMetrics>> CollectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(machine.SampleStorage());
        }
    }

    private sealed class SkippingCpuProvider(SimulatedMachine machine) : ICpuMetricProvider
    {
        private int _calls;
        private int _skipped;
        private int _skipNext;

        public int Calls => Volatile.Read(ref _calls);

        public int Skipped => Volatile.Read(ref _skipped);

        public int SkipNext
        {
            set => Volatile.Write(ref _skipNext, value);
        }

        public Task<CpuMetrics> CollectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Interlocked.Decrement(ref _skipNext) >= 0)
            {
                Interlocked.Increment(ref _skipped);
                return Task.FromException<CpuMetrics>(new MetricSampleSkippedException("Counter wrapped."));
            }

            Volatile.Write(ref _skipNext, 0);
            return Task.FromResult(machine.SampleCpu());
        }
    }

    private sealed class FailingGpuProvider : IGpuMetricProvider
    {
        public volatile bool Fail = true;

        public Task<IReadOnlyList<GpuMetrics>> CollectAsync(CancellationToken cancellationToken) =>
            Fail
                ? Task.FromException<IReadOnlyList<GpuMetrics>>(new InvalidOperationException("No GPU counters on this machine."))
                : Task.FromResult<IReadOnlyList<GpuMetrics>>([new GpuMetrics("gpu0", "Test GPU")]);
    }

    private sealed class NullStore : ISettingsStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task WriteAsync(string content, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task QuarantineAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
