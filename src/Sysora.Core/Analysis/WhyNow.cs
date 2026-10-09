using System.Globalization;
using Sysora.Core.Alerts;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Interfaces;
using Sysora.Localization;

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
        WhyNowMetric.Cpu => Strings.Diag_Metric_CpuUsage,
        WhyNowMetric.Memory => Strings.Diag_Metric_MemoryUsage,
        WhyNowMetric.Disk => Strings.WhyNow_Metric_Disk,
        WhyNowMetric.Gpu => Strings.Diag_Metric_GpuUsage,
        _ => Strings.WhyNow_Metric_Network,
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
                Headline = Text.Format(Strings.WhyNow_NotEnough_Headline, name),
                Summary = buckets.Count == 0 && points.Count > 0
                    ? Text.Format(Strings.WhyNow_NotEnough_Missing, name)
                    : Strings.WhyNow_NotEnough_Summary,
                ContributorText = Strings.Recurring_NotAnalyzed,
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
            var range = Text.Format(Strings.WhyNow_Range, format(buckets.Min(b => b.Value)), format(buckets.Max(b => b.Value)));
            var usualQuiet = Usual(metric, current, totalMemory, input.Baseline);
            return new WhyNowExplanation
            {
                Metric = metric,
                MetricName = name,
                Time = time,
                IsSignificant = false,
                Headline = Text.Format(Strings.WhyNow_Quiet_Headline, name),
                Summary = Text.Format(Strings.WhyNow_Quiet_Summary, name, range, MetricFormatter.DurationCompact(time - from), format(current)),
                NowText = format(current),
                Now = current,
                ContributorText = Strings.WhyNow_Quiet_Contributor,
                UsualText = usualQuiet,
                SimilarText = similar.Text,
                SimilarCount = similar.Count,
                Findings =
                [
                    new Finding(Strings.WhyNow_Label_Level, Text.Format(Strings.WhyNow_Quiet_Level, name, range), FindingBasis.Observed),
                    .. usualQuiet is null ? Array.Empty<Finding>() : [new Finding(Strings.WhyNow_Label_UsualRange, usualQuiet, FindingBasis.Observed)],
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
            findings.Add(new Finding(Strings.WhyNow_Label_Change, Text.Format(Strings.WhyNow_Change, name, format(b), format(current)), FindingBasis.Observed));
        }
        else
        {
            findings.Add(new Finding(Strings.WhyNow_Label_Level, Text.Format(Strings.WhyNow_AlreadyHigh, name, format(current), Clock(from)), FindingBasis.Observed));
        }

        findings.Add(started is { } s
            ? new Finding(Strings.WhyNow_Label_Started, Text.Format(Strings.WhyNow_Started, Clock(s), BucketLength.TotalSeconds), FindingBasis.Observed)
            : new Finding(Strings.WhyNow_Label_Started, Text.Format(Strings.WhyNow_StartedBefore, Clock(from)), FindingBasis.Unknown));
        findings.Add(new Finding(Strings.WhyNow_Label_Duration, started is null ? Text.Format(Strings.WhyNow_AtLeast, MetricFormatter.DurationPrecise(duration)) : MetricFormatter.DurationPrecise(duration), FindingBasis.Observed));
        findings.Add(contributor.Finding);
        foreach (var associatedEvent in associated.Take(3))
        {
            findings.Add(new Finding(Strings.WhyNow_Label_Associated, Text.Format(Strings.WhyNow_Associated, associatedEvent.Title, Clock(associatedEvent.Timestamp)), FindingBasis.Inferred) { Confidence = ConfidenceLevel.Low });
        }

        findings.AddRange(simultaneous.Select(text => new Finding(Strings.WhyNow_Label_SameTime, text, FindingBasis.Observed)));
        if (usual is not null)
        {
            findings.Add(new Finding(Strings.WhyNow_Label_UsualRange, usual, FindingBasis.Observed));
        }

        findings.Add(new Finding(Strings.WhyNow_Label_Similar, similar.Text, similar.Count is null ? FindingBasis.Unknown : FindingBasis.Observed));
        if (contributor.Value is not null)
        {
            findings.Add(new Finding(Strings.WhyNow_Label_WhyMore, Strings.WhyNow_NotObservable, FindingBasis.Unknown));
        }

        var headline = increased
            ? Text.Format(Strings.WhyNow_Headline_Increased, name)
            : Text.Format(Strings.WhyNow_Headline_High, name, Clock(from));
        var summary = (before is { } start
                ? Text.Format(Strings.WhyNow_Summary_WentFrom, name, format(start), format(current))
                : Text.Format(Strings.WhyNow_Summary_Is, name, format(current)))
            + (started is { } at
                ? Text.Format(Strings.WhyNow_Summary_Starting, Clock(at), MetricFormatter.DurationPrecise(duration))
                : ". ")
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
                new AnalysisEvidence(name, before is { } v
                    ? Text.Format(Strings.WhyNow_Ev_BeforeNow, format(v), format(current))
                    : Text.Format(Strings.WhyNow_Ev_Now, format(current)))
                {
                    From = from,
                    To = time,
                    SampleCount = points.Count,
                    Source = input.Source,
                    Reference = Text.Format(Strings.WhyNow_Ev_Reference, BucketLength.TotalSeconds, format(minDelta)),
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

        var position = percent > usual.P95 ? Strings.WhyNow_Position_Above95
            : percent > usual.P75 ? Strings.WhyNow_Position_Above
            : percent < usual.P25 ? Strings.WhyNow_Position_Below
            : Strings.WhyNow_Position_Within;
        return Text.Format(Strings.WhyNow_Usual, usual.UsualRange, MetricFormatter.Percent(usual.Median), MetricFormatter.Percent(percent), position);
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
            var text = Strings.WhyNow_Network_NotAvailable;
            return (null, text, Strings.WhyNow_Network_Summary, new Finding(Strings.WhyNow_Label_Contributor, text, FindingBasis.Unknown));
        }

        if (metric == WhyNowMetric.Gpu)
        {
            var text = Strings.WhyNow_Gpu_Unknown;
            return (null, text, text, new Finding(Strings.WhyNow_Label_Contributor, text, FindingBasis.Unknown));
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
            var text = Strings.WhyNow_NoAppData;
            return (null, text, text, new Finding(Strings.WhyNow_Label_Contributor, text, FindingBasis.Unknown));
        }

        var afterAverages = Averages(after, appValue);
        if (started is not { } onset)
        {
            // No "before" to compare with: only the largest consumer now can be named, which is not a cause.
            var largest = afterAverages.MaxBy(a => a.Value.Value);
            var text = largest.Key is not null
                ? Text.Format(Strings.WhyNow_StartedBeforeLargest, largest.Value.Name, appFormat(largest.Value.Value))
                : Strings.WhyNow_StartedBeforeData;
            return (null, text, text, new Finding(Strings.WhyNow_Label_Contributor, text, FindingBasis.Unknown));
        }

        var beforeSnapshots = detailed.Where(p => p.Timestamp >= onset - ContributorWindow && p.Timestamp < onset).ToArray();
        if (beforeSnapshots.Length == 0)
        {
            var text = Strings.WhyNow_NoDataBefore;
            return (null, text, text, new Finding(Strings.WhyNow_Label_Contributor, text, FindingBasis.Unknown));
        }

        var beforeAverages = Averages(beforeSnapshots, appValue);
        var changes = afterAverages
            .Select(a => (Key: a.Key, a.Value.Name, Delta: a.Value.Value - (beforeAverages.TryGetValue(a.Key, out var b) ? b.Value : 0), Absent: !beforeAverages.ContainsKey(a.Key)))
            .OrderByDescending(c => c.Delta)
            .ToArray();
        if (changes.Length == 0 || changes[0].Delta <= 0)
        {
            var text = Strings.WhyNow_NoIncrease;
            return (null, text, text, new Finding(Strings.WhyNow_Label_Contributor, text, FindingBasis.Unknown));
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
            var text = Text.Format(Strings.WhyNow_NoSingleApp, top.Name, change);
            return (null, text, text, new Finding(Strings.WhyNow_Label_Contributor, text, FindingBasis.Unknown));
        }

        var shareText = share is { } s ? Text.Format(Strings.WhyNow_Share, MetricFormatter.Percent(s * 100)) : string.Empty;
        var io = metric == WhyNowMetric.Disk ? Strings.WhyNow_IoNote : string.Empty;
        var newText = top.Absent ? Strings.WhyNow_NewApp : string.Empty;
        var label = level >= ConfidenceLevel.Medium ? Strings.WhyNow_Label_Likely : Strings.WhyNow_Label_Possible;
        var contributorText = Text.Format(Strings.WhyNow_ContributorText, label, top.Name, change, io, shareText, newText);
        var summaryText = level >= ConfidenceLevel.Medium
            ? Text.Format(Strings.WhyNow_Summary_Likely, top.Name, change, shareText)
            : Text.Format(Strings.WhyNow_Summary_Possible, top.Name, change, shareText);
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
                result.Add(Text.Format(Strings.WhyNow_AlsoRose, Name(other), format(b), format(a)));
            }
        }

        return result;
    }

    private static (string Text, int? Count) Similar(WhyNowMetric metric, IReadOnlyList<Alert>? alerts, DateTimeOffset now)
    {
        if (metric is WhyNowMetric.Gpu or WhyNowMetric.Network)
        {
            return (Strings.WhyNow_Similar_NotTracked, null);
        }

        if (alerts is null)
        {
            return (Strings.WhyNow_Similar_NoHistory, null);
        }

        bool Matches(Alert alert) => metric switch
        {
            WhyNowMetric.Cpu => alert.RuleId is "cpu.sustained" or "app.cpu" || alert.Key == "unusual.cpu",
            WhyNowMetric.Memory => alert.RuleId is "memory.sustained" or "memory.growth" || alert.Key == "unusual.memory",
            _ => alert.RuleId == "disk.busy" || alert.Key == "unusual.disk",
        };

        var count = alerts.Count(a => Matches(a) && a.RaisedAt <= now && now - a.RaisedAt <= SimilarWindow);
        return (count == 0
            ? Text.Format(Strings.WhyNow_Similar_None, (int)SimilarWindow.TotalDays)
            : Text.Format(Strings.WhyNow_Similar_Count, Text.Plural(count, Strings.Count_Alert_One, Strings.Count_Alert_Other), (int)SimilarWindow.TotalDays), count);
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
                ? Strings.WhyNow_Source_Mixed
                : Strings.State_Source_Seconds,
        };
        return await Task.Run(() => WhyNowAnalyzer.Explain(input), cancellationToken).ConfigureAwait(false);
    }
}
