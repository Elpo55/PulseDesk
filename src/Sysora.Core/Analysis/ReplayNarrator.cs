using System.Globalization;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Localization;

namespace Sysora.Core.Analysis;

/// <summary>What a replay moment is about.</summary>
public enum ReplayMomentKind
{
    /// <summary>A metric rose sharply or crossed a high level.</summary>
    Rise,

    /// <summary>A metric came back down.</summary>
    Recovery,

    /// <summary>An event Sysora observed (application, connectivity, alert, gap...).</summary>
    Event,
}

/// <summary>A notable moment in a replayed period.</summary>
/// <param name="Time">When it happened.</param>
/// <param name="Text">What happened, e.g. "CPU rose from 38% to 87% (render.exe 62%)".</param>
/// <param name="Kind">Kind of moment.</param>
/// <param name="Metric">Metric concerned, for metric moments.</param>
public sealed record ReplayMoment(DateTimeOffset Time, string Text, ReplayMomentKind Kind, HistoryMetric? Metric = null)
{
    /// <summary>Application concerned, when one is named.</summary>
    public string? AppKey { get; init; }
}

/// <summary>The story of a period: notable moments and a one-paragraph summary.</summary>
public sealed record ReplayStory(IReadOnlyList<ReplayMoment> Moments, string Summary);

/// <summary>
/// Explains what happened in a period from the measurements (deterministic). Detects sharp rises and high
/// levels of CPU, memory, disk and GPU, names the application using the most of the resource at that time when
/// one clearly stands out, merges the events Sysora recorded, and summarizes the sequence.
/// </summary>
public static class ReplayNarrator
{
    /// <summary>Moments kept at most (the earliest ones are dropped first beyond it).</summary>
    public const int MaxMoments = 20;

    private static readonly (HistoryMetric Metric, double High, double Jump)[] Metrics =
    [
        (HistoryMetric.Cpu, 80, 25),
        (HistoryMetric.Memory, 85, 15),
        (HistoryMetric.Disk, 90, 40),
        (HistoryMetric.Gpu, 90, 40),
    ];

    /// <summary>"CPU rose from 20% to 85%".</summary>
    private static string RoseTemplate(HistoryMetric metric) => metric switch
    {
        HistoryMetric.Cpu => Strings.Replay_Rose_Cpu,
        HistoryMetric.Memory => Strings.Replay_Rose_Memory,
        HistoryMetric.Disk => Strings.Replay_Rose_Disk,
        _ => Strings.Replay_Rose_Gpu,
    };

    /// <summary>"CPU reached 95%".</summary>
    private static string ReachedTemplate(HistoryMetric metric) => metric switch
    {
        HistoryMetric.Cpu => Strings.Replay_Reached_Cpu,
        HistoryMetric.Memory => Strings.Replay_Reached_Memory,
        HistoryMetric.Disk => Strings.Replay_Reached_Disk,
        _ => Strings.Replay_Reached_Gpu,
    };

    /// <summary>"CPU back to 30%".</summary>
    private static string BackTemplate(HistoryMetric metric) => metric switch
    {
        HistoryMetric.Cpu => Strings.Replay_Back_Cpu,
        HistoryMetric.Memory => Strings.Replay_Back_Memory,
        HistoryMetric.Disk => Strings.Replay_Back_Disk,
        _ => Strings.Replay_Back_Gpu,
    };

    public static ReplayStory Narrate(IReadOnlyList<MetricSnapshot> snapshots, IReadOnlyList<SystemEvent> events)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(events);
        if (snapshots.Count == 0)
        {
            return new ReplayStory([], Strings.Replay_NoMeasurements);
        }

        var moments = new List<ReplayMoment>();
        foreach (var (metric, high, jump) in Metrics)
        {
            moments.AddRange(DetectMetric(snapshots, metric, high, jump));
        }

        moments.AddRange(events.Select(e => new ReplayMoment(e.Timestamp, e.Title, ReplayMomentKind.Event) { AppKey = e.AppKey }));
        var ordered = moments.OrderBy(m => m.Time).ThenBy(m => m.Kind).ToList();
        if (ordered.Count > MaxMoments)
        {
            ordered = ordered.Skip(ordered.Count - MaxMoments).ToList();
        }

        return new ReplayStory(ordered, Summarize(snapshots, ordered));
    }

    /// <summary>Rises (sharp increases or crossing the high level, lasting) and recoveries of one metric.</summary>
    private static IEnumerable<ReplayMoment> DetectMetric(IReadOnlyList<MetricSnapshot> snapshots, HistoryMetric metric, double high, double jump)
    {
        var points = snapshots.Where(s => s.Get(metric) is not null).Select(s => (s.Timestamp, Value: s.Get(metric)!.Value, Snapshot: s)).ToArray();
        if (points.Length < 3)
        {
            yield break;
        }

        var interval = (points[^1].Timestamp - points[0].Timestamp) / Math.Max(points.Length - 1, 1);
        // About 10 seconds of smoothing for detailed data (CPU is noisy); none for per-minute data.
        var smoothing = Math.Max(1, (int)Math.Round(10 / Math.Max(interval.TotalSeconds, 1)));
        var smooth = Smooth(points.Select(p => p.Value).ToArray(), smoothing);
        var lookBack = Math.Max(1, (int)Math.Round(30 / Math.Max(interval.TotalSeconds, 1)));
        var isHigh = false;
        var armed = true;
        var peak = double.MinValue;
        var lastMoment = DateTimeOffset.MinValue;
        var spacing = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, interval.Ticks * 3));

        for (var i = 1; i < points.Length; i++)
        {
            var value = smooth[i];
            var before = smooth[Math.Max(0, i - lookBack)];

            // One moment per rise: after reporting one, wait until the value has come down noticeably.
            if (!armed)
            {
                peak = Math.Max(peak, value);
                armed = value <= peak - (jump / 2);
            }

            var crossedUp = !isHigh && value >= high;
            var jumped = value - before >= jump;
            if (armed && (crossedUp || jumped) && points[i].Timestamp - lastMoment >= spacing)
            {
                // Describe the level the rise reached, not the middle of the rise.
                var reached = smooth.AsSpan(i, Math.Min(lookBack + 1, smooth.Length - i)).ToArray().Max();
                var app = metric is HistoryMetric.Cpu or HistoryMetric.Memory or HistoryMetric.Disk ? Culprit(points[i].Snapshot, metric) : null;
                var text = jumped && before < high
                    ? Text.Format(RoseTemplate(metric), MetricFormatter.Percent(before), MetricFormatter.Percent(reached))
                    : Text.Format(ReachedTemplate(metric), MetricFormatter.Percent(reached));
                if (app is not null)
                {
                    text += $" ({app.Value.Description})";
                }

                yield return new ReplayMoment(points[i].Timestamp, text, ReplayMomentKind.Rise, metric) { AppKey = app?.Key };
                lastMoment = points[i].Timestamp;
                armed = false;
                peak = value;
            }

            if (value >= high)
            {
                isHigh = true;
            }
            else if (isHigh && value < high - 15)
            {
                isHigh = false;
                armed = true;
                yield return new ReplayMoment(points[i].Timestamp, Text.Format(BackTemplate(metric), MetricFormatter.Percent(value)), ReplayMomentKind.Recovery, metric);
                lastMoment = points[i].Timestamp;
            }
        }
    }

    /// <summary>The application clearly responsible for most of a resource at that time, if any.</summary>
    private static (string Key, string Description)? Culprit(MetricSnapshot snapshot, HistoryMetric metric)
    {
        var apps = snapshot.TopApps;
        if (apps.Count == 0)
        {
            return null;
        }

        switch (metric)
        {
            case HistoryMetric.Cpu:
                var cpu = apps.MaxBy(a => a.CpuPercent)!;
                return snapshot.CpuPercent is { } total && total > 0 && cpu.CpuPercent >= 15 && cpu.CpuPercent / total >= 0.4
                    ? (cpu.Key, $"{cpu.Name} {MetricFormatter.Percent(cpu.CpuPercent)}")
                    : null;
            case HistoryMetric.Memory:
                var memory = apps.MaxBy(a => a.MemoryBytes)!;
                return snapshot.MemoryTotalBytes is { } size && size > 0 && memory.MemoryBytes * 100.0 / size >= 15
                    ? (memory.Key, $"{memory.Name} {MetricFormatter.Bytes(memory.MemoryBytes)}")
                    : null;
            default:
                var io = apps.MaxBy(a => a.IoBytesPerSecond)!;
                return io.IoBytesPerSecond >= 5 * 1024 * 1024
                    ? (io.Key, Text.Format(Strings.Replay_MostIo, io.Name, MetricFormatter.BytesPerSecond(io.IoBytesPerSecond)))
                    : null;
        }
    }

    private static string Summarize(IReadOnlyList<MetricSnapshot> snapshots, IReadOnlyList<ReplayMoment> moments)
    {
        var rises = moments.Where(m => m.Kind == ReplayMomentKind.Rise).ToList();
        if (rises.Count == 0)
        {
            var cpu = SnapshotStatistics.Summarize(snapshots, s => s.CpuPercent);
            var memory = SnapshotStatistics.Summarize(snapshots, s => s.MemoryPercent);
            var levels = string.Join(Strings.List_Separator, new[]
            {
                cpu is { } c ? Text.Format(Strings.Replay_CpuAveraged, MetricFormatter.Percent(c.Average), MetricFormatter.Percent(c.Peak)) : null,
                memory is { } m ? Text.Format(Strings.Replay_MemoryLevel, MetricFormatter.Percent(m.Average)) : null,
            }.Where(p => p is not null));
            return levels.Length == 0
                ? Strings.Replay_NoNotableChange
                : Text.Format(Strings.Replay_NoSharpRise, levels);
        }

        // First rise of each metric, in order: "A rise in CPU usage appeared at 14:33, followed by memory at 14:34."
        var firsts = rises.GroupBy(r => r.Metric).Select(g => g.First()).OrderBy(r => r.Time).Take(3).ToList();
        var first = firsts[0];
        var culprit = first.AppKey is null ? string.Empty : " " + Culprit(first.Text);
        var sentence = Text.Format(Strings.Replay_SharpRise, Noun(first.Metric), culprit, Clock(first.Time));
        if (firsts.Count > 1)
        {
            sentence += Text.Format(
                Strings.Replay_FollowedBy,
                Text.List(firsts.Skip(1).Select(r => Text.Format(Strings.Replay_NounAt, Noun(r.Metric), Clock(r.Time)))));
        }

        return sentence + ".";
    }

    private static string Culprit(string text)
    {
        var start = text.LastIndexOf('(');
        return start < 0 ? string.Empty : "(" + text[(start + 1)..].TrimEnd(')') + ")";
    }

    private static string Noun(HistoryMetric? metric) => metric switch
    {
        HistoryMetric.Cpu => Strings.Replay_Noun_Cpu,
        HistoryMetric.Memory => Strings.Replay_Noun_Memory,
        HistoryMetric.Disk => Strings.Replay_Noun_Disk,
        HistoryMetric.Gpu => Strings.Replay_Noun_Gpu,
        _ => Strings.Replay_Noun_Other,
    };

    private static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);

    private static double[] Smooth(double[] values, int window)
    {
        if (window <= 1)
        {
            return values;
        }

        var result = new double[values.Length];
        double sum = 0;
        for (var i = 0; i < values.Length; i++)
        {
            sum += values[i];
            if (i >= window)
            {
                sum -= values[i - window];
            }

            result[i] = sum / Math.Min(i + 1, window);
        }

        return result;
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.CurrentCulture);
}
