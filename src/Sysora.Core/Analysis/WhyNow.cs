using System.Globalization;
using Sysora.Core.Alerts;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Interfaces;

namespace Sysora.Core.Analysis;

/// <summary>Metrics "Why now?" can explain.</summary>
public enum WhyNowMetric
{
    Cpu,
    Memory,
    Disk,
    Gpu,
    Network,
}

/// <summary>The application whose usage changed the most when the metric rose.</summary>
/// <param name="Name">Image name.</param>
/// <param name="AppKey">Application identity key.</param>
/// <param name="Change">Formatted change, e.g. "+3.9 GB".</param>
/// <param name="Share">Share of the system-wide increase it accounts for (0–1), when comparable.</param>
/// <param name="Confidence">How much the association can be trusted.</param>
/// <param name="WasAbsentBefore">True when it was not among the top applications before the increase.</param>
public sealed record WhyNowContributor(string Name, string AppKey, string Change, double? Share, ConfidenceLevel Confidence, bool WasAbsentBefore);

/// <summary>
/// Answer to "why is this happening now?": when the change started, from what level to what level, for how long,
/// what changed at the same time, and how often it happened before. Every statement is labeled as observed,
/// inferred or unknown; a cause is never stated as certain.
/// </summary>
public sealed record WhyNowExplanation
{
    public required WhyNowMetric Metric { get; init; }

    /// <summary>"Memory usage", "CPU usage"...</summary>
    public required string MetricName { get; init; }

    /// <summary>Time of the latest measurement analyzed.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>True when the metric rose clearly or is high: there is something to explain.</summary>
    public required bool IsSignificant { get; init; }

    /// <summary>"Memory usage increased significantly", or "CPU usage is not unusually high right now".</summary>
    public required string Headline { get; init; }

    /// <summary>One or two sentences: what happened and what evidence suggests.</summary>
    public required string Summary { get; init; }

    /// <summary>When the rise started (within <see cref="WhyNowAnalyzer.BucketLength"/>), when it is in the data.</summary>
    public DateTimeOffset? Started { get; init; }

    /// <summary>True when the metric was already high at the start of the analyzed data.</summary>
    public bool StartedBeforeData { get; init; }

    /// <summary>Level before the rise, formatted ("3.2 GB"), when known.</summary>
    public string? BeforeText { get; init; }

    /// <summary>Current level, formatted.</summary>
    public string? NowText { get; init; }

    public double? Before { get; init; }

    public double? Now { get; init; }

    /// <summary>How long the metric has been elevated (at least this long when it started before the data).</summary>
    public TimeSpan? Duration { get; init; }

    public WhyNowContributor? Contributor { get; init; }

    /// <summary>"Likely contributor: …", "Possible contributor: …", "Cause unknown: …" or "Not available: …".</summary>
    public required string ContributorText { get; init; }

    /// <summary>Events at the same time (a coincidence in time is not proof of cause).</summary>
    public IReadOnlyList<SystemEvent> AssociatedEvents { get; init; } = [];

    /// <summary>Other metrics that rose at the same time, e.g. "Disk activity also rose: 4% → 58%".</summary>
    public IReadOnlyList<string> Simultaneous { get; init; } = [];

    /// <summary>Comparison with the usual range, when the baseline is known.</summary>
    public string? UsualText { get; init; }

    /// <summary>"Similar events: 3 alerts in the last 7 days", or why it is not known.</summary>
    public required string SimilarText { get; init; }

    public int? SimilarCount { get; init; }

    /// <summary>Every statement with its basis.</summary>
    public IReadOnlyList<Finding> Findings { get; init; } = [];

    public IReadOnlyList<AnalysisEvidence> Evidence { get; init; } = [];

    /// <summary>Period of the data analyzed.</summary>
    public DateTimeOffset? From { get; init; }

    public int SampleCount { get; init; }

    /// <summary>Where the data comes from.</summary>
    public string Source { get; init; } = string.Empty;
}

/// <summary>Data analyzed by <see cref="WhyNowAnalyzer"/>.</summary>
public sealed record WhyNowInput
{
    public required WhyNowMetric Metric { get; init; }

    /// <summary>Measurements, oldest first, at any resolution (per-second snapshots and per-minute averages can be mixed).</summary>
    public required IReadOnlyList<MetricSnapshot> Points { get; init; }

    /// <summary>Events of the period.</summary>
    public IReadOnlyList<SystemEvent> Events { get; init; } = [];

    public UsageBaseline Baseline { get; init; } = UsageBaseline.Empty;

    /// <summary>Alerts of the last days, or null when no alert history is available.</summary>
    public IReadOnlyList<Alert>? Alerts { get; init; }

    public string Source { get; init; } = string.Empty;
}

/// <summary>
/// Explains why a metric is high now (pure, deterministic). It finds when the metric left its earlier level, compares
/// the applications before and after that moment, lists what happened at the same time and how often similar
/// problems were recorded. Correlation is reported as such ("Likely contributor", "Associated with"); when no single
/// application accounts for the change, the cause is reported as unknown.
/// </summary>
public static class WhyNowAnalyzer
{
    /// <summary>Measurements are averaged over buckets of this length to smooth noise.</summary>
    public static readonly TimeSpan BucketLength = TimeSpan.FromSeconds(30);

    /// <summary>Applications are compared over this long before the start of the rise.</summary>
    public static readonly TimeSpan ContributorWindow = TimeSpan.FromMinutes(3);

    /// <summary>Similar alerts are counted over this period.</summary>
    public static readonly TimeSpan SimilarWindow = TimeSpan.FromDays(7);

    private static readonly HashSet<SystemEventKind> AssociatedKinds =
    [
        SystemEventKind.AppStarted, SystemEventKind.AppHighCpu, SystemEventKind.AppHighMemory, SystemEventKind.AppExited,
        SystemEventKind.GameStarted, SystemEventKind.GameEnded, SystemEventKind.ConnectivityChanged, SystemEventKind.VolumeAdded,
        SystemEventKind.VolumeRemoved, SystemEventKind.SystemResumed, SystemEventKind.MonitoringResumed, SystemEventKind.AlertRaised,
    ];

    /// <summary>Display name of a metric.</summary>
    public static string Name(WhyNowMetric metric) => metric switch
    {
        WhyNowMetric.Cpu => "CPU usage",
        WhyNowMetric.Memory => "Memory usage",
        WhyNowMetric.Disk => "Disk activity",
        WhyNowMetric.Gpu => "GPU usage",
        _ => "Network traffic",
    };

    public static WhyNowExplanation Explain(WhyNowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var metric = input.Metric;
        var name = Name(metric);
        var points = input.Points;
        var buckets = Buckets(points, metric);
        var time = points.Count > 0 ? points[^1].Timestamp : DateTimeOffset.MinValue;
        var similar = Similar(metric, input.Alerts, time);
        if (buckets.Count < 4)
        {
            return new WhyNowExplanation
            {
                Metric = metric,
                MetricName = name,
                Time = time,
                IsSignificant = false,
                Headline = $"{name}: not enough measurements yet",
                Summary = buckets.Count == 0 && points.Count > 0
                    ? $"{name} is not available in the measurements, so it cannot be explained."
                    : "Sysora needs about two minutes of measurements to tell when a change started.",
                ContributorText = "Not analyzed yet.",
                SimilarText = similar.Text,
                SimilarCount = similar.Count,
                SampleCount = points.Count,
                Source = input.Source,
            };
        }

        var totalMemory = points.LastOrDefault(p => p.MemoryTotalBytes is not null)?.MemoryTotalBytes;
        var current = (buckets[^1].Value + buckets[^2].Value) / 2;
        var earlier = buckets.Take(buckets.Count - 2).Select(b => b.Value).Order().ToArray();
        var low = BaselineCalculator.Percentile(earlier, 0.2);
        var minDelta = MinimumChange(metric, low, totalMemory);
        var increased = current - low >= minDelta;
        var high = IsHigh(metric, current, totalMemory, input.Baseline);
        var format = Formatter(metric);
        var from = buckets[0].Start;

        if (!increased && !high)
        {
            var range = $"{format(buckets.Min(b => b.Value))} – {format(buckets.Max(b => b.Value))}";
            var usualQuiet = Usual(metric, current, totalMemory, input.Baseline);
            return new WhyNowExplanation
            {
                Metric = metric,
                MetricName = name,
                Time = time,
                IsSignificant = false,
                Headline = $"{name} is not unusually high right now",
                Summary = $"{name} stayed between {range} over the last {MetricFormatter.DurationCompact(time - from)}; it is {format(current)} now. There is no recent rise to explain.",
                NowText = format(current),
                Now = current,
                ContributorText = "Nothing to attribute: no significant rise.",
                UsualText = usualQuiet,
                SimilarText = similar.Text,
                SimilarCount = similar.Count,
                Findings =
                [
                    new Finding("Level", $"{name} stayed between {range} over the analyzed period.", FindingBasis.Observed),
                    .. usualQuiet is null ? Array.Empty<Finding>() : [new Finding("Usual range", usualQuiet, FindingBasis.Observed)],
                ],
                From = from,
                SampleCount = points.Count,
                Source = input.Source,
            };
        }

        // When did it start? Walk back while the level stays above halfway between the earlier level and now.
        var onset = 0;
        var startedBefore = true;
        if (increased)
        {
            var threshold = low + ((current - low) / 2);
            var index = buckets.Count - 1;
            var dips = 0;
            onset = index;
            while (index >= 0)
            {
                if (buckets[index].Value >= threshold)
                {
                    onset = index;
                    dips = 0;
                }
                else if (++dips > 1)
                {
                    break;
                }

                index--;
            }

            startedBefore = onset == 0;
        }

        var started = startedBefore ? (DateTimeOffset?)null : buckets[onset].Start;
        var beforeBuckets = startedBefore ? [] : buckets.Skip(Math.Max(0, onset - 4)).Take(onset - Math.Max(0, onset - 4)).ToArray();
        double? before = beforeBuckets.Length > 0 ? beforeBuckets.Average(b => b.Value) : null;
        var duration = time - (started ?? from);
        var contributor = Contributor(metric, points, started, time, before, current, format);
        var associatedFrom = (started ?? time - TimeSpan.FromMinutes(5)) - TimeSpan.FromMinutes(2);
        var associated = input.Events
            .Where(e => AssociatedKinds.Contains(e.Kind) && e.Timestamp >= associatedFrom && e.Timestamp <= time)
            .OrderBy(e => e.Timestamp)
            .Take(8)
            .ToArray();
        var simultaneous = started is { } onsetTime ? Simultaneous(metric, points, onsetTime, time) : [];
        var usual = Usual(metric, current, totalMemory, input.Baseline);

        var findings = new List<Finding>();
        if (before is { } b)
        {
            findings.Add(new Finding("Change", $"{name} rose from {format(b)} to {format(current)}.", FindingBasis.Observed));
        }
        else
        {
            findings.Add(new Finding("Level", $"{name} is {format(current)} and was already high at the start of the analyzed data ({Clock(from)}).", FindingBasis.Observed));
        }

        findings.Add(started is { } s
            ? new Finding("Started", $"Around {Clock(s)} (within {BucketLength.TotalSeconds:0} seconds).", FindingBasis.Observed)
            : new Finding("Started", $"Before {Clock(from)}: earlier data is not available.", FindingBasis.Unknown));
        findings.Add(new Finding("Duration", started is null ? $"At least {MetricFormatter.DurationPrecise(duration)}" : MetricFormatter.DurationPrecise(duration), FindingBasis.Observed));
        findings.Add(contributor.Finding);
        foreach (var associatedEvent in associated.Take(3))
        {
            findings.Add(new Finding("Associated with", $"{associatedEvent.Title} at {Clock(associatedEvent.Timestamp)} (same time; not proof of cause)", FindingBasis.Inferred) { Confidence = ConfidenceLevel.Low });
        }

        findings.AddRange(simultaneous.Select(text => new Finding("At the same time", text, FindingBasis.Observed)));
        if (usual is not null)
        {
            findings.Add(new Finding("Usual range", usual, FindingBasis.Observed));
        }

        findings.Add(new Finding("Similar events", similar.Text, similar.Count is null ? FindingBasis.Unknown : FindingBasis.Observed));
        if (contributor.Value is not null)
        {
            findings.Add(new Finding("Why the application needs more", "What an application does internally is not observable from outside it.", FindingBasis.Unknown));
        }

        var headline = increased ? $"{name} increased significantly" : $"{name} has been high since before {Clock(from)}";
        var summary = (before is { } start ? $"{name} went from {format(start)} to {format(current)}" : $"{name} is {format(current)}")
            + (started is { } at ? $" starting around {Clock(at)} ({MetricFormatter.DurationPrecise(duration)} ago). " : ". ")
            + contributor.Summary;

        return new WhyNowExplanation
        {
            Metric = metric,
            MetricName = name,
            Time = time,
            IsSignificant = true,
            Headline = headline,
            Summary = summary,
            Started = started,
            StartedBeforeData = startedBefore,
            BeforeText = before is { } value ? format(value) : null,
            NowText = format(current),
            Before = before,
            Now = current,
            Duration = duration,
            Contributor = contributor.Value,
            ContributorText = contributor.Text,
            AssociatedEvents = associated,
            Simultaneous = simultaneous,
            UsualText = usual,
            SimilarText = similar.Text,
            SimilarCount = similar.Count,
            Findings = findings,
            Evidence =
            [
                new AnalysisEvidence(name, before is { } v ? $"{format(v)} before, {format(current)} now" : $"{format(current)} now")
                {
                    From = from,
                    To = time,
                    SampleCount = points.Count,
                    Source = input.Source,
                    Reference = $"Averaged over {BucketLength.TotalSeconds:0}-second steps; a change counts from {format(minDelta)}",
                },
            ],
            From = from,
            SampleCount = points.Count,
            Source = input.Source,
        };
    }

    /// <summary>Value of the metric in a snapshot.</summary>
    public static double? Value(MetricSnapshot snapshot, WhyNowMetric metric)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return metric switch
        {
            WhyNowMetric.Cpu => snapshot.CpuPercent,
            WhyNowMetric.Memory => snapshot.MemoryUsedBytes,
            WhyNowMetric.Disk => snapshot.DiskActivePercent,
            WhyNowMetric.Gpu => snapshot.GpuPercent,
            _ => snapshot.NetworkReceiveBitsPerSecond is null && snapshot.NetworkSendBitsPerSecond is null
                ? null
                : (snapshot.NetworkReceiveBitsPerSecond ?? 0) + (snapshot.NetworkSendBitsPerSecond ?? 0),
        };
    }

    private static List<Bucket> Buckets(IReadOnlyList<MetricSnapshot> points, WhyNowMetric metric)
    {
        var buckets = new List<Bucket>();
        var length = BucketLength.Ticks;
        long? current = null;
        double sum = 0;
        var count = 0;
        foreach (var point in points)
        {
            if (Value(point, metric) is not { } value || !double.IsFinite(value))
            {
                continue;
            }

            var key = point.Timestamp.UtcTicks / length;
            if (current is { } open && key != open)
            {
                buckets.Add(new Bucket(new DateTimeOffset(open * length, TimeSpan.Zero), sum / count));
                sum = 0;
                count = 0;
            }

            current = key;
            sum += value;
            count++;
        }

        if (current is { } last && count > 0)
        {
            buckets.Add(new Bucket(new DateTimeOffset(last * length, TimeSpan.Zero), sum / count));
        }

        return buckets;
    }

    /// <summary>Smallest rise worth explaining.</summary>
    private static double MinimumChange(WhyNowMetric metric, double earlier, ulong? totalMemory) => metric switch
    {
        WhyNowMetric.Memory => totalMemory is > 0 and var total ? Math.Max(total * 0.08, 512.0 * 1024 * 1024) : 1024.0 * 1024 * 1024,
        WhyNowMetric.Network => Math.Max(5e6, earlier * 2),
        _ => 15,
    };

    /// <summary>High in absolute terms, or above what 95% of the usual minutes show.</summary>
    private static bool IsHigh(WhyNowMetric metric, double value, ulong? totalMemory, UsageBaseline baseline)
    {
        var percent = Percent(metric, value, totalMemory);
        if (percent is null)
        {
            return false;
        }

        var absolute = metric == WhyNowMetric.Memory ? 85 : 80;
        if (percent >= absolute)
        {
            return true;
        }

        return BaselineMetric(metric) is { } history && baseline.Get(history) is { } usual && percent > Math.Max(usual.P95, usual.Median + 15);
    }

    private static double? Percent(WhyNowMetric metric, double value, ulong? totalMemory) => metric switch
    {
        WhyNowMetric.Memory => totalMemory is > 0 and var total ? value * 100 / total : null,
        WhyNowMetric.Network => null,
        _ => value,
    };

    private static HistoryMetric? BaselineMetric(WhyNowMetric metric) => metric switch
    {
        WhyNowMetric.Cpu => HistoryMetric.Cpu,
        WhyNowMetric.Memory => HistoryMetric.Memory,
        WhyNowMetric.Disk => HistoryMetric.Disk,
        WhyNowMetric.Gpu => HistoryMetric.Gpu,
        _ => null,
    };

    private static string? Usual(WhyNowMetric metric, double current, ulong? totalMemory, UsageBaseline baseline)
    {
        if (BaselineMetric(metric) is not { } history || baseline.Get(history) is not { } usual || Percent(metric, current, totalMemory) is not { } percent)
        {
            return null;
        }

        var position = percent > usual.P95 ? "above 95% of your recorded minutes"
            : percent > usual.P75 ? "above your usual range"
            : percent < usual.P25 ? "below your usual range"
            : "within your usual range";
        return string.Create(CultureInfo.CurrentCulture, $"Usually {usual.UsualRange} (median {usual.Median:0}%); now {MetricFormatter.Percent(percent)}: {position}.");
    }

    private static Func<double, string> Formatter(WhyNowMetric metric) => metric switch
    {
        WhyNowMetric.Memory => v => MetricFormatter.Bytes(Math.Max(v, 0)),
        WhyNowMetric.Network => v => MetricFormatter.BitsPerSecond(v),
        _ => v => MetricFormatter.Percent(v),
    };

    private static (WhyNowContributor? Value, string Text, string Summary, Finding Finding) Contributor(
        WhyNowMetric metric, IReadOnlyList<MetricSnapshot> points, DateTimeOffset? started, DateTimeOffset now, double? before, double current, Func<double, string> format)
    {
        if (metric == WhyNowMetric.Network)
        {
            const string text = "Not available: Windows does not report network usage per application without administrator-level event tracing.";
            return (null, text, "The application responsible cannot be identified: " + text[15..], new Finding("Contributor", text, FindingBasis.Unknown));
        }

        if (metric == WhyNowMetric.Gpu)
        {
            const string text = "Cause unknown: GPU usage per application is not kept in the history. The events at the same time may help.";
            return (null, text, text, new Finding("Contributor", text, FindingBasis.Unknown));
        }

        Func<AppSample, double> appValue = metric switch
        {
            WhyNowMetric.Cpu => a => a.CpuPercent,
            WhyNowMetric.Memory => a => a.MemoryBytes,
            _ => a => a.IoBytesPerSecond,
        };
        Func<double, string> appFormat = metric switch
        {
            WhyNowMetric.Cpu => v => MetricFormatter.Percent(v),
            WhyNowMetric.Memory => v => MetricFormatter.Bytes(Math.Abs(v)),
            _ => v => MetricFormatter.BytesPerSecond(Math.Abs(v)),
        };

        var detailed = points.Where(p => p.TopApps.Count > 0).ToArray();
        var after = detailed.Where(p => p.Timestamp >= now - TimeSpan.FromMinutes(1)).ToArray();
        if (after.Length == 0)
        {
            const string text = "Cause unknown: no per-application data for this period (applications are kept only in the recent in-memory history).";
            return (null, text, text, new Finding("Contributor", text, FindingBasis.Unknown));
        }

        var afterAverages = Averages(after, appValue);
        if (started is not { } onset)
        {
            // No "before" to compare with: only the largest consumer now can be named, which is not a cause.
            var largest = afterAverages.MaxBy(a => a.Value.Value);
            var text = largest.Key is not null
                ? $"Cause unknown: the rise started before the analyzed data. Largest consumer now: {largest.Value.Name} ({appFormat(largest.Value.Value)})."
                : "Cause unknown: the rise started before the analyzed data.";
            return (null, text, text, new Finding("Contributor", text, FindingBasis.Unknown));
        }

        var beforeSnapshots = detailed.Where(p => p.Timestamp >= onset - ContributorWindow && p.Timestamp < onset).ToArray();
        if (beforeSnapshots.Length == 0)
        {
            const string text = "Cause unknown: no per-application data from before the rise.";
            return (null, text, text, new Finding("Contributor", text, FindingBasis.Unknown));
        }

        var beforeAverages = Averages(beforeSnapshots, appValue);
        var changes = afterAverages
            .Select(a => (Key: a.Key, a.Value.Name, Delta: a.Value.Value - (beforeAverages.TryGetValue(a.Key, out var b) ? b.Value : 0), Absent: !beforeAverages.ContainsKey(a.Key)))
            .OrderByDescending(c => c.Delta)
            .ToArray();
        if (changes.Length == 0 || changes[0].Delta <= 0)
        {
            const string text = "Cause unknown: none of the applications measured increased its usage.";
            return (null, text, text, new Finding("Contributor", text, FindingBasis.Unknown));
        }

        var top = changes[0];
        double? systemDelta = metric switch
        {
            WhyNowMetric.Disk => Throughput(after) - Throughput(beforeSnapshots),
            _ => before is { } b ? current - b : null,
        };
        double? share = systemDelta is > 0 and var d ? Math.Min(top.Delta / d, 1) : null;
        var confidence = share switch
        {
            >= 0.6 => ConfidenceLevel.High,
            >= 0.35 => ConfidenceLevel.Medium,
            >= 0.15 => ConfidenceLevel.Low,
            null => ConfidenceLevel.Low,
            _ => (ConfidenceLevel?)null,
        };
        var change = $"+{appFormat(top.Delta)}";
        if (confidence is not { } level)
        {
            var text = $"Cause unknown: no single application accounts for the increase (largest change: {top.Name} {change}).";
            return (null, text, text, new Finding("Contributor", text, FindingBasis.Unknown));
        }

        var shareText = share is { } s ? $", about {MetricFormatter.Percent(s * 100)} of the increase" : string.Empty;
        var io = metric == WhyNowMetric.Disk ? " of I/O (files, devices and network combined)" : string.Empty;
        var newText = top.Absent ? " — not among the busiest applications before" : string.Empty;
        var label = level >= ConfidenceLevel.Medium ? "Likely contributor" : "Possible contributor";
        var contributorText = $"{label}: {top.Name} ({change}{io}{shareText}){newText}";
        var summaryText = level >= ConfidenceLevel.Medium
            ? $"Evidence suggests {top.Name} is the main contributor ({change}{shareText})."
            : $"{top.Name} may have contributed ({change}{shareText}), but the evidence is weak.";
        return (
            new WhyNowContributor(top.Name, top.Key, change, share, level, top.Absent),
            contributorText,
            summaryText,
            new Finding(label, $"{top.Name} ({change}{io}{shareText})", FindingBasis.Inferred) { Confidence = level });
    }

    /// <summary>Average per application over snapshots; an application absent from a snapshot counts as 0 there.</summary>
    private static Dictionary<string, (string Name, double Value)> Averages(IReadOnlyList<MetricSnapshot> snapshots, Func<AppSample, double> value)
    {
        var sums = new Dictionary<string, (string Name, double Sum)>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            foreach (var app in snapshot.TopApps)
            {
                sums[app.Key] = sums.TryGetValue(app.Key, out var existing) ? (existing.Name, existing.Sum + value(app)) : (app.Name, value(app));
            }
        }

        return sums.ToDictionary(s => s.Key, s => (s.Value.Name, s.Value.Sum / snapshots.Count), StringComparer.Ordinal);
    }

    private static double Throughput(IReadOnlyList<MetricSnapshot> snapshots)
    {
        var values = snapshots
            .Where(s => s.DiskReadBytesPerSecond is not null || s.DiskWriteBytesPerSecond is not null)
            .Select(s => (s.DiskReadBytesPerSecond ?? 0) + (s.DiskWriteBytesPerSecond ?? 0))
            .ToArray();
        return values.Length == 0 ? 0 : values.Average();
    }

    /// <summary>Other metrics that rose clearly between just before the onset and now.</summary>
    private static IReadOnlyList<string> Simultaneous(WhyNowMetric metric, IReadOnlyList<MetricSnapshot> points, DateTimeOffset onset, DateTimeOffset now)
    {
        var result = new List<string>();
        var totalMemory = points.LastOrDefault(p => p.MemoryTotalBytes is not null)?.MemoryTotalBytes;
        foreach (var other in Enum.GetValues<WhyNowMetric>().Where(m => m != metric))
        {
            var before = points.Where(p => p.Timestamp >= onset - ContributorWindow && p.Timestamp < onset).Select(p => Value(p, other)).OfType<double>().ToArray();
            var after = points.Where(p => p.Timestamp >= now - TimeSpan.FromMinutes(1)).Select(p => Value(p, other)).OfType<double>().ToArray();
            if (before.Length == 0 || after.Length == 0)
            {
                continue;
            }

            var (b, a) = (before.Average(), after.Average());
            if (a - b >= MinimumChange(other, b, totalMemory))
            {
                var format = Formatter(other);
                result.Add($"{Name(other)} also rose: {format(b)} → {format(a)}");
            }
        }

        return result;
    }

    private static (string Text, int? Count) Similar(WhyNowMetric metric, IReadOnlyList<Alert>? alerts, DateTimeOffset now)
    {
        if (metric is WhyNowMetric.Gpu or WhyNowMetric.Network)
        {
            return ("Similar events: not tracked (no alert rule follows this metric).", null);
        }

        if (alerts is null)
        {
            return ("Similar events: not available (no alert history).", null);
        }

        bool Matches(Alert alert) => metric switch
        {
            WhyNowMetric.Cpu => alert.RuleId is "cpu.sustained" or "app.cpu" || alert.Key == "unusual.cpu",
            WhyNowMetric.Memory => alert.RuleId is "memory.sustained" or "memory.growth" || alert.Key == "unusual.memory",
            _ => alert.RuleId == "disk.busy" || alert.Key == "unusual.disk",
        };

        var count = alerts.Count(a => Matches(a) && a.RaisedAt <= now && now - a.RaisedAt <= SimilarWindow);
        return (count == 0
            ? $"Similar events: none in the last {MetricFormatter.Plural((int)SimilarWindow.TotalDays, "day")}."
            : $"Similar events: {MetricFormatter.Plural(count, "alert")} in the last {MetricFormatter.Plural((int)SimilarWindow.TotalDays, "day")}.", count);
    }

    private static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    private readonly record struct Bucket(DateTimeOffset Start, double Value);
}

/// <summary>
/// Runs "Why now?" on demand: the detailed in-memory history plus, before it, an hour of per-minute averages from the
/// local history (so a slow rise has an earlier level to compare with). Computed off the UI thread.
/// </summary>
public sealed class WhyNowService(
    IPerformanceHistory history,
    IHistoryRepository repository,
    BaselineService baseline,
    AlertService alerts,
    TimeProvider? timeProvider = null)
{
    /// <summary>Per-minute history read before the in-memory buffer.</summary>
    public static readonly TimeSpan OlderHistory = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<WhyNowExplanation> ExplainAsync(WhyNowMetric metric, CancellationToken cancellationToken)
    {
        var detailed = history.GetRecent(history.Duration);
        var now = detailed.Count > 0 ? detailed[^1].Timestamp : _time.GetUtcNow();
        var start = detailed.Count > 0 ? detailed[0].Timestamp : now;
        IReadOnlyList<MetricSnapshot> older = [];
        try
        {
            var minutes = await repository.GetSystemUsageAsync(start - OlderHistory, SystemUsageAggregate.BucketStart(start, HistoryResolution.Minute), HistoryResolution.Minute, cancellationToken).ConfigureAwait(false);
            older = minutes.Select(ReplayService.ToSnapshot).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Without the stored history, the in-memory measurements are still analyzed.
        }

        var events = history.GetEvents(start - OlderHistory, now);
        var input = new WhyNowInput
        {
            Metric = metric,
            Points = [.. older, .. detailed],
            Events = events,
            Baseline = baseline.Current,
            Alerts = alerts.Alerts,
            Source = older.Count > 0
                ? "Per-second measurements kept in memory, preceded by per-minute averages of the local history"
                : "Per-second measurements kept in memory",
        };
        return await Task.Run(() => WhyNowAnalyzer.Explain(input), cancellationToken).ConfigureAwait(false);
    }
}
