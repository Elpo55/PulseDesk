using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Sysora.App.Controls;

/// <summary>
/// Lightweight usage bar (0–100). Unlike <see cref="ProgressBar"/>, updating the value only changes a
/// render transform: no layout pass and no animation, which matters when dozens of bars update every
/// second (per-core usage, top processes).
/// </summary>
[TemplatePart(Name = ScalePartName, Type = typeof(ScaleTransform))]
public sealed partial class UsageBar : Control
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UsageBar), new PropertyMetadata(0d, OnValueChanged));

    private const string ScalePartName = "PART_Scale";
    private ScaleTransform? _scale;

    public UsageBar()
    {
        DefaultStyleKey = typeof(UsageBar);
    }

    /// <summary>Filled share, 0–100 (values outside the range are clamped; NaN shows an empty bar).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _scale = GetTemplateChild(ScalePartName) as ScaleTransform;
        UpdateScale();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new UsageBarAutomationPeer(this);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((UsageBar)d).UpdateScale();

    private void UpdateScale()
    {
        if (_scale is not null)
        {
            _scale.ScaleX = double.IsFinite(Value) ? Math.Clamp(Value, 0, 100) / 100 : 0;
        }
    }

    /// <summary>Exposes the bar to screen readers as a read-only range (like a progress bar).</summary>
    private sealed partial class UsageBarAutomationPeer(UsageBar owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? $"{Math.Round(((UsageBar)Owner).Value)}%" : name;
        }
    }
}
