using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.History;
using Sysora.Core.Models;
using Sysora.Core.Settings;

namespace Sysora.Tests.Analysis;

public sealed class DashboardInsightsTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Fact]
    public void QuietPc_SaysNoSignificantIssue_AndThatTheBaselineIsBeingCollected()
    {
        var recent = TestData.Series(T0, 120, _ => 10);
        var insights = DashboardInsights.Build(Report(recent), [], recent, UsageBaseline.Empty);

        Assert.Equal("No significant issue detected", insights[0].Text);
        var note = Assert.Single(insights, i => i.IsNote);
        Assert.StartsWith("Collecting baseline data", note.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void GameRunning_IsMentionedWithALinkToGaming()
    {
        var recent = TestData.Series(T0, 120, _ => 10);
        var game = new Sysora.Core.Gaming.LiveGameSession("path:GAME", "Space Game", @"D:\Games\Space\space.exe", T0, T0.AddSeconds(20), "Windows recognizes it.");

        var insights = DashboardInsights.Build(Report(recent), [], recent, UsageBaseline.Empty, [game]);

        var insight = Assert.Single(insights, i => i.Action == DiagnosisAction.Gaming);
        Assert.Equal("Game running: Space Game (just started). A recap appears when it closes.", insight.Text);
    }

    [Fact]
    public void RecentRecap_IsMentionedForTwoHours()
    {
        var recent = TestData.Series(T0, 120, _ => 10);
        var session = Gaming.GameRecapBuilderTests.Session(start: T0.AddMinutes(-90)) with { End = T0.AddMinutes(-30) };
        var recap = Sysora.Core.Gaming.GameRecapBuilder.Build(session, []);

        var soon = DashboardInsights.Build(Report(recent), [], recent, UsageBaseline.Empty, lastGame: recap);
        var later = DashboardInsights.Build(Report(recent) with { Timestamp = T0.AddHours(3) }, [], recent, UsageBaseline.Empty, lastGame: recap);

        Assert.Contains(soon, i => i.Text == "Last game: Space Game (1h 00m) · No problem observed");
        Assert.DoesNotContain(later, i => i.Action == DiagnosisAction.Gaming);
    }

    [Fact]
    public void Problems_ComeFirst_AndLargestConsumerIsNamed()
    {
        var apps = (IReadOnlyList<AppSample>)[TestData.App("browser.exe", 34, 2100)];
        var recent = TestData.Series(T0, 300, _ => 92, apps: _ => apps);

        var insights = DashboardInsights.Build(Report(recent), [], recent, UsageBaseline.Empty);

        Assert.Equal("High CPU usage", insights[0].Text);
        Assert.Equal(DiagnosisSeverity.Warning, insights[0].Severity);
        Assert.DoesNotContain(insights, i => i.Text == "No significant issue detected");
        Assert.Contains(insights, i => i.Text.StartsWith("browser.exe is currently the largest resource consumer (34% CPU", StringComparison.Ordinal));
    }

    [Fact]
    public void UsageAboveTheUsualLevel_IsQuantified()
    {
        var recent = TestData.Series(T0, 600, _ => 20, _ => 63);
        var baseline = new UsageBaseline(BaselineStatus.Ready, 2000, T0.AddDays(-7), T0, new Dictionary<HistoryMetric, MetricBaseline>
        {
            [HistoryMetric.Cpu] = new(HistoryMetric.Cpu, 22, 15, 30, 45, 2000),
            [HistoryMetric.Memory] = new(HistoryMetric.Memory, 45, 40, 50, 70, 2000),
        });

        var insights = DashboardInsights.Build(Report(recent, baseline), [], recent, baseline);

        Assert.Contains(insights, i => i.Text.StartsWith("Memory usage is 18 points above your usual level", StringComparison.Ordinal));
        Assert.DoesNotContain(insights, i => i.IsNote);
    }

    [Fact]
    public void ActiveCriticalAlert_MakesTheStateAProblem()
    {
        var recent = TestData.Series(T0, 60, _ => 10);
        var report = Report(recent);
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            RuleId = "cpu.sustained",
            Key = "cpu.sustained",
            Title = "CPU usage unusually high",
            Severity = AlertSeverity.Critical,
            Status = AlertStatus.New,
            RaisedAt = T0,
            UpdatedAt = T0,
            Metric = "CPU usage",
            Value = "95%",
            Context = string.Empty,
            Explanation = string.Empty,
        };

        Assert.Equal(PcHealthState.Healthy, DashboardInsights.State(report, []));
        Assert.Equal(PcHealthState.Problem, DashboardInsights.State(report, [alert]));
        Assert.Equal(PcHealthState.Healthy, DashboardInsights.State(report, [alert with { Status = AlertStatus.Resolved }]));
        var insight = Assert.Single(DashboardInsights.Build(report, [alert], recent, UsageBaseline.Empty), i => i.Action == DiagnosisAction.Alerts);
        Assert.Equal("Active alert: CPU usage unusually high", insight.Text);
    }

    [Fact]
    public void RepeatedAlerts_AreCountedOverTheLast24Hours()
    {
        var recent = TestData.Series(T0, 60, _ => 10);
        var now = recent[^1].Timestamp;
        var alerts = new[]
        {
            TestData.Alert("disk.busy", now.AddHours(-20), lasted: TimeSpan.FromMinutes(4), title: "Disk C: busy for 4m"),
            TestData.Alert("disk.busy", now.AddHours(-6), lasted: TimeSpan.FromMinutes(3), title: "Disk C: busy for 3m"),
            TestData.Alert("disk.busy", now.AddHours(-2), lasted: TimeSpan.FromMinutes(5), title: "Disk C: busy for 5m"),
            TestData.Alert("disk.busy", now.AddHours(-30), lasted: TimeSpan.FromMinutes(5)),
        };

        var insight = Assert.Single(DashboardInsights.Build(Report(recent), alerts, recent, UsageBaseline.Empty), i => i.Text.StartsWith("This is the", StringComparison.Ordinal));

        Assert.Equal("This is the third busy disk alert in the last 24 hours (latest: Disk C: busy for 5m).", insight.Text);
        Assert.Equal(DiagnosisAction.Alerts, insight.Action);
    }

    [Fact]
    public void RecentlyResolvedWarning_IsReportedAsARecovery_UnlessItCameBack()
    {
        var recent = TestData.Series(T0, 60, _ => 10);
        var now = recent[^1].Timestamp;
        var resolved = TestData.Alert("cpu.sustained", now.AddMinutes(-20), lasted: TimeSpan.FromMinutes(8), title: "Sustained high CPU usage");

        var recovered = DashboardInsights.Build(Report(recent), [resolved], recent, UsageBaseline.Empty);
        var cameBack = DashboardInsights.Build(Report(recent), [resolved, TestData.Alert("cpu.sustained", now.AddMinutes(-1))], recent, UsageBaseline.Empty);

        Assert.Contains(recovered, i => i.Text.StartsWith("Your PC recovered after the last problem (Sustained high CPU usage)", StringComparison.Ordinal));
        Assert.DoesNotContain(cameBack, i => i.Text.StartsWith("Your PC recovered", StringComparison.Ordinal));
    }

    [Fact]
    public void RecurringProblem_IsMentionedWithItsTimePattern()
    {
        var recent = TestData.Series(T0, 60, _ => 10);
        var problem = new RecurringProblem
        {
            Key = "memory.sustained",
            Kind = RecurringProblemKind.HighMemory,
            Title = "High memory usage",
            Description = "High memory usage occurred 6 times in the last 7 days.",
            Occurrences = 6,
            Days = 5,
            First = T0.AddDays(-6),
            Last = T0.AddDays(-1),
            TimePattern = "Most events happened between 19:00 and 22:00 (5 of 6)",
            Confidence = ConfidenceLevel.High,
        };
        var recurring = new RecurringProblemReport(T0, true, TimeSpan.FromDays(7), [problem], problem.Description);

        var insights = DashboardInsights.Build(Report(recent), [], recent, UsageBaseline.Empty, recurring: recurring);

        Assert.Contains(insights, i => i.Text == "Recurring: High memory usage occurred 6 times in the last 7 days. Most events happened between 19:00 and 22:00 (5 of 6)." && i.Action == DiagnosisAction.PcHealth);
    }

    [Fact]
    public void QuietPc_WithinItsUsualRange_SaysSo()
    {
        var recent = TestData.Series(T0, 600, _ => 20, _ => 45);
        var baseline = new UsageBaseline(BaselineStatus.Ready, 2000, T0.AddDays(-7), T0, new Dictionary<HistoryMetric, MetricBaseline>
        {
            [HistoryMetric.Cpu] = new(HistoryMetric.Cpu, 22, 15, 30, 45, 2000),
            [HistoryMetric.Memory] = new(HistoryMetric.Memory, 45, 40, 50, 70, 2000),
        });

        var insights = DashboardInsights.Build(Report(recent, baseline), [], recent, baseline);

        Assert.Equal("No significant issue detected", insights[0].Text);
        Assert.Equal("CPU and memory usage are within your usual range for this PC.", insights[1].Text);
    }

    private static DiagnosisReport Report(IReadOnlyList<MetricSnapshot> recent, UsageBaseline? baseline = null)
    {
        var snapshot = TestData.System(recent[^1].Timestamp, recent[^1].CpuPercent ?? 0);
        var context = new DiagnosisContext(recent[^1].Timestamp, snapshot, recent, baseline ?? UsageBaseline.Empty, new AlertSettings());
        return DiagnosisReportBuilder.Build(context, new DiagnosisEngine().Evaluate(context));
    }
}
