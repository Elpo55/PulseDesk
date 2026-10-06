using System.Globalization;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.History;
using PulseDesk.Core.Interfaces;

namespace PulseDesk.Core.Analysis;

/// <summary>Reference periods the current activity is compared with.</summary>
public enum ComparisonPeriod
{
    LastHour,
    Today,
    Yesterday,
    Last7Days,
    Last30Days,

    /// <summary>The same hour of the day on previous days (last 30 days, today excluded).</summary>
    UsualAtThisHour,
}

/// <summary>Average of one metric over one reference period, or why it is not known.</summary>
/// <param name="Period">Period.</param>
/// <param name="Average">Average over the measured time, or null when there is not enough data.</param>
/// <param name="MonitoredHours">Measured time in the period.</param>
/// <param name="Days">Distinct days with data in the period.</param>
/// <param name="NotEnoughData">Why no value is given, when <paramref name="Average"/> is null.</param>
public sealed record PeriodAverage(ComparisonPeriod Period, double? Average, double MonitoredHours, int Days, string? NotEnoughData)
{
    public bool IsKnown => Average is not null;
}

/// <summary>One metric now and over every reference period.</summary>
/// <param name="Metric">Metric.</param>
/// <param name="Name">Display name.</param>
/// <param name="Current">Average of the last minutes (in-memory measurements), or null.</param>
/// <param name="Periods">Reference periods, in <see cref="ComparisonPeriod"/> order.</param>
public sealed record MetricComparison(HistoryMetric Metric, string Name, double? Current, IReadOnlyList<PeriodAverage> Periods)
{
    public PeriodAverage Get(ComparisonPeriod period) => Periods.First(p => p.Period == period);
}

/// <summary>The current activity compared with the local history: last hour, today, yesterday, 7 and 30 days, usual at this hour.</summary>
/// <param name="Time">When it was computed.</param>
/// <param name="Metrics">Each compared metric.</param>
/// <param name="Summary">One sentence: the clearest difference, or what data is still missing.</param>
public sealed record UsageComparison(DateTimeOffset Time, IReadOnlyList<MetricComparison> Metrics, string Summary)
{
    public static UsageComparison Empty { get; } = new(DateTimeOffset.MinValue, [], "Loading the history…");
}

/// <summary>
/// Compares the current activity with periods of the local history (pure, deterministic). An average is given only when
/// the period has enough measured time; otherwise the period says how much data it has. Nothing is extrapolated.
/// </summary>
public static class UsageComparer
{
    /// <summary>"Now" is the average of this many recent minutes.</summary>
    public static readonly TimeSpan CurrentWindow = TimeSpan.FromMinutes(15);

    /// <summary>Difference (percentage points) from which the summary calls the current activity higher or lower.</summary>
    public const double NotablePoints = 10;

    private static readonly TimeSpan MinimumCurrent = TimeSpan.FromMinutes(2);

    private static readonly (HistoryMetric Metric, string Name)[] Compared =
    [
        (HistoryMetric.Cpu, "CPU"),
        (HistoryMetric.Memory, "Memory"),
        (HistoryMetric.Disk, "Disk active time"),
        (HistoryMetric.Gpu, "GPU"),
    ];

    /// <summary>Requirements per period: measured hours and distinct days.</summary>
    private static (double Hours, int Days) Requirement(ComparisonPeriod period) => period switch
    {
        ComparisonPeriod.LastHour => (0.5, 1),
        ComparisonPeriod.Today or ComparisonPeriod.Yesterday => (1, 1),
        ComparisonPeriod.Last7Days => (4, 2),
        ComparisonPeriod.Last30Days => (24, 8),
        _ => (0.75, 3),
    };

    /// <param name="recent">Recent in-memory snapshots (for "now").</param>
    /// <param name="buckets">Minute and hourly buckets of the local history (any order, may overlap: minutes win).</param>
    /// <param name="now">Current time.</param>
    /// <param name="zone">Time zone that defines days and hours of the day.</param>
    public static UsageComparison Compare(IReadOnlyList<MetricSnapshot> recent, IReadOnlyList<SystemUsageAggregate> buckets, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(recent);
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(zone);

        var chunks = Merge(buckets, now);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var midnight = new DateTimeOffset(localNow.Date, zone.GetUtcOffset(localNow.Date));
        var yesterday = midnight.AddDays(-1);

        var metrics = new List<MetricComparison>();
        foreach (var (metric, name) in Compared)
        {
            var current = Current(recent, metric);
            var periods = new List<PeriodAverage>
            {
                Average(ComparisonPeriod.LastHour, chunks.Where(c => c.Start >= now.AddHours(-1)), metric, zone),
                Average(ComparisonPeriod.Today, chunks.Where(c => c.Start >= midnight), metric, zone),
                Average(ComparisonPeriod.Yesterday, chunks.Where(c => c.Start >= yesterday && c.Start < midnight), metric, zone),
                Average(ComparisonPeriod.Last7Days, chunks.Where(c => c.Start >= now.AddDays(-7)), metric, zone),
                Average(ComparisonPeriod.Last30Days, chunks.Where(c => c.Start >= now.AddDays(-30)), metric, zone),
                Average(ComparisonPeriod.UsualAtThisHour, chunks.Where(c => c.Start >= now.AddDays(-30) && c.Start < midnight && TimeZoneInfo.ConvertTime(c.Start, zone).Hour == localNow.Hour), metric, zone),
            };

            // CPU and memory are always measured: shown even before there is data (dashes, with how much is missing).
            // Another metric never measured (GPU monitoring off, no adapter) is left out rather than shown empty.
            if (metric is HistoryMetric.Cpu or HistoryMetric.Memory || current is not null || periods.Any(p => p.MonitoredHours > 0))
            {
                metrics.Add(new MetricComparison(metric, name, current, periods));
            }
        }

        return new UsageComparison(now, metrics, Summary(metrics));
    }

    /// <summary>Label of a period, e.g. "Last 7 days".</summary>
    public static string Label(ComparisonPeriod period) => period switch
    {
        ComparisonPeriod.LastHour => "Last hour",
        ComparisonPeriod.Today => "Today",
        ComparisonPeriod.Yesterday => "Yesterday",
        ComparisonPeriod.Last7Days => "Last 7 days",
        ComparisonPeriod.Last30Days => "Last 30 days",
        _ => "Usual at this hour",
    };

    private static string Summary(IReadOnlyList<MetricComparison> metrics)
    {
        string? best = null;
        var bestGap = 0.0;
        foreach (var metric in metrics.Where(m => m.Metric is HistoryMetric.Cpu or HistoryMetric.Memory))
        {
            if (metric.Current is not { } current)
            {
                continue;
            }

            // The most specific reference available: the same hour on other days, then the last 7 or 30 days.
            var reference = new[] { ComparisonPeriod.UsualAtThisHour, ComparisonPeriod.Last7Days, ComparisonPeriod.Last30Days }
                .Select(metric.Get)
                .FirstOrDefault(p => p.IsKnown);
            if (reference is null)
            {
                continue;
            }

            var gap = current - reference.Average!.Value;
            if (Math.Abs(gap) >= NotablePoints && Math.Abs(gap) > bestGap)
            {
                bestGap = Math.Abs(gap);
                best = string.Create(CultureInfo.CurrentCulture,
                    $"{metric.Name} is {Math.Abs(gap):0} points {(gap > 0 ? "above" : "below")} your usual level: {MetricFormatter.Percent(current)} now vs {MetricFormatter.Percent(reference.Average)} ({Label(reference.Period).ToLowerInvariant()}).");
            }
        }

        if (best is not null)
        {
            return best;
        }

        var anyReference = metrics.Any(m => m.Periods.Any(p => p.Period is ComparisonPeriod.Last7Days or ComparisonPeriod.Last30Days or ComparisonPeriod.UsualAtThisHour && p.IsKnown));
        return anyReference
            ? "Current activity is close to your usual level (within 10 points)."
            : "Not enough history yet to say what is usual: the 7-day comparison needs 4 hours over 2 days, the 30-day one 24 hours over 8 days.";
    }

    private static double? Current(IReadOnlyList<MetricSnapshot> recent, HistoryMetric metric)
    {
        if (recent.Count == 0)
        {
            return null;
        }

        var from = recent[^1].Timestamp - CurrentWindow;
        double sum = 0;
        var count = 0;
        DateTimeOffset? first = null;
        foreach (var snapshot in recent)
        {
            if (snapshot.Timestamp >= from && snapshot.Get(metric) is { } value)
            {
                first ??= snapshot.Timestamp;
                sum += value;
                count++;
            }
        }

        return count > 0 && recent[^1].Timestamp - first!.Value >= MinimumCurrent ? sum / count : null;
    }

    private static PeriodAverage Average(ComparisonPeriod period, IEnumerable<Chunk> chunks, HistoryMetric metric, TimeZoneInfo zone)
    {
        double sum = 0, seconds = 0;
        long count = 0;
        var days = new HashSet<DateTime>();
        foreach (var chunk in chunks)
        {
            if (chunk.Bucket.Get(metric) is not { } value)
            {
                continue;
            }

            sum += value.Average * value.Count;
            count += value.Count;
            seconds += chunk.Bucket.MonitoredSeconds;
            days.Add(TimeZoneInfo.ConvertTime(chunk.Start, zone).Date);
        }

        var hours = seconds / 3600;
        var (requiredHours, requiredDays) = Requirement(period);
        if (count == 0 || hours < requiredHours || days.Count < requiredDays)
        {
            var needed = requiredDays > 1
                ? $"{FormatHours(requiredHours)} over {requiredDays.ToString(CultureInfo.CurrentCulture)} days"
                : FormatHours(requiredHours);
            return new PeriodAverage(period, null, hours, days.Count,
                $"Not enough data: {FormatHours(hours)} recorded{(requiredDays > 1 ? $" over {MetricFormatter.Plural(days.Count, "day")}" : string.Empty)}, {needed} needed");
        }

        return new PeriodAverage(period, sum / count, hours, days.Count, null);
    }

    /// <summary>
    /// One chunk per minute bucket, plus the hourly buckets of hours without minute data (minute data is more complete
    /// for recent hours; hourly summaries cover what was rolled up). No time is counted twice.
    /// </summary>
    private static List<Chunk> Merge(IReadOnlyList<SystemUsageAggregate> buckets, DateTimeOffset now)
    {
        var hoursWithMinutes = buckets
            .Where(b => b.Resolution == HistoryResolution.Minute)
            .Select(b => SystemUsageAggregate.BucketStart(b.Start, HistoryResolution.Hour))
            .ToHashSet();
        return buckets
            .Where(b => b.Start < now && (b.Resolution == HistoryResolution.Minute || (b.Resolution == HistoryResolution.Hour && !hoursWithMinutes.Contains(b.Start))))
            .Select(b => new Chunk(b.Start, b))
            .ToList();
    }

    private static string FormatHours(double hours) =>
        hours < 1 ? $"{(int)Math.Round(hours * 60)} min" : string.Create(CultureInfo.CurrentCulture, $"{hours:0.#} h");

    private readonly record struct Chunk(DateTimeOffset Start, SystemUsageAggregate Bucket);
}

/// <summary>Computes <see cref="UsageComparison"/> on demand from the local history (cached for a couple of minutes).</summary>
public sealed class UsageComparisonService(IHistoryRepository repository, IPerformanceHistory history, TimeProvider? timeProvider = null)
{
    /// <summary>A comparison is reused for this long: the history changes once a minute at most.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    /// <summary>Minute data is read for this long; older periods use hourly summaries.</summary>
    private static readonly TimeSpan MinuteDataRead = TimeSpan.FromDays(2);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UsageComparison? _cached;

    /// <summary>The current activity compared with the history.</summary>
    public async Task<UsageComparison> CompareAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_cached is { } cached && now - cached.Time < CacheDuration)
            {
                return cached;
            }

            // From a whole hour, so that hour is entirely covered by minutes (it then replaces its hourly summary).
            var minuteFrom = SystemUsageAggregate.BucketStart(now - MinuteDataRead, HistoryResolution.Hour);
            var minutes = await repository.GetSystemUsageAsync(minuteFrom, now, HistoryResolution.Minute, cancellationToken).ConfigureAwait(false);
            var hours = await repository.GetSystemUsageAsync(now.AddDays(-31), now, HistoryResolution.Hour, cancellationToken).ConfigureAwait(false);
            var recent = history.GetRecent(UsageComparer.CurrentWindow);
            var comparison = await Task.Run(() => UsageComparer.Compare(recent, [.. hours, .. minutes], now, _time.LocalTimeZone), cancellationToken).ConfigureAwait(false);
            _cached = comparison;
            return comparison;
        }
        finally
        {
            _gate.Release();
        }
    }
}
