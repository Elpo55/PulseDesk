using PulseDesk.Core.Analysis;
using PulseDesk.Core.Diagnosis;
using PulseDesk.Core.Gaming;

namespace PulseDesk.Tests.Gaming;

public sealed class GameRecapBuilderTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Fact]
    public void Fps_IsAlwaysReportedAsNotAvailable()
    {
        var recap = GameRecapBuilder.Build(Session(), []);

        var fps = Assert.Single(recap.Metrics, m => m.Name == "FPS");
        Assert.Equal(GameRecapBuilder.NotAvailable, fps.Average);
        Assert.Equal(GameRecapBuilder.NotAvailable, fps.Maximum);
        Assert.False(fps.IsAvailable);
        Assert.Contains(GameRecapBuilder.FpsNotAvailable, recap.NotAvailable);
    }

    [Fact]
    public void QuietSession_HasNoProblemAndSaysWhatWasChecked()
    {
        var recap = GameRecapBuilder.Build(Session(gpu: 50), []);

        Assert.Equal("No problem observed", recap.Headline);
        Assert.Equal(DiagnosisSeverity.Normal, recap.Severity);
        Assert.Contains(recap.Findings, f => f.Title == "No resource limit reached");
        Assert.Contains("First recorded session of Space Game", recap.ComparisonNote, StringComparison.Ordinal);
        Assert.Empty(recap.Comparison);
    }

    [Fact]
    public void MemoryNearlyFull_IsAPossibleCauseWithEvidence()
    {
        var session = Session() with
        {
            Conditions = [new GameCondition(GameConditionKind.MemoryNearlyFull, 90, 240, 2, 180, 97, T0.AddMinutes(10))],
        };

        var recap = GameRecapBuilder.Build(session, []);

        Assert.Equal("1 point to watch", recap.Headline);
        var finding = recap.Findings[0];
        Assert.Equal(GameFindingKind.Anomaly, finding.Kind);
        Assert.Equal(DiagnosisSeverity.Warning, finding.Severity);
        Assert.Equal("Possible cause of stutters", finding.Qualifier);
        Assert.Contains("4m", finding.Description, StringComparison.Ordinal);
        var evidence = Assert.Single(finding.Evidence);
        Assert.Equal(T0.AddMinutes(10), evidence.From);
        Assert.Contains("peak 97%", evidence.Observed, StringComparison.Ordinal);
        Assert.DoesNotContain(recap.Findings, f => f.Title == "No resource limit reached");
    }

    [Fact]
    public void BusyGpu_IsTheLikelyLimitingFactor()
    {
        var recap = GameRecapBuilder.Build(Session(gpu: 96, cpu: 40), []);

        var finding = Assert.Single(recap.Findings, f => f.Kind == GameFindingKind.Analysis);
        Assert.Equal("Likely limiting factor", finding.Qualifier);
        Assert.Contains("graphics card", finding.Title, StringComparison.Ordinal);
        Assert.Contains("Likely limiting factor", recap.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BusyCpuWithoutGpuData_DoesNotClaimWhichComponentLimited()
    {
        var recap = GameRecapBuilder.Build(Session(gpu: null, cpu: 88), []);

        var finding = Assert.Single(recap.Findings, f => f.Kind == GameFindingKind.Analysis);
        Assert.Equal("Potential contributor", finding.Qualifier);
        Assert.Equal(ConfidenceLevel.Low, finding.Confidence);
        Assert.Contains("cannot tell", finding.Description, StringComparison.Ordinal);
        Assert.Equal(GameRecapBuilder.NotAvailable, recap.Metrics.Single(m => m.Name == "GPU (busiest adapter)").Average);
        Assert.Contains(recap.NotAvailable, n => n.StartsWith("GPU usage:", StringComparison.Ordinal));
    }

    [Fact]
    public void BusyBackgroundApplication_IsAPotentialContributor()
    {
        var session = Session() with
        {
            BackgroundApps = [new GameAppUsage("path:AV", "antivirus.exe", @"C:\AV\antivirus.exe", 17, 60, 300UL << 20), new GameAppUsage("path:X", "small.exe", null, 1, 3, 10UL << 20)],
        };

        var recap = GameRecapBuilder.Build(session, []);

        var finding = Assert.Single(recap.Findings, f => f.Title.StartsWith("antivirus.exe", StringComparison.Ordinal));
        Assert.Equal("Potential contributor", finding.Qualifier);
        Assert.Equal(DiagnosisSeverity.Warning, finding.Severity);
        Assert.DoesNotContain(recap.Findings, f => f.Title.StartsWith("small.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void PreviousSessionsOfTheSameGame_AreComparedOthersAreIgnored()
    {
        var previous = new[]
        {
            Session(start: T0.AddDays(-2), gameMemoryGb: 3),
            Session(start: T0.AddDays(-1), gameMemoryGb: 3),
            Session(start: T0.AddDays(-1), gameMemoryGb: 9) with { GameKey = "path:OTHER", Name = "Other Game" },
            Session(start: T0.AddDays(1), gameMemoryGb: 9),
        };

        var recap = GameRecapBuilder.Build(Session(gameMemoryGb: 5), previous);

        Assert.Equal(2, recap.PreviousSessions);
        Assert.Contains("2 previous sessions of Space Game", recap.ComparisonNote, StringComparison.Ordinal);
        var memory = Assert.Single(recap.Comparison, c => c.Metric == "Memory used by the game");
        Assert.True(memory.IsNotable);
        Assert.StartsWith("+2", memory.Difference, StringComparison.Ordinal);
        var finding = Assert.Single(recap.Findings, f => f.Kind == GameFindingKind.Comparison);
        Assert.Equal("Observed change, cause unknown", finding.Qualifier);
        Assert.False(recap.Comparison.Single(c => c.Metric == "CPU (whole PC)").IsNotable);
    }

    [Fact]
    public void LateStartGapsAndShutdown_AreStatedInTheCoverage()
    {
        var session = Session() with
        {
            GameStartedAt = T0.AddMinutes(-15),
            DataGaps = 1,
            GapSeconds = 600,
            EndReason = GameSessionEnd.PulseDeskClosed,
        };

        var recap = GameRecapBuilder.Build(session, []);

        Assert.Contains("already running", recap.Coverage, StringComparison.Ordinal);
        Assert.Contains("1 interruption", recap.Coverage, StringComparison.Ordinal);
        Assert.Contains("PulseDesk was closed before the game", recap.Coverage, StringComparison.Ordinal);
        Assert.Contains(recap.Findings, f => f.Title == "Measurements were interrupted");
    }

    internal static GameSession Session(DateTimeOffset? start = null, double? gpu = 70, double cpu = 45, double gameMemoryGb = 4)
    {
        var from = start ?? T0;
        return new GameSession
        {
            Id = Guid.NewGuid(),
            GameKey = "path:" + GameSessionTrackerTests.GamePath.ToUpperInvariant(),
            Name = "Space Game",
            ExecutablePath = GameSessionTrackerTests.GamePath,
            Source = GameDetectionSource.WindowsRecognized,
            Confidence = ConfidenceLevel.High,
            DetectionEvidence = "Windows recognizes this executable as a game.",
            Start = from,
            End = from.AddHours(1),
            MonitoredSeconds = 3600,
            ProcessSamples = 1800,
            Cpu = new MetricStat(cpu, cpu + 20, 3600),
            Memory = new MetricStat(55, 70, 3600),
            MemoryTotalBytes = TestData.TotalMemory,
            Gpu = gpu is { } g ? new MetricStat(g, Math.Min(100, g + 5), 1800) : null,
            GameGpu = gpu is { } gg ? new MetricStat(gg - 3, gg, 1800) : null,
            GameCpu = new MetricStat(cpu / 2, cpu, 1800),
            GameMemoryBytes = new MetricStat(gameMemoryGb * (1UL << 30), (gameMemoryGb + 0.5) * (1UL << 30), 1800),
            SelfCpu = new MetricStat(0.05, 0.3, 1800),
        };
    }
}
