using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace PulseDesk.App.Controls;

/// <summary>
/// A small pill with a colored dot and a label ("High", "Warning"...). The color comes from a theme brush key
/// chosen by the view model (view models never handle brushes themselves).
/// </summary>
public sealed partial class LevelBadge : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LevelBadge), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty BrushKeyProperty = DependencyProperty.Register(
        nameof(BrushKey), typeof(string), typeof(LevelBadge), new PropertyMetadata("StatusUnknownBrush", OnChanged));

    private readonly Ellipse _dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _border;

    public LevelBadge()
    {
        _label.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _border = new Border
        {
            Padding = new Thickness(8, 2, 10, 3),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _dot, _label } },
        };
        Content = _border;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        ActualThemeChanged += (_, _) => Update();
        Update();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Key of a brush in <c>Themes/Colors.xaml</c>, e.g. "LevelHighBrush".</summary>
    public string BrushKey
    {
        get => (string)GetValue(BrushKeyProperty);
        set => SetValue(BrushKeyProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((LevelBadge)d).Update();

    private void Update()
    {
        _label.Text = Text;
        _dot.Fill = ThemeBrushes.Get(BrushKey, ActualTheme);
        AutomationProperties.SetName(this, Text);
    }
}

/// <summary>A Segoe Fluent icon colored with a theme brush chosen by the view model.</summary>
public sealed partial class StatusIcon : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(StatusIcon), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty BrushKeyProperty = DependencyProperty.Register(
        nameof(BrushKey), typeof(string), typeof(StatusIcon), new PropertyMetadata("StatusUnknownBrush", OnChanged));

    private readonly FontIcon _icon = new() { FontSize = 16 };

    public StatusIcon()
    {
        Content = _icon;
        IsTabStop = false;
        ActualThemeChanged += (_, _) => Update();
        Update();
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string BrushKey
    {
        get => (string)GetValue(BrushKeyProperty);
        set => SetValue(BrushKeyProperty, value);
    }

    public double GlyphSize
    {
        get => _icon.FontSize;
        set => _icon.FontSize = value;
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((StatusIcon)d).Update();

    private void Update()
    {
        _icon.Glyph = Glyph;
        _icon.Foreground = ThemeBrushes.Get(BrushKey, ActualTheme);
    }
}
