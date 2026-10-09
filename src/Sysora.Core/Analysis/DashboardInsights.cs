using System.Globalization;
using Sysora.Core.Alerts;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.Gaming;
using Sysora.Core.Health;
using Sysora.Core.History;
using Sysora.Localization;

namespace Sysora.Core.Analysis;

/// <summary>One line of the dashboard's "Insights": something worth knowing right now, and where to look.</summary>
/// <param name="Text">What to know, e.g. "chrome.exe is currently the largest resource consumer".</param>
/// <param name="Severity">How important it is.</param>
/// <param name="Action">Where to look further.</param>
public sealed record Insight(string Text, DiagnosisSeverity Severity, DiagnosisAction Action)
{
    public string? AppKey { get; init; }

    /// <summary>A neutral note (for example "collecting baseline data"), neither good nor bad.</summary>
    public bool IsNote { get; init; }
}

/// <summary>
/// Builds the dashboard's answer to "how is my PC?": an overall state and a few insights, from the diagnosis,
/// the active alerts, the recent history and the usual-behavior baseline. Pure and deterministic.
/// </summary>
public static class DashboardInsights
{
    /// <summary>Insights shown at most.</summary>
    public const int MaxInsights = 5;

    /// <summary>Overall state, taking ongoing alerts into account.</summary>
    public static PcHealthState State(DiagnosisReport report, IReadOnlyList<Alert> alerts)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(alerts);
        var active = alerts.Where(a => a.IsActive).ToList();
        var state = report.State;
        if (active.Any(a => a.Severity == AlertSeverity.Critical))
        {
            return PcHealthState.Problem;
        }

        if (active.Any(a => a.Severity == AlertSeverity.Warning) && state < PcHealthState.Attention)
        {
            return PcHealthState.Attention;
        }

        return state;
    }

    /// <summary>A finished game session is mentioned for this long after it ended.</summary>
    public static readonly TimeSpan RecentGameRecap = TimeSpan.FromHours(2);

    /// <param name="report">Latest diagnosis.</param>
    /// <param name="alerts">Current alerts.</param>
    /// <param name="recent">Recent history snapshots.</param>
    /// <param name="baseline">Usual behavior.</param>
    /// <param name="games">Game sessions in progress.</param>
    /// <param name="lastGame">Recap of the last game session, if any.</param>
    /// <param name="health">Latest PC Health score, if computed.</param>
    /// <param name="recurring">Recurring problems of the last days, if known.</param>
    public static IReadOnlyList<Insight> Build(
        DiagnosisReport report,
        IReadOnlyList<Alert> alerts,
        IReadOnlyList<MetricSnapshot> recent,
        UsageBaseline baseline,
        IReadOnlyList<LiveGameSession>? games = null,
        GameRecap? lastGame = null,
        PcHealthReport? health = null,
        RecurringProblemReport? recurring = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(recent);
        ArgumentNullException.ThrowIfNull(baseline);
        var insights = new List<Insight>();
        if (games is { Count: > 0 })
        {
            var game = games[0];
            var since = game.Duration < TimeSpan.FromMinutes(1)
                ? Strings.Insight_Game_JustStarted
                : Text.Format(Strings.Insight_Game_For, MetricFormatter.DurationPrecise(game.Duration));
            insights.Add(new Insight(Text.Format(Strings.Insight_Game_Running, game.Name, since), DiagnosisSeverity.Normal, DiagnosisAction.Gaming)
            {
                IsNote = true,
            });
        }
        else if (lastGame is { } recap && report.Timestamp - recap.Session.End <= RecentGameRecap)
        {
            insights.Add(new Insight(Text.Format(Strings.Insight_LastGame, recap.Session.Name, MetricFormatter.DurationPrecise(recap.Session.Duration), recap.Headline), recap.Severity >= DiagnosisSeverity.Warning ? DiagnosisSeverity.Info : DiagnosisSeverity.Normal, DiagnosisAction.Gaming)
            {
                IsNote = recap.Severity < DiagnosisSeverity.Warning,
            });
        }

        foreach (var result in report.Results.Where(r => r.Severity >= DiagnosisSeverity.Info).Take(3))
        {
            insights.Add(new Insight(result.Title, result.Severity, result.Action == DiagnosisAction.None ? DiagnosisAction.AppImpact : result.Action)
            {
                AppKey = result.AppKey,
            });
        }

        var active = alerts.Where(a => a.IsActive).ToList();
        if (active.Count > 0)
        {
            var newest = active.OrderByDescending(a => a.Severity).ThenByDescending(a => a.UpdatedAt).First();
            var text = active.Count > 1
                ? Text.Format(Strings.Insight_ActiveAlertMore, newest.Title, active.Count - 1)
                : Text.Format(Strings.Insight_ActiveAlert, newest.Title);
            insights.Add(new Insight(text, newest.Severity switch
            {
                AlertSeverity.Critical => DiagnosisSeverity.Critical,
                AlertSeverity.Warning => DiagnosisSeverity.Warning,
                _ => DiagnosisSeverity.Info,
            }, DiagnosisAction.Alerts)
            {
                AppKey = newest.AppKey,
            });
        }

        if (Repeated(alerts, report.Timestamp) is { } repeated)
        {
            insights.Add(repeated);
        }

        if (recurring is { Problems: [var problem, ..] })
        {
            insights.Add(new Insight(Text.Format(Strings.Insight_Recurring, problem.Description) + (problem.TimePattern is { } pattern ? $" {pattern}." : string.Empty), DiagnosisSeverity.Info, DiagnosisAction.PcHealth)
            {
                AppKey = problem.AssociatedAppKey,
            });
        }

        if (health is { Score: { } score, Grade: PcHealthGrade.NeedsAttention or PcHealthGrade.Poor }
            && health.Components.Where(c => c.Penalty > 0).MaxBy(c => c.Penalty) is { } lowest)
        {
            insights.Add(new Insight(Text.Format(Strings.Insight_Health, score, LowerFirst(lowest.Name), lowest.Summary), DiagnosisSeverity.Info, DiagnosisAction.PcHealth));
        }

        if (Compared(recent, baseline) is { } comparison)
        {
            insights.Add(comparison);
        }

        if (LargestConsumer(recent) is { } consumer)
        {
            insights.Add(consumer);
        }

        if (Recovered(alerts, report.Timestamp) is { } recovered)
        {
            insights.Add(recovered);
        }

        if (!baseline.IsReady && recent.Count > 0)
        {
            insights.Add(new Insight(baseline.Description, DiagnosisSeverity.Normal, DiagnosisAction.None) { IsNote = true });
        }

        if (!insights.Any(i => i.Severity >= DiagnosisSeverity.Info))
        {
            insights.Insert(0, new Insight(report.Results.Count == 0 ? Strings.Insight_AnalyzingFirst : Strings.Insight_NoIssue, DiagnosisSeverity.Normal, DiagnosisAction.None));
            if (WithinUsual(recent, baseline) is { } usual)
            {
                insights.Insert(1, usual);
            }
        }

        return insights
            .GroupBy(i => i.Text, StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(MaxInsights)
            .ToArray();
    }

    /// <summary>"Memory usage is 18 points above your usual level", when the baseline is known and the gap is clear.</summary>
    private static Insight? Compared(IReadOnlyList<MetricSnapshot> recent, UsageBaseline baseline)
    {
        if (!baseline.IsReady || recent.Count == 0)
        {
            return null;
        }

        var window = recent.Where(s => s.Timestamp >= recent[^1].Timestamp - TimeSpan.FromMinutes(10)).ToArray();
        Insight? best = null;
        var bestGap = 0.0;
        foreach (var metric in new[] { HistoryMetric.Memory, HistoryMetric.Cpu })
        {
            if (baseline.Get(metric) is not { } usual || SnapshotStatistics.Summarize(window, s => s.Get(metric)) is not { } summary)
            {
                continue;
            }

            var gap = summary.Average - usual.Median;
            if (Math.Abs(gap) >= 10 && Math.Abs(gap) > bestGap)
            {
                bestGap = Math.Abs(gap);
                var template = (metric, gap > 0) switch
                {
                    (HistoryMetric.Memory, true) => Strings.Insight_MemoryAbove,
                    (HistoryMetric.Memory, false) => Strings.Insight_MemoryBelow,
                    (_, true) => Strings.Insight_CpuAbove,
                    _ => Strings.Insight_CpuBelow,
                };
                best = new Insight(
                    Text.Format(template, Math.Abs(gap), MetricFormatter.Percent(summary.Average), usual.UsualRange),
                    gap > 0 ? DiagnosisSeverity.Info : DiagnosisSeverity.Normal,
                    DiagnosisAction.Replay);
            }
        }

        return best;
    }

    /// <summary>A resolved alert counts as a recovery for this long after it ended.</summary>
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(30);

    /// <summary>Alerts counted when looking for a problem repeating the same day.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(24);

    /// <summary>"Your PC recovered…": the latest warning that ended recently, when nothing of the same kind is still active.</summary>
    private static Insight? Recovered(IReadOnlyList<Alert> alerts, DateTimeOffset now)
    {
        var resolved = alerts
            .Where(a => a.Status == AlertStatus.Resolved && a.ResolvedAt is { } at && at <= now && now - at <= RecoveryWindow && a.Severity >= AlertSeverity.Warning)
            .MaxBy(a => a.ResolvedAt);
        if (resolved is null || alerts.Any(a => a.IsActive && a.RuleId == resolved.RuleId))
        {
            return null;
        }

        var at = resolved.ResolvedAt!.Value.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        return new Insight(Text.Format(Strings.Insight_Recovered, resolved.Title, at), DiagnosisSeverity.Normal, DiagnosisAction.Replay)
        {
            AppKey = resolved.AppKey,
        };
    }

    /// <summary>"This is the third busy disk alert in the last 24 hours", when a kind of alert repeats.</summary>
    private static Insight? Repeated(IReadOnlyList<Alert> alerts, DateTimeOffset now)
    {
        var group = alerts
            .Where(a => a.RaisedAt <= now && now - a.RaisedAt <= RepeatWindow)
            .GroupBy(a => a.RuleId == "unusual" ? a.Key : a.RuleId == "app.cpu" ? $"{a.RuleId}:{a.AppKey}" : a.RuleId, StringComparer.Ordinal)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(a => a.RaisedAt))
            .FirstOrDefault();
        if (group is null)
        {
            return null;
        }

        var newest = group.MaxBy(a => a.RaisedAt)!;
        var kind = newest.RuleId switch
        {
            "cpu.sustained" => Strings.Insight_Kind_HighCpu,
            "memory.sustained" => Strings.Insight_Kind_HighMemory,
            "memory.growth" => Strings.Insight_Kind_RisingMemory,
            "disk.busy" => Strings.Insight_Kind_BusyDisk,
            "app.cpu" => Diagnosis.Rules.CpuHungryAppRule.AppCpu(RecurringProblemDetector.AppName(newest) ?? string.Empty),
            "storage.low" => Strings.Insight_Kind_LowSpace,
            _ => Strings.Insight_Kind_Unusual,
        };
        return new Insight(Text.Format(Strings.Insight_Repeated, Ordinal(group.Count()), kind, newest.Title), DiagnosisSeverity.Info, DiagnosisAction.Alerts)
        {
            AppKey = newest.AppKey,
        };
    }

    /// <summary>"CPU and memory usage are within your usual range", when the baseline says so.</summary>
    private static Insight? WithinUsual(IReadOnlyList<MetricSnapshot> recent, UsageBaseline baseline)
    {
        if (!baseline.IsReady || recent.Count == 0)
        {
            return null;
        }

        var window = recent.Where(s => s.Timestamp >= recent[^1].Timestamp - TimeSpan.FromMinutes(10)).ToArray();
        foreach (var metric in new[] { HistoryMetric.Cpu, HistoryMetric.Memory })
        {
            if (baseline.Get(metric) is not { } usual || SnapshotStatistics.Summarize(window, s => s.Get(metric)) is not { } summary || summary.Average > usual.P95)
            {
                return null;
            }
        }

        return new Insight(Strings.Insight_WithinUsual, DiagnosisSeverity.Normal, DiagnosisAction.None) { IsNote = true };
    }

    private static string Ordinal(int value) => value switch
    {
        2 => Strings.Ordinal_2,
        3 => Strings.Ordinal_3,
        4 => Strings.Ordinal_4,
        5 => Strings.Ordinal_5,
        _ => Text.Format(Strings.Ordinal_N, value),
    };

    /// <summary>"Memory" → "memory" inside a sentence; acronyms ("GPU") are kept as they are.</summary>
    private static string LowerFirst(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && char.IsLower(text[1])
            ? char.ToLower(text[0], CultureInfo.CurrentCulture) + text[1..]
            : text;

    /// <summary>The application using the most resources now, when it is significant.</summary>
    private static Insight? LargestConsumer(IReadOnlyList<MetricSnapshot> recent)
    {
        if (recent.Count == 0 || recent[^1] is not { TopApps.Count: > 0 } latest)
        {
            return null;
        }

        var total = latest.MemoryTotalBytes ?? 0;
        double Weight(AppSample a) => a.CpuPercent + (total > 0 ? a.MemoryBytes * 100.0 / total : 0);
        var top = latest.TopApps.MaxBy(Weight)!;
        var memoryShare = total > 0 ? top.MemoryBytes * 100.0 / total : 0;
        if (top.CpuPercent < 10 && memoryShare < 10)
        {
            return null;
        }

        return new Insight(
            Text.Format(Strings.Insight_LargestConsumer, top.Name, MetricFormatter.Percent(top.CpuPercent), MetricFormatter.Bytes(top.MemoryBytes)),
            DiagnosisSeverity.Normal,
            DiagnosisAction.AppImpact)
        {
            AppKey = top.Key,
        };
    }
}
