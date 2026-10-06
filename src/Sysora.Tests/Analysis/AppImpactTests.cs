using Sysora.Core.Analysis;
using Sysora.Core.Metrics;
using Sysora.Core.Models;

namespace Sysora.Tests.Analysis;

public sealed class AppImpactTests
{
    private const ulong Gigabyte = 1024UL * 1024 * 1024;
    private static readonly DateTimeOffset T0 = TestData.Start;
    private readonly AppImpactAnalyzer _analyzer = new();

    [Fact]
    public void Score_FollowsTheDocumentedFormula()
    {
        // 8% CPU and 2.4 GB (15% of 16 GB) while running, running 90% of the time:
        // CPU load 7.2% → 7.2/15 = 0.48; memory load 13.5% → 13.5/25 = 0.54; no I/O.
        var usage = Usage(cpu: 8, memoryBytes: 0.15 * 16 * Gigabyte, active: 0.9 * 3600, monitored: 3600);

        var score = AppImpactAnalyzer.Score(usage, 16 * Gigabyte);

        Assert.Equal((int)Math.Round(100 * ((0.45 * 0.48) + (0.40 * 0.54))), score.Value);
        Assert.Equal(43, score.Value);
        Assert.Equal(ImpactLevel.High, score.Level);
        Assert.Equal(ImpactLevel.High, score.Components.Single(c => c.Resource == "CPU").Level);
        Assert.Equal(ImpactLevel.High, score.Components.Single(c => c.Resource == "Memory").Level);
        Assert.Equal(ImpactLevel.Low, score.Components.Single(c => c.Resource == "Disk I/O").Level);
    }

    [Fact]
    public void ShortIntenseProcess_HasLowImpact_AndMentionsItsPeaks()
    {
        // 70% CPU for 2 minutes out of 4 hours.
        var usage = Usage(cpu: 70, memoryBytes: 200 * 1024 * 1024, active: 120, monitored: 4 * 3600, cpuMax: 95);

        var result = _analyzer.Analyze(usage, Context());

        Assert.Equal(ImpactLevel.Low, result.Score.Level);
        Assert.Contains("Low resource usage", result.Explanation, StringComparison.Ordinal);
        Assert.Contains("peaks up to 95%", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void LongModerateProcess_OutranksShortIntenseOne()
    {
        var shortIntense = Usage(cpu: 70, memoryBytes: 200 * 1024 * 1024, active: 120, monitored: 4 * 3600, name: "setup.exe");
        var longModerate = Usage(cpu: 6, memoryBytes: 3 * Gigabyte, active: 4 * 3600, monitored: 4 * 3600, name: "browser.exe");

        var ranked = _analyzer.Rank([shortIntense, longModerate], Context());

        Assert.Equal("browser.exe", ranked[0].Usage.Identity.Name);
        Assert.True(ranked[0].Score.Level >= ImpactLevel.High);
        Assert.Contains("for most of the monitored time", ranked[0].Explanation, StringComparison.Ordinal);
        Assert.Equal(ConfidenceLevel.High, ranked[0].Confidence);
    }

    [Fact]
    public void Evidence_ExplainsTheDataUsed_AndReportsNetworkAsNotAvailable()
    {
        var result = _analyzer.Analyze(Usage(cpu: 5, memoryBytes: Gigabyte, active: 600, monitored: 1200), Context());

        Assert.Contains(result.Evidence, e => e.Metric == "Network" && e.Observed == "Not available");
        Assert.Contains(result.Evidence, e => e.Metric == "Score" && e.Reference == AppImpactScore.Formula);
        Assert.Contains(result.Evidence, e => e.Metric == "Identification");
        Assert.Equal(ConfidenceLevel.Medium, result.Confidence);
    }

    [Fact]
    public void Trend_ComparesBothHalvesOfThePeriod()
    {
        var usage = Usage(cpu: 5, memoryBytes: Gigabyte, active: 3600, monitored: 3600) with
        {
            FirstHalf = new UsageLevel(5, 1.0 * Gigabyte, 1800),
            SecondHalf = new UsageLevel(5.2, 1.6 * Gigabyte, 1800),
        };

        var trend = AppImpactAnalyzer.Trend(usage);

        Assert.Equal(TrendDirection.Rising, trend.Memory);
        Assert.Equal(TrendDirection.Stable, trend.Cpu);
        Assert.Equal(60, trend.MemoryChangePercent!.Value, 3);
    }

    [Fact]
    public void ProcessHistory_AggregatesCpuAndMemoryAcrossProcessesOfAnApplication()
    {
        var history = new ProcessHistory();
        for (var i = 0; i <= 10; i++)
        {
            history.Record(new ProcessSnapshot(
            [
                TestData.Process(1, "browser.exe", 10, 500, path: @"C:\Browser\browser.exe"),
                TestData.Process(2, "browser.exe", i < 5 ? 2 : 6, 300, path: @"C:\Browser\browser.exe"),
            ]), T0.AddSeconds(2 * i));
        }

        var browser = Assert.Single(history.GetSessionStatistics());

        Assert.Equal(20, history.MonitoredSeconds, 6);
        Assert.Equal(20, browser.ActiveSeconds, 6);
        Assert.Equal(2, browser.InstanceCount);
        Assert.True(browser.IsRunning);
        Assert.True(browser.Identity.IsIdentifiedByPath);
        // Time-weighted: samples 1–4 at 12%, samples 5–10 at 16% (the first sample has no interval).
        Assert.Equal(((4 * 12) + (6 * 16)) / 10.0, browser.CpuAverage, 6);
        Assert.Equal(16, browser.CpuMaximum, 6);
        Assert.Equal(800.0 * 1024 * 1024, browser.MemoryAverageBytes, 0);
    }

    [Fact]
    public void ProcessHistory_CountsOnlyRunningTime_ForAppsThatComeAndGo()
    {
        var history = new ProcessHistory();
        for (var i = 0; i <= 30; i++)
        {
            var processes = new List<ProcessMetrics> { TestData.Process(1, "explorer.exe", 1, 100) };
            if (i is >= 10 and < 15)
            {
                processes.Add(TestData.Process(50, "tool.exe", 40, 50, created: 900));
            }

            history.Record(new ProcessSnapshot(processes), T0.AddSeconds(2 * i));
        }

        var tool = history.GetSessionStatistics().Single(a => a.Identity.Name == "tool.exe");

        // Each sample in which the application is present counts the interval since the previous sample.
        Assert.Equal(10, tool.ActiveSeconds, 6);
        Assert.Equal(60, tool.MonitoredSeconds, 6);
        Assert.Equal(1, tool.Launches);
        Assert.False(tool.IsRunning);
        Assert.Equal(T0.AddSeconds(28), tool.LastSeen);
    }

    [Fact]
    public void ProcessHistory_ProcessesRunningAtStartup_AreNotCountedAsLaunches()
    {
        var history = new ProcessHistory();
        history.Record(new ProcessSnapshot([TestData.Process(1, "a.exe", 1, 10)]), T0);
        history.Record(new ProcessSnapshot([TestData.Process(1, "a.exe", 1, 10), TestData.Process(2, "a.exe", 1, 10, created: 5)]), T0.AddSeconds(2));

        Assert.Equal(1, history.GetSessionStatistics().Single().Launches);
    }

    [Fact]
    public void ProcessHistory_KeepsSameNameDifferentPathApart()
    {
        var history = new ProcessHistory();
        history.Record(new ProcessSnapshot(
        [
            TestData.Process(1, "Update.exe", 1, 10, path: @"C:\A\Update.exe"),
            TestData.Process(2, "Update.exe", 1, 10, path: @"C:\B\Update.exe"),
            TestData.Process(3, "svchost.exe", 1, 10),
            TestData.Process(4, "svchost.exe", 1, 10),
        ]), T0);

        var stats = history.GetSessionStatistics();

        Assert.Equal(2, stats.Count(s => s.Identity.Name == "Update.exe"));
        var svchost = Assert.Single(stats, s => s.Identity.Name == "svchost.exe");
        Assert.False(svchost.Identity.IsIdentifiedByPath);
        Assert.Equal(2, svchost.InstanceCount);
    }

    [Fact]
    public void ProcessHistory_CompletesFiveMinuteBuckets()
    {
        var history = new ProcessHistory();
        var buckets = new List<Sysora.Core.History.AppUsageBucket>();
        history.BucketCompleted += (_, b) => buckets.Add(b);

        for (var i = 0; i < 200; i++)
        {
            history.Record(new ProcessSnapshot([TestData.Process(1, "a.exe", 3, 100)]), T0.AddSeconds(2 * i));
        }

        var bucket = Assert.Single(buckets);
        Assert.Equal(T0, bucket.Start);
        Assert.Equal(150, bucket.SampleCount);
        Assert.Equal(298, bucket.MonitoredSeconds, 6);
        var app = Assert.Single(bucket.Apps);
        Assert.Equal(3, app.CpuAverage, 6);
        Assert.Equal(T0.AddMinutes(5), history.FlushBucket()!.Start);
    }

    private static AppImpactContext Context() => new(16 * Gigabyte, T0, T0.AddHours(4));

    private static AppUsageStatistics Usage(double cpu, double memoryBytes, double active, double monitored, double? cpuMax = null, string name = "app.exe") => new()
    {
        Identity = AppIdentity.Create(name, $@"C:\Apps\{name}"),
        FirstSeen = T0,
        LastSeen = T0.AddSeconds(active),
        ActiveSeconds = active,
        MonitoredSeconds = monitored,
        Samples = (int)Math.Max(1, active / 2),
        CpuAverage = cpu,
        CpuMaximum = cpuMax ?? cpu,
        MemoryAverageBytes = memoryBytes,
        MemoryMaximumBytes = memoryBytes,
    };
}
