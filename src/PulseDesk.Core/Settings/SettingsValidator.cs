namespace PulseDesk.Core.Settings;

/// <summary>
/// Brings settings back into supported ranges. Applied to everything loaded from disk and to every
/// update, so a hand-edited or outdated file can never push PulseDesk into an unreasonable state
/// (for example, sampling every millisecond).
/// </summary>
public static class SettingsValidator
{
    /// <summary>Time spans offered by charts, in seconds (30 s, 1 min, 5 min, 15 min, 30 min).</summary>
    public static IReadOnlyList<int> ChartWindowOptions { get; } = [30, 60, 300, 900, 1800];

    /// <summary>Longest chart window, which sizes the in-memory history.</summary>
    public static TimeSpan MaxChartWindow => TimeSpan.FromSeconds(ChartWindowOptions[^1]);

    /// <summary>Durations offered for the in-memory replay buffer, in minutes.</summary>
    public static IReadOnlyList<int> ReplayMinuteOptions { get; } = [5, 10, 15, 30, 60];

    /// <summary>Fastest allowed sampling interval for any metric.</summary>
    public const int MinIntervalMs = 500;

    public static AppSettings Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Sections can be null when the JSON contains an explicit null.
        return settings with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            General = Normalize(settings.General ?? new GeneralSettings()),
            Monitoring = Normalize(settings.Monitoring ?? new MonitoringSettings()),
            Alerts = Normalize(settings.Alerts ?? new AlertSettings()),
            SmartAlerts = Normalize(settings.SmartAlerts ?? new SmartAlertSettings()),
            History = Normalize(settings.History ?? new HistorySettings()),
            Gaming = Normalize(settings.Gaming ?? new GamingSettings()),
            Diagnostics = Normalize(settings.Diagnostics ?? new DiagnosticsSettings()),
            Window = Normalize(settings.Window ?? new WindowSettings()),
        };
    }

    private static GeneralSettings Normalize(GeneralSettings general) => general with
    {
        Theme = DefinedOr(general.Theme, ThemePreference.System),
        CloseBehavior = DefinedOr(general.CloseBehavior, CloseBehavior.MinimizeToTray),
    };

    private static MonitoringSettings Normalize(MonitoringSettings monitoring) => monitoring with
    {
        CpuIntervalMs = Math.Clamp(monitoring.CpuIntervalMs, MinIntervalMs, 10_000),
        MemoryIntervalMs = Math.Clamp(monitoring.MemoryIntervalMs, MinIntervalMs, 10_000),
        NetworkIntervalMs = Math.Clamp(monitoring.NetworkIntervalMs, MinIntervalMs, 10_000),
        ProcessIntervalMs = Math.Clamp(monitoring.ProcessIntervalMs, 1_000, 30_000),
        GpuIntervalMs = Math.Clamp(monitoring.GpuIntervalMs, 1_000, 30_000),
        DiskActivityIntervalMs = Math.Clamp(monitoring.DiskActivityIntervalMs, 1_000, 30_000),
        StorageIntervalSeconds = Math.Clamp(monitoring.StorageIntervalSeconds, 5, 600),
        ChartWindowSeconds = Nearest(ChartWindowOptions, monitoring.ChartWindowSeconds),
        MaxSelfCpuPercent = monitoring.MaxSelfCpuPercent <= 0 || double.IsNaN(monitoring.MaxSelfCpuPercent)
            ? 0
            : Math.Clamp(monitoring.MaxSelfCpuPercent, 0.5, 25),
    };

    private static AlertSettings Normalize(AlertSettings alerts)
    {
        var (cpuWarning, cpuCritical) = OrderedThresholds(alerts.CpuWarningPercent, alerts.CpuCriticalPercent);
        var (memoryWarning, memoryCritical) = OrderedThresholds(alerts.MemoryWarningPercent, alerts.MemoryCriticalPercent);
        var (diskWarning, diskCritical) = OrderedThresholds(alerts.DiskWarningPercent, alerts.DiskCriticalPercent);

        return alerts with
        {
            CpuWarningPercent = cpuWarning,
            CpuCriticalPercent = cpuCritical,
            CpuSustainSeconds = Math.Clamp(alerts.CpuSustainSeconds, 5, 600),
            MemoryWarningPercent = memoryWarning,
            MemoryCriticalPercent = memoryCritical,
            MemorySustainSeconds = Math.Clamp(alerts.MemorySustainSeconds, 5, 600),
            DiskWarningPercent = diskWarning,
            DiskCriticalPercent = diskCritical,
            ProcessMemoryWarningPercent = ClampPercent(alerts.ProcessMemoryWarningPercent, 30),
            ProcessCpuWarningPercent = ClampPercent(alerts.ProcessCpuWarningPercent, 50),
        };
    }

    private static SmartAlertSettings Normalize(SmartAlertSettings alerts) => alerts with
    {
        CpuPercent = Math.Clamp(ClampPercent(alerts.CpuPercent, 90), 50, 100),
        CpuMinutes = Math.Clamp(alerts.CpuMinutes, 1, 60),
        MemoryPercent = Math.Clamp(ClampPercent(alerts.MemoryPercent, 90), 50, 100),
        MemoryMinutes = Math.Clamp(alerts.MemoryMinutes, 1, 60),
        DiskActivePercent = Math.Clamp(ClampPercent(alerts.DiskActivePercent, 95), 50, 100),
        DiskMinutes = Math.Clamp(alerts.DiskMinutes, 1, 60),
        AppCpuPercent = Math.Clamp(ClampPercent(alerts.AppCpuPercent, 25), 5, 100),
        AppCpuMinutes = Math.Clamp(alerts.AppCpuMinutes, 1, 60),
        MemoryGrowthPoints = double.IsFinite(alerts.MemoryGrowthPoints) ? Math.Clamp(alerts.MemoryGrowthPoints, 3, 50) : 10,
        MemoryGrowthMinutes = Math.Clamp(alerts.MemoryGrowthMinutes, 10, 120),
        UnusualMinutes = Math.Clamp(alerts.UnusualMinutes, 2, 60),
        LowDiskFreePercent = double.IsFinite(alerts.LowDiskFreePercent) ? Math.Clamp(alerts.LowDiskFreePercent, 1, 50) : 10,
        CooldownMinutes = Math.Clamp(alerts.CooldownMinutes, 1, 240),
        MaxNewAlertsPerHour = Math.Clamp(alerts.MaxNewAlertsPerHour, 1, 60),
    };

    private static HistorySettings Normalize(HistorySettings history)
    {
        var detail = Math.Clamp(history.DetailRetentionDays, 1, 31);
        return history with
        {
            ReplayMinutes = Nearest(ReplayMinuteOptions, history.ReplayMinutes),
            DetailRetentionDays = detail,
            SummaryRetentionDays = Math.Max(Math.Clamp(history.SummaryRetentionDays, 7, 365), detail),
        };
    }

    /// <summary>Most executables kept in each game list (a hand-edited file cannot grow without bound).</summary>
    public const int MaxGameListEntries = 500;

    private static GamingSettings Normalize(GamingSettings gaming)
    {
        var excluded = NormalizePaths(gaming.ExcludedGames, []);

        // A path cannot be both: "not a game" wins, it is the safer choice.
        var added = NormalizePaths(gaming.AddedGames, excluded);
        return gaming with
        {
            MinimumSessionMinutes = Math.Clamp(gaming.MinimumSessionMinutes, 1, 30),
            ExcludedGames = excluded,
            AddedGames = added,
        };
    }

    /// <summary>
    /// Trimmed, distinct (case-insensitive) paths not in <paramref name="forbidden"/>. Returns the same instance when the
    /// list is already clean (the common case: nothing to allocate).
    /// </summary>
    private static IReadOnlyList<string> NormalizePaths(IReadOnlyList<string>? paths, IReadOnlyList<string> forbidden)
    {
        if (paths is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = paths.Count <= MaxGameListEntries;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Trim().Length != path.Length || !seen.Add(path)
                || forbidden.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return paths;
        }

        return paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !forbidden.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Take(MaxGameListEntries)
            .ToArray();
    }

    private static DiagnosticsSettings Normalize(DiagnosticsSettings diagnostics) => diagnostics with
    {
        LogLevel = DefinedOr(diagnostics.LogLevel, LogVerbosity.Information),
    };

    private static WindowSettings Normalize(WindowSettings window) => window with
    {
        Width = Math.Clamp(window.Width, 640, 16_384),
        Height = Math.Clamp(window.Height, 480, 16_384),
    };

    /// <summary>Clamps both thresholds to 1–100 and makes sure warning ≤ critical.</summary>
    private static (double Warning, double Critical) OrderedThresholds(double warning, double critical)
    {
        warning = ClampPercent(warning, 80);
        critical = ClampPercent(critical, 95);
        return warning <= critical ? (warning, critical) : (critical, warning);
    }

    private static double ClampPercent(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, 1, 100) : fallback;

    private static int Nearest(IReadOnlyList<int> options, int value) =>
        options.MinBy(option => Math.Abs((long)option - value));

    private static T DefinedOr<T>(T value, T fallback)
        where T : struct, Enum =>
        Enum.IsDefined(value) ? value : fallback;
}
