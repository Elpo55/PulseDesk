using Sysora.Core.History;
using Sysora.Core.Interfaces;

namespace Sysora.Core.Analysis;

/// <summary>Data of a replayed period.</summary>
/// <param name="From">Start of the period.</param>
/// <param name="To">End of the period.</param>
/// <param name="IsDetailed">True for per-second data from memory, false for per-minute averages from the local history.</param>
/// <param name="Points">Measurements, oldest first.</param>
/// <param name="Events">Events in the period, oldest first.</param>
/// <param name="Story">What happened.</param>
/// <param name="Source">Where the data comes from, in words.</param>
public sealed record ReplayData(
    DateTimeOffset From,
    DateTimeOffset To,
    bool IsDetailed,
    IReadOnlyList<MetricSnapshot> Points,
    IReadOnlyList<SystemEvent> Events,
    ReplayStory Story,
    string Source);

/// <summary>
/// Serves Performance Replay: the detailed in-memory history for recent periods, per-minute averages from the
/// local history for longer ones, the events of the period, and an explanation of what happened.
/// </summary>
public sealed class ReplayService(IPerformanceHistory history, IHistoryRepository repository, TimeProvider? timeProvider = null)
{
    private const int MaxEvents = 500;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Longest period served from the detailed in-memory history.</summary>
    public TimeSpan DetailedDuration => history.Duration;

    /// <summary>Loads the period of length <paramref name="length"/> ending at <paramref name="end"/> (now when null).</summary>
    public async Task<ReplayData> LoadAsync(TimeSpan length, DateTimeOffset? end, CancellationToken cancellationToken)
    {
        var detailed = length <= history.Duration;
        var to = end ?? (detailed ? history.Latest?.Timestamp : null) ?? _time.GetUtcNow();
        var from = to - length;

        if (detailed)
        {
            var points = history.GetSnapshots(from, to);
            var events = history.GetEvents(from, to);
            var story = await Task.Run(() => ReplayNarrator.Narrate(points, events), cancellationToken).ConfigureAwait(false);
            return new ReplayData(from, to, true, points, events, story, "Detailed measurements kept in memory (one per CPU sample).");
        }

        var minutes = await repository.GetSystemUsageAsync(from, to, HistoryResolution.Minute, cancellationToken).ConfigureAwait(false);
        var stored = await repository.GetEventsAsync(from, to, MaxEvents, cancellationToken).ConfigureAwait(false);
        var converted = minutes.Select(ToSnapshot).ToArray();
        var narrated = await Task.Run(() => ReplayNarrator.Narrate(converted, stored), cancellationToken).ConfigureAwait(false);
        return new ReplayData(from, to, false, converted, stored, narrated, "Per-minute averages from the local history.");
    }

    /// <summary>
    /// Applications recorded around <paramref name="time"/> in the long-term history (the five-minute period containing it),
    /// for periods where per-second application data is no longer in memory.
    /// </summary>
    public Task<IReadOnlyList<AppUsageStatistics>> GetAppsAroundAsync(DateTimeOffset time, CancellationToken cancellationToken)
    {
        var start = SystemUsageAggregate.BucketStart(time, HistoryResolution.FiveMinutes);
        return repository.GetAppUsageAsync(start, start.AddMinutes(5), cancellationToken);
    }

    /// <summary>A per-minute bucket as a snapshot (averages; no application data at this resolution).</summary>
    public static MetricSnapshot ToSnapshot(SystemUsageAggregate minute)
    {
        ArgumentNullException.ThrowIfNull(minute);
        return new MetricSnapshot
        {
            Timestamp = minute.Start + (minute.Length / 2),
            CpuPercent = minute.Cpu?.Average,
            MemoryPercent = minute.Memory?.Average,
            MemoryTotalBytes = minute.MemoryTotalBytes,
            MemoryUsedBytes = minute.Memory is { } memory && minute.MemoryTotalBytes is { } total ? (ulong)(total * memory.Average / 100) : null,
            DiskActivePercent = minute.Disk?.Average,
            NetworkReceiveBitsPerSecond = minute.NetworkReceive?.Average,
            NetworkSendBitsPerSecond = minute.NetworkSend?.Average,
            GpuPercent = minute.Gpu?.Average,
            ProcessCount = minute.ProcessCount is { } count ? (int)Math.Round(count.Average) : null,
        };
    }
}
