namespace PulseDesk.Core.Analysis;

/// <summary>
/// Measured resource usage of one application over a period (this session, or a period of the local
/// history). Averages are computed over the time the application was running.
/// </summary>
public sealed record AppUsageStatistics
{
    /// <summary>The application.</summary>
    public required AppIdentity Identity { get; init; }

    /// <summary>First time the application was seen running in the period.</summary>
    public DateTimeOffset FirstSeen { get; init; }

    /// <summary>Last time the application was seen running in the period.</summary>
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>Measured time during which the application was running.</summary>
    public double ActiveSeconds { get; init; }

    /// <summary>Time covered by measurements in the period (all applications), gaps excluded.</summary>
    public double MonitoredSeconds { get; init; }

    /// <summary>Number of process samples in which the application was running.</summary>
    public int Samples { get; init; }

    /// <summary>Average CPU while running, percent of total capacity.</summary>
    public double CpuAverage { get; init; }

    /// <summary>Highest CPU observed, percent of total capacity.</summary>
    public double CpuMaximum { get; init; }

    /// <summary>Average private working set while running.</summary>
    public double MemoryAverageBytes { get; init; }

    /// <summary>Largest private working set observed.</summary>
    public double MemoryMaximumBytes { get; init; }

    /// <summary>Average I/O rate while running (files, devices and network).</summary>
    public double IoAverageBytesPerSecond { get; init; }

    /// <summary>Highest I/O rate observed.</summary>
    public double IoMaximumBytesPerSecond { get; init; }

    /// <summary>Process instances started during the period, as observed by PulseDesk.</summary>
    public int Launches { get; init; }

    /// <summary>True when the application is running now (session statistics only).</summary>
    public bool IsRunning { get; init; }

    /// <summary>Current number of processes (session statistics only).</summary>
    public int InstanceCount { get; init; }

    /// <summary>Usage in the first half of the period, when known (for trends).</summary>
    public UsageLevel? FirstHalf { get; init; }

    /// <summary>Usage in the second half of the period, when known (for trends).</summary>
    public UsageLevel? SecondHalf { get; init; }

    /// <summary>Share of the monitored time during which the application was running, 0–1.</summary>
    public double Presence => MonitoredSeconds > 0
        ? Math.Clamp(ActiveSeconds / MonitoredSeconds, 0, 1)
        : ActiveSeconds > 0 || Samples > 0 ? 1 : 0;
}

/// <summary>Average CPU and memory of an application over part of a period.</summary>
/// <param name="CpuAverage">Average CPU while running, percent of total capacity.</param>
/// <param name="MemoryAverageBytes">Average private working set while running.</param>
/// <param name="ActiveSeconds">Time running in that part of the period.</param>
public readonly record struct UsageLevel(double CpuAverage, double MemoryAverageBytes, double ActiveSeconds);

/// <summary>Average usage of an application during one minute (session history).</summary>
/// <param name="Start">Start of the minute (UTC).</param>
/// <param name="CpuAverage">Average CPU while running, percent of total capacity.</param>
/// <param name="MemoryAverageBytes">Average private working set.</param>
/// <param name="IoAverageBytesPerSecond">Average I/O rate.</param>
/// <param name="ActiveSeconds">Measured time running during the minute.</param>
public readonly record struct AppMinutePoint(DateTimeOffset Start, double CpuAverage, double MemoryAverageBytes, double IoAverageBytesPerSecond, double ActiveSeconds);
