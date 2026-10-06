using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Sysora.App.Services;
using Sysora.Core.Alerts;
using Sysora.Core.Formatting;
using Sysora.Core.Models;
using Sysora.Core.Settings;

namespace Sysora.App.ViewModels;

/// <summary>Alerts: lasting or unusual conditions, with their status (new, seen, resolved) and explanation.</summary>
public sealed partial class AlertsViewModel : PageViewModel
{
    private readonly AlertService _alerts;
    private readonly SettingsService _settings;
    private readonly InsightNavigator _navigator;
    private readonly NavigationService _navigation;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<Guid, AlertItemViewModel> _items = [];
    private bool _refreshQueued;

    public AlertsViewModel(
        UiMetricsHub hub,
        AlertService alerts,
        SettingsService settings,
        InsightNavigator navigator,
        NavigationService navigation,
        DispatcherQueue dispatcher)
        : base(hub)
    {
        _alerts = alerts;
        _settings = settings;
        _navigator = navigator;
        _navigation = navigation;
        _dispatcher = dispatcher;
        Summary = RulesText = string.Empty;
    }

    public ObservableCollection<AlertItemViewModel> Items { get; } = [];

    public IReadOnlyList<string> Filters { get; } = ["All", "Active", "Resolved"];

    /// <summary>0 = all, 1 = active (new or seen), 2 = resolved.</summary>
    [ObservableProperty]
    public partial int FilterIndex { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial string RulesText { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsDisabled { get; set; }

    partial void OnFilterIndexChanged(int value) => Refresh();

    [RelayCommand]
    private void MarkAllSeen() => _alerts.MarkAllSeen();

    [RelayCommand]
    private void OpenSettings() => _navigation.Navigate(AppPage.Settings);

    protected override void OnActivated()
    {
        _alerts.Changed += OnAlertsChanged;
        Refresh();
    }

    protected override void OnDeactivated() => _alerts.Changed -= OnAlertsChanged;

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        // Alerts have their own change notifications.
    }

    private void OnAlertsChanged(object? sender, EventArgs e)
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _refreshQueued = false;
            if (IsActive)
            {
                Refresh();
            }
        });
    }

    private void Refresh()
    {
        var settings = _settings.Current.SmartAlerts;
        IsDisabled = !settings.Enabled;
        RulesText = Rules(settings);

        var all = _alerts.Alerts;
        var shown = FilterIndex switch
        {
            1 => all.Where(a => a.IsActive),
            2 => all.Where(a => !a.IsActive),
            _ => all,
        };
        var list = shown.ToList();

        var active = all.Count(a => a.IsActive);
        var unseen = all.Count(a => a.Status == AlertStatus.New);
        Summary = all.Count == 0
            ? "No alert recorded in the last 7 days."
            : $"{MetricFormatter.Plural(active, "active alert")} · {unseen.ToString(CultureInfo.CurrentCulture)} new · {MetricFormatter.Plural(all.Count, "alert")} in the last 7 days";
        IsEmpty = list.Count == 0;

        var desired = list.Select(alert =>
        {
            if (!_items.TryGetValue(alert.Id, out var item))
            {
                item = new AlertItemViewModel(OnOpen, OnSeen);
                _items[alert.Id] = item;
            }

            item.Set(alert);
            return item;
        }).ToList();

        for (var i = 0; i < desired.Count; i++)
        {
            var index = Items.IndexOf(desired[i]);
            if (index == i)
            {
                continue;
            }

            if (index > i)
            {
                Items.Move(index, i);
            }
            else
            {
                Items.Insert(i, desired[i]);
            }
        }

        while (Items.Count > desired.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        var ids = all.Select(a => a.Id).ToHashSet();
        foreach (var stale in _items.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _items.Remove(stale);
        }
    }

    private void OnOpen(AlertItemViewModel item)
    {
        _alerts.MarkSeen(item.Id);
        _navigator.Open(item.Action, item.AppKey);
    }

    private void OnSeen(AlertItemViewModel item) => _alerts.MarkSeen(item.Id);

    private static string Rules(SmartAlertSettings s) => string.Join(" · ",
    [
        $"CPU above {MetricFormatter.Percent(s.CpuPercent)} for {MetricFormatter.Plural(s.CpuMinutes, "minute")}",
        $"memory above {MetricFormatter.Percent(s.MemoryPercent)} for {MetricFormatter.Plural(s.MemoryMinutes, "minute")}",
        $"disk busy above {MetricFormatter.Percent(s.DiskActivePercent)} for {MetricFormatter.Plural(s.DiskMinutes, "minute")}",
        $"one application above {MetricFormatter.Percent(s.AppCpuPercent)} CPU for {MetricFormatter.Plural(s.AppCpuMinutes, "minute")}",
        string.Create(CultureInfo.CurrentCulture, $"memory rising {s.MemoryGrowthPoints:0} points in {s.MemoryGrowthMinutes} minutes"),
        s.UnusualActivity ? $"activity above your usual range for {MetricFormatter.Plural(s.UnusualMinutes, "minute")}" : "unusual activity off",
        string.Create(CultureInfo.CurrentCulture, $"less than {s.LowDiskFreePercent:0}% free on the system disk"),
    ]);
}

/// <summary>One alert.</summary>
public sealed partial class AlertItemViewModel : ObservableObject
{
    private readonly Action<AlertItemViewModel> _open;
    private readonly Action<AlertItemViewModel> _seen;

    public AlertItemViewModel(Action<AlertItemViewModel> open, Action<AlertItemViewModel> seen)
    {
        _open = open;
        _seen = seen;
        Title = Glyph = BrushKey = StatusText = StatusBrushKey = TimeText = Observed = ContextText = Explanation = Recommendation = ActionLabel = string.Empty;
    }

    public ObservableCollection<EvidenceItemViewModel> Evidence { get; } = [];

    public Guid Id { get; private set; }

    public string? AppKey { get; private set; }

    public Core.Diagnosis.DiagnosisAction Action { get; private set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Glyph { get; set; }

    [ObservableProperty]
    public partial string BrushKey { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial string StatusBrushKey { get; set; }

    [ObservableProperty]
    public partial string TimeText { get; set; }

    [ObservableProperty]
    public partial string Observed { get; set; }

    [ObservableProperty]
    public partial string ContextText { get; set; }

    [ObservableProperty]
    public partial string Explanation { get; set; }

    [ObservableProperty]
    public partial string Recommendation { get; set; }

    [ObservableProperty]
    public partial bool HasRecommendation { get; set; }

    [ObservableProperty]
    public partial string ActionLabel { get; set; }

    [ObservableProperty]
    public partial bool HasAction { get; set; }

    [ObservableProperty]
    public partial bool IsNew { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        // Opening an alert's details means it has been seen.
        if (value && IsNew)
        {
            _seen(this);
        }
    }

    [RelayCommand]
    private void Open() => _open(this);

    public void Set(Alert alert)
    {
        Id = alert.Id;
        AppKey = alert.AppKey;
        Action = alert.Action;
        Title = alert.Title;
        (Glyph, BrushKey) = alert.Severity switch
        {
            AlertSeverity.Critical => (InsightDisplay.CriticalGlyph, "StatusCriticalBrush"),
            AlertSeverity.Warning => (InsightDisplay.WarningGlyph, "StatusWarningBrush"),
            _ => (InsightDisplay.InfoGlyph, "StatusInfoBrush"),
        };
        (StatusText, StatusBrushKey) = alert.Status switch
        {
            AlertStatus.New => ("New", "StatusCriticalBrush"),
            AlertStatus.Seen => ("Seen", "StatusInfoBrush"),
            _ => ("Resolved", "StatusNormalBrush"),
        };
        IsNew = alert.Status == AlertStatus.New;

        var lasted = alert.Duration > TimeSpan.Zero ? $" · lasted {MetricFormatter.DurationPrecise(alert.Duration)}" : string.Empty;
        var since = alert.Since != default ? alert.Since : alert.RaisedAt;
        TimeText = alert.Status == AlertStatus.Resolved && alert.ResolvedAt is { } resolved
            ? $"{InsightDisplay.Time(since)} → {InsightDisplay.Time(resolved)}{lasted}"
            : $"Since {InsightDisplay.Time(since)} · ongoing{lasted}";
        if (alert.Occurrences > 1)
        {
            TimeText += $" · {alert.Occurrences.ToString(CultureInfo.CurrentCulture)} occurrences";
        }

        Observed = $"{alert.Metric}: {alert.Value}";
        ContextText = alert.Context;
        Explanation = alert.Explanation;
        Recommendation = alert.Recommendation ?? string.Empty;
        HasRecommendation = alert.Recommendation is not null;
        ActionLabel = InsightNavigator.Label(alert.Action);
        HasAction = ActionLabel.Length > 0;

        var evidence = alert.Evidence.Select(EvidenceItemViewModel.From).ToList();
        if (!Evidence.SequenceEqual(evidence))
        {
            Evidence.Clear();
            foreach (var item in evidence)
            {
                Evidence.Add(item);
            }
        }
    }
}
