using PulseDesk.Core.Metrics;

namespace PulseDesk.Tests.Metrics;

public sealed class MetricHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Series_GetSamples_ReturnsOnlyWindow()
    {
        var series = new MetricSeries("cpu", 100);
        for (var i = 0; i < 10; i++)
        {
            series.Add(new MetricSample(T0.AddSeconds(i), i * 10));
        }

        var samples = series.GetSamples(T0.AddSeconds(7));

        Assert.Equal([70, 80, 90], samples.Select(s => s.Value));
    }

    [Fact]
    public void Series_IgnoresOutOfOrderAndNonFiniteSamples()
    {
        var series = new MetricSeries("cpu", 10);
        series.Add(new MetricSample(T0.AddSeconds(5), 1));
        series.Add(new MetricSample(T0.AddSeconds(4), 2));
        series.Add(new MetricSample(T0.AddSeconds(6), double.NaN));
        series.Add(new MetricSample(T0.AddSeconds(7), double.PositiveInfinity));

        Assert.Equal(1, series.Count);
        Assert.Equal(1, series.Latest?.Value);
    }

    [Fact]
    public void Series_Statistics_AreComputedOverWindow()
    {
        var series = new MetricSeries("cpu", 10);
        series.Add(new MetricSample(T0, 100));
        series.Add(new MetricSample(T0.AddSeconds(1), 10));
        series.Add(new MetricSample(T0.AddSeconds(2), 20));
        series.Add(new MetricSample(T0.AddSeconds(3), 30));

        var stats = series.GetStatistics(T0.AddSeconds(1));

        Assert.NotNull(stats);
        Assert.Equal(20, stats.Value.Average, precision: 10);
        Assert.Equal(10, stats.Value.Minimum);
        Assert.Equal(30, stats.Value.Maximum);
        Assert.Equal(3, stats.Value.Count);
    }

    [Fact]
    public void Series_Statistics_EmptyWindow_ReturnsNull()
    {
        var series = new MetricSeries("cpu", 10);
        series.Add(new MetricSample(T0, 50));

        Assert.Null(series.GetStatistics(T0.AddMinutes(1)));
    }

    [Fact]
    public void Series_KeepsOnlyCapacityNewestSamples()
    {
        var series = new MetricSeries("cpu", 5);
        for (var i = 0; i < 12; i++)
        {
            series.Add(new MetricSample(T0.AddSeconds(i), i));
        }

        var all = series.GetSamples(DateTimeOffset.MinValue);
        Assert.Equal([7, 8, 9, 10, 11], all.Select(s => s.Value));
    }

    [Fact]
    public void History_Record_CreatesSeriesOnDemand()
    {
        var history = new MetricHistory(capacityPerSeries: 3);
        Assert.Null(history.Get(SeriesKeys.Cpu));
        Assert.Empty(history.GetSamples(SeriesKeys.Cpu, T0));

        history.Record(SeriesKeys.Cpu, T0, 12);
        history.Record(SeriesKeys.Gpu("luid_1"), T0, 40);

        Assert.Equal(3, history.Get(SeriesKeys.Cpu)!.Capacity);
        Assert.Contains("gpu:luid_1", history.Keys);
        Assert.Single(history.GetSamples(SeriesKeys.Cpu, T0));
    }

    [Fact]
    public void History_Clear_RemovesAllSeries()
    {
        var history = new MetricHistory(3);
        history.Record(SeriesKeys.Memory, T0, 1);
        history.Clear();

        Assert.Empty(history.Keys);
    }

    [Fact]
    public async Task Series_ConcurrentWritersAndReaders_StayConsistent()
    {
        var series = new MetricSeries("cpu", 256);
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                series.Add(new MetricSample(T0.AddMilliseconds(i), i));
            }
        }, TestContext.Current.CancellationToken);

        var reader = Task.Run(() =>
        {
            while (!writer.IsCompleted)
            {
                var samples = series.GetSamples(DateTimeOffset.MinValue);
                for (var i = 1; i < samples.Length; i++)
                {
                    Assert.True(samples[i].Timestamp > samples[i - 1].Timestamp);
                }
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(writer, reader);
        Assert.Equal(256, series.Count);
    }
}
