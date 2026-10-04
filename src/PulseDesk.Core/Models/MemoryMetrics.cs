using PulseDesk.Core.Metrics;

namespace PulseDesk.Core.Models;

/// <summary>
/// Point-in-time physical memory measurements.
/// </summary>
/// <param name="TotalBytes">Physical memory usable by Windows.</param>
/// <param name="UsedBytes">Physical memory in use (total minus available).</param>
/// <param name="AvailableBytes">Physical memory immediately available to processes.</param>
public sealed record MemoryMetrics(
    ulong TotalBytes,
    ulong UsedBytes,
    ulong AvailableBytes)
{
    /// <summary>Share of physical memory in use, 0–100.</summary>
    public double UsedPercent => Percentages.Of(UsedBytes, TotalBytes);

    /// <summary>Committed virtual memory (commit charge), when available.</summary>
    public ulong? CommittedBytes { get; init; }

    /// <summary>Maximum commit charge (physical memory + page files), when available.</summary>
    public ulong? CommitLimitBytes { get; init; }

    /// <summary>System file cache size, when available.</summary>
    public ulong? CachedBytes { get; init; }

    /// <summary>Kernel paged pool size, when available.</summary>
    public ulong? PagedPoolBytes { get; init; }

    /// <summary>Kernel non-paged pool size, when available.</summary>
    public ulong? NonPagedPoolBytes { get; init; }

    /// <summary>Creates metrics from total and available memory, deriving the used amount.</summary>
    public static MemoryMetrics FromTotalAndAvailable(ulong totalBytes, ulong availableBytes)
    {
        var available = Math.Min(availableBytes, totalBytes);
        return new MemoryMetrics(totalBytes, totalBytes - available, available);
    }
}
