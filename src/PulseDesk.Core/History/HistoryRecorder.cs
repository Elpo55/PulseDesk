using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Settings;

namespace PulseDesk.Core.History;

/// <summary>
/// Feeds the long-term local history: aggregates history snapshots into one-minute buckets, collects
/// application usage buckets and events, and writes them from a single background task. Also runs the
/// hourly maintenance (roll-up into hourly summaries, retention, size limit).
/// </summary>
/// <remarks>
/// Nothing here runs on the UI thread, and the monitoring loop only appends to an in-memory queue: database
/// writes never slow collection down. The queue is bounded; if the storage stops responding, the oldest
/// pending writes are dropped rather than growing memory.
/// </remarks>
public sealed class HistoryRecorder : IAsyncDisposable
{
    /// <summary>Interval between two maintenance runs.</summary>
    public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(1);

    private static readonly TimeSpan FirstMaintenanceDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly IPerformanceHistory _history;
    private readonly IHistoryRepository _repository;
    private readonly SettingsService _settings;
    private readonly ILogger<HistoryRecorder> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly SystemUsageAggregator _aggregator = new(HistoryResolution.Minute);
    private readonly Channel<WorkItem> _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(512)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    private CancellationTokenSource? _cts;
    private Task _writer = Task.CompletedTask;
    private ITimer? _maintenanceTimer;
    private bool _started;
    private bool _available;
    private int _failures;

    public HistoryRecorder(
        IPerformanceHistory history,
        IHistoryRepository repository,
        SettingsService settings,
        ILogger<HistoryRecorder> logger,
        TimeProvider? timeProvider = null)
    {
        _history = history;
        _repository = repository;
        _settings = settings;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True once the storage was opened successfully.</summary>
    public bool IsAvailable => Volatile.Read(ref _available);

    private bool IsRecording => _settings.Current.History.RecordHistory;

    /// <summary>Opens the storage and starts recording. Never throws for storage errors: history is then simply not kept.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        try
        {
            await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _available, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The history database could not be opened; long-term history is disabled for this session.");
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _writer = Task.Run(() => WriteLoopAsync(token), CancellationToken.None);
        _history.SnapshotRecorded += OnSnapshotRecorded;
        _history.EventRecorded += OnEventRecorded;
        _settings.Changed += OnSettingsChanged;
        _maintenanceTimer = _time.CreateTimer(_ => Enqueue(new MaintenanceItem()), null, FirstMaintenanceDelay, MaintenanceInterval);
    }

    /// <summary>Queues a completed application usage bucket for writing.</summary>
    public void Record(AppUsageBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        if (IsRecording && bucket.Apps.Count > 0)
        {
            Enqueue(new WriteItem(new HistoryBatch([], []) { AppUsage = [bucket] }));
        }
    }

    /// <summary>Writes everything queued so far, including the minute in progress (tests, shutdown).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        SystemUsageAggregate? partial;
        lock (_lock)
        {
            partial = _aggregator.Flush();
        }

        if (partial is not null && IsRecording)
        {
            Enqueue(new WriteItem(new HistoryBatch([partial], [])));
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new FlushItem(done)))
        {
            return;
        }

        await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the maintenance now (used after the retention settings change).</summary>
    public void RequestMaintenance() => Enqueue(new MaintenanceItem());

    /// <summary>Size and coverage of the stored history.</summary>
    public Task<HistoryStorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken) =>
        _repository.GetStorageInfoAsync(cancellationToken);

    /// <summary>Deletes all stored history (the user's request). In-memory replay data is kept.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _repository.ClearAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Local history deleted at the user's request.");
    }

    /// <summary>Stops recording after writing the minute in progress.</summary>
    public async Task StopAsync()
    {
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
        }

        _history.SnapshotRecorded -= OnSnapshotRecorded;
        _history.EventRecorded -= OnEventRecorded;
        _settings.Changed -= OnSettingsChanged;
        _maintenanceTimer?.Dispose();
        _maintenanceTimer = null;

        try
        {
            await FlushAsync().WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Pending history writes did not complete in time and were dropped.");
        }

        _queue.Writer.TryComplete();
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void OnSnapshotRecorded(object? sender, MetricSnapshot snapshot)
    {
        if (!IsRecording)
        {
            return;
        }

        SystemUsageAggregate? completed;
        lock (_lock)
        {
            completed = _aggregator.Add(snapshot);
        }

        if (completed is not null)
        {
            Enqueue(new WriteItem(new HistoryBatch([completed], [])));
        }
    }

    private void OnEventRecorded(object? sender, SystemEvent systemEvent)
    {
        if (IsRecording)
        {
            Enqueue(new WriteItem(new HistoryBatch([], [systemEvent])));
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        var before = e.Previous.History;
        var after = e.Current.History;
        if (before.DetailRetentionDays != after.DetailRetentionDays || before.SummaryRetentionDays != after.SummaryRetentionDays)
        {
            RequestMaintenance();
        }
    }

    private void Enqueue(WorkItem item) => _queue.Writer.TryWrite(item);

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Merge everything already queued into one transaction.
            var system = new List<SystemUsageAggregate>();
            var events = new List<SystemEvent>();
            var apps = new List<AppUsageBucket>();
            var maintenance = false;
            var flushes = new List<TaskCompletionSource>();
            while (reader.TryRead(out var item))
            {
                switch (item)
                {
                    case WriteItem write:
                        system.AddRange(write.Batch.SystemUsage);
                        events.AddRange(write.Batch.Events);
                        apps.AddRange(write.Batch.AppUsage);
                        break;
                    case MaintenanceItem:
                        maintenance = true;
                        break;
                    case FlushItem flush:
                        flushes.Add(flush.Done);
                        break;
                }
            }

            var batch = new HistoryBatch(system, events) { AppUsage = apps };
            if (IsAvailable && !batch.IsEmpty)
            {
                await RunSafeAsync(() => _repository.AppendAsync(batch, cancellationToken), "write").ConfigureAwait(false);
            }

            if (IsAvailable && maintenance)
            {
                var retention = HistoryRetention.From(_settings.Current.History);
                await RunSafeAsync(() => _repository.RunMaintenanceAsync(retention, _time.GetUtcNow(), cancellationToken), "maintenance").ConfigureAwait(false);
            }

            foreach (var flush in flushes)
            {
                flush.TrySetResult();
            }
        }
    }

    private async Task RunSafeAsync(Func<Task> operation, string what)
    {
        try
        {
            await operation().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _failures, 0) > 0)
            {
                _logger.LogInformation("History {Operation} succeeded again.", what);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Log the first failure in full, then stay quiet: a full disk must not flood the log.
            if (Interlocked.Increment(ref _failures) == 1)
            {
                _logger.LogWarning(ex, "History {Operation} failed.", what);
            }
            else
            {
                _logger.LogDebug("History {Operation} failed again: {Message}", what, ex.Message);
            }
        }
    }

    private abstract record WorkItem;

    private sealed record WriteItem(HistoryBatch Batch) : WorkItem;

    private sealed record MaintenanceItem : WorkItem;

    private sealed record FlushItem(TaskCompletionSource Done) : WorkItem;
}
