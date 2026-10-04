using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PulseDesk.App.Controls;
using PulseDesk.App.Services;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Metrics;
using PulseDesk.Core.Models;
using PulseDesk.Core.Settings;

namespace PulseDesk.App.ViewModels;

/// <summary>Sections of the Performance page.</summary>
public enum PerformanceSection
{
    Cpu,
    Memory,
    Gpu,
    Disk,
}

/// <summary>Detailed, real-time view of CPU, memory, GPU and disk activity with history charts.</summary>
public sealed partial class PerformanceViewModel : PageViewModel
{
    private const string TemperatureHint =
        "Windows does not expose CPU temperature through a documented API. Reading it requires a kernel driver, which PulseDesk deliberately does not install.";

    private readonly SettingsService _settings;
    private readonly ISystemInfoProvider _systemInfo;
    private SystemInformation? _information;

    public PerformanceViewModel(UiMetricsHub hub, SettingsService settings, ISystemInfoProvider systemInfo)
        : base(hub)
    {
        _settings = settings;
        _systemInfo = systemInfo;
        SelectedWindow = ChartWindowOption.FromSeconds(settings.Current.Monitoring.ChartWindowSeconds);
        CpuName = MetricFormatter.Pending;
        Cpu = new CpuSection();
        Memory = new MemorySection();
    }

    public IReadOnlyList<ChartWindowOption> WindowOptions => ChartWindowOption.All;

    public CpuSection Cpu { get; }

    public MemorySection Memory { get; }

    public ObservableCollection<CoreUsageViewModel> Cores { get; } = [];

    public ObservableCollection<GpuItemViewModel> Gpus { get; } = [];

    public ObservableCollection<DiskActivityItemViewModel> Disks { get; } = [];

    public string TemperatureExplanation => TemperatureHint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCpuSection), nameof(IsMemorySection), nameof(IsGpuSection), nameof(IsDiskSection))]
    public partial PerformanceSection Section { get; set; }

    public bool IsCpuSection => Section == PerformanceSection.Cpu;

    public bool IsMemorySection => Section == PerformanceSection.Memory;

    public bool IsGpuSection => Section == PerformanceSection.Gpu;

    public bool IsDiskSection => Section == PerformanceSection.Disk;

    [ObservableProperty]
    public partial ChartWindowOption SelectedWindow { get; set; }

    [ObservableProperty]
    public partial string CpuName { get; set; }

    [ObservableProperty]
    public partial bool HasNoGpu { get; set; }

    [ObservableProperty]
    public partial bool HasNoDisk { get; set; }

    partial void OnSectionChanged(PerformanceSection value)
    {
        if (IsActive)
        {
            Update(Hub.Snapshot, MetricKind.All);
        }
    }

    partial void OnSelectedWindowChanged(ChartWindowOption value)
    {
        if (value is null)
        {
            return;
        }

        _settings.Update(s => s with { Monitoring = s.Monitoring with { ChartWindowSeconds = value.Seconds } });
        if (IsActive)
        {
            Update(Hub.Snapshot, MetricKind.All);
        }
    }

    protected override async void OnActivated()
    {
        SelectedWindow = ChartWindowOption.FromSeconds(_settings.Current.Monitoring.ChartWindowSeconds);
        _information ??= await _systemInfo.GetAsync(CancellationToken.None);
        CpuName = _information.Processor.Name ?? "Processor";
        Cpu.ApplyStaticInformation(_information);
        Memory.ApplyStaticInformation(_information);
    }

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        var history = Hub.Monitor.History;
        switch (Section)
        {
            case PerformanceSection.Cpu when (updated & (MetricKind.Cpu | MetricKind.Processes | MetricKind.System)) != 0:
                Cpu.Update(snapshot, history, SelectedWindow);
                UpdateCores(snapshot);
                break;
            case PerformanceSection.Memory when (updated & MetricKind.Memory) != 0:
                Memory.Update(snapshot, history, SelectedWindow);
                break;
            case PerformanceSection.Gpu when (updated & MetricKind.Gpu) != 0:
                UpdateGpus(snapshot, history);
                break;
            case PerformanceSection.Disk when (updated & MetricKind.DiskActivity) != 0:
                UpdateDisks(snapshot, history);
                break;
        }
    }

    private void UpdateCores(SystemSnapshot snapshot)
    {
        var usage = snapshot.Cpu?.LogicalProcessorUsage ?? [];
        CollectionSync.Resize(Cores, usage.Count, i => new CoreUsageViewModel($"CPU {i}"), (item, i) =>
        {
            item.Percent = usage[i];
            item.Text = MetricFormatter.Percent(usage[i]);
        });
    }

    private void UpdateGpus(SystemSnapshot snapshot, MetricHistory history)
    {
        var gpus = snapshot.Gpus ?? [];
        HasNoGpu = gpus.Count == 0;
        CollectionSync.Resize(Gpus, gpus.Count, _ => new GpuItemViewModel(), (item, i) => item.Update(gpus[i], snapshot, history, SelectedWindow));
    }

    private void UpdateDisks(SystemSnapshot snapshot, MetricHistory history)
    {
        var disks = snapshot.DiskActivity ?? [];
        HasNoDisk = disks.Count == 0;
        CollectionSync.Resize(Disks, disks.Count, _ => new DiskActivityItemViewModel(), (item, i) => item.Update(disks[i], snapshot, history, SelectedWindow));
    }
}

/// <summary>CPU section of the Performance page.</summary>
public sealed partial class CpuSection : ObservableObject
{
    public CpuSection()
    {
        Usage = Speed = BaseSpeed = PeakSpeed = Stats = MetricFormatter.Pending;
        Cores = Threads = Sockets = L2Cache = L3Cache = MetricFormatter.Pending;
        Processes = ThreadsTotal = Handles = Uptime = Temperature = MetricFormatter.Pending;
    }

    [ObservableProperty]
    public partial TimeSeriesData? Chart { get; set; }

    [ObservableProperty]
    public partial string Usage { get; set; }

    [ObservableProperty]
    public partial string Stats { get; set; }

    [ObservableProperty]
    public partial string Speed { get; set; }

    [ObservableProperty]
    public partial string BaseSpeed { get; set; }

    [ObservableProperty]
    public partial string PeakSpeed { get; set; }

    [ObservableProperty]
    public partial string Temperature { get; set; }

    [ObservableProperty]
    public partial string Cores { get; set; }

    [ObservableProperty]
    public partial string Threads { get; set; }

    [ObservableProperty]
    public partial string Sockets { get; set; }

    [ObservableProperty]
    public partial string L2Cache { get; set; }

    [ObservableProperty]
    public partial string L3Cache { get; set; }

    [ObservableProperty]
    public partial string Processes { get; set; }

    [ObservableProperty]
    public partial string ThreadsTotal { get; set; }

    [ObservableProperty]
    public partial string Handles { get; set; }

    [ObservableProperty]
    public partial string Uptime { get; set; }

    public void ApplyStaticInformation(SystemInformation information)
    {
        var processor = information.Processor;
        Cores = processor.PhysicalCores?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? MetricFormatter.NotAvailable;
        Threads = processor.LogicalProcessors.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Sockets = processor.Sockets?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? MetricFormatter.NotAvailable;
        L2Cache = MetricFormatter.Bytes(processor.L2CacheBytes);
        L3Cache = MetricFormatter.Bytes(processor.L3CacheBytes);
        if (BaseSpeed == MetricFormatter.Pending)
        {
            BaseSpeed = MetricFormatter.FrequencyGHz(processor.BaseFrequencyGHz);
        }
    }

    public void Update(SystemSnapshot snapshot, MetricHistory history, ChartWindowOption window)
    {
        Chart = ChartFactory.Percent(history, SeriesKeys.Cpu, window, snapshot);
        Stats = ChartFactory.Summary(history, SeriesKeys.Cpu, window, snapshot);
        Usage = Display.Format(snapshot, MetricKind.Cpu, snapshot.Cpu, c => MetricFormatter.Percent(c.UsagePercent));
        Speed = Display.Format(snapshot, MetricKind.Cpu, snapshot.Cpu, c => MetricFormatter.FrequencyGHz(c.CurrentFrequencyGHz));
        PeakSpeed = Display.Format(snapshot, MetricKind.Cpu, snapshot.Cpu, c => MetricFormatter.FrequencyGHz(c.PeakFrequencyGHz));
        Temperature = Display.Format(snapshot, MetricKind.Cpu, snapshot.Cpu, c => MetricFormatter.Temperature(c.TemperatureCelsius));
        if (snapshot.Cpu?.BaseFrequencyGHz is { } baseGhz)
        {
            BaseSpeed = MetricFormatter.FrequencyGHz(baseGhz);
        }

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        Processes = Display.Format(snapshot, MetricKind.Processes, snapshot.Processes, p => p.ProcessCount.ToString("N0", culture));
        ThreadsTotal = Display.Format(snapshot, MetricKind.Processes, snapshot.Processes, p => p.ThreadCount.ToString("N0", culture));
        Handles = Display.Format(snapshot, MetricKind.Processes, snapshot.Processes, p => p.HandleCount.ToString("N0", culture));
        Uptime = Display.Format(snapshot, MetricKind.System, snapshot.System, s => MetricFormatter.DurationCompact(s.Uptime));
    }
}

/// <summary>Memory section of the Performance page.</summary>
public sealed partial class MemorySection : ObservableObject
{
    public MemorySection()
    {
        Usage = Stats = InUse = Available = Total = Installed = MetricFormatter.Pending;
        Committed = Cached = PagedPool = NonPagedPool = MetricFormatter.Pending;
    }

    [ObservableProperty]
    public partial TimeSeriesData? Chart { get; set; }

    [ObservableProperty]
    public partial string Usage { get; set; }

    [ObservableProperty]
    public partial string Stats { get; set; }

    [ObservableProperty]
    public partial string InUse { get; set; }

    [ObservableProperty]
    public partial string Available { get; set; }

    [ObservableProperty]
    public partial string Total { get; set; }

    [ObservableProperty]
    public partial string Installed { get; set; }

    [ObservableProperty]
    public partial string Committed { get; set; }

    [ObservableProperty]
    public partial string Cached { get; set; }

    [ObservableProperty]
    public partial string PagedPool { get; set; }

    [ObservableProperty]
    public partial string NonPagedPool { get; set; }

    public void ApplyStaticInformation(SystemInformation information) =>
        Installed = MetricFormatter.Bytes(information.InstalledMemoryBytes);

    public void Update(SystemSnapshot snapshot, MetricHistory history, ChartWindowOption window)
    {
        Chart = ChartFactory.Percent(history, SeriesKeys.Memory, window, snapshot);
        Stats = ChartFactory.Summary(history, SeriesKeys.Memory, window, snapshot);
        var memory = snapshot.Memory;
        Usage = Display.Format(snapshot, MetricKind.Memory, memory, m => MetricFormatter.Percent(m.UsedPercent, 1));
        InUse = Display.Format(snapshot, MetricKind.Memory, memory, m => MetricFormatter.Bytes(m.UsedBytes));
        Available = Display.Format(snapshot, MetricKind.Memory, memory, m => MetricFormatter.Bytes(m.AvailableBytes));
        Total = Display.Format(snapshot, MetricKind.Memory, memory, m => MetricFormatter.Bytes(m.TotalBytes));
        Committed = Display.Format(snapshot, MetricKind.Memory, memory, m =>
            m.CommittedBytes is { } c && m.CommitLimitBytes is { } l ? $"{MetricFormatter.Bytes(c)} / {MetricFormatter.Bytes(l)}" : MetricFormatter.NotAvailable);
        Cached = Display.Format(snapshot, MetricKind.Memory, memory, m => MetricFormatter.Bytes(m.CachedBytes));
        PagedPool = Display.Format(snapshot, MetricKind.Memory, memory, m => MetricFormatter.Bytes(m.PagedPoolBytes));
        NonPagedPool = Display.Format(snapshot, MetricKind.Memory, memory, m => MetricFormatter.Bytes(m.NonPagedPoolBytes));
    }
}

/// <summary>One graphics adapter on the Performance page.</summary>
public sealed partial class GpuItemViewModel : ObservableObject
{
    public GpuItemViewModel()
    {
        Name = Usage = Stats = Dedicated = Shared = Temperature = Frequency = Engines = MetricFormatter.Pending;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? Chart { get; set; }

    [ObservableProperty]
    public partial string Usage { get; set; }

    [ObservableProperty]
    public partial string Stats { get; set; }

    [ObservableProperty]
    public partial string Dedicated { get; set; }

    [ObservableProperty]
    public partial string Shared { get; set; }

    [ObservableProperty]
    public partial string Temperature { get; set; }

    [ObservableProperty]
    public partial string Frequency { get; set; }

    [ObservableProperty]
    public partial string Engines { get; set; }

    public void Update(GpuMetrics gpu, SystemSnapshot snapshot, MetricHistory history, ChartWindowOption window)
    {
        var key = SeriesKeys.Gpu(gpu.AdapterId);
        Name = gpu.Name;
        Chart = ChartFactory.Percent(history, key, window, snapshot);
        Stats = ChartFactory.Summary(history, key, window, snapshot);
        Usage = MetricFormatter.Percent(gpu.UsagePercent);
        Dedicated = UsedOfTotal(gpu.DedicatedMemoryUsedBytes, gpu.DedicatedMemoryTotalBytes);
        Shared = UsedOfTotal(gpu.SharedMemoryUsedBytes, gpu.SharedMemoryTotalBytes);
        Temperature = MetricFormatter.Temperature(gpu.TemperatureCelsius);
        Frequency = gpu.FrequencyMHz is { } mhz ? MetricFormatter.FrequencyGHz(mhz / 1000) : MetricFormatter.NotAvailable;
        Engines = gpu.Engines.Count == 0
            ? MetricFormatter.NotAvailable
            : string.Join(" · ", gpu.Engines.Take(4).Select(e => $"{e.EngineType} {MetricFormatter.Percent(e.UsagePercent)}"));
    }

    private static string UsedOfTotal(ulong? used, ulong? total) =>
        used is null && total is null ? MetricFormatter.NotAvailable
        : $"{MetricFormatter.Bytes(used)} / {MetricFormatter.Bytes(total)}";
}

/// <summary>Activity of one volume on the Performance page.</summary>
public sealed partial class DiskActivityItemViewModel : ObservableObject
{
    public DiskActivityItemViewModel()
    {
        Drive = Active = Read = Write = Stats = MetricFormatter.Pending;
    }

    [ObservableProperty]
    public partial string Drive { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? Chart { get; set; }

    [ObservableProperty]
    public partial string Active { get; set; }

    [ObservableProperty]
    public partial string Stats { get; set; }

    [ObservableProperty]
    public partial string Read { get; set; }

    [ObservableProperty]
    public partial string Write { get; set; }

    public void Update(DiskActivityMetrics disk, SystemSnapshot snapshot, MetricHistory history, ChartWindowOption window)
    {
        var key = SeriesKeys.DiskActive(disk.Drive);
        Drive = $"Disk {disk.Drive}";
        Chart = ChartFactory.Percent(history, key, window, snapshot);
        Stats = ChartFactory.Summary(history, key, window, snapshot);
        Active = MetricFormatter.Percent(disk.ActiveTimePercent);
        Read = MetricFormatter.BytesPerSecond(disk.ReadBytesPerSecond);
        Write = MetricFormatter.BytesPerSecond(disk.WriteBytesPerSecond);
    }
}
