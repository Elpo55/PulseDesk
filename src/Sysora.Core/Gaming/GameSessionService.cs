using Microsoft.Extensions.Logging;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Interfaces;
using Sysora.Core.Models;
using Sysora.Core.Settings;

namespace Sysora.Core.Gaming;

/// <summary>
/// Follows gaming sessions in the background: detects identified games from the process samples Sysora already
/// takes, records what happened during each session, and produces a recap when the game closes. Sessions are kept in
/// the local history (or in memory for this session only when recording is off).
/// </summary>
/// <remarks>
/// Designed to cost the game nothing measurable: no timer per sample and no extra collection (it reads the monitor's
/// snapshots), work proportional to the number of processes only when processes are sampled, and bounded memory.
/// </remarks>
public sealed class GameSessionService : IAsyncDisposable
{
    /// <summary>How often the list of games Windows recognizes is read again.</summary>
    public static readonly TimeSpan LibraryRefreshInterval = TimeSpan.FromMinutes(30);

    /// <summary>Sessions shown on the Gaming page.</summary>
    public static readonly TimeSpan ListedHistory = TimeSpan.FromDays(90);

    /// <summary>Sessions kept in memory when the history is not recorded.</summary>
    private const int MaxUnsavedSessions = 50;

    private readonly IMetricsMonitor _monitor;
    private readonly IPerformanceHistory _history;
    private readonly IGameLibrary _library;
    private readonly IHistoryRepository _repository;
    private readonly SettingsService _settings;
    private readonly ILogger<GameSessionService> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly GameSessionTracker _tracker;
    private readonly List<GameSession> _unsaved = [];
    private ITimer? _libraryTimer;
    private GameRecap? _latestRecap;
    private bool _started;

    public GameSessionService(
        IMetricsMonitor monitor,
        IPerformanceHistory history,
        IGameLibrary library,
        IHistoryRepository repository,
        SettingsService settings,
        ILogger<GameSessionService> logger,
        TimeProvider? timeProvider = null,
        int? selfProcessId = null)
    {
        _monitor = monitor;
        _history = history;
        _library = library;
        _repository = repository;
        _settings = settings;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _tracker = new GameSessionTracker(settings.Current.Gaming, library.RecognizedGames, ReadProductName, selfProcessId ?? Environment.ProcessId);
    }

    /// <summary>Raised on a background thread when a game starts.</summary>
    public event EventHandler<LiveGameSession>? SessionStarted;

    /// <summary>Raised on a background thread when a session ends and its recap is ready.</summary>
    public event EventHandler<GameRecap>? RecapReady;

    /// <summary>Raised on a background thread when sessions start, end or are dropped.</summary>
    public event EventHandler? SessionsChanged;

    /// <summary>Sessions in progress.</summary>
    public IReadOnlyList<LiveGameSession> ActiveSessions
    {
        get
        {
            lock (_lock)
            {
                return _tracker.Active;
            }
        }
    }

    /// <summary>Recap of the last session that ended since Sysora started, or null.</summary>
    public GameRecap? LatestRecap => Volatile.Read(ref _latestRecap);

    /// <summary>True while a game is running.</summary>
    public bool IsGameRunning
    {
        get
        {
            lock (_lock)
            {
                return _tracker.HasActiveSessions;
            }
        }
    }

    /// <summary>Starts following game sessions.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _monitor.MetricsUpdated += OnMetricsUpdated;
        _history.EventRecorded += OnEventRecorded;
        _settings.Changed += OnSettingsChanged;
        _libraryTimer = _time.CreateTimer(_ => RefreshLibrary(), null, TimeSpan.Zero, LibraryRefreshInterval);
    }

    /// <summary>Ends the sessions in progress (keeping what was measured) and stops following games.</summary>
    public async Task StopAsync()
    {
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
        }

        _monitor.MetricsUpdated -= OnMetricsUpdated;
        _history.EventRecorded -= OnEventRecorded;
        _settings.Changed -= OnSettingsChanged;
        if (_libraryTimer is not null)
        {
            await _libraryTimer.DisposeAsync().ConfigureAwait(false);
            _libraryTimer = null;
        }

        IReadOnlyList<GameSession> ended;
        lock (_lock)
        {
            ended = _tracker.EndAll(GameSessionEnd.SysoraClosed);
        }

        foreach (var session in ended)
        {
            await KeepAsync(session, notify: false).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Recorded sessions since <paramref name="since"/>, most recent first.</summary>
    public async Task<IReadOnlyList<GameSession>> GetSessionsAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        IReadOnlyList<GameSession> stored = [];
        try
        {
            stored = await _repository.GetGameSessionsAsync(since, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Game sessions could not be read from the history.");
        }

        GameSession[] unsaved;
        lock (_lock)
        {
            unsaved = _unsaved.Where(s => s.Start >= since).ToArray();
        }

        return stored.Concat(unsaved)
            .DistinctBy(s => s.Id)
            .OrderByDescending(s => s.Start)
            .ToArray();
    }

    /// <summary>The recap of a recorded session, compared with the previous sessions of the same game.</summary>
    public async Task<GameRecap> GetRecapAsync(GameSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var history = await GetSessionsAsync(session.Start - ListedHistory, cancellationToken).ConfigureAwait(false);
        return GameRecapBuilder.Build(session, history);
    }

    /// <summary>Marks an executable as not a game: it is never followed again, and a session in progress is dropped.</summary>
    public void MarkNotAGame(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _settings.Update(s => s with
        {
            Gaming = s.Gaming with
            {
                ExcludedGames = [.. s.Gaming.ExcludedGames, executablePath],
                AddedGames = s.Gaming.AddedGames.Where(p => !string.Equals(p, executablePath, StringComparison.OrdinalIgnoreCase)).ToArray(),
            },
        });
    }

    /// <summary>Marks an executable as a game (detected from then on, whatever the automatic detection says).</summary>
    public void MarkAsGame(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _settings.Update(s => s with
        {
            Gaming = s.Gaming with
            {
                AddedGames = [.. s.Gaming.AddedGames, executablePath],
                ExcludedGames = s.Gaming.ExcludedGames.Where(p => !string.Equals(p, executablePath, StringComparison.OrdinalIgnoreCase)).ToArray(),
            },
        });
    }

    private void OnMetricsUpdated(object? sender, SystemMetricsUpdatedEventArgs e)
    {
        GameTrackerUpdate update;
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            update = _tracker.Observe(e.Snapshot, e.Updated);
        }

        if (update.IsEmpty)
        {
            return;
        }

        foreach (var started in update.Started)
        {
            _logger.LogInformation("Game session started: {Game}. {Evidence}", started.Name, started.DetectionEvidence);
            _history.AddEvent(new SystemEvent(started.Start, SystemEventKind.GameStarted, $"Game started: {started.Name}", started.DetectionEvidence)
            {
                AppKey = started.GameKey,
            });
            SessionStarted?.Invoke(this, started);
        }

        foreach (var ended in update.Ended)
        {
            _history.AddEvent(new SystemEvent(ended.End, SystemEventKind.GameEnded, $"Game closed: {ended.Name} ({MetricFormatter.DurationPrecise(ended.Duration)})")
            {
                AppKey = ended.GameKey,
            });

            // Saving and comparing read the history: off the monitoring thread.
            _ = Task.Run(() => KeepAsync(ended, notify: true));
        }

        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves a finished session (when long enough) and announces its recap.</summary>
    private async Task KeepAsync(GameSession session, bool notify)
    {
        try
        {
            var minimum = TimeSpan.FromMinutes(_settings.Current.Gaming.MinimumSessionMinutes);
            if (session.Duration < minimum)
            {
                _logger.LogInformation("Game session of {Game} not kept: {Duration} is shorter than {Minimum}.", session.Name, MetricFormatter.DurationPrecise(session.Duration), MetricFormatter.DurationCompact(minimum));
                return;
            }

            var saved = false;
            if (_settings.Current.History.RecordHistory)
            {
                try
                {
                    await _repository.SaveGameSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
                    saved = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "The game session could not be saved; it is kept until Sysora exits.");
                }
            }

            if (!saved)
            {
                lock (_lock)
                {
                    _unsaved.Add(session);
                    if (_unsaved.Count > MaxUnsavedSessions)
                    {
                        _unsaved.RemoveAt(0);
                    }
                }
            }

            _logger.LogInformation("Game session ended: {Game}, {Duration}.", session.Name, MetricFormatter.DurationPrecise(session.Duration));
            if (notify)
            {
                var recap = await GetRecapAsync(session, CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref _latestRecap, recap);
                RecapReady?.Invoke(this, recap);
            }

            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "The game session of {Game} could not be completed.", session.Name);
        }
    }

    private void OnEventRecorded(object? sender, SystemEvent systemEvent)
    {
        if (systemEvent.Kind is not (SystemEventKind.AlertRaised or SystemEventKind.SystemSuspending or SystemEventKind.SystemResumed or SystemEventKind.MonitoringPaused or SystemEventKind.MonitoringResumed))
        {
            return;
        }

        lock (_lock)
        {
            _tracker.AddEvent(systemEvent);
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Gaming == e.Current.Gaming)
        {
            return;
        }

        IReadOnlyList<GameSession> stopped = [];
        IReadOnlyList<LiveGameSession> dropped;
        lock (_lock)
        {
            if (!e.Current.Gaming.Enabled && e.Previous.Gaming.Enabled)
            {
                stopped = _tracker.EndAll(GameSessionEnd.TrackingStopped);
            }

            dropped = _tracker.Configure(e.Current.Gaming, _library.RecognizedGames);
        }

        foreach (var session in dropped)
        {
            _logger.LogInformation("Game session of {Game} dropped: marked as not a game.", session.Name);
        }

        foreach (var session in stopped)
        {
            _ = Task.Run(() => KeepAsync(session, notify: false));
        }

        if (stopped.Count > 0 || dropped.Count > 0)
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshLibrary()
    {
        try
        {
            if (!_library.Refresh())
            {
                return;
            }

            lock (_lock)
            {
                _tracker.Configure(_settings.Current.Gaming, _library.RecognizedGames);
            }

            _logger.LogDebug("{Count} games recognized by Windows.", _library.RecognizedGames.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "The list of games recognized by Windows could not be read.");
        }
    }

    private string? ReadProductName(string path) => _library.GetProductName(path);
}
