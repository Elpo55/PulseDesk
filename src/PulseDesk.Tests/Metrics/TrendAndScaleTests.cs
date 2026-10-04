using PulseDesk.Core.Metrics;

namespace PulseDesk.Tests.Metrics;

public sealed class TrendAndScaleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static MetricSample[] Series(params double[] values) =>
        values.Select((v, i) => new MetricSample(T0.AddSeconds(i), v)).ToArray();

    [Fact]
    public void Trend_TooFewSamples_IsUnknown()
    {
        Assert.Equal(TrendDirection.Unknown, TrendCalculator.Compute(Series(10, 20)).Direction);
    }

    [Fact]
    public void Trend_SteadyIncrease_IsRising()
    {
        var trend = TrendCalculator.Compute(Series(10, 20, 30, 40, 50, 60));

        Assert.Equal(TrendDirection.Rising, trend.Direction);
        Assert.Equal(600, trend.ChangePerMinute, precision: 6); // +10 per second
    }

    [Fact]
    public void Trend_SteadyDecrease_IsFalling()
    {
        Assert.Equal(TrendDirection.Falling, TrendCalculator.Compute(Series(80, 70, 60, 50, 40)).Direction);
    }

    [Fact]
    public void Trend_SmallFluctuations_AreStable()
    {
        Assert.Equal(TrendDirection.Stable, TrendCalculator.Compute(Series(20, 22, 19, 21, 20, 21)).Direction);
    }

    [Fact]
    public void Trend_SingleSpike_DoesNotFlipFlatSeries()
    {
        var trend = TrendCalculator.Compute(Series(20, 20, 20, 95, 20, 20, 20, 20, 20, 20), stableBand: 10);

        Assert.Equal(TrendDirection.Stable, trend.Direction);
    }

    [Fact]
    public void Trend_IdenticalTimestamps_IsUnknown()
    {
        var samples = new[] { new MetricSample(T0, 1), new MetricSample(T0, 5), new MetricSample(T0, 9) };

        Assert.Equal(TrendDirection.Unknown, TrendCalculator.Compute(samples).Direction);
    }

    [Fact]
    public void Lttb_BelowThreshold_ReturnsInputUnchanged()
    {
        var data = Series(1, 2, 3, 4);

        Assert.Equal(data, Downsampler.Lttb(data, 10));
    }

    [Fact]
    public void Lttb_ReducesToThreshold_KeepingEndpointsAndPeak()
    {
        var values = Enumerable.Range(0, 1000).Select(i => i == 500 ? 100.0 : 10.0).ToArray();
        var data = Series(values);

        var result = Downsampler.Lttb(data, 50);

        Assert.Equal(50, result.Length);
        Assert.Equal(data[0], result[0]);
        Assert.Equal(data[^1], result[^1]);
        Assert.Contains(result, s => s.Value == 100.0);
        Assert.True(result.Zip(result.Skip(1)).All(p => p.First.Timestamp < p.Second.Timestamp));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.4, 1)]
    [InlineData(1.3, 2)]
    [InlineData(2.2, 2.5)]
    [InlineData(3.1, 5)]
    [InlineData(7, 10)]
    [InlineData(12, 20)]
    [InlineData(82_400_000, 100_000_000)]
    [InlineData(double.NaN, 1)]
    public void NiceMaximum_RoundsUpToNiceValues(double value, double expected)
    {
        Assert.Equal(expected, ChartScale.NiceMaximum(value), precision: 6);
    }

    [Fact]
    public void NiceMaximum_RespectsMinimum()
    {
        Assert.Equal(1_000_000, ChartScale.NiceMaximum(1200, minimum: 1_000_000));
    }

    [Theory]
    [InlineData(0UL, 0UL, 0)]
    [InlineData(50UL, 200UL, 25)]
    [InlineData(300UL, 200UL, 100)]
    [InlineData(1UL, 3UL, 33.333333333)]
    public void Percentages_Of_ClampsAndHandlesZeroTotal(ulong part, ulong total, double expected)
    {
        Assert.Equal(expected, Percentages.Of(part, total), precision: 6);
    }

    [Fact]
    public void Percentages_Of_Doubles_HandlesInvalidInput()
    {
        Assert.Equal(0, Percentages.Of(double.NaN, 10));
        Assert.Equal(0, Percentages.Of(5, 0));
        Assert.Equal(0, Percentages.Clamp(-4));
        Assert.Equal(100, Percentages.Clamp(140));
    }
}
