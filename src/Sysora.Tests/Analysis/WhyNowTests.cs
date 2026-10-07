using Sysora.Core.Analysis;
using Sysora.Core.History;

namespace Sysora.Tests.Analysis;

public sealed class WhyNowTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Fact]
    public void WithAMinuteOfData_ItSaysItNeedsMore()
    {
        var points = TestData.Series(T0, 60, _ => 90);

        var explanation = WhyNowAnalyzer.Explain(new WhyNowInput { Metric = WhyNowMetric.Cpu, Points = points });

        Assert.False(explanation.IsSignificant);
        Assert.Equal("CPU usage: not enough measurements yet", explanation.Headline);
    }

    [Fact]
    public void StableActivity_HasNothingToExplain()
    {
        var points = TestData.Series(T0, 900, i => 20 + (i % 5));

        var explanation = WhyNowAnalyzer.Explain(new WhyNowInput { Metric = WhyNowMetric.Cpu, Points = points });

        Assert.False(explanation.IsSignificant);
        Assert.Equal("CPU usage is not unusually high right now", explanation.Headline);
        Assert.Null(explanation.Contributor);
    }

    [Fact]
    public void MemoryRise_FindsTheStart_TheLevels_AndTheLikelyContributor()
    {
        IReadOnlyList<AppSample> Before() => [TestData.App("browser.exe", 2, 1024), TestData.App("editor.exe", 1, 512)];
        IReadOnlyList<AppSample> After() => [TestData.App("browser.exe", 3, 5632), TestData.App("editor.exe", 1, 512)];
        var points = TestData.Series(T0, 1200, _ => 10, i => i < 600 ? 20 : 48.75, apps: i => i < 600 ? Before() : After());

        var explanation = WhyNowAnalyzer.Explain(new WhyNowInput { Metric = WhyNowMetric.Memory, Points = points, Alerts = [] });

        Assert.True(explanation.IsSignificant);
        Assert.Equal("Memory usage increased significantly", explanation.Headline);
        Assert.Equal(T0.AddMinutes(10), explanation.Started);
        Assert.Equal("3.2 GB", explanation.BeforeText);
        Assert.Equal("7.8 GB", explanation.NowText);
        Assert.Equal(TimeSpan.FromSeconds(599), explanation.Duration);
        var contributor = Assert.IsType<WhyNowContributor>(explanation.Contributor);
        Assert.Equal("browser.exe", contributor.Name);
        Assert.Equal(ConfidenceLevel.High, contributor.Confidence);
        Assert.StartsWith("Likely contributor: browser.exe (+4.5 GB", explanation.ContributorText, StringComparison.Ordinal);
        Assert.StartsWith("Evidence suggests browser.exe is the main contributor", explanation.Summary[explanation.Summary.IndexOf("Evidence", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains(explanation.Findings, f => f.Label == "Likely contributor" && f.Basis == FindingBasis.Inferred);
        Assert.Contains(explanation.Findings, f => f.Label == "Started" && f.Basis == FindingBasis.Observed);
        Assert.Equal("Similar events: none in the last 7 days.", explanation.SimilarText);
    }

    [Fact]
    public void RiseSpreadOverManyApplications_IsReportedAsCauseUnknown()
    {
        IReadOnlyList<AppSample> Apps(int i) =>
            [.. Enumerable.Range(0, 8).Select(n => TestData.App($"app{n}.exe", i < 600 ? 1 : 9, 100))];
        var points = TestData.Series(T0, 1200, i => i < 600 ? 10 : 80, apps: Apps);

        var explanation = WhyNowAnalyzer.Explain(new WhyNowInput { Metric = WhyNowMetric.Cpu, Points = points });

        Assert.True(explanation.IsSignificant);
        Assert.Null(explanation.Contributor);
        Assert.StartsWith("Cause unknown: no single application accounts for the increase", explanation.ContributorText, StringComparison.Ordinal);
        Assert.Contains(explanation.Findings, f => f.Basis == FindingBasis.Unknown);
    }

    [Fact]
    public void AlreadyHighAtTheStart_SaysTheStartIsUnknown()
    {
        var apps = (IReadOnlyList<AppSample>)[TestData.App("render.exe", 85, 900)];
        var points = TestData.Series(T0, 900, _ => 95, apps: _ => apps);

        var explanation = WhyNowAnalyzer.Explain(new WhyNowInput { Metric = WhyNowMetric.Cpu, Points = points });

        Assert.True(explanation.IsSignificant);
        Assert.True(explanation.StartedBeforeData);
        Assert.Null(explanation.Started);
        Assert.StartsWith("CPU usage has been high since before", explanation.Headline, StringComparison.Ordinal);
        Assert.Equal("Cause unknown: the rise started before the analyzed data. Largest consumer now: render.exe (85%).", explanation.ContributorText);
        Assert.Contains(explanation.Findings, f => f.Label == "Started" && f.Basis == FindingBasis.Unknown);
    }

    [Fact]
    public void NetworkContributor_IsNotAvailable()
    {
        var points = Enumerable.Range(0, 900)
            .Select(i => TestData.Metric(T0.AddSeconds(i), 10) with { NetworkReceiveBitsPerSecond = i < 450 ? 1e6 : 80e6, NetworkSendBitsPerSecond = 1e5 })
            .ToArray();

        var explanation = WhyNowAnalyzer.Explain(new WhyNowInput { Metric = WhyNowMetric.Network, Points = points });

        Assert.True(explanation.IsSignificant);
        Assert.StartsWith("Not available: Windows does not report network usage per application", explanation.ContributorText, StringComparison.Ordinal);
        Assert.Equal("Similar events: not tracked (no alert rule follows this metric).", explanation.SimilarText);
    }

    [Fact]
    public void SimultaneousChanges_AndAssociatedEvents_AreListed()
    {
        var points = TestData.Series(T0, 1200, i => i < 600 ? 10 : 85, disk: i => i < 600 ? 5 : 70);
        var events = new[]
        {
            new SystemEvent(T0.AddMinutes(9).AddSeconds(50), SystemEventKind.AppStarted, "setup.exe started") { AppKey = "name:SETUP.EXE" },
            new SystemEvent(T0.AddMinutes(2), SystemEventKind.AppStarted, "old.exe started"),
        };

        var explanation = WhyNowAnalyzer.Explain(new WhyNowInput { Metric = WhyNowMetric.Cpu, Points = points, Events = events });

        Assert.Contains("Disk activity also rose: 5% → 70%", explanation.Simultaneous);
        var associated = Assert.Single(explanation.AssociatedEvents);
        Assert.Equal("setup.exe started", associated.Title);
        Assert.Contains(explanation.Findings, f => f.Label == "Associated with" && f.Text.Contains("not proof of cause", StringComparison.Ordinal));
    }

    [Fact]
    public void SimilarEvents_AreCountedFromTheAlertHistory()
    {
        var points = TestData.Series(T0, 1200, _ => 10, i => i < 600 ? 20 : 60);
        var now = points[^1].Timestamp;
        var alerts = new[]
        {
            TestData.Alert("memory.sustained", now.AddDays(-2), lasted: TimeSpan.FromMinutes(5)),
            TestData.Alert("memory.growth", now.AddDays(-5), lasted: TimeSpan.FromMinutes(5)),
            TestData.Alert("memory.sustained", now.AddDays(-9), lasted: TimeSpan.FromMinutes(5)),
            TestData.Alert("cpu.sustained", now.AddDays(-1), lasted: TimeSpan.FromMinutes(5)),
        };

        var withHistory = WhyNowAnalyzer.Explain(new WhyNowInput { Metric = WhyNowMetric.Memory, Points = points, Alerts = alerts });
        var withoutHistory = WhyNowAnalyzer.Explain(new WhyNowInput { Metric = WhyNowMetric.Memory, Points = points });

        Assert.Equal(2, withHistory.SimilarCount);
        Assert.Equal("Similar events: 2 alerts in the last 7 days.", withHistory.SimilarText);
        Assert.Null(withoutHistory.SimilarCount);
        Assert.Equal("Similar events: not available (no alert history).", withoutHistory.SimilarText);
    }

    [Fact]
    public void PerMinuteHistory_BeforeTheDetailedData_ProvidesTheEarlierLevel()
    {
        // An hour of per-minute averages at 25% memory, then detailed data at 60%: the rise started at the boundary.
        var minutes = Enumerable.Range(0, 60)
            .Select(i => ReplayService.ToSnapshot(new SystemUsageAggregate
            {
                Start = T0.AddMinutes(i),
                Resolution = HistoryResolution.Minute,
                SampleCount = 60,
                Memory = new AggregateValue(25, 25, 60),
                MemoryTotalBytes = TestData.TotalMemory,
            }))
            .ToList();
        var detailed = TestData.Series(T0.AddHours(1), 600, _ => 10, _ => 60);

        var explanation = WhyNowAnalyzer.Explain(new WhyNowInput { Metric = WhyNowMetric.Memory, Points = [.. minutes, .. detailed] });

        Assert.True(explanation.IsSignificant);
        Assert.False(explanation.StartedBeforeData);
        Assert.Equal(T0.AddHours(1), explanation.Started);
        Assert.Equal("4 GB", explanation.BeforeText);
        Assert.StartsWith("Cause unknown: no per-application data", explanation.ContributorText, StringComparison.Ordinal);
    }
}
