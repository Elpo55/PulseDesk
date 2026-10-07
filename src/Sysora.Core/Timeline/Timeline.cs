using System.Globalization;
using Sysora.Core.Changes;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Interfaces;

namespace Sysora.Core.Timeline;

/// <summary>Kinds of timeline entries (used for filters).</summary>
public enum TimelineCategory
{
    Application,
    Game,
    Alert,
    Anomaly,
    Change,
    System,
    Investigation,
    Monitoring,
}

/// <summary>One entry of the global timeline.</summary>
public sealed record TimelineItem
{
    /// <summary>When it happened (UTC). For a change dated between two snapshots, the latest possible time.</summary>
    public required DateTimeOffset Time { get; init; }

    public required TimelineCategory Category { get; init; }

    public required string Title { get; init; }

    public string? Detail { get; init; }

    public DiagnosisSeverity Severity { get; init; }

    /// <summary>True when the time is not exact (a change found by comparing snapshots).</summary>
    public bool IsApproximate { get; init; }

    /// <summary>Earliest possible time of an approximate entry.</summary>
    public DateTimeOffset? EarliestTime { get; init; }

    /// <summary>Where the entry comes from, e.g. "Observed by Sysora", "Per-minute history".</summary>
    public required string Source { get; init; }

    /// <summary>The page with more context.</summary>
    public DiagnosisAction Action { get; init; }

    public string? AppKey { get; init; }
}

/// <summary>Everything a timeline is built from. Nothing is collected for the timeline itself.</summary>
public sealed record TimelineInput
{
    public required DateTimeOffset From { get; init; }

    public required DateTimeOffset To { get; init; }

    /// <summary>Events observed by Sysora (applications, games, alerts, connectivity, devices, sleep, investigations).</summary>
    public IReadOnlyList<SystemEvent> Events { get; init; } = [];

    /// <summary>Changes found by comparing snapshots of the PC.</summary>
    public IReadOnlyList<DetectedChange> Changes { get; init; } = [];

    /// <summary>Per-minute history of the period, used to mark spikes and returns to normal.</summary>
    public IReadOnlyList<SystemUsageAggregate> Minutes { get; init; } = [];
}

/// <summary>
/// Builds the global timeline of the PC (pure, deterministic) from the data other functions already keep: events,
/// alerts, game sessions, detected changes and the per-minute history. Entries are dated with the real timestamps;
/// a change only known to have happened between two snapshots says so.
/// </summary>
public static class TimelineBuilder
{
    /// <summary>Most entries returned (the most recent ones).</summary>
    public const int MaxItems = 1500;

    /// <summary>A spike starts when the minute average reaches this level (CPU, disk, GPU).</summary>
    public const double SpikeLevel = 60;

    /// <summary>Activity is back to normal below this minute average (CPU, disk, GPU).</summary>
    public const double NormalLevel = 40;

    /// <summary>Memory is reported nearly full from this minute average, and back to normal below <see cref="MemoryNormalLevel"/>.</summary>
    public const double MemorySpikeLevel = 90;

    public const double MemoryNormalLevel = 85;

    /// <summary>Most recent first.</summary>
    public static IReadOnlyList<TimelineItem> Build(TimelineInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var items = new List<TimelineItem>();
        foreach (var systemEvent in input.Events)
        {
            if (systemEvent.Timestamp >= input.From && systemEvent.Timestamp <= input.To && FromEvent(systemEvent) is { } item)
            {
                items.Add(item);
            }
        }

        foreach (var change in input.Changes)
        {
            if (change.Before >= input.From && change.Before <= input.To)
            {
                items.Add(FromChange(change));
            }
        }

        items.AddRange(Spikes(input.Minutes).Where(i => i.Time >= input.From && i.Time <= input.To));
        return items
            .OrderByDescending(i => i.Time)
            .ThenBy(i => i.Category)
            .ThenBy(i => i.Title, StringComparer.Ordinal)
            .Take(MaxItems)
            .ToArray();
    }

    /// <summary>Starts and ends of high activity, from per-minute averages (oldest first in, any order out).</summary>
    public static IReadOnlyList<TimelineItem> Spikes(IReadOnlyList<SystemUsageAggregate> minutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        var ordered = minutes.Where(m => m.Resolution == HistoryResolution.Minute).OrderBy(m => m.Start).ToArray();
        var items = new List<TimelineItem>();
        foreach (var (metric, name, high, normal) in new[]
        {
            (HistoryMetric.Cpu, "CPU usage", SpikeLevel, NormalLevel),
            (HistoryMetric.Memory, "Memory usage", MemorySpikeLevel, MemoryNormalLevel),
            (HistoryMetric.Disk, "Disk activity", SpikeLevel, NormalLevel),
            (HistoryMetric.Gpu, "GPU usage", SpikeLevel, NormalLevel),
        })
        {
            DateTimeOffset? since = null;
            double peak = 0;
            DateTimeOffset? previousEnd = null;
            foreach (var minute in ordered)
            {
                // A gap (Sysora closed, PC asleep) ends a run without claiming the activity went back to normal.
                if (previousEnd is { } end && minute.Start - end > TimeSpan.FromMinutes(2))
                {
                    since = null;
                }

                previousEnd = minute.End;
                if (minute.Get(metric) is not { } value)
                {
                    continue;
                }

                if (since is null && value.Average >= high)
                {
                    since = minute.Start;
                    peak = value.Maximum;
                    items.Add(new TimelineItem
                    {
                        Time = minute.Start,
                        Category = TimelineCategory.Anomaly,
                        Title = $"{name} high",
                        Detail = string.Create(CultureInfo.CurrentCulture, $"{MetricFormatter.Percent(value.Average)} on average over the minute (peak {MetricFormatter.Percent(value.Maximum)})"),
                        Severity = DiagnosisSeverity.Info,
                        Source = "Per-minute history",
                        Action = DiagnosisAction.Replay,
                    });
                }
                else if (since is { } start)
                {
                    peak = Math.Max(peak, value.Maximum);
                    if (value.Average < normal)
                    {
                        items.Add(new TimelineItem
                        {
                            Time = minute.Start,
                            Category = TimelineCategory.Anomaly,
                            Title = $"{name} back to normal",
                            Detail = $"After {MetricFormatter.DurationCompact(minute.Start - start)} of high activity (peak {MetricFormatter.Percent(peak)})",
                            Severity = DiagnosisSeverity.Normal,
                            Source = "Per-minute history",
                            Action = DiagnosisAction.Replay,
                        });
                        since = null;
                    }
                }
            }
        }

        return items;
    }

    private static TimelineItem? FromEvent(SystemEvent systemEvent)
    {
        var (category, severity, action) = systemEvent.Kind switch
        {
            SystemEventKind.AppStarted or SystemEventKind.AppExited => (TimelineCategory.Application, DiagnosisSeverity.Normal, DiagnosisAction.AppImpact),
            SystemEventKind.AppHighCpu or SystemEventKind.AppHighMemory => (TimelineCategory.Anomaly, DiagnosisSeverity.Info, DiagnosisAction.AppImpact),
            SystemEventKind.GameStarted or SystemEventKind.GameEnded => (TimelineCategory.Game, DiagnosisSeverity.Normal, DiagnosisAction.Gaming),
            SystemEventKind.AlertRaised => (TimelineCategory.Alert, DiagnosisSeverity.Warning, DiagnosisAction.Alerts),
            SystemEventKind.AlertResolved => (TimelineCategory.Alert, DiagnosisSeverity.Normal, DiagnosisAction.Alerts),
            SystemEventKind.ConnectivityChanged => (TimelineCategory.System, SystemEventDetector.IsConnectivityLoss(systemEvent) ? DiagnosisSeverity.Info : DiagnosisSeverity.Normal, DiagnosisAction.Network),
            SystemEventKind.VolumeAdded or SystemEventKind.VolumeRemoved => (TimelineCategory.System, DiagnosisSeverity.Normal, DiagnosisAction.Storage),
            SystemEventKind.SystemSuspending or SystemEventKind.SystemResumed => (TimelineCategory.System, DiagnosisSeverity.Normal, DiagnosisAction.Replay),
            SystemEventKind.InvestigationStarted or SystemEventKind.InvestigationEnded => (TimelineCategory.Investigation, DiagnosisSeverity.Normal, DiagnosisAction.Troubleshooting),
            SystemEventKind.MetricUnavailable => (TimelineCategory.Monitoring, DiagnosisSeverity.Info, DiagnosisAction.Performance),
            _ => (TimelineCategory.Monitoring, DiagnosisSeverity.Normal, DiagnosisAction.Replay),
        };

        return new TimelineItem
        {
            Time = systemEvent.Timestamp,
            Category = category,
            Title = systemEvent.Title,
            Detail = systemEvent.Detail,
            Severity = severity,
            Source = "Observed by Sysora",
            Action = action,
            AppKey = systemEvent.AppKey,
        };
    }

    private static TimelineItem FromChange(DetectedChange change)
    {
        var values = (change.OldValue, change.NewValue) switch
        {
            ({ } old, { } current) => $"{old} → {current}",
            (null, { } current) => current,
            ({ } old, null) => $"Was: {old}",
            _ => null,
        };
        var window = change.After is { } after
            ? $"Happened between {after.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} and {change.Before.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}."
            : null;
        return new TimelineItem
        {
            Time = change.Before,
            EarliestTime = change.After,
            IsApproximate = change.After is not null,
            Category = TimelineCategory.Change,
            Title = change.Title,
            Detail = string.Join(" ", new[] { values, window }.Where(p => p is not null)),
            Severity = change.Importance == ChangeImportance.High ? DiagnosisSeverity.Info : DiagnosisSeverity.Normal,
            Source = "Detected by comparing snapshots of the PC",
            Action = DiagnosisAction.Changes,
            AppKey = change.AppKey,
        };
    }
}

/// <summary>Loads the timeline of a period from the local history and the in-memory events.</summary>
public sealed class TimelineService(IPerformanceHistory history, IHistoryRepository repository)
{
    private const int MaxEvents = 5000;

    /// <summary>Spikes are derived from per-minute data only for periods up to this long.</summary>
    public static readonly TimeSpan SpikeRange = TimeSpan.FromDays(8);

    public async Task<IReadOnlyList<TimelineItem>> LoadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // Events written to the history and those still only in memory (recording off, or not written yet) are merged.
        var stored = await repository.GetEventsAsync(from, to, MaxEvents, cancellationToken).ConfigureAwait(false);
        var recent = history.GetEvents(from, to);
        var events = stored
            .Concat(recent)
            .DistinctBy(e => (e.Timestamp.UtcTicks / TimeSpan.TicksPerMillisecond, e.Kind, e.Title))
            .ToArray();
        var changes = await repository.GetChangesAsync(from - TimeSpan.FromDays(1), cancellationToken).ConfigureAwait(false);
        var minutes = to - from <= SpikeRange
            ? await repository.GetSystemUsageAsync(from, to, HistoryResolution.Minute, cancellationToken).ConfigureAwait(false)
            : [];
        var input = new TimelineInput { From = from, To = to, Events = events, Changes = changes, Minutes = minutes };
        return await Task.Run(() => TimelineBuilder.Build(input), cancellationToken).ConfigureAwait(false);
    }
}
