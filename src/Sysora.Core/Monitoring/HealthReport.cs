namespace Sysora.Core.Monitoring;

/// <summary>Technical status of one area of the system.</summary>
public enum HealthStatus
{
    /// <summary>No data to decide (metric unavailable).</summary>
    Unknown,
    Normal,
    Warning,
    Critical,
}

/// <summary>Area covered by a health indicator.</summary>
public enum HealthCategory
{
    Cpu,
    Memory,
    Storage,
    Network,
    Processes,
}

/// <summary>
/// One line of the System Health summary. Purely a technical indicator based on configurable thresholds;
/// it does not diagnose the cause of a problem.
/// </summary>
/// <param name="Category">Area covered.</param>
/// <param name="Status">Status.</param>
/// <param name="Summary">Short text, e.g. "Storage C: 87%".</param>
/// <param name="Detail">Optional explanation, e.g. "Above the 85% warning threshold".</param>
public sealed record HealthIndicator(HealthCategory Category, HealthStatus Status, string Summary, string? Detail = null);

/// <summary>Health indicators at a point in time.</summary>
public sealed record HealthReport(DateTimeOffset Timestamp, IReadOnlyList<HealthIndicator> Indicators)
{
    /// <summary>An empty report (no data yet).</summary>
    public static HealthReport Empty { get; } = new(DateTimeOffset.MinValue, []);

    /// <summary>The most severe known status, or <see cref="HealthStatus.Unknown"/> when nothing is known.</summary>
    public HealthStatus Overall =>
        Indicators.Count == 0 ? HealthStatus.Unknown : Indicators.Max(i => i.Status);

    /// <summary>True when both reports contain the same indicators (timestamps are ignored).</summary>
    public bool HasSameIndicators(HealthReport other) =>
        other is not null && Indicators.SequenceEqual(other.Indicators);
}
