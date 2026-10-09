using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysora.App.Controls;
using Sysora.App.Services;
using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.Changes;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.Gaming;
using Sysora.Core.Health;
using Sysora.Core.Interfaces;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;
using Sysora.Core.Settings;
using Sysora.Localization;

namespace Sysora.App.ViewModels;

/// <summary>
/// The default page: "how is my PC?" at a glance. An overall status, the key metrics with mini-graphs, the
/// insights worth knowing now, and one-click access to Diagnosis, Replay, Changes, App Impact, Alerts and Gaming.
/// </summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    private const int TopCount = 5;
    private static readonly TimeSpan SparklineWindow = TimeSpan.FromMinutes(2);

    private readonly SettingsService _settings;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly NavigationService _navigation;
    private readonly DiagnosisService _diagnosis;
    private readonly AlertService _alerts;
    private readonly BaselineService _baseline;
    private readonly IPerformanceHistory _history;
    private readonly InsightNavigator _navigator;
    private readonly GameSessionService _games;
    private readonly PcHealthService _health;
    private readonly RecurringProblemService _recurring;
    private readonly SinceYesterdayService _sinceYesterday;
    private SystemInformation? _information;
    private long _lastInsights;
    private bool _insightsRunning;

    public DashboardViewModel(
        UiMetricsHub hub,
        SettingsService settings,
        ISystemInfoProvider systemInfo,
        NavigationService navigation,
        DiagnosisService diagnosis,
        AlertService alerts,
        BaselineService baseline,
        IPerformanceHistory history,
        InsightNavigator navigator,
        GameSessionService games,
        PcHealthService health,
        RecurringProblemService recurring,
        SinceYesterdayService sinceYesterday)
        : base(hub)
    {
        _health = health;
        _recurring = recurring;
        _sinceYesterday = sinceYesterday;
        HealthScoreText = "—";
        HealthGradeText = PcHealthReport.GradeText(PcHealthGrade.Unknown);
        HealthDetail = PcHealthReport.Empty.Summary;
        HealthBrushKey = "StatusUnknownBrush";
        HealthGlyph = InsightDisplay.InfoGlyph;
        SinceYesterdayHeadline = SinceYesterdaySummary.Loading.Headline;
        SinceYesterdayDetail = string.Empty;
        _settings = settings;
        _systemInfo = systemInfo;
        _navigation = navigation;
        _diagnosis = diagnosis;
        _alerts = alerts;
        _baseline = baseline;
        _history = history;
        _navigator = navigator;
        _games = games;
        GamingText = UiStrings.Common_Gaming;
        StatusText = HealthGlyphs.Text(PcHealthState.Unknown);
        StatusDetail = DiagnosisReport.Empty.Headline;
        StatusGlyph = HealthGlyphs.Unknown;
        StatusBrushKey = "StatusUnknownBrush";
        AlertsText = UiStrings.Common_Alerts;
        Subtitle = string.Empty;
        HealthSummary = UiStrings.Common_Checking;
        CpuStats = string.Empty;
        MemoryStats = string.Empty;
        MemoryText = MetricFormatter.Pending;
        ProcessCountText = string.Empty;
        SelectedWindow = ChartWindowOption.FromSeconds(settings.Current.Monitoring.ChartWindowSeconds);
    }

    [ObservableProperty]
    public partial string HealthScoreText { get; set; }

    [ObservableProperty]
    public partial string HealthGradeText { get; set; }

    [ObservableProperty]
    public partial string HealthDetail { get; set; }

    [ObservableProperty]
    public partial string HealthBrushKey { get; set; }

    [ObservableProperty]
    public partial string HealthGlyph { get; set; }

    [ObservableProperty]
    public partial string SinceYesterdayHeadline { get; set; }

    [ObservableProperty]
    public partial string SinceYesterdayDetail { get; set; }

    [ObservableProperty]
    public partial int SignificantChanges { get; set; }

    [ObservableProperty]
    public partial int MinorChanges { get; set; }

    [ObservableProperty]
    public partial bool HasSignificantChanges { get; set; }

    [ObservableProperty]
    public partial bool HasMinorChanges { get; set; }

    [ObservableProperty]
    public partial bool IsMostlyUnchanged { get; set; }

    public MetricTileViewModel Cpu { get; } = new();

    public MetricTileViewModel Memory { get; } = new();

    public MetricTileViewModel Gpu { get; } = new();

    public MetricTileViewModel Disk { get; } = new();

    public MetricTileViewModel Network { get; } = new();

    public ObservableCollection<InsightItemViewModel> Insights { get; } = [];

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial string StatusDetail { get; set; }

    [ObservableProperty]
    public partial string StatusGlyph { get; set; }

    [ObservableProperty]
    public partial string StatusBrushKey { get; set; }

    [ObservableProperty]
    public partial string AlertsText { get; set; }

    [ObservableProperty]
    public partial string GamingText { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? CpuSpark { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? MemorySpark { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? GpuSpark { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? DiskSpark { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? NetworkSpark { get; set; }

    public ObservableCollection<HealthItemViewModel> HealthItems { get; } = [];

    public ObservableCollection<TopProcessItemViewModel> TopProcesses { get; } = [];

    public IReadOnlyList<ChartWindowOption> WindowOptions => ChartWindowOption.All;

    [ObservableProperty]
    public partial string Subtitle { get; set; }

    [ObservableProperty]
    public partial string HealthSummary { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? CpuChart { get; set; }

    [ObservableProperty]
    public partial string CpuStats { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? MemoryChart { get; set; }

    [ObservableProperty]
    public partial string MemoryStats { get; set; }

    [ObservableProperty]
    public partial string MemoryText { get; set; }

    [ObservableProperty]
    public partial double MemoryPercent { get; set; }

    [ObservableProperty]
    public partial string ProcessCountText { get; set; }

    /// <summary>Sort order of the top applications: 0 = by CPU, 1 = by memory.</summary>
    [ObservableProperty]
    public partial int TopSortIndex { get; set; }

    private bool SortTopByMemory => TopSortIndex == 1;

    public IReadOnlyList<string> TopSortOptions { get; } = [UiStrings.Dashboard_SortByCpu, UiStrings.Dashboard_SortByMemory];

    [ObservableProperty]
    public partial ChartWindowOption SelectedWindow { get; set; }

    partial void OnSelectedWindowChanged(ChartWindowOption value)
    {
        if (value is null)
        {
            return;
        }

        _settings.Update(s => s with { Monitoring = s.Monitoring with { ChartWindowSeconds = value.Seconds } });
        if (IsActive)
        {
            UpdateCharts(Hub.Snapshot);
        }
    }

    partial void OnTopSortIndexChanged(int value)
    {
        if (IsActive)
        {
            UpdateTopProcesses(Hub.Snapshot);
        }
    }

    [RelayCommand]
    private void OpenProcesses() => _navigation.Navigate(AppPage.Processes);

    [RelayCommand]
    private void OpenPerformance() => _navigation.Navigate(AppPage.Performance);

    [RelayCommand]
    private void OpenDiagnosis() => _navigation.Navigate(AppPage.Diagnosis);

    [RelayCommand]
    private void OpenReplay() => _navigation.Navigate(AppPage.Replay);

    [RelayCommand]
    private void OpenChanges() => _navigation.Navigate(AppPage.Changes);

    [RelayCommand]
    private void OpenAppImpact() => _navigation.Navigate(AppPage.AppImpact);

    [RelayCommand]
    private void OpenAlerts() => _navigation.Navigate(AppPage.Alerts);

    [RelayCommand]
    private void OpenGaming() => _navigation.Navigate(AppPage.Gaming);

    [RelayCommand]
    private void OpenPcHealth() => _navigation.Navigate(AppPage.PcHealth);

    [RelayCommand]
    private void OpenTimeline() => _navigation.Navigate(AppPage.Timeline);

    [RelayCommand]
    private void OpenTroubleshooting() => _navigation.Navigate(AppPage.Troubleshooting);

    protected override async void OnActivated()
    {
        SelectedWindow = ChartWindowOption.FromSeconds(_settings.Current.Monitoring.ChartWindowSeconds);
        _alerts.Changed += OnAlertsChanged;
        _games.SessionsChanged += OnAlertsChanged;
        _ = RefreshInsightsAsync();
        _information ??= await _systemInfo.GetAsync(CancellationToken.None);
        UpdateSubtitle(Hub.Snapshot);
    }

    protected override void OnDeactivated()
    {
        _alerts.Changed -= OnAlertsChanged;
        _games.SessionsChanged -= OnAlertsChanged;
    }

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        if ((updated & MetricKind.Cpu) != 0)
        {
            UpdateCpu(snapshot);
        }

        if ((updated & MetricKind.Memory) != 0)
        {
            UpdateMemory(snapshot);
        }

        if ((updated & MetricKind.Gpu) != 0)
        {
            UpdateGpu(snapshot);
        }

        if ((updated & (MetricKind.Storage | MetricKind.DiskActivity)) != 0)
        {
            UpdateDisk(snapshot);
        }

        if ((updated & MetricKind.Processes) != 0)
        {
            UpdateTopProcesses(snapshot);
        }

        if ((updated & MetricKind.System) != 0)
        {
            UpdateSubtitle(snapshot);
        }

        if ((updated & MetricKind.Network) != 0)
        {
            UpdateNetwork(snapshot);
        }

        if ((updated & (MetricKind.Cpu | MetricKind.Memory)) != 0)
        {
            UpdateCharts(snapshot);
        }

        UpdateSparklines(snapshot, updated);

        if ((updated & MetricKind.Cpu) != 0 && Environment.TickCount64 - _lastInsights >= InsightRefreshInterval.TotalMilliseconds)
        {
            _ = RefreshInsightsAsync();
        }
    }

    protected override void OnHealthChanged(HealthReport report)
    {
        var problems = report.Indicators.Count(i => i.Status is HealthStatus.Warning or HealthStatus.Critical);
        HealthSummary = report.Indicators.Count == 0 ? UiStrings.Common_Checking
            : problems == 0 ? UiStrings.Dashboard_NoIssue
            : Text.Plural(problems, UiStrings.Dashboard_ItemsAttention_One, UiStrings.Dashboard_ItemsAttention_Other);

        HealthItems.Clear();
        foreach (var indicator in report.Indicators.OrderByDescending(i => i.Status))
        {
            HealthItems.Add(new HealthItemViewModel(indicator.Status, indicator.Summary, indicator.Detail));
        }
    }

    /// <summary>Re-runs the diagnosis in the background and updates the status and insights.</summary>
    private async Task RefreshInsightsAsync()
    {
        if (_insightsRunning)
        {
            return;
        }

        _insightsRunning = true;
        _lastInsights = Environment.TickCount64;
        try
        {
            var report = await _diagnosis.RunAsync(CancellationToken.None);
            var health = await _health.RefreshAsync(CancellationToken.None);
            if (IsActive)
            {
                ApplyHealth(health);
                ApplyInsights(report, health);
            }

            var since = await _sinceYesterday.GetAsync(force: false, CancellationToken.None);
            if (IsActive)
            {
                ApplySinceYesterday(since);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The diagnosis service logs its own failures; the dashboard keeps working without insights.
            StatusDetail = UiStrings.Dashboard_DiagnosisNotAvailable;
        }
        finally
        {
            _insightsRunning = false;
        }
    }

    /// <summary>Insights are refreshed less often with the Minimal intensity.</summary>
    private TimeSpan InsightRefreshInterval => MonitoringProfile.For(_settings.Current.Monitoring.Intensity).InsightRefreshInterval;

    private void ApplyHealth(PcHealthReport health)
    {
        HealthScoreText = health.Score is { } score ? $"{score}/100" : "—";
        HealthGradeText = PcHealthReport.GradeText(health.Grade);
        HealthDetail = health.Summary;
        HealthBrushKey = HealthDisplay.BrushKey(health.Grade);
        HealthGlyph = health.Grade switch
        {
            PcHealthGrade.Good => InsightDisplay.NormalGlyph,
            PcHealthGrade.NeedsAttention => InsightDisplay.WarningGlyph,
            PcHealthGrade.Poor => InsightDisplay.CriticalGlyph,
            _ => InsightDisplay.InfoGlyph,
        };
    }

    private void ApplySinceYesterday(SinceYesterdaySummary summary)
    {
        SinceYesterdayHeadline = summary.Headline;
        SignificantChanges = summary.Significant;
        MinorChanges = summary.Minor;
        HasSignificantChanges = summary.Significant > 0;
        HasMinorChanges = summary.Minor > 0;
        IsMostlyUnchanged = summary.HasReference && summary.MostlyUnchanged;
        SinceYesterdayDetail = !summary.HasReference
            ? UiStrings.Dashboard_SinceYesterday_NoReference
            : summary.Items.Count == 0 ? Text.Format(UiStrings.Dashboard_SinceYesterday_Nothing, string.Join(Strings.List_Separator, summary.UnchangedAreas).ToLower(CultureInfo.CurrentCulture))
            : string.Join(" · ", summary.Items.Take(2).Select(i => i.Title));
    }

    private void ApplyInsights(DiagnosisReport report, PcHealthReport health)
    {
        var alerts = _alerts.Alerts;
        var state = DashboardInsights.State(report, alerts);
        StatusText = HealthGlyphs.Text(state);
        (StatusGlyph, StatusBrushKey) = HealthGlyphs.For(state);
        StatusDetail = report.Headline;

        var active = alerts.Count(a => a.IsActive);
        var unseen = alerts.Count(a => a.Status == AlertStatus.New);
        AlertsText = unseen > 0
            ? Text.Format(UiStrings.Dashboard_AlertsNew, unseen)
            : active > 0 ? Text.Format(UiStrings.Dashboard_AlertsActive, active) : UiStrings.Common_Alerts;

        var games = _games.ActiveSessions;
        GamingText = games.Count > 0 ? $"{UiStrings.Common_Gaming} · {games[0].Name}" : UiStrings.Common_Gaming;
        var insights = DashboardInsights.Build(report, alerts, _history.GetRecent(TimeSpan.FromMinutes(10)), _baseline.Current, games, _games.LatestRecap, health, _recurring.Latest);
        CollectionSync.Resize(Insights, insights.Count, _ => new InsightItemViewModel(i => _navigator.Open(i.Action, i.AppKey)), (item, i) => item.Set(insights[i]));
    }

    private void OnAlertsChanged(object? sender, EventArgs e) => _lastInsights = 0;

    private void UpdateNetwork(SystemSnapshot snapshot)
    {
        if (snapshot.Network is not { } network)
        {
            var off = !_settings.Current.Monitoring.NetworkEnabled;
            Network.Set(
                off ? UiStrings.Common_Off : Display.Format<NetworkMetrics>(snapshot, MetricKind.Network, null, _ => string.Empty),
                off ? UiStrings.Dashboard_NetworkOff : string.Empty,
                string.Empty,
                double.NaN);
            return;
        }

        var connectivity = network.Connectivity switch
        {
            NetworkConnectivity.InternetAccess => Strings.Connectivity_Internet,
            NetworkConnectivity.ConstrainedInternetAccess => Strings.Connectivity_Constrained,
            NetworkConnectivity.LocalAccess => Strings.Diag_Net_LocalTitle,
            NetworkConnectivity.None => UiStrings.Dashboard_NotConnected,
            _ => UiStrings.Dashboard_ConnectivityUnknown,
        };
        var hasRates = network.Interfaces.Any(i => i.ReceiveBitsPerSecond is not null);
        Network.Set(
            hasRates ? $"↓ {MetricFormatter.BitsPerSecond(network.ReceiveBitsPerSecond)}" : MetricFormatter.Pending,
            hasRates ? $"↑ {MetricFormatter.BitsPerSecond(network.SendBitsPerSecond)}" : string.Empty,
            network.PrimaryInterface is { } primary ? $"{primary.Name} · {connectivity}" : connectivity,
            double.NaN);
    }

    /// <summary>Mini-graphs of the last two minutes, from the chart history (no extra collection).</summary>
    private void UpdateSparklines(SystemSnapshot snapshot, MetricKind updated)
    {
        if (snapshot.Timestamp == default)
        {
            return;
        }

        var history = Hub.Monitor.History;
        var since = snapshot.Timestamp - SparklineWindow;
        TimeSeriesData Spark(string key, double maximum) =>
            new(history.GetSamples(key, since), null, snapshot.Timestamp, SparklineWindow, maximum, string.Empty, string.Empty);

        if ((updated & MetricKind.Cpu) != 0)
        {
            CpuSpark = Spark(SeriesKeys.Cpu, 100);
        }

        if ((updated & MetricKind.Memory) != 0)
        {
            MemorySpark = Spark(SeriesKeys.Memory, 100);
        }

        if ((updated & MetricKind.Gpu) != 0)
        {
            GpuSpark = snapshot.PrimaryGpu is { } gpu ? Spark(SeriesKeys.Gpu(gpu.AdapterId), 100) : null;
        }

        if ((updated & MetricKind.DiskActivity) != 0)
        {
            DiskSpark = snapshot.SystemDrive is { } drive ? Spark(SeriesKeys.DiskActive(drive.Letter), 100) : null;
        }

        if ((updated & MetricKind.Network) != 0)
        {
            var receive = history.GetSamples(SeriesKeys.NetworkReceive, since);
            var peak = receive.Select(s => s.Value).DefaultIfEmpty(0).Max();
            NetworkSpark = new TimeSeriesData(receive, null, snapshot.Timestamp, SparklineWindow, ChartScale.NiceMaximum(peak * 1.1, 100_000), string.Empty, string.Empty);
        }
    }

    private void UpdateCpu(SystemSnapshot snapshot)
    {
        if (snapshot.Cpu is not { } cpu)
        {
            Cpu.Set(Display.Format<CpuMetrics>(snapshot, MetricKind.Cpu, null, _ => string.Empty), string.Empty, string.Empty, double.NaN);
            return;
        }

        var speed = cpu.CurrentFrequencyGHz is { } ghz ? MetricFormatter.FrequencyGHz(ghz) : UiStrings.Dashboard_SpeedNotAvailable;
        Cpu.Set(
            MetricFormatter.Percent(cpu.UsagePercent),
            $"{speed} · {Text.Plural(cpu.LogicalProcessors, UiStrings.Count_Thread_One, UiStrings.Count_Thread_Other)}",
            Text.Format(UiStrings.Dashboard_Temperature, MetricFormatter.Temperature(cpu.TemperatureCelsius)),
            cpu.UsagePercent);
    }

    private void UpdateMemory(SystemSnapshot snapshot)
    {
        if (snapshot.Memory is not { } memory)
        {
            var text = Display.Format<MemoryMetrics>(snapshot, MetricKind.Memory, null, _ => string.Empty);
            Memory.Set(text, string.Empty, string.Empty, double.NaN);
            MemoryText = text;
            return;
        }

        Memory.Set(
            MetricFormatter.Bytes(memory.UsedBytes),
            Text.Format(UiStrings.Dashboard_MemoryOf, MetricFormatter.Bytes(memory.TotalBytes), MetricFormatter.Percent(memory.UsedPercent)),
            Text.Format(UiStrings.Dashboard_MemoryAvailable, MetricFormatter.Bytes(memory.AvailableBytes)),
            memory.UsedPercent);
        MemoryText = $"{MetricFormatter.Bytes(memory.UsedBytes)} / {MetricFormatter.Bytes(memory.TotalBytes)}";
        MemoryPercent = memory.UsedPercent;
    }

    private void UpdateGpu(SystemSnapshot snapshot)
    {
        if (snapshot.PrimaryGpu is not { } gpu)
        {
            var settingsDisabled = !_settings.Current.Monitoring.GpuEnabled;
            Gpu.Set(
                settingsDisabled ? UiStrings.Common_Off : Display.Format<GpuMetrics>(snapshot, MetricKind.Gpu, null, _ => string.Empty),
                settingsDisabled ? UiStrings.Dashboard_GpuOff : string.Empty,
                string.Empty,
                double.NaN);
            return;
        }

        var memory = gpu.DedicatedMemoryUsedBytes is { } used && gpu.DedicatedMemoryTotalBytes is > 0
            ? Text.Format(UiStrings.Dashboard_GpuMemory, MetricFormatter.Bytes(used), MetricFormatter.Bytes(gpu.DedicatedMemoryTotalBytes))
            : Text.Format(UiStrings.Dashboard_Temperature, MetricFormatter.Temperature(gpu.TemperatureCelsius));
        Gpu.Set(MetricFormatter.Percent(gpu.UsagePercent), gpu.Name, memory, gpu.UsagePercent ?? double.NaN);
    }

    private void UpdateDisk(SystemSnapshot snapshot)
    {
        if (snapshot.SystemDrive is not { } drive)
        {
            Disk.Set(Display.Format<StorageMetrics>(snapshot, MetricKind.Storage, null, _ => string.Empty), string.Empty, string.Empty, double.NaN);
            return;
        }

        var activity = snapshot.DiskActivity?.FirstOrDefault(d => string.Equals(d.Drive, drive.Letter, StringComparison.OrdinalIgnoreCase));
        Disk.Set(
            MetricFormatter.Percent(drive.UsedPercent),
            Text.Format(UiStrings.Dashboard_DiskFree, drive.Letter, MetricFormatter.Bytes(drive.FreeBytes), MetricFormatter.Bytes(drive.TotalBytes)),
            activity?.ActiveTimePercent is { } active
                ? Text.Format(UiStrings.Dashboard_ActiveTime, MetricFormatter.Percent(active))
                : UiStrings.Dashboard_ActiveTimeNotAvailable,
            drive.UsedPercent);
    }

    private void UpdateCharts(SystemSnapshot snapshot)
    {
        var history = Hub.Monitor.History;
        CpuChart = ChartFactory.Percent(history, SeriesKeys.Cpu, SelectedWindow, snapshot);
        CpuStats = ChartFactory.Summary(history, SeriesKeys.Cpu, SelectedWindow, snapshot);
        MemoryChart = ChartFactory.Percent(history, SeriesKeys.Memory, SelectedWindow, snapshot);
        MemoryStats = ChartFactory.Summary(history, SeriesKeys.Memory, SelectedWindow, snapshot);
    }

    private void UpdateTopProcesses(SystemSnapshot snapshot)
    {
        if (snapshot.Processes is not { } processes)
        {
            ProcessCountText = Display.Format<ProcessSnapshot>(snapshot, MetricKind.Processes, null, _ => string.Empty);
            TopProcesses.Clear();
            return;
        }

        ProcessCountText = Text.Plural(processes.ProcessCount, Strings.Count_Process_One, Strings.Count_Process_Other);
        var groups = ProcessAggregation.GroupByName(processes.Processes);
        var top = SortTopByMemory ? ProcessAggregation.TopByMemory(groups, TopCount) : ProcessAggregation.TopByCpu(groups, TopCount);
        // Bars are relative to the first entry: they show the ranking, the numbers show the values.
        double Weight(ProcessGroup g) => SortTopByMemory ? g.PrivateWorkingSetBytes : g.CpuPercent;
        var largest = top.Count > 0 ? Weight(top[0]) : 0;

        CollectionSync.Resize(TopProcesses, top.Count, _ => new TopProcessItemViewModel(), (item, i) =>
        {
            var group = top[i];
            var cpu = MetricFormatter.Percent(group.CpuPercent, 1);
            var memory = MetricFormatter.Bytes(group.PrivateWorkingSetBytes);
            item.Set(
                group,
                SortTopByMemory ? memory : cpu,
                SortTopByMemory
                    ? Text.Format(UiStrings.Dashboard_TopCpu, cpu)
                    : Text.Format(UiStrings.Dashboard_TopMemory, memory),
                Percentages.Of(Weight(group), largest));
        });
    }

    private void UpdateSubtitle(SystemSnapshot snapshot)
    {
        var name = _information?.ComputerName ?? Environment.MachineName;
        Subtitle = snapshot.System is { } system
            ? Text.Format(UiStrings.Dashboard_Uptime, name, MetricFormatter.DurationCompact(system.Uptime))
            : name;
    }
}

/// <summary>One insight on the dashboard.</summary>
public sealed partial class InsightItemViewModel : ObservableObject
{
    private readonly Action<InsightItemViewModel> _open;

    public InsightItemViewModel(Action<InsightItemViewModel> open)
    {
        _open = open;
        Text = Glyph = BrushKey = string.Empty;
    }

    public DiagnosisAction Action { get; private set; }

    public string? AppKey { get; private set; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial string Glyph { get; set; }

    [ObservableProperty]
    public partial string BrushKey { get; set; }

    [ObservableProperty]
    public partial bool HasAction { get; set; }

    [RelayCommand]
    private void Open() => _open(this);

    public void Set(Insight insight)
    {
        Text = insight.Text;
        (Glyph, BrushKey) = insight.IsNote ? (InsightDisplay.InfoGlyph, "StatusUnknownBrush") : HealthGlyphs.For(insight.Severity);
        Action = insight.Action;
        AppKey = insight.AppKey;
        HasAction = insight.Action != DiagnosisAction.None;
    }
}
