using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Sysora.Core.Gaming;
using Sysora.Core.History;
using Sysora.Core.Interfaces;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;
using Sysora.Core.Settings;
using Sysora.Infrastructure.Storage;

namespace Sysora.Tests.Gaming;

public sealed class GameSessionServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = TestData.Start;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(T0);
    private readonly PublishingMonitor _monitor = new();
    private readonly HistoryRepository _repository = HistoryRepository.InMemory(NullLogger<HistoryRepository>.Instance);
    private PerformanceHistory _history = null!;
    private SettingsService _settings = null!;
    private GameSessionService _service = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repository.InitializeAsync(Token);
        _history = new PerformanceHistory(TimeSpan.FromMinutes(30), _time);
        _settings = NullSettingsStore.Create(time: _time);
        _service = new GameSessionService(_monitor, _history, new EmptyLibrary(), _repository, _settings, NullLogger<GameSessionService>.Instance, _time, selfProcessId: 77);
        _service.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        await _repository.DisposeAsync();
    }

    [Fact]
    public async Task ClosedGame_IsSavedAndItsRecapIsAnnounced()
    {
        var recapReady = new TaskCompletionSource<GameRecap>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.RecapReady += (_, recap) => recapReady.TrySetResult(recap);
        LiveGameSession? started = null;
        _service.SessionStarted += (_, live) => started = live;

        PlayFor(TimeSpan.FromMinutes(5));
        Assert.NotNull(started);
        Assert.True(_service.IsGameRunning);
        Assert.Single(_service.ActiveSessions);
        CloseGame(after: TimeSpan.FromMinutes(5));

        var recap = await recapReady.Task.WaitAsync(Timeout, Token);
        Assert.Equal("Space Game", recap.Session.Name);
        Assert.Equal(TimeSpan.FromMinutes(5), recap.Session.Duration);
        Assert.False(_service.IsGameRunning);

        var stored = Assert.Single(await _repository.GetGameSessionsAsync(T0.AddDays(-1), Token));
        Assert.Equal(recap.Session.Id, stored.Id);
        Assert.Equal(recap.Session.GameCpu, stored.GameCpu);
        Assert.Equal(recap.Session.Timeline.Count, stored.Timeline.Count);
        Assert.Single(await _service.GetSessionsAsync(T0.AddDays(-1), Token));

        var events = _history.GetEvents(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Contains(events, e => e.Kind == SystemEventKind.GameStarted && e.Title == "Game started: Space Game");
        Assert.Contains(events, e => e.Kind == SystemEventKind.GameEnded);
    }

    [Fact]
    public async Task SessionShorterThanTheMinimum_IsNotKept()
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recaps = 0;
        _service.RecapReady += (_, _) => recaps++;

        PlayFor(TimeSpan.FromSeconds(40));
        _service.SessionsChanged += (_, _) => changed.TrySetResult();
        CloseGame(after: TimeSpan.FromSeconds(40));
        await changed.Task.WaitAsync(Timeout, Token);
        await Task.Delay(100, Token);

        Assert.Equal(0, recaps);
        Assert.Empty(await _service.GetSessionsAsync(T0.AddDays(-1), Token));
    }

    [Fact]
    public async Task WithoutRecordedHistory_SessionsStayInMemoryOnly()
    {
        _settings.Update(s => s with { History = s.History with { RecordHistory = false } });
        var recapReady = new TaskCompletionSource<GameRecap>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.RecapReady += (_, recap) => recapReady.TrySetResult(recap);

        PlayFor(TimeSpan.FromMinutes(3));
        CloseGame(after: TimeSpan.FromMinutes(3));
        await recapReady.Task.WaitAsync(Timeout, Token);

        Assert.Empty(await _repository.GetGameSessionsAsync(T0.AddDays(-1), Token));
        Assert.Single(await _service.GetSessionsAsync(T0.AddDays(-1), Token));
    }

    [Fact]
    public void MarkNotAGame_DropsTheSessionAndExcludesTheGame()
    {
        PlayFor(TimeSpan.FromSeconds(10));
        Assert.True(_service.IsGameRunning);

        _service.MarkNotAGame(GameSessionTrackerTests.GamePath);

        Assert.False(_service.IsGameRunning);
        Assert.Contains(GameSessionTrackerTests.GamePath, _settings.Current.Gaming.ExcludedGames);
        PlayFor(TimeSpan.FromSeconds(10), from: TimeSpan.FromSeconds(12));
        Assert.False(_service.IsGameRunning);
    }

    [Fact]
    public async Task Stopping_KeepsTheSessionInProgress()
    {
        PlayFor(TimeSpan.FromMinutes(3));

        await _service.StopAsync();

        var stored = Assert.Single(await _repository.GetGameSessionsAsync(T0.AddDays(-1), Token));
        Assert.Equal(GameSessionEnd.SysoraClosed, stored.EndReason);
    }

    [Fact]
    public async Task RecapOfAStoredSession_ComparesWithPreviousSessions()
    {
        var earlier = GameRecapBuilderTests.Session(start: T0.AddDays(-1));
        await _repository.SaveGameSessionAsync(earlier, Token);
        var current = GameRecapBuilderTests.Session(start: T0);
        await _repository.SaveGameSessionAsync(current, Token);

        var recap = await _service.GetRecapAsync(current, Token);

        Assert.Equal(1, recap.PreviousSessions);
        Assert.Contains("previous session", recap.ComparisonNote, StringComparison.Ordinal);
    }

    /// <summary>Publishes one update per second (processes every two seconds) with the game running.</summary>
    private void PlayFor(TimeSpan duration, TimeSpan? from = null)
    {
        var start = (int)(from ?? TimeSpan.Zero).TotalSeconds;
        for (var s = start; s <= start + (int)duration.TotalSeconds; s++)
        {
            _time.SetUtcNow(T0.AddSeconds(s));
            _monitor.Publish(GameSessionTrackerTests.Snapshot(T0.AddSeconds(s)), GameSessionTrackerTests.Kinds(s));
        }
    }

    private void CloseGame(TimeSpan after)
    {
        for (var s = (int)after.TotalSeconds + 2; s <= (int)after.TotalSeconds + 30; s += 2)
        {
            _time.SetUtcNow(T0.AddSeconds(s));
            _monitor.Publish(GameSessionTrackerTests.Snapshot(T0.AddSeconds(s), game: false), MetricKind.All);
        }
    }

    private sealed class EmptyLibrary : IGameLibrary
    {
        public IReadOnlySet<string> RecognizedGames { get; } = new HashSet<string>();

        public bool Refresh() => false;

        public string? GetProductName(string executablePath) => null;
    }

    /// <summary>A monitor whose updates are published by the test.</summary>
    private sealed class PublishingMonitor : IMetricsMonitor
    {
        public event EventHandler<SystemMetricsUpdatedEventArgs>? MetricsUpdated;

        public event EventHandler? StateChanged { add { } remove { } }

        public SystemSnapshot Current { get; private set; } = SystemSnapshot.Empty;

        public MetricHistory History { get; } = new(10);

        public bool IsRunning => true;

        public bool IsPaused => false;

        public SelfUsage SelfUsage => new(null, 0, 1);

        public void Publish(SystemSnapshot snapshot, MetricKind updated)
        {
            Current = snapshot;
            MetricsUpdated?.Invoke(this, new SystemMetricsUpdatedEventArgs(snapshot, updated));
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void SetActivity(MonitoringActivity activity)
        {
        }

        public bool IsInvestigating { get; private set; }

        public Sysora.Core.Monitoring.MonitoringScheduleInfo ScheduleInfo => Sysora.Core.Monitoring.MonitoringScheduleInfo.Unknown;

        public void SetInvestigationMode(bool enabled) => IsInvestigating = enabled;

        public void RequestRefresh(MetricKind kinds)
        {
        }
    }
}
