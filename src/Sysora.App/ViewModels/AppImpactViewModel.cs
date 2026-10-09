using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sysora.App.Controls;
using Sysora.App.Services;
using Sysora.Core.Analysis;
using Sysora.Core.Formatting;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Localization;

namespace Sysora.App.ViewModels;

/// <summary>
/// App Impact: "which application slows my PC down the most?". Ranks applications by an explainable impact
/// score over a period, and shows the history and evidence of the selected one.
/// </summary>
public sealed partial class AppImpactViewModel : PageViewModel
{
    private const int MaxRows = 50;
    private static readonly TimeSpan SessionRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly AppImpactService _service;
    private readonly ILogger<AppImpactViewModel> _logger;
    private readonly Dictionary<string, AppImpactResult> _results = new(StringComparer.Ordinal);
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _detailLoad;
    private long _lastRefresh;
    private bool _syncingSelection;
    private string? _pendingSelection;

    private readonly NavigationService _navigation;
    private readonly NavigationRequests _requests;
    private readonly ReportExportService _export;
    private AppImpactReport? _report;

    public AppImpactViewModel(UiMetricsHub hub, AppImpactService service, NavigationService navigation, NavigationRequests requests, ReportExportService export, ILogger<AppImpactViewModel> logger)
        : base(hub)
    {
        _navigation = navigation;
        _requests = requests;
        _export = export;
        _service = service;
        _logger = logger;
        Summary = Note = string.Empty;
        // Detail first: setting the selection raises its change handler, which uses Detail.
        Detail = new AppImpactDetailViewModel();
        SelectedIndex = -1;
    }

    public IReadOnlyList<string> Periods { get; } =
    [
        AppImpactPeriodText.Label(AppImpactPeriod.Session),
        AppImpactPeriodText.Label(AppImpactPeriod.Last24Hours),
        AppImpactPeriodText.Label(AppImpactPeriod.Last7Days),
    ];

    public ObservableCollection<AppImpactItemViewModel> Items { get; } = [];

    public AppImpactDetailViewModel Detail { get; }

    /// <summary>0 = this session, 1 = last 24 hours, 2 = last 7 days.</summary>
    [ObservableProperty]
    public partial int PeriodIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial string Note { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    public string ScoreFormula => AppImpactScore.Formula;

    private AppImpactPeriod Period => (AppImpactPeriod)Math.Clamp(PeriodIndex, 0, 2);

    partial void OnPeriodIndexChanged(int value) => _ = RefreshAsync();

    partial void OnSelectedIndexChanged(int value)
    {
        if (!_syncingSelection)
        {
            _ = ShowDetailAsync(value >= 0 && value < Items.Count ? Items[value].Key : null);
        }
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    /// <summary>
    /// Selects an application by key (used when another page links to it). If the ranking does not contain it
    /// yet, it is selected after the next refresh.
    /// </summary>
    public void RequestSelection(string appKey)
    {
        var index = Items.ToList().FindIndex(i => i.Key == appKey);
        if (index >= 0)
        {
            _pendingSelection = null;
            SelectedIndex = index;
        }
        else
        {
            _pendingSelection = appKey;
        }
    }

    protected override void OnActivated() => _ = RefreshAsync();

    protected override void OnDeactivated()
    {
        _load?.Cancel();
        _detailLoad?.Cancel();
    }

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        // The session view follows live data, but re-ranking every second would be distracting and wasteful.
        if (Period == AppImpactPeriod.Session
            && (updated & MetricKind.Processes) != 0
            && Environment.TickCount64 - _lastRefresh > SessionRefreshInterval.TotalMilliseconds
            && !IsLoading)
        {
            _ = RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        if (!IsActive)
        {
            return;
        }

        _load?.Cancel();
        _load?.Dispose();
        var load = _load = new CancellationTokenSource();
        _lastRefresh = Environment.TickCount64;
        IsLoading = true;
        try
        {
            var report = await _service.GetReportAsync(Period, load.Token);
            if (load.IsCancellationRequested)
            {
                return;
            }

            Apply(report);
        }
        catch (OperationCanceledException)
        {
            // A newer refresh replaced this one.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "App impact could not be computed.");
            Note = UiStrings.AppImpact_Failed;
        }
        finally
        {
            if (_load == load)
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private Task Export()
    {
        if (_report is not { } report)
        {
            return Task.CompletedTask;
        }

        return _export.ExportAsync(system => Sysora.Core.Reports.ReportBuilder.AppImpact(report, DateTimeOffset.Now, system));
    }

    /// <summary>"Why now?" for the resource that weighs the most in the selected application's score.</summary>
    [RelayCommand]
    private void WhyNow()
    {
        var key = SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex].Key : null;
        var memory = key is not null && _results.TryGetValue(key, out var result)
            && result.Score.Components.FirstOrDefault(c => c.Resource == "Memory")?.Normalized > result.Score.Components.FirstOrDefault(c => c.Resource == "CPU")?.Normalized;
        _requests.WhyNow(memory ? WhyNowMetric.Memory : WhyNowMetric.Cpu);
        _navigation.Navigate(AppPage.Diagnosis);
    }

    private void Apply(AppImpactReport report)
    {
        _report = report;
        var selectedKey = _pendingSelection ?? (SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex].Key : null);
        _pendingSelection = null;
        var top = report.Apps.Take(MaxRows).ToList();
        _results.Clear();
        foreach (var result in top)
        {
            _results[result.Usage.Identity.Key] = result;
        }

        var measured = MetricFormatter.DurationCompact(TimeSpan.FromSeconds(report.MonitoredSeconds));
        Summary = report.Apps.Count == 0
            ? Text.Format(UiStrings.AppImpact_Summary, InsightDisplay.Period(report.From, report.To), measured)
            : Text.Format(UiStrings.AppImpact_SummaryApps, InsightDisplay.Period(report.From, report.To), measured, Text.Plural(report.Apps.Count, UiStrings.Count_Application_One, UiStrings.Count_Application_Other));
        Note = report.Note ?? string.Empty;
        IsEmpty = top.Count == 0;

        _syncingSelection = true;
        try
        {
            CollectionSync.Resize(Items, top.Count, _ => new AppImpactItemViewModel(), (item, i) => item.Set(i + 1, top[i]));
            SelectedIndex = selectedKey is null ? -1 : top.FindIndex(r => r.Usage.Identity.Key == selectedKey);
        }
        finally
        {
            _syncingSelection = false;
        }

        // The selection is restored silently: show (or clear) its details explicitly.
        _ = ShowDetailAsync(SelectedIndex >= 0 ? selectedKey : null);
    }

    /// <remarks>Called without awaiting: every exception is handled here so none goes unobserved.</remarks>
    private async Task ShowDetailAsync(string? key)
    {
        _detailLoad?.Cancel();
        _detailLoad?.Dispose();
        _detailLoad = null;
        try
        {
            if (key is null || !_results.TryGetValue(key, out var result))
            {
                HasSelection = false;
                Detail.Clear();
                return;
            }

            HasSelection = true;
            Detail.Apply(result, Summary);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Application details could not be shown.");
            return;
        }

        var load = _detailLoad = new CancellationTokenSource();
        try
        {
            var timeline = await _service.GetTimelineAsync(key, Period, load.Token);
            if (!load.IsCancellationRequested)
            {
                Detail.ApplyTimeline(timeline, Period == AppImpactPeriod.Session ? UiStrings.AppImpact_LastHourPerMinute : Periods[PeriodIndex]);
            }
        }
        catch (OperationCanceledException)
        {
            // Another application was selected.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Application history could not be loaded.");
        }
    }
}

/// <summary>One row of the App Impact ranking.</summary>
public sealed partial class AppImpactItemViewModel : ObservableObject
{
    public AppImpactItemViewModel()
    {
        Key = Name = RankText = LevelText = ScoreText = CpuText = MemoryText = DiskText = DurationText = TrendGlyph = TrendText = Location = string.Empty;
        LevelBrushKey = "LevelLowBrush";
    }

    public string Key { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Location { get; set; }

    [ObservableProperty]
    public partial string RankText { get; set; }

    [ObservableProperty]
    public partial string LevelText { get; set; }

    [ObservableProperty]
    public partial string LevelBrushKey { get; set; }

    [ObservableProperty]
    public partial string ScoreText { get; set; }

    [ObservableProperty]
    public partial string CpuText { get; set; }

    [ObservableProperty]
    public partial string MemoryText { get; set; }

    [ObservableProperty]
    public partial string DiskText { get; set; }

    [ObservableProperty]
    public partial string DurationText { get; set; }

    [ObservableProperty]
    public partial string TrendGlyph { get; set; }

    [ObservableProperty]
    public partial string TrendText { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    public void Set(int rank, AppImpactResult result)
    {
        var usage = result.Usage;
        Key = usage.Identity.Key;
        Name = usage.Identity.Name;
        Location = usage.Identity.ExecutablePath ?? UiStrings.AppImpact_PathNotAccessible;
        RankText = rank.ToString(CultureInfo.CurrentCulture);
        LevelText = InsightDisplay.Text(result.Score.Level);
        LevelBrushKey = InsightDisplay.BrushKey(result.Score.Level);
        ScoreText = string.Create(CultureInfo.CurrentCulture, $"{result.Score.Value}");
        CpuText = MetricFormatter.Percent(usage.CpuAverage, 1);
        MemoryText = MetricFormatter.Bytes(usage.MemoryAverageBytes);
        DiskText = MetricFormatter.BytesPerSecond(usage.IoAverageBytesPerSecond);
        DurationText = MetricFormatter.DurationCompact(TimeSpan.FromSeconds(usage.ActiveSeconds));
        TrendGlyph = InsightDisplay.Glyph(InsightDisplay.Dominant(result.Trend));
        TrendText = InsightDisplay.Describe(result.Trend);
        IsRunning = usage.IsRunning;
    }
}

/// <summary>Details of the selected application: explanation, components, evidence and charts.</summary>
public sealed partial class AppImpactDetailViewModel : ObservableObject
{
    public AppImpactDetailViewModel() => Clear();

    public ObservableCollection<ImpactComponentViewModel> Components { get; } = [];

    public ObservableCollection<EvidenceItemViewModel> Evidence { get; } = [];

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Location { get; set; }

    [ObservableProperty]
    public partial string LevelText { get; set; }

    [ObservableProperty]
    public partial string LevelBrushKey { get; set; }

    [ObservableProperty]
    public partial string ScoreText { get; set; }

    [ObservableProperty]
    public partial string Explanation { get; set; }

    [ObservableProperty]
    public partial string ConfidenceText { get; set; }

    [ObservableProperty]
    public partial string PeakText { get; set; }

    [ObservableProperty]
    public partial string TrendText { get; set; }

    [ObservableProperty]
    public partial string LaunchesText { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? CpuChart { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? MemoryChart { get; set; }

    [ObservableProperty]
    public partial string ChartCaption { get; set; }

    [ObservableProperty]
    public partial bool HasChart { get; set; }

    public void Clear()
    {
        Name = Location = LevelText = ScoreText = Explanation = ConfidenceText = PeakText = TrendText = LaunchesText = StatusText = ChartCaption = string.Empty;
        LevelBrushKey = "LevelLowBrush";
        CpuChart = MemoryChart = null;
        HasChart = false;
        Components.Clear();
        Evidence.Clear();
    }

    public void Apply(AppImpactResult result, string period)
    {
        var usage = result.Usage;
        Name = usage.Identity.Name;
        Location = usage.Identity.ExecutablePath ?? UiStrings.AppImpact_PathProtected;
        LevelText = Text.Format(UiStrings.AppImpact_LevelImpact, InsightDisplay.Text(result.Score.Level).ToLower(CultureInfo.CurrentCulture));
        LevelBrushKey = InsightDisplay.BrushKey(result.Score.Level);
        ScoreText = Text.Format(UiStrings.AppImpact_RelativeScore, result.Score.Value);
        Explanation = result.Explanation;
        ConfidenceText = $"{InsightDisplay.Text(result.Confidence)} · {period}";
        PeakText = Text.Format(UiStrings.AppImpact_Peaks, MetricFormatter.Percent(usage.CpuMaximum, 1), MetricFormatter.Bytes(usage.MemoryMaximumBytes), MetricFormatter.BytesPerSecond(usage.IoMaximumBytesPerSecond));
        TrendText = InsightDisplay.Describe(result.Trend);
        LaunchesText = usage.Launches > 0
            ? Text.Plural(usage.Launches, UiStrings.AppImpact_Starts_One, UiStrings.AppImpact_Starts_Other)
            : UiStrings.AppImpact_NoStart;
        StatusText = usage.IsRunning
            ? Text.Format(UiStrings.AppImpact_RunningNow, Text.Plural(usage.InstanceCount, Strings.Count_Process_One, Strings.Count_Process_Other))
            : Text.Format(UiStrings.AppImpact_LastSeen, InsightDisplay.Time(usage.LastSeen));

        CollectionSync.Resize(Components, result.Score.Components.Count, _ => new ImpactComponentViewModel(), (item, i) => item.Set(result.Score.Components[i]));
        Evidence.Clear();
        foreach (var evidence in result.Evidence)
        {
            Evidence.Add(EvidenceItemViewModel.From(evidence));
        }
    }

    public void ApplyTimeline(AppUsageTimeline timeline, string caption)
    {
        if (timeline.Points.Count == 0)
        {
            CpuChart = MemoryChart = null;
            HasChart = false;
            ChartCaption = UiStrings.AppImpact_NoHistory;
            return;
        }

        // Points are plotted at the middle of their bucket.
        var half = timeline.BucketLength / 2;
        var window = timeline.To - timeline.From + half;
        var cpu = timeline.Points.Select(p => new MetricSample(p.Start + half, p.CpuAverage)).ToArray();
        var memory = timeline.Points.Select(p => new MetricSample(p.Start + half, p.MemoryAverageBytes)).ToArray();
        var cpuMaximum = ChartScale.NiceMaximum(cpu.Max(s => s.Value) * 1.1, 5);
        var memoryMaximum = ChartScale.NiceMaximum(memory.Max(s => s.Value) * 1.1, 64 * 1024 * 1024);
        CpuChart = new TimeSeriesData(cpu, null, timeline.To, window, cpuMaximum, MetricFormatter.Percent(cpuMaximum), caption);
        MemoryChart = new TimeSeriesData(memory, null, timeline.To, window, memoryMaximum, MetricFormatter.Bytes(memoryMaximum), caption);
        HasChart = true;
        ChartCaption = timeline.BucketLength.TotalMinutes >= 60
            ? UiStrings.AppImpact_ChartPerHour
            : Text.Format(UiStrings.AppImpact_ChartPerMinutes, Text.Plural((int)timeline.BucketLength.TotalMinutes, Strings.Duration_Minute_One, Strings.Duration_Minute_Other));
    }
}

/// <summary>One resource's part of the impact score.</summary>
public sealed partial class ImpactComponentViewModel : ObservableObject
{
    public ImpactComponentViewModel()
    {
        Resource = LevelText = Description = string.Empty;
        LevelBrushKey = "LevelLowBrush";
    }

    [ObservableProperty]
    public partial string Resource { get; set; }

    [ObservableProperty]
    public partial string LevelText { get; set; }

    [ObservableProperty]
    public partial string LevelBrushKey { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial double Percent { get; set; }

    public void Set(ImpactComponent component)
    {
        Resource = component.Label;
        LevelText = component.Resource == "Running time"
            ? component.Level switch
            {
                ImpactLevel.High => UiStrings.AppImpact_MostOfTheTime,
                ImpactLevel.Moderate => UiStrings.AppImpact_PartOfTheTime,
                _ => UiStrings.AppImpact_Briefly,
            }
            : InsightDisplay.Text(component.Level);
        LevelBrushKey = component.Resource == "Running time" ? "LevelModerateBrush" : InsightDisplay.BrushKey(component.Level);
        Description = component.Description;
        Percent = component.Normalized * 100;
    }
}
