using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sysora.App.Services;
using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.Models;
using Sysora.Core.Reports;
using Sysora.Core.Timeline;
using Sysora.Localization;

namespace Sysora.App.ViewModels;

/// <summary>
/// Timeline: everything important that happened on the PC, in one list (applications, games, alerts, spikes, changes,
/// sleep, investigations), with the real timestamps and a way to open the context of each entry.
/// </summary>
public sealed partial class TimelineViewModel : PageViewModel
{
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EmptyRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly TimelineService _timeline;
    private readonly InsightNavigator _navigator;
    private readonly ReportExportService _export;
    private readonly ILogger<TimelineViewModel> _logger;
    private IReadOnlyList<TimelineItem> _items = [];
    private (DateTimeOffset From, DateTimeOffset To) _period;
    private CancellationTokenSource? _load;
    private long _lastLoad;

    public TimelineViewModel(UiMetricsHub hub, TimelineService timeline, InsightNavigator navigator, ReportExportService export, ILogger<TimelineViewModel> logger)
        : base(hub)
    {
        _timeline = timeline;
        _navigator = navigator;
        _export = export;
        _logger = logger;
        Summary = string.Empty;
        RangeIndex = 1;
        ShowApplications = ShowGames = ShowAlerts = ShowAnomalies = ShowChanges = ShowSystem = ShowInvestigations = true;
        NewestFirst = true;
    }

    public IReadOnlyList<string> Ranges { get; } =
    [
        Strings.Usual_Period_LastHour,
        Strings.Usual_Period_Today,
        AppImpactPeriodText.Label(AppImpactPeriod.Last24Hours),
        Strings.Usual_Period_7Days,
    ];

    /// <summary>Day headers and entries, in one flat list (so the list stays virtualized).</summary>
    public ObservableCollection<TimelineRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial int RangeIndex { get; set; }

    [ObservableProperty]
    public partial bool NewestFirst { get; set; }

    [ObservableProperty]
    public partial bool ShowApplications { get; set; }

    [ObservableProperty]
    public partial bool ShowGames { get; set; }

    [ObservableProperty]
    public partial bool ShowAlerts { get; set; }

    [ObservableProperty]
    public partial bool ShowAnomalies { get; set; }

    [ObservableProperty]
    public partial bool ShowChanges { get; set; }

    [ObservableProperty]
    public partial bool ShowSystem { get; set; }

    [ObservableProperty]
    public partial bool ShowInvestigations { get; set; }

    [ObservableProperty]
    public partial bool ShowMonitoring { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    partial void OnRangeIndexChanged(int value) => _ = LoadAsync();

    partial void OnNewestFirstChanged(bool value) => Show();

    partial void OnShowApplicationsChanged(bool value) => Show();

    partial void OnShowGamesChanged(bool value) => Show();

    partial void OnShowAlertsChanged(bool value) => Show();

    partial void OnShowAnomaliesChanged(bool value) => Show();

    partial void OnShowChangesChanged(bool value) => Show();

    partial void OnShowSystemChanged(bool value) => Show();

    partial void OnShowInvestigationsChanged(bool value) => Show();

    partial void OnShowMonitoringChanged(bool value) => Show();

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    [RelayCommand]
    private Task Export()
    {
        var items = Visible().ToArray();
        var (from, to) = _period;
        return _export.ExportAsync(system => ReportBuilder.Timeline(items, from, to, DateTimeOffset.Now, system));
    }

    protected override void OnActivated() => _ = LoadAsync();

    protected override void OnDeactivated() => _load?.Cancel();

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        // Recent ranges follow new events (sooner while still empty); longer ones are reloaded on demand.
        var interval = _items.Count == 0 ? EmptyRefreshInterval : LiveRefreshInterval;
        if (RangeIndex <= 2 && (updated & MetricKind.Cpu) != 0 && Environment.TickCount64 - _lastLoad > interval.TotalMilliseconds)
        {
            _ = LoadAsync();
        }
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
        _lastLoad = Environment.TickCount64;
        IsLoading = true;
        try
        {
            var to = DateTimeOffset.UtcNow;
            var from = RangeIndex switch
            {
                0 => to.AddHours(-1),
                1 => new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset).ToUniversalTime(),
                2 => to.AddDays(-1),
                _ => to.AddDays(-7),
            };
            var items = await _timeline.LoadAsync(from, to, load.Token);
            if (load.IsCancellationRequested)
            {
                return;
            }

            _items = items;
            _period = (from, to);
            Show();
        }
        catch (OperationCanceledException)
        {
            // Another range was selected.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "The timeline could not be loaded.");
            Summary = UiStrings.Timeline_LoadFailed;
        }
        finally
        {
            if (_load == load)
            {
                IsLoading = false;
            }
        }
    }

    private IEnumerable<TimelineItem> Visible() => _items.Where(i => i.Category switch
    {
        TimelineCategory.Application => ShowApplications,
        TimelineCategory.Game => ShowGames,
        TimelineCategory.Alert => ShowAlerts,
        TimelineCategory.Anomaly => ShowAnomalies,
        TimelineCategory.Change => ShowChanges,
        TimelineCategory.System => ShowSystem,
        TimelineCategory.Investigation => ShowInvestigations,
        _ => ShowMonitoring,
    });

    private void Show()
    {
        var visible = Visible().ToList();
        if (!NewestFirst)
        {
            visible.Reverse();
        }

        var rows = new List<TimelineRowViewModel>(visible.Count + 8);
        DateTime? day = null;
        foreach (var item in visible)
        {
            var local = item.Time.ToLocalTime().Date;
            if (day != local)
            {
                day = local;
                rows.Add(TimelineRowViewModel.Header(DayTitle(local)));
            }

            rows.Add(TimelineRowViewModel.Entry(item, Open, OpenReplay, Compare));
        }

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        IsEmpty = visible.Count == 0;
        Summary = _items.Count == 0
            ? UiStrings.Timeline_Empty
            : Text.Format(
                UiStrings.Timeline_Shown,
                Text.Plural(visible.Count, Strings.Count_Entry_One, Strings.Count_Entry_Other),
                _items.Count);
    }

    private static string DayTitle(DateTime day)
    {
        var today = DateTime.Now.Date;
        return day == today ? Strings.Usual_Period_Today : day == today.AddDays(-1) ? Strings.Usual_Period_Yesterday : day.ToString("D", CultureInfo.CurrentCulture);
    }

    private void Open(TimelineRowViewModel row) => _navigator.Open(row.Action, row.AppKey);

    private void OpenReplay(TimelineRowViewModel row) => _navigator.OpenReplayAt(row.Time);

    private void Compare(TimelineRowViewModel row) => _navigator.OpenCompare(new ComparisonRequest(ComparisonPreset.AroundTime) { Time = row.Time });
}

/// <summary>A day header or an entry of the timeline.</summary>
public sealed partial class TimelineRowViewModel
{
    private Action<TimelineRowViewModel>? _open;
    private Action<TimelineRowViewModel>? _replay;
    private Action<TimelineRowViewModel>? _compare;

    private TimelineRowViewModel()
    {
    }

    public bool IsHeader { get; private init; }

    public bool IsEntry => !IsHeader;

    public string HeaderText { get; private init; } = string.Empty;

    public DateTimeOffset Time { get; private init; }

    public string TimeText { get; private init; } = string.Empty;

    public string Title { get; private init; } = string.Empty;

    public string Detail { get; private init; } = string.Empty;

    public bool HasDetail => Detail.Length > 0;

    public string Source { get; private init; } = string.Empty;

    public string Glyph { get; private init; } = string.Empty;

    public string BrushKey { get; private init; } = "StatusUnknownBrush";

    public string CategoryText { get; private init; } = string.Empty;

    public DiagnosisAction Action { get; private init; }

    public string? AppKey { get; private init; }

    public string ActionLabel { get; private init; } = string.Empty;

    public bool HasAction => ActionLabel.Length > 0;

    /// <summary>Replay and comparisons need an exact time.</summary>
    public bool IsExact { get; private init; }

    public static TimelineRowViewModel Header(string text) => new() { IsHeader = true, HeaderText = text };

    public static TimelineRowViewModel Entry(TimelineItem item, Action<TimelineRowViewModel> open, Action<TimelineRowViewModel> replay, Action<TimelineRowViewModel> compare)
    {
        var (glyph, brush) = item.Category switch
        {
            TimelineCategory.Application => ("", "StatusUnknownBrush"),
            TimelineCategory.Game => ("", "StatusInfoBrush"),
            TimelineCategory.Alert => item.Severity >= DiagnosisSeverity.Warning ? (InsightDisplay.WarningGlyph, "StatusWarningBrush") : (InsightDisplay.NormalGlyph, "StatusNormalBrush"),
            TimelineCategory.Anomaly => item.Severity >= DiagnosisSeverity.Info ? ("", "StatusWarningBrush") : ("", "StatusNormalBrush"),
            TimelineCategory.Change => ("", "StatusInfoBrush"),
            TimelineCategory.Investigation => ("", "StatusInfoBrush"),
            TimelineCategory.System => ("", "StatusUnknownBrush"),
            _ => ("", "StatusUnknownBrush"),
        };
        var time = item.Time.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        return new TimelineRowViewModel
        {
            _open = open,
            _replay = replay,
            _compare = compare,
            Time = item.Time,
            TimeText = item.IsApproximate ? $"≈ {time}" : time,
            Title = item.Title,
            Detail = item.Detail ?? string.Empty,
            Source = item.Source,
            Glyph = glyph,
            BrushKey = brush,
            CategoryText = item.Category.ToString(),
            Action = item.Action,
            AppKey = item.AppKey,
            ActionLabel = item.Action is DiagnosisAction.Replay ? string.Empty : InsightNavigator.Label(item.Action),
            IsExact = !item.IsApproximate,
        };
    }

    [RelayCommand]
    private void Open() => _open?.Invoke(this);

    [RelayCommand]
    private void Replay() => _replay?.Invoke(this);

    [RelayCommand]
    private void Compare() => _compare?.Invoke(this);
}
