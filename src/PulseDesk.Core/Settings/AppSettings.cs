namespace PulseDesk.Core.Settings;

/// <summary>
/// User preferences, persisted as JSON in the local application data folder.
/// Immutable: change settings with <c>with</c> expressions through <see cref="SettingsService.Update"/>.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Version of the settings schema, for future migrations.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public GeneralSettings General { get; init; } = new();

    public MonitoringSettings Monitoring { get; init; } = new();

    public AlertSettings Alerts { get; init; } = new();

    public SmartAlertSettings SmartAlerts { get; init; } = new();

    public HistorySettings History { get; init; } = new();

    public GamingSettings Gaming { get; init; } = new();

    public DiagnosticsSettings Diagnostics { get; init; } = new();

    public WindowSettings Window { get; init; } = new();

    public const int CurrentSchemaVersion = 1;

    /// <summary>Default settings.</summary>
    public static AppSettings Default { get; } = new();
}

/// <summary>Application theme.</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>What closing the main window does.</summary>
public enum CloseBehavior
{
    /// <summary>Hide the window and keep monitoring from the notification area.</summary>
    MinimizeToTray,

    /// <summary>Exit PulseDesk.</summary>
    Quit,
}

/// <summary>General preferences.</summary>
public sealed record GeneralSettings
{
    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public CloseBehavior CloseBehavior { get; init; } = CloseBehavior.MinimizeToTray;

    /// <summary>When started with Windows, open minimized to the taskbar.</summary>
    public bool StartMinimized { get; init; }

    /// <summary>When started with Windows, stay in the notification area without showing the window.</summary>
    public bool StartInTray { get; init; }

    /// <summary>Whether the "PulseDesk is still running in the notification area" hint was already shown once.</summary>
    public bool TrayHintShown { get; init; }
}

/// <summary>Collection preferences. Intervals are validated by <see cref="SettingsValidator"/>.</summary>
public sealed record MonitoringSettings
{
    public int CpuIntervalMs { get; init; } = 1000;

    public int MemoryIntervalMs { get; init; } = 1000;

    public int ProcessIntervalMs { get; init; } = 2000;

    public int GpuIntervalMs { get; init; } = 2000;

    public int NetworkIntervalMs { get; init; } = 1000;

    public int DiskActivityIntervalMs { get; init; } = 2000;

    public int StorageIntervalSeconds { get; init; } = 15;

    public bool GpuEnabled { get; init; } = true;

    public bool NetworkEnabled { get; init; } = true;

    /// <summary>Default time span shown by charts, in seconds.</summary>
    public int ChartWindowSeconds { get; init; } = 60;

    /// <summary>Slow down on-screen-only metrics while the window is hidden.</summary>
    public bool ReduceActivityWhenHidden { get; init; } = true;

    /// <summary>
    /// CPU budget for PulseDesk itself, in percent of total CPU capacity. When exceeded, collection
    /// intervals are stretched automatically. 0 disables the budget.
    /// </summary>
    public double MaxSelfCpuPercent { get; init; } = 2.0;
}

/// <summary>Thresholds used by the health indicators.</summary>
public sealed record AlertSettings
{
    public double CpuWarningPercent { get; init; } = 80;

    public double CpuCriticalPercent { get; init; } = 95;

    /// <summary>How long CPU usage must stay above a threshold before it is reported.</summary>
    public int CpuSustainSeconds { get; init; } = 30;

    public double MemoryWarningPercent { get; init; } = 85;

    public double MemoryCriticalPercent { get; init; } = 95;

    /// <summary>How long memory usage must stay above a threshold before it is reported.</summary>
    public int MemorySustainSeconds { get; init; } = 60;

    public double DiskWarningPercent { get; init; } = 85;

    public double DiskCriticalPercent { get; init; } = 95;

    /// <summary>An application using more than this share of physical memory is reported.</summary>
    public double ProcessMemoryWarningPercent { get; init; } = 30;

    /// <summary>An application using more than this share of total CPU for the CPU sustain time is reported.</summary>
    public double ProcessCpuWarningPercent { get; init; } = 50;
}

/// <summary>
/// Rules of the intelligent alerts. Unlike the health thresholds (instant indicators), alerts require a
/// condition to last for minutes, take the PC's usual behavior into account and are rate-limited.
/// </summary>
public sealed record SmartAlertSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>CPU usage that must be sustained for <see cref="CpuMinutes"/>.</summary>
    public double CpuPercent { get; init; } = 90;

    public int CpuMinutes { get; init; } = 5;

    /// <summary>Memory usage that must be sustained for <see cref="MemoryMinutes"/>.</summary>
    public double MemoryPercent { get; init; } = 90;

    public int MemoryMinutes { get; init; } = 3;

    /// <summary>Disk active time that must be sustained for <see cref="DiskMinutes"/>.</summary>
    public double DiskActivePercent { get; init; } = 95;

    public int DiskMinutes { get; init; } = 2;

    /// <summary>Share of total CPU one application must use for <see cref="AppCpuMinutes"/>.</summary>
    public double AppCpuPercent { get; init; } = 25;

    public int AppCpuMinutes { get; init; } = 5;

    /// <summary>Rise of memory usage (percentage points) over <see cref="MemoryGrowthMinutes"/> that is reported.</summary>
    public double MemoryGrowthPoints { get; init; } = 10;

    public int MemoryGrowthMinutes { get; init; } = 20;

    /// <summary>Alert when CPU, memory or disk stay above the PC's usual range (needs the baseline).</summary>
    public bool UnusualActivity { get; init; } = true;

    /// <summary>How long activity must stay unusual before an alert.</summary>
    public int UnusualMinutes { get; init; } = 8;

    /// <summary>Free space on the system disk, percent of its size, below which an alert is raised.</summary>
    public double LowDiskFreePercent { get; init; } = 10;

    /// <summary>A condition that ends and comes back within this time reopens the same alert instead of creating a new one.</summary>
    public int CooldownMinutes { get; init; } = 15;

    /// <summary>At most this many new alerts per hour; extra ones are not raised (and logged).</summary>
    public int MaxNewAlertsPerHour { get; init; } = 6;

    /// <summary>Show a Windows notification for new warnings and critical alerts.</summary>
    public bool ShowNotifications { get; init; }
}

/// <summary>Performance history preferences (replay buffer and long-term local history).</summary>
public sealed record HistorySettings
{
    /// <summary>Record aggregated history to the local database (replay of the last minutes always works).</summary>
    public bool RecordHistory { get; init; } = true;

    /// <summary>Detailed, in-memory history available in Replay, in minutes.</summary>
    public int ReplayMinutes { get; init; } = 15;

    /// <summary>How long minute-level data is kept, in days.</summary>
    public int DetailRetentionDays { get; init; } = 7;

    /// <summary>How long hourly summaries, events, alerts and changes are kept, in days.</summary>
    public int SummaryRetentionDays { get; init; } = 90;
}

/// <summary>
/// Gaming sessions: how PulseDesk recognizes a game, and the applications the user marked as games or as not
/// games. Executable paths are compared case-insensitively.
/// </summary>
public sealed record GamingSettings
{
    /// <summary>Follow game sessions and produce a recap when the game closes.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Treat executables that Windows recognizes as games (Game Bar's list for this user) as games.</summary>
    public bool DetectWindowsGames { get; init; } = true;

    /// <summary>Treat executables installed in a game library folder (Steam, Epic, Xbox, GOG...) as likely games.</summary>
    public bool DetectLibraryGames { get; init; } = true;

    /// <summary>Sessions shorter than this are not kept (launchers that restart, quick checks).</summary>
    public int MinimumSessionMinutes { get; init; } = 2;

    /// <summary>Show a Windows notification when a recap is ready.</summary>
    public bool NotifyRecap { get; init; } = true;

    /// <summary>
    /// While a game runs and PulseDesk is not the active window, stop the window's live updates and sample detailed
    /// metrics less often, so PulseDesk takes as little as possible from the game. Recording continues.
    /// </summary>
    public bool ReduceMonitoringDuringGames { get; init; } = true;

    /// <summary>Executables the user marked as games.</summary>
    public IReadOnlyList<string> AddedGames { get; init; } = [];

    /// <summary>Executables the user marked as not games: never followed, whatever the detection says.</summary>
    public IReadOnlyList<string> ExcludedGames { get; init; } = [];

    // Lists compare by content: a record compares them by reference, so settings read back from disk would never
    // equal the ones written, and "no change" would not be detected.
    public bool Equals(GamingSettings? other) =>
        other is not null
        && Enabled == other.Enabled
        && DetectWindowsGames == other.DetectWindowsGames
        && DetectLibraryGames == other.DetectLibraryGames
        && MinimumSessionMinutes == other.MinimumSessionMinutes
        && NotifyRecap == other.NotifyRecap
        && ReduceMonitoringDuringGames == other.ReduceMonitoringDuringGames
        && AddedGames.SequenceEqual(other.AddedGames, StringComparer.Ordinal)
        && ExcludedGames.SequenceEqual(other.ExcludedGames, StringComparer.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(Enabled, DetectWindowsGames, DetectLibraryGames, MinimumSessionMinutes, NotifyRecap, ReduceMonitoringDuringGames, AddedGames.Count, ExcludedGames.Count);
}

/// <summary>Minimum level written to the local log files.</summary>
public enum LogVerbosity
{
    Debug,
    Information,
    Warning,
    Error,
}

/// <summary>Diagnostics preferences.</summary>
public sealed record DiagnosticsSettings
{
    public LogVerbosity LogLevel { get; init; } = LogVerbosity.Information;
}

/// <summary>Main window placement, restored on the next start.</summary>
public sealed record WindowSettings
{
    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 840;

    public bool IsMaximized { get; init; }
}
