using Sysora.Core.Metrics;
using Sysora.Localization;

namespace Sysora.Core.Analysis;

/// <summary>Qualitative level of an application's impact. Deliberately coarse: the score is a relative indicator.</summary>
public enum ImpactLevel
{
    Low,
    Moderate,
    High,
    VeryHigh,
}

/// <summary>Contribution of one resource to an application's impact.</summary>
/// <param name="Resource">"CPU", "Memory", "Disk I/O" or "Running time".</param>
/// <param name="Level">Qualitative level.</param>
/// <param name="Load">Average load over the period (unit depends on the resource).</param>
/// <param name="Normalized">Load relative to the level considered "very high", 0–1 (its weight in the score).</param>
/// <param name="Description">What the level is based on, e.g. "7.2% of total CPU on average over the period".</param>
public sealed record ImpactComponent(string Resource, ImpactLevel Level, double Load, double Normalized, string Description)
{
    /// <summary>The resource in the interface language (<see cref="Resource"/> is a stable identifier).</summary>
    public string Label => Resource switch
    {
        "CPU" => Strings.Impact_Resource_Cpu,
        "Memory" => Strings.Impact_Resource_Memory,
        "Disk I/O" => Strings.Impact_Resource_Io,
        _ => Strings.Impact_Resource_Running,
    };
}

/// <summary>Impact levels in words.</summary>
public static class ImpactLevelText
{
    /// <summary>"Low", "Moderate", "High", "Very high".</summary>
    public static string Label(ImpactLevel level) => level switch
    {
        ImpactLevel.VeryHigh => Strings.Impact_Level_VeryHigh,
        ImpactLevel.High => Strings.Impact_Level_High,
        ImpactLevel.Moderate => Strings.Impact_Level_Moderate,
        _ => Strings.Impact_Level_Low,
    };
}

/// <summary>
/// Explainable impact score: a 0–100 relative indicator built from measured CPU, memory and I/O, weighted by
/// how long the application ran. Not an absolute or scientific measure; the UI shows the level and its
/// components rather than the number alone.
/// </summary>
/// <param name="Value">Score, 0–100.</param>
/// <param name="Level">Level derived from the score.</param>
/// <param name="Components">Contribution of each resource.</param>
public sealed record AppImpactScore(int Value, ImpactLevel Level, IReadOnlyList<ImpactComponent> Components)
{
    /// <summary>How the score is computed, in plain words.</summary>
    public static string Formula => Strings.Impact_Formula;
}

/// <summary>Direction of an application's usage between the first and second half of the period.</summary>
/// <param name="Cpu">CPU direction.</param>
/// <param name="Memory">Memory direction.</param>
/// <param name="CpuChangePercent">Relative CPU change, when meaningful.</param>
/// <param name="MemoryChangePercent">Relative memory change, when meaningful.</param>
public sealed record UsageTrend(TrendDirection Cpu, TrendDirection Memory, double? CpuChangePercent, double? MemoryChangePercent)
{
    /// <summary>No trend could be computed.</summary>
    public static UsageTrend Unknown { get; } = new(TrendDirection.Unknown, TrendDirection.Unknown, null, null);
}

/// <summary>Impact of one application over a period, with the evidence behind it.</summary>
/// <param name="Usage">Measured usage.</param>
/// <param name="Score">Explainable score.</param>
/// <param name="Explanation">One or two sentences explaining the level.</param>
/// <param name="Confidence">How much data supports the result.</param>
/// <param name="Evidence">Data used.</param>
/// <param name="Trend">Evolution over the period.</param>
public sealed record AppImpactResult(
    AppUsageStatistics Usage,
    AppImpactScore Score,
    string Explanation,
    ConfidenceLevel Confidence,
    IReadOnlyList<AnalysisEvidence> Evidence,
    UsageTrend Trend);

/// <summary>Periods offered by App Impact.</summary>
public enum AppImpactPeriod
{
    /// <summary>Since Sysora started (every application, finest detail).</summary>
    Session,

    /// <summary>Last 24 hours of the local history (most significant applications of each five minutes).</summary>
    Last24Hours,

    /// <summary>Last 7 days of the local history.</summary>
    Last7Days,
}

/// <summary>What the analyzer needs besides the usage itself.</summary>
/// <param name="TotalMemoryBytes">Physical memory of the PC (0 when unknown: memory is then not scored).</param>
/// <param name="From">Start of the analyzed period.</param>
/// <param name="To">End of the analyzed period.</param>
public sealed record AppImpactContext(ulong TotalMemoryBytes, DateTimeOffset From, DateTimeOffset To);

/// <summary>Ranked impact of applications over a period.</summary>
/// <param name="Period">Period analyzed.</param>
/// <param name="From">Start of the period.</param>
/// <param name="To">End of the period.</param>
/// <param name="MonitoredSeconds">Time actually covered by measurements.</param>
/// <param name="Apps">Applications, highest impact first.</param>
public sealed record AppImpactReport(AppImpactPeriod Period, DateTimeOffset From, DateTimeOffset To, double MonitoredSeconds, IReadOnlyList<AppImpactResult> Apps)
{
    /// <summary>Limits of the data for this period, shown to the user.</summary>
    public string? Note { get; init; }
}

/// <summary>Usage of one application over time.</summary>
/// <param name="Points">One point per bucket in which the application ran, oldest first.</param>
/// <param name="BucketLength">Length of each bucket.</param>
/// <param name="From">Start of the period.</param>
/// <param name="To">End of the period.</param>
public sealed record AppUsageTimeline(IReadOnlyList<AppMinutePoint> Points, TimeSpan BucketLength, DateTimeOffset From, DateTimeOffset To);

/// <summary>App Impact periods in words.</summary>
public static class AppImpactPeriodText
{
    public static string Label(AppImpactPeriod period) => period switch
    {
        AppImpactPeriod.Session => Strings.ImpactPeriod_Session,
        AppImpactPeriod.Last24Hours => Strings.ImpactPeriod_24Hours,
        _ => Strings.ImpactPeriod_7Days,
    };
}
