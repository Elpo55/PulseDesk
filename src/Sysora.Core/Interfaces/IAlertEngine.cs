using Sysora.Core.Alerts;

namespace Sysora.Core.Interfaces;

/// <summary>
/// Turns measurements into alerts: evaluates the rules, deduplicates, resolves and limits alerts. Pure logic
/// with in-memory state; persistence and threading are handled by <see cref="AlertService"/>.
/// </summary>
public interface IAlertEngine
{
    /// <summary>Current alerts, oldest first.</summary>
    IReadOnlyList<Alert> Alerts { get; }

    /// <summary>Replaces the alerts (for example with the ones stored in the history).</summary>
    void Load(IEnumerable<Alert> alerts);

    /// <summary>Evaluates the rules and returns what changed.</summary>
    AlertEvaluation Evaluate(AlertContext context);

    /// <summary>Marks a new alert as seen. Returns the updated alert, or null when nothing changed.</summary>
    Alert? MarkSeen(Guid id);

    /// <summary>Marks every new alert as seen. Returns the updated alerts.</summary>
    IReadOnlyList<Alert> MarkAllSeen();
}
