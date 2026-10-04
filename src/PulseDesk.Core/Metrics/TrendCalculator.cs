namespace PulseDesk.Core.Metrics;

/// <summary>Direction of a metric over a window.</summary>
public enum TrendDirection
{
    /// <summary>Not enough samples to decide.</summary>
    Unknown,
    Stable,
    Rising,
    Falling,
}

/// <summary>Result of a trend computation.</summary>
/// <param name="Direction">Overall direction.</param>
/// <param name="ChangePerMinute">Estimated change of the value per minute (units of the metric).</param>
public readonly record struct Trend(TrendDirection Direction, double ChangePerMinute)
{
    public static Trend Unknown { get; } = new(TrendDirection.Unknown, 0);
}

/// <summary>
/// Computes trends with the Theil–Sen estimator (median of pairwise slopes). Unlike a least-squares fit,
/// a single spike — very common for CPU usage — does not tilt the result.
/// </summary>
public static class TrendCalculator
{
    /// <summary>Minimum number of samples needed to report a trend.</summary>
    public const int MinimumSamples = 3;

    /// <summary>Long windows are first averaged into this many buckets, bounding the cost to ~7,000 slopes.</summary>
    internal const int MaxPoints = 120;

    /// <param name="samples">Samples ordered by time.</param>
    /// <param name="stableBand">
    /// Total change over the window (in metric units) below which the metric is considered stable.
    /// For percentages, the default of 5 means "less than 5 points over the window".
    /// </param>
    public static Trend Compute(ReadOnlySpan<MetricSample> samples, double stableBand = 5.0)
    {
        if (samples.Length < MinimumSamples)
        {
            return Trend.Unknown;
        }

        var (x, y) = Reduce(samples);
        var slopes = new List<double>(x.Length * (x.Length - 1) / 2);
        for (var i = 0; i < x.Length; i++)
        {
            for (var j = i + 1; j < x.Length; j++)
            {
                var dx = x[j] - x[i];
                if (dx > 0)
                {
                    slopes.Add((y[j] - y[i]) / dx);
                }
            }
        }

        if (slopes.Count == 0)
        {
            // All samples share the same timestamp: there is no time axis to fit against.
            return Trend.Unknown;
        }

        slopes.Sort();
        var middle = slopes.Count / 2;
        var slopePerSecond = slopes.Count % 2 == 1 ? slopes[middle] : (slopes[middle - 1] + slopes[middle]) / 2;

        var spanSeconds = (samples[^1].Timestamp - samples[0].Timestamp).TotalSeconds;
        var totalChange = slopePerSecond * spanSeconds;
        var direction = Math.Abs(totalChange) < stableBand
            ? TrendDirection.Stable
            : totalChange > 0 ? TrendDirection.Rising : TrendDirection.Falling;

        return new Trend(direction, slopePerSecond * 60);
    }

    /// <summary>Converts samples to (seconds, value) points, averaging them into buckets when there are too many.</summary>
    private static (double[] X, double[] Y) Reduce(ReadOnlySpan<MetricSample> samples)
    {
        var origin = samples[0].Timestamp;
        var count = Math.Min(samples.Length, MaxPoints);
        var x = new double[count];
        var y = new double[count];

        for (var bucket = 0; bucket < count; bucket++)
        {
            var start = (int)((long)bucket * samples.Length / count);
            var end = (int)((long)(bucket + 1) * samples.Length / count);
            double sumX = 0, sumY = 0;
            for (var i = start; i < end; i++)
            {
                sumX += (samples[i].Timestamp - origin).TotalSeconds;
                sumY += samples[i].Value;
            }

            x[bucket] = sumX / (end - start);
            y[bucket] = sumY / (end - start);
        }

        return (x, y);
    }
}
