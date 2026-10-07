namespace Sysora.Core.History;

/// <summary>Length of the time buckets of the long-term history.</summary>
public enum HistoryResolution
{
    /// <summary>One-minute buckets (system metrics, kept for the detailed retention period).</summary>
    Minute = 60,

    /// <summary>Five-minute buckets (application usage, kept for the detailed retention period).</summary>
    FiveMinutes = 300,

    /// <summary>One-hour buckets (everything, kept for the summary retention period).</summary>
    Hour = 3600,
}

/// <summary>Average and maximum of a metric over a bucket.</summary>
/// <param name="Average">Mean of the samples.</param>
/// <param name="Maximum">Largest sample.</param>
/// <param name="Count">Number of samples (used to weight averages when buckets are merged).</param>
public readonly record struct AggregateValue(double Average, double Maximum, int Count);

/// <summary>
/// System metrics aggregated over one time bucket of the long-term history. A metric that was never
/// available during the bucket is null.
/// </summary>
public sealed record SystemUsageAggregate
{
    /// <summary>Start of the bucket (UTC).</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>Bucket length.</summary>
    public required HistoryResolution Resolution { get; init; }

    /// <summary>Number of snapshots aggregated.</summary>
    public int SampleCount { get; init; }

    /// <summary>Time actually covered by measurements (gaps such as sleep are excluded).</summary>
    public double MonitoredSeconds { get; init; }

    public AggregateValue? Cpu { get; init; }

    public AggregateValue? Memory { get; init; }

    public AggregateValue? Disk { get; init; }

    public AggregateValue? NetworkReceive { get; init; }

    public AggregateValue? NetworkSend { get; init; }

    public AggregateValue? Gpu { get; init; }

    public AggregateValue? ProcessCount { get; init; }

    /// <summary>Free space on the Windows volume, bytes (absent from history recorded before it was kept).</summary>
    public AggregateValue? SystemDriveFree { get; init; }

    /// <summary>Physical memory size during the bucket (lets memory changes be detected).</summary>
    public ulong? MemoryTotalBytes { get; init; }

    /// <summary>Bucket length as a time span.</summary>
    public TimeSpan Length => TimeSpan.FromSeconds((int)Resolution);

    /// <summary>End of the bucket (exclusive).</summary>
    public DateTimeOffset End => Start + Length;

    /// <summary>Aggregate of one metric.</summary>
    public AggregateValue? Get(HistoryMetric metric) => metric switch
    {
        HistoryMetric.Cpu => Cpu,
        HistoryMetric.Memory => Memory,
        HistoryMetric.Disk => Disk,
        HistoryMetric.NetworkReceive => NetworkReceive,
        HistoryMetric.NetworkSend => NetworkSend,
        HistoryMetric.Gpu => Gpu,
        HistoryMetric.ProcessCount => ProcessCount,
        HistoryMetric.SystemDriveFree => SystemDriveFree,
        _ => null,
    };

    /// <summary>Start of the bucket containing <paramref name="time"/>.</summary>
    public static DateTimeOffset BucketStart(DateTimeOffset time, HistoryResolution resolution)
    {
        var length = TimeSpan.FromSeconds((int)resolution).Ticks;
        var ticks = time.UtcTicks;
        return new DateTimeOffset(ticks - (ticks % length), TimeSpan.Zero);
    }
}

/// <summary>Running sum, maximum and count of a nullable metric.</summary>
internal struct ValueAccumulator
{
    private double _sum;
    private double _max;
    private int _count;

    public void Add(double? value)
    {
        if (value is not { } v || !double.IsFinite(v))
        {
            return;
        }

        _max = _count == 0 ? v : Math.Max(_max, v);
        _sum += v;
        _count++;
    }

    public readonly AggregateValue? Result => _count == 0 ? null : new AggregateValue(_sum / _count, _max, _count);
}

/// <summary>
/// Turns the stream of history snapshots into fixed-length buckets (one minute by default) for long-term
/// storage. Gaps between snapshots longer than <see cref="MaxSampleInterval"/> are not counted as
/// monitored time.
/// </summary>
/// <remarks>Not thread-safe: fed by a single thread.</remarks>
public sealed class SystemUsageAggregator(HistoryResolution resolution = HistoryResolution.Minute)
{
    /// <summary>Longer intervals between two snapshots are treated as a gap (sleep, pause), not as monitored time.</summary>
    public static readonly TimeSpan MaxSampleInterval = TimeSpan.FromSeconds(60);

    private DateTimeOffset? _bucket;
    private DateTimeOffset? _previous;
    private int _samples;
    private double _seconds;
    private ulong? _memoryTotal;
    private ValueAccumulator _cpu;
    private ValueAccumulator _memory;
    private ValueAccumulator _disk;
    private ValueAccumulator _receive;
    private ValueAccumulator _send;
    private ValueAccumulator _gpu;
    private ValueAccumulator _processes;
    private ValueAccumulator _systemFree;

    public HistoryResolution Resolution { get; } = resolution;

    /// <summary>Adds a snapshot. Returns the previous bucket when this snapshot starts a new one.</summary>
    public SystemUsageAggregate? Add(MetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_previous is { } previous && snapshot.Timestamp <= previous)
        {
            return null;
        }

        var bucket = SystemUsageAggregate.BucketStart(snapshot.Timestamp, Resolution);
        SystemUsageAggregate? completed = null;
        if (_bucket is { } current && bucket != current)
        {
            completed = Flush();
        }

        _bucket = bucket;
        if (_previous is { } last && snapshot.Timestamp - last <= MaxSampleInterval)
        {
            _seconds += (snapshot.Timestamp - last).TotalSeconds;
        }

        _previous = snapshot.Timestamp;
        _samples++;
        _cpu.Add(snapshot.CpuPercent);
        _memory.Add(snapshot.MemoryPercent);
        _disk.Add(snapshot.DiskActivePercent);
        _receive.Add(snapshot.NetworkReceiveBitsPerSecond);
        _send.Add(snapshot.NetworkSendBitsPerSecond);
        _gpu.Add(snapshot.GpuPercent);
        _processes.Add(snapshot.ProcessCount);
        _systemFree.Add(snapshot.SystemDriveFreeBytes);
        _memoryTotal = snapshot.MemoryTotalBytes ?? _memoryTotal;
        return completed;
    }

    /// <summary>Returns the bucket in progress (possibly partial) and starts over. Null when it is empty.</summary>
    public SystemUsageAggregate? Flush()
    {
        if (_bucket is not { } start || _samples == 0)
        {
            return null;
        }

        var aggregate = new SystemUsageAggregate
        {
            Start = start,
            Resolution = Resolution,
            SampleCount = _samples,
            MonitoredSeconds = _seconds,
            Cpu = _cpu.Result,
            Memory = _memory.Result,
            Disk = _disk.Result,
            NetworkReceive = _receive.Result,
            NetworkSend = _send.Result,
            Gpu = _gpu.Result,
            ProcessCount = _processes.Result,
            SystemDriveFree = _systemFree.Result,
            MemoryTotalBytes = _memoryTotal,
        };

        _samples = 0;
        _seconds = 0;
        _cpu = _memory = _disk = _receive = _send = _gpu = _processes = _systemFree = default;
        _bucket = null;
        return aggregate;
    }
}
