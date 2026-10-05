using System.Globalization;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.History;

namespace PulseDesk.Core.Analysis;

/// <summary>Whether PulseDesk knows enough about this PC's usual behavior to compare against it.</summary>
public enum BaselineStatus
{
    /// <summary>Not enough history yet: PulseDesk does not claim to know what is usual.</summary>
    Collecting,

    /// <summary>Enough history to describe the usual behavior.</summary>
    Ready,
}

/// <summary>Usual range of one metric, from per-minute averages of the local history.</summary>
/// <param name="Metric">Metric.</param>
/// <param name="Median">Median minute average.</param>
/// <param name="P25">25th percentile (lower end of the usual range).</param>
/// <param name="P75">75th percentile (upper end of the usual range).</param>
/// <param name="P95">95th percentile (above it, activity is unusual).</param>
/// <param name="Minutes">Number of minutes of data used.</param>
public sealed record MetricBaseline(HistoryMetric Metric, double Median, double P25, double P75, double P95, int Minutes)
{
    /// <summary>"35–50%" for percentages.</summary>
    public string UsualRange => string.Create(CultureInfo.CurrentCulture, $"{Math.Round(P25):0}–{Math.Round(P75):0}%");
}

/// <summary>
/// The usual behavior of this PC, built progressively from the local history. Until enough data exists its
/// status is <see cref="BaselineStatus.Collecting"/> and no comparison with "usual" is made.
/// </summary>
/// <param name="Status">Whether the baseline can be used.</param>
/// <param name="MinutesOfData">Minutes of history available.</param>
/// <param name="From">Start of the data used.</param>
/// <param name="To">End of the data used.</param>
/// <param name="Metrics">Usual range of each metric with enough data.</param>
public sealed record UsageBaseline(BaselineStatus Status, int MinutesOfData, DateTimeOffset? From, DateTimeOffset? To, IReadOnlyDictionary<HistoryMetric, MetricBaseline> Metrics)
{
    /// <summary>No data yet.</summary>
    public static UsageBaseline Empty { get; } = new(BaselineStatus.Collecting, 0, null, null, new Dictionary<HistoryMetric, MetricBaseline>());

    public bool IsReady => Status == BaselineStatus.Ready;

    /// <summary>The usual range of a metric, when the baseline is ready and the metric has data.</summary>
    public MetricBaseline? Get(HistoryMetric metric) =>
        IsReady && Metrics.TryGetValue(metric, out var baseline) ? baseline : null;

    /// <summary>Text shown while collecting, e.g. "Collecting baseline data… (1h 12m of 4h)".</summary>
    public string Description => IsReady
        ? $"Usual behavior learned from {MetricFormatter.DurationCompact(TimeSpan.FromMinutes(MinutesOfData))} of history"
        : $"Collecting baseline data… ({MetricFormatter.DurationCompact(TimeSpan.FromMinutes(MinutesOfData))} recorded of the {MetricFormatter.Plural((int)BaselineCalculator.MinimumData.TotalHours, "hour")} needed before PulseDesk compares with your usual activity)";
}

/// <summary>Computes the usual behavior of the PC from per-minute history (pure, deterministic).</summary>
public static class BaselineCalculator
{
    /// <summary>Minimum history before PulseDesk describes anything as "usual".</summary>
    public static readonly TimeSpan MinimumData = TimeSpan.FromHours(4);

    /// <summary>History used for the baseline.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>The most recent minutes are left out, so a problem happening now does not shift what is "usual".</summary>
    public static readonly TimeSpan ExcludedRecent = TimeSpan.FromMinutes(15);

    private static readonly HistoryMetric[] Metrics = [HistoryMetric.Cpu, HistoryMetric.Memory, HistoryMetric.Disk, HistoryMetric.Gpu];

    /// <summary>Builds the baseline from minute buckets (any order).</summary>
    public static UsageBaseline Compute(IReadOnlyList<SystemUsageAggregate> minutes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        var usable = minutes
            .Where(m => m.Resolution == HistoryResolution.Minute && m.Start < now - ExcludedRecent && m.Start >= now - Window)
            .ToList();
        if (usable.Count == 0)
        {
            return UsageBaseline.Empty;
        }

        var from = usable.Min(m => m.Start);
        var to = usable.Max(m => m.End);
        var required = (int)MinimumData.TotalMinutes;
        var metrics = new Dictionary<HistoryMetric, MetricBaseline>();
        foreach (var metric in Metrics)
        {
            var values = usable.Select(m => m.Get(metric)).Where(v => v is not null).Select(v => v!.Value.Average).ToArray();
            if (values.Length >= required)
            {
                Array.Sort(values);
                metrics[metric] = new MetricBaseline(
                    metric,
                    Percentile(values, 0.50),
                    Percentile(values, 0.25),
                    Percentile(values, 0.75),
                    Percentile(values, 0.95),
                    values.Length);
            }
        }

        var status = usable.Count >= required && metrics.ContainsKey(HistoryMetric.Cpu) ? BaselineStatus.Ready : BaselineStatus.Collecting;
        return new UsageBaseline(status, usable.Count, from, to, metrics);
    }

    /// <summary>Percentile of sorted values with linear interpolation.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        var position = Math.Clamp(fraction, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }
}
