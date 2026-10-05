namespace PulseDesk.Core.History;

/// <summary>
/// System-wide measurements at one instant, as kept by the performance history (replay, diagnosis, alerts).
/// Deliberately compact: one is recorded at every CPU sample. A null value means the metric was not
/// available at that time; it is never replaced by 0.
/// </summary>
public sealed record MetricSnapshot
{
    /// <summary>When the values were collected (UTC).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Overall CPU utilization, 0–100.</summary>
    public double? CpuPercent { get; init; }

    /// <summary>Physical memory in use, 0–100.</summary>
    public double? MemoryPercent { get; init; }

    /// <summary>Physical memory in use.</summary>
    public ulong? MemoryUsedBytes { get; init; }

    /// <summary>Physical memory usable by Windows.</summary>
    public ulong? MemoryTotalBytes { get; init; }

    /// <summary>Commit charge as a share of the commit limit, 0–100.</summary>
    public double? CommitPercent { get; init; }

    /// <summary>Active time of the busiest volume, 0–100.</summary>
    public double? DiskActivePercent { get; init; }

    /// <summary>Letter of the busiest volume (e.g. "C:"), when known.</summary>
    public string? DiskActiveDrive { get; init; }

    /// <summary>Read throughput summed over all volumes.</summary>
    public double? DiskReadBytesPerSecond { get; init; }

    /// <summary>Write throughput summed over all volumes.</summary>
    public double? DiskWriteBytesPerSecond { get; init; }

    /// <summary>Network receive rate, bits per second.</summary>
    public double? NetworkReceiveBitsPerSecond { get; init; }

    /// <summary>Network send rate, bits per second.</summary>
    public double? NetworkSendBitsPerSecond { get; init; }

    /// <summary>Utilization of the busiest graphics adapter, 0–100. Null when no adapter reports it.</summary>
    public double? GpuPercent { get; init; }

    /// <summary>Number of running processes.</summary>
    public int? ProcessCount { get; init; }

    /// <summary>
    /// Applications using the most resources at that time (by CPU, memory and I/O). The same list instance is
    /// shared by consecutive snapshots until processes are sampled again.
    /// </summary>
    public IReadOnlyList<AppSample> TopApps { get; init; } = [];

    /// <summary>Returns the value of a metric, or null when it was not available.</summary>
    public double? Get(HistoryMetric metric) => metric switch
    {
        HistoryMetric.Cpu => CpuPercent,
        HistoryMetric.Memory => MemoryPercent,
        HistoryMetric.Disk => DiskActivePercent,
        HistoryMetric.NetworkReceive => NetworkReceiveBitsPerSecond,
        HistoryMetric.NetworkSend => NetworkSendBitsPerSecond,
        HistoryMetric.Gpu => GpuPercent,
        HistoryMetric.ProcessCount => ProcessCount,
        _ => null,
    };
}

/// <summary>Resource usage of one application (all its processes) at one instant.</summary>
/// <param name="Key">Identity of the application (see <see cref="Analysis.AppIdentity"/>).</param>
/// <param name="Name">Image name, e.g. "chrome.exe".</param>
/// <param name="InstanceCount">Number of processes.</param>
/// <param name="CpuPercent">Combined CPU usage, percent of total capacity.</param>
/// <param name="MemoryBytes">Combined private working set.</param>
/// <param name="IoBytesPerSecond">Combined read and write I/O rate.</param>
public sealed record AppSample(string Key, string Name, int InstanceCount, double CpuPercent, ulong MemoryBytes, double IoBytesPerSecond);

/// <summary>System-wide metrics kept in history.</summary>
public enum HistoryMetric
{
    Cpu,
    Memory,
    Disk,
    NetworkReceive,
    NetworkSend,
    Gpu,
    ProcessCount,
}
