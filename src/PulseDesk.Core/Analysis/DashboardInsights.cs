using System.Globalization;
using PulseDesk.Core.Alerts;
using PulseDesk.Core.Diagnosis;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.History;

namespace PulseDesk.Core.Analysis;

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

    public static IReadOnlyList<Insight> Build(
        DiagnosisReport report,
        IReadOnlyList<Alert> alerts,
        IReadOnlyList<MetricSnapshot> recent,
        UsageBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(recent);
        ArgumentNullException.ThrowIfNull(baseline);
        var insights = new List<Insight>();

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
            var more = active.Count > 1 ? $" (+{active.Count - 1} more)" : string.Empty;
            insights.Add(new Insight($"Active alert: {newest.Title}{more}", newest.Severity switch
            {
                AlertSeverity.Critical => DiagnosisSeverity.Critical,
                AlertSeverity.Warning => DiagnosisSeverity.Warning,
                _ => DiagnosisSeverity.Info,
            }, DiagnosisAction.Alerts)
            {
                AppKey = newest.AppKey,
            });
        }

        if (Compared(recent, baseline) is { } comparison)
        {
            insights.Add(comparison);
        }

        if (LargestConsumer(recent) is { } consumer)
        {
            insights.Add(consumer);
        }

        if (!baseline.IsReady && recent.Count > 0)
        {
            insights.Add(new Insight(baseline.Description, DiagnosisSeverity.Normal, DiagnosisAction.None) { IsNote = true });
        }

        if (!insights.Any(i => i.Severity >= DiagnosisSeverity.Info))
        {
            insights.Insert(0, new Insight(report.Results.Count == 0 ? "Analyzing the first measurements…" : "No significant issue detected", DiagnosisSeverity.Normal, DiagnosisAction.None));
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
        foreach (var (metric, name) in new[] { (HistoryMetric.Memory, "Memory usage"), (HistoryMetric.Cpu, "CPU usage") })
        {
            if (baseline.Get(metric) is not { } usual || SnapshotStatistics.Summarize(window, s => s.Get(metric)) is not { } summary)
            {
                continue;
            }

            var gap = summary.Average - usual.Median;
            if (Math.Abs(gap) >= 10 && Math.Abs(gap) > bestGap)
            {
                bestGap = Math.Abs(gap);
                var direction = gap > 0 ? "above" : "below";
                best = new Insight(
                    string.Create(CultureInfo.CurrentCulture, $"{name} is {Math.Abs(gap):0} points {direction} your usual level ({MetricFormatter.Percent(summary.Average)} vs usually {usual.UsualRange})"),
                    gap > 0 ? DiagnosisSeverity.Info : DiagnosisSeverity.Normal,
                    DiagnosisAction.Replay);
            }
        }

        return best;
    }

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
            $"{top.Name} is currently the largest resource consumer ({MetricFormatter.Percent(top.CpuPercent)} CPU, {MetricFormatter.Bytes(top.MemoryBytes)})",
            DiagnosisSeverity.Normal,
            DiagnosisAction.AppImpact)
        {
            AppKey = top.Key,
        };
    }
}
