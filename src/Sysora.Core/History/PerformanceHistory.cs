using Sysora.Core.Analysis;
using Sysora.Core.Interfaces;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Settings;

namespace Sysora.Core.History;

/// <summary>
/// Default <see cref="IPerformanceHistory"/>: records one <see cref="MetricSnapshot"/> at every CPU sample
/// into a ring buffer sized for the configured replay duration, and the events Sysora observes into
/// a second ring buffer. Memory use is bounded and allocations per sample are small.
/// </summary>
public sealed class PerformanceHistory : IPerformanceHistory, IDisposable
{
    /// <summary>Applications kept per snapshot for each criterion (CPU, memory, I/O).</summary>
    public const int TopAppsPerCriterion = 5;

    /// <summary>Number of events kept in memory.</summary>
    public const int EventCapacity = 1000;

    /// <summary>Longest window examined by the diagnosis rules, in minutes.</summary>
    private const int AnalysisWindowMinutes = 20;

    /// <summary>A longer interval between two snapshots is reported as a gap in the data.</summary>
    public static readonly TimeSpan GapThreshold = SystemUsageAggregator.MaxSampleInterval;

    private readonly IMetricsMonitor? _monitor;
    private readonly SettingsService? _settings;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly AppGrouper _grouper = new();
    private readonly SystemEventDetector _detector = new();
    private readonly RingBuffer<SystemEvent> _events = new(EventCapacity);
    private RingBuffer<MetricSnapshot> _snapshots;
    private IReadOnlyList<AppSample> _topApps = [];
    private TimeSpan _duration;
    private bool _wasPaused;
    private bool _resumedSinceLastSnapshot;

    /// <summary>Creates a history fed by the monitor, sized from the settings.</summary>
    public PerformanceHistory(IMetricsMonitor monitor, SettingsService settings, TimeProvider? timeProvider = null)
        : this(RequiredDuration(settings.Current), timeProvider)
    {
        _monitor = monitor;
        _settings = settings;
        _monitor.MetricsUpdated += OnMetricsUpdated;
        _monitor.StateChanged += OnMonitorStateChanged;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Creates a history fed explicitly through <see cref="Record"/> (tests, tools).</summary>
    public PerformanceHistory(TimeSpan duration, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        _time = timeProvider ?? TimeProvider.System;
        _duration = duration;
        _snapshots = new RingBuffer<MetricSnapshot>(CapacityFor(duration));
    }

    public event EventHandler<MetricSnapshot>? SnapshotRecorded;

    public event EventHandler<SystemEvent>? EventRecorded;

    public TimeSpan Duration
    {
        get
        {
            lock (_lock)
            {
                return _duration;
            }
        }
    }

    public int Capacity
    {
        get
        {
            lock (_lock)
            {
                return _snapshots.Capacity;
            }
        }
    }

    public MetricSnapshot? Latest
    {
        get
        {
            lock (_lock)
            {
                return _snapshots.TryGetNewest(out var latest) ? latest : null;
            }
        }
    }

    /// <summary>
    /// Time the buffer must cover: the replay duration chosen by the user, and at least the longest window used
    /// by the alert rules and the diagnosis (plus a margin), so they can always see the whole period they analyze.
    /// </summary>
    public static TimeSpan RequiredDuration(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var alerts = settings.SmartAlerts;
        var longest = new[]
        {
            alerts.CpuMinutes, alerts.MemoryMinutes, alerts.DiskMinutes, alerts.AppCpuMinutes,
            alerts.MemoryGrowthMinutes, alerts.UnusualMinutes, AnalysisWindowMinutes,
        }.Max();
        return TimeSpan.FromMinutes(Math.Max(settings.History.ReplayMinutes, longest + 5));
    }

    /// <summary>Number of snapshots needed to cover <paramref name="duration"/> at the fastest sampling interval.</summary>
    public static int CapacityFor(TimeSpan duration) =>
        (int)Math.Ceiling(duration.TotalMilliseconds / SettingsValidator.MinIntervalMs);

    /// <summary>
    /// Processes a monitor update: refreshes the top applications when processes were sampled, detects events,
    /// and records a snapshot when the CPU was sampled (the CPU is the heartbeat of the history).
    /// </summary>
    public void Record(SystemSnapshot snapshot, MetricKind updated)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Timestamp == default)
        {
            return;
        }

        IReadOnlyList<AppGroup>? apps = null;
        if ((updated & MetricKind.Processes) != 0 && snapshot.Processes is { } processes)
        {
            apps = _grouper.Group(processes.Processes);
            _topApps = AppGrouper.SelectTop(apps, TopAppsPerCriterion).Select(a => a.ToSample()).ToArray();
        }
        else if ((updated & MetricKind.Processes) != 0)
        {
            _topApps = [];
        }

        var events = new List<SystemEvent>(_detector.Detect(snapshot, updated, apps));
        MetricSnapshot? recorded = null;
        if ((updated & MetricKind.Cpu) != 0)
        {
            recorded = MetricSnapshotFactory.Create(snapshot, _topApps);
            lock (_lock)
            {
                if (_snapshots.TryGetNewest(out var previous))
                {
                    if (recorded.Timestamp <= previous.Timestamp)
                    {
                        return;
                    }

                    var gap = recorded.Timestamp - previous.Timestamp;
                    if (gap > GapThreshold && !_resumedSinceLastSnapshot)
                    {
                        events.Insert(0, new SystemEvent(previous.Timestamp, SystemEventKind.DataGap,
                            $"No measurements for {Formatting.MetricFormatter.DurationCompact(gap)}",
                            "Sysora was not running or the PC was asleep during this period."));
                    }
                }
                else
                {
                    events.Insert(0, new SystemEvent(recorded.Timestamp, SystemEventKind.MonitoringStarted, "Monitoring started"));
                }

                _resumedSinceLastSnapshot = false;
                _snapshots.Add(recorded);
            }
        }

        foreach (var systemEvent in events)
        {
            AddEvent(systemEvent);
        }

        if (recorded is not null)
        {
            SnapshotRecorded?.Invoke(this, recorded);
        }
    }

    public IReadOnlyList<MetricSnapshot> GetSnapshots(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            var start = FirstIndex(_snapshots, s => s.Timestamp >= from);
            var end = FirstIndex(_snapshots, s => s.Timestamp > to);
            if (end <= start)
            {
                return [];
            }

            var result = new MetricSnapshot[end - start];
            for (var i = start; i < end; i++)
            {
                result[i - start] = _snapshots[i];
            }

            return result;
        }
    }

    public IReadOnlyList<MetricSnapshot> GetRecent(TimeSpan window)
    {
        var latest = Latest;
        return latest is null ? [] : GetSnapshots(latest.Timestamp - window, latest.Timestamp);
    }

    public MetricSnapshot? GetNearest(DateTimeOffset time)
    {
        lock (_lock)
        {
            if (_snapshots.Count == 0)
            {
                return null;
            }

            var index = FirstIndex(_snapshots, s => s.Timestamp >= time);
            if (index == 0)
            {
                return _snapshots[0];
            }

            if (index == _snapshots.Count)
            {
                return _snapshots[^1];
            }

            var before = _snapshots[index - 1];
            var after = _snapshots[index];
            return time - before.Timestamp <= after.Timestamp - time ? before : after;
        }
    }

    public IReadOnlyList<SystemEvent> GetEvents(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            return _events.Where(e => e.Timestamp >= from && e.Timestamp <= to).ToArray();
        }
    }

    public void AddEvent(SystemEvent systemEvent)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);
        lock (_lock)
        {
            _events.Add(systemEvent);
        }

        EventRecorded?.Invoke(this, systemEvent);
    }

    /// <summary>Changes how much time the snapshot buffer covers, keeping the most recent snapshots.</summary>
    public void Resize(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        lock (_lock)
        {
            if (duration == _duration)
            {
                return;
            }

            var resized = new RingBuffer<MetricSnapshot>(CapacityFor(duration));
            var keep = Math.Min(_snapshots.Count, resized.Capacity);
            for (var i = _snapshots.Count - keep; i < _snapshots.Count; i++)
            {
                resized.Add(_snapshots[i]);
            }

            _snapshots = resized;
            _duration = duration;
        }
    }

    public void Dispose()
    {
        if (_monitor is not null)
        {
            _monitor.MetricsUpdated -= OnMetricsUpdated;
            _monitor.StateChanged -= OnMonitorStateChanged;
        }

        if (_settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
        }
    }

    private void OnMetricsUpdated(object? sender, SystemMetricsUpdatedEventArgs e) => Record(e.Snapshot, e.Updated);

    private void OnMonitorStateChanged(object? sender, EventArgs e)
    {
        if (_monitor is null)
        {
            return;
        }

        var paused = _monitor.IsPaused;
        SystemEvent? systemEvent = null;
        lock (_lock)
        {
            if (paused == _wasPaused)
            {
                return;
            }

            _wasPaused = paused;
            if (!paused)
            {
                _resumedSinceLastSnapshot = true;
            }

            systemEvent = new SystemEvent(_time.GetUtcNow(), paused ? SystemEventKind.MonitoringPaused : SystemEventKind.MonitoringResumed,
                paused ? "Monitoring paused" : "Monitoring resumed");
        }

        AddEvent(systemEvent);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        var duration = RequiredDuration(e.Current);
        if (duration != RequiredDuration(e.Previous))
        {
            Resize(duration);
        }
    }

    /// <summary>Binary search: index of the first item matching a predicate that is monotonic over the (time-ordered) buffer.</summary>
    private static int FirstIndex<T>(RingBuffer<T> buffer, Func<T, bool> predicate)
    {
        int low = 0, high = buffer.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (!predicate(buffer[mid]))
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
