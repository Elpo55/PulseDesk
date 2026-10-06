using PulseDesk.Core.Interfaces;

namespace PulseDesk.Core.Alerts;

/// <summary>
/// Default <see cref="IAlertEngine"/>: turns the conditions reported by the rules into alerts without spamming.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>One alert per condition: while it lasts, the same alert is updated (duration, value, severity can only go up).</item>
/// <item>A condition must be absent for <see cref="ResolveDelay"/> before its alert is resolved, so a value hovering
/// around a threshold does not open and close alerts repeatedly.</item>
/// <item>A condition that comes back within the cooldown reopens its previous alert instead of creating a new one.</item>
/// <item>At most <c>MaxNewAlertsPerHour</c> new alerts per hour; extra conditions are counted as suppressed.</item>
/// </list>
/// Not thread-safe: <see cref="AlertService"/> serializes calls.
/// </remarks>
public sealed class AlertEngine(IEnumerable<AlertRule> rules) : IAlertEngine
{
    /// <summary>How long a condition must be absent before its alert is resolved.</summary>
    public static readonly TimeSpan ResolveDelay = TimeSpan.FromSeconds(60);

    /// <summary>Alerts kept in memory (the most recent ones; older ones stay in the history database).</summary>
    public const int MaxAlerts = 200;

    private readonly AlertRule[] _rules = rules.ToArray();
    private readonly List<Alert> _alerts = [];
    private readonly Dictionary<string, DateTimeOffset> _missingSince = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _recentRaises = new();
    private int _suppressed;

    /// <summary>An engine with every built-in rule.</summary>
    public AlertEngine()
        : this(CreateDefaultRules())
    {
    }

    public IReadOnlyList<Alert> Alerts => _alerts;

    /// <summary>Conditions not raised since the engine started because of the hourly limit.</summary>
    public int SuppressedCount => _suppressed;

    public static IReadOnlyList<AlertRule> CreateDefaultRules() =>
    [
        new SustainedCpuAlertRule(),
        new SustainedMemoryAlertRule(),
        new MemoryGrowthAlertRule(),
        new AppCpuAlertRule(),
        new DiskBusyAlertRule(),
        new UnusualActivityAlertRule(),
        new LowDiskSpaceAlertRule(),
    ];

    /// <remarks>
    /// Alerts still active when they were stored come from a previous session. An activity (CPU, memory, disk busy...)
    /// cannot be known to still be going on: it is resolved at its last observation. A lasting state (low disk space)
    /// stays active until the first evaluation: if the condition is still met, the same alert continues (no new alert,
    /// no new notification); otherwise it is resolved at its last observation.
    /// </remarks>
    public void Load(IEnumerable<Alert> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        _alerts.Clear();
        _missingSince.Clear();
        var persistent = _rules.Where(r => r.IsPersistentState).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var alert in alerts.OrderBy(a => a.RaisedAt).TakeLast(MaxAlerts))
        {
            if (!alert.IsActive)
            {
                _alerts.Add(alert);
            }
            else if (persistent.Contains(alert.RuleId))
            {
                _alerts.Add(alert);
                _missingSince[alert.Key] = alert.UpdatedAt;
            }
            else
            {
                _alerts.Add(alert with { Status = AlertStatus.Resolved, ResolvedAt = alert.UpdatedAt });
            }
        }
    }

    public AlertEvaluation Evaluate(AlertContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = context.Now;
        var settings = context.Settings;
        if (!settings.Enabled)
        {
            return ResolveAll(now);
        }

        context = context with { ActiveKeys = _alerts.Where(a => a.IsActive).Select(a => a.Key).ToHashSet(StringComparer.Ordinal) };
        var conditions = new Dictionary<string, AlertCondition>(StringComparer.Ordinal);
        foreach (var rule in _rules)
        {
            foreach (var condition in rule.Evaluate(context))
            {
                conditions[condition.Key] = condition with { RuleId = rule.Id };
            }
        }

        var raised = new List<Alert>();
        var updated = new List<Alert>();
        var resolved = new List<Alert>();
        var suppressed = 0;
        var cooldown = TimeSpan.FromMinutes(settings.CooldownMinutes);

        foreach (var condition in conditions.Values)
        {
            _missingSince.Remove(condition.Key);
            var index = _alerts.FindLastIndex(a => a.Key == condition.Key);
            var existing = index >= 0 ? _alerts[index] : null;

            if (existing is { IsActive: true })
            {
                var escalated = condition.Severity > existing.Severity;
                var next = Apply(existing, condition, now) with
                {
                    Duration = now - existing.Since,
                    Severity = escalated ? condition.Severity : existing.Severity,
                    // A more serious condition deserves the user's attention again.
                    Status = escalated ? AlertStatus.New : existing.Status,
                };
                _alerts[index] = next;
                (escalated ? raised : updated).Add(next);
                continue;
            }

            if (existing is { ResolvedAt: { } resolvedAt } && now - resolvedAt <= cooldown)
            {
                var reopened = Apply(existing, condition, now) with
                {
                    Status = AlertStatus.New,
                    ResolvedAt = null,
                    Severity = condition.Severity,
                    Since = now - condition.Duration,
                    Occurrences = existing.Occurrences + 1,
                };
                _alerts[index] = reopened;
                raised.Add(reopened);
                continue;
            }

            if (!TryReserveRaise(now, settings.MaxNewAlertsPerHour))
            {
                suppressed++;
                continue;
            }

            var alert = Apply(new Alert
            {
                Id = Guid.NewGuid(),
                RuleId = condition.RuleId,
                Key = condition.Key,
                Title = condition.Title,
                Severity = condition.Severity,
                Status = AlertStatus.New,
                RaisedAt = now,
                UpdatedAt = now,
                Since = now - condition.Duration,
                Metric = condition.Metric,
                Value = condition.Value,
                Context = condition.Context,
                Explanation = condition.Explanation,
            }, condition, now);
            _alerts.Add(alert);
            raised.Add(alert);
        }

        for (var i = 0; i < _alerts.Count; i++)
        {
            var alert = _alerts[i];
            if (!alert.IsActive || conditions.ContainsKey(alert.Key))
            {
                continue;
            }

            if (!_missingSince.TryGetValue(alert.Key, out var since))
            {
                _missingSince[alert.Key] = now;
                continue;
            }

            if (now - since >= ResolveDelay)
            {
                var done = alert with { Status = AlertStatus.Resolved, ResolvedAt = since };
                _alerts[i] = done;
                resolved.Add(done);
                _missingSince.Remove(alert.Key);
            }
        }

        while (_alerts.Count > MaxAlerts)
        {
            _alerts.RemoveAt(0);
        }

        _suppressed += suppressed;
        return new AlertEvaluation(raised, updated, resolved, suppressed);
    }

    public Alert? MarkSeen(Guid id)
    {
        var index = _alerts.FindIndex(a => a.Id == id);
        if (index < 0 || _alerts[index].Status != AlertStatus.New)
        {
            return null;
        }

        var seen = _alerts[index] with { Status = AlertStatus.Seen };
        _alerts[index] = seen;
        return seen;
    }

    public IReadOnlyList<Alert> MarkAllSeen()
    {
        var changed = new List<Alert>();
        for (var i = 0; i < _alerts.Count; i++)
        {
            if (_alerts[i].Status == AlertStatus.New)
            {
                _alerts[i] = _alerts[i] with { Status = AlertStatus.Seen };
                changed.Add(_alerts[i]);
            }
        }

        return changed;
    }

    private static Alert Apply(Alert alert, AlertCondition condition, DateTimeOffset now) => alert with
    {
        Title = condition.Title,
        UpdatedAt = now,
        Metric = condition.Metric,
        Value = condition.Value,
        Duration = condition.Duration,
        Context = condition.Context,
        Explanation = condition.Explanation,
        Recommendation = condition.Recommendation,
        Evidence = condition.Evidence,
        AppKey = condition.AppKey,
        Action = condition.Action,
    };

    private bool TryReserveRaise(DateTimeOffset now, int maxPerHour)
    {
        while (_recentRaises.Count > 0 && now - _recentRaises.Peek() >= TimeSpan.FromHours(1))
        {
            _recentRaises.Dequeue();
        }

        if (_recentRaises.Count >= maxPerHour)
        {
            return false;
        }

        _recentRaises.Enqueue(now);
        return true;
    }

    private AlertEvaluation ResolveAll(DateTimeOffset now)
    {
        var resolved = new List<Alert>();
        for (var i = 0; i < _alerts.Count; i++)
        {
            if (_alerts[i].IsActive)
            {
                _alerts[i] = _alerts[i] with { Status = AlertStatus.Resolved, ResolvedAt = now };
                resolved.Add(_alerts[i]);
            }
        }

        _missingSince.Clear();
        return resolved.Count == 0 ? AlertEvaluation.None : new AlertEvaluation([], [], resolved, 0);
    }
}
