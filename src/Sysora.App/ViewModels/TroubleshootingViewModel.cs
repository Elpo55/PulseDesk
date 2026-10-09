using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Sysora.App.Controls;
using Sysora.App.Services;
using Sysora.Core.Formatting;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Reports;
using Sysora.Core.Troubleshooting;
using Sysora.Localization;

namespace Sysora.App.ViewModels;

/// <summary>A previous investigation in the list.</summary>
public sealed record InvestigationOption(Guid Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Troubleshooting: investigate a problem for a limited time with detailed collection, then read what was measured,
/// what it suggests and what could not be known. Collection returns to normal by itself at the end.
/// </summary>
public sealed partial class TroubleshootingViewModel : PageViewModel
{
    private readonly TroubleshootingService _service;
    private readonly NavigationRequests _requests;
    private readonly InsightNavigator _navigator;
    private readonly ReportExportService _export;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly ILogger<TroubleshootingViewModel> _logger;
    private readonly Dictionary<Guid, TroubleshootingReport> _reports = [];
    private TroubleshootingReport? _shown;
    private bool _selecting;

    public TroubleshootingViewModel(UiMetricsHub hub, TroubleshootingService service, NavigationRequests requests, InsightNavigator navigator, ReportExportService export, DispatcherQueue dispatcher, ILogger<TroubleshootingViewModel> logger)
        : base(hub)
    {
        _service = service;
        _requests = requests;
        _navigator = navigator;
        _export = export;
        _dispatcher = dispatcher;
        _logger = logger;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => UpdateStatus(_service.Status);
        Duration = Durations[2];
        StatusText = ElapsedText = CountsText = Headline = Summary = PeriodText = string.Empty;
        _service.StatusChanged += (_, status) => _dispatcher.TryEnqueue(() =>
        {
            if (IsActive)
            {
                UpdateStatus(status);
            }
        });
        _service.Completed += (_, report) => _dispatcher.TryEnqueue(() =>
        {
            _reports[report.Id] = report;
            if (IsActive)
            {
                _ = LoadReportsAsync(report.Id);
            }
        });
        _requests.Requested += (_, kind) =>
        {
            if (kind == NavigationRequestKind.TroubleshootingReport && IsActive)
            {
                _ = LoadReportsAsync(_requests.TakeTroubleshootingReport());
            }
        };
    }

    public IReadOnlyList<IntervalOption> Durations { get; } = TroubleshootingService.DurationOptions.Select(d => IntervalOption.Of((int)d.TotalMilliseconds)).ToArray();

    public ObservableCollection<InvestigationOption> Investigations { get; } = [];

    public ObservableCollection<TroubleshootingMetric> Metrics { get; } = [];

    public ObservableCollection<FindingItemViewModel> Anomalies { get; } = [];

    public ObservableCollection<FindingItemViewModel> Correlations { get; } = [];

    public ObservableCollection<FindingItemViewModel> Unknowns { get; } = [];

    public ObservableCollection<InvestigationAppViewModel> Applications { get; } = [];

    public ObservableCollection<string> Events { get; } = [];

    public ObservableCollection<string> Recommendations { get; } = [];

    [ObservableProperty]
    public partial IntervalOption Duration { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial bool IsRunning { get; set; }

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial string ElapsedText { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string CountsText { get; set; }

    [ObservableProperty]
    public partial InvestigationOption? SelectedInvestigation { get; set; }

    [ObservableProperty]
    public partial bool HasReport { get; set; }

    [ObservableProperty]
    public partial bool HasInvestigations { get; set; }

    [ObservableProperty]
    public partial string Headline { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial string PeriodText { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? CpuChart { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? MemoryChart { get; set; }

    partial void OnSelectedInvestigationChanged(InvestigationOption? value)
    {
        if (!_selecting && value is not null && _reports.TryGetValue(value.Id, out var report))
        {
            Show(report);
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Start()
    {
        if (_service.Start(TimeSpan.FromMilliseconds(Duration.Milliseconds)))
        {
            UpdateStatus(_service.Status);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task Stop()
    {
        try
        {
            await _service.StopAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "The investigation could not be stopped cleanly.");
        }
    }

    [RelayCommand]
    private Task Export()
    {
        if (_shown is not { } report)
        {
            return Task.CompletedTask;
        }

        return _export.ExportAsync(system => ReportBuilder.Troubleshooting(report, DateTimeOffset.Now, system));
    }

    [RelayCommand]
    private void OpenInReplay()
    {
        if (_shown is { } report)
        {
            _navigator.OpenReplay(report.Start, report.End);
        }
    }

    protected override void OnActivated()
    {
        UpdateStatus(_service.Status);
        _ = LoadReportsAsync(_requests.TakeTroubleshootingReport());
    }

    protected override void OnDeactivated() => _timer.Stop();

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        // Progress is driven by the investigation's own status and a one-second timer while it runs.
    }

    private void UpdateStatus(TroubleshootingStatus status)
    {
        IsRunning = status.IsRunning;
        if (!status.IsRunning || status.Start is not { } start)
        {
            _timer.Stop();
            StatusText = UiStrings.Troubleshooting_Idle;
            ElapsedText = CountsText = string.Empty;
            Progress = 0;
            return;
        }

        if (!_timer.IsRunning)
        {
            _timer.Start();
        }

        var elapsed = DateTimeOffset.UtcNow - start;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        StatusText = UiStrings.Troubleshooting_Active;
        ElapsedText = Text.Format(UiStrings.Troubleshooting_Elapsed, MetricFormatter.DurationPrecise(elapsed), MetricFormatter.DurationPrecise(status.Planned));
        Progress = Math.Clamp(elapsed / status.Planned * 100, 0, 100);
        CountsText = string.Join(
            " · ",
            Text.Plural(status.Samples, UiStrings.Count_MeasurementN0_One, UiStrings.Count_MeasurementN0_Other),
            Text.Plural(status.Events, UiStrings.Count_Event_One, UiStrings.Count_Event_Other),
            Text.Plural(status.Alerts, Strings.Count_Alert_One, Strings.Count_Alert_Other));
    }

    private async Task LoadReportsAsync(Guid? select)
    {
        try
        {
            foreach (var report in await _service.GetReportsAsync(CancellationToken.None))
            {
                _reports[report.Id] = report;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Previous investigations could not be loaded.");
        }

        var ordered = _reports.Values.OrderByDescending(r => r.Start).ToList();
        _selecting = true;
        try
        {
            Investigations.Clear();
            foreach (var report in ordered)
            {
                Investigations.Add(new InvestigationOption(report.Id, $"{report.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · {MetricFormatter.DurationCompact(report.Duration)} · {AfterColon(report.Headline)}"));
            }

            HasInvestigations = Investigations.Count > 0;
            var target = select ?? _shown?.Id ?? ordered.FirstOrDefault()?.Id;
            SelectedInvestigation = Investigations.FirstOrDefault(i => i.Id == target);
        }
        finally
        {
            _selecting = false;
        }

        if (SelectedInvestigation is { } selected && _reports.TryGetValue(selected.Id, out var shown))
        {
            Show(shown);
        }
    }

    /// <summary>"Investigation complete: 2 anomalies found" → "2 anomalies found", in any language.</summary>
    private static string AfterColon(string headline)
    {
        var colon = headline.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && colon < headline.Length - 1 ? headline[(colon + 1)..].Trim() : headline;
    }

    private void Show(TroubleshootingReport report)
    {
        _shown = report;
        HasReport = true;
        Headline = report.Headline;
        Summary = report.Summary;
        PeriodText = Text.Format(
            UiStrings.Troubleshooting_Period,
            InsightDisplay.Period(report.Start, report.End),
            report.EndReason switch
            {
                TroubleshootingEndReason.Completed => UiStrings.Troubleshooting_EndedByItself,
                TroubleshootingEndReason.StoppedByUser => UiStrings.Troubleshooting_Stopped,
                _ => UiStrings.Troubleshooting_EndedOnClose,
            });
        Replace(Metrics, report.Metrics);
        Replace(Anomalies, report.Anomalies.Select(FindingItemViewModel.From));
        Replace(Correlations, report.Correlations.Concat(report.Likely).Select(FindingItemViewModel.From));
        Replace(Unknowns, report.Unknowns.Select(FindingItemViewModel.From));
        Replace(Applications, report.Applications.Take(8).Select(a => new InvestigationAppViewModel(a)));
        Replace(Events, report.Events.Select(e => $"{e.Timestamp.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)} · {e.Title}"));
        Replace(Recommendations, report.Recommendations);

        // Each point averages one step: draw it in the middle of its step, over exactly the steps recorded.
        var points = report.Timeline;
        var step = points.Count > 1 ? points[^1].Start - points[^2].Start : TroubleshootingRecorder.StepLength;
        var first = points.Count > 0 ? points[0].Start : report.Start;
        var end = points.Count > 0 ? points[^1].Start + step : report.End;
        var length = end - first;
        TimeSeriesData Chart(Func<TroubleshootingPoint, double?> value) => new(
            points.Where(p => value(p) is not null).Select(p => new MetricSample(p.Start + (step / 2), value(p)!.Value)).ToArray(),
            null,
            end,
            length > TimeSpan.Zero ? length : TimeSpan.FromMinutes(1),
            100,
            "100%",
            MetricFormatter.DurationCompact(report.End - report.Start));
        CpuChart = Chart(p => p.Cpu);
        MemoryChart = Chart(p => p.Memory);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}

/// <summary>An application in an investigation report.</summary>
public sealed class InvestigationAppViewModel(TroubleshootingApp app)
{
    public string Name { get; } = app.Name;

    public string Cpu { get; } = Text.Format(UiStrings.Troubleshooting_AppCpu, MetricFormatter.Percent(app.CpuAverage, 1), MetricFormatter.Percent(app.CpuPeak));

    public string Memory { get; } = Text.Format(UiStrings.Troubleshooting_AppMemory, MetricFormatter.Bytes(app.MemoryPeakBytes));

    public string Note { get; } = app.BusiestDuringHighCpu > 0
        ? Text.Plural(app.BusiestDuringHighCpu, UiStrings.Troubleshooting_BusiestCpu_One, UiStrings.Troubleshooting_BusiestCpu_Other)
        : app.BusiestDuringHighDisk > 0
            ? Text.Plural(app.BusiestDuringHighDisk, UiStrings.Troubleshooting_MostIo_One, UiStrings.Troubleshooting_MostIo_Other)
            : string.Empty;
}
