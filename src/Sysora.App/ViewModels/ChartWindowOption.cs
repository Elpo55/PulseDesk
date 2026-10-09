using Sysora.App.Controls;
using Sysora.Core.Formatting;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Settings;
using Sysora.Localization;

namespace Sysora.App.ViewModels;

/// <summary>A time span offered by the chart window selector.</summary>
public sealed record ChartWindowOption(int Seconds, string Label)
{
    public static IReadOnlyList<ChartWindowOption> All { get; } =
        SettingsValidator.ChartWindowOptions.Select(s => new ChartWindowOption(s, Describe(s))).ToArray();

    public TimeSpan Window => TimeSpan.FromSeconds(Seconds);

    public static ChartWindowOption FromSeconds(int seconds) =>
        All.FirstOrDefault(o => o.Seconds == seconds) ?? All[1];

    public override string ToString() => Label;

    private static string Describe(int seconds) =>
        seconds < 60
            ? Text.Plural(seconds, UiStrings.Duration_Second_One, UiStrings.Duration_Second_Other)
            : Text.Plural(seconds / 60, Strings.Duration_Minute_One, Strings.Duration_Minute_Other);
}

/// <summary>Builds chart frames from the monitor's history.</summary>
internal static class ChartFactory
{
    /// <summary>A sample just before the window is kept so the line starts at the left edge.</summary>
    private static readonly TimeSpan LeadIn = TimeSpan.FromSeconds(5);

    /// <summary>A 0–100% chart, or null before the first measurement.</summary>
    public static TimeSeriesData? Percent(MetricHistory history, string key, ChartWindowOption window, SystemSnapshot snapshot) =>
        !HasData(snapshot) ? null : new(
            history.GetSamples(key, snapshot.Timestamp - window.Window - LeadIn),
            null,
            snapshot.Timestamp,
            window.Window,
            100,
            MetricFormatter.Percent(100),
            window.Label);

    /// <summary>A throughput chart (bits per second) whose scale adapts to the largest visible value.</summary>
    public static TimeSeriesData? BitRate(MetricHistory history, string primaryKey, string secondaryKey, ChartWindowOption window, SystemSnapshot snapshot)
    {
        if (!HasData(snapshot))
        {
            return null;
        }

        var since = snapshot.Timestamp - window.Window - LeadIn;
        var primary = history.GetSamples(primaryKey, since);
        var secondary = history.GetSamples(secondaryKey, since);
        var peak = primary.Concat(secondary).Select(s => s.Value).DefaultIfEmpty(0).Max();
        var maximum = ChartScale.NiceMaximum(peak * 1.1, minimum: 100_000);
        return new TimeSeriesData(primary, secondary, snapshot.Timestamp, window.Window, maximum, MetricFormatter.BitsPerSecond(maximum), window.Label);
    }

    /// <summary>Average and peak over the window, e.g. "Average 12% · Peak 45%".</summary>
    public static string Summary(MetricHistory history, string key, ChartWindowOption window, SystemSnapshot snapshot)
    {
        var series = history.Get(key);
        if (!HasData(snapshot) || series?.GetStatistics(snapshot.Timestamp - window.Window) is not { } stats)
        {
            return string.Empty;
        }

        var trend = TrendCalculator.Compute(series.GetSamples(snapshot.Timestamp - window.Window));
        var direction = trend.Direction switch
        {
            TrendDirection.Rising => " · " + UiStrings.Chart_Rising,
            TrendDirection.Falling => " · " + UiStrings.Chart_Falling,
            TrendDirection.Stable => " · " + UiStrings.Trend_Stable,
            _ => string.Empty,
        };

        return Text.Format(UiStrings.Chart_Stats, MetricFormatter.Percent(stats.Average), MetricFormatter.Percent(stats.Maximum), direction);
    }

    /// <summary>False until the monitor published its first snapshot (its timestamp is still the default).</summary>
    private static bool HasData(SystemSnapshot snapshot) => snapshot.Timestamp != default;
}
