namespace Sysora.Core.Monitoring;

/// <summary>Sysora's own resource usage, as measured by the monitor every <see cref="SelfUsageGovernor.MeasurementPeriod"/>.</summary>
/// <param name="CpuPercent">Share of total CPU capacity used by Sysora over the last measurement period.</param>
/// <param name="WorkingSetBytes">Physical memory currently mapped by the Sysora process.</param>
/// <param name="ThrottleFactor">Current slowdown applied to collection intervals (1 = none).</param>
public sealed record SelfUsage(double? CpuPercent, long WorkingSetBytes, double ThrottleFactor)
{
    /// <summary>When the values were measured.</summary>
    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>Memory held by .NET objects (managed heap).</summary>
    public long ManagedHeapBytes { get; init; }

    /// <summary>Rate of .NET allocations over the last period.</summary>
    public double? AllocatedBytesPerSecond { get; init; }

    /// <summary>Garbage collections during the last period, per generation (0, 1, 2).</summary>
    public int Gen0Collections { get; init; }

    public int Gen1Collections { get; init; }

    public int Gen2Collections { get; init; }

    /// <summary>Collection rounds of the monitoring loop per minute over the last period.</summary>
    public double? CollectionRoundsPerMinute { get; init; }

    /// <summary>Processes in the latest process sample (each one is analyzed).</summary>
    public int? ProcessesAnalyzed { get; init; }

    /// <summary>Sysora's own write rate (files and devices), from its entry in the process sample.</summary>
    public double? WriteBytesPerSecond { get; init; }

    /// <summary>Threads of the Sysora process, from its entry in the process sample.</summary>
    public int? ThreadCount { get; init; }

    /// <summary>True when Sysora has stayed above its CPU budget for a minute or more.</summary>
    public bool OverBudgetSustained { get; init; }
}

/// <summary>
/// Keeps Sysora within its CPU budget. Every measurement period it compares Sysora's own CPU usage
/// with the budget and stretches (or relaxes) all collection intervals accordingly.
/// </summary>
internal sealed class SelfUsageGovernor
{
    /// <summary>How often Sysora's own CPU usage is measured.</summary>
    public static readonly TimeSpan MeasurementPeriod = TimeSpan.FromSeconds(10);

    /// <summary>Intervals are never stretched more than this.</summary>
    public const double MaxFactor = 4.0;

    private const double Step = 1.5;

    private readonly Func<TimeSpan> _processCpuTime;
    private readonly int _processorCount;
    private TimeSpan? _lastElapsed;
    private TimeSpan _lastCpuTime;

    /// <param name="processCpuTime">Returns the total CPU time consumed by this process.</param>
    /// <param name="processorCount">Number of logical processors.</param>
    public SelfUsageGovernor(Func<TimeSpan> processCpuTime, int processorCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processorCount);
        _processCpuTime = processCpuTime;
        _processorCount = processorCount;
    }

    /// <summary>Current interval multiplier (1 = configured intervals).</summary>
    public double Factor { get; private set; } = 1.0;

    /// <summary>Last measured CPU usage of Sysora, in percent of total capacity.</summary>
    public double? LastCpuPercent { get; private set; }

    /// <summary>Number of completed measurements (lets callers detect a new measurement).</summary>
    public int MeasurementCount { get; private set; }

    /// <summary>Measures usage when a period has elapsed and adjusts the factor.</summary>
    /// <param name="elapsed">Monotonic time.</param>
    /// <param name="budgetPercent">CPU budget in percent of total capacity; 0 or less disables throttling.</param>
    /// <returns>True when <see cref="Factor"/> changed.</returns>
    public bool Update(TimeSpan elapsed, double budgetPercent)
    {
        var cpuTime = _processCpuTime();
        if (_lastElapsed is not { } lastElapsed)
        {
            _lastElapsed = elapsed;
            _lastCpuTime = cpuTime;
            return false;
        }

        var wall = elapsed - lastElapsed;
        if (wall < MeasurementPeriod)
        {
            return false;
        }

        var cpuPercent = (cpuTime - _lastCpuTime).TotalMilliseconds / (wall.TotalMilliseconds * _processorCount) * 100.0;
        LastCpuPercent = Math.Max(cpuPercent, 0);
        MeasurementCount++;
        _lastElapsed = elapsed;
        _lastCpuTime = cpuTime;

        var previous = Factor;
        if (budgetPercent <= 0)
        {
            Factor = 1.0;
        }
        else if (cpuPercent > budgetPercent)
        {
            Factor = Math.Min(Factor * Step, MaxFactor);
        }
        else if (cpuPercent < budgetPercent / 2 && Factor > 1.0)
        {
            // Relax only when comfortably under budget, to avoid oscillating around the limit.
            Factor = Math.Max(Factor / Step, 1.0);
        }

        return Math.Abs(Factor - previous) > 1e-9;
    }
}
