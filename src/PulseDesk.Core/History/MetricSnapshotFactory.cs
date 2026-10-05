using PulseDesk.Core.Metrics;
using PulseDesk.Core.Models;

namespace PulseDesk.Core.History;

/// <summary>Builds compact history snapshots from the monitor's full snapshots.</summary>
public static class MetricSnapshotFactory
{
    /// <summary>
    /// Extracts the system-wide values of <paramref name="snapshot"/>. Values the monitor could not collect
    /// stay null.
    /// </summary>
    public static MetricSnapshot Create(SystemSnapshot snapshot, IReadOnlyList<AppSample> topApps)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(topApps);

        var memory = snapshot.Memory;
        var busiestDisk = snapshot.DiskActivity?
            .Where(d => d.ActiveTimePercent is not null)
            .MaxBy(d => d.ActiveTimePercent);

        return new MetricSnapshot
        {
            Timestamp = snapshot.Timestamp,
            CpuPercent = snapshot.Cpu?.UsagePercent,
            MemoryPercent = memory?.UsedPercent,
            MemoryUsedBytes = memory?.UsedBytes,
            MemoryTotalBytes = memory?.TotalBytes,
            CommitPercent = memory is { CommittedBytes: { } committed, CommitLimitBytes: > 0 and var limit }
                ? Percentages.Of(committed, limit)
                : null,
            DiskActivePercent = busiestDisk?.ActiveTimePercent,
            DiskActiveDrive = busiestDisk?.Drive,
            DiskReadBytesPerSecond = SumOrNull(snapshot.DiskActivity, d => d.ReadBytesPerSecond),
            DiskWriteBytesPerSecond = SumOrNull(snapshot.DiskActivity, d => d.WriteBytesPerSecond),
            // Interface rates are null on their first sample: report "unknown", not 0.
            NetworkReceiveBitsPerSecond = snapshot.Network is { } rx && rx.Interfaces.Any(i => i.ReceiveBitsPerSecond is not null)
                ? rx.ReceiveBitsPerSecond
                : null,
            NetworkSendBitsPerSecond = snapshot.Network is { } tx && tx.Interfaces.Any(i => i.SendBitsPerSecond is not null)
                ? tx.SendBitsPerSecond
                : null,
            GpuPercent = snapshot.Gpus?.Where(g => g.UsagePercent is not null).Select(g => g.UsagePercent).Max(),
            ProcessCount = snapshot.Processes?.ProcessCount,
            TopApps = topApps,
        };
    }

    private static double? SumOrNull<T>(IReadOnlyList<T>? items, Func<T, double?> value)
    {
        if (items is null)
        {
            return null;
        }

        double? sum = null;
        foreach (var item in items)
        {
            if (value(item) is { } v)
            {
                sum = (sum ?? 0) + v;
            }
        }

        return sum;
    }
}
