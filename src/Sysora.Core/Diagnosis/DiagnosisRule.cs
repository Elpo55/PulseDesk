using System.Globalization;
using Sysora.Core.Analysis;
using Sysora.Core.Formatting;
using Sysora.Core.History;

namespace Sysora.Core.Diagnosis;

/// <summary>
/// A deterministic, explainable check. Each rule looks at the measurements in a <see cref="DiagnosisContext"/>
/// and returns results: a normal result when its area is fine, so the user also sees what was checked.
/// </summary>
/// <remarks>
/// Rules never invent data: when a metric is not available they return nothing (the report lists the metric
/// as not analyzed). Hypotheses are worded as such and get a lower confidence.
/// </remarks>
public abstract class DiagnosisRule
{
    /// <summary>Stable identifier, e.g. "cpu.load".</summary>
    public abstract string Id { get; }

    /// <summary>Evaluates the rule.</summary>
    public abstract IEnumerable<DiagnosisResult> Evaluate(DiagnosisContext context);

    protected static string Percent(double value) => MetricFormatter.Percent(value);

    protected static string Duration(TimeSpan duration) => MetricFormatter.DurationPrecise(duration);

    protected static string Format(FormattableString text) => text.ToString(CultureInfo.CurrentCulture);

    /// <summary>"Usual 35–50% (median 42%, last 7 days)", or null when the baseline is not ready.</summary>
    protected static string? UsualText(MetricBaseline? baseline) =>
        baseline is null ? null : Format($"Usual {baseline.UsualRange} (median {baseline.Median:0}%)");

    /// <summary>Evidence describing the usual range used for a comparison.</summary>
    protected static AnalysisEvidence BaselineEvidence(UsageBaseline baseline, HistoryMetric metric, string name) =>
        baseline.Get(metric) is { } usual
            ? new AnalysisEvidence($"Usual {name}", Format($"{usual.UsualRange} (median {usual.Median:0}%, 95% of minutes below {usual.P95:0}%)"))
            {
                From = baseline.From,
                To = baseline.To,
                SampleCount = usual.Minutes,
                Source = "Per-minute averages of the local history (last 7 days)",
            }
            : new AnalysisEvidence($"Usual {name}", "Not known yet")
            {
                Reference = baseline.Description,
            };

    /// <summary>Evidence for a metric over a period of history snapshots.</summary>
    protected static AnalysisEvidence WindowEvidence(string metric, WindowSummary summary, string source, Func<double, string>? format = null)
    {
        format ??= Percent;
        return new AnalysisEvidence(metric, $"Average {format(summary.Average)}, peak {format(summary.Peak)}")
        {
            From = summary.From,
            To = summary.To,
            SampleCount = summary.Count,
            Source = source,
        };
    }

    /// <summary>Evidence for a sustained period.</summary>
    protected static AnalysisEvidence SpanEvidence(string metric, SustainedSpan span, double threshold, string source, Func<double, string>? format = null)
    {
        format ??= Percent;
        return new AnalysisEvidence(metric, $"At or above {format(threshold)} for {Duration(span.Duration)} (average {format(span.Average)}, peak {format(span.Peak)})")
        {
            Reference = $"Threshold {format(threshold)}",
            From = span.Since,
            To = span.Until,
            SampleCount = span.Samples,
            Source = source,
        };
    }
}
