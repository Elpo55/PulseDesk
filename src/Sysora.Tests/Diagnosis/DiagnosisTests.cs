using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.History;
using Sysora.Core.Models;
using Sysora.Core.Settings;

namespace Sysora.Tests.Diagnosis;

public sealed class DiagnosisTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;
    private readonly DiagnosisEngine _engine = new();

    [Fact]
    public void NormalActivity_GivesHealthyReport_WithNormalResults()
    {
        var report = Diagnose(TestData.Series(T0, 600, _ => 20, _ => 45, _ => 4));

        Assert.Equal(PcHealthState.Healthy, report.State);
        Assert.Equal("No problem detected", report.Headline);
        Assert.Empty(report.Problems);
        Assert.Contains(report.Normal, r => r.RuleId == "cpu.load" && r.Title == "CPU usage normal");
        Assert.Contains(report.Normal, r => r.RuleId == "memory.pressure");
        Assert.Contains(report.Normal, r => r.RuleId == "disk.activity");
        Assert.Equal(600, report.SampleCount);
    }

    [Fact]
    public void SustainedHighCpu_IsAWarning_WithDurationAndEvidence()
    {
        // 9 minutes at 30%, then 3m 20s at 90%.
        var snapshots = TestData.Series(T0, 740, i => i < 540 ? 30 : 90);

        var report = Diagnose(snapshots);

        var cpu = Assert.Single(report.Problems, r => r.RuleId == "cpu.load");
        Assert.Equal(DiagnosisSeverity.Warning, cpu.Severity);
        Assert.Equal("High CPU usage", cpu.Title);
        Assert.Equal(TimeSpan.FromSeconds(199), cpu.Duration);
        Assert.Equal(ConfidenceLevel.High, cpu.Confidence);
        Assert.Contains("for 3m 19s", cpu.Description, StringComparison.Ordinal);
        Assert.Equal("Threshold 80%", cpu.ReferenceValue);
        Assert.Contains(cpu.Evidence, e => e.Metric == "CPU usage" && e.SampleCount == 200);
        Assert.Equal(DiagnosisAction.AppImpact, cpu.Action);
        Assert.Equal(PcHealthState.Attention, report.State);
        Assert.StartsWith("Most likely cause: CPU usage has stayed above 80%", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SaturatedCpu_IsCritical()
    {
        var report = Diagnose(TestData.Series(T0, 120, _ => 99));

        Assert.Equal(DiagnosisSeverity.Critical, report.Results.Single(r => r.RuleId == "cpu.load").Severity);
        Assert.Equal(PcHealthState.Problem, report.State);
    }

    [Fact]
    public void ShortCpuSpike_IsOnlyAPointToWatch()
    {
        var report = Diagnose(TestData.Series(T0, 300, i => i < 292 ? 20 : 97));

        var cpu = report.Results.Single(r => r.RuleId == "cpu.load");
        Assert.Equal(DiagnosisSeverity.Info, cpu.Severity);
        Assert.Equal("Short CPU spike", cpu.Title);
        Assert.Equal(ConfidenceLevel.Low, cpu.Confidence);
        Assert.Equal(PcHealthState.Healthy, report.State);
    }

    [Fact]
    public void BriefDipsDoNotResetASustainedPeriod()
    {
        // 90% for 2 minutes with a 2-second dip every 20 seconds.
        var report = Diagnose(TestData.Series(T0, 120, i => i % 20 is 10 or 11 ? 60 : 90));

        var cpu = report.Results.Single(r => r.RuleId == "cpu.load");
        Assert.Equal(DiagnosisSeverity.Warning, cpu.Severity);
        Assert.Equal(TimeSpan.FromSeconds(119), cpu.Duration);
    }

    [Fact]
    public void SustainedHighMemory_IsAWarning_NamingTheLargestApplication()
    {
        var apps = (IReadOnlyList<AppSample>)[TestData.App("browser.exe", 3, 6000), TestData.App("chat.exe", 1, 800)];
        var report = Diagnose(TestData.Series(T0, 180, _ => 20, _ => 91, apps: _ => apps));

        var memory = Assert.Single(report.Problems, r => r.RuleId == "memory.pressure");
        Assert.Equal(DiagnosisSeverity.Warning, memory.Severity);
        Assert.Equal("Available memory is low", memory.Title);
        Assert.Contains("browser.exe", memory.Recommendation, StringComparison.Ordinal);
        Assert.Equal("name:BROWSER.EXE", memory.AppKey);
    }

    [Fact]
    public void ApplicationBusyForMinutes_IsReported_WithTheApplication()
    {
        var busy = (IReadOnlyList<AppSample>)[TestData.App("render.exe", 70, 900)];
        var report = Diagnose(TestData.Series(T0, 300, _ => 85, apps: i => i % 2 == 0 ? busy : [.. busy])); // a new list = a new process sample

        var app = Assert.Single(report.Problems, r => r.RuleId == "apps.cpu");
        Assert.Equal("render.exe has been using a lot of CPU for several minutes", app.Title);
        Assert.Equal("name:RENDER.EXE", app.AppKey);
    }

    [Fact]
    public void SteadyMemoryGrowth_IsAPointToWatch_NamingTheGrowingApplication()
    {
        var report = Diagnose(TestData.Series(
            T0,
            1200,
            _ => 15,
            i => 40 + (i / 60.0),
            apps: i => [TestData.App("leaky.exe", 1, 500 + (i * 2)), TestData.App("stable.exe", 1, 700)]));

        var growth = Assert.Single(report.Results, r => r.RuleId == "memory.growth");
        Assert.Equal(DiagnosisSeverity.Info, growth.Severity);
        Assert.Contains("leaky.exe grew by", growth.Description, StringComparison.Ordinal);
        Assert.Equal(ConfidenceLevel.Medium, growth.Confidence);
    }

    [Fact]
    public void ProlongedAnomaly_ComparedWithBaseline_IsUnusual()
    {
        var baseline = Baseline(cpuMedian: 30);
        var report = Diagnose(TestData.Series(T0, 900, _ => 75, _ => 45, _ => 4), baseline);

        var unusual = report.Results.Single(r => r.RuleId == "activity.unusual");
        Assert.Equal(DiagnosisSeverity.Info, unusual.Severity);
        Assert.Contains("CPU 75% (usually", unusual.Description, StringComparison.Ordinal);
        Assert.Equal(DiagnosisAction.Replay, unusual.Action);
        Assert.StartsWith("Usual behavior learned", report.BaselineDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutBaseline_NothingIsCalledUnusual_AndTheReportSaysSo()
    {
        var report = Diagnose(TestData.Series(T0, 900, _ => 75));

        Assert.DoesNotContain(report.Results, r => r.RuleId == "activity.unusual");
        Assert.StartsWith("Collecting baseline data", report.BaselineDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableMetrics_AreListedAsNotAnalyzed_NotGuessed()
    {
        var report = Diagnose(TestData.Series(T0, 60, _ => 10, disk: _ => null));

        Assert.DoesNotContain(report.Results, r => r.RuleId == "disk.activity");
        Assert.Contains(report.NotAnalyzed, n => n.StartsWith("Disk activity", StringComparison.Ordinal));
        Assert.Contains(report.NotAnalyzed, n => n.StartsWith("CPU temperature", StringComparison.Ordinal));
    }

    [Fact]
    public void NearlyFullDataVolume_IsAPointToWatch_NotAProblem()
    {
        var snapshots = TestData.Series(T0, 60, _ => 10);
        var snapshot = TestData.System(snapshots[^1].Timestamp, 10) with
        {
            Storage =
            [
                StorageMetrics.FromTotalAndFree(@"C:\", 1000UL << 30, 600UL << 30) with { Kind = DriveKind.Fixed, IsSystemDrive = true },
                StorageMetrics.FromTotalAndFree(@"D:\", 2000UL << 30, 260UL << 30) with { Kind = DriveKind.Fixed },
            ],
        };
        var context = new DiagnosisContext(snapshot.Timestamp, snapshot, snapshots, UsageBaseline.Empty, new AlertSettings());

        var report = DiagnosisReportBuilder.Build(context, _engine.Evaluate(context));

        Assert.Equal(DiagnosisSeverity.Normal, report.Results.Single(r => r.RuleId == "storage.space").Severity);
        var data = report.Results.Single(r => r.RuleId == "storage.space.D:");
        Assert.Equal(DiagnosisSeverity.Info, data.Severity);
        Assert.Equal("Volume D: is 87% full", data.Title);
        Assert.Equal(PcHealthState.Healthy, report.State);
        Assert.Equal("No problem detected · 1 point to watch", report.Headline);
    }

    [Fact]
    public void NoData_GivesUnknownState()
    {
        var report = Diagnose([]);

        Assert.Equal(PcHealthState.Unknown, report.State);
        Assert.Empty(report.Results);
    }

    [Fact]
    public void Baseline_RequiresFourHoursOfHistory()
    {
        var now = T0.AddDays(1);
        var minutes = Enumerable.Range(0, 200).Select(i => Minute(now.AddHours(-5).AddMinutes(i), 30)).ToList();

        var collecting = BaselineCalculator.Compute(minutes, now);
        Assert.Equal(BaselineStatus.Collecting, collecting.Status);
        Assert.Null(collecting.Get(HistoryMetric.Cpu));

        minutes.AddRange(Enumerable.Range(200, 100).Select(i => Minute(now.AddHours(-5).AddMinutes(i), 50)));
        var ready = BaselineCalculator.Compute(minutes, now);
        Assert.Equal(BaselineStatus.Ready, ready.Status);
        var cpu = ready.Get(HistoryMetric.Cpu)!;
        Assert.Equal(30, cpu.Median);
        Assert.Equal(50, cpu.P95);
        Assert.Equal(285, cpu.Minutes); // the last 15 minutes are left out
    }

    [Fact]
    public void Baseline_IgnoresTheLastMinutes()
    {
        var now = T0.AddDays(1);
        var minutes = Enumerable.Range(0, 300).Select(i => Minute(now.AddMinutes(-300 + i), 30)).ToList();

        var baseline = BaselineCalculator.Compute(minutes, now);

        Assert.Equal(285, baseline.MinutesOfData);
    }

    [Fact]
    public void Percentile_InterpolatesLinearly()
    {
        Assert.Equal(25, BaselineCalculator.Percentile([0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100], 0.25));
        Assert.Equal(15, BaselineCalculator.Percentile([10, 20], 0.5));
    }

    private DiagnosisReport Diagnose(IReadOnlyList<MetricSnapshot> snapshots, UsageBaseline? baseline = null)
    {
        var last = snapshots.Count > 0 ? snapshots[^1].Timestamp : T0;
        var snapshot = TestData.System(last, snapshots.Count > 0 ? snapshots[^1].CpuPercent ?? 0 : 0);
        var context = new DiagnosisContext(last, snapshot, snapshots, baseline ?? UsageBaseline.Empty, new AlertSettings());
        return DiagnosisReportBuilder.Build(context, _engine.Evaluate(context));
    }

    private static UsageBaseline Baseline(double cpuMedian) =>
        new(BaselineStatus.Ready, 3000, T0.AddDays(-7), T0, new Dictionary<HistoryMetric, MetricBaseline>
        {
            [HistoryMetric.Cpu] = new(HistoryMetric.Cpu, cpuMedian, cpuMedian - 10, cpuMedian + 10, cpuMedian + 20, 3000),
            [HistoryMetric.Memory] = new(HistoryMetric.Memory, 45, 40, 50, 60, 3000),
            [HistoryMetric.Disk] = new(HistoryMetric.Disk, 5, 2, 8, 20, 3000),
        });

    private static SystemUsageAggregate Minute(DateTimeOffset start, double cpu) => new()
    {
        Start = start,
        Resolution = HistoryResolution.Minute,
        SampleCount = 60,
        Cpu = new AggregateValue(cpu, cpu, 60),
        Memory = new AggregateValue(40, 40, 60),
    };
}
