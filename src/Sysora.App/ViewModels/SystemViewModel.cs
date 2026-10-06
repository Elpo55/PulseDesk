using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysora.App.Services;
using Sysora.Core.Formatting;
using Sysora.Core.Interfaces;
using Sysora.Core.Models;

namespace Sysora.App.ViewModels;

/// <summary>Description of the machine: Windows, hardware, firmware and uptime.</summary>
public sealed partial class SystemViewModel : PageViewModel
{
    private readonly ISystemInfoProvider _systemInfo;

    public SystemViewModel(UiMetricsHub hub, ISystemInfoProvider systemInfo)
        : base(hub)
    {
        _systemInfo = systemInfo;
        Windows = WindowsVersion = InstalledOn = ComputerName = Architecture = MetricFormatter.Pending;
        Processor = ProcessorDetails = Graphics = Memory = MemoryDetails = MetricFormatter.Pending;
        SystemModel = Motherboard = Bios = BiosDate = Uptime = UptimeLong = BootTime = MetricFormatter.Pending;
    }

    [ObservableProperty]
    public partial string Windows { get; set; }

    [ObservableProperty]
    public partial string WindowsVersion { get; set; }

    [ObservableProperty]
    public partial string InstalledOn { get; set; }

    [ObservableProperty]
    public partial string ComputerName { get; set; }

    [ObservableProperty]
    public partial string Architecture { get; set; }

    [ObservableProperty]
    public partial string Processor { get; set; }

    [ObservableProperty]
    public partial string ProcessorDetails { get; set; }

    [ObservableProperty]
    public partial string Graphics { get; set; }

    [ObservableProperty]
    public partial string Memory { get; set; }

    [ObservableProperty]
    public partial string MemoryDetails { get; set; }

    [ObservableProperty]
    public partial string SystemModel { get; set; }

    [ObservableProperty]
    public partial string Motherboard { get; set; }

    [ObservableProperty]
    public partial string Bios { get; set; }

    [ObservableProperty]
    public partial string BiosDate { get; set; }

    [ObservableProperty]
    public partial string Uptime { get; set; }

    [ObservableProperty]
    public partial string UptimeLong { get; set; }

    [ObservableProperty]
    public partial string BootTime { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [RelayCommand]
    private async Task RefreshAsync() => Apply(await _systemInfo.RefreshAsync(CancellationToken.None));

    protected override async void OnActivated()
    {
        IsLoading = true;
        try
        {
            Apply(await _systemInfo.GetAsync(CancellationToken.None));
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        if ((updated & MetricKind.System) == 0 || snapshot.System is not { } system)
        {
            return;
        }

        Uptime = MetricFormatter.DurationCompact(system.Uptime);
        UptimeLong = MetricFormatter.DurationLong(system.Uptime);
    }

    private void Apply(SystemInformation info)
    {
        var culture = CultureInfo.CurrentCulture;
        var os = info.OperatingSystem;
        Windows = os.ProductName;
        WindowsVersion = os.DisplayVersion is { } display ? $"Version {display} (build {os.Build})" : $"Build {os.Build}";
        InstalledOn = os.InstallDate?.ToString("d", culture) ?? MetricFormatter.NotAvailable;
        ComputerName = info.ComputerName;
        Architecture = info.OsArchitecture == info.ProcessArchitecture
            ? Bitness(info.OsArchitecture)
            : $"{Bitness(info.OsArchitecture)} (Sysora runs as {info.ProcessArchitecture})";

        var processor = info.Processor;
        Processor = processor.Name ?? MetricFormatter.NotAvailable;
        var parts = new List<string>();
        if (processor.PhysicalCores is { } cores)
        {
            parts.Add(MetricFormatter.Plural(cores, "core"));
        }

        parts.Add(MetricFormatter.Plural(processor.LogicalProcessors, "thread"));
        if (processor.BaseFrequencyGHz is { } ghz)
        {
            parts.Add($"base {MetricFormatter.FrequencyGHz(ghz)}");
        }

        ProcessorDetails = string.Join(" · ", parts);

        Graphics = info.Gpus.Count == 0
            ? MetricFormatter.NotAvailable
            : string.Join(Environment.NewLine, info.Gpus.Select(g =>
                g.DedicatedMemoryBytes is > 0 ? $"{g.Name} ({MetricFormatter.Bytes(g.DedicatedMemoryBytes)})" : g.Name));

        Memory = MetricFormatter.Bytes(info.InstalledMemoryBytes ?? info.UsableMemoryBytes);
        MemoryDetails = info.UsableMemoryBytes is { } usable ? $"{MetricFormatter.Bytes(usable)} usable" : string.Empty;

        var firmware = info.Firmware;
        SystemModel = Join(firmware.SystemManufacturer, firmware.SystemModel);
        Motherboard = Join(firmware.BoardManufacturer, firmware.BoardProduct);
        Bios = Join(firmware.BiosVendor, firmware.BiosVersion);
        BiosDate = firmware.BiosReleaseDate ?? MetricFormatter.NotAvailable;
        BootTime = info.BootTime?.ToLocalTime().ToString("g", culture) ?? MetricFormatter.NotAvailable;
        if (Hub.Snapshot.System is { } system)
        {
            Uptime = MetricFormatter.DurationCompact(system.Uptime);
            UptimeLong = MetricFormatter.DurationLong(system.Uptime);
        }
    }

    private static string Bitness(string architecture) => architecture switch
    {
        "X64" => "64-bit (x64)",
        "Arm64" => "64-bit (ARM64)",
        "X86" => "32-bit (x86)",
        _ => architecture,
    };

    private static string Join(string? first, string? second) =>
        (first, second) switch
        {
            (null, null) => MetricFormatter.NotAvailable,
            (null, _) => second!,
            (_, null) => first,
            _ => $"{first} {second}",
        };
}
