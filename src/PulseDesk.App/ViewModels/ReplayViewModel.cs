using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PulseDesk.App.Controls;
using PulseDesk.App.Services;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.History;
using PulseDesk.Core.Metrics;
using PulseDesk.Core.Models;

namespace PulseDesk.App.ViewModels;

/// <summary>A period offered by Replay.</summary>
public sealed record ReplayRangeOption(TimeSpan Length, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Performance Replay: "what happened in the last minutes?". Charts of CPU, memory, disk, network and GPU over a
/// period, a cursor to read the values at any moment, events on the timeline, and an explanation of what happened.
/// </summary>
public sealed partial class ReplayViewModel : PageViewModel
{
    private const double SliderMaximum = 1000;

    private readonly ReplayService _replay;
    private readonly ILogger<ReplayViewModel> _logger;
    private ReplayData? _data;
    private DateTimeOffset? _end;
    private DateTimeOffset? _cursor;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _appsLoad;
    private long _lastLoad;
    private bool _settingSlider;

    public ReplayViewModel(UiMetricsHub hub, ReplayService replay, ILogger<ReplayViewModel> logger)
        : base(hub)
    {
        _replay = replay;
        _logger = logger;
        Ranges =
        [
            new(TimeSpan.FromMinutes(1), "1 minute"),
            new(TimeSpan.FromMinutes(5), "5 minutes"),
            new(TimeSpan.FromMinutes(15), "15 minutes"),
            new(TimeSpan.FromHours(1), "1 hour"),
            new(TimeSpan.FromHours(6), "6 hours"),
            new(TimeSpan.FromHours(24), "24 hours"),
        ];
        Range = Ranges[2];
        IsLive = true;
        Summary = SourceText = PositionText = CursorTimeText = string.Empty;
        CursorCpu = CursorMemory = CursorDisk = CursorNetwork = CursorGpu = CursorProcesses = MetricFormatter.Pending;
    }

    public IReadOnlyList<ReplayRangeOption> Ranges { get; }

    public ObservableCollection<MomentItemViewModel> Moments { get; } = [];

    public ObservableCollection<string> CursorApps { get; } = [];

    public ObservableCollection<string> CursorEvents { get; } = [];

    [ObservableProperty]
    public partial ReplayRangeOption Range { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    public partial bool IsLive { get; set; }

    public bool IsPaused => !IsLive;

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial string SourceText { get; set; }

    [ObservableProperty]
    public partial string PositionText { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? CpuChart { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? MemoryChart { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? DiskChart { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? NetworkChart { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? GpuChart { get; set; }

    [ObservableProperty]
    public partial bool HasGpu { get; set; }

    [ObservableProperty]
    public partial bool HasNetwork { get; set; }

    [ObservableProperty]
    public partial bool HasDisk { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<DateTimeOffset>? EventTimes { get; set; }

    /// <summary>Cursor shown on the charts (null while live).</summary>
    [ObservableProperty]
    public partial DateTimeOffset? CursorTime { get; set; }

    [ObservableProperty]
    public partial double SliderPosition { get; set; }

    public double SliderMaximumValue => SliderMaximum;

    [ObservableProperty]
    public partial string CursorTimeText { get; set; }

    [ObservableProperty]
    public partial string CursorCpu { get; set; }

    [ObservableProperty]
    public partial string CursorMemory { get; set; }

    [ObservableProperty]
    public partial string CursorDisk { get; set; }

    [ObservableProperty]
    public partial string CursorNetwork { get; set; }

    [ObservableProperty]
    public partial string CursorGpu { get; set; }

    [ObservableProperty]
    public partial string CursorProcesses { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    partial void OnRangeChanged(ReplayRangeOption value)
    {
        if (value is not null)
        {
            _ = LoadAsync();
        }
    }

    partial void OnSliderPositionChanged(double value)
    {
        if (_settingSlider || _data is null)
        {
            return;
        }

        SetCursor(_data.From + ((_data.To - _data.From) * (value / SliderMaximum)));
    }

    /// <summary>Moves the cursor to a time (chart click, slider, moment); freezes the period.</summary>
    public void SetCursor(DateTimeOffset time)
    {
        if (_data is null)
        {
            return;
        }

        if (IsLive)
        {
            IsLive = false;
            _end = _data.To;
        }

        _cursor = time < _data.From ? _data.From : time > _data.To ? _data.To : time;
        ShowCursor();
    }

    [RelayCommand]
    private void BackToNow()
    {
        IsLive = true;
        _end = null;
        _cursor = null;
        _ = LoadAsync();
    }

    [RelayCommand]
    private void ZoomIn() => Zoom(-1);

    [RelayCommand]
    private void ZoomOut() => Zoom(+1);

    protected override void OnActivated() => _ = LoadAsync();

    protected override void OnDeactivated()
    {
        _load?.Cancel();
        _appsLoad?.Cancel();
    }

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        if (!IsLive || (updated & MetricKind.Cpu) == 0)
        {
            return;
        }

        // Detailed periods follow live data; per-minute periods only change once a minute.
        var interval = _data?.IsDetailed == false ? 60_000 : Range.Length <= TimeSpan.FromMinutes(5) ? 1_000 : 2_000;
        if (Environment.TickCount64 - _lastLoad >= interval)
        {
            _ = LoadAsync();
        }
    }

    private void Zoom(int direction)
    {
        var index = Ranges.ToList().IndexOf(Range) + direction;
        if (index < 0 || index >= Ranges.Count)
        {
            return;
        }

        var next = Ranges[index];
        if (_cursor is { } center && !IsLive)
        {
            // Keep the moment being examined in the middle of the new period, without going into the future.
            var end = center + (next.Length / 2);
            var latest = DateTimeOffset.UtcNow;
            _end = end > latest ? latest : end;
        }

        Range = next;
    }

    private async Task LoadAsync()
    {
        if (!IsActive || Range is null)
        {
            return;
        }

        _load?.Cancel();
        _load?.Dispose();
        var load = _load = new CancellationTokenSource();
        _lastLoad = Environment.TickCount64;
        try
        {
            var data = await _replay.LoadAsync(Range.Length, IsLive ? null : _end, load.Token);
            if (!load.IsCancellationRequested)
            {
                Apply(data);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer load replaced this one.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Replay data could not be loaded.");
            Summary = "Replay data could not be loaded. See the log for details.";
        }
    }

    private void Apply(ReplayData data)
    {
        _data = data;
        var points = data.Points;
        IsEmpty = points.Count == 0;
        var window = data.To - data.From;
        var label = data.IsDetailed ? Range.Label : $"{Range.Label} (per minute)";

        CpuChart = Percent(points, s => s.CpuPercent, data, label);
        MemoryChart = Percent(points, s => s.MemoryPercent, data, label);
        HasDisk = points.Any(p => p.DiskActivePercent is not null);
        DiskChart = HasDisk ? Percent(points, s => s.DiskActivePercent, data, label) : null;
        HasGpu = points.Any(p => p.GpuPercent is not null);
        GpuChart = HasGpu ? Percent(points, s => s.GpuPercent, data, label) : null;
        HasNetwork = points.Any(p => p.NetworkReceiveBitsPerSecond is not null);
        if (HasNetwork)
        {
            var receive = Samples(points, s => s.NetworkReceiveBitsPerSecond);
            var send = Samples(points, s => s.NetworkSendBitsPerSecond);
            var peak = receive.Concat(send).Select(s => s.Value).DefaultIfEmpty(0).Max();
            var maximum = ChartScale.NiceMaximum(peak * 1.1, minimum: 100_000);
            NetworkChart = new TimeSeriesData(receive, send, data.To, window, maximum, MetricFormatter.BitsPerSecond(maximum), label);
        }
        else
        {
            NetworkChart = null;
        }

        EventTimes = data.Events.Select(e => e.Timestamp).ToArray();
        Summary = data.Story.Summary;
        SourceText = data.IsDetailed
            ? $"{data.Source} Click a chart or move the slider to read the values at a given moment."
            : $"{data.Source} Older than {MetricFormatter.DurationCompact(_replay.DetailedDuration)}: applications are shown per five minutes.";
        var moments = data.Story.Moments.Reverse().ToList();
        CollectionSync.Resize(Moments, moments.Count, _ => new MomentItemViewModel(m => SetCursor(m.Time)), (item, i) => item.Set(moments[i]));

        if (_cursor is { } cursor && (cursor < data.From || cursor > data.To))
        {
            _cursor = cursor < data.From ? data.From : data.To;
        }

        ShowCursor();
    }

    private void ShowCursor()
    {
        if (_data is not { } data)
        {
            return;
        }

        CursorTime = IsLive ? null : _cursor;
        PositionText = IsLive
            ? $"Live · {InsightDisplay.Period(data.From, data.To)}"
            : $"Paused · {InsightDisplay.Period(data.From, data.To)}";
        _settingSlider = true;
        try
        {
            var span = (data.To - data.From).TotalMilliseconds;
            SliderPosition = IsLive || _cursor is not { } c || span <= 0 ? SliderMaximum : (c - data.From).TotalMilliseconds / span * SliderMaximum;
        }
        finally
        {
            _settingSlider = false;
        }

        var time = IsLive ? data.To : _cursor ?? data.To;
        var point = Nearest(data.Points, time);
        CursorTimeText = point is null
            ? "No measurement at this time"
            : IsLive ? $"Now ({point.Timestamp.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)})" : point.Timestamp.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
        CursorCpu = Value(point?.CpuPercent, v => MetricFormatter.Percent(v));
        CursorMemory = point?.MemoryPercent is { } memory
            ? point.MemoryUsedBytes is { } used ? $"{MetricFormatter.Percent(memory)} ({MetricFormatter.Bytes(used)})" : MetricFormatter.Percent(memory)
            : MetricFormatter.NotAvailable;
        CursorDisk = point?.DiskActivePercent is { } disk
            ? $"{MetricFormatter.Percent(disk)} active{(point.DiskActiveDrive is { } drive ? $" ({drive})" : string.Empty)}"
            : MetricFormatter.NotAvailable;
        CursorNetwork = point?.NetworkReceiveBitsPerSecond is { } receive
            ? $"↓ {MetricFormatter.BitsPerSecond(receive)} · ↑ {MetricFormatter.BitsPerSecond(point.NetworkSendBitsPerSecond)}"
            : MetricFormatter.NotAvailable;
        CursorGpu = Value(point?.GpuPercent, v => MetricFormatter.Percent(v));
        CursorProcesses = point?.ProcessCount is { } count ? count.ToString(CultureInfo.CurrentCulture) : MetricFormatter.NotAvailable;

        var tolerance = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, (data.To - data.From).Ticks / 50));
        var near = data.Events.Where(e => (e.Timestamp - time).Duration() <= tolerance)
            .Select(e => $"{e.Timestamp.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)} — {e.Title}")
            .ToList();
        Replace(CursorEvents, near);

        if (point is not null && point.TopApps.Count > 0)
        {
            Replace(CursorApps, DescribeApps(point.TopApps));
        }
        else if (!data.IsDetailed && point is not null)
        {
            _ = LoadAppsAroundAsync(point.Timestamp);
        }
        else
        {
            Replace(CursorApps, [point is null ? string.Empty : "Application data not available for this moment."]);
        }
    }

    private async Task LoadAppsAroundAsync(DateTimeOffset time)
    {
        _appsLoad?.Cancel();
        _appsLoad?.Dispose();
        var load = _appsLoad = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, load.Token);
            var apps = await _replay.GetAppsAroundAsync(time, load.Token);
            if (load.IsCancellationRequested)
            {
                return;
            }

            var lines = apps
                .OrderByDescending(a => a.CpuAverage * a.Presence)
                .Take(6)
                .Select(a => $"{a.Identity.Name} — {MetricFormatter.Percent(a.CpuAverage, 1)} CPU, {MetricFormatter.Bytes(a.MemoryAverageBytes)} (5-minute average)")
                .ToList();
            Replace(CursorApps, lines.Count > 0 ? lines : ["No application recorded for this five-minute period."]);
        }
        catch (OperationCanceledException)
        {
            // The cursor moved again.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Applications around the cursor could not be loaded.");
        }
    }

    private static IReadOnlyList<string> DescribeApps(IReadOnlyList<AppSample> apps) =>
        apps.OrderByDescending(a => a.CpuPercent)
            .ThenByDescending(a => a.MemoryBytes)
            .Take(6)
            .Select(a => $"{a.Name}{(a.InstanceCount > 1 ? $" ×{a.InstanceCount}" : string.Empty)} — {MetricFormatter.Percent(a.CpuPercent, 1)} CPU, {MetricFormatter.Bytes(a.MemoryBytes)}")
            .ToArray();

    private static MetricSnapshot? Nearest(IReadOnlyList<MetricSnapshot> points, DateTimeOffset time)
    {
        if (points.Count == 0)
        {
            return null;
        }

        int low = 0, high = points.Count - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (points[mid].Timestamp < time)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low > 0 && (time - points[low - 1].Timestamp) < (points[low].Timestamp - time) ? points[low - 1] : points[low];
    }

    private static TimeSeriesData Percent(IReadOnlyList<MetricSnapshot> points, Func<MetricSnapshot, double?> value, ReplayData data, string label) =>
        new(Samples(points, value), null, data.To, data.To - data.From, 100, "100%", label);

    private static MetricSample[] Samples(IReadOnlyList<MetricSnapshot> points, Func<MetricSnapshot, double?> value) =>
        points.Where(p => value(p) is not null).Select(p => new MetricSample(p.Timestamp, value(p)!.Value)).ToArray();

    private static string Value(double? value, Func<double, string> format) =>
        value is { } v ? format(v) : MetricFormatter.NotAvailable;

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> values)
    {
        if (target.SequenceEqual(values))
        {
            return;
        }

        target.Clear();
        foreach (var value in values.Where(v => v.Length > 0))
        {
            target.Add(value);
        }
    }
}

/// <summary>A notable moment in the replayed period.</summary>
public sealed partial class MomentItemViewModel : ObservableObject
{
    private readonly Action<MomentItemViewModel> _select;

    public MomentItemViewModel(Action<MomentItemViewModel> select)
    {
        _select = select;
        TimeText = Text = Glyph = BrushKey = string.Empty;
    }

    public DateTimeOffset Time { get; private set; }

    public string? AppKey { get; private set; }

    [ObservableProperty]
    public partial string TimeText { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial string Glyph { get; set; }

    [ObservableProperty]
    public partial string BrushKey { get; set; }

    [ObservableProperty]
    public partial bool HasApp { get; set; }

    /// <summary>Moves the replay cursor to this moment.</summary>
    [RelayCommand]
    private void Select() => _select(this);

    public void Set(ReplayMoment moment)
    {
        Time = moment.Time;
        AppKey = moment.AppKey;
        HasApp = moment.AppKey is not null;
        TimeText = moment.Time.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        Text = moment.Text;
        (Glyph, BrushKey) = moment.Kind switch
        {
            ReplayMomentKind.Rise => (InsightDisplay.RisingGlyph, "StatusWarningBrush"),
            ReplayMomentKind.Recovery => (InsightDisplay.FallingGlyph, "StatusNormalBrush"),
            _ => (InsightDisplay.InfoGlyph, "StatusInfoBrush"),
        };
    }
}
