using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PulseDesk.App.Controls;
using PulseDesk.App.Services;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.Metrics;
using PulseDesk.Core.Models;

namespace PulseDesk.App.ViewModels;

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

    public AppImpactViewModel(UiMetricsHub hub, AppImpactService service, ILogger<AppImpactViewModel> logger)
        : base(hub)
    {
        _service = service;
        _logger = logger;
        Summary = Note = string.Empty;
        // Detail first: setting the selection raises its change handler, which uses Detail.
        Detail = new AppImpactDetailViewModel();
        SelectedIndex = -1;
    }

    public IReadOnlyList<string> Periods { get; } = ["This session", "Last 24 hours", "Last 7 days"];

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
            Note = "App impact could not be computed. See the log for details.";
        }
        finally
        {
            if (_load == load)
            {
                IsLoading = false;
            }
        }
    }

    private void Apply(AppImpactReport report)
    {
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
            ? $"{InsightDisplay.Period(report.From, report.To)} · {measured} measured"
            : $"{InsightDisplay.Period(report.From, report.To)} · {measured} measured · {MetricFormatter.Plural(report.Apps.Count, "application")}";
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

    private async Task ShowDetailAsync(string? key)
    {
        _detailLoad?.Cancel();
        _detailLoad?.Dispose();
        _detailLoad = null;
        if (key is null || !_results.TryGetValue(key, out var result))
        {
            HasSelection = false;
            Detail.Clear();
            return;
        }

        HasSelection = true;
        Detail.Apply(result, Summary);
        var load = _detailLoad = new CancellationTokenSource();
        try
        {
            var timeline = await _service.GetTimelineAsync(key, Period, load.Token);
            if (!load.IsCancellationRequested)
            {
                Detail.ApplyTimeline(timeline, Period == AppImpactPeriod.Session ? "Last hour, per minute" : Periods[PeriodIndex]);
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
        Location = usage.Identity.ExecutablePath ?? "Path not accessible: identified by name only";
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
        Location = usage.Identity.ExecutablePath ?? "Executable path not accessible (protected process): identified by name only.";
        LevelText = $"{InsightDisplay.Text(result.Score.Level)} impact";
        LevelBrushKey = InsightDisplay.BrushKey(result.Score.Level);
        ScoreText = string.Create(CultureInfo.CurrentCulture, $"Relative score {result.Score.Value}/100");
        Explanation = result.Explanation;
        ConfidenceText = $"{InsightDisplay.Text(result.Confidence)} · {period}";
        PeakText = $"Peak CPU {MetricFormatter.Percent(usage.CpuMaximum, 1)} · peak memory {MetricFormatter.Bytes(usage.MemoryMaximumBytes)} · peak I/O {MetricFormatter.BytesPerSecond(usage.IoMaximumBytesPerSecond)}";
        TrendText = InsightDisplay.Describe(result.Trend);
        LaunchesText = usage.Launches > 0 ? MetricFormatter.Plural(usage.Launches, "start") + " observed" : "No start observed in this period";
        StatusText = usage.IsRunning
            ? $"Running now · {MetricFormatter.Plural(usage.InstanceCount, "process", "processes")}"
            : $"Last seen {InsightDisplay.Time(usage.LastSeen)}";

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
            ChartCaption = "No history for this application in this period.";
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
        ChartCaption = $"Average while running, per {(timeline.BucketLength.TotalMinutes >= 60 ? "hour" : MetricFormatter.Plural((int)timeline.BucketLength.TotalMinutes, "minute"))}. Gaps: not running or not measured.";
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
        Resource = component.Resource;
        LevelText = component.Resource == "Running time"
            ? component.Level switch { ImpactLevel.High => "Most of the time", ImpactLevel.Moderate => "Part of the time", _ => "Briefly" }
            : InsightDisplay.Text(component.Level);
        LevelBrushKey = component.Resource == "Running time" ? "LevelModerateBrush" : InsightDisplay.BrushKey(component.Level);
        Description = component.Description;
        Percent = component.Normalized * 100;
    }
}
