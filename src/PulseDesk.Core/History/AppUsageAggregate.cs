namespace PulseDesk.Core.History;

/// <summary>
/// Resource usage of one application over one bucket of the long-term history. Averages are computed over
/// the samples in which the application was running.
/// </summary>
public sealed record AppUsageAggregate
{
    /// <summary>Application key (see <see cref="Analysis.AppIdentity"/>).</summary>
    public required string AppKey { get; init; }

    /// <summary>Image name.</summary>
    public required string Name { get; init; }

    /// <summary>Executable path, when known.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Start of the bucket (UTC).</summary>
    public required DateTimeOffset Start { get; init; }

    public required HistoryResolution Resolution { get; init; }

    /// <summary>Number of process samples in which the application was running.</summary>
    public int Samples { get; init; }

    /// <summary>Measured time during which the application was running.</summary>
    public double ActiveSeconds { get; init; }

    /// <summary>Average CPU usage while running, percent of total capacity.</summary>
    public double CpuAverage { get; init; }

    /// <summary>Highest CPU usage observed, percent of total capacity.</summary>
    public double CpuMaximum { get; init; }

    /// <summary>Average private working set while running.</summary>
    public double MemoryAverageBytes { get; init; }

    /// <summary>Largest private working set observed.</summary>
    public double MemoryMaximumBytes { get; init; }

    /// <summary>Average read and write I/O rate while running.</summary>
    public double IoAverageBytesPerSecond { get; init; }

    /// <summary>Highest I/O rate observed.</summary>
    public double IoMaximumBytesPerSecond { get; init; }

    /// <summary>Number of process instances that started during the bucket (observed by PulseDesk).</summary>
    public int Launches { get; init; }
}

/// <summary>Application usage over one bucket, with the time the bucket was actually monitored.</summary>
/// <param name="Start">Start of the bucket (UTC).</param>
/// <param name="Resolution">Bucket length.</param>
/// <param name="MonitoredSeconds">Time covered by process samples in the bucket.</param>
/// <param name="SampleCount">Number of process samples in the bucket.</param>
/// <param name="Apps">Applications recorded for the bucket (the most significant ones only).</param>
public sealed record AppUsageBucket(
    DateTimeOffset Start,
    HistoryResolution Resolution,
    double MonitoredSeconds,
    int SampleCount,
    IReadOnlyList<AppUsageAggregate> Apps);
