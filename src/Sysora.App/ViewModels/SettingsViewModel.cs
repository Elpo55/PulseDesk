using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysora.App.Services;
using Sysora.Core;
using Sysora.Core.Formatting;
using Sysora.Core.Gaming;
using Sysora.Core.History;
using Sysora.Core.Interfaces;
using Sysora.Core.Models;
using Sysora.Core.Settings;
using Sysora.Infrastructure;

namespace Sysora.App.ViewModels;

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

/// <summary>Preferences, privacy statement, Sysora's own overhead and About information.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly IStartupRegistration _startup;
    private readonly ExternalLauncher _launcher;
    private readonly SysoraPaths _paths;
    private readonly HistoryRecorder _history;
    private readonly DialogService _dialogs;
    private readonly GameSessionService _games;
    private bool _loading;
    private long _lastOverheadUpdate;

    public SettingsViewModel(
        UiMetricsHub hub,
        SettingsService settings,
        IStartupRegistration startup,
        ExternalLauncher launcher,
        SysoraPaths paths,
        HistoryRecorder history,
        DialogService dialogs,
        GameSessionService games,
        StartupOptions options)
        : base(hub)
    {
        _settings = settings;
        _startup = startup;
        _launcher = launcher;
        _paths = paths;
        _history = history;
        _dialogs = dialogs;
        _games = games;
        IsDemoMode = options.DemoMode;
        _loading = true;
        StartupStatus = SelfCpu = SelfMemory = Throttle = HistoryStatus = string.Empty;
        ReplayDuration = ReplayDurations[2];
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

    public IReadOnlyList<IntervalOption> ReplayDurations { get; } =
        SettingsValidator.ReplayMinuteOptions.Select(m => IntervalOption.Of(m * 60_000)).ToArray();

    public bool IsDemoMode { get; }

    public string AppName => AppInfo.Name;

    public string Tagline => AppInfo.Tagline;

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

    /// <summary>CPU budget for Sysora, percent of total capacity (0 = no limit).</summary>
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

    // ---- Smart alerts ---------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool SmartAlertsEnabled { get; set; }

    [ObservableProperty]
    public partial double AlertCpuPercent { get; set; }

    [ObservableProperty]
    public partial double AlertCpuMinutes { get; set; }

    [ObservableProperty]
    public partial double AlertMemoryPercent { get; set; }

    [ObservableProperty]
    public partial double AlertMemoryMinutes { get; set; }

    [ObservableProperty]
    public partial double AlertDiskPercent { get; set; }

    [ObservableProperty]
    public partial double AlertDiskMinutes { get; set; }

    [ObservableProperty]
    public partial double AlertAppCpuPercent { get; set; }

    [ObservableProperty]
    public partial double AlertAppCpuMinutes { get; set; }

    [ObservableProperty]
    public partial double AlertGrowthPoints { get; set; }

    [ObservableProperty]
    public partial double AlertGrowthMinutes { get; set; }

    [ObservableProperty]
    public partial bool AlertUnusual { get; set; }

    [ObservableProperty]
    public partial double AlertUnusualMinutes { get; set; }

    [ObservableProperty]
    public partial double AlertLowDiskPercent { get; set; }

    [ObservableProperty]
    public partial double AlertCooldownMinutes { get; set; }

    [ObservableProperty]
    public partial double AlertMaxPerHour { get; set; }

    [ObservableProperty]
    public partial bool AlertNotifications { get; set; }

    // ---- History --------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool RecordHistory { get; set; }

    [ObservableProperty]
    public partial IntervalOption ReplayDuration { get; set; }

    [ObservableProperty]
    public partial double DetailRetentionDays { get; set; }

    [ObservableProperty]
    public partial double SummaryRetentionDays { get; set; }

    [ObservableProperty]
    public partial string HistoryStatus { get; set; }

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

    // ---- Gaming -----------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool GamingEnabled { get; set; }

    [ObservableProperty]
    public partial bool DetectWindowsGames { get; set; }

    [ObservableProperty]
    public partial bool DetectLibraryGames { get; set; }

    [ObservableProperty]
    public partial double MinimumSessionMinutes { get; set; }

    [ObservableProperty]
    public partial bool NotifyRecap { get; set; }

    [ObservableProperty]
    public partial bool ReduceMonitoringDuringGames { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddGameCommand))]
    public partial RunningAppOption? SelectedRunningApp { get; set; }

    [ObservableProperty]
    public partial bool HasAddedGames { get; set; }

    [ObservableProperty]
    public partial bool HasExcludedGames { get; set; }

    /// <summary>Running applications with a known executable path, offered to be marked as games.</summary>
    public ObservableCollection<RunningAppOption> RunningApps { get; } = [];

    public ObservableCollection<GameListItemViewModel> AddedGames { get; } = [];

    public ObservableCollection<GameListItemViewModel> ExcludedGames { get; } = [];

    [RelayCommand]
    private void RefreshRunningApps()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var added = _settings.Current.Gaming.AddedGames;
        var apps = (Hub.Snapshot.Processes?.Processes ?? [])
            .Where(p => p.ExecutablePath is { Length: > 0 } path
                && !path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                && !added.Contains(path, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(p => p.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(p => new RunningAppOption(p.ExecutablePath!, $"{p.Name} — {p.ExecutablePath}"))
            .OrderBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        RunningApps.Clear();
        foreach (var app in apps)
        {
            RunningApps.Add(app);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddGame))]
    private void AddGame()
    {
        if (SelectedRunningApp is { } app)
        {
            _games.MarkAsGame(app.Path);
            SelectedRunningApp = null;
            RefreshRunningApps();
        }
    }

    private bool CanAddGame() => SelectedRunningApp is not null;

    partial void OnGamingEnabledChanged(bool value) => Save(s => s with { Gaming = s.Gaming with { Enabled = value } });

    partial void OnDetectWindowsGamesChanged(bool value) => Save(s => s with { Gaming = s.Gaming with { DetectWindowsGames = value } });

    partial void OnDetectLibraryGamesChanged(bool value) => Save(s => s with { Gaming = s.Gaming with { DetectLibraryGames = value } });

    partial void OnMinimumSessionMinutesChanged(double value) => SaveNumber(value, v => s => s with { Gaming = s.Gaming with { MinimumSessionMinutes = (int)v } });

    partial void OnNotifyRecapChanged(bool value) => Save(s => s with { Gaming = s.Gaming with { NotifyRecap = value } });

    partial void OnReduceMonitoringDuringGamesChanged(bool value) => Save(s => s with { Gaming = s.Gaming with { ReduceMonitoringDuringGames = value } });

    /// <summary>Removes an executable from both game lists (back to automatic detection).</summary>
    private void RemoveGameMark(string path) =>
        _settings.Update(s => s with
        {
            Gaming = s.Gaming with
            {
                AddedGames = s.Gaming.AddedGames.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToArray(),
                ExcludedGames = s.Gaming.ExcludedGames.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToArray(),
            },
        });

    private void LoadGameLists(GamingSettings gaming)
    {
        void Sync(ObservableCollection<GameListItemViewModel> items, IReadOnlyList<string> paths)
        {
            if (items.Select(i => i.Path).SequenceEqual(paths, StringComparer.Ordinal))
            {
                return;
            }

            items.Clear();
            foreach (var path in paths)
            {
                items.Add(new GameListItemViewModel(path, RemoveGameMark));
            }
        }

        Sync(AddedGames, gaming.AddedGames);
        Sync(ExcludedGames, gaming.ExcludedGames);
        HasAddedGames = AddedGames.Count > 0;
        HasExcludedGames = ExcludedGames.Count > 0;
    }

    [RelayCommand]
    private void ResetAlerts() => _settings.Update(s => s with { Alerts = new AlertSettings() });

    [RelayCommand]
    private void ResetSmartAlerts() => _settings.Update(s => s with { SmartAlerts = new SmartAlertSettings() });

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Delete the local history?",
            "Sysora will delete the recorded performance history, application usage, events, alerts, detected changes and game sessions from this PC. The usual-behavior baseline will be learned again. This cannot be undone.",
            "Delete history");
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _history.ClearAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HistoryStatus = $"The history could not be deleted: {ex.Message}";
            return;
        }

        await UpdateHistoryStatusAsync();
    }

    protected override void OnActivated()
    {
        Load(_settings.Current);
        UpdateOverhead();
        RefreshRunningApps();
        _ = UpdateHistoryStatusAsync();
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

    partial void OnSmartAlertsEnabledChanged(bool value) => Save(s => s with { SmartAlerts = s.SmartAlerts with { Enabled = value } });

    partial void OnAlertCpuPercentChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { CpuPercent = v } });

    partial void OnAlertCpuMinutesChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { CpuMinutes = (int)v } });

    partial void OnAlertMemoryPercentChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { MemoryPercent = v } });

    partial void OnAlertMemoryMinutesChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { MemoryMinutes = (int)v } });

    partial void OnAlertDiskPercentChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { DiskActivePercent = v } });

    partial void OnAlertDiskMinutesChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { DiskMinutes = (int)v } });

    partial void OnAlertAppCpuPercentChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { AppCpuPercent = v } });

    partial void OnAlertAppCpuMinutesChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { AppCpuMinutes = (int)v } });

    partial void OnAlertGrowthPointsChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { MemoryGrowthPoints = v } });

    partial void OnAlertGrowthMinutesChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { MemoryGrowthMinutes = (int)v } });

    partial void OnAlertUnusualChanged(bool value) => Save(s => s with { SmartAlerts = s.SmartAlerts with { UnusualActivity = value } });

    partial void OnAlertUnusualMinutesChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { UnusualMinutes = (int)v } });

    partial void OnAlertLowDiskPercentChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { LowDiskFreePercent = v } });

    partial void OnAlertCooldownMinutesChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { CooldownMinutes = (int)v } });

    partial void OnAlertMaxPerHourChanged(double value) => SaveNumber(value, v => s => s with { SmartAlerts = s.SmartAlerts with { MaxNewAlertsPerHour = (int)v } });

    partial void OnAlertNotificationsChanged(bool value) => Save(s => s with { SmartAlerts = s.SmartAlerts with { ShowNotifications = value } });

    partial void OnRecordHistoryChanged(bool value) => Save(s => s with { History = s.History with { RecordHistory = value } });

    partial void OnReplayDurationChanged(IntervalOption value) =>
        Save(s => s with { History = s.History with { ReplayMinutes = value.Milliseconds / 60_000 } });

    partial void OnDetailRetentionDaysChanged(double value) => SaveNumber(value, v => s => s with { History = s.History with { DetailRetentionDays = (int)v } });

    partial void OnSummaryRetentionDaysChanged(double value) => SaveNumber(value, v => s => s with { History = s.History with { SummaryRetentionDays = (int)v } });

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

            var smart = settings.SmartAlerts;
            SmartAlertsEnabled = smart.Enabled;
            AlertCpuPercent = smart.CpuPercent;
            AlertCpuMinutes = smart.CpuMinutes;
            AlertMemoryPercent = smart.MemoryPercent;
            AlertMemoryMinutes = smart.MemoryMinutes;
            AlertDiskPercent = smart.DiskActivePercent;
            AlertDiskMinutes = smart.DiskMinutes;
            AlertAppCpuPercent = smart.AppCpuPercent;
            AlertAppCpuMinutes = smart.AppCpuMinutes;
            AlertGrowthPoints = smart.MemoryGrowthPoints;
            AlertGrowthMinutes = smart.MemoryGrowthMinutes;
            AlertUnusual = smart.UnusualActivity;
            AlertUnusualMinutes = smart.UnusualMinutes;
            AlertLowDiskPercent = smart.LowDiskFreePercent;
            AlertCooldownMinutes = smart.CooldownMinutes;
            AlertMaxPerHour = smart.MaxNewAlertsPerHour;
            AlertNotifications = smart.ShowNotifications;

            var history = settings.History;
            RecordHistory = history.RecordHistory;
            ReplayDuration = Pick(ReplayDurations, history.ReplayMinutes * 60_000);
            DetailRetentionDays = history.DetailRetentionDays;
            SummaryRetentionDays = history.SummaryRetentionDays;

            var gaming = settings.Gaming;
            GamingEnabled = gaming.Enabled;
            DetectWindowsGames = gaming.DetectWindowsGames;
            DetectLibraryGames = gaming.DetectLibraryGames;
            MinimumSessionMinutes = gaming.MinimumSessionMinutes;
            NotifyRecap = gaming.NotifyRecap;
            ReduceMonitoringDuringGames = gaming.ReduceMonitoringDuringGames;
            LoadGameLists(gaming);

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
            StartupState.Enabled => "Sysora starts when you sign in.",
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

    private async Task UpdateHistoryStatusAsync()
    {
        try
        {
            var info = await _history.GetStorageInfoAsync(CancellationToken.None);
            var since = info.OldestData is { } oldest ? $" · data since {InsightDisplay.Time(oldest)}" : " · no data yet";
            HistoryStatus = info.SizeBytes > 0
                ? $"{MetricFormatter.Bytes((ulong)info.SizeBytes)} on disk{since}"
                : $"{info.Location}{since}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HistoryStatus = $"History not available: {ex.Message}";
        }
    }

    private static IntervalOption Pick(IReadOnlyList<IntervalOption> options, int milliseconds) =>
        options.MinBy(o => Math.Abs(o.Milliseconds - milliseconds))!;
}

/// <summary>A running application that can be marked as a game.</summary>
/// <param name="Path">Executable path.</param>
/// <param name="Label">Name and path.</param>
public sealed record RunningAppOption(string Path, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An executable in one of the user's game lists.</summary>
public sealed partial class GameListItemViewModel(string path, Action<string> remove)
{
    public string Path { get; } = path;

    [RelayCommand]
    private void Remove() => remove(Path);
}
