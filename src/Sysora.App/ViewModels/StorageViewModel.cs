using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysora.App.Services;
using Sysora.Core.Formatting;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;
using Sysora.Core.Settings;
using Sysora.Localization;

namespace Sysora.App.ViewModels;

/// <summary>Capacity and activity of every volume.</summary>
public sealed partial class StorageViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly NavigationService _navigation;

    public StorageViewModel(UiMetricsHub hub, SettingsService settings, NavigationService navigation)
        : base(hub)
    {
        _settings = settings;
        _navigation = navigation;
        Summary = MetricFormatter.Pending;
    }

    public ObservableCollection<DriveItemViewModel> Drives { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial bool IsUnavailable { get; set; }

    [RelayCommand]
    private void Refresh() => Hub.Monitor.RequestRefresh(MetricKind.Storage | MetricKind.DiskActivity);

    [RelayCommand]
    private void OpenLargeFiles() => _navigation.Navigate(AppPage.LargeFiles);

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        if ((updated & (MetricKind.Storage | MetricKind.DiskActivity)) == 0)
        {
            return;
        }

        IsUnavailable = snapshot.IsUnavailable(MetricKind.Storage);
        if (snapshot.Storage is not { } drives)
        {
            Summary = Display.Format<IReadOnlyList<StorageMetrics>>(snapshot, MetricKind.Storage, null, _ => string.Empty);
            return;
        }

        var ordered = drives.OrderByDescending(d => d.IsSystemDrive).ThenBy(d => d.Drive, StringComparer.OrdinalIgnoreCase).ToArray();
        var alerts = _settings.Current.Alerts;
        CollectionSync.Resize(Drives, ordered.Length, _ => new DriveItemViewModel(), (item, i) =>
        {
            var drive = ordered[i];
            var activity = snapshot.DiskActivity?.FirstOrDefault(a => string.Equals(a.Drive, drive.Letter, StringComparison.OrdinalIgnoreCase));
            item.Update(drive, activity, alerts);
        });

        var total = (ulong)drives.Sum(d => (decimal)d.TotalBytes);
        var free = (ulong)drives.Sum(d => (decimal)d.FreeBytes);
        Summary = Text.Format(UiStrings.Storage_Summary, Text.Plural(drives.Count, UiStrings.Count_Volume_One, UiStrings.Count_Volume_Other), MetricFormatter.Bytes(free), MetricFormatter.Bytes(total));
    }
}

/// <summary>One volume card.</summary>
public sealed partial class DriveItemViewModel : ObservableObject
{
    public DriveItemViewModel()
    {
        Title = Letter = Details = Used = Free = Total = UsageText = ActiveTime = Read = Write = string.Empty;
    }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Letter { get; set; }

    /// <summary>File system and drive type, e.g. "NTFS · Fixed".</summary>
    [ObservableProperty]
    public partial string Details { get; set; }

    [ObservableProperty]
    public partial bool IsSystemDrive { get; set; }

    [ObservableProperty]
    public partial double UsedPercent { get; set; }

    [ObservableProperty]
    public partial HealthStatus Level { get; set; }

    [ObservableProperty]
    public partial string UsageText { get; set; }

    [ObservableProperty]
    public partial string Used { get; set; }

    [ObservableProperty]
    public partial string Free { get; set; }

    [ObservableProperty]
    public partial string Total { get; set; }

    [ObservableProperty]
    public partial string ActiveTime { get; set; }

    [ObservableProperty]
    public partial string Read { get; set; }

    [ObservableProperty]
    public partial string Write { get; set; }

    public void Update(StorageMetrics drive, DiskActivityMetrics? activity, AlertSettings alerts)
    {
        var name = drive.Label ?? DefaultName(drive.Kind);
        Title = $"{name} ({drive.Letter})";
        Letter = drive.Letter;
        Details = string.Join(" · ", new[] { drive.FileSystem, KindName(drive.Kind) }.Where(s => !string.IsNullOrEmpty(s)));
        IsSystemDrive = drive.IsSystemDrive;
        UsedPercent = drive.UsedPercent;
        var used = Math.Round(drive.UsedPercent);
        Level = drive.Kind != DriveKind.Fixed ? HealthStatus.Normal
            : used >= alerts.DiskCriticalPercent ? HealthStatus.Critical
            : used >= alerts.DiskWarningPercent ? HealthStatus.Warning
            : HealthStatus.Normal;
        UsageText = Text.Format(UiStrings.Storage_UsedPercent, MetricFormatter.Percent(drive.UsedPercent));
        Used = MetricFormatter.Bytes(drive.UsedBytes);
        Free = MetricFormatter.Bytes(drive.FreeBytes);
        Total = MetricFormatter.Bytes(drive.TotalBytes);
        ActiveTime = MetricFormatter.Percent(activity?.ActiveTimePercent);
        Read = MetricFormatter.BytesPerSecond(activity?.ReadBytesPerSecond);
        Write = MetricFormatter.BytesPerSecond(activity?.WriteBytesPerSecond);
    }

    private static string DefaultName(DriveKind kind) => kind switch
    {
        DriveKind.Removable => UiStrings.Storage_RemovableDisk,
        DriveKind.Optical => UiStrings.Storage_OpticalDrive,
        DriveKind.RamDisk => UiStrings.Storage_RamDisk,
        _ => UiStrings.Storage_LocalDisk,
    };

    private static string KindName(DriveKind kind) => kind switch
    {
        DriveKind.Fixed => UiStrings.Storage_Kind_Fixed,
        DriveKind.Removable => UiStrings.Storage_Kind_Removable,
        DriveKind.Optical => UiStrings.Storage_Kind_Optical,
        DriveKind.RamDisk => UiStrings.Storage_RamDisk,
        DriveKind.Network => UiStrings.Common_Network,
        _ => string.Empty,
    };
}
