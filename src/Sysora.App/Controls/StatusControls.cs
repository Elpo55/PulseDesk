using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sysora.App.Converters;
using Sysora.Core.Monitoring;

namespace Sysora.App.Controls;

/// <summary>Status icon (check, warning, error, unknown) colored from the theme palette.</summary>
public sealed partial class HealthGlyph : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(HealthStatus), typeof(HealthGlyph), new PropertyMetadata(HealthStatus.Unknown, OnChanged));

    private readonly FontIcon _icon = new() { FontSize = 16 };

    public HealthGlyph()
    {
        Content = _icon;
        ActualThemeChanged += (_, _) => Update();
        Update();
    }

    public HealthStatus Status
    {
        get => (HealthStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public double GlyphSize
    {
        get => _icon.FontSize;
        set => _icon.FontSize = value;
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((HealthGlyph)d).Update();

    private void Update()
    {
        _icon.Glyph = HealthStatusToGlyphConverter.ToGlyph(Status);
        _icon.Foreground = ThemeBrushes.Get(ThemeBrushes.StatusKey(Status), ActualTheme);
        AutomationProperties.SetName(this, Status.ToString());
    }
}

/// <summary>
/// Usage bar colored by severity: the accent color while normal, then the warning or critical color
/// when the value crosses the user's thresholds.
/// </summary>
public sealed partial class LevelProgressBar : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LevelProgressBar), new PropertyMetadata(0d, OnChanged));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(HealthStatus), typeof(LevelProgressBar), new PropertyMetadata(HealthStatus.Normal, OnChanged));

    public static readonly DependencyProperty NormalBrushKeyProperty = DependencyProperty.Register(
        nameof(NormalBrushKey), typeof(string), typeof(LevelProgressBar), new PropertyMetadata("DiskAccentBrush", OnChanged));

    private readonly UsageBar _bar;

    public LevelProgressBar()
    {
        _bar = new UsageBar { Height = 6 };
        Content = _bar;
        ActualThemeChanged += (_, _) => Update();
        Update();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public HealthStatus Level
    {
        get => (HealthStatus)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>Theme brush used while the level is normal.</summary>
    public string NormalBrushKey
    {
        get => (string)GetValue(NormalBrushKeyProperty);
        set => SetValue(NormalBrushKeyProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((LevelProgressBar)d).Update();

    private void Update()
    {
        _bar.Value = Math.Clamp(Value, 0, 100);
        var key = Level is HealthStatus.Warning or HealthStatus.Critical ? ThemeBrushes.StatusKey(Level) : NormalBrushKey;
        _bar.Foreground = ThemeBrushes.Get(key, ActualTheme);
    }
}

/// <summary>
/// A settings line: icon, title and description on the left, the control on the right
/// (the layout of Windows 11 Settings).
/// </summary>
public sealed partial class SettingRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public SettingRow()
    {
        DefaultStyleKey = typeof(SettingRow);
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
}
