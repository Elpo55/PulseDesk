using Sysora.Core.Analysis;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Metrics;
using Sysora.Localization;

namespace Sysora.Core.Diagnosis.Rules;

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
        var state = Text.Format(Strings.Diag_Memory_State, MetricFormatter.Bytes(latest.MemoryUsedBytes), MetricFormatter.Bytes(latest.MemoryTotalBytes), MetricFormatter.Bytes(available));
        var memoryEvidence = new AnalysisEvidence(Strings.Diag_Memory_Physical, state)
        {
            To = latest.Timestamp,
            Source = MetricSources.Memory,
        };

        if (span is { } high && high.Duration >= TimeSpan.FromSeconds(thresholds.MemorySustainSeconds))
        {
            var critical = high.Average >= thresholds.MemoryCriticalPercent;
            var evidence = new List<AnalysisEvidence>
            {
                SpanEvidence(Strings.Diag_Metric_MemoryUsage, high, thresholds.MemoryWarningPercent, MetricSources.Memory),
                memoryEvidence,
                BaselineEvidence(context.Baseline, HistoryMetric.Memory, Strings.Diag_Metric_MemoryUsage),
            };
            if (largest is not null)
            {
                evidence.Add(new AnalysisEvidence(Strings.Diag_Memory_LargestApp, Text.Format(Strings.Common_NameValue, largest.Name, MetricFormatter.Bytes(largest.MemoryBytes))) { Source = MetricSources.Processes });
            }

            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.Memory,
                Severity = critical ? DiagnosisSeverity.Critical : DiagnosisSeverity.Warning,
                Title = critical ? Strings.Diag_Memory_FullTitle : Strings.Diag_Memory_LowTitle,
                Description = Text.Format(Strings.Diag_Memory_HighDescription, Percent(thresholds.MemoryWarningPercent), Duration(high.Duration), MetricFormatter.Bytes(available)),
                Metric = Strings.Diag_Metric_MemoryUsage,
                ObservedValue = Percent(high.Average),
                ReferenceValue = UsualText(usual) ?? Threshold(Percent(thresholds.MemoryWarningPercent)),
                Duration = high.Duration,
                Timestamp = latest.Timestamp,
                Explanation = Strings.Diag_Memory_HighExplanation,
                Recommendation = largest is null
                    ? Strings.Diag_Memory_CloseApps
                    : Text.Format(Strings.Diag_Memory_CloseAppsLargest, largest.Name, MetricFormatter.Bytes(largest.MemoryBytes)),
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
                Title = Strings.Diag_Memory_HighTitle,
                Description = Text.Format(Strings.Diag_Memory_JustReached, Percent(current), MetricFormatter.Bytes(available)),
                Metric = Strings.Diag_Metric_MemoryUsage,
                ObservedValue = Percent(current),
                ReferenceValue = ReportedWhenAbove(Percent(thresholds.MemoryWarningPercent), TimeSpan.FromSeconds(thresholds.MemorySustainSeconds)),
                Timestamp = latest.Timestamp,
                Explanation = Strings.Diag_Memory_JustReachedExplanation,
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
                Title = Strings.Diag_Memory_EnoughTitle,
                Description = Text.Format(Strings.Diag_Memory_EnoughDescription, state, Percent(current)),
                Metric = Strings.Diag_Metric_MemoryUsage,
                ObservedValue = Percent(current),
                ReferenceValue = UsualText(usual) ?? Threshold(Percent(thresholds.MemoryWarningPercent)),
                Timestamp = latest.Timestamp,
                Explanation = Strings.Diag_Memory_EnoughExplanation,
                Confidence = ConfidenceLevel.High,
                Evidence = [memoryEvidence, BaselineEvidence(context.Baseline, HistoryMetric.Memory, Strings.Diag_Metric_MemoryUsage)],
            };
        }

        if (latest.CommitPercent is { } commit && commit >= CommitWarningPercent)
        {
            yield return new DiagnosisResult
            {
                RuleId = Id + ".commit",
                Category = DiagnosisCategory.Memory,
                Severity = DiagnosisSeverity.Warning,
                Title = Strings.Diag_Commit_Title,
                Description = Text.Format(Strings.Diag_Commit_Description, Percent(commit)),
                Metric = Strings.Diag_Metric_Commit,
                ObservedValue = Percent(commit),
                ReferenceValue = Threshold(Percent(CommitWarningPercent)),
                Timestamp = latest.Timestamp,
                Explanation = Strings.Diag_Commit_Explanation,
                Recommendation = Strings.Diag_Commit_Recommendation,
                Confidence = ConfidenceLevel.High,
                Evidence = [new AnalysisEvidence(Strings.Diag_Metric_Commit, Percent(commit)) { Source = MetricSources.Commit, To = latest.Timestamp }],
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
            new(Strings.Diag_Growth_Trend, Text.Format(Strings.Diag_Growth_TrendValue, rise, Duration(summary.Duration), trend.ChangePerMinute))
            {
                Reference = Strings.Diag_Growth_TrendReference,
                From = summary.From,
                To = summary.To,
                SampleCount = summary.Count,
                Source = MetricSources.Memory,
            },
        };
        if (app is not null)
        {
            evidence.Add(new AnalysisEvidence(Text.Format(Strings.Diag_Growth_AppMemory, app.Name), Text.Format(Strings.Diag_Growth_AppGrowth, MetricFormatter.Bytes(growth))) { Source = MetricSources.Processes });
        }

        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.Memory,
            Severity = DiagnosisSeverity.Info,
            Title = Strings.Diag_Growth_Title,
            Description = Text.Format(Strings.Diag_Growth_Description, rise, Duration(summary.Duration))
                + (app is null ? string.Empty : " " + Text.Format(Strings.Diag_Growth_DescriptionApp, app.Name, MetricFormatter.Bytes(growth))),
            Metric = Strings.Diag_Metric_MemoryUsage,
            ObservedValue = Text.Format(Strings.Diag_Growth_Points, rise),
            ReferenceValue = Text.Format(Strings.Diag_Growth_Reference, MinimumRisePoints, Duration(Window)),
            Duration = summary.Duration,
            Timestamp = summary.To,
            Explanation = Strings.Diag_Growth_Explanation,
            Recommendation = app is null
                ? Strings.Diag_Growth_RecommendationReplay
                : Text.Format(Strings.Diag_Growth_RecommendationApp, app.Name),
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
