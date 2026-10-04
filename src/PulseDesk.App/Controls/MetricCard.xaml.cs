using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PulseDesk.App.Controls;

/// <summary>
/// Dashboard tile: an icon and title, a large value, two lines of detail and an optional usage bar.
/// </summary>
public sealed partial class MetricCard : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(MetricCard), new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty GlyphProperty = Register(nameof(Glyph), typeof(string), string.Empty);
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), typeof(string), string.Empty);
    public static readonly DependencyProperty DetailProperty = Register(nameof(Detail), typeof(string), string.Empty);
    public static readonly DependencyProperty SecondaryDetailProperty = Register(nameof(SecondaryDetail), typeof(string), string.Empty);
    public static readonly DependencyProperty AccentBrushProperty = Register(nameof(AccentBrush), typeof(Brush), null);

    /// <summary>Bar value, 0–100; NaN hides the bar.</summary>
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(MetricCard), new PropertyMetadata(double.NaN, OnPercentChanged));

    public MetricCard()
    {
        InitializeComponent();
        UpdateBar();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public string SecondaryDetail
    {
        get => (string)GetValue(SecondaryDetailProperty);
        set => SetValue(SecondaryDetailProperty, value);
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(MetricCard), new PropertyMetadata(defaultValue));

    // The accessible name is the title; screen readers read the values from the text inside the card.
    // Values are not copied into the name: that would raise an automation event on every update.
    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        AutomationProperties.SetName(d, e.NewValue as string ?? string.Empty);

    private static void OnPercentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MetricCard)d).UpdateBar();

    private void UpdateBar()
    {
        var percent = Percent;
        Bar.Visibility = double.IsFinite(percent) ? Visibility.Visible : Visibility.Collapsed;
        Bar.Value = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
    }
}
