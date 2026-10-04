using PulseDesk.Core.Models;

namespace PulseDesk.Core.Monitoring;

/// <summary>
/// Resource usage of all processes sharing an image name (an application such as chrome.exe is
/// typically made of many processes).
/// </summary>
/// <param name="Name">Image name.</param>
/// <param name="InstanceCount">Number of processes with this name.</param>
/// <param name="CpuPercent">Combined CPU usage, percent of total capacity.</param>
/// <param name="PrivateWorkingSetBytes">Combined private working set.</param>
/// <param name="IoBytesPerSecond">Combined read and write I/O rate.</param>
public sealed record ProcessGroup(
    string Name,
    int InstanceCount,
    double CpuPercent,
    ulong PrivateWorkingSetBytes,
    double IoBytesPerSecond);

/// <summary>Groups processes by application and ranks the top consumers.</summary>
public static class ProcessAggregation
{
    /// <summary>Groups processes by image name (case-insensitive).</summary>
    public static IReadOnlyList<ProcessGroup> GroupByName(IEnumerable<ProcessMetrics> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);

        return processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessGroup(
                g.First().Name,
                g.Count(),
                g.Sum(p => p.CpuPercent ?? 0),
                (ulong)g.Sum(p => (decimal)p.PrivateWorkingSetBytes),
                g.Sum(p => p.IoBytesPerSecond ?? 0)))
            .ToArray();
    }

    /// <summary>Top <paramref name="count"/> groups by CPU usage.</summary>
    public static IReadOnlyList<ProcessGroup> TopByCpu(IEnumerable<ProcessGroup> groups, int count) =>
        Top(groups, g => g.CpuPercent, count);

    /// <summary>Top <paramref name="count"/> groups by memory usage.</summary>
    public static IReadOnlyList<ProcessGroup> TopByMemory(IEnumerable<ProcessGroup> groups, int count) =>
        Top(groups, g => g.PrivateWorkingSetBytes, count);

    /// <summary>Top <paramref name="count"/> groups by I/O rate.</summary>
    public static IReadOnlyList<ProcessGroup> TopByIo(IEnumerable<ProcessGroup> groups, int count) =>
        Top(groups, g => g.IoBytesPerSecond, count);

    private static IReadOnlyList<ProcessGroup> Top<TKey>(IEnumerable<ProcessGroup> groups, Func<ProcessGroup, TKey> key, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return groups
            .OrderByDescending(key)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToArray();
    }
}
