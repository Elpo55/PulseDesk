using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Sysora.App.Controls;
using Sysora.App.Services;
using Sysora.Core.Analysis;
using Sysora.Core.Formatting;
using Sysora.Core.Gaming;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Settings;
using Sysora.Localization;

namespace Sysora.App.ViewModels;

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

    private readonly InsightNavigator _navigator;
    private readonly ReportExportService _export;

    public GamingViewModel(
        UiMetricsHub hub,
        GameSessionService games,
        SettingsService settings,
        NavigationService navigation,
        InsightNavigator navigator,
        ReportExportService export,
        DispatcherQueue dispatcher,
        ILogger<GamingViewModel> logger)
        : base(hub)
    {
        _navigator = navigator;
        _export = export;
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

    [RelayCommand]
    private Task ExportRecap()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _recaps.Count)
        {
            return Task.CompletedTask;
        }

        var recap = _recaps[SelectedIndex];
        return _export.ExportAsync(system => Sysora.Core.Reports.ReportBuilder.GameSession(recap, DateTimeOffset.Now, system));
    }

    [RelayCommand]
    private void CompareBeforeDuring() => CompareSelected(Sysora.Core.Analysis.ComparisonPreset.GameBeforeVsDuring);

    [RelayCommand]
    private void CompareBeforeAfter() => CompareSelected(Sysora.Core.Analysis.ComparisonPreset.GameBeforeVsAfter);

    [RelayCommand]
    private void CompareWithPrevious() => CompareSelected(Sysora.Core.Analysis.ComparisonPreset.GameVsPreviousGame);

    private void CompareSelected(Sysora.Core.Analysis.ComparisonPreset preset)
    {
        if (SelectedIndex >= 0 && SelectedIndex < _recaps.Count)
        {
            _navigator.OpenCompare(new Sysora.Core.Analysis.ComparisonRequest(preset) { SessionId = _recaps[SelectedIndex].Session.Id });
        }
    }

    /// <summary>The selected session is not a game: stop following this executable (its past sessions are kept).</summary>
    [RelayCommand]
    private void MarkSelectedNotAGame()
    {
        if (SelectedIndex >= 0 && SelectedIndex < _recaps.Count)
        {
            _games.MarkNotAGame(_recaps[SelectedIndex].Session.ExecutablePath);
            Note = Text.Format(UiStrings.Gaming_NotFollowed, _recaps[SelectedIndex].Session.Name);
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

        LiveTitle = Text.Format(UiStrings.Gaming_LiveTitle, live.Name, MetricFormatter.DurationPrecise(live.Duration));
        LiveDetail = $"{live.DetectionEvidence} ({InsightDisplay.Text(live.Confidence).ToLower(CultureInfo.CurrentCulture)}) · {live.ExecutablePath}";
        var parts = new List<string>();
        if (live.Cpu is { } cpu)
        {
            parts.Add(Text.Format(UiStrings.Gaming_LiveCpu, MetricFormatter.Percent(cpu.Average), MetricFormatter.Percent(cpu.Maximum)));
        }

        if (live.Gpu is { } gpu)
        {
            parts.Add(Text.Format(UiStrings.Gaming_LiveGpu, MetricFormatter.Percent(gpu.Average), MetricFormatter.Percent(gpu.Maximum)));
        }

        if (live.Memory is { } memory)
        {
            parts.Add(Text.Format(UiStrings.Gaming_LiveMemory, MetricFormatter.Percent(memory.Average)));
        }

        if (live.GameMemoryBytes is { } gameMemory)
        {
            parts.Add(Text.Format(UiStrings.Gaming_LiveGameMemory, MetricFormatter.Bytes(gameMemory.Average)));
        }

        LiveStats = parts.Count > 0 ? string.Join(" · ", parts) : UiStrings.Gaming_Collecting;
        LiveOverhead = live.SelfCpu is { } self
            ? Text.Format(UiStrings.Gaming_SelfCpu, MetricFormatter.Percent(self.Average, 2))
            : UiStrings.Gaming_FpsNotAvailable;
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
                ? UiStrings.Gaming_NoSessionYet
                : Text.Format(
                    UiStrings.Gaming_Summary,
                    Text.Plural(recaps.Length, UiStrings.Count_Session_One, UiStrings.Count_Session_Other),
                    MetricFormatter.DurationCompact(TimeSpan.FromSeconds(recaps.Sum(r => r.Session.Duration.TotalSeconds))));

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
            ErrorText = Text.Format(UiStrings.Gaming_LoadFailed, ex.Message);
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
        Detection = Text.Format(UiStrings.Gaming_Detection, session.DetectionEvidence, InsightDisplay.Text(session.Confidence).ToLower(CultureInfo.CurrentCulture));
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
        CpuLegend = Text.Format(UiStrings.Gaming_CpuLegend, Step(session));
        var gpu = Series(session, p => p.Gpu);
        HasGpuChart = gpu.Count > 0;
        GpuChart = HasGpuChart ? new TimeSeriesData(gpu, Series(session, p => p.GameGpu), end, window, 100, "100%", label) : null;
        GpuLegend = session.GameGpu is null
            ? Text.Format(UiStrings.Gaming_GpuLegendNoGame, Step(session))
            : Text.Format(UiStrings.Gaming_GpuLegend, Step(session));
    }

    private static string Step(GameSession session) =>
        session.TimelineStepSeconds <= 60
            ? UiStrings.Gaming_StepMinute
            : Text.Format(UiStrings.Gaming_StepOther, MetricFormatter.DurationCompact(TimeSpan.FromSeconds(session.TimelineStepSeconds)));

    private static List<MetricSample> Series(GameSession session, Func<GameTimelinePoint, double?> value) =>
        session.Timeline.Where(p => value(p) is not null).Select(p => new MetricSample(p.Start, value(p)!.Value)).ToList();

    private static string Describe(GameAppUsage app) =>
        Text.Format(UiStrings.Gaming_AppLine, app.Name, MetricFormatter.Percent(app.CpuAverage, 1), MetricFormatter.Percent(app.CpuMaximum, 1), MetricFormatter.Bytes(app.MemoryMaximumBytes));

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
