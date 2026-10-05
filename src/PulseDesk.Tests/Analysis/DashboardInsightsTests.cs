using PulseDesk.Core.Alerts;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Diagnosis;
using PulseDesk.Core.History;
using PulseDesk.Core.Models;
using PulseDesk.Core.Settings;

namespace PulseDesk.Tests.Analysis;

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

    private static DiagnosisReport Report(IReadOnlyList<MetricSnapshot> recent, UsageBaseline? baseline = null)
    {
        var snapshot = TestData.System(recent[^1].Timestamp, recent[^1].CpuPercent ?? 0);
        var context = new DiagnosisContext(recent[^1].Timestamp, snapshot, recent, baseline ?? UsageBaseline.Empty, new AlertSettings());
        return DiagnosisReportBuilder.Build(context, new DiagnosisEngine().Evaluate(context));
    }
}
