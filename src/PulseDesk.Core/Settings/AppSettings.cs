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
