using Sysora.Core.Analysis;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Localization;

namespace Sysora.Core.Diagnosis.Rules;

/// <summary>Is the processor busy enough, for long enough, to slow the PC down?</summary>
public sealed class CpuLoadRule : DiagnosisRule
{
    public override string Id => "cpu.load";

    public override IEnumerable<DiagnosisResult> Evaluate(DiagnosisContext context)
    {
        if (context.Latest is not { CpuPercent: { } current } latest
            || SnapshotStatistics.Summarize(context.Last(TimeSpan.FromMinutes(1)), s => s.CpuPercent) is not { } minute)
        {
            yield break;
        }

        var thresholds = context.Thresholds;
        var sustain = TimeSpan.FromSeconds(thresholds.CpuSustainSeconds);
        var usual = context.Baseline.Get(HistoryMetric.Cpu);
        var span = SnapshotStatistics.Sustained(context.Recent, s => s.CpuPercent, thresholds.CpuWarningPercent);
        var minuteEvidence = WindowEvidence(Strings.Diag_CpuLastMinute, minute, MetricSources.Cpu);

        if (span is { } busy && busy.Duration >= sustain)
        {
            var critical = busy.Average >= thresholds.CpuCriticalPercent;
            var unusual = usual is not null && busy.Average > usual.P95;
            var explanation = usual is null
                ? Strings.Diag_CpuLoad_Explanation
                : unusual
                    ? Text.Format(Strings.Diag_CpuLoad_ExplanationUnusual, usual.UsualRange)
                    : Text.Format(Strings.Diag_CpuLoad_ExplanationUsual, usual.UsualRange);

            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.Cpu,
                Severity = critical ? DiagnosisSeverity.Critical : DiagnosisSeverity.Warning,
                Title = critical ? Strings.Diag_CpuLoad_SaturatedTitle : Strings.Diag_CpuLoad_HighTitle,
                Description = Text.Format(Strings.Diag_CpuLoad_HighDescription, Percent(thresholds.CpuWarningPercent), Duration(busy.Duration), Percent(busy.Average)),
                Metric = Strings.Diag_Metric_CpuUsage,
                ObservedValue = Percent(busy.Average),
                ReferenceValue = UsualText(usual) ?? Threshold(Percent(thresholds.CpuWarningPercent)),
                Duration = busy.Duration,
                Timestamp = latest.Timestamp,
                Explanation = explanation,
                Recommendation = Strings.Diag_CpuLoad_Recommendation,
                Confidence = busy.Duration >= sustain * 2 ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                Evidence = [SpanEvidence(Strings.Diag_Metric_CpuUsage, busy, thresholds.CpuWarningPercent, MetricSources.Cpu), BaselineEvidence(context.Baseline, HistoryMetric.Cpu, Strings.Diag_Metric_CpuUsage)],
                Action = DiagnosisAction.AppImpact,
            };
            yield break;
        }

        if (current >= thresholds.CpuWarningPercent)
        {
            var since = span?.Duration ?? TimeSpan.Zero;
            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.Cpu,
                Severity = DiagnosisSeverity.Info,
                Title = Strings.Diag_CpuSpike_Title,
                Description = Text.Format(Strings.Diag_CpuSpike_Description, Percent(current), Duration(since)),
                Metric = Strings.Diag_Metric_CpuUsage,
                ObservedValue = Percent(current),
                ReferenceValue = ReportedWhenAbove(Percent(thresholds.CpuWarningPercent), sustain),
                Duration = since,
                Timestamp = latest.Timestamp,
                Explanation = Strings.Diag_CpuSpike_Explanation,
                Confidence = ConfidenceLevel.Low,
                Evidence = [minuteEvidence],
                Action = DiagnosisAction.Replay,
            };
            yield break;
        }

        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.Cpu,
            Severity = DiagnosisSeverity.Normal,
            Title = Strings.Diag_CpuNormal_Title,
            Description = Text.Format(Strings.Diag_CpuNormal_Description, Percent(minute.Average), Percent(minute.Peak)),
            Metric = Strings.Diag_Metric_CpuUsage,
            ObservedValue = Percent(minute.Average),
            ReferenceValue = UsualText(usual) ?? Threshold(Percent(thresholds.CpuWarningPercent)),
            Timestamp = latest.Timestamp,
            Explanation = Strings.Diag_CpuNormal_Explanation,
            Confidence = ConfidenceLevel.High,
            Evidence = [minuteEvidence, BaselineEvidence(context.Baseline, HistoryMetric.Cpu, Strings.Diag_Metric_CpuUsage)],
        };
    }
}

/// <summary>Is one application responsible for most of the processor load?</summary>
public sealed class CpuHungryAppRule : DiagnosisRule
{
    /// <summary>Above this duration, the application is described as busy "for several minutes".</summary>
    public static readonly TimeSpan LongDuration = TimeSpan.FromMinutes(3);

    public override string Id => "apps.cpu";

    public override IEnumerable<DiagnosisResult> Evaluate(DiagnosisContext context)
    {
        if (context.Latest is not { CpuPercent: { } total } latest || latest.TopApps.Count == 0)
        {
            yield break;
        }

        var top = latest.TopApps.MaxBy(a => a.CpuPercent)!;
        var thresholds = context.Thresholds;
        var series = SnapshotStatistics.AppSeries(context.Recent, top.Key);
        var busy = SustainedApp(series, thresholds.ProcessCpuWarningPercent);
        var appEvidence = new AnalysisEvidence(AppCpu(top.Name), Text.Format(Strings.Diag_AppCpu_Now, MetricFormatter.Percent(top.CpuPercent, 1), Text.Plural(top.InstanceCount, Strings.Count_Process_One, Strings.Count_Process_Other)))
        {
            Reference = Text.Format(Strings.Diag_AppCpu_TotalNow, Percent(total)),
            Source = MetricSources.Processes,
            To = latest.Timestamp,
        };

        if (busy is { } span && span.Duration >= TimeSpan.FromSeconds(thresholds.CpuSustainSeconds))
        {
            var longRunning = span.Duration >= LongDuration;
            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.Applications,
                Severity = DiagnosisSeverity.Warning,
                Title = longRunning
                    ? Text.Format(Strings.Diag_AppCpu_LongTitle, top.Name)
                    : Text.Format(Strings.Diag_AppCpu_LargeShareTitle, top.Name),
                Description = Text.Format(Strings.Diag_AppCpu_Description, top.Name, Percent(span.Average), Duration(span.Duration)),
                Metric = Strings.Diag_Metric_AppCpu,
                ObservedValue = Percent(span.Average),
                ReferenceValue = PerAppThreshold(Percent(thresholds.ProcessCpuWarningPercent)),
                Duration = span.Duration,
                Timestamp = latest.Timestamp,
                Explanation = Strings.Diag_AppCpu_Explanation,
                Recommendation = Text.Format(Strings.Diag_AppCpu_Recommendation, top.Name),
                Confidence = longRunning ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                Evidence = [SpanEvidence(AppCpu(top.Name), span, thresholds.ProcessCpuWarningPercent, MetricSources.Processes), appEvidence],
                AppKey = top.Key,
                Action = DiagnosisAction.AppImpact,
            };
            yield break;
        }

        if (total >= thresholds.CpuWarningPercent && total > 0 && top.CpuPercent / total >= 0.6)
        {
            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.Applications,
                Severity = DiagnosisSeverity.Info,
                Title = Text.Format(Strings.Diag_AppCpu_MainTitle, top.Name),
                Description = Text.Format(Strings.Diag_AppCpu_MainDescription, top.Name, Percent(top.CpuPercent / total * 100), Percent(top.CpuPercent), Percent(total)),
                Metric = Strings.Diag_Metric_AppCpu,
                ObservedValue = Percent(top.CpuPercent),
                ReferenceValue = Text.Format(Strings.Diag_AppCpu_TotalCpu, Percent(total)),
                Timestamp = latest.Timestamp,
                Explanation = Strings.Diag_AppCpu_MainExplanation,
                Confidence = ConfidenceLevel.Medium,
                Evidence = [appEvidence],
                AppKey = top.Key,
                Action = DiagnosisAction.AppImpact,
            };
            yield break;
        }

        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.Applications,
            Severity = DiagnosisSeverity.Normal,
            Title = Strings.Diag_AppCpu_NoneTitle,
            Description = Text.Format(Strings.Diag_AppCpu_NoneDescription, top.Name, MetricFormatter.Percent(top.CpuPercent, 1)),
            Metric = Strings.Diag_Metric_AppCpu,
            ObservedValue = MetricFormatter.Percent(top.CpuPercent, 1),
            ReferenceValue = PerAppThreshold(Percent(thresholds.ProcessCpuWarningPercent)),
            Timestamp = latest.Timestamp,
            Explanation = Strings.Diag_AppCpu_NoneExplanation,
            Confidence = ConfidenceLevel.High,
            Evidence = [appEvidence],
            AppKey = top.Key,
        };
    }

    /// <summary>"chrome.exe CPU": the name of an application's CPU evidence.</summary>
    internal static string AppCpu(string name) => Text.Format(Strings.Diag_AppCpu_EvidenceName, name);

    private static string PerAppThreshold(string value) =>
        Text.Format(Strings.Diag_PerAppThreshold, value);

    /// <summary>How long the application has stayed at or above <paramref name="threshold"/> up to its latest sample.</summary>
    internal static SustainedSpan? SustainedApp(IReadOnlyList<(DateTimeOffset Time, AppSample? App)> series, double threshold)
    {
        if (series.Count == 0 || series[^1].App is not { } last || last.CpuPercent < threshold)
        {
            return null;
        }

        var since = series[^1].Time;
        var values = new List<double>();
        var previous = since;
        for (var i = series.Count - 1; i >= 0; i--)
        {
            var (time, app) = series[i];
            if (previous - time > SystemUsageAggregator.MaxSampleInterval || app is null || app.CpuPercent < threshold)
            {
                break;
            }

            since = time;
            previous = time;
            values.Add(app.CpuPercent);
        }

        return new SustainedSpan(since, series[^1].Time, values.Average(), values.Max(), values.Count);
    }
}
