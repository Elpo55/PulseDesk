namespace Sysora.Core.Models;

/// <summary>
/// Point-in-time measurements for one graphics adapter. Every metric is optional:
/// drivers and Windows versions differ in what they expose.
/// </summary>
/// <param name="AdapterId">Stable identifier of the adapter for this boot session (LUID).</param>
/// <param name="Name">Adapter name as reported by the driver.</param>
public sealed record GpuMetrics(string AdapterId, string Name)
{
    /// <summary>Utilization of the busiest engine, 0–100 (same method as Task Manager).</summary>
    public double? UsagePercent { get; init; }

    /// <summary>Dedicated video memory currently in use.</summary>
    public ulong? DedicatedMemoryUsedBytes { get; init; }

    /// <summary>Dedicated video memory available on the adapter.</summary>
    public ulong? DedicatedMemoryTotalBytes { get; init; }

    /// <summary>Shared system memory currently used by the adapter.</summary>
    public ulong? SharedMemoryUsedBytes { get; init; }

    /// <summary>Shared system memory the adapter may use.</summary>
    public ulong? SharedMemoryTotalBytes { get; init; }

    /// <summary>GPU temperature, when the driver exposes it.</summary>
    public double? TemperatureCelsius { get; init; }

    /// <summary>Core clock frequency, when the driver exposes it.</summary>
    public double? FrequencyMHz { get; init; }

    /// <summary>Utilization per engine type (3D, Copy, Video Decode...). Empty when unavailable.</summary>
    public IReadOnlyList<GpuEngineUsage> Engines { get; init; } = [];

    /// <summary>
    /// Utilization of this adapter by each process using it (its busiest engine, like Task Manager's GPU column),
    /// for processes above 0.1%. Empty when Windows does not report per-process engine usage.
    /// </summary>
    public IReadOnlyList<GpuProcessUsage> Processes { get; init; } = [];
}

/// <summary>GPU utilization of one process on one adapter.</summary>
/// <param name="ProcessId">Process ID (the counter does not carry the creation time).</param>
/// <param name="UsagePercent">Utilization of the process's busiest engine, 0–100.</param>
public readonly record struct GpuProcessUsage(int ProcessId, double UsagePercent);

/// <summary>Utilization of one GPU engine type.</summary>
/// <param name="EngineType">Engine type label as reported by Windows (e.g. "3D", "VideoDecode").</param>
/// <param name="UsagePercent">Utilization, 0–100.</param>
public sealed record GpuEngineUsage(string EngineType, double UsagePercent);
