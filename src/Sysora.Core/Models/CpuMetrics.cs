namespace Sysora.Core.Models;

/// <summary>
/// Point-in-time CPU measurements.
/// </summary>
/// <param name="UsagePercent">Overall utilization, 0–100.</param>
/// <param name="TemperatureCelsius">Package temperature, or <c>null</c> when Windows does not expose it.</param>
/// <param name="CurrentFrequencyGHz">Effective clock speed, or <c>null</c> when unavailable.</param>
/// <param name="LogicalProcessors">Number of logical processors measured.</param>
public sealed record CpuMetrics(
    double UsagePercent,
    double? TemperatureCelsius,
    double? CurrentFrequencyGHz,
    int LogicalProcessors)
{
    /// <summary>Utilization of each logical processor (0–100), in processor order. Empty when unavailable.</summary>
    public IReadOnlyList<double> LogicalProcessorUsage { get; init; } = [];

    /// <summary>Rated (base) clock speed reported by Windows, or <c>null</c> when unavailable.</summary>
    public double? BaseFrequencyGHz { get; init; }

    /// <summary>Highest effective clock speed measured since Sysora started, or <c>null</c> when unavailable.</summary>
    public double? PeakFrequencyGHz { get; init; }
}
