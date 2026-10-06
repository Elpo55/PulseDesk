using Sysora.Core.Metrics;

namespace Sysora.App.Controls;

/// <summary>
/// Everything <see cref="TimeSeriesChart"/> needs to draw one frame. Immutable: view models publish a new
/// instance per update, so a chart redraws exactly once per update.
/// </summary>
/// <param name="Primary">Main series, ordered by time.</param>
/// <param name="Secondary">Optional second series drawn as a line only (e.g. upload next to download).</param>
/// <param name="End">Time at the right edge of the chart.</param>
/// <param name="Window">Time span covered by the chart.</param>
/// <param name="Maximum">Value at the top of the chart.</param>
/// <param name="MaximumLabel">Label of the top of the chart, e.g. "100%".</param>
/// <param name="WindowLabel">Label of the time span, e.g. "60 seconds".</param>
public sealed record TimeSeriesData(
    IReadOnlyList<MetricSample> Primary,
    IReadOnlyList<MetricSample>? Secondary,
    DateTimeOffset End,
    TimeSpan Window,
    double Maximum,
    string MaximumLabel,
    string WindowLabel);
