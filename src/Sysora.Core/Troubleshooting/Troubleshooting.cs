using System.Globalization;
using System.Text.Json.Serialization;
using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Models;

namespace Sysora.Core.Troubleshooting;

/// <summary>Why an investigation ended.</summary>
public enum TroubleshootingEndReason
{
    /// <summary>The planned duration elapsed.</summary>
    Completed,

    /// <summary>The user stopped it.</summary>
    StoppedByUser,

    /// <summary>Sysora exited during the investigation: the report covers what was measured until then.</summary>
    SysoraClosed,
}

/// <summary>Average and peak of one metric over an investigation.</summary>
/// <param name="Name">"CPU usage"…</param>
/// <param name="Average">Formatted average.</param>
/// <param name="Peak">Formatted peak.</param>
/// <param name="AverageValue">Raw average (percent, bytes or bits per second).</param>
/// <param name="PeakValue">Raw peak.</param>
/// <param name="Unit">"%", "bytes" or "bit/s".</param>
/// <param name="Samples">Measurements.</param>
public sealed record TroubleshootingMetric(string Name, string Average, string Peak, double AverageValue, double PeakValue, string Unit, int Samples);

/// <summary>One application during an investigation.</summary>
/// <param name="Key">Application identity key.</param>
/// <param name="Name">Image name.</param>
/// <param name="CpuAverage">Average share of total CPU while measured among the top applications.</param>
/// <param name="CpuPeak">Highest share of total CPU.</param>
/// <param name="MemoryPeakBytes">Largest private working set.</param>
/// <param name="MemoryGrowthBytes">Change of its memory between its first and last measurement.</param>
/// <param name="IoAverageBytesPerSecond">Average I/O rate (files, devices and network combined).</param>
/// <param name="BusiestDuringHighCpu">High-CPU moments in which it was the busiest application.</param>
/// <param name="BusiestDuringHighDisk">High-disk moments in which it had the most I/O.</param>
public sealed record TroubleshootingApp(string Key, string Name, double CpuAverage, double CpuPeak, ulong MemoryPeakBytes, long MemoryGrowthBytes, double IoAverageBytesPerSecond, int BusiestDuringHighCpu, int BusiestDuringHighDisk);

/// <summary>Averages over one step of the investigation (for its chart).</summary>
public sealed record TroubleshootingPoint(DateTimeOffset Start, double? Cpu, double? Memory, double? Disk, double? Gpu);

/// <summary>
/// The result of a troubleshooting investigation: what was measured, the anomalies, the events, the applications
/// involved, what the measurements suggest (inferred) and what could not be known. Nothing in it is estimated.
/// </summary>
public sealed record TroubleshootingReport
{
    public required Guid Id { get; init; }

    public required DateTimeOffset Start { get; init; }

    public required DateTimeOffset End { get; init; }

    /// <summary>Duration the user asked for.</summary>
    public required TimeSpan Planned { get; init; }

    public required TroubleshootingEndReason EndReason { get; init; }

    /// <summary>"Investigation complete: 2 anomalies found".</summary>
    public required string Headline { get; init; }

    /// <summary>A few sentences summing up the findings.</summary>
    public required string Summary { get; init; }

    public int SampleCount { get; init; }

    public IReadOnlyList<TroubleshootingMetric> Metrics { get; init; } = [];

    /// <summary>What was measured as abnormal (observed).</summary>
    public IReadOnlyList<Finding> Anomalies { get; init; } = [];

    /// <summary>Events recorded during the investigation.</summary>
    public IReadOnlyList<SystemEvent> Events { get; init; } = [];

    /// <summary>Applications that used the most resources, busiest first.</summary>
    public IReadOnlyList<TroubleshootingApp> Applications { get; init; } = [];

    /// <summary>Coincidences between anomalies and applications or events (inferred).</summary>
    public IReadOnlyList<Finding> Correlations { get; init; } = [];

    /// <summary>The most likely explanations, with their confidence (inferred).</summary>
    public IReadOnlyList<Finding> Likely { get; init; } = [];

    /// <summary>What could not be known.</summary>
    public IReadOnlyList<Finding> Unknowns { get; init; } = [];

    public IReadOnlyList<string> Recommendations { get; init; } = [];

    /// <summary>Averages over time, oldest first.</summary>
    public IReadOnlyList<TroubleshootingPoint> Timeline { get; init; } = [];

    [JsonIgnore]
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Accumulates what an investigation measures, at a constant cost per snapshot: running statistics, a timeline of
/// <see cref="StepLength"/> steps (at most <see cref="MaxPoints"/>), per-application statistics (at most
/// <see cref="MaxApps"/>) and the events (at most <see cref="MaxEvents"/>). Not thread-safe.
/// </summary>
public sealed class TroubleshootingRecorder(DateTimeOffset start)
{
    public static readonly TimeSpan StepLength = TimeSpan.FromSeconds(10);

    public const int MaxPoints = 400;

    public const int MaxApps = 300;

    public const int MaxEvents = 500;

    /// <summary>A snapshot at or above these levels is a high-load moment.</summary>
    public const double HighCpu = 80;

    public const double HighMemory = 85;

    public const double HighDisk = 80;

    private readonly Stat _cpu = new();
    private readonly Stat _memory = new();
    private readonly Stat _memoryBytes = new();
    private readonly Stat _disk = new();
    private readonly Stat _gpu = new();
    private readonly Stat _receive = new();
    private readonly Stat _send = new();
    private readonly Stat _processes = new();
    private readonly Dictionary<string, AppStat> _apps = new(StringComparer.Ordinal);
    private readonly List<TroubleshootingPoint> _points = [];
    private readonly List<SystemEvent> _events = [];
    private readonly Step _step = new();
    private int _highCpu;
    private int _highMemory;
    private int _highDisk;
    private int _detailedSamples;
    private double? _firstMemory;
    private double? _lastMemory;
    private DateTimeOffset? _lastSnapshot;
    private int _droppedEvents;

    public DateTimeOffset Start { get; } = start;

    public int SampleCount { get; private set; }

    public int EventCount => _events.Count + _droppedEvents;

    public int AlertCount { get; private set; }

    public void Add(MetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Timestamp < Start || (_lastSnapshot is { } last && snapshot.Timestamp <= last))
        {
            return;
        }

        _lastSnapshot = snapshot.Timestamp;
        SampleCount++;
        _cpu.Add(snapshot.CpuPercent);
        _memory.Add(snapshot.MemoryPercent);
        _memoryBytes.Add(snapshot.MemoryUsedBytes);
        _disk.Add(snapshot.DiskActivePercent);
        _gpu.Add(snapshot.GpuPercent);
        _receive.Add(snapshot.NetworkReceiveBitsPerSecond);
        _send.Add(snapshot.NetworkSendBitsPerSecond);
        _processes.Add(snapshot.ProcessCount);
        if (snapshot.MemoryUsedBytes is { } used)
        {
            _firstMemory ??= used;
            _lastMemory = used;
        }

        AddStep(snapshot);
        if (snapshot.TopApps.Count == 0)
        {
            return;
        }

        _detailedSamples++;
        AppSample? busiestCpu = null;
        AppSample? busiestIo = null;
        foreach (var app in snapshot.TopApps)
        {
            if (!_apps.TryGetValue(app.Key, out var stat))
            {
                if (_apps.Count >= MaxApps)
                {
                    continue;
                }

                stat = new AppStat(app.Name, app.MemoryBytes);
                _apps[app.Key] = stat;
            }

            stat.Samples++;
            stat.CpuSum += app.CpuPercent;
            stat.CpuPeak = Math.Max(stat.CpuPeak, app.CpuPercent);
            stat.MemoryPeak = Math.Max(stat.MemoryPeak, app.MemoryBytes);
            stat.MemoryLast = app.MemoryBytes;
            stat.IoSum += app.IoBytesPerSecond;
            if (busiestCpu is null || app.CpuPercent > busiestCpu.CpuPercent)
            {
                busiestCpu = app;
            }

            if (busiestIo is null || app.IoBytesPerSecond > busiestIo.IoBytesPerSecond)
            {
                busiestIo = app;
            }
        }

        if (snapshot.CpuPercent >= HighCpu)
        {
            _highCpu++;
            if (busiestCpu is not null && _apps.TryGetValue(busiestCpu.Key, out var cpuStat))
            {
                cpuStat.BusiestCpu++;
            }
        }

        if (snapshot.MemoryPercent >= HighMemory)
        {
            _highMemory++;
        }

        if (snapshot.DiskActivePercent >= HighDisk)
        {
            _highDisk++;
            if (busiestIo is { IoBytesPerSecond: > 0 } && _apps.TryGetValue(busiestIo.Key, out var ioStat))
            {
                ioStat.BusiestIo++;
            }
        }
    }

    public void Add(SystemEvent systemEvent)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);
        if (systemEvent.Kind == SystemEventKind.AlertRaised)
        {
            AlertCount++;
        }

        if (_events.Count < MaxEvents)
        {
            _events.Add(systemEvent);
        }
        else
        {
            _droppedEvents++;
        }
    }

    /// <summary>Builds the report from everything recorded.</summary>
    /// <param name="id">Identifier of the investigation.</param>
    /// <param name="end">When it ended.</param>
    /// <param name="planned">Duration the user asked for.</param>
    /// <param name="reason">Why it ended.</param>
    /// <param name="diagnosis">Diagnosis run at the end, if any.</param>
    /// <param name="snapshot">Latest monitor snapshot (to tell which metrics this PC exposes).</param>
    public TroubleshootingReport Build(Guid id, DateTimeOffset end, TimeSpan planned, TroubleshootingEndReason reason, DiagnosisReport? diagnosis, SystemSnapshot? snapshot)
    {
        FlushStep();
        var metrics = new List<TroubleshootingMetric>();
        void Metric(string name, Stat stat, Func<double, string> format, string unit)
        {
            if (stat.Count > 0)
            {
                metrics.Add(new TroubleshootingMetric(name, format(stat.Average), format(stat.Peak), stat.Average, stat.Peak, unit, stat.Count));
            }
        }

        Metric("CPU usage", _cpu, v => MetricFormatter.Percent(v), "%");
        Metric("Memory in use", _memory, v => MetricFormatter.Percent(v), "%");
        Metric("Memory in use (amount)", _memoryBytes, v => MetricFormatter.Bytes(v), "bytes");
        Metric("Disk active time (busiest disk)", _disk, v => MetricFormatter.Percent(v), "%");
        Metric("GPU usage (busiest adapter)", _gpu, v => MetricFormatter.Percent(v), "%");
        Metric("Download", _receive, v => MetricFormatter.BitsPerSecond(v), "bit/s");
        Metric("Upload", _send, v => MetricFormatter.BitsPerSecond(v), "bit/s");
        Metric("Processes", _processes, v => Math.Round(v).ToString("N0", CultureInfo.CurrentCulture), "count");

        var apps = _apps
            .Select(a => new TroubleshootingApp(
                a.Key,
                a.Value.Name,
                _detailedSamples == 0 ? 0 : a.Value.CpuSum / _detailedSamples,
                a.Value.CpuPeak,
                a.Value.MemoryPeak,
                (long)a.Value.MemoryLast - (long)a.Value.MemoryFirst,
                _detailedSamples == 0 ? 0 : a.Value.IoSum / _detailedSamples,
                a.Value.BusiestCpu,
                a.Value.BusiestIo))
            .OrderByDescending(a => a.CpuAverage + (a.MemoryPeakBytes / (1024.0 * 1024 * 1024)))
            .ToArray();
        var events = _events.OrderBy(e => e.Timestamp).ToArray();

        var anomalies = Anomalies(events, diagnosis);
        var correlations = Correlations(apps, events);
        var likely = correlations
            .Where(c => c.Confidence >= ConfidenceLevel.Medium)
            .Select(c => new Finding("Likely", c.Text, FindingBasis.Inferred) { Confidence = c.Confidence })
            .ToArray();
        var unknowns = Unknowns(snapshot);
        var recommendations = Recommendations(apps, likely.Length > 0);

        var headline = anomalies.Count == 0
            ? "Investigation complete: nothing abnormal measured"
            : $"Investigation complete: {MetricFormatter.Plural(anomalies.Count, "anomaly", "anomalies")} found";
        var summary = SampleCount == 0
            ? "No measurement was taken during the investigation (monitoring paused or the PC asleep)."
            : $"{MetricFormatter.Plural(SampleCount, "measurement")} over {MetricFormatter.DurationPrecise(end - Start)}"
              + (_cpu.Count > 0 ? $": CPU {MetricFormatter.Percent(_cpu.Average)} on average (peak {MetricFormatter.Percent(_cpu.Peak)})" : string.Empty)
              + (_memory.Count > 0 ? $", memory {MetricFormatter.Percent(_memory.Average)} (peak {MetricFormatter.Percent(_memory.Peak)})" : string.Empty)
              + ". "
              + (likely.Length > 0 ? $"Evidence suggests: {likely[0].Text}" : anomalies.Count > 0 ? "No single application clearly explains the anomalies." : "Nothing abnormal was measured.");

        return new TroubleshootingReport
        {
            Id = id,
            Start = Start,
            End = end,
            Planned = planned,
            EndReason = reason,
            Headline = headline,
            Summary = summary,
            SampleCount = SampleCount,
            Metrics = metrics,
            Anomalies = anomalies,
            Events = events,
            Applications = apps.Take(15).ToArray(),
            Correlations = correlations,
            Likely = likely,
            Unknowns = unknowns,
            Recommendations = recommendations,
            Timeline = [.. _points],
        };
    }

    private List<Finding> Anomalies(IReadOnlyList<SystemEvent> events, DiagnosisReport? diagnosis)
    {
        var anomalies = new List<Finding>();
        void Share(int count, string what)
        {
            if (count > 0 && SampleCount > 0)
            {
                anomalies.Add(new Finding("High load", string.Create(CultureInfo.CurrentCulture, $"{what} in {count} of {SampleCount} measurements ({MetricFormatter.Percent(count * 100.0 / SampleCount)} of the time)"), FindingBasis.Observed));
            }
        }

        Share(_highCpu, $"CPU at or above {HighCpu:0}%");
        Share(_highMemory, $"Memory at or above {HighMemory:0}%");
        Share(_highDisk, $"A disk active at or above {HighDisk:0}% of the time");
        foreach (var alert in events.Where(e => e.Kind == SystemEventKind.AlertRaised))
        {
            anomalies.Add(new Finding("Alert", $"{alert.Title} ({Clock(alert.Timestamp)})", FindingBasis.Observed));
        }

        foreach (var gap in events.Where(e => e.Kind == SystemEventKind.DataGap))
        {
            anomalies.Add(new Finding("Interruption", $"{gap.Title} ({Clock(gap.Timestamp)})", FindingBasis.Observed));
        }

        if (diagnosis is not null)
        {
            foreach (var problem in diagnosis.Problems)
            {
                anomalies.Add(new Finding("Diagnosis at the end", $"{problem.Title}: {problem.Description}", FindingBasis.Observed));
            }
        }

        return anomalies;
    }

    private List<Finding> Correlations(IReadOnlyList<TroubleshootingApp> apps, IReadOnlyList<SystemEvent> events)
    {
        var correlations = new List<Finding>();
        if (_highCpu >= 5 && apps.MaxBy(a => a.BusiestDuringHighCpu) is { BusiestDuringHighCpu: > 0 } cpu)
        {
            var share = cpu.BusiestDuringHighCpu * 1.0 / _highCpu;
            if (share >= 0.4)
            {
                correlations.Add(new Finding("High CPU", $"{cpu.Name} was the busiest application in {cpu.BusiestDuringHighCpu} of the {_highCpu} high-CPU moments ({MetricFormatter.Percent(cpu.CpuPeak)} at its peak)", FindingBasis.Inferred)
                {
                    Confidence = share >= 0.8 && _highCpu >= 10 ? ConfidenceLevel.High : share >= 0.6 ? ConfidenceLevel.Medium : ConfidenceLevel.Low,
                });
            }
        }

        if (_highDisk >= 5 && apps.MaxBy(a => a.BusiestDuringHighDisk) is { BusiestDuringHighDisk: > 0 } disk)
        {
            var share = disk.BusiestDuringHighDisk * 1.0 / _highDisk;
            if (share >= 0.4)
            {
                correlations.Add(new Finding("Busy disk", $"{disk.Name} had the most I/O (files, devices and network combined) in {disk.BusiestDuringHighDisk} of the {_highDisk} busy-disk moments", FindingBasis.Inferred)
                {
                    Confidence = share >= 0.8 && _highDisk >= 10 ? ConfidenceLevel.High : share >= 0.6 ? ConfidenceLevel.Medium : ConfidenceLevel.Low,
                });
            }
        }

        const double gigabyte = 1024.0 * 1024 * 1024;
        if (_firstMemory is { } first && _lastMemory is { } last && last - first >= gigabyte)
        {
            var growth = last - first;
            if (apps.MaxBy(a => a.MemoryGrowthBytes) is { MemoryGrowthBytes: > 0 } grower)
            {
                var share = grower.MemoryGrowthBytes / growth;
                correlations.Add(new Finding("Memory growth", $"Memory in use grew {MetricFormatter.Bytes(growth)}; {grower.Name} grew {MetricFormatter.Bytes(grower.MemoryGrowthBytes)} of it", FindingBasis.Inferred)
                {
                    Confidence = share >= 0.7 ? ConfidenceLevel.High : share >= 0.4 ? ConfidenceLevel.Medium : ConfidenceLevel.Low,
                });
            }
        }

        foreach (var game in events.Where(e => e.Kind == SystemEventKind.GameStarted))
        {
            correlations.Add(new Finding("Game", $"{game.Title} at {Clock(game.Timestamp)}: a high load after it is expected", FindingBasis.Observed));
        }

        foreach (var started in events.Where(e => e.Kind == SystemEventKind.AppStarted).Take(5))
        {
            correlations.Add(new Finding("Started during the investigation", $"{started.Title} at {Clock(started.Timestamp)}", FindingBasis.Observed));
        }

        return correlations;
    }

    private List<Finding> Unknowns(SystemSnapshot? snapshot)
    {
        var unknowns = new List<Finding>();
        var hasTemperature = snapshot?.Cpu?.TemperatureCelsius is not null || snapshot?.Gpus?.Any(g => g.TemperatureCelsius is not null) == true;
        if (!hasTemperature)
        {
            unknowns.Add(new Finding("Temperatures", "Not available: Windows has no documented way to read them without a kernel driver.", FindingBasis.Unknown));
        }

        if (_gpu.Count == 0)
        {
            unknowns.Add(new Finding("GPU usage", "Not available on this PC (or GPU monitoring is turned off).", FindingBasis.Unknown));
        }

        if (_detailedSamples == 0)
        {
            unknowns.Add(new Finding("Applications", "Not available: no process measurement during the investigation.", FindingBasis.Unknown));
        }

        unknowns.Add(new Finding("Network per application", "Not available: Windows does not report it without administrator-level event tracing.", FindingBasis.Unknown));
        unknowns.Add(new Finding("Frame rate (FPS)", "Not available: Windows offers no reliable source to other applications.", FindingBasis.Unknown));
        unknowns.Add(new Finding("Inside applications", "What an application does internally (why it uses resources) is not observable from outside it.", FindingBasis.Unknown));
        return unknowns;
    }

    private List<string> Recommendations(IReadOnlyList<TroubleshootingApp> apps, bool hasLikely)
    {
        var recommendations = new List<string>();
        if (_highCpu > 0 && apps.MaxBy(a => a.BusiestDuringHighCpu) is { BusiestDuringHighCpu: > 0 } cpu && hasLikely)
        {
            recommendations.Add($"If the slowdown matched the moments {cpu.Name} was busy, close it or let its task finish, then run another investigation to compare.");
        }

        if (_highMemory > 0)
        {
            recommendations.Add("Memory was nearly full at times: close the applications you do not need. If it happens often, more memory would help.");
        }

        if (_highDisk > 0)
        {
            recommendations.Add("A disk was very busy at times: let updates or copies finish. App Impact shows which applications read and write the most.");
        }

        if (_highCpu == 0 && _highMemory == 0 && _highDisk == 0)
        {
            recommendations.Add("Nothing abnormal was measured. If the problem happened during the investigation, it may not show in these measurements (network latency, frame drops, a device fault); otherwise run another investigation while it happens.");
        }

        recommendations.Add("Export the report to keep it or to share it with someone who helps you.");
        return recommendations;
    }

    private void AddStep(MetricSnapshot snapshot)
    {
        var start = new DateTimeOffset(snapshot.Timestamp.UtcTicks - (snapshot.Timestamp.UtcTicks % StepLength.Ticks), TimeSpan.Zero);
        if (_step.Start is { } current && current != start)
        {
            FlushStep();
        }

        _step.Start = start;
        _step.Cpu.Add(snapshot.CpuPercent);
        _step.Memory.Add(snapshot.MemoryPercent);
        _step.Disk.Add(snapshot.DiskActivePercent);
        _step.Gpu.Add(snapshot.GpuPercent);
    }

    private void FlushStep()
    {
        if (_step.Start is not { } start)
        {
            return;
        }

        if (_points.Count >= MaxPoints)
        {
            // Keep the whole investigation visible: merge neighbors two by two (long investigations get coarser steps).
            var merged = new List<TroubleshootingPoint>(_points.Count / 2 + 1);
            for (var i = 0; i < _points.Count; i += 2)
            {
                var a = _points[i];
                var b = i + 1 < _points.Count ? _points[i + 1] : a;
                merged.Add(new TroubleshootingPoint(a.Start, Mean(a.Cpu, b.Cpu), Mean(a.Memory, b.Memory), Mean(a.Disk, b.Disk), Mean(a.Gpu, b.Gpu)));
            }

            _points.Clear();
            _points.AddRange(merged);
        }

        _points.Add(new TroubleshootingPoint(start, _step.Cpu.Mean, _step.Memory.Mean, _step.Disk.Mean, _step.Gpu.Mean));
        _step.Reset();
    }

    private static double? Mean(double? a, double? b) => (a, b) switch
    {
        ({ } x, { } y) => (x + y) / 2,
        ({ } x, null) => x,
        (null, { } y) => y,
        _ => null,
    };

    private static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);

    private sealed class Stat
    {
        private double _sum;

        public int Count { get; private set; }

        public double Peak { get; private set; }

        public double Average => Count == 0 ? 0 : _sum / Count;

        public double? Mean => Count == 0 ? null : _sum / Count;

        public void Add(double? value)
        {
            if (value is not { } v || !double.IsFinite(v))
            {
                return;
            }

            Peak = Count == 0 ? v : Math.Max(Peak, v);
            _sum += v;
            Count++;
        }

        public void Reset()
        {
            _sum = 0;
            Count = 0;
            Peak = 0;
        }
    }

    private sealed class Step
    {
        public DateTimeOffset? Start { get; set; }

        public Stat Cpu { get; } = new();

        public Stat Memory { get; } = new();

        public Stat Disk { get; } = new();

        public Stat Gpu { get; } = new();

        public void Reset()
        {
            Start = null;
            Cpu.Reset();
            Memory.Reset();
            Disk.Reset();
            Gpu.Reset();
        }
    }

    private sealed class AppStat(string name, ulong firstMemory)
    {
        public string Name { get; } = name;

        public ulong MemoryFirst { get; } = firstMemory;

        public int Samples { get; set; }

        public double CpuSum { get; set; }

        public double CpuPeak { get; set; }

        public ulong MemoryPeak { get; set; }

        public ulong MemoryLast { get; set; }

        public double IoSum { get; set; }

        public int BusiestCpu { get; set; }

        public int BusiestIo { get; set; }
    }
}
