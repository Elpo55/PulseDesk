using System.Globalization;
using Sysora.Core.Changes;
using Sysora.Core.Formatting;
using Sysora.Core.Gaming;
using Sysora.Core.History;
using Sysora.Core.Interfaces;
using Sysora.Core.Settings;

namespace Sysora.Core.Analysis;

/// <summary>Measurements compared between two periods.</summary>
public enum StateMetric
{
    Cpu,
    MemoryUsed,
    Disk,
    Gpu,
    NetworkReceive,
    NetworkSend,
    ProcessCount,
    SystemDriveFree,

    /// <summary>Memory in use as a share of physical memory (used to rate the importance of a memory difference).</summary>
    MemoryPercent,
}

/// <summary>Direction of a difference.</summary>
public enum ChangeDirection
{
    Same,
    Up,
    Down,
}

/// <summary>Averages of the measurements over one period. A metric without data in the period is absent (never 0).</summary>
/// <param name="Label">What the period is, e.g. "1 hour ago", "During Space Game".</param>
/// <param name="From">Start of the period.</param>
/// <param name="To">End of the period.</param>
/// <param name="SampleCount">Measurements used.</param>
/// <param name="Source">Where the data comes from, in words.</param>
/// <param name="Values">Average of each metric with data.</param>
public sealed record PeriodState(string Label, DateTimeOffset From, DateTimeOffset To, int SampleCount, string Source, IReadOnlyDictionary<StateMetric, double> Values)
{
    public bool HasData => Values.Count > 0;

    public double? Get(StateMetric metric) => Values.TryGetValue(metric, out var value) ? value : null;
}

/// <summary>One row of a comparison: before, after, the difference and how much it matters.</summary>
public sealed record StateDifference(
    StateMetric Metric,
    string Name,
    double? Before,
    double? After,
    string BeforeText,
    string AfterText,
    string ChangeText,
    string? RelativeText,
    ChangeDirection? Direction,
    ChangeImportance Importance,
    string Explanation)
{
    /// <summary>True when both periods have data for this metric.</summary>
    public bool IsComparable => Before is not null && After is not null;
}

/// <summary>Result of comparing two periods.</summary>
public sealed record StateComparisonResult(PeriodState Before, PeriodState After, IReadOnlyList<StateDifference> Rows, string Summary, IReadOnlyList<string> Notes)
{
    /// <summary>Rows that matter (medium or high importance).</summary>
    public IEnumerable<StateDifference> Notable => Rows.Where(r => r.IsComparable && r.Importance >= ChangeImportance.Medium);
}

/// <summary>
/// Summarizes periods and compares them (pure, deterministic). Only metrics measured in both periods are compared; a
/// metric missing on one side is shown as "Not available" without a difference.
/// </summary>
public static class StateComparer
{
    private static readonly StateMetric[] Shown =
    [
        StateMetric.Cpu, StateMetric.MemoryUsed, StateMetric.Disk, StateMetric.Gpu, StateMetric.NetworkReceive,
        StateMetric.NetworkSend, StateMetric.ProcessCount, StateMetric.SystemDriveFree,
    ];

    /// <summary>Display name of a metric.</summary>
    public static string Name(StateMetric metric) => metric switch
    {
        StateMetric.Cpu => "CPU usage",
        StateMetric.MemoryUsed => "Memory in use",
        StateMetric.Disk => "Disk active time",
        StateMetric.Gpu => "GPU usage",
        StateMetric.NetworkReceive => "Download",
        StateMetric.NetworkSend => "Upload",
        StateMetric.ProcessCount => "Processes",
        StateMetric.SystemDriveFree => "Free space (Windows volume)",
        _ => "Memory in use (%)",
    };

    /// <summary>Averages of every metric over the snapshots of a period.</summary>
    public static PeriodState Summarize(string label, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<MetricSnapshot> points, string source)
    {
        ArgumentNullException.ThrowIfNull(points);
        var inPeriod = points.Where(p => p.Timestamp >= from && p.Timestamp <= to).ToArray();
        var values = new Dictionary<StateMetric, double>();
        void Add(StateMetric metric, Func<MetricSnapshot, double?> value)
        {
            if (SnapshotStatistics.Summarize(inPeriod, value) is { } summary)
            {
                values[metric] = summary.Average;
            }
        }

        Add(StateMetric.Cpu, p => p.CpuPercent);
        Add(StateMetric.MemoryUsed, p => p.MemoryUsedBytes);
        Add(StateMetric.MemoryPercent, p => p.MemoryPercent);
        Add(StateMetric.Disk, p => p.DiskActivePercent);
        Add(StateMetric.Gpu, p => p.GpuPercent);
        Add(StateMetric.NetworkReceive, p => p.NetworkReceiveBitsPerSecond);
        Add(StateMetric.NetworkSend, p => p.NetworkSendBitsPerSecond);
        Add(StateMetric.ProcessCount, p => p.ProcessCount);
        Add(StateMetric.SystemDriveFree, p => p.SystemDriveFreeBytes);
        return new PeriodState(label, from, to, inPeriod.Length, source, values);
    }

    /// <summary>The averages a gaming session recorded while the game ran.</summary>
    public static PeriodState FromSession(GameSession session, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var values = new Dictionary<StateMetric, double>();
        if (session.Cpu is { } cpu)
        {
            values[StateMetric.Cpu] = cpu.Average;
        }

        if (session.Memory is { } memory)
        {
            values[StateMetric.MemoryPercent] = memory.Average;
            if (session.MemoryTotalBytes is { } total)
            {
                values[StateMetric.MemoryUsed] = total * memory.Average / 100;
            }
        }

        if (session.Disk is { } disk)
        {
            values[StateMetric.Disk] = disk.Average;
        }

        if (session.Gpu is { } gpu)
        {
            values[StateMetric.Gpu] = gpu.Average;
        }

        if (session.NetworkReceive is { } receive)
        {
            values[StateMetric.NetworkReceive] = receive.Average;
        }

        if (session.NetworkSend is { } send)
        {
            values[StateMetric.NetworkSend] = send.Average;
        }

        var samples = session.Cpu?.Samples ?? 0;
        return new PeriodState(label ?? $"During {session.Name}", session.Start, session.End, samples, "Averages recorded during the game session", values);
    }

    public static StateComparisonResult Compare(PeriodState before, PeriodState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var rows = new List<StateDifference>();
        foreach (var metric in Shown)
        {
            var (b, a) = (before.Get(metric), after.Get(metric));
            if (b is null && a is null)
            {
                continue;
            }

            rows.Add(Row(metric, b, a, before, after));
        }

        var notable = rows.Where(r => r.IsComparable && r.Importance >= ChangeImportance.Medium)
            .OrderByDescending(r => r.Importance)
            .ToArray();
        var summary = !before.HasData || !after.HasData
            ? "Not enough data to compare: " + string.Join(" and ", new[] { before, after }.Where(p => !p.HasData).Select(p => $"no measurement for \"{p.Label}\"")) + "."
            : notable.Length == 0
                ? "No notable difference between the two periods."
                : $"{MetricFormatter.Plural(notable.Length, "notable difference")}: {string.Join(", ", notable.Take(4).Select(r => $"{r.Name} {r.ChangeText}"))}.";

        var notes = new List<string>
        {
            $"{before.Label}: {Period(before)} · {Samples(before)} · {before.Source}",
            $"{after.Label}: {Period(after)} · {Samples(after)} · {after.Source}",
        };
        var missing = rows.Where(r => !r.IsComparable).Select(r => r.Name).ToArray();
        if (missing.Length > 0)
        {
            notes.Add($"Not compared (measured in only one period): {string.Join(", ", missing)}.");
        }

        return new StateComparisonResult(before, after, rows, summary, notes);
    }

    private static StateDifference Row(StateMetric metric, double? before, double? after, PeriodState beforeState, PeriodState afterState)
    {
        var name = Name(metric);
        var format = Format(metric);
        var beforeText = before is { } bv ? format(bv) : MetricFormatter.NotAvailable;
        var afterText = after is { } av ? format(av) : MetricFormatter.NotAvailable;
        if (before is not { } b || after is not { } a)
        {
            return new StateDifference(metric, name, before, after, beforeText, afterText, "—", null, null, ChangeImportance.Low, "Measured in only one of the two periods: not compared.");
        }

        var difference = a - b;
        var direction = IsSame(metric, b, a) ? ChangeDirection.Same : difference > 0 ? ChangeDirection.Up : ChangeDirection.Down;
        var changeText = direction == ChangeDirection.Same ? "No change" : Difference(metric, difference);
        string? relative = metric is StateMetric.MemoryUsed or StateMetric.NetworkReceive or StateMetric.NetworkSend or StateMetric.ProcessCount or StateMetric.SystemDriveFree
            && b > 0 && direction != ChangeDirection.Same
            ? $"{(difference >= 0 ? "+" : "−")}{MetricFormatter.Percent(Math.Abs(difference / b) * 100)}"
            : null;
        var memoryPoints = afterState.Get(StateMetric.MemoryPercent) - beforeState.Get(StateMetric.MemoryPercent);
        var importance = direction == ChangeDirection.Same ? ChangeImportance.Low : Importance(metric, b, a, memoryPoints);
        return new StateDifference(metric, name, b, a, beforeText, afterText, changeText, relative, direction, importance, Explain(metric, direction));
    }

    private static bool IsSame(StateMetric metric, double before, double after)
    {
        var difference = Math.Abs(after - before);
        return metric switch
        {
            StateMetric.MemoryUsed => difference < Math.Max(50.0 * 1024 * 1024, Math.Max(before, after) * 0.01),
            StateMetric.SystemDriveFree => difference < 100.0 * 1024 * 1024,
            StateMetric.NetworkReceive or StateMetric.NetworkSend => difference < 100_000,
            StateMetric.ProcessCount => difference < 3,
            _ => difference < 1,
        };
    }

    private static ChangeImportance Importance(StateMetric metric, double before, double after, double? memoryPoints)
    {
        var difference = Math.Abs(after - before);
        const double gigabyte = 1024.0 * 1024 * 1024;
        return metric switch
        {
            StateMetric.Cpu or StateMetric.Disk or StateMetric.Gpu => difference >= 20 ? ChangeImportance.High : difference >= 8 ? ChangeImportance.Medium : ChangeImportance.Low,
            StateMetric.MemoryUsed when memoryPoints is { } points => Math.Abs(points) >= 15 ? ChangeImportance.High : Math.Abs(points) >= 5 ? ChangeImportance.Medium : ChangeImportance.Low,
            StateMetric.MemoryUsed => difference / Math.Max(before, 1) >= 0.3 ? ChangeImportance.High : difference / Math.Max(before, 1) >= 0.1 ? ChangeImportance.Medium : ChangeImportance.Low,
            StateMetric.SystemDriveFree => difference >= 20 * gigabyte ? ChangeImportance.High : difference >= 5 * gigabyte ? ChangeImportance.Medium : ChangeImportance.Low,
            StateMetric.NetworkReceive or StateMetric.NetworkSend => Math.Max(before, after) < 1e6 ? ChangeImportance.Low
                : Ratio(before, after) >= 3 ? ChangeImportance.High
                : Ratio(before, after) >= 1.5 ? ChangeImportance.Medium
                : ChangeImportance.Low,
            StateMetric.ProcessCount => difference >= 60 ? ChangeImportance.High : difference >= 20 ? ChangeImportance.Medium : ChangeImportance.Low,
            _ => ChangeImportance.Low,
        };
    }

    private static double Ratio(double a, double b) => Math.Max(a, b) / Math.Max(Math.Min(a, b), 1);

    private static string Explain(StateMetric metric, ChangeDirection direction) => (metric, direction) switch
    {
        (_, ChangeDirection.Same) => "About the same in both periods.",
        (StateMetric.Cpu, ChangeDirection.Up) => "The processor was busier in the second period.",
        (StateMetric.Cpu, _) => "The processor was less busy in the second period.",
        (StateMetric.MemoryUsed, ChangeDirection.Up) => "More memory was in use in the second period.",
        (StateMetric.MemoryUsed, _) => "Less memory was in use in the second period.",
        (StateMetric.Disk, ChangeDirection.Up) => "Disks were busier in the second period.",
        (StateMetric.Disk, _) => "Disks were less busy in the second period.",
        (StateMetric.Gpu, ChangeDirection.Up) => "The graphics processor was busier in the second period.",
        (StateMetric.Gpu, _) => "The graphics processor was less busy in the second period.",
        (StateMetric.SystemDriveFree, ChangeDirection.Down) => "Space was used on the Windows volume between the two periods.",
        (StateMetric.SystemDriveFree, _) => "Space was freed on the Windows volume between the two periods.",
        (StateMetric.ProcessCount, ChangeDirection.Up) => "More processes were running in the second period.",
        (StateMetric.ProcessCount, _) => "Fewer processes were running in the second period.",
        (_, ChangeDirection.Up) => "More network traffic in the second period.",
        _ => "Less network traffic in the second period.",
    };

    private static Func<double, string> Format(StateMetric metric) => metric switch
    {
        StateMetric.MemoryUsed or StateMetric.SystemDriveFree => v => MetricFormatter.Bytes(Math.Max(v, 0)),
        StateMetric.NetworkReceive or StateMetric.NetworkSend => v => MetricFormatter.BitsPerSecond(v),
        StateMetric.ProcessCount => v => Math.Round(v).ToString("N0", CultureInfo.CurrentCulture),
        _ => v => MetricFormatter.Percent(v),
    };

    private static string Difference(StateMetric metric, double difference)
    {
        var sign = difference >= 0 ? "+" : "−";
        var magnitude = Math.Abs(difference);
        return metric switch
        {
            StateMetric.MemoryUsed or StateMetric.SystemDriveFree => $"{sign}{MetricFormatter.Bytes(magnitude)}",
            StateMetric.NetworkReceive or StateMetric.NetworkSend => $"{sign}{MetricFormatter.BitsPerSecond(magnitude)}",
            StateMetric.ProcessCount => string.Create(CultureInfo.CurrentCulture, $"{sign}{Math.Round(magnitude):N0}"),
            _ => string.Create(CultureInfo.CurrentCulture, $"{sign}{Math.Round(magnitude):0} percentage {(Math.Round(magnitude) == 1 ? "point" : "points")}"),
        };
    }

    private static string Period(PeriodState state) =>
        $"{state.From.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} – {state.To.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}";

    private static string Samples(PeriodState state) =>
        state.SampleCount > 0 ? MetricFormatter.Plural(state.SampleCount, "measurement") : "no measurement";
}

/// <summary>Ready-made comparisons offered to the user.</summary>
public enum ComparisonPreset
{
    NowVsHourAgo,
    NowVsYesterday,
    TodayVsYesterday,
    GameBeforeVsDuring,
    GameBeforeVsAfter,
    GameVsPreviousGame,
    AroundTime,
    TwoMoments,
}

/// <summary>A comparison to run.</summary>
/// <param name="Preset">Which comparison.</param>
public sealed record ComparisonRequest(ComparisonPreset Preset)
{
    /// <summary>Time of interest: the event for <see cref="ComparisonPreset.AroundTime"/>, the first moment for <see cref="ComparisonPreset.TwoMoments"/>.</summary>
    public DateTimeOffset? Time { get; init; }

    /// <summary>Second moment for <see cref="ComparisonPreset.TwoMoments"/>.</summary>
    public DateTimeOffset? SecondTime { get; init; }

    /// <summary>Game session for the game presets (the latest one when null).</summary>
    public Guid? SessionId { get; init; }
}

/// <summary>
/// Loads the two periods of a comparison from the best source available: per-second measurements in memory when the
/// period is recent enough, per-minute averages of the local history otherwise, hourly summaries for older periods.
/// </summary>
public sealed class StateComparisonService(
    IPerformanceHistory history,
    IHistoryRepository repository,
    SettingsService settings,
    TimeProvider? timeProvider = null)
{
    /// <summary>Length of the "now" and "moment" windows.</summary>
    public static readonly TimeSpan MomentWindow = TimeSpan.FromMinutes(10);

    /// <summary>Length of the windows before and after an event or a game.</summary>
    public static readonly TimeSpan AroundWindow = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<StateComparisonResult> CompareAsync(ComparisonRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = history.Latest?.Timestamp ?? _time.GetUtcNow();
        var zone = TimeZoneInfo.Local;
        switch (request.Preset)
        {
            case ComparisonPreset.NowVsHourAgo:
                return StateComparer.Compare(
                    await LoadAsync("1 hour ago", now - TimeSpan.FromHours(1) - MomentWindow, now - TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false),
                    await LoadAsync("Now", now - MomentWindow, now, cancellationToken).ConfigureAwait(false));
            case ComparisonPreset.NowVsYesterday:
                return StateComparer.Compare(
                    await LoadAsync("Yesterday at this time", now.AddDays(-1) - MomentWindow, now.AddDays(-1), cancellationToken).ConfigureAwait(false),
                    await LoadAsync("Now", now - MomentWindow, now, cancellationToken).ConfigureAwait(false));
            case ComparisonPreset.TodayVsYesterday:
            {
                var local = TimeZoneInfo.ConvertTime(now, zone);
                var midnight = new DateTimeOffset(local.Date, zone.GetUtcOffset(local.Date));
                return StateComparer.Compare(
                    await LoadAsync("Yesterday", midnight.AddDays(-1), midnight, cancellationToken).ConfigureAwait(false),
                    await LoadAsync("Today", midnight, now, cancellationToken).ConfigureAwait(false));
            }

            case ComparisonPreset.AroundTime when request.Time is { } time:
                return StateComparer.Compare(
                    await LoadAsync("Before", time - AroundWindow, time, cancellationToken).ConfigureAwait(false),
                    await LoadAsync("After", time, Min(time + AroundWindow, now), cancellationToken).ConfigureAwait(false));
            case ComparisonPreset.TwoMoments when request.Time is { } first && request.SecondTime is { } second:
            {
                var (a, b) = first <= second ? (first, second) : (second, first);
                return StateComparer.Compare(
                    await LoadAsync(Moment(a), a - MomentWindow, a, cancellationToken).ConfigureAwait(false),
                    await LoadAsync(Moment(b), b - MomentWindow, b, cancellationToken).ConfigureAwait(false));
            }

            case ComparisonPreset.GameBeforeVsDuring or ComparisonPreset.GameBeforeVsAfter or ComparisonPreset.GameVsPreviousGame:
                return await CompareGameAsync(request, now, cancellationToken).ConfigureAwait(false);
            default:
                throw new ArgumentException("The comparison is missing its time.", nameof(request));
        }
    }

    /// <summary>Averages over a period, from the most detailed source that covers it.</summary>
    public async Task<PeriodState> LoadAsync(string label, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var detailed = history.GetSnapshots(from, to);
        if (detailed.Count > 0 && detailed[0].Timestamp - from <= TimeSpan.FromMinutes(1))
        {
            return StateComparer.Summarize(label, from, to, detailed, "Per-second measurements kept in memory");
        }

        var detailLimit = _time.GetUtcNow().AddDays(-settings.Current.History.DetailRetentionDays);
        var resolution = from >= detailLimit ? HistoryResolution.Minute : HistoryResolution.Hour;
        var start = SystemUsageAggregate.BucketStart(from, resolution);
        var buckets = await repository.GetSystemUsageAsync(start, to, resolution, cancellationToken).ConfigureAwait(false);
        var points = buckets.Select(ReplayService.ToSnapshot).ToArray();
        var source = resolution == HistoryResolution.Minute ? "Per-minute averages of the local history" : "Hourly summaries of the local history";
        return StateComparer.Summarize(label, start, to, points, source);
    }

    private async Task<StateComparisonResult> CompareGameAsync(ComparisonRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessions = (await repository.GetGameSessionsAsync(now - GameSessionService.ListedHistory, cancellationToken).ConfigureAwait(false))
            .OrderBy(s => s.Start)
            .ToList();
        var index = request.SessionId is { } id ? sessions.FindIndex(s => s.Id == id) : sessions.Count - 1;
        if (index < 0)
        {
            var none = new PeriodState("No game session", now, now, 0, "No game session recorded", new Dictionary<StateMetric, double>());
            return StateComparer.Compare(none, none) with { Summary = "No game session recorded yet: play a game for a few minutes, then compare." };
        }

        var session = sessions[index];
        switch (request.Preset)
        {
            case ComparisonPreset.GameBeforeVsDuring:
                return StateComparer.Compare(
                    await LoadAsync($"Before {session.Name}", session.Start - AroundWindow, session.Start, cancellationToken).ConfigureAwait(false),
                    StateComparer.FromSession(session));
            case ComparisonPreset.GameBeforeVsAfter:
                return StateComparer.Compare(
                    await LoadAsync($"Before {session.Name}", session.Start - AroundWindow, session.Start, cancellationToken).ConfigureAwait(false),
                    await LoadAsync($"After {session.Name}", session.End, Min(session.End + AroundWindow, now), cancellationToken).ConfigureAwait(false));
            default:
                if (index == 0)
                {
                    var only = StateComparer.FromSession(session);
                    return StateComparer.Compare(only with { Values = new Dictionary<StateMetric, double>(), Label = "Previous session" }, only)
                        with { Summary = "There is no earlier game session to compare with." };
                }

                var previous = sessions[index - 1];
                return StateComparer.Compare(
                    StateComparer.FromSession(previous, $"{previous.Name} ({previous.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)})"),
                    StateComparer.FromSession(session, $"{session.Name} ({session.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)})"));
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static string Moment(DateTimeOffset time) => time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
