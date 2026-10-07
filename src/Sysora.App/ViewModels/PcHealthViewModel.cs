using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sysora.App.Services;
using Sysora.Core.Analysis;
using Sysora.Core.Health;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;
using Sysora.Core.Reports;
using Sysora.Core.Settings;

namespace Sysora.App.ViewModels;

/// <summary>PC Health: "in what state is my PC?" as a score out of 100, explained area by area.</summary>
public sealed partial class PcHealthViewModel : PageViewModel
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly PcHealthService _health;
    private readonly RecurringProblemService _recurring;
    private readonly SettingsService _settings;
    private readonly InsightNavigator _navigator;
    private readonly NavigationService _navigation;
    private readonly ReportExportService _export;
    private readonly ILogger<PcHealthViewModel> _logger;
    private readonly Dictionary<PcHealthArea, HealthComponentItemViewModel> _items = [];
    private PcHealthReport _report = PcHealthReport.Empty;
    private long _lastRefresh;
    private bool _refreshing;

    public PcHealthViewModel(UiMetricsHub hub, PcHealthService health, RecurringProblemService recurring, SettingsService settings, InsightNavigator navigator, NavigationService navigation, ReportExportService export, ILogger<PcHealthViewModel> logger)
        : base(hub)
    {
        _health = health;
        _recurring = recurring;
        _settings = settings;
        _navigator = navigator;
        _navigation = navigation;
        _export = export;
        _logger = logger;
        ScoreText = "—";
        GradeText = PcHealthReport.GradeText(PcHealthGrade.Unknown);
        GradeBrushKey = "StatusUnknownBrush";
        GradeGlyph = InsightDisplay.InfoGlyph;
        Summary = PcHealthReport.Empty.Summary;
        UpdatedText = RecurringSummary = SelfImpactHeadline = SelfImpactText = string.Empty;
        Method = PcHealthScorer.MethodText;
    }

    public ObservableCollection<HealthComponentItemViewModel> Components { get; } = [];

    public ObservableCollection<RecurringItemViewModel> Recurring { get; } = [];

    public ObservableCollection<string> NotAvailable { get; } = [];

    public string Method { get; }

    [ObservableProperty]
    public partial string ScoreText { get; set; }

    [ObservableProperty]
    public partial double ScoreValue { get; set; }

    [ObservableProperty]
    public partial bool HasScore { get; set; }

    [ObservableProperty]
    public partial string GradeText { get; set; }

    [ObservableProperty]
    public partial string GradeBrushKey { get; set; }

    [ObservableProperty]
    public partial string GradeGlyph { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial string UpdatedText { get; set; }

    [ObservableProperty]
    public partial string RecurringSummary { get; set; }

    [ObservableProperty]
    public partial bool HasRecurring { get; set; }

    [ObservableProperty]
    public partial string SelfImpactHeadline { get; set; }

    [ObservableProperty]
    public partial string SelfImpactText { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [RelayCommand]
    private Task Refresh() => RefreshAsync(forceRecurring: true);

    [RelayCommand]
    private Task Export()
    {
        var report = _report;
        var self = SelfImpactAssessor.Assess(Hub.Monitor.SelfUsage, Hub.Monitor.ScheduleInfo, _settings.Current.Monitoring.MaxSelfCpuPercent);
        return _export.ExportAsync(system => ReportBuilder.PcHealth(report, self, DateTimeOffset.Now, system));
    }

    [RelayCommand]
    private void OpenSettings() => _navigation.Navigate(AppPage.Settings);

    protected override void OnActivated() => _ = RefreshAsync(forceRecurring: false);

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        if ((updated & MetricKind.Cpu) != 0 && Environment.TickCount64 - _lastRefresh > RefreshInterval.TotalMilliseconds)
        {
            _ = RefreshAsync(forceRecurring: false);
        }
    }

    private async Task RefreshAsync(bool forceRecurring)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        IsRefreshing = true;
        _lastRefresh = Environment.TickCount64;
        try
        {
            if (forceRecurring)
            {
                await _recurring.GetAsync(force: true, CancellationToken.None);
            }

            var report = await _health.RefreshAsync(CancellationToken.None);
            if (IsActive)
            {
                Apply(report);
                ApplyRecurring(_recurring.Latest);
                ApplySelfImpact();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "The PC Health score could not be computed.");
            Summary = "The score could not be computed. See the log for details.";
        }
        finally
        {
            _refreshing = false;
            IsRefreshing = false;
        }
    }

    private void Apply(PcHealthReport report)
    {
        _report = report;
        HasScore = report.Score is not null;
        ScoreText = report.Score is { } score ? score.ToString(CultureInfo.CurrentCulture) : "—";
        ScoreValue = report.Score ?? 0;
        GradeText = PcHealthReport.GradeText(report.Grade);
        GradeBrushKey = HealthDisplay.BrushKey(report.Grade);
        GradeGlyph = report.Grade switch
        {
            PcHealthGrade.Good => InsightDisplay.NormalGlyph,
            PcHealthGrade.Fair => InsightDisplay.InfoGlyph,
            PcHealthGrade.NeedsAttention => InsightDisplay.WarningGlyph,
            PcHealthGrade.Poor => InsightDisplay.CriticalGlyph,
            _ => InsightDisplay.InfoGlyph,
        };
        Summary = report.Summary;
        UpdatedText = report.From is { } from && report.To is { } to
            ? $"Based on {InsightDisplay.Period(from, to)} · {report.SampleCount:N0} measurements · updated {InsightDisplay.Time(report.Timestamp)}"
            : string.Empty;

        var items = report.Components.Select(c =>
        {
            if (!_items.TryGetValue(c.Area, out var item))
            {
                item = new HealthComponentItemViewModel(i => _navigator.Open(i.Action));
                _items[c.Area] = item;
            }

            item.Set(c);
            return item;
        }).ToList();
        if (!Components.SequenceEqual(items))
        {
            Components.Clear();
            foreach (var item in items)
            {
                Components.Add(item);
            }
        }

        if (!NotAvailable.SequenceEqual(report.NotAvailable))
        {
            NotAvailable.Clear();
            foreach (var line in report.NotAvailable)
            {
                NotAvailable.Add(line);
            }
        }
    }

    private void ApplyRecurring(RecurringProblemReport report)
    {
        RecurringSummary = report.Time == DateTimeOffset.MinValue ? "Not analyzed yet." : report.Summary;
        Recurring.Clear();
        foreach (var problem in report.Problems)
        {
            Recurring.Add(new RecurringItemViewModel(problem, item => _navigator.Open(item.Action, item.AppKey)));
        }

        HasRecurring = Recurring.Count > 0;
    }

    private void ApplySelfImpact()
    {
        var monitor = Hub.Monitor;
        var report = SelfImpactAssessor.Assess(monitor.SelfUsage, monitor.ScheduleInfo, _settings.Current.Monitoring.MaxSelfCpuPercent);
        SelfImpactHeadline = report.Headline;
        SelfImpactText = report.Level == SelfImpactLevel.Unknown
            ? "Sysora measures its own usage every ten seconds."
            : string.Join(" · ", report.Items.Take(2).Select(i => $"{i.Label} {i.Value}")) + (report.Warnings.Count > 0 ? $" · {report.Warnings[0]}" : string.Empty);
    }
}
