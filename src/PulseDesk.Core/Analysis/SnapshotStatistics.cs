using PulseDesk.Core.History;
using PulseDesk.Core.Metrics;

namespace PulseDesk.Core.Analysis;

/// <summary>A period during which a metric stayed at or above a threshold.</summary>
/// <param name="Since">First snapshot of the period.</param>
/// <param name="Until">Last snapshot of the period.</param>
/// <param name="Average">Average value over the period.</param>
/// <param name="Peak">Highest value over the period.</param>
/// <param name="Samples">Number of snapshots in the period.</param>
public readonly record struct SustainedSpan(DateTimeOffset Since, DateTimeOffset Until, double Average, double Peak, int Samples)
{
    public TimeSpan Duration => Until - Since;
}

/// <summary>Average, peak and count of a metric over snapshots.</summary>
public readonly record struct WindowSummary(double Average, double Peak, double Minimum, int Count, DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Duration => To - From;
}

/// <summary>Statistics over history snapshots shared by diagnosis rules, alert rules and the replay narrator.</summary>
public static class SnapshotStatistics
{
    /// <summary>Dips below the threshold shorter than this do not break a sustained period (CPU usage is noisy).</summary>
    public static readonly TimeSpan DefaultDipTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long <paramref name="value"/> has stayed at or above <paramref name="threshold"/> up to the latest
    /// snapshot. Short dips (up to <paramref name="dipTolerance"/>) are tolerated; a missing value or a gap in
    /// the data ends the period. Returns null when the latest value is below the threshold.
    /// </summary>
    public static SustainedSpan? Sustained(
        IReadOnlyList<MetricSnapshot> snapshots,
        Func<MetricSnapshot, double?> value,
        double threshold,
        TimeSpan? dipTolerance = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(value);
        if (snapshots.Count == 0 || value(snapshots[^1]) is not { } last || last < threshold)
        {
            return null;
        }

        var tolerance = dipTolerance ?? DefaultDipTolerance;
        var until = snapshots[^1].Timestamp;
        var since = until;
        DateTimeOffset? dipEnd = null;
        var previous = until;

        for (var i = snapshots.Count - 1; i >= 0; i--)
        {
            var snapshot = snapshots[i];
            if (previous - snapshot.Timestamp > SystemUsageAggregator.MaxSampleInterval || value(snapshot) is not { } v)
            {
                break;
            }

            previous = snapshot.Timestamp;
            if (v >= threshold)
            {
                dipEnd = null;
                since = snapshot.Timestamp;
            }
            else
            {
                dipEnd ??= snapshot.Timestamp;
                if (dipEnd.Value - snapshot.Timestamp >= tolerance)
                {
                    break;
                }
            }
        }

        // Statistics over the period only: a dip that ended the period is not part of it, short dips inside it are.
        var inPeriod = snapshots.Where(s => s.Timestamp >= since).Select(value).OfType<double>().ToArray();
        return inPeriod.Length == 0
            ? null
            : new SustainedSpan(since, until, inPeriod.Average(), inPeriod.Max(), inPeriod.Length);
    }

    /// <summary>Average, peak and minimum of a metric over snapshots; null when it was never available.</summary>
    public static WindowSummary? Summarize(IReadOnlyList<MetricSnapshot> snapshots, Func<MetricSnapshot, double?> value)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(value);
        double sum = 0, peak = double.MinValue, minimum = double.MaxValue;
        var count = 0;
        DateTimeOffset? from = null, to = null;
        foreach (var snapshot in snapshots)
        {
            if (value(snapshot) is not { } v)
            {
                continue;
            }

            sum += v;
            peak = Math.Max(peak, v);
            minimum = Math.Min(minimum, v);
            count++;
            from ??= snapshot.Timestamp;
            to = snapshot.Timestamp;
        }

        return count == 0 ? null : new WindowSummary(sum / count, peak, minimum, count, from!.Value, to!.Value);
    }

    /// <summary>Robust trend (Theil–Sen) of a metric over snapshots.</summary>
    public static Trend Trend(IReadOnlyList<MetricSnapshot> snapshots, Func<MetricSnapshot, double?> value, double stableBand = 5)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var samples = snapshots
            .Where(s => value(s) is not null)
            .Select(s => new MetricSample(s.Timestamp, value(s)!.Value))
            .ToArray();
        return TrendCalculator.Compute(samples, stableBand);
    }

    /// <summary>
    /// Usage of one application over snapshots, one point per process sample (snapshots share their
    /// application list until processes are sampled again). An application absent from a sample counts as 0.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset Time, AppSample? App)> AppSeries(IReadOnlyList<MetricSnapshot> snapshots, string appKey)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var points = new List<(DateTimeOffset, AppSample?)>();
        IReadOnlyList<AppSample>? previous = null;
        foreach (var snapshot in snapshots)
        {
            if (ReferenceEquals(snapshot.TopApps, previous) || snapshot.TopApps.Count == 0)
            {
                continue;
            }

            previous = snapshot.TopApps;
            points.Add((snapshot.Timestamp, snapshot.TopApps.FirstOrDefault(a => a.Key == appKey)));
        }

        return points;
    }
}
