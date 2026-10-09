using Sysora.Core.History;
using Sysora.Localization;

namespace Sysora.Core.Analysis;

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
/// what it was compared with, over which period and from which source. Every conclusion Sysora shows
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

/// <summary>Confidence levels in words.</summary>
public static class ConfidenceText
{
    /// <summary>"high", "medium", "low" (lowercase, to go inside a sentence).</summary>
    public static string Lower(ConfidenceLevel level) => level switch
    {
        ConfidenceLevel.High => Strings.Confidence_High_Lower,
        ConfidenceLevel.Medium => Strings.Confidence_Medium_Lower,
        _ => Strings.Confidence_Low_Lower,
    };

    /// <summary>"High confidence", "Medium confidence", "Low confidence".</summary>
    public static string Label(ConfidenceLevel level) => level switch
    {
        ConfidenceLevel.High => Strings.Confidence_High,
        ConfidenceLevel.Medium => Strings.Confidence_Medium,
        _ => Strings.Confidence_Low,
    };
}

/// <summary>Plain-language description of where each metric comes from.</summary>
public static class MetricSources
{
    public static string Cpu => Strings.Source_Cpu;

    public static string Memory => Strings.Source_Memory;

    public static string Commit => Strings.Source_Commit;

    public static string Disk => Strings.Source_Disk;

    public static string Network => Strings.Source_Network;

    public static string Gpu => Strings.Source_Gpu;

    public static string Processes => Strings.Source_Processes;

    public static string ProcessIo => Strings.Source_ProcessIo;

    public static string Storage => Strings.Source_Storage;

    public static string Uptime => Strings.Source_Uptime;

    public static string Connectivity => Strings.Source_Connectivity;

    /// <summary>Source of a history metric.</summary>
    public static string For(HistoryMetric metric) => metric switch
    {
        HistoryMetric.Cpu => Cpu,
        HistoryMetric.Memory => Memory,
        HistoryMetric.Disk => Disk,
        HistoryMetric.NetworkReceive or HistoryMetric.NetworkSend => Network,
        HistoryMetric.Gpu => Gpu,
        HistoryMetric.ProcessCount => Processes,
        HistoryMetric.SystemDriveFree => Storage,
        _ => Strings.Source_Sysora,
    };
}
