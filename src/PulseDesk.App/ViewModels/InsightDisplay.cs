using System.Globalization;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Metrics;

namespace PulseDesk.App.ViewModels;

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
        ImpactLevel.VeryHigh => "Very high",
        ImpactLevel.High => "High",
        ImpactLevel.Moderate => "Moderate",
        _ => "Low",
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
        ConfidenceLevel.High => "High confidence",
        ConfidenceLevel.Medium => "Medium confidence",
        _ => "Low confidence",
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
        static string Part(string name, TrendDirection direction, double? change) => direction switch
        {
            TrendDirection.Rising => change is { } c ? string.Create(CultureInfo.CurrentCulture, $"{name} rising (+{c:0}%)") : $"{name} rising",
            TrendDirection.Falling => change is { } c ? string.Create(CultureInfo.CurrentCulture, $"{name} falling ({c:0}%)") : $"{name} falling",
            _ => string.Empty,
        };

        var parts = new[] { Part("Memory", trend.Memory, trend.MemoryChangePercent), Part("CPU", trend.Cpu, trend.CpuChangePercent) }
            .Where(p => p.Length > 0)
            .ToArray();
        if (parts.Length > 0)
        {
            return string.Join(" · ", parts);
        }

        return trend.Cpu == TrendDirection.Unknown && trend.Memory == TrendDirection.Unknown ? "Not enough data" : "Stable";
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
            details.Add($"Period: {InsightDisplay.Period(from, to)}");
        }

        if (evidence.SampleCount is { } samples)
        {
            details.Add(string.Create(CultureInfo.CurrentCulture, $"{samples:N0} measurements"));
        }

        if (evidence.Source is { Length: > 0 } source)
        {
            details.Add($"Source: {source}");
        }

        return new EvidenceItemViewModel(evidence.Metric, evidence.Observed, string.Join(Environment.NewLine, details));
    }
}
