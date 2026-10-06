using CommunityToolkit.Mvvm.ComponentModel;
using Sysora.Core.Formatting;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;

namespace Sysora.App.ViewModels;

/// <summary>Shared display rules for metrics that may be pending or unavailable.</summary>
internal static class Display
{
    /// <summary>
    /// Formats a metric, or returns "—" while it is still being collected and "Not available" when this
    /// machine cannot provide it. A missing value is never shown as 0.
    /// </summary>
    public static string Format<T>(SystemSnapshot snapshot, MetricKind kind, T? metric, Func<T, string> format)
        where T : class =>
        metric is not null ? format(metric)
        : snapshot.IsUnavailable(kind) ? MetricFormatter.NotAvailable
        : MetricFormatter.Pending;
}

/// <summary>Values of one dashboard tile.</summary>
public sealed partial class MetricTileViewModel : ObservableObject
{
    public MetricTileViewModel()
    {
        Value = MetricFormatter.Pending;
        Detail = string.Empty;
        SecondaryDetail = string.Empty;
        Percent = double.NaN;
    }

    [ObservableProperty]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; }

    [ObservableProperty]
    public partial string SecondaryDetail { get; set; }

    [ObservableProperty]
    public partial double Percent { get; set; }

    public void Set(string value, string detail, string secondaryDetail, double percent)
    {
        Value = value;
        Detail = detail;
        SecondaryDetail = secondaryDetail;
        Percent = percent;
    }
}

/// <summary>One line of the health summary.</summary>
public sealed record HealthItemViewModel(HealthStatus Status, string Summary, string? Detail)
{
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>An application in a "top consumers" list (processes grouped by name).</summary>
public sealed partial class TopProcessItemViewModel : ObservableObject
{
    public TopProcessItemViewModel()
    {
        Name = string.Empty;
        InstancesText = string.Empty;
        PrimaryText = string.Empty;
        SecondaryText = string.Empty;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>"×12" when the application runs several processes.</summary>
    [ObservableProperty]
    public partial string InstancesText { get; set; }

    [ObservableProperty]
    public partial string PrimaryText { get; set; }

    [ObservableProperty]
    public partial string SecondaryText { get; set; }

    /// <summary>Relative weight, 0–100, for the small bar.</summary>
    [ObservableProperty]
    public partial double Percent { get; set; }

    public void Set(ProcessGroup group, string primary, string secondary, double percent)
    {
        Name = group.Name;
        InstancesText = group.InstanceCount > 1 ? $"×{group.InstanceCount}" : string.Empty;
        PrimaryText = primary;
        SecondaryText = secondary;
        Percent = percent;
    }
}

/// <summary>Usage of one logical processor.</summary>
public sealed partial class CoreUsageViewModel(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;
}

/// <summary>Keeps an observable list of item view models in step with fresh data, reusing existing items.</summary>
internal static class CollectionSync
{
    /// <summary>Resizes <paramref name="items"/> to <paramref name="count"/>, then updates each item in place.</summary>
    public static void Resize<T>(IList<T> items, int count, Func<int, T> create, Action<T, int> update)
    {
        while (items.Count > count)
        {
            items.RemoveAt(items.Count - 1);
        }

        while (items.Count < count)
        {
            items.Add(create(items.Count));
        }

        for (var i = 0; i < count; i++)
        {
            update(items[i], i);
        }
    }
}
