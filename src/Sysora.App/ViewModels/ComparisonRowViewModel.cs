using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sysora.Core.Analysis;
using Sysora.Core.Formatting;
using Sysora.Localization;

namespace Sysora.App.ViewModels;

/// <summary>One metric of the comparison table: now, and each reference period ("—" with the reason when unknown).</summary>
public sealed partial class ComparisonRowViewModel : ObservableObject
{
    public ComparisonRowViewModel()
    {
        Name = string.Empty;
        Now = LastHour = Today = Yesterday = Last7Days = Last30Days = Usual = ComparisonCell.Unknown;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial ComparisonCell Now { get; set; }

    [ObservableProperty]
    public partial ComparisonCell LastHour { get; set; }

    [ObservableProperty]
    public partial ComparisonCell Today { get; set; }

    [ObservableProperty]
    public partial ComparisonCell Yesterday { get; set; }

    [ObservableProperty]
    public partial ComparisonCell Last7Days { get; set; }

    [ObservableProperty]
    public partial ComparisonCell Last30Days { get; set; }

    [ObservableProperty]
    public partial ComparisonCell Usual { get; set; }

    public void Set(MetricComparison metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        Name = metric.Name;
        Now = metric.Current is { } current
            ? new ComparisonCell(MetricFormatter.Percent(current), Text.Format(UiStrings.Usual_CurrentAverage, UsageComparer.CurrentWindow.TotalMinutes))
            : new ComparisonCell(ComparisonCell.Missing, UiStrings.Usual_NotMeasuredRecently);
        LastHour = Cell(metric.Get(ComparisonPeriod.LastHour));
        Today = Cell(metric.Get(ComparisonPeriod.Today));
        Yesterday = Cell(metric.Get(ComparisonPeriod.Yesterday));
        Last7Days = Cell(metric.Get(ComparisonPeriod.Last7Days));
        Last30Days = Cell(metric.Get(ComparisonPeriod.Last30Days));
        Usual = Cell(metric.Get(ComparisonPeriod.UsualAtThisHour));
    }

    private static ComparisonCell Cell(PeriodAverage period) =>
        period.Average is { } average
            ? new ComparisonCell(
                MetricFormatter.Percent(average),
                Text.Format(UiStrings.Usual_PeriodAverage, UsageComparer.Label(period.Period), MetricFormatter.DurationCompact(TimeSpan.FromHours(period.MonitoredHours)), Text.Plural(period.Days, Strings.Duration_Day_One, Strings.Duration_Day_Other)))
            : new ComparisonCell(ComparisonCell.Missing, period.NotEnoughData ?? UiStrings.Trend_NotEnoughData);
}

/// <summary>A value of the comparison table and where it comes from.</summary>
/// <param name="Text">Value, or a dash when unknown.</param>
/// <param name="Tooltip">Period and amount of data behind the value, or why there is none.</param>
public sealed record ComparisonCell(string Text, string Tooltip)
{
    public const string Missing = "—";

    public static ComparisonCell Unknown { get; } = new(Missing, UiStrings.Trend_NotEnoughData);
}
