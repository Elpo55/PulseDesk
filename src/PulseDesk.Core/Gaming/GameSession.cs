using System.Text.Json.Serialization;
using PulseDesk.Core.Analysis;

namespace PulseDesk.Core.Gaming;

/// <summary>Why a gaming session ended.</summary>
public enum GameSessionEnd
{
    /// <summary>The game's processes exited.</summary>
    GameClosed,

    /// <summary>PulseDesk exited while the game was running: the session covers what was observed until then.</summary>
    PulseDeskClosed,

    /// <summary>Game sessions were turned off in Settings while the game was running.</summary>
    TrackingStopped,
}

/// <summary>Average and maximum of one metric over a session, from <paramref name="Samples"/> measurements.</summary>
public readonly record struct MetricStat(double Average, double Maximum, int Samples);

/// <summary>
/// A resource limit reached during a session for at least a minimum duration (memory nearly full, CPU saturated...),
/// as measured. Only periods lasting at least the minimum duration are counted.
/// </summary>
/// <param name="Kind">Which limit.</param>
/// <param name="Threshold">Threshold used (percent).</param>
/// <param name="TotalSeconds">Time spent above the threshold, over all counted periods.</param>
/// <param name="Periods">Number of counted periods.</param>
/// <param name="LongestSeconds">Longest period.</param>
/// <param name="Peak">Highest value observed during the counted periods.</param>
/// <param name="FirstAt">Start of the first counted period.</param>
public sealed record GameCondition(GameConditionKind Kind, double Threshold, double TotalSeconds, int Periods, double LongestSeconds, double Peak, DateTimeOffset FirstAt);

/// <summary>Resource limits followed during a game.</summary>
public enum GameConditionKind
{
    /// <summary>Total CPU usage near 100%.</summary>
    CpuSaturated,

    /// <summary>Physical memory nearly full.</summary>
    MemoryNearlyFull,

    /// <summary>Dedicated GPU memory nearly full.</summary>
    VideoMemoryNearlyFull,

    /// <summary>A disk busy almost all the time.</summary>
    DiskSaturated,
}

/// <summary>Usage of one application (all its processes) during a session.</summary>
/// <param name="Key">Application identity key.</param>
/// <param name="Name">Image name.</param>
/// <param name="ExecutablePath">Executable path, when known.</param>
/// <param name="CpuAverage">Average share of total CPU over the whole session (0 while it was not running).</param>
/// <param name="CpuMaximum">Highest share of total CPU measured.</param>
/// <param name="MemoryMaximumBytes">Largest private working set measured.</param>
public sealed record GameAppUsage(string Key, string Name, string? ExecutablePath, double CpuAverage, double CpuMaximum, ulong MemoryMaximumBytes);

/// <summary>Averages over one step of the session timeline. Null when the metric was not measured in that step.</summary>
public sealed record GameTimelinePoint(DateTimeOffset Start)
{
    public double? Cpu { get; init; }

    public double? Gpu { get; init; }

    public double? Memory { get; init; }

    public double? GameCpu { get; init; }

    public double? GameGpu { get; init; }

    public double? GameMemoryBytes { get; init; }
}

/// <summary>Something observed during the session (an alert, a gap in the measurements).</summary>
/// <param name="Time">When it happened.</param>
/// <param name="Text">What happened.</param>
public sealed record GameSessionEvent(DateTimeOffset Time, string Text);

/// <summary>
/// One gaming session as measured by PulseDesk: what was identified as a game and why, when it ran, and the resources
/// used by the PC, the game and the other applications. Every value is a measurement; a metric that was not available is
/// null. Frame rates are not part of it: Windows provides no reliable source to other applications.
/// </summary>
public sealed record GameSession
{
    public required Guid Id { get; init; }

    /// <summary>Application identity key of the game's executable (sessions of the same game share it).</summary>
    public required string GameKey { get; init; }

    /// <summary>Display name of the game.</summary>
    public required string Name { get; init; }

    public required string ExecutablePath { get; init; }

    /// <summary>What identified the executable as a game.</summary>
    public required GameDetectionSource Source { get; init; }

    public required ConfidenceLevel Confidence { get; init; }

    /// <summary>Plain-language reason, e.g. "Windows recognizes this executable as a game".</summary>
    public required string DetectionEvidence { get; init; }

    /// <summary>Game library folder that identified it, when relevant.</summary>
    public string? Library { get; init; }

    /// <summary>First observation of the game by PulseDesk.</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>Last observation of the game.</summary>
    public required DateTimeOffset End { get; init; }

    public GameSessionEnd EndReason { get; init; }

    /// <summary>When the game's process started, if before PulseDesk first saw it (PulseDesk started after the game).</summary>
    public DateTimeOffset? GameStartedAt { get; init; }

    /// <summary>Time covered by measurements (gaps such as sleep excluded).</summary>
    public double MonitoredSeconds { get; init; }

    /// <summary>Interruptions in the measurements longer than a minute (sleep, paused monitoring).</summary>
    public int DataGaps { get; init; }

    /// <summary>Time not covered because of those interruptions.</summary>
    public double GapSeconds { get; init; }

    /// <summary>Process samples taken during the session.</summary>
    public int ProcessSamples { get; init; }

    /// <summary>Total CPU usage, percent.</summary>
    public MetricStat? Cpu { get; init; }

    /// <summary>Physical memory in use, percent.</summary>
    public MetricStat? Memory { get; init; }

    public ulong? MemoryTotalBytes { get; init; }

    /// <summary>Utilization of the busiest graphics adapter, percent.</summary>
    public MetricStat? Gpu { get; init; }

    /// <summary>Dedicated memory in use on the game's graphics adapter (all applications), bytes.</summary>
    public MetricStat? VideoMemoryBytes { get; init; }

    public ulong? VideoMemoryTotalBytes { get; init; }

    /// <summary>Graphics adapter the game used the most (or the busiest one when per-process data is not available).</summary>
    public string? GpuName { get; init; }

    /// <summary>Active time of the busiest disk, percent.</summary>
    public MetricStat? Disk { get; init; }

    /// <summary>Network receive rate, bits per second (whole PC).</summary>
    public MetricStat? NetworkReceive { get; init; }

    /// <summary>Network send rate, bits per second (whole PC).</summary>
    public MetricStat? NetworkSend { get; init; }

    /// <summary>The game's processes: share of total CPU.</summary>
    public MetricStat? GameCpu { get; init; }

    /// <summary>The game's processes: private working set, bytes.</summary>
    public MetricStat? GameMemoryBytes { get; init; }

    /// <summary>The game's processes: GPU utilization (busiest engine), when Windows reports it per process.</summary>
    public MetricStat? GameGpu { get; init; }

    /// <summary>The game's processes: disk, device and network I/O, bytes per second.</summary>
    public MetricStat? GameIoBytesPerSecond { get; init; }

    /// <summary>Most game processes running at the same time.</summary>
    public int GameProcessCount { get; init; }

    /// <summary>PulseDesk's own CPU usage during the session, share of total CPU.</summary>
    public MetricStat? SelfCpu { get; init; }

    /// <summary>PulseDesk's own private working set during the session, bytes.</summary>
    public MetricStat? SelfMemoryBytes { get; init; }

    /// <summary>Other processes of the game (same install folder, or started by the game).</summary>
    public IReadOnlyList<GameAppUsage> AssociatedProcesses { get; init; } = [];

    /// <summary>Other applications that used the most CPU during the session.</summary>
    public IReadOnlyList<GameAppUsage> BackgroundApps { get; init; } = [];

    /// <summary>Resource limits reached during the session.</summary>
    public IReadOnlyList<GameCondition> Conditions { get; init; } = [];

    /// <summary>Averages over time, oldest first; each point covers <see cref="TimelineStepSeconds"/>.</summary>
    public IReadOnlyList<GameTimelinePoint> Timeline { get; init; } = [];

    public int TimelineStepSeconds { get; init; } = 60;

    /// <summary>Alerts and interruptions observed during the session.</summary>
    public IReadOnlyList<GameSessionEvent> Events { get; init; } = [];

    /// <summary>Wall-clock duration from the first to the last observation.</summary>
    [JsonIgnore]
    public TimeSpan Duration => End - Start;
}

/// <summary>A session in progress, for the live view.</summary>
/// <param name="GameKey">Application identity key of the game.</param>
/// <param name="Name">Display name.</param>
/// <param name="ExecutablePath">Executable path.</param>
/// <param name="Start">First observation.</param>
/// <param name="LastSeen">Latest observation.</param>
/// <param name="DetectionEvidence">Why it is treated as a game.</param>
public sealed record LiveGameSession(string GameKey, string Name, string ExecutablePath, DateTimeOffset Start, DateTimeOffset LastSeen, string DetectionEvidence)
{
    public ConfidenceLevel Confidence { get; init; }

    public MetricStat? Cpu { get; init; }

    public MetricStat? Gpu { get; init; }

    public MetricStat? Memory { get; init; }

    public MetricStat? GameCpu { get; init; }

    public MetricStat? GameMemoryBytes { get; init; }

    public MetricStat? SelfCpu { get; init; }

    public TimeSpan Duration => LastSeen - Start;
}
