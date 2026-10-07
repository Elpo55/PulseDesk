using Sysora.Core.Models;
using Sysora.Core.Settings;

namespace Sysora.Core.Monitoring;

/// <summary>
/// What a monitoring intensity changes technically: how often each family of metrics is sampled (as a multiple of the
/// configured intervals), how many applications are kept per history sample, and how often alerts and dashboard
/// insights are re-evaluated.
/// </summary>
/// <param name="Intensity">The intensity.</param>
/// <param name="RealtimeFactor">Multiplier of the CPU, memory and network intervals.</param>
/// <param name="DetailFactor">Multiplier of the process, GPU and disk activity intervals.</param>
/// <param name="StorageFactor">Multiplier of the free-space interval.</param>
/// <param name="TopAppsPerCriterion">Applications kept per history sample for each criterion (CPU, memory, I/O).</param>
/// <param name="AlertEvaluationInterval">Interval between two evaluations of the alert rules.</param>
/// <param name="InsightRefreshInterval">Interval between two refreshes of the dashboard insights.</param>
public sealed record MonitoringProfile(
    MonitoringIntensity Intensity,
    double RealtimeFactor,
    double DetailFactor,
    double StorageFactor,
    int TopAppsPerCriterion,
    TimeSpan AlertEvaluationInterval,
    TimeSpan InsightRefreshInterval)
{
    /// <summary>Fastest real-time interval any profile may reach.</summary>
    public static readonly TimeSpan MinimumRealtime = TimeSpan.FromMilliseconds(SettingsValidator.MinIntervalMs);

    /// <summary>Fastest process, GPU and disk interval any profile may reach (a process scan costs more than a counter).</summary>
    public static readonly TimeSpan MinimumDetail = TimeSpan.FromSeconds(1);

    /// <summary>Fastest free-space interval any profile may reach.</summary>
    public static readonly TimeSpan MinimumStorage = TimeSpan.FromSeconds(5);

    public static MonitoringProfile Minimal { get; } = new(MonitoringIntensity.Minimal, 2, 3, 4, 3, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));

    public static MonitoringProfile Balanced { get; } = new(MonitoringIntensity.Balanced, 1, 1, 1, 5, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));

    public static MonitoringProfile Detailed { get; } = new(MonitoringIntensity.Detailed, 0.5, 0.5, 0.5, 8, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

    public static MonitoringProfile For(MonitoringIntensity intensity) => intensity switch
    {
        MonitoringIntensity.Minimal => Minimal,
        MonitoringIntensity.Detailed => Detailed,
        _ => Balanced,
    };

    /// <summary>Plain-language description of the intensity, for Settings.</summary>
    public static string Describe(MonitoringIntensity intensity) => intensity switch
    {
        MonitoringIntensity.Minimal => "For gaming or battery: samples less often (CPU and memory every 2 intervals, processes, GPU and disks every 3), keeps fewer applications per sample and evaluates alerts every 10 seconds.",
        MonitoringIntensity.Detailed => "For investigating a problem: samples twice as often where it helps (down to 0.5 s for CPU and memory, 1 s for processes), keeps more applications per sample.",
        _ => "The default: the intervals set below, a good balance between precision and Sysora's own consumption.",
    };

    /// <summary>The effective interval of a metric family for a configured interval.</summary>
    public TimeSpan Apply(MetricKind kind, TimeSpan configured)
    {
        var (factor, minimum) = kind switch
        {
            MetricKind.Cpu or MetricKind.Memory or MetricKind.Network => (RealtimeFactor, MinimumRealtime),
            MetricKind.Processes or MetricKind.Gpu or MetricKind.DiskActivity => (DetailFactor, MinimumDetail),
            MetricKind.Storage => (StorageFactor, MinimumStorage),
            _ => (Math.Max(1, RealtimeFactor), MinimumRealtime),
        };
        var interval = configured * factor;
        return interval < minimum ? minimum : interval;
    }
}

/// <summary>How the monitor is sampling right now, for Sysora's own impact view.</summary>
/// <param name="Intensity">Intensity in effect (Detailed during an investigation).</param>
/// <param name="Investigating">True while a troubleshooting investigation runs.</param>
/// <param name="Background">True while the window is hidden (on-screen-only metrics slowed down).</param>
/// <param name="ThrottleFactor">Slowdown applied to stay within the CPU budget (1 = none).</param>
/// <param name="Intervals">Effective interval of each enabled metric family.</param>
public sealed record MonitoringScheduleInfo(
    MonitoringIntensity Intensity,
    bool Investigating,
    bool Background,
    double ThrottleFactor,
    IReadOnlyDictionary<MetricKind, TimeSpan> Intervals)
{
    public static MonitoringScheduleInfo Unknown { get; } = new(MonitoringIntensity.Balanced, false, false, 1, new Dictionary<MetricKind, TimeSpan>());

    public TimeSpan? Get(MetricKind kind) => Intervals.TryGetValue(kind, out var interval) ? interval : null;

    /// <summary>Approximate number of collections per minute, all metric families together.</summary>
    public double CollectionsPerMinute => Intervals.Values.Sum(i => 60 / Math.Max(i.TotalSeconds, 0.1));
}
