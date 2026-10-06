using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Sysora.Core.Metrics;
using Windows.Foundation;

namespace Sysora.App.Controls;

/// <summary>
/// Lightweight real-time line chart. It draws one path per series with no animation and no per-point
/// elements; long windows are reduced to about one point per two pixels with LTTB, so a 30-minute
/// window costs about as much as a 30-second one. Gaps in the data (paused monitoring, sleep) are
/// shown as gaps rather than bridged with a straight line.
/// </summary>
public sealed partial class TimeSeriesChart : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(TimeSeriesData), typeof(TimeSeriesChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(TimeSeriesChart), new PropertyMetadata(null, OnBrushChanged));

    public static readonly DependencyProperty SecondaryStrokeProperty = DependencyProperty.Register(
        nameof(SecondaryStroke), typeof(Brush), typeof(TimeSeriesChart), new PropertyMetadata(null, OnBrushChanged));

    public static readonly DependencyProperty ShowAxisLabelsProperty = DependencyProperty.Register(
        nameof(ShowAxisLabels), typeof(bool), typeof(TimeSeriesChart), new PropertyMetadata(true));

    /// <summary>Time of the cursor line (a <see cref="DateTimeOffset"/>), or null for none.</summary>
    public static readonly DependencyProperty CursorTimeProperty = DependencyProperty.Register(
        nameof(CursorTime), typeof(object), typeof(TimeSeriesChart), new PropertyMetadata(null, OnCursorChanged));

    /// <summary>Times drawn as small ticks along the top edge (events), or null for none.</summary>
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(object), typeof(TimeSeriesChart), new PropertyMetadata(null, OnChanged));

    /// <summary>When true, clicking or dragging on the plot raises <see cref="TimeSelected"/>.</summary>
    public static readonly DependencyProperty IsTimeSelectionEnabledProperty = DependencyProperty.Register(
        nameof(IsTimeSelectionEnabled), typeof(bool), typeof(TimeSeriesChart), new PropertyMetadata(false));

    /// <summary>A gap longer than this many average sampling intervals breaks the line.</summary>
    private const double GapFactor = 4;
    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(3);

    public TimeSeriesChart()
    {
        InitializeComponent();
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

    public Brush? SecondaryStroke
    {
        get => (Brush?)GetValue(SecondaryStrokeProperty);
        set => SetValue(SecondaryStrokeProperty, value);
    }

    public bool ShowAxisLabels
    {
        get => (bool)GetValue(ShowAxisLabelsProperty);
        set => SetValue(ShowAxisLabelsProperty, value);
    }

    public DateTimeOffset? CursorTime
    {
        get => GetValue(CursorTimeProperty) as DateTimeOffset?;
        set => SetValue(CursorTimeProperty, value);
    }

    public IReadOnlyList<DateTimeOffset>? Markers
    {
        get => GetValue(MarkersProperty) as IReadOnlyList<DateTimeOffset>;
        set => SetValue(MarkersProperty, value);
    }

    public bool IsTimeSelectionEnabled
    {
        get => (bool)GetValue(IsTimeSelectionEnabledProperty);
        set => SetValue(IsTimeSelectionEnabledProperty, value);
    }

    /// <summary>Raised when the user clicks or drags on the plot (only when <see cref="IsTimeSelectionEnabled"/>).</summary>
    public event EventHandler<DateTimeOffset>? TimeSelected;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TimeSeriesChart)d).Render();

    private static void OnCursorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TimeSeriesChart)d).UpdateCursor();

    private void OnPlotPointerPressed(object sender, PointerRoutedEventArgs e) => SelectTime(e);

    private void OnPlotPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.IsInContact)
        {
            SelectTime(e);
        }
    }

    private void SelectTime(PointerRoutedEventArgs e)
    {
        if (!IsTimeSelectionEnabled || Data is not { } data || Plot.ActualWidth <= 1)
        {
            return;
        }

        var x = Math.Clamp(e.GetCurrentPoint(Plot).Position.X / Plot.ActualWidth, 0, 1);
        TimeSelected?.Invoke(this, data.End - data.Window + (data.Window * x));
        e.Handled = true;
    }

    /// <summary>Moves the cursor line without redrawing the series (cheap enough for dragging).</summary>
    private void UpdateCursor()
    {
        var data = Data;
        var width = Plot.ActualWidth;
        if (CursorTime is not { } cursor || data is null || width <= 1 || data.Window <= TimeSpan.Zero)
        {
            CursorLine.Visibility = Visibility.Collapsed;
            return;
        }

        var position = (cursor - (data.End - data.Window)) / data.Window;
        if (position is < 0 or > 1)
        {
            CursorLine.Visibility = Visibility.Collapsed;
            return;
        }

        CursorTransform.X = (position * width) - 1;
        CursorLine.Visibility = Visibility.Visible;
    }

    private void RenderMarkers(TimeSeriesData data, double width)
    {
        if (Markers is not { Count: > 0 } markers)
        {
            MarkerPath.Data = null;
            return;
        }

        var geometry = new GeometryGroup();
        var start = data.End - data.Window;
        foreach (var marker in markers)
        {
            var position = (marker - start) / data.Window;
            if (position is < 0 or > 1)
            {
                continue;
            }

            var x = position * width;
            geometry.Children.Add(new LineGeometry { StartPoint = new Point(x, 0), EndPoint = new Point(x, 8) });
        }

        MarkerPath.Data = geometry;
    }

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TimeSeriesChart)d;
        chart.LinePath.Stroke = chart.Stroke;
        chart.SecondaryLinePath.Stroke = chart.SecondaryStroke;
        chart.AreaPath.Fill = chart.Stroke is SolidColorBrush solid ? new SolidColorBrush(solid.Color) { Opacity = 0.18 } : null;
    }

    private void OnPlotSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Plot.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        Render();
    }

    private void Render()
    {
        var data = Data;
        var width = Plot.ActualWidth;
        var height = Plot.ActualHeight;
        if (data is null || width <= 1 || height <= 1 || data.Window <= TimeSpan.Zero)
        {
            AreaPath.Data = LinePath.Data = SecondaryLinePath.Data = MarkerPath.Data = null;
            UpdateCursor();
            return;
        }

        MaximumLabel.Text = data.MaximumLabel;
        WindowLabel.Text = data.WindowLabel;

        var maxPoints = Math.Max(16, (int)(width / 2));
        var primary = BuildFigures(data.Primary, data, width, height, maxPoints);
        LinePath.Data = ToGeometry(primary, closed: false, height);
        AreaPath.Data = ToGeometry(primary, closed: true, height);
        SecondaryLinePath.Data = data.Secondary is { Count: > 0 } secondary
            ? ToGeometry(BuildFigures(secondary, data, width, height, maxPoints), closed: false, height)
            : null;
        RenderMarkers(data, width);
        UpdateCursor();
    }

    /// <summary>Converts samples to screen points, split into runs wherever the data has a gap.</summary>
    private static List<List<Point>> BuildFigures(IReadOnlyList<MetricSample> samples, TimeSeriesData data, double width, double height, int maxPoints)
    {
        var figures = new List<List<Point>>();
        if (samples.Count == 0)
        {
            return figures;
        }

        var points = samples.Count > maxPoints
            ? Downsampler.Lttb(samples is MetricSample[] array ? array : samples.ToArray(), maxPoints)
            : samples as MetricSample[] ?? samples.ToArray();

        var start = data.End - data.Window;
        var maximum = data.Maximum > 0 ? data.Maximum : 1;
        var averageInterval = samples.Count > 1
            ? (samples[^1].Timestamp - samples[0].Timestamp) / (samples.Count - 1)
            : TimeSpan.Zero;
        var gap = TimeSpan.FromTicks(Math.Max((long)(averageInterval.Ticks * GapFactor), MinimumGap.Ticks));

        List<Point>? current = null;
        DateTimeOffset? previous = null;
        foreach (var sample in points)
        {
            if (current is null || (previous is { } p && sample.Timestamp - p > gap))
            {
                current = [];
                figures.Add(current);
            }

            var x = (sample.Timestamp - start) / data.Window * width;
            var y = height - (Math.Clamp(sample.Value / maximum, 0, 1) * (height - 2)) - 1;
            current.Add(new Point(x, y));
            previous = sample.Timestamp;
        }

        return figures;
    }

    private static PathGeometry ToGeometry(List<List<Point>> figures, bool closed, double height)
    {
        var geometry = new PathGeometry();
        foreach (var run in figures)
        {
            if (run.Count == 0)
            {
                continue;
            }

            var figure = new PathFigure
            {
                StartPoint = closed ? new Point(run[0].X, height) : run[0],
                IsClosed = closed,
                IsFilled = closed,
            };

            var segment = new PolyLineSegment();
            foreach (var point in run)
            {
                segment.Points.Add(point);
            }

            if (closed)
            {
                segment.Points.Add(new Point(run[^1].X, height));
            }

            figure.Segments.Add(segment);
            geometry.Figures.Add(figure);
        }

        return geometry;
    }
}
