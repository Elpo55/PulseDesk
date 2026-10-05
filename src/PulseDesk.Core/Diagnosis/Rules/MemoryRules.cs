using PulseDesk.Core.Analysis;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.History;
using PulseDesk.Core.Metrics;

namespace PulseDesk.Core.Diagnosis.Rules;

/// <summary>Is physical or virtual memory running short?</summary>
public sealed class MemoryPressureRule : DiagnosisRule
{
    /// <summary>Commit charge (share of the commit limit) above which Windows is close to refusing allocations.</summary>
    public const double CommitWarningPercent = 90;

    public override string Id => "memory.pressure";

    public override IEnumerable<DiagnosisResult> Evaluate(DiagnosisContext context)
    {
        if (context.Latest is not { MemoryPercent: { } current } latest)
        {
            yield break;
        }

        var thresholds = context.Thresholds;
        var usual = context.Baseline.Get(HistoryMetric.Memory);
        var span = SnapshotStatistics.Sustained(context.Recent, s => s.MemoryPercent, thresholds.MemoryWarningPercent);
        var available = latest.MemoryTotalBytes is { } total && latest.MemoryUsedBytes is { } used ? total - Math.Min(used, total) : (ulong?)null;
        var largest = latest.TopApps.Count > 0 ? latest.TopApps.MaxBy(a => a.MemoryBytes) : null;
        var state = Format($"{MetricFormatter.Bytes(latest.MemoryUsedBytes)} of {MetricFormatter.Bytes(latest.MemoryTotalBytes)} in use, {MetricFormatter.Bytes(available)} available");
        var memoryEvidence = new AnalysisEvidence("Physical memory", state)
        {
            To = latest.Timestamp,
            Source = MetricSources.Memory,
        };

        if (span is { } high && high.Duration >= TimeSpan.FromSeconds(thresholds.MemorySustainSeconds))
        {
            var critical = high.Average >= thresholds.MemoryCriticalPercent;
            var evidence = new List<AnalysisEvidence>
            {
                SpanEvidence("Memory usage", high, thresholds.MemoryWarningPercent, MetricSources.Memory),
                memoryEvidence,
                BaselineEvidence(context.Baseline, HistoryMetric.Memory, "memory usage"),
            };
            if (largest is not null)
            {
                evidence.Add(new AnalysisEvidence("Largest application", $"{largest.Name}: {MetricFormatter.Bytes(largest.MemoryBytes)}") { Source = MetricSources.Processes });
            }

            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.Memory,
                Severity = critical ? DiagnosisSeverity.Critical : DiagnosisSeverity.Warning,
                Title = critical ? "Memory is nearly full" : "Available memory is low",
                Description = $"Memory usage has stayed above {Percent(thresholds.MemoryWarningPercent)} for {Duration(high.Duration)} ({MetricFormatter.Bytes(available)} available).",
                Metric = "Memory usage",
                ObservedValue = Percent(high.Average),
                ReferenceValue = UsualText(usual) ?? $"Threshold {Percent(thresholds.MemoryWarningPercent)}",
                Duration = high.Duration,
                Timestamp = latest.Timestamp,
                Explanation = "When memory runs short, Windows moves data to the disk (paging), which is much slower than memory: applications can pause or respond slowly.",
                Recommendation = largest is null
                    ? "Close applications you are not using."
                    : $"Close applications you are not using. The largest is {largest.Name} ({MetricFormatter.Bytes(largest.MemoryBytes)}).",
                Confidence = high.Duration >= TimeSpan.FromSeconds(thresholds.MemorySustainSeconds * 2) ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                Evidence = evidence,
                AppKey = largest?.Key,
                Action = DiagnosisAction.AppImpact,
            };
        }
        else if (current >= thresholds.MemoryWarningPercent)
        {
            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.Memory,
                Severity = DiagnosisSeverity.Info,
                Title = "Memory usage is high",
                Description = $"Memory usage just reached {Percent(current)} ({MetricFormatter.Bytes(available)} available).",
                Metric = "Memory usage",
                ObservedValue = Percent(current),
                ReferenceValue = $"Reported when above {Percent(thresholds.MemoryWarningPercent)} for {Duration(TimeSpan.FromSeconds(thresholds.MemorySustainSeconds))}",
                Timestamp = latest.Timestamp,
                Explanation = "If it stays this high, Windows will start moving data to the disk, which slows applications down.",
                Confidence = ConfidenceLevel.Low,
                Evidence = [memoryEvidence],
                Action = DiagnosisAction.AppImpact,
            };
        }
        else
        {
            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.Memory,
                Severity = DiagnosisSeverity.Normal,
                Title = "Enough memory available",
                Description = $"{state} ({Percent(current)}).",
                Metric = "Memory usage",
                ObservedValue = Percent(current),
                ReferenceValue = UsualText(usual) ?? $"Threshold {Percent(thresholds.MemoryWarningPercent)}",
                Timestamp = latest.Timestamp,
                Explanation = "Applications have room to work without Windows moving data to the disk.",
                Confidence = ConfidenceLevel.High,
                Evidence = [memoryEvidence, BaselineEvidence(context.Baseline, HistoryMetric.Memory, "memory usage")],
            };
        }

        if (latest.CommitPercent is { } commit && commit >= CommitWarningPercent)
        {
            yield return new DiagnosisResult
            {
                RuleId = Id + ".commit",
                Category = DiagnosisCategory.Memory,
                Severity = DiagnosisSeverity.Warning,
                Title = "Virtual memory is nearly exhausted",
                Description = $"Windows has committed {Percent(commit)} of its virtual memory limit (physical memory + page file).",
                Metric = "Commit charge",
                ObservedValue = Percent(commit),
                ReferenceValue = Format($"Threshold {CommitWarningPercent:0}%"),
                Timestamp = latest.Timestamp,
                Explanation = "When the commit charge reaches its limit, applications can fail to get memory and may crash or freeze.",
                Recommendation = "Close memory-hungry applications. If this happens often, let Windows manage the page file size automatically.",
                Confidence = ConfidenceLevel.High,
                Evidence = [new AnalysisEvidence("Commit charge", Percent(commit)) { Source = MetricSources.Commit, To = latest.Timestamp }],
                Action = DiagnosisAction.Performance,
            };
        }
    }
}

/// <summary>Has memory usage been rising steadily (a possible leak)?</summary>
public sealed class MemoryGrowthRule : DiagnosisRule
{
    /// <summary>Period examined.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(20);

    /// <summary>Minimum history before a trend is computed.</summary>
    public static readonly TimeSpan MinimumData = TimeSpan.FromMinutes(10);

    /// <summary>Minimum rise over the window, in percentage points of physical memory.</summary>
    public const double MinimumRisePoints = 8;

    /// <summary>Minimum growth for an application to be named.</summary>
    public const double MinimumAppGrowthBytes = 300 * 1024 * 1024;

    public override string Id => "memory.growth";

    public override IEnumerable<DiagnosisResult> Evaluate(DiagnosisContext context)
    {
        var window = context.Last(Window);
        if (SnapshotStatistics.Summarize(window, s => s.MemoryPercent) is not { } summary || summary.Duration < MinimumData)
        {
            yield break;
        }

        var trend = SnapshotStatistics.Trend(window, s => s.MemoryPercent, MinimumRisePoints);
        var rise = trend.ChangePerMinute * summary.Duration.TotalMinutes;
        if (trend.Direction != TrendDirection.Rising || rise < MinimumRisePoints)
        {
            yield break;
        }

        var (app, growth) = LargestGrowth(window);
        var evidence = new List<AnalysisEvidence>
        {
            new("Memory usage trend", Format($"+{rise:0.#} points in {Duration(summary.Duration)} ({trend.ChangePerMinute:0.##} points per minute)"))
            {
                Reference = "Robust trend (Theil–Sen), insensitive to isolated spikes",
                From = summary.From,
                To = summary.To,
                SampleCount = summary.Count,
                Source = MetricSources.Memory,
            },
        };
        if (app is not null)
        {
            evidence.Add(new AnalysisEvidence($"{app.Name} memory", $"+{MetricFormatter.Bytes(growth)} over the same period") { Source = MetricSources.Processes });
        }

        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.Memory,
            Severity = DiagnosisSeverity.Info,
            Title = "Memory usage has been rising steadily",
            Description = Format($"Memory usage rose by {rise:0} points in {Duration(summary.Duration)}.")
                + (app is null ? string.Empty : $" {app.Name} grew by {MetricFormatter.Bytes(growth)} in the same period."),
            Metric = "Memory usage",
            ObservedValue = Format($"+{rise:0} points"),
            ReferenceValue = Format($"Reported from +{MinimumRisePoints:0} points over {Duration(Window)}"),
            Duration = summary.Duration,
            Timestamp = summary.To,
            Explanation = "A steady rise can be normal (an application loading data) or a sign that an application does not release memory (a leak). PulseDesk cannot tell which from these measurements alone.",
            Recommendation = app is null ? "Watch memory usage in Replay; if it keeps rising, restart the application responsible." : $"If {app.Name} keeps growing, restarting it will free the memory.",
            Confidence = app is null ? ConfidenceLevel.Low : ConfidenceLevel.Medium,
            Evidence = evidence,
            AppKey = app?.Key,
            Action = app is null ? DiagnosisAction.Replay : DiagnosisAction.AppImpact,
        };
    }

    /// <summary>The application whose memory grew the most between the first and last process samples of the window.</summary>
    internal static (AppSample? App, double Growth) LargestGrowth(IReadOnlyList<MetricSnapshot> window)
    {
        var first = window.FirstOrDefault(s => s.TopApps.Count > 0)?.TopApps;
        var last = window.LastOrDefault(s => s.TopApps.Count > 0)?.TopApps;
        if (first is null || last is null || ReferenceEquals(first, last))
        {
            return (null, 0);
        }

        var before = first.ToDictionary(a => a.Key, a => (double)a.MemoryBytes, StringComparer.Ordinal);
        var best = last
            .Where(a => before.ContainsKey(a.Key))
            .Select(a => (App: a, Growth: a.MemoryBytes - before[a.Key]))
            .OrderByDescending(x => x.Growth)
            .FirstOrDefault();
        return best.App is not null && best.Growth >= MinimumAppGrowthBytes ? (best.App, best.Growth) : (null, 0);
    }
}
