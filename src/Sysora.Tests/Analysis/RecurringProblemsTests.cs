using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.History;

namespace Sysora.Tests.Analysis;

public sealed class RecurringProblemsTests
{
    // Monday 9 March 2026, 12:00 UTC; history since a week before.
    private static readonly DateTimeOffset Now = new(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset HistoryStart = Now.AddDays(-8);

    [Fact]
    public void ShortHistory_SaysThereIsNotEnoughData()
    {
        var alerts = Enumerable.Range(0, 5).Select(i => TestData.Alert("memory.sustained", Now.AddHours(-i * 3))).ToArray();

        var report = RecurringProblemDetector.Detect(alerts, [], Now, Now.AddHours(-20), TimeZoneInfo.Utc);

        Assert.False(report.HasEnoughHistory);
        Assert.Empty(report.Problems);
        Assert.StartsWith("Not enough historical data (20h 0m of history", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NoHistory_SaysSo()
    {
        var report = RecurringProblemDetector.Detect([], [], Now, null, TimeZoneInfo.Utc);

        Assert.False(report.HasEnoughHistory);
        Assert.StartsWith("Not enough historical data (no history yet", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeEpisodesOnTwoDays_AreRecurring_WithMediumConfidence()
    {
        var alerts = new[]
        {
            TestData.Alert("memory.sustained", Now.AddDays(-3).AddHours(2), lasted: TimeSpan.FromMinutes(10)),
            TestData.Alert("memory.sustained", Now.AddDays(-3).AddHours(5), lasted: TimeSpan.FromMinutes(10)),
            TestData.Alert("memory.sustained", Now.AddDays(-1), lasted: TimeSpan.FromMinutes(10)),
        };

        var report = RecurringProblemDetector.Detect(alerts, [], Now, HistoryStart, TimeZoneInfo.Utc);

        var problem = Assert.Single(report.Problems);
        Assert.Equal(RecurringProblemKind.HighMemory, problem.Kind);
        Assert.Equal(3, problem.Occurrences);
        Assert.Equal(2, problem.Days);
        Assert.Equal(ConfidenceLevel.Medium, problem.Confidence);
        Assert.Equal("High memory usage occurred 3 times in the last 7 days.", problem.Description);
        Assert.Null(problem.TimePattern);
    }

    [Fact]
    public void EpisodesOnASingleDay_AreNotCalledRecurring()
    {
        var alerts = Enumerable.Range(0, 5).Select(i => TestData.Alert("cpu.sustained", Now.AddDays(-2).Date.AddHours(8 + i))).ToArray();

        var report = RecurringProblemDetector.Detect(alerts, [], Now, HistoryStart, TimeZoneInfo.Utc);

        Assert.True(report.HasEnoughHistory);
        Assert.Empty(report.Problems);
        Assert.Equal("No recurring problem in the last 7 days.", report.Summary);
    }

    [Fact]
    public void FrequentEveningEpisodes_HaveHighConfidence_AndATimePattern_AndAnAssociatedApplication()
    {
        var alerts = new List<Alert>();
        var events = new List<SystemEvent>();
        for (var day = 1; day <= 5; day++)
        {
            var raised = Now.Date.AddDays(-day).AddHours(20);
            alerts.Add(TestData.Alert("memory.sustained", new DateTimeOffset(raised, TimeSpan.Zero), lasted: TimeSpan.FromMinutes(15)));
            if (day <= 4)
            {
                events.Add(new SystemEvent(new DateTimeOffset(raised, TimeSpan.Zero).AddMinutes(-4), SystemEventKind.AppHighMemory, "game.exe uses 9.1 GB of memory") { AppKey = "path:D:\\GAME.EXE" });
            }
        }

        alerts.Add(TestData.Alert("memory.sustained", new DateTimeOffset(Now.Date.AddDays(-6).AddHours(9), TimeSpan.Zero), lasted: TimeSpan.FromMinutes(5)));

        var problem = Assert.Single(RecurringProblemDetector.Detect(alerts, events, Now, HistoryStart, TimeZoneInfo.Utc).Problems);

        Assert.Equal(6, problem.Occurrences);
        Assert.Equal(ConfidenceLevel.High, problem.Confidence);
        Assert.Equal("Most events happened between 20:00 and 21:00 (5 of 6)", problem.TimePattern);
        Assert.Equal("game.exe", problem.AssociatedApp);
        Assert.Equal(4, problem.AssociatedCount);
        Assert.Contains(problem.Findings, f => f.Basis == FindingBasis.Inferred && f.Text.StartsWith("game.exe (4 of 6", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplicationAlerts_AreGroupedPerApplication()
    {
        var alerts = new List<Alert>();
        for (var day = 1; day <= 3; day++)
        {
            alerts.Add(TestData.Alert("app.cpu", Now.AddDays(-day), appKey: "path:C:\\TOOLS\\BUILD.EXE") with
            {
                Evidence = [new AnalysisEvidence("build.exe CPU", "At or above 25%")],
            });
            alerts.Add(TestData.Alert("app.cpu", Now.AddDays(-day).AddHours(1), appKey: "path:C:\\OTHER\\SYNC.EXE"));
        }

        var problems = RecurringProblemDetector.Detect(alerts, [], Now, HistoryStart, TimeZoneInfo.Utc).Problems;

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Title == "build.exe using a lot of CPU" && p.AssociatedAppKey == "path:C:\\TOOLS\\BUILD.EXE");
        Assert.Contains(problems, p => p.Title == "sync.exe using a lot of CPU");
    }

    [Fact]
    public void ConnectivityLosses_CloseTogether_AreOneEpisode()
    {
        var events = new List<SystemEvent>();
        for (var day = 1; day <= 3; day++)
        {
            var start = Now.AddDays(-day);
            events.Add(new SystemEvent(start, SystemEventKind.ConnectivityChanged, SystemEventDetector.NetworkLostTitle));
            events.Add(new SystemEvent(start.AddMinutes(1), SystemEventKind.ConnectivityChanged, SystemEventDetector.InternetAvailableTitle));
            events.Add(new SystemEvent(start.AddMinutes(5), SystemEventKind.ConnectivityChanged, SystemEventDetector.InternetLostTitle));
        }

        var problem = Assert.Single(RecurringProblemDetector.Detect([], events, Now, HistoryStart, TimeZoneInfo.Utc).Problems);

        Assert.Equal(RecurringProblemKind.ConnectivityLoss, problem.Kind);
        Assert.Equal(3, problem.Occurrences);
        Assert.Contains(problem.Findings, f => f.Basis == FindingBasis.Unknown);
    }

    [Fact]
    public void LastingStates_AndOldAlerts_AreIgnored()
    {
        var alerts = new[]
        {
            TestData.Alert("storage.low", Now.AddDays(-1), key: "storage.low:C:"),
            TestData.Alert("storage.low", Now.AddDays(-2), key: "storage.low:C:"),
            TestData.Alert("storage.low", Now.AddDays(-3), key: "storage.low:C:"),
            TestData.Alert("disk.busy", Now.AddDays(-9)),
            TestData.Alert("disk.busy", Now.AddDays(-10)),
            TestData.Alert("disk.busy", Now.AddDays(-1)),
        };

        Assert.Empty(RecurringProblemDetector.Detect(alerts, [], Now, HistoryStart.AddDays(-5), TimeZoneInfo.Utc).Problems);
    }
}
