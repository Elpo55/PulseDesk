using Sysora.Core.Gaming;
using Sysora.Core.History;
using Sysora.Core.Models;
using Sysora.Core.Settings;

namespace Sysora.Tests.Gaming;

public sealed class GameSessionTrackerTests
{
    internal const string GamePath = @"D:\SteamLibrary\steamapps\common\Space Game\bin\space.exe";
    private const int GamePid = 500;
    private const int SelfPid = 77;
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Fact]
    public void OrdinaryApplications_NeverStartASession()
    {
        var tracker = NewTracker();

        var update = tracker.Observe(Snapshot(T0, game: false), MetricKind.All);

        Assert.True(update.IsEmpty);
        Assert.False(tracker.HasActiveSessions);
    }

    [Fact]
    public void GameStartAndClose_ProduceOneSessionWithMeasuredValues()
    {
        var tracker = NewTracker();
        var started = tracker.Observe(Snapshot(T0, cpu: 50), MetricKind.All);
        var live = Assert.Single(started.Started);
        Assert.Equal("Space Game", live.Name);

        for (var s = 1; s <= 300; s++)
        {
            tracker.Observe(Snapshot(T0.AddSeconds(s), cpu: 70), Kinds(s));
        }

        // Closed: absent from the following process samples until the grace period has passed.
        GameTrackerUpdate update = GameTrackerUpdate.None;
        for (var s = 302; s <= 330 && update.Ended.Count == 0; s += 2)
        {
            update = tracker.Observe(Snapshot(T0.AddSeconds(s), cpu: 5, game: false), MetricKind.Cpu | MetricKind.Processes);
        }

        var session = Assert.Single(update.Ended);
        Assert.False(tracker.HasActiveSessions);
        Assert.Equal(GameSessionEnd.GameClosed, session.EndReason);
        Assert.Equal(T0, session.Start);
        Assert.Equal(T0.AddSeconds(300), session.End);
        Assert.Equal(300, session.MonitoredSeconds, 0.001);
        Assert.Equal(GameDetectionSource.GameLibrary, session.Source);

        // CPU samples after the game closed are not attributed to it.
        Assert.Equal(70, session.Cpu!.Value.Maximum);
        Assert.InRange(session.Cpu.Value.Average, 69.9, 70);
        Assert.Equal(30, session.GameCpu!.Value.Average, 0.001);
        Assert.Equal(4096.0 * 1024 * 1024, session.GameMemoryBytes!.Value.Average, 1);
        Assert.Equal(1, session.GameProcessCount);
        Assert.Equal(1.5, session.SelfCpu!.Value.Average, 0.001);
        Assert.Equal(90, session.Gpu!.Value.Average, 0.001);
        Assert.Equal(85, session.GameGpu!.Value.Average, 0.001);
        Assert.Equal(6.0 * (1UL << 30), session.VideoMemoryBytes!.Value.Average, 1);
        Assert.Equal(8UL << 30, session.VideoMemoryTotalBytes);
        Assert.Equal("Test GPU", session.GpuName);
        Assert.Empty(session.Conditions);
        Assert.Equal(0, session.DataGaps);
    }

    [Fact]
    public void SysoraClosingRightAfterTheGame_RecordsTheGameAsClosed()
    {
        var tracker = NewTracker();
        tracker.Observe(Snapshot(T0), MetricKind.All);
        tracker.Observe(Snapshot(T0.AddSeconds(4), game: false), MetricKind.All);

        var session = Assert.Single(tracker.EndAll(GameSessionEnd.SysoraClosed));

        Assert.Equal(GameSessionEnd.GameClosed, session.EndReason);
        Assert.Equal(T0, session.End);
    }

    [Fact]
    public void GameRestartingWithinGracePeriod_StaysOneSession()
    {
        var tracker = NewTracker();
        tracker.Observe(Snapshot(T0), MetricKind.All);
        tracker.Observe(Snapshot(T0.AddSeconds(2)), MetricKind.All);

        var absent = tracker.Observe(Snapshot(T0.AddSeconds(10), game: false), MetricKind.All);
        var back = tracker.Observe(Snapshot(T0.AddSeconds(14)), MetricKind.All);

        Assert.True(absent.IsEmpty);
        Assert.True(back.IsEmpty);
        Assert.True(tracker.HasActiveSessions);
    }

    [Fact]
    public void BackgroundAssociatedAndOwnProcesses_AreKeptApart()
    {
        var tracker = NewTracker();
        for (var s = 0; s <= 60; s += 2)
        {
            tracker.Observe(Snapshot(T0.AddSeconds(s)), MetricKind.All);
        }

        var session = Assert.Single(tracker.EndAll(GameSessionEnd.SysoraClosed));

        Assert.Equal(GameSessionEnd.SysoraClosed, session.EndReason);
        var background = session.BackgroundApps[0];
        Assert.Equal("antivirus.exe", background.Name);
        Assert.Equal(12, background.CpuAverage, 0.001);
        Assert.DoesNotContain(session.BackgroundApps, a => a.Name is "space.exe" or "Sysora.exe" or "crashhandler.exe" or "voicechat.exe");
        Assert.Contains(session.AssociatedProcesses, a => a.Name == "crashhandler.exe");
        Assert.Contains(session.AssociatedProcesses, a => a.Name == "voicechat.exe");
    }

    [Fact]
    public void MemoryNearlyFullForMinutes_IsRecordedButShortSpikesAreNot()
    {
        var tracker = NewTracker();
        for (var s = 0; s <= 600; s++)
        {
            // 30-second spike at 2 min, then 2 minutes above 90% from 5 min.
            var memory = s is >= 120 and < 150 ? 95 : s is >= 300 and <= 420 ? 93 : 60;
            tracker.Observe(Snapshot(T0.AddSeconds(s), memory: memory), Kinds(s));
        }

        var session = Assert.Single(tracker.EndAll(GameSessionEnd.GameClosed));
        var condition = Assert.Single(session.Conditions);
        Assert.Equal(GameConditionKind.MemoryNearlyFull, condition.Kind);
        Assert.Equal(1, condition.Periods);
        Assert.Equal(120, condition.TotalSeconds, 0.001);
        Assert.Equal(T0.AddSeconds(300), condition.FirstAt);
    }

    [Fact]
    public void GapInMeasurements_IsNotCountedAsMonitoredTime()
    {
        var tracker = NewTracker();
        tracker.Observe(Snapshot(T0), MetricKind.All);
        tracker.Observe(Snapshot(T0.AddSeconds(10)), MetricKind.All);
        tracker.Observe(Snapshot(T0.AddMinutes(30)), MetricKind.All);
        tracker.Observe(Snapshot(T0.AddMinutes(30).AddSeconds(10)), MetricKind.All);

        var session = Assert.Single(tracker.EndAll(GameSessionEnd.GameClosed));

        Assert.Equal(1, session.DataGaps);
        Assert.Equal(20, session.MonitoredSeconds, 0.001);
        Assert.Equal((30 * 60) - 10, session.GapSeconds, 0.001);
        Assert.Contains(session.Events, e => e.Text.Contains("No measurements", StringComparison.Ordinal));
    }

    [Fact]
    public void AdapterWithoutPerProcessData_LeavesGameGpuUnknown()
    {
        var tracker = NewTracker();
        var gpus = new[] { new GpuMetrics("gpu0", "Old GPU") { UsagePercent = 40 } };
        tracker.Observe(Snapshot(T0) with { Gpus = gpus }, MetricKind.All);

        var session = Assert.Single(tracker.EndAll(GameSessionEnd.GameClosed));

        Assert.Equal(40, session.Gpu!.Value.Average);
        Assert.Null(session.GameGpu);
        Assert.Null(session.VideoMemoryBytes);
    }

    [Fact]
    public void LongSession_KeepsABoundedTimeline()
    {
        var tracker = NewTracker();
        for (var minute = 0; minute < 600; minute++)
        {
            tracker.Observe(Snapshot(T0.AddMinutes(minute)), MetricKind.All);
        }

        var session = Assert.Single(tracker.EndAll(GameSessionEnd.GameClosed));

        Assert.InRange(session.Timeline.Count, 100, GameSessionTracker.MaxTimelinePoints + 1);
        Assert.True(session.TimelineStepSeconds >= 120);
        Assert.Equal(session.Timeline.Count, session.Timeline.Select(p => p.Start).Distinct().Count());
        Assert.All(session.Timeline, p => Assert.Equal(50, p.Cpu!.Value, 0.001));
    }

    [Fact]
    public void MarkingTheGameAsNotAGame_DropsTheSessionInProgress()
    {
        var tracker = NewTracker();
        tracker.Observe(Snapshot(T0), MetricKind.All);

        var dropped = tracker.Configure(new GamingSettings { ExcludedGames = [GamePath] }, new HashSet<string>());

        Assert.Single(dropped);
        Assert.False(tracker.HasActiveSessions);
        Assert.True(tracker.Observe(Snapshot(T0.AddSeconds(2)), MetricKind.All).IsEmpty);
    }

    [Fact]
    public void GameAlreadyRunningWhenSysoraStarts_IsFlagged()
    {
        var tracker = NewTracker();
        tracker.Observe(Snapshot(T0, gameStarted: T0.AddMinutes(-20)), MetricKind.All);

        var session = Assert.Single(tracker.EndAll(GameSessionEnd.GameClosed));

        Assert.Equal(T0.AddMinutes(-20), session.GameStartedAt);
        Assert.Equal(T0, session.Start);
    }

    [Fact]
    public void DisabledTracking_ObservesNothing()
    {
        var tracker = new GameSessionTracker(new GamingSettings { Enabled = false }, new HashSet<string>(), selfProcessId: SelfPid);

        Assert.True(tracker.Observe(Snapshot(T0), MetricKind.All).IsEmpty);
    }

    internal static GameSessionTracker NewTracker() =>
        new(new GamingSettings(), new HashSet<string>(), _ => "Unity Player", SelfPid);

    /// <summary>CPU every second, everything else every two seconds (as the monitor does by default).</summary>
    internal static MetricKind Kinds(int second) => second % 2 == 0 ? MetricKind.All : MetricKind.Cpu | MetricKind.Memory;

    /// <summary>A PC running the game (30% CPU, 4 GB), Sysora, an antivirus, the game's helpers and a GPU at 90%.</summary>
    internal static SystemSnapshot Snapshot(DateTimeOffset time, double cpu = 50, double memory = 60, bool game = true, DateTimeOffset? gameStarted = null)
    {
        var processes = new List<ProcessMetrics>
        {
            TestData.Process(SelfPid, "Sysora.exe", 1.5, 120, @"C:\Program Files\Sysora\Sysora.exe"),
            TestData.Process(40, "antivirus.exe", 12, 300, @"C:\Program Files\AV\antivirus.exe"),
            TestData.Process(41, "explorer.exe", 0.5, 90, @"C:\Windows\explorer.exe"),
        };
        if (game)
        {
            processes.Add(TestData.Process(GamePid, "space.exe", 30, 4096, GamePath) with { StartTime = gameStarted ?? T0 });
            processes.Add(TestData.Process(501, "crashhandler.exe", 0.1, 20, @"D:\SteamLibrary\steamapps\common\Space Game\crashhandler.exe"));
            processes.Add(TestData.Process(502, "voicechat.exe", 2, 80, @"C:\Program Files\Voice\voicechat.exe") with { ParentProcessId = GamePid });
        }

        return TestData.System(time, cpu, memory, processes) with
        {
            Gpus =
            [
                new GpuMetrics("gpu0", "Test GPU")
                {
                    UsagePercent = 90,
                    DedicatedMemoryTotalBytes = 8UL << 30,
                    DedicatedMemoryUsedBytes = 6UL << 30,
                    Engines = [new GpuEngineUsage("3D", 90)],
                    Processes = game ? [new GpuProcessUsage(GamePid, 85), new GpuProcessUsage(SelfPid, 0.5)] : [],
                },
            ],
        };
    }
}
