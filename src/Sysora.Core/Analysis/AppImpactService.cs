using Sysora.Core.History;
using Sysora.Core.Interfaces;

namespace Sysora.Core.Analysis;

/// <summary>
/// Answers "which application slows my PC down the most?": gathers measured usage for a period (this session
/// from memory, longer periods from the local history) and ranks applications with <see cref="IAppImpactAnalyzer"/>.
/// </summary>
public sealed class AppImpactService(
    ProcessHistory sessions,
    IHistoryRepository repository,
    IAppImpactAnalyzer analyzer,
    IMetricsMonitor monitor,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Ranks applications over a period. Runs off the calling thread for history periods.</summary>
    public async Task<AppImpactReport> GetReportAsync(AppImpactPeriod period, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var totalMemory = monitor.Current.Memory?.TotalBytes ?? 0;

        if (period == AppImpactPeriod.Session)
        {
            var start = sessions.SessionStart ?? now;
            var usage = await Task.Run(sessions.GetSessionStatistics, cancellationToken).ConfigureAwait(false);
            var ranked = analyzer.Rank(usage, new AppImpactContext(totalMemory, start, now));
            return new AppImpactReport(period, start, now, sessions.MonitoredSeconds, ranked)
            {
                Note = sessions.SessionStart is null
                    ? "Waiting for the first process measurements."
                    : "Every application seen since Sysora started, measured every few seconds.",
            };
        }

        var from = now - Length(period);
        var stored = await repository.GetAppUsageAsync(from, now, cancellationToken).ConfigureAwait(false);
        var results = analyzer.Rank(stored, new AppImpactContext(totalMemory, from, now));
        var monitored = stored.Count > 0 ? stored[0].MonitoredSeconds : 0;
        return new AppImpactReport(period, from, now, monitored, results)
        {
            Note = monitored <= 0
                ? "No application history recorded for this period yet. History is saved every five minutes while Sysora runs."
                : "From the local history: the most significant applications of each five-minute period (applications below that are not recorded). The last few minutes appear once their five-minute period ends.",
        };
    }

    /// <summary>Usage of one application over time, for its detail view.</summary>
    public async Task<AppUsageTimeline> GetTimelineAsync(string appKey, AppImpactPeriod period, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(appKey);
        var now = _time.GetUtcNow();
        if (period == AppImpactPeriod.Session)
        {
            var minutes = sessions.GetMinutes(appKey);
            return new AppUsageTimeline(minutes, TimeSpan.FromMinutes(1), minutes.Count > 0 ? minutes[0].Start : now, now);
        }

        var from = now - Length(period);
        var resolution = period == AppImpactPeriod.Last24Hours ? HistoryResolution.FiveMinutes : HistoryResolution.Hour;
        var buckets = (await repository.GetAppTimelineAsync(appKey, from, now, resolution, cancellationToken).ConfigureAwait(false)).ToList();
        if (resolution == HistoryResolution.Hour)
        {
            // Hours are summarized once complete; the most recent ones are still five-minute buckets.
            var detailFrom = buckets.Count > 0 ? buckets[^1].Start + TimeSpan.FromHours(1) : from;
            buckets.AddRange(await repository.GetAppTimelineAsync(appKey, detailFrom, now, HistoryResolution.FiveMinutes, cancellationToken).ConfigureAwait(false));
        }

        var points = buckets
            .Select(b => new AppMinutePoint(b.Start, b.CpuAverage, b.MemoryAverageBytes, b.IoAverageBytesPerSecond, b.ActiveSeconds))
            .ToArray();
        return new AppUsageTimeline(points, TimeSpan.FromSeconds((int)resolution), from, now);
    }

    private static TimeSpan Length(AppImpactPeriod period) => period switch
    {
        AppImpactPeriod.Last24Hours => TimeSpan.FromHours(24),
        AppImpactPeriod.Last7Days => TimeSpan.FromDays(7),
        _ => TimeSpan.Zero,
    };
}
