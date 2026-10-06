using PulseDesk.Core.Analysis;
using PulseDesk.Core.History;
using PulseDesk.Core.Models;
using PulseDesk.Core.Monitoring;
using PulseDesk.Core.Settings;

namespace PulseDesk.Core.Diagnosis;

/// <summary>Severity of a diagnosis result.</summary>
public enum DiagnosisSeverity
{
    /// <summary>Checked and normal.</summary>
    Normal,

    /// <summary>Worth knowing; possibly a cause, not a confirmed problem.</summary>
    Info,

    /// <summary>A measured problem that can slow the PC down.</summary>
    Warning,

    /// <summary>A resource is saturated or nearly exhausted.</summary>
    Critical,
}

/// <summary>Area a diagnosis result is about.</summary>
public enum DiagnosisCategory
{
    Cpu,
    Memory,
    Disk,
    Storage,
    Gpu,
    Network,
    Applications,
    System,
}

/// <summary>Where the user can look further. The UI maps it to a page.</summary>
public enum DiagnosisAction
{
    None,
    AppImpact,
    Replay,
    Processes,
    Performance,
    Storage,
    Network,
    Diagnosis,
    Alerts,
    Changes,
    Gaming,
}

/// <summary>Overall state of the PC, as shown on the dashboard.</summary>
public enum PcHealthState
{
    /// <summary>Not enough data yet.</summary>
    Unknown,

    /// <summary>No problem detected.</summary>
    Healthy,

    /// <summary>Something deserves attention.</summary>
    Attention,

    /// <summary>A problem is detected.</summary>
    Problem,
}

/// <summary>
/// One conclusion of the diagnosis, with everything needed to understand it: what was measured, the value,
/// what it was compared with, for how long, why it matters, what to do, and how confident PulseDesk is.
/// </summary>
public sealed record DiagnosisResult
{
    /// <summary>Rule that produced the result.</summary>
    public required string RuleId { get; init; }

    public required DiagnosisCategory Category { get; init; }

    public required DiagnosisSeverity Severity { get; init; }

    /// <summary>Short title, e.g. "High CPU usage".</summary>
    public required string Title { get; init; }

    /// <summary>One sentence describing the finding, e.g. "CPU usage has stayed above 80% for 3m 24s."</summary>
    public required string Description { get; init; }

    /// <summary>Metric concerned, e.g. "CPU usage".</summary>
    public required string Metric { get; init; }

    /// <summary>Value observed, formatted, e.g. "94%".</summary>
    public required string ObservedValue { get; init; }

    /// <summary>What the value is compared with, e.g. "Usual 35–50%" or "Threshold 80%".</summary>
    public string? ReferenceValue { get; init; }

    /// <summary>How long the condition has lasted, when relevant.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>When the condition was observed (latest measurement).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Why this matters, in plain words. Hypotheses are worded as such.</summary>
    public required string Explanation { get; init; }

    /// <summary>What the user can do, when there is something useful to suggest.</summary>
    public string? Recommendation { get; init; }

    public required ConfidenceLevel Confidence { get; init; }

    /// <summary>Data used to reach the conclusion.</summary>
    public IReadOnlyList<AnalysisEvidence> Evidence { get; init; } = [];

    /// <summary>Application concerned, when the result is about one.</summary>
    public string? AppKey { get; init; }

    /// <summary>Where to look further.</summary>
    public DiagnosisAction Action { get; init; }
}

/// <summary>Everything the rules can look at. Built once per diagnosis, immutable.</summary>
/// <param name="Now">Time of the diagnosis.</param>
/// <param name="Snapshot">Latest full snapshot from the monitor.</param>
/// <param name="Recent">Recent history snapshots (oldest first), typically the last 15 minutes.</param>
/// <param name="Baseline">Usual behavior of the PC (may still be collecting).</param>
/// <param name="Thresholds">The user's thresholds.</param>
public sealed record DiagnosisContext(
    DateTimeOffset Now,
    SystemSnapshot Snapshot,
    IReadOnlyList<MetricSnapshot> Recent,
    UsageBaseline Baseline,
    AlertSettings Thresholds)
{
    /// <summary>The most recent history snapshot, if any.</summary>
    public MetricSnapshot? Latest => Recent.Count > 0 ? Recent[^1] : null;

    /// <summary>Snapshots of the last <paramref name="window"/> before the latest one.</summary>
    public IReadOnlyList<MetricSnapshot> Last(TimeSpan window)
    {
        if (Latest is not { } latest)
        {
            return [];
        }

        var from = latest.Timestamp - window;
        var start = 0;
        while (start < Recent.Count && Recent[start].Timestamp < from)
        {
            start++;
        }

        return Recent.Skip(start).ToArray();
    }
}

/// <summary>Result of a diagnosis: the overall state, an answer to "why is my PC slow?" and every result.</summary>
public sealed record DiagnosisReport
{
    public required DateTimeOffset Timestamp { get; init; }

    public required PcHealthState State { get; init; }

    /// <summary>Short headline, e.g. "2 problems detected".</summary>
    public required string Headline { get; init; }

    /// <summary>A sentence or two answering "why is my PC slow?" from the results.</summary>
    public required string Summary { get; init; }

    /// <summary>All results, most severe first.</summary>
    public required IReadOnlyList<DiagnosisResult> Results { get; init; }

    /// <summary>Start of the analyzed period.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>End of the analyzed period.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Number of history snapshots analyzed.</summary>
    public int SampleCount { get; init; }

    /// <summary>State of the usual-behavior baseline, in words.</summary>
    public string BaselineDescription { get; init; } = string.Empty;

    /// <summary>Metrics that could not be analyzed on this PC, with the reason.</summary>
    public IReadOnlyList<string> NotAnalyzed { get; init; } = [];

    /// <summary>An empty report (no data yet).</summary>
    public static DiagnosisReport Empty { get; } = new()
    {
        Timestamp = DateTimeOffset.MinValue,
        State = PcHealthState.Unknown,
        Headline = "Analyzing…",
        Summary = "PulseDesk needs a few seconds of measurements before it can diagnose the PC.",
        Results = [],
    };

    public IEnumerable<DiagnosisResult> Problems => Results.Where(r => r.Severity >= DiagnosisSeverity.Warning);

    public IEnumerable<DiagnosisResult> Potential => Results.Where(r => r.Severity == DiagnosisSeverity.Info);

    public IEnumerable<DiagnosisResult> Normal => Results.Where(r => r.Severity == DiagnosisSeverity.Normal);
}
