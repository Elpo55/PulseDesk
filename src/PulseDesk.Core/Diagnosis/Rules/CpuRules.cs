using PulseDesk.Core.Analysis;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.History;

namespace PulseDesk.Core.Diagnosis.Rules;

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
        var minuteEvidence = WindowEvidence("CPU usage (last minute)", minute, MetricSources.Cpu);

        if (span is { } busy && busy.Duration >= sustain)
        {
            var critical = busy.Average >= thresholds.CpuCriticalPercent;
            var unusual = usual is not null && busy.Average > usual.P95;
            var explanation = usual is null
                ? "When the processor stays this busy, applications have to wait for processor time, so the PC can feel slow."
                : unusual
                    ? $"The processor is much busier than usual for this PC ({usual.UsualRange}). Applications have to wait for processor time, so the PC can feel slow."
                    : $"This level is high, but not unusual for this PC ({usual.UsualRange} most of the time).";

            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.Cpu,
                Severity = critical ? DiagnosisSeverity.Critical : DiagnosisSeverity.Warning,
                Title = critical ? "The CPU is saturated" : "High CPU usage",
                Description = $"CPU usage has stayed above {Percent(thresholds.CpuWarningPercent)} for {Duration(busy.Duration)} (average {Percent(busy.Average)}).",
                Metric = "CPU usage",
                ObservedValue = Percent(busy.Average),
                ReferenceValue = UsualText(usual) ?? $"Threshold {Percent(thresholds.CpuWarningPercent)}",
                Duration = busy.Duration,
                Timestamp = latest.Timestamp,
                Explanation = explanation,
                Recommendation = "Open App Impact to see which applications use the processor, and close the ones you don't need right now.",
                Confidence = busy.Duration >= sustain * 2 ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                Evidence = [SpanEvidence("CPU usage", busy, thresholds.CpuWarningPercent, MetricSources.Cpu), BaselineEvidence(context.Baseline, HistoryMetric.Cpu, "CPU usage")],
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
                Title = "Short CPU spike",
                Description = $"The CPU is at {Percent(current)} right now, but only for {Duration(since)} so far.",
                Metric = "CPU usage",
                ObservedValue = Percent(current),
                ReferenceValue = $"Reported when above {Percent(thresholds.CpuWarningPercent)} for {Duration(sustain)}",
                Duration = since,
                Timestamp = latest.Timestamp,
                Explanation = "Short spikes are normal (opening an application, a background task). It becomes a likely cause of slowness only if it lasts.",
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
            Title = "CPU usage normal",
            Description = $"The processor averaged {Percent(minute.Average)} over the last minute (peak {Percent(minute.Peak)}).",
            Metric = "CPU usage",
            ObservedValue = Percent(minute.Average),
            ReferenceValue = UsualText(usual) ?? $"Threshold {Percent(thresholds.CpuWarningPercent)}",
            Timestamp = latest.Timestamp,
            Explanation = "The processor has spare capacity.",
            Confidence = ConfidenceLevel.High,
            Evidence = [minuteEvidence, BaselineEvidence(context.Baseline, HistoryMetric.Cpu, "CPU usage")],
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
        var appEvidence = new AnalysisEvidence($"{top.Name} CPU", Format($"{top.CpuPercent:0.#}% of total CPU now ({MetricFormatter.Plural(top.InstanceCount, "process", "processes")})"))
        {
            Reference = Format($"Total CPU usage now: {total:0}%"),
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
                Title = longRunning ? $"{top.Name} has been using a lot of CPU for several minutes" : $"{top.Name} uses a large share of the CPU",
                Description = $"{top.Name} has used {Percent(span.Average)} of total CPU capacity on average for {Duration(span.Duration)}.",
                Metric = "Application CPU usage",
                ObservedValue = Percent(span.Average),
                ReferenceValue = $"Threshold {Percent(thresholds.ProcessCpuWarningPercent)} per application",
                Duration = span.Duration,
                Timestamp = latest.Timestamp,
                Explanation = "A single application keeping the processor this busy leaves less capacity for everything else. It may be doing legitimate work (a build, an export, a game) or be stuck.",
                Recommendation = $"If you don't need {top.Name} right now, close it or wait for its task to finish. App Impact shows its history.",
                Confidence = longRunning ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                Evidence = [SpanEvidence($"{top.Name} CPU", span, thresholds.ProcessCpuWarningPercent, MetricSources.Processes), appEvidence],
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
                Title = $"{top.Name} is the main CPU consumer",
                Description = Format($"{top.Name} accounts for {top.CpuPercent / total * 100:0}% of the processor load right now ({Percent(top.CpuPercent)} of {Percent(total)})."),
                Metric = "Application CPU usage",
                ObservedValue = Percent(top.CpuPercent),
                ReferenceValue = $"Total CPU {Percent(total)}",
                Timestamp = latest.Timestamp,
                Explanation = "If the processor stays busy, this application is the most likely reason.",
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
            Title = "No application dominates the CPU",
            Description = $"The largest CPU consumer is {top.Name} with {MetricFormatter.Percent(top.CpuPercent, 1)} of total capacity.",
            Metric = "Application CPU usage",
            ObservedValue = MetricFormatter.Percent(top.CpuPercent, 1),
            ReferenceValue = $"Threshold {Percent(thresholds.ProcessCpuWarningPercent)} per application",
            Timestamp = latest.Timestamp,
            Explanation = "No single application is keeping the processor busy.",
            Confidence = ConfidenceLevel.High,
            Evidence = [appEvidence],
            AppKey = top.Key,
        };
    }

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
