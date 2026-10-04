using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseDesk.App.Services;
using PulseDesk.Core;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Models;
using PulseDesk.Core.Settings;
using PulseDesk.Infrastructure;

namespace PulseDesk.App.ViewModels;

/// <summary>A choice of sampling interval.</summary>
public sealed record IntervalOption(int Milliseconds, string Label)
{
    public override string ToString() => Label;

    public static IntervalOption Of(int milliseconds) => new(
        milliseconds,
        milliseconds < 1000 ? $"{milliseconds} ms"
        : milliseconds < 60_000 ? MetricFormatter.Plural(milliseconds / 1000, "second")
        : MetricFormatter.Plural(milliseconds / 60_000, "minute"));
}

/// <summary>Preferences, privacy statement, PulseDesk's own overhead and About information.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly IStartupRegistration _startup;
    private readonly ExternalLauncher _launcher;
    private readonly PulseDeskPaths _paths;
    private bool _loading;
    private long _lastOverheadUpdate;

    public SettingsViewModel(
        UiMetricsHub hub,
        SettingsService settings,
        IStartupRegistration startup,
        ExternalLauncher launcher,
        PulseDeskPaths paths,
        StartupOptions options)
        : base(hub)
    {
        _settings = settings;
        _startup = startup;
        _launcher = launcher;
        _paths = paths;
        IsDemoMode = options.DemoMode;
        _loading = true;
        StartupStatus = SelfCpu = SelfMemory = Throttle = string.Empty;
        RealtimeInterval = RealtimeIntervals[1];
        DetailInterval = DetailIntervals[1];
        StorageInterval = StorageIntervals[1];
        ChartWindow = ChartWindowOption.All[1];
        Load(settings.Current);
        _settings.Changed += (_, e) => Load(e.Current);
    }

    public IReadOnlyList<IntervalOption> RealtimeIntervals { get; } = [IntervalOption.Of(500), IntervalOption.Of(1000), IntervalOption.Of(2000), IntervalOption.Of(5000)];

    public IReadOnlyList<IntervalOption> DetailIntervals { get; } = [IntervalOption.Of(1000), IntervalOption.Of(2000), IntervalOption.Of(5000), IntervalOption.Of(10_000)];

    public IReadOnlyList<IntervalOption> StorageIntervals { get; } = [IntervalOption.Of(10_000), IntervalOption.Of(15_000), IntervalOption.Of(30_000), IntervalOption.Of(60_000), IntervalOption.Of(300_000)];

    public IReadOnlyList<ChartWindowOption> ChartWindows => ChartWindowOption.All;

    public IReadOnlyList<string> LogLevels { get; } = ["Debug", "Information", "Warning", "Error"];

    public bool IsDemoMode { get; }

    public string AppName => AppInfo.Name;

    public string Version => $"Version {AppInfo.InformationalVersion}";

    public string Description => AppInfo.LongDescription;

    public string Authors => AppInfo.Authors;

    public string License => $"{AppInfo.License} license · {AppInfo.Copyright}";

    public string ProjectUrl => AppInfo.ProjectUrl;

    public string DataFolder => _paths.DataDirectory;

    // ---- General --------------------------------------------------------------------------------

    /// <summary>0 = System, 1 = Light, 2 = Dark.</summary>
    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    /// <summary>0 = Quit application, 1 = Minimize to tray.</summary>
    [ObservableProperty]
    public partial int CloseBehaviorIndex { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial string StartupStatus { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool StartInTray { get; set; }

    // ---- Monitoring -----------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial IntervalOption RealtimeInterval { get; set; }

    [ObservableProperty]
    public partial IntervalOption DetailInterval { get; set; }

    [ObservableProperty]
    public partial IntervalOption StorageInterval { get; set; }

    [ObservableProperty]
    public partial ChartWindowOption ChartWindow { get; set; }

    [ObservableProperty]
    public partial bool GpuEnabled { get; set; }

    [ObservableProperty]
    public partial bool NetworkEnabled { get; set; }

    [ObservableProperty]
    public partial bool ReduceActivityWhenHidden { get; set; }

    /// <summary>CPU budget for PulseDesk, percent of total capacity (0 = no limit).</summary>
    [ObservableProperty]
    public partial double CpuBudget { get; set; }

    // ---- Alerts ---------------------------------------------------------------------------------

    [ObservableProperty]
    public partial double CpuWarning { get; set; }

    [ObservableProperty]
    public partial double CpuCritical { get; set; }

    [ObservableProperty]
    public partial double CpuSustainSeconds { get; set; }

    [ObservableProperty]
    public partial double MemoryWarning { get; set; }

    [ObservableProperty]
    public partial double MemoryCritical { get; set; }

    [ObservableProperty]
    public partial double MemorySustainSeconds { get; set; }

    [ObservableProperty]
    public partial double DiskWarning { get; set; }

    [ObservableProperty]
    public partial double DiskCritical { get; set; }

    [ObservableProperty]
    public partial double ProcessMemoryWarning { get; set; }

    [ObservableProperty]
    public partial double ProcessCpuWarning { get; set; }

    // ---- Diagnostics ----------------------------------------------------------------------------

    [ObservableProperty]
    public partial int LogLevelIndex { get; set; }

    [ObservableProperty]
    public partial string SelfCpu { get; set; }

    [ObservableProperty]
    public partial string SelfMemory { get; set; }

    [ObservableProperty]
    public partial string Throttle { get; set; }

    [RelayCommand]
    private void OpenLogsFolder() => _launcher.OpenFolder(Path.Combine(_paths.DataDirectory, "Logs"));

    [RelayCommand]
    private void OpenDataFolder() => _launcher.OpenFolder(_paths.DataDirectory);

    [RelayCommand]
    private void OpenProjectPage()
    {
        if (Uri.TryCreate(AppInfo.ProjectUrl, UriKind.Absolute, out var uri))
        {
            _launcher.OpenUri(uri);
        }
    }

    [RelayCommand]
    private void ResetAlerts() => _settings.Update(s => s with { Alerts = new AlertSettings() });

    protected override void OnActivated()
    {
        Load(_settings.Current);
        UpdateOverhead();
    }

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        // Overhead changes slowly; refresh it every few seconds at most.
        if (Environment.TickCount64 - _lastOverheadUpdate > 3000)
        {
            UpdateOverhead();
        }
    }

    partial void OnThemeIndexChanged(int value) =>
        Save(s => s with { General = s.General with { Theme = (ThemePreference)Math.Clamp(value, 0, 2) } });

    partial void OnCloseBehaviorIndexChanged(int value) =>
        Save(s => s with { General = s.General with { CloseBehavior = value == 0 ? CloseBehavior.Quit : CloseBehavior.MinimizeToTray } });

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        UpdateStartupStatus(_startup.SetEnabled(value));
    }

    partial void OnStartMinimizedChanged(bool value) =>
        Save(s => s with { General = s.General with { StartMinimized = value } });

    partial void OnStartInTrayChanged(bool value) =>
        Save(s => s with { General = s.General with { StartInTray = value } });

    partial void OnIsPausedChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        if (value)
        {
            Hub.Monitor.Pause();
        }
        else
        {
            Hub.Monitor.Resume();
        }
    }

    partial void OnRealtimeIntervalChanged(IntervalOption value) =>
        Save(s => s with
        {
            Monitoring = s.Monitoring with
            {
                CpuIntervalMs = value.Milliseconds,
                MemoryIntervalMs = value.Milliseconds,
                NetworkIntervalMs = value.Milliseconds,
            },
        });

    partial void OnDetailIntervalChanged(IntervalOption value) =>
        Save(s => s with
        {
            Monitoring = s.Monitoring with
            {
                ProcessIntervalMs = value.Milliseconds,
                GpuIntervalMs = value.Milliseconds,
                DiskActivityIntervalMs = value.Milliseconds,
            },
        });

    partial void OnStorageIntervalChanged(IntervalOption value) =>
        Save(s => s with { Monitoring = s.Monitoring with { StorageIntervalSeconds = value.Milliseconds / 1000 } });

    partial void OnChartWindowChanged(ChartWindowOption value) =>
        Save(s => s with { Monitoring = s.Monitoring with { ChartWindowSeconds = value.Seconds } });

    partial void OnGpuEnabledChanged(bool value) => Save(s => s with { Monitoring = s.Monitoring with { GpuEnabled = value } });

    partial void OnNetworkEnabledChanged(bool value) => Save(s => s with { Monitoring = s.Monitoring with { NetworkEnabled = value } });

    partial void OnReduceActivityWhenHiddenChanged(bool value) =>
        Save(s => s with { Monitoring = s.Monitoring with { ReduceActivityWhenHidden = value } });

    partial void OnCpuBudgetChanged(double value) =>
        SaveNumber(value, v => s => s with { Monitoring = s.Monitoring with { MaxSelfCpuPercent = v } });

    partial void OnCpuWarningChanged(double value) => SaveNumber(value, v => s => s with { Alerts = s.Alerts with { CpuWarningPercent = v } });

    partial void OnCpuCriticalChanged(double value) => SaveNumber(value, v => s => s with { Alerts = s.Alerts with { CpuCriticalPercent = v } });

    partial void OnCpuSustainSecondsChanged(double value) => SaveNumber(value, v => s => s with { Alerts = s.Alerts with { CpuSustainSeconds = (int)v } });

    partial void OnMemoryWarningChanged(double value) => SaveNumber(value, v => s => s with { Alerts = s.Alerts with { MemoryWarningPercent = v } });

    partial void OnMemoryCriticalChanged(double value) => SaveNumber(value, v => s => s with { Alerts = s.Alerts with { MemoryCriticalPercent = v } });

    partial void OnMemorySustainSecondsChanged(double value) => SaveNumber(value, v => s => s with { Alerts = s.Alerts with { MemorySustainSeconds = (int)v } });

    partial void OnDiskWarningChanged(double value) => SaveNumber(value, v => s => s with { Alerts = s.Alerts with { DiskWarningPercent = v } });

    partial void OnDiskCriticalChanged(double value) => SaveNumber(value, v => s => s with { Alerts = s.Alerts with { DiskCriticalPercent = v } });

    partial void OnProcessMemoryWarningChanged(double value) => SaveNumber(value, v => s => s with { Alerts = s.Alerts with { ProcessMemoryWarningPercent = v } });

    partial void OnProcessCpuWarningChanged(double value) => SaveNumber(value, v => s => s with { Alerts = s.Alerts with { ProcessCpuWarningPercent = v } });

    partial void OnLogLevelIndexChanged(int value) =>
        Save(s => s with { Diagnostics = s.Diagnostics with { LogLevel = (LogVerbosity)Math.Clamp(value, 0, 3) } });

    /// <summary>Copies the stored settings into the view model without writing them back.</summary>
    private void Load(AppSettings settings)
    {
        _loading = true;
        try
        {
            var general = settings.General;
            ThemeIndex = (int)general.Theme;
            CloseBehaviorIndex = general.CloseBehavior == CloseBehavior.Quit ? 0 : 1;
            StartMinimized = general.StartMinimized;
            StartInTray = general.StartInTray;
            var state = _startup.GetState();
            StartWithWindows = state is StartupState.Enabled or StartupState.DisabledByUser;
            UpdateStartupStatus(state);

            var monitoring = settings.Monitoring;
            IsPaused = Hub.Monitor.IsPaused;
            RealtimeInterval = Pick(RealtimeIntervals, monitoring.CpuIntervalMs);
            DetailInterval = Pick(DetailIntervals, monitoring.ProcessIntervalMs);
            StorageInterval = Pick(StorageIntervals, monitoring.StorageIntervalSeconds * 1000);
            ChartWindow = ChartWindowOption.FromSeconds(monitoring.ChartWindowSeconds);
            GpuEnabled = monitoring.GpuEnabled;
            NetworkEnabled = monitoring.NetworkEnabled;
            ReduceActivityWhenHidden = monitoring.ReduceActivityWhenHidden;
            CpuBudget = monitoring.MaxSelfCpuPercent;

            var alerts = settings.Alerts;
            CpuWarning = alerts.CpuWarningPercent;
            CpuCritical = alerts.CpuCriticalPercent;
            CpuSustainSeconds = alerts.CpuSustainSeconds;
            MemoryWarning = alerts.MemoryWarningPercent;
            MemoryCritical = alerts.MemoryCriticalPercent;
            MemorySustainSeconds = alerts.MemorySustainSeconds;
            DiskWarning = alerts.DiskWarningPercent;
            DiskCritical = alerts.DiskCriticalPercent;
            ProcessMemoryWarning = alerts.ProcessMemoryWarningPercent;
            ProcessCpuWarning = alerts.ProcessCpuWarningPercent;

            LogLevelIndex = (int)settings.Diagnostics.LogLevel;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!_loading)
        {
            _settings.Update(change);
        }
    }

    /// <summary>Saves a number box value; an emptied box (NaN) is ignored instead of resetting the setting.</summary>
    private void SaveNumber(double value, Func<double, Func<AppSettings, AppSettings>> change)
    {
        if (double.IsFinite(value))
        {
            Save(change(value));
        }
    }

    private void UpdateStartupStatus(StartupState state) =>
        StartupStatus = state switch
        {
            StartupState.Enabled => "PulseDesk starts when you sign in.",
            StartupState.DisabledByUser => "Registered, but turned off in Windows Settings › Apps › Startup.",
            StartupState.Unknown => "The startup registration could not be read.",
            _ => "Uses the standard per-user Run key; visible in Settings › Apps › Startup.",
        };

    private void UpdateOverhead()
    {
        _lastOverheadUpdate = Environment.TickCount64;
        var usage = Hub.Monitor.SelfUsage;
        SelfCpu = usage.CpuPercent is { } cpu ? $"≈ {MetricFormatter.Percent(cpu, 2)}" : "Measuring…";
        SelfMemory = usage.WorkingSetBytes > 0 ? $"≈ {MetricFormatter.Bytes((ulong)usage.WorkingSetBytes)}" : "Measuring…";
        Throttle = usage.ThrottleFactor > 1.01
            ? $"Intervals stretched ×{usage.ThrottleFactor:0.##} to stay within the CPU budget"
            : "Running at the configured intervals";
    }

    private static IntervalOption Pick(IReadOnlyList<IntervalOption> options, int milliseconds) =>
        options.MinBy(o => Math.Abs(o.Milliseconds - milliseconds))!;
}
