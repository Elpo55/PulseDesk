using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.History;
using Sysora.Core.Models;
using Sysora.Core.Settings;
using Sysora.Localization;

namespace Sysora.Core.Alerts;

/// <summary>Importance of an alert.</summary>
public enum AlertSeverity
{
    /// <summary>Worth knowing (for example high but usual for this PC).</summary>
    Info,

    /// <summary>A lasting problem.</summary>
    Warning,

    /// <summary>A lasting problem that is also unusual or severe.</summary>
    Critical,
}

/// <summary>Life cycle of an alert.</summary>
public enum AlertStatus
{
    /// <summary>Not looked at yet.</summary>
    New,

    /// <summary>Looked at by the user; the condition may still be ongoing.</summary>
    Seen,

    /// <summary>The condition ended.</summary>
    Resolved,
}

/// <summary>
/// An alert: a condition that lasted or was unusual, with everything needed to understand it. One alert per
/// condition: while it lasts, the same alert is updated rather than new ones being created.
/// </summary>
public sealed record Alert
{
    public required Guid Id { get; init; }

    /// <summary>Rule that raised the alert.</summary>
    public required string RuleId { get; init; }

    /// <summary>Identifies the condition (e.g. "cpu.sustained", "app.cpu:path:C:\…"). Used for deduplication.</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required AlertSeverity Severity { get; init; }

    public required AlertStatus Status { get; init; }

    /// <summary>When the alert was raised.</summary>
    public required DateTimeOffset RaisedAt { get; init; }

    /// <summary>Last time the condition was observed.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When the condition ended, for resolved alerts.</summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>Metric concerned, e.g. "CPU usage".</summary>
    public required string Metric { get; init; }

    /// <summary>Value observed, e.g. "94% on average".</summary>
    public required string Value { get; init; }

    /// <summary>When the condition started (it had to last before the alert was raised).</summary>
    public DateTimeOffset Since { get; init; }

    /// <summary>How long the condition had lasted when last observed.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Context, e.g. "Usual level 30–45% · Top application: chrome.exe (62%)".</summary>
    public required string Context { get; init; }

    /// <summary>Why the alert was raised, in plain words.</summary>
    public required string Explanation { get; init; }

    public string? Recommendation { get; init; }

    /// <summary>How many times the condition came back within the cooldown and reopened this alert.</summary>
    public int Occurrences { get; init; } = 1;

    public IReadOnlyList<AnalysisEvidence> Evidence { get; init; } = [];

    /// <summary>Application concerned, when the alert is about one.</summary>
    public string? AppKey { get; init; }

    /// <summary>
    /// Name of the application the alert is about ("chrome.exe"), when it is about one. Alerts recorded before this
    /// field existed have none: <see cref="Analysis.RecurringProblemDetector.AppName"/> falls back to their evidence.
    /// </summary>
    public string? AppName { get; init; }

    /// <summary>Where to look further.</summary>
    public DiagnosisAction Action { get; init; }

    /// <summary>True while the condition is ongoing.</summary>
    public bool IsActive => Status != AlertStatus.Resolved;
}

/// <summary>A condition currently met, as reported by a rule. The engine turns conditions into alerts.</summary>
public sealed record AlertCondition
{
    public required string Key { get; init; }

    /// <summary>Rule that reported the condition (set by the engine).</summary>
    public string RuleId { get; init; } = string.Empty;

    public required AlertSeverity Severity { get; init; }

    public required string Title { get; init; }

    public required string Metric { get; init; }

    public required string Value { get; init; }

    public TimeSpan Duration { get; init; }

    public required string Context { get; init; }

    public required string Explanation { get; init; }

    public string? Recommendation { get; init; }

    public IReadOnlyList<AnalysisEvidence> Evidence { get; init; } = [];

    public string? AppKey { get; init; }

    /// <summary>Name of the application the condition is about, when it is about one.</summary>
    public string? AppName { get; init; }

    public DiagnosisAction Action { get; init; }
}

/// <summary>What alert rules look at.</summary>
/// <param name="Now">Time of the evaluation.</param>
/// <param name="Snapshot">Latest full snapshot from the monitor.</param>
/// <param name="Recent">Recent history snapshots, oldest first.</param>
/// <param name="Baseline">Usual behavior of the PC (may still be collecting).</param>
/// <param name="Settings">Rule settings.</param>
public sealed record AlertContext(
    DateTimeOffset Now,
    SystemSnapshot Snapshot,
    IReadOnlyList<MetricSnapshot> Recent,
    UsageBaseline Baseline,
    SmartAlertSettings Settings)
{
    public MetricSnapshot? Latest => Recent.Count > 0 ? Recent[^1] : null;

    /// <summary>
    /// Keys of the alerts currently active. Rules keep an active condition alive with hysteresis (slightly lower
    /// threshold, no minimum duration), so a value hovering around a threshold does not close and reopen alerts.
    /// </summary>
    public IReadOnlySet<string> ActiveKeys { get; init; } = new HashSet<string>();

    public bool IsActive(string key) => ActiveKeys.Contains(key);
}

/// <summary>Changes produced by one evaluation.</summary>
/// <param name="Raised">Alerts created or reopened.</param>
/// <param name="Updated">Ongoing alerts whose values changed.</param>
/// <param name="Resolved">Alerts whose condition ended.</param>
/// <param name="Suppressed">Conditions not raised because of the hourly limit.</param>
public sealed record AlertEvaluation(IReadOnlyList<Alert> Raised, IReadOnlyList<Alert> Updated, IReadOnlyList<Alert> Resolved, int Suppressed)
{
    public static AlertEvaluation None { get; } = new([], [], [], 0);

    public bool HasChanges => Raised.Count > 0 || Updated.Count > 0 || Resolved.Count > 0;
}

/// <summary>Alert severities in words.</summary>
public static class AlertSeverityText
{
    public static string Label(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => Strings.AlertSeverity_Critical,
        AlertSeverity.Warning => Strings.AlertSeverity_Warning,
        _ => Strings.AlertSeverity_Info,
    };
}

/// <summary>Alert statuses in words.</summary>
public static class AlertStatusText
{
    public static string Label(AlertStatus status) => status switch
    {
        AlertStatus.New => Strings.AlertStatus_New,
        AlertStatus.Seen => Strings.AlertStatus_Seen,
        _ => Strings.AlertStatus_Resolved,
    };
}
