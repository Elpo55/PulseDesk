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
using Sysora.Localization;

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

    private readonly ReportExportService _export;

    public AlertsViewModel(
        UiMetricsHub hub,
        AlertService alerts,
        SettingsService settings,
        InsightNavigator navigator,
        NavigationService navigation,
        ReportExportService export,
        DispatcherQueue dispatcher)
        : base(hub)
    {
        _export = export;
        _alerts = alerts;
        _settings = settings;
        _navigator = navigator;
        _navigation = navigation;
        _dispatcher = dispatcher;
        Summary = RulesText = string.Empty;
    }

    public ObservableCollection<AlertItemViewModel> Items { get; } = [];

    public IReadOnlyList<string> Filters { get; } = [UiStrings.Alerts_Filter_All, UiStrings.Alerts_Filter_Active, UiStrings.Alerts_Filter_Resolved];

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

    [RelayCommand]
    private Task Export()
    {
        var alerts = _alerts.Alerts;
        return _export.ExportAsync(system => Sysora.Core.Reports.ReportBuilder.Alerts(alerts, DateTimeOffset.Now, system));
    }

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
            ? UiStrings.Alerts_NoneIn7Days
            : Text.Format(
                UiStrings.Alerts_Summary,
                Text.Plural(active, UiStrings.Count_ActiveAlert_One, UiStrings.Count_ActiveAlert_Other),
                unseen,
                Text.Plural(all.Count, Strings.Count_Alert_One, Strings.Count_Alert_Other));
        IsEmpty = list.Count == 0;

        var desired = list.Select(alert =>
        {
            if (!_items.TryGetValue(alert.Id, out var item))
            {
                item = new AlertItemViewModel(OnOpen, OnSeen, OnReplay, OnWhyNow);
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

    private void OnReplay(AlertItemViewModel item) => _navigator.OpenReplay(item.Since, item.Until);

    private void OnWhyNow(AlertItemViewModel item)
    {
        if (item.WhyNowMetric is { } metric)
        {
            _navigator.OpenWhyNow(metric);
        }
    }

    private static string Rules(SmartAlertSettings s) => string.Join(" · ",
    [
        Text.Format(UiStrings.Alerts_Rule_Cpu, MetricFormatter.Percent(s.CpuPercent), Text.Plural(s.CpuMinutes, Strings.Duration_Minute_One, Strings.Duration_Minute_Other)),
        Text.Format(UiStrings.Alerts_Rule_Memory, MetricFormatter.Percent(s.MemoryPercent), Text.Plural(s.MemoryMinutes, Strings.Duration_Minute_One, Strings.Duration_Minute_Other)),
        Text.Format(UiStrings.Alerts_Rule_Disk, MetricFormatter.Percent(s.DiskActivePercent), Text.Plural(s.DiskMinutes, Strings.Duration_Minute_One, Strings.Duration_Minute_Other)),
        Text.Format(UiStrings.Alerts_Rule_App, MetricFormatter.Percent(s.AppCpuPercent), Text.Plural(s.AppCpuMinutes, Strings.Duration_Minute_One, Strings.Duration_Minute_Other)),
        Text.Format(UiStrings.Alerts_Rule_Growth, s.MemoryGrowthPoints, Text.Plural(s.MemoryGrowthMinutes, Strings.Duration_Minute_One, Strings.Duration_Minute_Other)),
        s.UnusualActivity
            ? Text.Format(UiStrings.Alerts_Rule_Unusual, Text.Plural(s.UnusualMinutes, Strings.Duration_Minute_One, Strings.Duration_Minute_Other))
            : UiStrings.Alerts_Rule_UnusualOff,
        Text.Format(UiStrings.Alerts_Rule_Space, MetricFormatter.Percent(s.LowDiskFreePercent)),
    ]);
}

/// <summary>One alert.</summary>
public sealed partial class AlertItemViewModel : ObservableObject
{
    private readonly Action<AlertItemViewModel> _open;
    private readonly Action<AlertItemViewModel> _seen;
    private readonly Action<AlertItemViewModel>? _replay;
    private readonly Action<AlertItemViewModel>? _whyNow;

    public AlertItemViewModel(Action<AlertItemViewModel> open, Action<AlertItemViewModel> seen, Action<AlertItemViewModel>? replay = null, Action<AlertItemViewModel>? whyNow = null)
    {
        _open = open;
        _seen = seen;
        _replay = replay;
        _whyNow = whyNow;
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

    /// <summary>When the condition started (the period shown by "Show in Replay").</summary>
    public DateTimeOffset Since { get; private set; }

    /// <summary>When the condition ended, or was last observed.</summary>
    public DateTimeOffset Until { get; private set; }

    /// <summary>Metric "Why now?" can explain, for an alert still in progress.</summary>
    public Sysora.Core.Analysis.WhyNowMetric? WhyNowMetric { get; private set; }

    [ObservableProperty]
    public partial bool CanExplainWhyNow { get; set; }

    [RelayCommand]
    private void Open() => _open(this);

    [RelayCommand]
    private void Replay() => _replay?.Invoke(this);

    [RelayCommand]
    private void WhyNow() => _whyNow?.Invoke(this);

    public void Set(Alert alert)
    {
        Since = alert.Since != default ? alert.Since : alert.RaisedAt;
        Until = alert.ResolvedAt ?? alert.UpdatedAt;
        WhyNowMetric = alert.RuleId switch
        {
            "cpu.sustained" or "app.cpu" => Sysora.Core.Analysis.WhyNowMetric.Cpu,
            "memory.sustained" or "memory.growth" => Sysora.Core.Analysis.WhyNowMetric.Memory,
            "disk.busy" => Sysora.Core.Analysis.WhyNowMetric.Disk,
            "unusual" when alert.Key.EndsWith(".cpu", StringComparison.Ordinal) => Sysora.Core.Analysis.WhyNowMetric.Cpu,
            "unusual" when alert.Key.EndsWith(".memory", StringComparison.Ordinal) => Sysora.Core.Analysis.WhyNowMetric.Memory,
            "unusual" when alert.Key.EndsWith(".disk", StringComparison.Ordinal) => Sysora.Core.Analysis.WhyNowMetric.Disk,
            _ => null,
        };

        // "Why now?" explains the current measurements: offered only while the alert is ongoing.
        CanExplainWhyNow = WhyNowMetric is not null && alert.IsActive && _whyNow is not null;
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
            AlertStatus.New => (AlertStatusText.Label(AlertStatus.New), "StatusCriticalBrush"),
            AlertStatus.Seen => (AlertStatusText.Label(AlertStatus.Seen), "StatusInfoBrush"),
            _ => (AlertStatusText.Label(AlertStatus.Resolved), "StatusNormalBrush"),
        };
        IsNew = alert.Status == AlertStatus.New;

        var lasted = alert.Duration > TimeSpan.Zero ? " · " + Text.Format(UiStrings.Alerts_Lasted, MetricFormatter.DurationPrecise(alert.Duration)) : string.Empty;
        var since = alert.Since != default ? alert.Since : alert.RaisedAt;
        TimeText = alert.Status == AlertStatus.Resolved && alert.ResolvedAt is { } resolved
            ? $"{InsightDisplay.Time(since)} → {InsightDisplay.Time(resolved)}{lasted}"
            : Text.Format(UiStrings.Alerts_SinceOngoing, InsightDisplay.Time(since), lasted);
        if (alert.Occurrences > 1)
        {
            TimeText += " · " + Text.Plural(alert.Occurrences, UiStrings.Count_Occurrence_One, UiStrings.Count_Occurrence_Other);
        }

        Observed = Text.Format(Strings.Common_NameValue, alert.Metric, alert.Value);
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
