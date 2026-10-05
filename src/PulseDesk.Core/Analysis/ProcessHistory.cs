using PulseDesk.Core.History;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Metrics;
using PulseDesk.Core.Models;

namespace PulseDesk.Core.Analysis;

/// <summary>
/// Per-application usage since PulseDesk started: running time, average and peak CPU, memory and I/O,
/// launches, and a per-minute series of the last hour. Also cuts usage into five-minute buckets for the
/// long-term history.
/// </summary>
/// <remarks>
/// Applications come and go: an application only accumulates time and averages while it is running, and an
/// application that is not seen for a long time is eventually forgotten (bounded memory). Processes are
/// grouped by <see cref="AppIdentity"/>, so different programs sharing a name stay apart.
/// </remarks>
public sealed class ProcessHistory : IDisposable
{
    /// <summary>Applications followed at most; the least used ones not seen recently are forgotten first.</summary>
    public const int MaxTrackedApps = 2000;

    /// <summary>Minutes of per-minute history kept per application.</summary>
    public const int MinutesKept = 60;

    /// <summary>Applications written per five-minute bucket, for each ranking (CPU, memory).</summary>
    public const int AppsPerBucketPerCriterion = 25;

    /// <summary>Applications not seen for this long can be forgotten when the limit is reached.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(2);

    private readonly IMetricsMonitor? _monitor;
    private readonly Lock _lock = new();
    private readonly AppGrouper _grouper = new();
    private readonly Dictionary<string, AppState> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Sample> _current = new(StringComparer.Ordinal);
    private HashSet<ProcessIdentity> _knownProcesses = [];
    private HashSet<ProcessIdentity> _seenProcesses = [];
    private DateTimeOffset? _sessionStart;
    private DateTimeOffset? _lastSample;
    private double _monitoredSeconds;
    private DateTimeOffset? _bucketStart;
    private double _bucketSeconds;
    private int _bucketSamples;

    /// <summary>Creates a history fed by the monitor's process samples.</summary>
    public ProcessHistory(IMetricsMonitor monitor)
    {
        _monitor = monitor;
        _monitor.MetricsUpdated += OnMetricsUpdated;
    }

    /// <summary>Creates a history fed explicitly through <see cref="Record"/> (tests).</summary>
    public ProcessHistory()
    {
    }

    /// <summary>Raised (on the recording thread) when a five-minute bucket is complete.</summary>
    public event EventHandler<AppUsageBucket>? BucketCompleted;

    /// <summary>When the first process sample was recorded.</summary>
    public DateTimeOffset? SessionStart
    {
        get
        {
            lock (_lock)
            {
                return _sessionStart;
            }
        }
    }

    /// <summary>Time covered by process samples since the session started, gaps excluded.</summary>
    public double MonitoredSeconds
    {
        get
        {
            lock (_lock)
            {
                return _monitoredSeconds;
            }
        }
    }

    /// <summary>Records one process sample.</summary>
    /// <param name="processes">All running processes.</param>
    /// <param name="time">When they were sampled.</param>
    public void Record(ProcessSnapshot processes, DateTimeOffset time)
    {
        ArgumentNullException.ThrowIfNull(processes);
        AppUsageBucket? completed = null;
        lock (_lock)
        {
            if (_lastSample is { } last && time <= last)
            {
                return;
            }

            var interval = _lastSample is { } previous && time - previous <= SystemUsageAggregator.MaxSampleInterval
                ? (time - previous).TotalSeconds
                : 0;
            var first = _sessionStart is null;
            _sessionStart ??= time;

            var bucket = SystemUsageAggregate.BucketStart(time, HistoryResolution.FiveMinutes);
            if (_bucketStart is { } currentBucket && bucket != currentBucket)
            {
                completed = CompleteBucket();
            }

            _bucketStart = bucket;
            _monitoredSeconds += interval;
            _bucketSeconds += interval;
            _bucketSamples++;

            GroupProcesses(processes, countLaunches: !first);
            foreach (var state in _apps.Values)
            {
                state.Instances = 0;
            }

            foreach (var (key, sample) in _current)
            {
                if (!_apps.TryGetValue(key, out var state))
                {
                    state = new AppState(sample.Identity!, time);
                    _apps[key] = state;
                }

                state.Add(time, interval, sample);
            }

            _current.Clear();
            _lastSample = time;
            ForgetStaleApps(time);
        }

        if (completed is not null)
        {
            BucketCompleted?.Invoke(this, completed);
        }
    }

    /// <summary>Statistics of every application seen since PulseDesk started.</summary>
    public IReadOnlyList<AppUsageStatistics> GetSessionStatistics()
    {
        lock (_lock)
        {
            return _apps.Values.Select(ToStatistics).ToArray();
        }
    }

    /// <summary>Statistics of one application, or null when it was never seen.</summary>
    public AppUsageStatistics? GetStatistics(string appKey)
    {
        lock (_lock)
        {
            return _apps.TryGetValue(appKey, out var state) ? ToStatistics(state) : null;
        }
    }

    /// <summary>Per-minute usage of an application over the last hour, oldest first (the current minute included).</summary>
    public IReadOnlyList<AppMinutePoint> GetMinutes(string appKey)
    {
        lock (_lock)
        {
            return _apps.TryGetValue(appKey, out var state) ? state.GetMinutes() : [];
        }
    }

    /// <summary>Completes the bucket in progress (called on exit, so the last minutes are not lost).</summary>
    public AppUsageBucket? FlushBucket()
    {
        lock (_lock)
        {
            return _bucketStart is null ? null : CompleteBucket();
        }
    }

    public void Dispose()
    {
        if (_monitor is not null)
        {
            _monitor.MetricsUpdated -= OnMetricsUpdated;
        }
    }

    private void OnMetricsUpdated(object? sender, SystemMetricsUpdatedEventArgs e)
    {
        if ((e.Updated & MetricKind.Processes) != 0 && e.Snapshot.Processes is { } processes && e.Snapshot.Timestamp != default)
        {
            Record(processes, e.Snapshot.Timestamp);
        }
    }

    /// <summary>Sums the processes of each application into <see cref="_current"/> and counts new process instances.</summary>
    private void GroupProcesses(ProcessSnapshot processes, bool countLaunches)
    {
        _seenProcesses.Clear();
        foreach (var process in processes.Processes)
        {
            var identity = _grouper.Identify(process);
            ref var sample = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_current, identity.Key, out _);
            sample.Identity ??= identity;
            sample.Instances++;
            sample.Cpu += process.CpuPercent ?? 0;
            sample.Memory += process.PrivateWorkingSetBytes;
            sample.Io += process.IoBytesPerSecond ?? 0;
            _seenProcesses.Add(process.Identity);

            // Processes already running when PulseDesk started are not launches PulseDesk observed.
            if (countLaunches && !_knownProcesses.Contains(process.Identity))
            {
                sample.Launches++;
            }
        }

        (_knownProcesses, _seenProcesses) = (_seenProcesses, _knownProcesses);
    }

    private AppUsageBucket CompleteBucket()
    {
        var start = _bucketStart!.Value;
        var aggregates = new List<AppUsageAggregate>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        void AddTop(Func<AppState, double> rank)
        {
            foreach (var state in _apps.Values.Where(s => s.Bucket.Samples > 0 && rank(s) > 0)
                .OrderByDescending(rank).ThenBy(s => s.Identity.Key, StringComparer.Ordinal).Take(AppsPerBucketPerCriterion))
            {
                if (keys.Add(state.Identity.Key))
                {
                    aggregates.Add(state.Bucket.ToAggregate(state.Identity, start, HistoryResolution.FiveMinutes));
                }
            }
        }

        // Rank by resource-time (usage × running time) so short spikes and long moderate use are comparable.
        AddTop(s => s.Bucket.CpuTime);
        AddTop(s => s.Bucket.MemoryTime);
        AddTop(s => s.Bucket.IoTime);

        var bucket = new AppUsageBucket(start, HistoryResolution.FiveMinutes, _bucketSeconds, _bucketSamples, aggregates);
        foreach (var state in _apps.Values)
        {
            state.Bucket = default;
        }

        _bucketStart = null;
        _bucketSeconds = 0;
        _bucketSamples = 0;
        return bucket;
    }

    private void ForgetStaleApps(DateTimeOffset now)
    {
        if (_apps.Count <= MaxTrackedApps)
        {
            return;
        }

        var stale = _apps.Values
            .Where(s => now - s.LastSeen > ForgetAfter)
            .OrderBy(s => s.Session.CpuTime + s.Session.MemoryTime)
            .Take(_apps.Count - MaxTrackedApps + (MaxTrackedApps / 10))
            .Select(s => s.Identity.Key)
            .ToArray();
        foreach (var key in stale)
        {
            _apps.Remove(key);
        }
    }

    private AppUsageStatistics ToStatistics(AppState state)
    {
        var session = state.Session;
        var minutes = state.GetMinutes();
        UsageLevel? firstHalf = null, secondHalf = null;
        if (minutes.Count >= 4)
        {
            var middle = minutes[0].Start + ((minutes[^1].Start - minutes[0].Start) / 2);
            firstHalf = Level(minutes.Where(m => m.Start < middle));
            secondHalf = Level(minutes.Where(m => m.Start >= middle));
        }

        return new AppUsageStatistics
        {
            Identity = state.Identity,
            FirstSeen = state.FirstSeen,
            LastSeen = state.LastSeen,
            ActiveSeconds = session.Seconds,
            MonitoredSeconds = _monitoredSeconds,
            Samples = session.Samples,
            CpuAverage = session.CpuAverage,
            CpuMaximum = session.CpuMaximum,
            MemoryAverageBytes = session.MemoryAverage,
            MemoryMaximumBytes = session.MemoryMaximum,
            IoAverageBytesPerSecond = session.IoAverage,
            IoMaximumBytesPerSecond = session.IoMaximum,
            Launches = session.Launches,
            IsRunning = state.LastSeen == _lastSample,
            InstanceCount = state.Instances,
            FirstHalf = firstHalf,
            SecondHalf = secondHalf,
        };
    }

    private static UsageLevel? Level(IEnumerable<AppMinutePoint> points)
    {
        double seconds = 0, cpu = 0, memory = 0;
        foreach (var point in points)
        {
            var weight = Math.Max(point.ActiveSeconds, 1e-6);
            seconds += weight;
            cpu += point.CpuAverage * weight;
            memory += point.MemoryAverageBytes * weight;
        }

        return seconds <= 0 ? null : new UsageLevel(cpu / seconds, memory / seconds, seconds);
    }

    /// <summary>Usage of one application in one process sample.</summary>
    private struct Sample
    {
        public AppIdentity? Identity;
        public int Instances;
        public double Cpu;
        public ulong Memory;
        public double Io;
        public int Launches;
    }

    /// <summary>Running totals of an application's usage. Averages are weighted by time when intervals are known.</summary>
    private struct Usage
    {
        public int Samples;
        public double Seconds;
        public double CpuTime;
        public double MemoryTime;
        public double IoTime;
        public double CpuSum;
        public double MemorySum;
        public double IoSum;
        public double CpuMaximum;
        public double MemoryMaximum;
        public double IoMaximum;
        public int Launches;

        public readonly double CpuAverage => Average(CpuTime, CpuSum);

        public readonly double MemoryAverage => Average(MemoryTime, MemorySum);

        public readonly double IoAverage => Average(IoTime, IoSum);

        public void Add(double interval, double cpu, double memory, double io, int launches)
        {
            Samples++;
            Seconds += interval;
            CpuTime += cpu * interval;
            MemoryTime += memory * interval;
            IoTime += io * interval;
            CpuSum += cpu;
            MemorySum += memory;
            IoSum += io;
            CpuMaximum = Math.Max(CpuMaximum, cpu);
            MemoryMaximum = Math.Max(MemoryMaximum, memory);
            IoMaximum = Math.Max(IoMaximum, io);
            Launches += launches;
        }

        public readonly AppUsageAggregate ToAggregate(AppIdentity identity, DateTimeOffset start, HistoryResolution resolution) => new()
        {
            AppKey = identity.Key,
            Name = identity.Name,
            ExecutablePath = identity.ExecutablePath,
            Start = start,
            Resolution = resolution,
            Samples = Samples,
            ActiveSeconds = Seconds,
            CpuAverage = CpuAverage,
            CpuMaximum = CpuMaximum,
            MemoryAverageBytes = MemoryAverage,
            MemoryMaximumBytes = MemoryMaximum,
            IoAverageBytesPerSecond = IoAverage,
            IoMaximumBytesPerSecond = IoMaximum,
            Launches = Launches,
        };

        // Time-weighted when the sampling intervals are known (the first sample after a gap has none).
        private readonly double Average(double weighted, double sum) =>
            Seconds > 0 ? weighted / Seconds : Samples > 0 ? sum / Samples : 0;
    }

    private sealed class AppState(AppIdentity identity, DateTimeOffset firstSeen)
    {
        private RingBuffer<AppMinutePoint>? _minutes;
        private DateTimeOffset? _minuteStart;
        private Usage _minute;

        public AppIdentity Identity { get; } = identity;

        public DateTimeOffset FirstSeen { get; } = firstSeen;

        public DateTimeOffset LastSeen { get; private set; } = firstSeen;

        public int Instances { get; set; }

        public Usage Session;

        public Usage Bucket;

        public void Add(DateTimeOffset time, double interval, Sample sample)
        {
            LastSeen = time;
            Instances = sample.Instances;
            Session.Add(interval, sample.Cpu, sample.Memory, sample.Io, sample.Launches);
            Bucket.Add(interval, sample.Cpu, sample.Memory, sample.Io, sample.Launches);

            var minute = SystemUsageAggregate.BucketStart(time, HistoryResolution.Minute);
            if (_minuteStart is { } current && current != minute)
            {
                (_minutes ??= new RingBuffer<AppMinutePoint>(MinutesKept)).Add(ToPoint(current, _minute));
                _minute = default;
            }

            _minuteStart = minute;
            _minute.Add(interval, sample.Cpu, sample.Memory, sample.Io, sample.Launches);
        }

        public IReadOnlyList<AppMinutePoint> GetMinutes()
        {
            var points = new List<AppMinutePoint>(MinutesKept + 1);
            if (_minutes is not null)
            {
                points.AddRange(_minutes);
            }

            if (_minuteStart is { } current && _minute.Samples > 0)
            {
                points.Add(ToPoint(current, _minute));
            }

            return points;
        }

        private static AppMinutePoint ToPoint(DateTimeOffset start, Usage usage) =>
            new(start, usage.CpuAverage, usage.MemoryAverage, usage.IoAverage, usage.Seconds);
    }
}
