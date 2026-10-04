using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseDesk.App.Controls;
using PulseDesk.App.Services;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Metrics;
using PulseDesk.Core.Models;
using PulseDesk.Core.Monitoring;
using PulseDesk.Core.Settings;

namespace PulseDesk.App.ViewModels;

/// <summary>The default page: the state of the PC at a glance.</summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    private const int TopCount = 5;

    private readonly SettingsService _settings;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly NavigationService _navigation;
    private SystemInformation? _information;

    public DashboardViewModel(UiMetricsHub hub, SettingsService settings, ISystemInfoProvider systemInfo, NavigationService navigation)
        : base(hub)
    {
        _settings = settings;
        _systemInfo = systemInfo;
        _navigation = navigation;
        Subtitle = string.Empty;
        HealthSummary = "Checking…";
        CpuStats = string.Empty;
        MemoryStats = string.Empty;
        MemoryText = MetricFormatter.Pending;
        ProcessCountText = string.Empty;
        SelectedWindow = ChartWindowOption.FromSeconds(settings.Current.Monitoring.ChartWindowSeconds);
    }

    public MetricTileViewModel Cpu { get; } = new();

    public MetricTileViewModel Memory { get; } = new();

    public MetricTileViewModel Gpu { get; } = new();

    public MetricTileViewModel Disk { get; } = new();

    public ObservableCollection<HealthItemViewModel> HealthItems { get; } = [];

    public ObservableCollection<TopProcessItemViewModel> TopProcesses { get; } = [];

    public IReadOnlyList<ChartWindowOption> WindowOptions => ChartWindowOption.All;

    [ObservableProperty]
    public partial string Subtitle { get; set; }

    [ObservableProperty]
    public partial HealthStatus OverallHealth { get; set; }

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

    protected override async void OnActivated()
    {
        SelectedWindow = ChartWindowOption.FromSeconds(_settings.Current.Monitoring.ChartWindowSeconds);
        _information ??= await _systemInfo.GetAsync(CancellationToken.None);
        UpdateSubtitle(Hub.Snapshot);
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

        if ((updated & (MetricKind.Cpu | MetricKind.Memory)) != 0)
        {
            UpdateCharts(snapshot);
        }
    }

    protected override void OnHealthChanged(HealthReport report)
    {
        OverallHealth = report.Overall;
        var problems = report.Indicators.Count(i => i.Status is HealthStatus.Warning or HealthStatus.Critical);
        HealthSummary = report.Indicators.Count == 0 ? "Checking…"
            : problems == 0 ? "No issue detected"
            : problems == 1 ? "1 item needs attention"
            : $"{problems} items need attention";

        HealthItems.Clear();
        foreach (var indicator in report.Indicators.OrderByDescending(i => i.Status))
        {
            HealthItems.Add(new HealthItemViewModel(indicator.Status, indicator.Summary, indicator.Detail));
        }
    }

    private void UpdateCpu(SystemSnapshot snapshot)
    {
        if (snapshot.Cpu is not { } cpu)
        {
            Cpu.Set(Display.Format<CpuMetrics>(snapshot, MetricKind.Cpu, null, _ => string.Empty), string.Empty, string.Empty, double.NaN);
            return;
        }

        var speed = cpu.CurrentFrequencyGHz is { } ghz ? MetricFormatter.FrequencyGHz(ghz) : "Speed not available";
        Cpu.Set(
            MetricFormatter.Percent(cpu.UsagePercent),
            $"{speed} · {MetricFormatter.Plural(cpu.LogicalProcessors, "thread")}",
            $"Temperature: {MetricFormatter.Temperature(cpu.TemperatureCelsius)}",
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
            $"of {MetricFormatter.Bytes(memory.TotalBytes)} · {MetricFormatter.Percent(memory.UsedPercent)}",
            $"{MetricFormatter.Bytes(memory.AvailableBytes)} available",
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
                settingsDisabled ? "Off" : Display.Format<GpuMetrics>(snapshot, MetricKind.Gpu, null, _ => string.Empty),
                settingsDisabled ? "GPU monitoring is turned off" : string.Empty,
                string.Empty,
                double.NaN);
            return;
        }

        var memory = gpu.DedicatedMemoryUsedBytes is { } used && gpu.DedicatedMemoryTotalBytes is > 0
            ? $"Memory {MetricFormatter.Bytes(used)} / {MetricFormatter.Bytes(gpu.DedicatedMemoryTotalBytes)}"
            : $"Temperature: {MetricFormatter.Temperature(gpu.TemperatureCelsius)}";
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
            $"{drive.Letter} · {MetricFormatter.Bytes(drive.FreeBytes)} free of {MetricFormatter.Bytes(drive.TotalBytes)}",
            activity?.ActiveTimePercent is { } active ? $"Active time {MetricFormatter.Percent(active)}" : "Active time not available",
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

        ProcessCountText = MetricFormatter.Plural(processes.ProcessCount, "process", "processes");
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
                SortTopByMemory ? $"{cpu} CPU" : $"{memory} memory",
                Percentages.Of(Weight(group), largest));
        });
    }

    private void UpdateSubtitle(SystemSnapshot snapshot)
    {
        var name = _information?.ComputerName ?? Environment.MachineName;
        Subtitle = snapshot.System is { } system
            ? $"{name} · Up {MetricFormatter.DurationCompact(system.Uptime)}"
            : name;
    }
}
