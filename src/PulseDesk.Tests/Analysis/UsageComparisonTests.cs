using PulseDesk.Core.Analysis;
using PulseDesk.Core.History;

namespace PulseDesk.Tests.Analysis;

public sealed class UsageComparisonTests
{
    // Monday 2 March 2026, 15:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WithoutHistory_EveryPeriodSaysHowMuchDataIsMissing()
    {
        var comparison = UsageComparer.Compare(Recent(cpu: 40), [], Now, TimeZoneInfo.Utc);

        var cpu = comparison.Metrics.Single(m => m.Metric == HistoryMetric.Cpu);
        Assert.Equal(40, cpu.Current!.Value, 0.001);
        Assert.All(cpu.Periods, p =>
        {
            Assert.Null(p.Average);
            Assert.StartsWith("Not enough data", p.NotEnoughData, StringComparison.Ordinal);
        });
        Assert.StartsWith("Not enough history yet", comparison.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(comparison.Metrics, m => m.Metric == HistoryMetric.Gpu);
    }

    [Fact]
    public void LastHourTodayAndYesterday_UseTheirOwnData()
    {
        var buckets = new List<SystemUsageAggregate>();
        buckets.AddRange(Minutes(Now.AddHours(-1), 60, cpu: 30));
        buckets.AddRange(Minutes(Now.AddHours(-5), 60, cpu: 50));
        buckets.AddRange(Minutes(Now.AddHours(-20), 90, cpu: 10));

        var cpu = UsageComparer.Compare(Recent(cpu: 35), buckets, Now, TimeZoneInfo.Utc).Metrics.Single(m => m.Metric == HistoryMetric.Cpu);

        Assert.Equal(30, cpu.Get(ComparisonPeriod.LastHour).Average!.Value, 0.001);
        Assert.Equal(40, cpu.Get(ComparisonPeriod.Today).Average!.Value, 0.001);
        Assert.Equal(10, cpu.Get(ComparisonPeriod.Yesterday).Average!.Value, 0.001);
        Assert.Equal(1.5, cpu.Get(ComparisonPeriod.Yesterday).MonitoredHours, 0.001);
        // Two days of data but only 3.5 hours: not enough for "last 7 days".
        Assert.Null(cpu.Get(ComparisonPeriod.Last7Days).Average);
        Assert.Contains("over 2 days", cpu.Get(ComparisonPeriod.Last7Days).NotEnoughData, StringComparison.Ordinal);
    }

    [Fact]
    public void MinuteData_ReplacesTheHourlySummaryOfTheSameHour()
    {
        var hour = Now.AddHours(-1);
        var buckets = new List<SystemUsageAggregate> { Hourly(hour, cpu: 90) };
        buckets.AddRange(Minutes(hour, 60, cpu: 10));

        var cpu = UsageComparer.Compare([], buckets, Now, TimeZoneInfo.Utc).Metrics.Single(m => m.Metric == HistoryMetric.Cpu);

        var lastHour = cpu.Get(ComparisonPeriod.LastHour);
        Assert.Equal(10, lastHour.Average!.Value, 0.001);
        Assert.Equal(1, lastHour.MonitoredHours, 0.001);
    }

    [Fact]
    public void MonthOfHistory_GivesUsualLevelsAndAClearSummary()
    {
        var buckets = new List<SystemUsageAggregate>();
        for (var day = 1; day <= 29; day++)
        {
            for (var hour = 9; hour <= 18; hour++)
            {
                var start = Now.Date.AddDays(-day).AddHours(hour);
                // Busier at 15:00 than the rest of the day.
                buckets.Add(Hourly(new DateTimeOffset(start, TimeSpan.Zero), cpu: hour == 15 ? 25 : 15));
            }
        }

        var comparison = UsageComparer.Compare(Recent(cpu: 80), buckets, Now, TimeZoneInfo.Utc);
        var cpu = comparison.Metrics.Single(m => m.Metric == HistoryMetric.Cpu);

        Assert.Equal(16, cpu.Get(ComparisonPeriod.Last30Days).Average!.Value, 0.001);
        // Seven days back from 15:00 includes only the afternoon of the first day.
        Assert.Equal(1030.0 / 64, cpu.Get(ComparisonPeriod.Last7Days).Average!.Value, 0.001);
        var usual = cpu.Get(ComparisonPeriod.UsualAtThisHour);
        Assert.Equal(25, usual.Average!.Value, 0.001);
        Assert.Equal(29, usual.Days);
        Assert.Null(cpu.Get(ComparisonPeriod.Today).Average);
        Assert.Contains("CPU is 55 points above your usual level", comparison.Summary, StringComparison.Ordinal);
        Assert.Contains("usual at this hour", comparison.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivityCloseToUsual_IsSaidSo()
    {
        var buckets = new List<SystemUsageAggregate>();
        for (var day = 1; day <= 10; day++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                buckets.Add(Hourly(new DateTimeOffset(Now.Date.AddDays(-day).AddHours(hour), TimeSpan.Zero), cpu: 20));
            }
        }

        var comparison = UsageComparer.Compare(Recent(cpu: 24), buckets, Now, TimeZoneInfo.Utc);

        Assert.StartsWith("Current activity is close to your usual level", comparison.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DaysFollowTheLocalTimeZone()
    {
        // UTC+10: local midnight of 3 March is 14:00 UTC on 2 March, so 14:30 UTC is "today" and 13:00 UTC "yesterday".
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "Test+10", "Test+10");
        var buckets = new List<SystemUsageAggregate>();
        buckets.AddRange(Minutes(Now.AddMinutes(-30), 30, cpu: 60));
        buckets.AddRange(Minutes(Now.AddHours(-2), 60, cpu: 20));
        buckets.AddRange(Minutes(Now.AddHours(-3), 60, cpu: 20));

        var cpu = UsageComparer.Compare([], buckets, Now.AddMinutes(-0.5), zone).Metrics.Single(m => m.Metric == HistoryMetric.Cpu);

        Assert.Null(cpu.Get(ComparisonPeriod.Today).Average);
        Assert.Equal(0.5, cpu.Get(ComparisonPeriod.Today).MonitoredHours, 0.02);
        Assert.Equal(20, cpu.Get(ComparisonPeriod.Yesterday).Average!.Value, 0.001);
    }

    private static List<MetricSnapshot> Recent(double cpu) =>
        TestData.Series(Now.AddMinutes(-10), 600, _ => cpu);

    private static IEnumerable<SystemUsageAggregate> Minutes(DateTimeOffset from, int count, double cpu) =>
        Enumerable.Range(0, count).Select(i => new SystemUsageAggregate
        {
            Start = from.AddMinutes(i),
            Resolution = HistoryResolution.Minute,
            SampleCount = 60,
            MonitoredSeconds = 60,
            Cpu = new AggregateValue(cpu, cpu + 5, 60),
            Memory = new AggregateValue(40, 45, 60),
        });

    private static SystemUsageAggregate Hourly(DateTimeOffset start, double cpu) => new()
    {
        Start = start,
        Resolution = HistoryResolution.Hour,
        SampleCount = 3600,
        MonitoredSeconds = 3600,
        Cpu = new AggregateValue(cpu, cpu + 20, 3600),
        Memory = new AggregateValue(40, 50, 3600),
    };
}
