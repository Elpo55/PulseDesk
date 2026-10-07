using System.Text.Json;
using Sysora.Core.Analysis;
using Sysora.Core.Changes;
using Sysora.Core.Diagnosis;
using Sysora.Core.Health;
using Sysora.Core.History;
using Sysora.Core.Models;
using Sysora.Core.Reports;
using Sysora.Core.Settings;
using Sysora.Core.Storage;
using Sysora.Core.Timeline;
using Sysora.Core.Troubleshooting;

namespace Sysora.Tests.Reports;

public sealed class ReportTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;

    private static readonly ReportSystemInfo System = new("Windows 11 Pro 24H2 (build 26100.1, X64)", "Test CPU", "16 GB", ["Test GPU"]);

    [Fact]
    public void Html_IsSelfContained_AndEscapesEverything()
    {
        var report = new ReportDocument
        {
            Kind = ReportKind.Alerts,
            Title = "Alerts <script>alert(1)</script>",
            GeneratedAt = T0,
            Summary = "A & B",
            Sections = [new ReportSection("S") { Findings = [new Finding("Likely", "<b>x</b>", FindingBasis.Inferred) { Confidence = ConfidenceLevel.Medium }] }],
            MissingData = ["GPU <none>"],
        };

        var html = ReportWriter.ToHtml(report);

        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("Alerts &lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("A &amp; B", html, StringComparison.Ordinal);
        Assert.Contains("Inferred · medium confidence", html, StringComparison.Ordinal);
        Assert.Contains("GPU &lt;none&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_IsStructured_WithTypedValuesAndTheUnderlyingData()
    {
        var recent = TestData.Series(T0, 900, _ => 10, _ => 88);
        var health = PcHealthScorer.Compute(new PcHealthInput { Now = recent[^1].Timestamp, Snapshot = TestData.System(recent[^1].Timestamp, 10), Recent = recent });

        var json = ReportWriter.ToJson(ReportBuilder.PcHealth(health, null, T0, System));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("sysora.report/1", root.GetProperty("schema").GetString());
        Assert.Equal("PcHealth", root.GetProperty("kind").GetString());
        Assert.Equal("Test CPU", root.GetProperty("system").GetProperty("processor").GetString());
        Assert.Equal(84, root.GetProperty("data").GetProperty("score").GetInt32());
        Assert.Equal("Fair", root.GetProperty("data").GetProperty("grade").GetString());
        var memory = root.GetProperty("data").GetProperty("components").EnumerateArray().Single(c => c.GetProperty("area").GetString() == "Memory");
        Assert.Equal(16, memory.GetProperty("penalty").GetInt32());
        Assert.Contains(root.GetProperty("missingData").EnumerateArray(), m => m.GetString()!.StartsWith("Temperatures:", StringComparison.Ordinal));
    }

    [Fact]
    public void FileName_SaysWhatAndWhen()
    {
        var report = new ReportDocument { Kind = ReportKind.Diagnosis, Title = "Diagnosis", GeneratedAt = new DateTimeOffset(2026, 10, 7, 17, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 7))), Summary = string.Empty };

        Assert.Equal("Sysora-Diagnosis-2026-10-07-1730.html", ReportWriter.FileName(report, ReportFormat.Html));
        Assert.Equal("Sysora-Diagnosis-2026-10-07-1730.json", ReportWriter.FileName(report, ReportFormat.Json));
    }

    [Fact]
    public void EveryFunction_ProducesAReadableAndAStructuredReport()
    {
        var recent = TestData.Series(T0, 600, i => i < 300 ? 10 : 85, apps: _ => [TestData.App("app.exe", 30, 500)]);
        var now = recent[^1].Timestamp;
        var context = new DiagnosisContext(now, TestData.System(now, 85), recent, UsageBaseline.Empty, new AlertSettings());
        var diagnosis = DiagnosisReportBuilder.Build(context, new DiagnosisEngine().Evaluate(context));
        var why = WhyNowAnalyzer.Explain(new WhyNowInput { Metric = WhyNowMetric.Cpu, Points = recent, Alerts = [] });
        var replay = new ReplayData(T0, now, true, recent, [new SystemEvent(T0, SystemEventKind.MonitoringStarted, "Monitoring started")], ReplayNarrator.Narrate(recent, []), "test");
        var recap = Sysora.Core.Gaming.GameRecapBuilder.Build(Gaming.GameRecapBuilderTests.Session(start: T0), []);
        var alerts = new[] { TestData.Alert("cpu.sustained", T0, lasted: TimeSpan.FromMinutes(5), title: "High CPU") };
        var comparison = StateComparer.Compare(StateComparer.Summarize("Before", T0, T0.AddMinutes(5), recent, "test"), StateComparer.Summarize("After", T0.AddMinutes(5), now, recent, "test"));
        var timeline = TimelineBuilder.Build(new TimelineInput { From = T0, To = now, Events = replay.Events });
        var recorder = new TroubleshootingRecorder(T0);
        foreach (var point in recent)
        {
            recorder.Add(point);
        }

        var investigation = recorder.Build(Guid.NewGuid(), now, TimeSpan.FromMinutes(10), TroubleshootingEndReason.Completed, diagnosis, null);
        var scan = new LargeFileScanResult
        {
            Started = T0,
            Finished = T0.AddSeconds(30),
            Roots = [@"C:\"],
            MinimumSizeBytes = 100L << 20,
            Outcome = LargeFileScanOutcome.Completed,
            Message = "Scan completed",
            Files = [new LargeFile("a.zip", @"C:\a.zip", @"C:\", 5L << 30, T0, ".zip", @"C:\", LargeFileCategory.Archive, null)],
            AccessDenied = 2,
            AccessDeniedSamples = [@"C:\Secret"],
        };

        var reports = new[]
        {
            ReportBuilder.Diagnosis(diagnosis, null, now, System),
            ReportBuilder.WhyNow(why, now, System),
            ReportBuilder.Replay(replay, now, System),
            ReportBuilder.GameSession(recap, now, System),
            ReportBuilder.Alerts(alerts, now, System),
            ReportBuilder.Changes([], null, now, System),
            ReportBuilder.Comparison(comparison, now, System),
            ReportBuilder.Timeline(timeline, T0, now, now, System),
            ReportBuilder.Troubleshooting(investigation, now, System),
            ReportBuilder.LargeFiles(scan, now, System),
        };

        foreach (var report in reports)
        {
            Assert.Contains("<h1>", ReportWriter.ToHtml(report), StringComparison.Ordinal);
            using var json = JsonDocument.Parse(ReportWriter.ToJson(report));
            Assert.Equal(report.Kind.ToString(), json.RootElement.GetProperty("kind").GetString());
            Assert.True(json.RootElement.TryGetProperty("data", out _), report.Kind.ToString());
        }

        Assert.Contains(reports[^1].MissingData, m => m.StartsWith("2 folders could not be read (access denied)", StringComparison.Ordinal));
        Assert.Contains(reports[3].MissingData, m => m.Contains("FPS", StringComparison.Ordinal));
    }
}
