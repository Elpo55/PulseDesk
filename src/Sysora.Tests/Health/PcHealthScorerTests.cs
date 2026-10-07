using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.Health;
using Sysora.Core.History;
using Sysora.Core.Models;

namespace Sysora.Tests.Health;

public sealed class PcHealthScorerTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Fact]
    public void WithoutEnoughMeasurements_NoScoreIsGiven()
    {
        var recent = TestData.Series(T0, 60, _ => 10);

        var report = PcHealthScorer.Compute(Input(recent));

        Assert.Null(report.Score);
        Assert.Equal(PcHealthGrade.Unknown, report.Grade);
        Assert.Contains("(59s so far)", report.Summary, StringComparison.Ordinal);
        Assert.NotEmpty(report.Components);
    }

    [Fact]
    public void QuietPc_Scores100_AndUnmeasurableAreasAreNotCounted()
    {
        var recent = TestData.Series(T0, 900, _ => 10, _ => 40, _ => 5);

        var report = PcHealthScorer.Compute(Input(recent));

        Assert.Equal(100, report.Score);
        Assert.Equal(PcHealthGrade.Good, report.Grade);
        Assert.Equal("PC Health: 100/100 · Good", report.Headline);
        Assert.Equal("Nothing lowers the score right now.", report.Summary);
        var temperatures = Component(report, PcHealthArea.Temperatures);
        Assert.Equal(PcHealthStatus.NotAvailable, temperatures.Status);
        Assert.Equal("Not available", temperatures.StatusText);
        Assert.False(temperatures.IsScored);
        Assert.Equal(PcHealthStatus.NotAvailable, Component(report, PcHealthArea.UsualBehavior).Status);
        Assert.Equal(PcHealthStatus.NotAvailable, Component(report, PcHealthArea.Stability).Status);
        Assert.Contains(report.NotAvailable, n => n.StartsWith("Temperatures:", StringComparison.Ordinal));
    }

    [Fact]
    public void Score_IsAlways100MinusTheShownPenalties()
    {
        var recent = TestData.Series(T0, 900, i => 70 + (i % 20), _ => 88, _ => 60);
        var alerts = new[] { TestData.Alert("cpu.sustained", T0.AddMinutes(5), AlertSeverity.Critical) };

        var report = PcHealthScorer.Compute(Input(recent) with { Alerts = alerts });

        Assert.Equal(100 - report.Components.Where(c => c.IsScored).Sum(c => c.Penalty), report.Score);
        Assert.All(report.Components, c => Assert.InRange(c.Penalty, 0, c.MaxPenalty));
    }

    [Fact]
    public void HighMemory_TakesOffTheDocumentedPoints()
    {
        var recent = TestData.Series(T0, 900, _ => 10, _ => 88);

        var report = PcHealthScorer.Compute(Input(recent));
        var memory = Component(report, PcHealthArea.Memory);

        // (88 − 60) / (95 − 60) × 20 = 16 points.
        Assert.Equal(16, memory.Penalty);
        Assert.Equal(PcHealthStatus.Attention, memory.Status);
        Assert.Equal(84, report.Score);
        Assert.Equal("Lowered by Memory (−16).", report.Summary);
        Assert.StartsWith("Average 88% in use", memory.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortSpike_DoesNotChangeTheScore()
    {
        var recent = TestData.Series(T0, 900, i => i is >= 400 and < 420 ? 100 : 10);

        var report = PcHealthScorer.Compute(Input(recent));

        Assert.Equal(0, Component(report, PcHealthArea.Cpu).Penalty);
        Assert.Equal(PcHealthStatus.Good, Component(report, PcHealthArea.Cpu).Status);
        Assert.Equal(100, report.Score);
    }

    [Fact]
    public void SaturatedCpu_IsAProblem()
    {
        var recent = TestData.Series(T0, 900, _ => 97);

        var cpu = Component(PcHealthScorer.Compute(Input(recent)), PcHealthArea.Cpu);

        Assert.Equal(PcHealthStatus.Problem, cpu.Status);
        Assert.Equal(14, cpu.Penalty);
    }

    [Fact]
    public void MissingDiskActivity_IsNotAvailable_NotZero()
    {
        var recent = TestData.Series(T0, 900, _ => 10, disk: _ => null);
        var snapshot = Snapshot(recent) with { Unavailable = MetricKind.DiskActivity };

        var disk = Component(PcHealthScorer.Compute(Input(recent, snapshot)), PcHealthArea.DiskActivity);

        Assert.Equal(PcHealthStatus.NotAvailable, disk.Status);
        Assert.Equal("Disk activity is not available on this PC", disk.Summary);
        Assert.Equal(0, disk.Penalty);
    }

    [Fact]
    public void AlmostFullWindowsVolume_IsAProblem()
    {
        var recent = TestData.Series(T0, 900, _ => 10);
        var snapshot = Snapshot(recent) with { Storage = [TestData.Volume("C:", 500, 15, system: true), TestData.Volume("D:", 1000, 600)] };

        var storage = Component(PcHealthScorer.Compute(Input(recent, snapshot)), PcHealthArea.Storage);

        Assert.Equal(PcHealthStatus.Problem, storage.Status);
        Assert.Equal(16, storage.Penalty);
        Assert.StartsWith("C: 15 GB free of 500 GB · 1 other volume", storage.Summary, StringComparison.Ordinal);
        Assert.Equal(Sysora.Core.Diagnosis.DiagnosisAction.LargeFiles, storage.Action);
    }

    [Fact]
    public void RecentAlerts_AreCountedWithTheirSeverity()
    {
        var recent = TestData.Series(T0, 900, _ => 10);
        var now = recent[^1].Timestamp;
        var alerts = new[]
        {
            TestData.Alert("disk.busy", now.AddHours(-3), AlertSeverity.Warning, TimeSpan.FromMinutes(5)),
            TestData.Alert("cpu.sustained", now.AddMinutes(-5), AlertSeverity.Critical),
            TestData.Alert("unusual", now.AddHours(-2), AlertSeverity.Info, TimeSpan.FromMinutes(5)),
            TestData.Alert("memory.sustained", now.AddDays(-2), AlertSeverity.Critical, TimeSpan.FromMinutes(5)),
        };

        var anomalies = Component(PcHealthScorer.Compute(Input(recent) with { Alerts = alerts }), PcHealthArea.RecentAnomalies);

        Assert.Equal("3", anomalies.StatusText);
        Assert.Equal(6, anomalies.Penalty);
        Assert.Equal(PcHealthStatus.Problem, anomalies.Status);
        Assert.Equal("3 alerts in the last 24 hours (1 still active)", anomalies.Summary);
    }

    [Fact]
    public void RecurringProblems_LowerStability()
    {
        var recent = TestData.Series(T0, 900, _ => 10);
        var problem = new RecurringProblem
        {
            Key = "memory.sustained",
            Kind = RecurringProblemKind.HighMemory,
            Title = "High memory usage",
            Description = "High memory usage occurred 6 times in the last 7 days.",
            Occurrences = 6,
            Days = 4,
            First = T0.AddDays(-5),
            Last = T0.AddDays(-1),
            Confidence = ConfidenceLevel.High,
        };
        var recurring = new RecurringProblemReport(T0, true, TimeSpan.FromDays(7), [problem], problem.Description);

        var stability = Component(PcHealthScorer.Compute(Input(recent) with { Recurring = recurring }), PcHealthArea.Stability);

        Assert.Equal(PcHealthStatus.Attention, stability.Status);
        Assert.Equal(4, stability.Penalty);
        Assert.Equal("1 recurring problem: High memory usage (6×)", stability.Summary);
    }

    [Fact]
    public void NoRecurringProblem_WithEnoughHistory_IsGood()
    {
        var recent = TestData.Series(T0, 900, _ => 10);
        var recurring = new RecurringProblemReport(T0, true, TimeSpan.FromDays(7), [], "None");

        var stability = Component(PcHealthScorer.Compute(Input(recent) with { Recurring = recurring }), PcHealthArea.Stability);

        Assert.Equal(PcHealthStatus.Good, stability.Status);
        Assert.Equal("Good", stability.StatusText);
    }

    [Fact]
    public void ActivityAboveTheUsualRange_IsReported()
    {
        var recent = TestData.Series(T0, 900, _ => 15, _ => 75);
        var baseline = new UsageBaseline(BaselineStatus.Ready, 3000, T0.AddDays(-7), T0, new Dictionary<HistoryMetric, MetricBaseline>
        {
            [HistoryMetric.Cpu] = new(HistoryMetric.Cpu, 20, 10, 30, 50, 3000),
            [HistoryMetric.Memory] = new(HistoryMetric.Memory, 45, 40, 50, 60, 3000),
        });

        var usual = Component(PcHealthScorer.Compute(Input(recent) with { Baseline = baseline }), PcHealthArea.UsualBehavior);

        Assert.Equal(PcHealthStatus.Attention, usual.Status);
        Assert.Equal(3, usual.Penalty);
        Assert.StartsWith("Above your usual range: Memory (75%", usual.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NearlyFullVideoMemory_NeedsAttention_ButGpuUsageAloneDoesNot()
    {
        var recent = TestData.Series(T0, 900, _ => 10);
        var busy = Snapshot(recent) with
        {
            Gpus = [new GpuMetrics("1", "Test GPU") { UsagePercent = 99, DedicatedMemoryUsedBytes = 7_900_000_000, DedicatedMemoryTotalBytes = 8_000_000_000 }],
        };
        var calm = Snapshot(recent) with
        {
            Gpus = [new GpuMetrics("1", "Test GPU") { UsagePercent = 99, DedicatedMemoryUsedBytes = 2_000_000_000, DedicatedMemoryTotalBytes = 8_000_000_000 }],
        };

        Assert.Equal(5, Component(PcHealthScorer.Compute(Input(recent, busy)), PcHealthArea.Gpu).Penalty);
        Assert.Equal(0, Component(PcHealthScorer.Compute(Input(recent, calm)), PcHealthArea.Gpu).Penalty);
        Assert.Equal(PcHealthStatus.NotAvailable, Component(PcHealthScorer.Compute(Input(recent, calm) with { GpuMonitoringEnabled = false }), PcHealthArea.Gpu).Status);
    }

    [Fact]
    public void ReportedTemperatures_AreScored()
    {
        var recent = TestData.Series(T0, 900, _ => 10);
        var snapshot = Snapshot(recent) with { Cpu = new CpuMetrics(10, 91, null, 8) };

        var temperatures = Component(PcHealthScorer.Compute(Input(recent, snapshot)), PcHealthArea.Temperatures);

        Assert.Equal(PcHealthStatus.Attention, temperatures.Status);
        Assert.Equal(5, temperatures.Penalty);
    }

    [Fact]
    public void Scale_IsLinearBetweenItsBounds()
    {
        Assert.Equal(0, PcHealthScorer.Scale(40, 50, 100, 15));
        Assert.Equal(8, PcHealthScorer.Scale(75, 50, 100, 15));
        Assert.Equal(15, PcHealthScorer.Scale(120, 50, 100, 15));
    }

    private static PcHealthComponent Component(PcHealthReport report, PcHealthArea area) => report.Components.Single(c => c.Area == area);

    private static SystemSnapshot Snapshot(IReadOnlyList<MetricSnapshot> recent) =>
        TestData.System(recent[^1].Timestamp, recent[^1].CpuPercent ?? 0) with { Storage = [TestData.Volume("C:", 500, 300, system: true)] };

    private static PcHealthInput Input(IReadOnlyList<MetricSnapshot> recent, SystemSnapshot? snapshot = null) => new()
    {
        Now = recent[^1].Timestamp,
        Snapshot = snapshot ?? Snapshot(recent),
        Recent = recent,
    };
}
