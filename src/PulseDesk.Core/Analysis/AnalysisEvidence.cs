using PulseDesk.Core.History;

namespace PulseDesk.Core.Analysis;

/// <summary>How much a conclusion can be trusted, given the data behind it.</summary>
public enum ConfidenceLevel
{
    /// <summary>Little data, or an indirect indication: treat as a hint.</summary>
    Low,

    /// <summary>Enough data for a reasonable conclusion.</summary>
    Medium,

    /// <summary>Sustained, directly measured evidence.</summary>
    High,
}

/// <summary>
/// One piece of data behind a diagnosis, alert, change or score: what was measured, the value observed,
/// what it was compared with, over which period and from which source. Every conclusion PulseDesk shows
/// carries its evidence, so the user can see why.
/// </summary>
/// <param name="Metric">What was measured, e.g. "CPU usage".</param>
/// <param name="Observed">The value observed, formatted, e.g. "94% for 3m 24s".</param>
public sealed record AnalysisEvidence(string Metric, string Observed)
{
    /// <summary>What the value was compared with, e.g. "Usual level 35–50% (last 7 days)".</summary>
    public string? Reference { get; init; }

    /// <summary>Start of the analyzed period.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>End of the analyzed period.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Where the data comes from, e.g. "Windows performance counters".</summary>
    public string? Source { get; init; }

    /// <summary>Number of measurements used, when meaningful.</summary>
    public int? SampleCount { get; init; }
}

/// <summary>Plain-language description of where each metric comes from.</summary>
public static class MetricSources
{
    public const string Cpu = "Windows performance counter \\Processor Information(_Total)\\% Processor Utility";
    public const string Memory = "GlobalMemoryStatusEx (physical memory in use)";
    public const string Commit = "GetPerformanceInfo (commit charge and limit)";
    public const string Disk = "Windows performance counter \\LogicalDisk(*)\\% Idle Time (active time = 100 − idle)";
    public const string Network = "IP Helper interface statistics (interfaces with a default gateway)";
    public const string Gpu = "Windows performance counter \\GPU Engine(*)\\Utilization Percentage";
    public const string Processes = "NtQuerySystemInformation (per-process CPU time, private working set and I/O counters)";
    public const string ProcessIo = "Per-process I/O counters (files, devices and network combined)";
    public const string Storage = "GetDiskFreeSpaceEx (volume capacity)";
    public const string Uptime = "GetTickCount64 (time since Windows started, including sleep)";
    public const string Connectivity = "Windows Network Connectivity Status Indicator";

    /// <summary>Source of a history metric.</summary>
    public static string For(HistoryMetric metric) => metric switch
    {
        HistoryMetric.Cpu => Cpu,
        HistoryMetric.Memory => Memory,
        HistoryMetric.Disk => Disk,
        HistoryMetric.NetworkReceive or HistoryMetric.NetworkSend => Network,
        HistoryMetric.Gpu => Gpu,
        HistoryMetric.ProcessCount => Processes,
        _ => "PulseDesk measurements",
    };
}
