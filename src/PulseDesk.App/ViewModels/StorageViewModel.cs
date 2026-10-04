using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseDesk.App.Services;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.Models;
using PulseDesk.Core.Monitoring;
using PulseDesk.Core.Settings;

namespace PulseDesk.App.ViewModels;

/// <summary>Capacity and activity of every volume.</summary>
public sealed partial class StorageViewModel : PageViewModel
{
    private readonly SettingsService _settings;

    public StorageViewModel(UiMetricsHub hub, SettingsService settings)
        : base(hub)
    {
        _settings = settings;
        Summary = MetricFormatter.Pending;
    }

    public ObservableCollection<DriveItemViewModel> Drives { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial bool IsUnavailable { get; set; }

    [RelayCommand]
    private void Refresh() => Hub.Monitor.RequestRefresh(MetricKind.Storage | MetricKind.DiskActivity);

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
        Summary = $"{MetricFormatter.Plural(drives.Count, "volume")} · {MetricFormatter.Bytes(free)} free of {MetricFormatter.Bytes(total)}";
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
        UsageText = $"{MetricFormatter.Percent(drive.UsedPercent)} used";
        Used = MetricFormatter.Bytes(drive.UsedBytes);
        Free = MetricFormatter.Bytes(drive.FreeBytes);
        Total = MetricFormatter.Bytes(drive.TotalBytes);
        ActiveTime = MetricFormatter.Percent(activity?.ActiveTimePercent);
        Read = MetricFormatter.BytesPerSecond(activity?.ReadBytesPerSecond);
        Write = MetricFormatter.BytesPerSecond(activity?.WriteBytesPerSecond);
    }

    private static string DefaultName(DriveKind kind) => kind switch
    {
        DriveKind.Removable => "Removable disk",
        DriveKind.Optical => "Optical drive",
        DriveKind.RamDisk => "RAM disk",
        _ => "Local disk",
    };

    private static string KindName(DriveKind kind) => kind switch
    {
        DriveKind.Fixed => "Fixed",
        DriveKind.Removable => "Removable",
        DriveKind.Optical => "Optical",
        DriveKind.RamDisk => "RAM disk",
        DriveKind.Network => "Network",
        _ => string.Empty,
    };
}
