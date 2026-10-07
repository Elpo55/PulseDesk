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
            StatusText = "Choose how long to investigate, then start while the problem happens (or just before).";
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

        StatusText = "Troubleshooting mode active · Collecting additional context…";
        ElapsedText = $"{MetricFormatter.DurationPrecise(elapsed)} of {MetricFormatter.DurationPrecise(status.Planned)} · ends by itself, then collection returns to normal";
        Progress = Math.Clamp(elapsed / status.Planned * 100, 0, 100);
        CountsText = $"{status.Samples:N0} measurements · {MetricFormatter.Plural(status.Events, "event")} · {MetricFormatter.Plural(status.Alerts, "alert")}";
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
                Investigations.Add(new InvestigationOption(report.Id, $"{report.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · {MetricFormatter.DurationCompact(report.Duration)} · {report.Headline.Replace("Investigation complete: ", string.Empty, StringComparison.Ordinal)}"));
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

    private void Show(TroubleshootingReport report)
    {
        _shown = report;
        HasReport = true;
        Headline = report.Headline;
        Summary = report.Summary;
        PeriodText = $"{InsightDisplay.Period(report.Start, report.End)} · {(report.EndReason switch
        {
            TroubleshootingEndReason.Completed => "ended by itself",
            TroubleshootingEndReason.StoppedByUser => "stopped",
            _ => "ended when Sysora closed",
        })} · detailed collection";
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

    public string Cpu { get; } = $"{MetricFormatter.Percent(app.CpuAverage, 1)} avg · {MetricFormatter.Percent(app.CpuPeak)} peak";

    public string Memory { get; } = $"{MetricFormatter.Bytes(app.MemoryPeakBytes)} peak";

    public string Note { get; } = app.BusiestDuringHighCpu > 0
        ? $"Busiest in {MetricFormatter.Plural(app.BusiestDuringHighCpu, "high-CPU moment")}"
        : app.BusiestDuringHighDisk > 0 ? $"Most I/O in {MetricFormatter.Plural(app.BusiestDuringHighDisk, "busy-disk moment")}" : string.Empty;
}
