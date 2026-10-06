using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Sysora.App.Controls;

/// <summary>A caption with a value below it ("Base speed" / "3.00 GHz").</summary>
public sealed partial class StatItem : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(StatItem), new PropertyMetadata(string.Empty, OnLabelChanged));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(StatItem), new PropertyMetadata(string.Empty, OnValueChanged));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(StatItem), new PropertyMetadata(null, OnHintChanged));

    private readonly TextBlock _label;
    private readonly TextBlock _value;

    public StatItem()
    {
        _label = new TextBlock { Style = (Style)Application.Current.Resources["LabelTextStyle"] };
        _value = new TextBlock { Style = (Style)Application.Current.Resources["ValueTextStyle"] };
        Content = new StackPanel { Spacing = 2, Children = { _label, _value } };
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Optional explanation shown as a tooltip (e.g. why a value is not available).</summary>
    public string? Hint
    {
        get => (string?)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    // Only the value changes every second: updating it touches a single TextBlock.
    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatItem)d)._value.Text = e.NewValue as string ?? string.Empty;

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (StatItem)d;
        item._label.Text = item.Label;
        AutomationProperties.SetName(item._value, item.Label);
    }

    private static void OnHintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ToolTipService.SetToolTip(d, string.IsNullOrEmpty(e.NewValue as string) ? null : e.NewValue);
}
