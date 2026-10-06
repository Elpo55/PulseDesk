using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using PulseDesk.App.Controls;
using PulseDesk.App.Services;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.Gaming;
using PulseDesk.Core.Metrics;
using PulseDesk.Core.Models;
using PulseDesk.Core.Settings;

namespace PulseDesk.App.ViewModels;

/// <summary>
/// Gaming: the game running now, and a recap of each past session (averages and peaks, limits reached, analysis,
/// comparison with previous sessions of the same game). Frame rates are never shown: Windows offers no reliable source.
/// </summary>
public sealed partial class GamingViewModel : PageViewModel
{
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly GameSessionService _games;
    private readonly SettingsService _settings;
    private readonly NavigationService _navigation;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<GamingViewModel> _logger;
    private IReadOnlyList<GameRecap> _recaps = [];
    private CancellationTokenSource? _load;
    private Guid? _requestedSession;
    private string? _liveExecutable;
    private long _lastLive;
    private bool _reloadQueued;

    public GamingViewModel(
        UiMetricsHub hub,
        GameSessionService games,
        SettingsService settings,
        NavigationService navigation,
        DispatcherQueue dispatcher,
        ILogger<GamingViewModel> logger)
        : base(hub)
    {
        _games = games;
        _settings = settings;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _logger = logger;
        Summary = Note = LiveTitle = LiveDetail = LiveStats = LiveOverhead = ErrorText = string.Empty;
        SelectedIndex = -1;
    }

    public ObservableCollection<GameSessionItemViewModel> Sessions { get; } = [];

    public GameRecapViewModel Recap { get; } = new();

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string Note { get; set; }

    public bool HasNote => Note.Length > 0;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsDisabled { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; }

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    public partial bool HasLive { get; set; }

    [ObservableProperty]
    public partial string LiveTitle { get; set; }

    [ObservableProperty]
    public partial string LiveDetail { get; set; }

    [ObservableProperty]
    public partial string LiveStats { get; set; }

    [ObservableProperty]
    public partial string LiveOverhead { get; set; }

    /// <summary>Shows the recap of a session when the page opens (from a notification or the dashboard).</summary>
    public void RequestSelection(Guid sessionId)
    {
        _requestedSession = sessionId;
        if (IsActive)
        {
            SelectRequested();
        }
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (value >= 0 && value < _recaps.Count)
        {
            Recap.Apply(_recaps[value]);
            HasSelection = true;
        }
        else
        {
            HasSelection = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void OpenSettings() => _navigation.Navigate(AppPage.Settings);

    /// <summary>The game running now is not a game: forget it and never follow it again.</summary>
    [RelayCommand]
    private void MarkLiveNotAGame()
    {
        if (_liveExecutable is { } path)
        {
            _games.MarkNotAGame(path);
            UpdateLive();
        }
    }

    /// <summary>The selected session is not a game: stop following this executable (its past sessions are kept).</summary>
    [RelayCommand]
    private void MarkSelectedNotAGame()
    {
        if (SelectedIndex >= 0 && SelectedIndex < _recaps.Count)
        {
            _games.MarkNotAGame(_recaps[SelectedIndex].Session.ExecutablePath);
            Note = $"{_recaps[SelectedIndex].Session.Name} will not be followed as a game any more. You can undo this in Settings › Gaming.";
        }
    }

    protected override void OnActivated()
    {
        _games.SessionsChanged += OnSessionsChanged;
        _settings.Changed += OnSettingsChanged;
        IsDisabled = !_settings.Current.Gaming.Enabled;
        UpdateLive();
        _ = LoadAsync();
    }

    protected override void OnDeactivated()
    {
        _games.SessionsChanged -= OnSessionsChanged;
        _settings.Changed -= OnSettingsChanged;
        _load?.Cancel();
    }

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        if ((updated & MetricKind.Cpu) != 0 && Environment.TickCount64 - _lastLive >= LiveRefreshInterval.TotalMilliseconds)
        {
            UpdateLive();
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) =>
        _dispatcher.TryEnqueue(() => IsDisabled = !e.Current.Gaming.Enabled);

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        if (_reloadQueued)
        {
            return;
        }

        _reloadQueued = true;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _reloadQueued = false;
            if (IsActive)
            {
                UpdateLive();
                _ = LoadAsync();
            }
        });
    }

    private void UpdateLive()
    {
        _lastLive = Environment.TickCount64;
        var live = _games.ActiveSessions.FirstOrDefault();
        HasLive = live is not null;
        _liveExecutable = live?.ExecutablePath;
        if (live is null)
        {
            return;
        }

        LiveTitle = $"{live.Name} · running for {MetricFormatter.DurationPrecise(live.Duration)}";
        LiveDetail = $"{live.DetectionEvidence} ({InsightDisplay.Text(live.Confidence).ToLowerInvariant()}) · {live.ExecutablePath}";
        var parts = new List<string>();
        if (live.Cpu is { } cpu)
        {
            parts.Add($"CPU {MetricFormatter.Percent(cpu.Average)} avg ({MetricFormatter.Percent(cpu.Maximum)} peak)");
        }

        if (live.Gpu is { } gpu)
        {
            parts.Add($"GPU {MetricFormatter.Percent(gpu.Average)} avg ({MetricFormatter.Percent(gpu.Maximum)} peak)");
        }

        if (live.Memory is { } memory)
        {
            parts.Add($"memory {MetricFormatter.Percent(memory.Average)} avg");
        }

        if (live.GameMemoryBytes is { } gameMemory)
        {
            parts.Add($"game {MetricFormatter.Bytes(gameMemory.Average)} avg");
        }

        LiveStats = parts.Count > 0 ? string.Join(" · ", parts) : "Collecting the first measurements…";
        LiveOverhead = live.SelfCpu is { } self
            ? $"PulseDesk itself: {MetricFormatter.Percent(self.Average, 2)} of CPU on average during this session. FPS: Not available."
            : "FPS: Not available.";
    }

    private async Task LoadAsync()
    {
        if (!IsActive)
        {
            return;
        }

        _load?.Cancel();
        _load?.Dispose();
        var load = _load = new CancellationTokenSource();
        IsLoading = true;
        HasError = false;
        try
        {
            var sessions = await _games.GetSessionsAsync(DateTimeOffset.UtcNow - GameSessionService.ListedHistory, load.Token);
            var recaps = await Task.Run(() => sessions.Select(s => GameRecapBuilder.Build(s, sessions)).ToArray(), load.Token);
            if (load.IsCancellationRequested)
            {
                return;
            }

            var selectedId = SelectedIndex >= 0 && SelectedIndex < _recaps.Count ? _recaps[SelectedIndex].Session.Id : (Guid?)null;
            _recaps = recaps;
            CollectionSync.Resize(Sessions, recaps.Length, _ => new GameSessionItemViewModel(), (item, i) => item.Set(recaps[i]));
            IsEmpty = recaps.Length == 0;
            Summary = recaps.Length == 0
                ? "No game session recorded yet."
                : $"{MetricFormatter.Plural(recaps.Length, "session")} in the last 90 days · {MetricFormatter.DurationCompact(TimeSpan.FromSeconds(recaps.Sum(r => r.Session.Duration.TotalSeconds)))} of play measured";

            if (_requestedSession is not null)
            {
                SelectRequested();
            }
            else
            {
                var index = selectedId is { } id ? Array.FindIndex(recaps, r => r.Session.Id == id) : -1;
                Select(index >= 0 ? index : recaps.Length > 0 ? 0 : -1);
            }
        }
        catch (OperationCanceledException)
        {
            // Left the page or reloaded.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Game sessions could not be loaded.");
            HasError = true;
            ErrorText = $"Game sessions could not be loaded: {ex.Message}";
        }
        finally
        {
            if (_load == load)
            {
                IsLoading = false;
            }
        }
    }

    private void SelectRequested()
    {
        if (_requestedSession is not { } id)
        {
            return;
        }

        var index = _recaps.ToList().FindIndex(r => r.Session.Id == id);
        if (index >= 0)
        {
            _requestedSession = null;
            Select(index);
        }
    }

    /// <summary>Selects a session; shows its recap again when it was already selected (the list was reloaded).</summary>
    private void Select(int index)
    {
        if (SelectedIndex == index)
        {
            OnSelectedIndexChanged(index);
        }
        else
        {
            SelectedIndex = index;
        }
    }
}

/// <summary>One session in the list.</summary>
public sealed partial class GameSessionItemViewModel : ObservableObject
{
    public GameSessionItemViewModel()
    {
        Name = WhenText = Headline = Glyph = BrushKey = string.Empty;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string WhenText { get; set; }

    [ObservableProperty]
    public partial string Headline { get; set; }

    [ObservableProperty]
    public partial string Glyph { get; set; }

    [ObservableProperty]
    public partial string BrushKey { get; set; }

    public void Set(GameRecap recap)
    {
        var session = recap.Session;
        Name = session.Name;
        WhenText = $"{InsightDisplay.Time(session.Start)} · {MetricFormatter.DurationPrecise(session.Duration)}";
        Headline = recap.Headline;
        (Glyph, BrushKey) = HealthGlyphs.For(recap.Severity);
    }
}

/// <summary>The recap of the selected session.</summary>
public sealed partial class GameRecapViewModel : ObservableObject
{
    public GameRecapViewModel()
    {
        Name = Period = Headline = Summary = Coverage = Detection = Glyph = BrushKey = ComparisonNote = ExecutablePath = CpuLegend = GpuLegend = string.Empty;
    }

    public ObservableCollection<GameMetricItemViewModel> Metrics { get; } = [];

    public ObservableCollection<GameFindingItemViewModel> Findings { get; } = [];

    public ObservableCollection<GameComparisonItem> Comparison { get; } = [];

    public ObservableCollection<string> OtherApps { get; } = [];

    public ObservableCollection<string> GameProcesses { get; } = [];

    public ObservableCollection<string> Events { get; } = [];

    public ObservableCollection<string> NotAvailable { get; } = [];

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Period { get; set; }

    [ObservableProperty]
    public partial string Headline { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial string Coverage { get; set; }

    [ObservableProperty]
    public partial string Detection { get; set; }

    [ObservableProperty]
    public partial string ExecutablePath { get; set; }

    [ObservableProperty]
    public partial string Glyph { get; set; }

    [ObservableProperty]
    public partial string BrushKey { get; set; }

    [ObservableProperty]
    public partial string ComparisonNote { get; set; }

    [ObservableProperty]
    public partial bool HasComparison { get; set; }

    [ObservableProperty]
    public partial bool HasOtherApps { get; set; }

    [ObservableProperty]
    public partial bool HasGameProcesses { get; set; }

    [ObservableProperty]
    public partial bool HasEvents { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? CpuChart { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? GpuChart { get; set; }

    [ObservableProperty]
    public partial bool HasGpuChart { get; set; }

    [ObservableProperty]
    public partial string CpuLegend { get; set; }

    [ObservableProperty]
    public partial string GpuLegend { get; set; }

    public void Apply(GameRecap recap)
    {
        var session = recap.Session;
        Name = session.Name;
        Period = $"{InsightDisplay.Period(session.Start, session.End)} · {MetricFormatter.DurationPrecise(session.Duration)}";
        Headline = recap.Headline;
        Summary = recap.Summary;
        Coverage = recap.Coverage;
        Detection = $"Identified as a game: {session.DetectionEvidence} ({InsightDisplay.Text(session.Confidence).ToLowerInvariant()})";
        ExecutablePath = session.ExecutablePath;
        (Glyph, BrushKey) = HealthGlyphs.For(recap.Severity);

        Replace(Metrics, recap.Metrics.Select(m => new GameMetricItemViewModel(m)));
        Replace(Findings, recap.Findings.Select(f => new GameFindingItemViewModel(f)));
        Replace(Comparison, recap.Comparison);
        ComparisonNote = recap.ComparisonNote;
        HasComparison = recap.Comparison.Count > 0;
        Replace(OtherApps, session.BackgroundApps.Select(Describe));
        HasOtherApps = OtherApps.Count > 0;
        Replace(GameProcesses, session.AssociatedProcesses.Select(Describe));
        HasGameProcesses = GameProcesses.Count > 0;
        Replace(Events, session.Events.Select(e => $"{InsightDisplay.Time(e.Time)} — {e.Text}"));
        HasEvents = Events.Count > 0;
        Replace(NotAvailable, recap.NotAvailable);

        var window = session.Duration > TimeSpan.Zero ? session.Duration + TimeSpan.FromSeconds(session.TimelineStepSeconds) : TimeSpan.FromMinutes(1);
        var end = session.Timeline.Count > 0 ? session.Timeline[^1].Start + TimeSpan.FromSeconds(session.TimelineStepSeconds) : session.End;
        var label = MetricFormatter.DurationPrecise(session.Duration);
        CpuChart = new TimeSeriesData(Series(session, p => p.Cpu), Series(session, p => p.GameCpu), end, window, 100, "100%", label);
        CpuLegend = $"Whole PC (filled) and the game (line), {Step(session)}";
        var gpu = Series(session, p => p.Gpu);
        HasGpuChart = gpu.Count > 0;
        GpuChart = HasGpuChart ? new TimeSeriesData(gpu, Series(session, p => p.GameGpu), end, window, 100, "100%", label) : null;
        GpuLegend = session.GameGpu is null
            ? $"Busiest graphics adapter, {Step(session)} (GPU usage of the game: not available)"
            : $"Busiest graphics adapter (filled) and the game (line), {Step(session)}";
    }

    private static string Step(GameSession session) =>
        session.TimelineStepSeconds <= 60 ? "average per minute" : $"average per {MetricFormatter.DurationCompact(TimeSpan.FromSeconds(session.TimelineStepSeconds))}";

    private static List<MetricSample> Series(GameSession session, Func<GameTimelinePoint, double?> value) =>
        session.Timeline.Where(p => value(p) is not null).Select(p => new MetricSample(p.Start, value(p)!.Value)).ToList();

    private static string Describe(GameAppUsage app) =>
        string.Create(CultureInfo.CurrentCulture, $"{app.Name}: {MetricFormatter.Percent(app.CpuAverage, 1)} CPU on average (peak {MetricFormatter.Percent(app.CpuMaximum, 1)}), up to {MetricFormatter.Bytes(app.MemoryMaximumBytes)}");

    private static void Replace<T>(ObservableCollection<T> items, IEnumerable<T> values)
    {
        items.Clear();
        foreach (var value in values)
        {
            items.Add(value);
        }
    }
}

/// <summary>One line of the measurements table.</summary>
public sealed record GameMetricItemViewModel(string Name, string Average, string Maximum, string Note, bool IsAvailable)
{
    public GameMetricItemViewModel(GameRecapMetric metric)
        : this(metric.Name, metric.Average, metric.Maximum, metric.Note ?? string.Empty, metric.IsAvailable)
    {
    }

    public double Opacity => IsAvailable ? 1 : 0.6;
}

/// <summary>One finding of the recap, with its evidence.</summary>
public sealed record GameFindingItemViewModel
{
    public GameFindingItemViewModel(GameFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        Title = finding.Title;
        Description = finding.Description;
        Qualifier = finding.Qualifier ?? string.Empty;
        HasQualifier = finding.Qualifier is not null;
        Recommendation = finding.Recommendation ?? string.Empty;
        HasRecommendation = finding.Recommendation is not null;
        (Glyph, BrushKey) = HealthGlyphs.For(finding.Severity);
        ConfidenceText = InsightDisplay.Text(finding.Confidence);
        Evidence = finding.Evidence.Select(EvidenceItemViewModel.From).ToArray();
        HasEvidence = Evidence.Count > 0;
    }

    public string Title { get; }

    public string Description { get; }

    public string Qualifier { get; }

    public bool HasQualifier { get; }

    public string Recommendation { get; }

    public bool HasRecommendation { get; }

    public string Glyph { get; }

    public string BrushKey { get; }

    public string ConfidenceText { get; }

    public IReadOnlyList<EvidenceItemViewModel> Evidence { get; }

    public bool HasEvidence { get; }
}
