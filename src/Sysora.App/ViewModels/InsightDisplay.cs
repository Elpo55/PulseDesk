using System.Globalization;
using Sysora.Core.Analysis;
using Sysora.Core.Formatting;
using Sysora.Core.Metrics;
using Sysora.Localization;

namespace Sysora.App.ViewModels;

/// <summary>Shared wording, glyphs and theme brush keys for analysis results.</summary>
internal static class InsightDisplay
{
    public const string InfoGlyph = "";
    public const string WarningGlyph = "";
    public const string CriticalGlyph = "";
    public const string NormalGlyph = "";
    public const string RisingGlyph = "";
    public const string FallingGlyph = "";
    public const string StableGlyph = "";

    public static string Text(ImpactLevel level) => level switch
    {
        ImpactLevel.VeryHigh => ImpactLevelText.Label(ImpactLevel.VeryHigh),
        ImpactLevel.High => ImpactLevelText.Label(ImpactLevel.High),
        ImpactLevel.Moderate => ImpactLevelText.Label(ImpactLevel.Moderate),
        _ => ImpactLevelText.Label(ImpactLevel.Low),
    };

    public static string BrushKey(ImpactLevel level) => level switch
    {
        ImpactLevel.VeryHigh => "LevelVeryHighBrush",
        ImpactLevel.High => "LevelHighBrush",
        ImpactLevel.Moderate => "LevelModerateBrush",
        _ => "LevelLowBrush",
    };

    public static string Text(ConfidenceLevel confidence) => confidence switch
    {
        ConfidenceLevel.High => ConfidenceText.Label(ConfidenceLevel.High),
        ConfidenceLevel.Medium => ConfidenceText.Label(ConfidenceLevel.Medium),
        _ => ConfidenceText.Label(ConfidenceLevel.Low),
    };

    public static string Glyph(TrendDirection direction) => direction switch
    {
        TrendDirection.Rising => RisingGlyph,
        TrendDirection.Falling => FallingGlyph,
        TrendDirection.Stable => StableGlyph,
        _ => string.Empty,
    };

    /// <summary>Short description of a usage trend, e.g. "Memory rising (+34%)".</summary>
    public static string Describe(UsageTrend trend)
    {
        static string Part(bool memory, TrendDirection direction, double? change) => (direction, memory) switch
        {
            (TrendDirection.Rising, true) => change is { } c
                ? Localization.Text.Format(UiStrings.Trend_MemoryRisingBy, MetricFormatter.Percent(c))
                : UiStrings.Trend_MemoryRising,
            (TrendDirection.Rising, false) => change is { } c
                ? Localization.Text.Format(UiStrings.Trend_CpuRisingBy, MetricFormatter.Percent(c))
                : UiStrings.Trend_CpuRising,
            (TrendDirection.Falling, true) => change is { } c
                ? Localization.Text.Format(UiStrings.Trend_MemoryFallingBy, MetricFormatter.Percent(c))
                : UiStrings.Trend_MemoryFalling,
            (TrendDirection.Falling, false) => change is { } c
                ? Localization.Text.Format(UiStrings.Trend_CpuFallingBy, MetricFormatter.Percent(c))
                : UiStrings.Trend_CpuFalling,
            _ => string.Empty,
        };

        var parts = new[] { Part(true, trend.Memory, trend.MemoryChangePercent), Part(false, trend.Cpu, trend.CpuChangePercent) }
            .Where(p => p.Length > 0)
            .ToArray();
        if (parts.Length > 0)
        {
            return string.Join(" · ", parts);
        }

        return trend.Cpu == TrendDirection.Unknown && trend.Memory == TrendDirection.Unknown
            ? UiStrings.Trend_NotEnoughData
            : UiStrings.Trend_Stable;
    }

    /// <summary>The dominant direction of a trend (memory first, as leaks build up there).</summary>
    public static TrendDirection Dominant(UsageTrend trend) =>
        trend.Memory is TrendDirection.Rising or TrendDirection.Falling ? trend.Memory
        : trend.Cpu is TrendDirection.Rising or TrendDirection.Falling ? trend.Cpu
        : trend.Memory == TrendDirection.Stable || trend.Cpu == TrendDirection.Stable ? TrendDirection.Stable
        : TrendDirection.Unknown;

    /// <summary>Formats a local time for timelines: time only today, otherwise date and time.</summary>
    public static string Time(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("g", CultureInfo.CurrentCulture);
    }

    /// <summary>Formats a period, e.g. "Today 09:12 – 11:40".</summary>
    public static string Period(DateTimeOffset from, DateTimeOffset to) => $"{Time(from)} – {Time(to)}";
}

/// <summary>One line of evidence in a details panel.</summary>
/// <param name="Metric">What was measured.</param>
/// <param name="Observed">Value observed.</param>
/// <param name="Detail">Reference, period and source, combined.</param>
public sealed record EvidenceItemViewModel(string Metric, string Observed, string Detail)
{
    public bool HasDetail => Detail.Length > 0;

    public static EvidenceItemViewModel From(AnalysisEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var details = new List<string>(4);
        if (evidence.Reference is { Length: > 0 } reference)
        {
            details.Add(reference);
        }

        if (evidence.From is { } from && evidence.To is { } to)
        {
            details.Add(Localization.Text.Format(Strings.Report_Period, InsightDisplay.Period(from, to)));
        }

        if (evidence.SampleCount is { } samples)
        {
            details.Add(Localization.Text.Plural(samples, UiStrings.Count_MeasurementN0_One, UiStrings.Count_MeasurementN0_Other));
        }

        if (evidence.Source is { Length: > 0 } source)
        {
            details.Add(Localization.Text.Format(UiStrings.Evidence_Source, source));
        }

        return new EvidenceItemViewModel(evidence.Metric, evidence.Observed, string.Join(Environment.NewLine, details));
    }
}
