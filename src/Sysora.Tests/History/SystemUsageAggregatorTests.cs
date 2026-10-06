using Sysora.Core.History;

namespace Sysora.Tests.History;

public sealed class SystemUsageAggregatorTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Fact]
    public void SnapshotsOfOneMinute_FormOneBucket_WithAverageAndMaximum()
    {
        var aggregator = new SystemUsageAggregator();
        SystemUsageAggregate? completed = null;
        foreach (var snapshot in TestData.Series(T0, 60, i => i < 30 ? 20 : 80))
        {
            completed ??= aggregator.Add(snapshot);
        }

        Assert.Null(completed);
        var bucket = aggregator.Flush()!;
        Assert.Equal(T0, bucket.Start);
        Assert.Equal(60, bucket.SampleCount);
        Assert.Equal(50, bucket.Cpu!.Value.Average, 6);
        Assert.Equal(80, bucket.Cpu.Value.Maximum);
        Assert.Equal(59, bucket.MonitoredSeconds, 6);
        Assert.Null(aggregator.Flush());
    }

    [Fact]
    public void NewMinute_ReturnsPreviousBucket()
    {
        var aggregator = new SystemUsageAggregator();
        var completed = TestData.Series(T0, 90, i => i).Select(aggregator.Add).Where(b => b is not null).ToList();

        var first = Assert.Single(completed)!;
        Assert.Equal(T0, first.Start);
        Assert.Equal(60, first.SampleCount);
        Assert.Equal(29.5, first.Cpu!.Value.Average, 6);
        Assert.Equal(T0.AddMinutes(1), aggregator.Flush()!.Start);
    }

    [Fact]
    public void UnavailableMetric_IsNullNotZero()
    {
        var aggregator = new SystemUsageAggregator();
        foreach (var snapshot in TestData.Series(T0, 10, i => 10, disk: _ => null))
        {
            aggregator.Add(snapshot);
        }

        var bucket = aggregator.Flush()!;
        Assert.Null(bucket.Disk);
        Assert.Null(bucket.Gpu);
        Assert.NotNull(bucket.Cpu);
        Assert.Equal(10, bucket.Memory!.Value.Count);
    }

    [Fact]
    public void GapBetweenSnapshots_IsNotCountedAsMonitoredTime()
    {
        var aggregator = new SystemUsageAggregator(HistoryResolution.Hour);
        aggregator.Add(TestData.Metric(T0, 10));
        aggregator.Add(TestData.Metric(T0.AddSeconds(1), 10));
        aggregator.Add(TestData.Metric(T0.AddMinutes(30), 10));
        aggregator.Add(TestData.Metric(T0.AddMinutes(30).AddSeconds(2), 10));

        Assert.Equal(3, aggregator.Flush()!.MonitoredSeconds, 6);
    }
}
