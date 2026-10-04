namespace PulseDesk.Core.Models;

/// <summary>Families of metrics collected by the monitor.</summary>
[Flags]
public enum MetricKind
{
    None = 0,
    Cpu = 1 << 0,
    Memory = 1 << 1,
    Gpu = 1 << 2,
    Storage = 1 << 3,
    DiskActivity = 1 << 4,
    Network = 1 << 5,
    Processes = 1 << 6,
    System = 1 << 7,
    All = Cpu | Memory | Gpu | Storage | DiskActivity | Network | Processes | System,
}

/// <summary>
/// Latest known value of every metric. Immutable: the monitor publishes a new instance on each update.
/// </summary>
/// <remarks>
/// A null metric means it has not been collected yet, or that it is unavailable on this machine
/// (check <see cref="Unavailable"/> to tell the two apart).
/// </remarks>
public sealed record SystemSnapshot
{
    /// <summary>Time of the most recent update.</summary>
    public DateTimeOffset Timestamp { get; init; }

    public CpuMetrics? Cpu { get; init; }

    public MemoryMetrics? Memory { get; init; }

    public IReadOnlyList<GpuMetrics>? Gpus { get; init; }

    public IReadOnlyList<StorageMetrics>? Storage { get; init; }

    public IReadOnlyList<DiskActivityMetrics>? DiskActivity { get; init; }

    public NetworkMetrics? Network { get; init; }

    public ProcessSnapshot? Processes { get; init; }

    public SystemMetrics? System { get; init; }

    /// <summary>Metrics whose last collection failed or that this machine does not support.</summary>
    public MetricKind Unavailable { get; init; }

    /// <summary>An empty snapshot (nothing collected yet).</summary>
    public static SystemSnapshot Empty { get; } = new();

    /// <summary>True when the given metric could not be collected.</summary>
    public bool IsUnavailable(MetricKind kind) => (Unavailable & kind) != 0;

    /// <summary>The volume Windows runs from, when known.</summary>
    public StorageMetrics? SystemDrive => Storage?.FirstOrDefault(s => s.IsSystemDrive);

    /// <summary>
    /// The adapter shown on the dashboard: the busiest one (on laptops the integrated GPU often does the
    /// work while the discrete GPU sleeps); ties go to the adapter with the most dedicated memory.
    /// </summary>
    public GpuMetrics? PrimaryGpu => Gpus?
        .OrderByDescending(g => Math.Round(g.UsagePercent ?? -1))
        .ThenByDescending(g => g.DedicatedMemoryTotalBytes ?? 0)
        .FirstOrDefault();
}

/// <summary>Arguments of <see cref="Interfaces.IMetricsMonitor.MetricsUpdated"/>.</summary>
/// <param name="Snapshot">The new snapshot.</param>
/// <param name="Updated">Metrics refreshed by this update.</param>
public sealed class SystemMetricsUpdatedEventArgs(SystemSnapshot snapshot, MetricKind updated) : EventArgs
{
    public SystemSnapshot Snapshot { get; } = snapshot;

    public MetricKind Updated { get; } = updated;
}
