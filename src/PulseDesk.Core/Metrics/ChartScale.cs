namespace PulseDesk.Core.Metrics;

/// <summary>Axis scaling helpers for charts whose maximum is not fixed (network throughput, I/O).</summary>
public static class ChartScale
{
    private static readonly double[] NiceSteps = [1, 2, 2.5, 5, 10];

    /// <summary>
    /// Rounds <paramref name="value"/> up to a "nice" axis maximum (1, 2, 2.5 or 5 times a power of ten),
    /// never below <paramref name="minimum"/>.
    /// </summary>
    public static double NiceMaximum(double value, double minimum = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimum);
        if (!double.IsFinite(value) || value <= minimum)
        {
            return minimum;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in NiceSteps)
        {
            var candidate = step * magnitude;
            if (candidate >= value)
            {
                return candidate;
            }
        }

        return 10 * magnitude;
    }
}
