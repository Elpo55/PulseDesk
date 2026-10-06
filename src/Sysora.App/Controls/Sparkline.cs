using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sysora.Core.Metrics;
using Windows.Foundation;

namespace Sysora.App.Controls;

/// <summary>
/// Mini-graph for dashboard tiles: one line and a faint area, no axes. Reuses <see cref="TimeSeriesData"/> and
/// is reduced to about one point per two pixels, so a tile costs a single small path per update.
/// </summary>
public sealed partial class Sparkline : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(TimeSeriesData), typeof(Sparkline), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new PropertyMetadata(null, OnBrushChanged));

    private readonly Microsoft.UI.Xaml.Shapes.Path _line = new() { StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
    private readonly Microsoft.UI.Xaml.Shapes.Path _area = new() { IsHitTestVisible = false };
    private readonly Grid _root = new();

    public Sparkline()
    {
        _root.Children.Add(_area);
        _root.Children.Add(_line);
        Content = _root;
        IsHitTestVisible = false;
        IsTabStop = false;
        SizeChanged += (_, e) =>
        {
            _root.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            Render();
        };
    }

    public TimeSeriesData? Data
    {
        get => (TimeSeriesData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Sparkline)d).Render();

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var sparkline = (Sparkline)d;
        sparkline._line.Stroke = sparkline.Stroke;
        sparkline._area.Fill = sparkline.Stroke is SolidColorBrush solid ? new SolidColorBrush(solid.Color) { Opacity = 0.15 } : null;
    }

    private void Render()
    {
        var data = Data;
        var width = ActualWidth;
        var height = ActualHeight;
        if (data is null || data.Primary.Count < 2 || width <= 1 || height <= 1 || data.Window <= TimeSpan.Zero)
        {
            _line.Data = _area.Data = null;
            return;
        }

        var samples = data.Primary as MetricSample[] ?? data.Primary.ToArray();
        var points = Downsampler.Lttb(samples, Math.Max(8, (int)(width / 2)));
        var start = data.End - data.Window;
        var maximum = data.Maximum > 0 ? data.Maximum : 1;

        var line = new PolyLineSegment();
        var area = new PolyLineSegment();
        Point? first = null;
        Point last = default;
        foreach (var sample in points)
        {
            var x = Math.Clamp((sample.Timestamp - start) / data.Window, 0, 1) * width;
            var y = height - (Math.Clamp(sample.Value / maximum, 0, 1) * (height - 2)) - 1;
            var point = new Point(x, y);
            first ??= point;
            last = point;
            line.Points.Add(point);
            area.Points.Add(point);
        }

        area.Points.Add(new Point(last.X, height));
        _line.Data = new PathGeometry { Figures = { new PathFigure { StartPoint = first!.Value, Segments = { line } } } };
        _area.Data = new PathGeometry
        {
            Figures = { new PathFigure { StartPoint = new Point(first.Value.X, height), IsClosed = true, IsFilled = true, Segments = { area } } },
        };
    }
}
