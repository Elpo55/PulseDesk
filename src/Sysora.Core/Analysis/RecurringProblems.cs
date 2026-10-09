using System.Globalization;
using Sysora.Core.Alerts;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Interfaces;
using Sysora.Localization;

namespace Sysora.Core.Analysis;

/// <summary>Kinds of problems Sysora follows over days.</summary>
public enum RecurringProblemKind
{
    HighCpu,
    HighMemory,
    MemoryGrowth,
    DiskBusy,
    ApplicationCpu,
    UnusualActivity,
    ConnectivityLoss,
}

/// <summary>
/// A problem that came back several times on different days, with when it tends to happen and the application most
/// often associated with it. Built only from recorded alerts and events: nothing is extrapolated.
/// </summary>
public sealed record RecurringProblem
{
    /// <summary>Stable identifier of the problem family, e.g. "memory.sustained" or "app.cpu:path:C:\…".</summary>
    public required string Key { get; init; }

    public required RecurringProblemKind Kind { get; init; }

    /// <summary>Short title, e.g. "High memory usage".</summary>
    public required string Title { get; init; }

    /// <summary>One sentence, e.g. "High memory usage occurred 6 times in the last 7 days."</summary>
    public required string Description { get; init; }

    /// <summary>Number of distinct episodes.</summary>
    public required int Occurrences { get; init; }

    /// <summary>Number of distinct local days with at least one episode.</summary>
    public required int Days { get; init; }

    public required DateTimeOffset First { get; init; }

    public required DateTimeOffset Last { get; init; }

    /// <summary>"Most events happened between 19:00 and 22:00 (4 of 6)", when the times cluster clearly.</summary>
    public string? TimePattern { get; init; }

    /// <summary>Application most often associated with the episodes, when one stands out.</summary>
    public string? AssociatedApp { get; init; }

    public string? AssociatedAppKey { get; init; }

    /// <summary>Number of episodes the associated application took part in.</summary>
    public int AssociatedCount { get; init; }

    public required ConfidenceLevel Confidence { get; init; }

    /// <summary>When each episode started, oldest first.</summary>
    public IReadOnlyList<DateTimeOffset> Times { get; init; } = [];

    /// <summary>What the detection rests on (observed counts, inferred association).</summary>
    public IReadOnlyList<Finding> Findings { get; init; } = [];

    /// <summary>Where to look further.</summary>
    public DiagnosisAction Action { get; init; }
}

/// <summary>Recurring problems over the analyzed window.</summary>
/// <param name="Time">When the detection ran.</param>
/// <param name="HasEnoughHistory">False while the local history is too short to talk about recurrence.</param>
/// <param name="HistoryCovered">How far back the local history goes (capped to the window).</param>
/// <param name="Problems">Problems found, most frequent first.</param>
/// <param name="Summary">One sentence for the UI.</param>
public sealed record RecurringProblemReport(DateTimeOffset Time, bool HasEnoughHistory, TimeSpan HistoryCovered, IReadOnlyList<RecurringProblem> Problems, string Summary)
{
    /// <summary>Report used before the first detection or when the history cannot be read.</summary>
    public static RecurringProblemReport Unknown { get; } = new(DateTimeOffset.MinValue, false, TimeSpan.Zero, [], Strings.Recurring_NotAnalyzed);
}

/// <summary>
/// Finds problems that repeat over days (pure, deterministic). A pattern is reported only with enough data: at least
/// <see cref="MinimumOccurrences"/> episodes on at least <see cref="MinimumDays"/> different days, in a history covering
/// at least <see cref="MinimumHistory"/>. Otherwise it says so ("Not enough historical data.") rather than guessing.
/// </summary>
public static class RecurringProblemDetector
{
    /// <summary>Period analyzed.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>History needed before anything is called recurring.</summary>
    public static readonly TimeSpan MinimumHistory = TimeSpan.FromDays(2);

    public const int MinimumOccurrences = 3;

    public const int MinimumDays = 2;

    /// <summary>Episodes needed before a time-of-day pattern is described.</summary>
    public const int MinimumForTimePattern = 4;

    /// <summary>Share of episodes that must fall in the same 3-hour slot for a time-of-day pattern.</summary>
    public const double TimePatternShare = 0.6;

    /// <summary>An application event this long before an episode, or during it, counts as associated with it.</summary>
    public static readonly TimeSpan AssociationWindow = TimeSpan.FromMinutes(10);

    /// <summary>Connectivity losses closer than this belong to the same episode.</summary>
    private static readonly TimeSpan ConnectivityEpisodeGap = TimeSpan.FromMinutes(15);

    /// <param name="alerts">Alerts of the window (any status).</param>
    /// <param name="events">Timeline events of the window.</param>
    /// <param name="now">Time of the detection.</param>
    /// <param name="historyStart">Oldest data of the local history, or null when there is none.</param>
    /// <param name="zone">Time zone defining days and hours.</param>
    public static RecurringProblemReport Detect(
        IReadOnlyList<Alert> alerts,
        IReadOnlyList<SystemEvent> events,
        DateTimeOffset now,
        DateTimeOffset? historyStart,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(zone);

        var from = now - Window;
        var covered = historyStart is { } start ? now - (start > from ? start : from) : TimeSpan.Zero;
        if (covered < MinimumHistory)
        {
            var have = covered <= TimeSpan.Zero
                ? Strings.Recurring_NoHistoryYet
                : Text.Format(Strings.Recurring_OfHistory, MetricFormatter.DurationCompact(covered));
            return new RecurringProblemReport(now, false, covered < TimeSpan.Zero ? TimeSpan.Zero : covered, [],
                Text.Format(Strings.Recurring_NotEnough, have, Text.Plural((int)MinimumHistory.TotalDays, Strings.Duration_Day_One, Strings.Duration_Day_Other)));
        }

        var appEvents = events
            .Where(e => e.AppKey is not null && e.Kind is SystemEventKind.AppHighCpu or SystemEventKind.AppHighMemory && e.Timestamp >= from)
            .ToArray();
        var problems = new List<RecurringProblem>();
        foreach (var family in Families(alerts.Where(a => a.RaisedAt >= from && a.RaisedAt <= now)))
        {
            if (Build(family, appEvents, zone) is { } problem)
            {
                problems.Add(problem);
            }
        }

        if (Build(ConnectivityFamily(events.Where(e => e.Timestamp >= from && e.Timestamp <= now)), [], zone) is { } network)
        {
            problems.Add(network);
        }

        var ordered = problems
            .OrderByDescending(p => p.Confidence)
            .ThenByDescending(p => p.Occurrences)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToArray();
        var summary = ordered.Length switch
        {
            0 => Text.Format(Strings.Recurring_None, (int)Window.TotalDays),
            1 => Text.Format(Strings.Recurring_One, ordered[0].Description),
            _ => Text.Format(Strings.Recurring_Many, ordered.Length, ordered[0].Description),
        };
        return new RecurringProblemReport(now, true, covered, ordered, summary);
    }

    /// <summary>Name of the application an alert is about ("chrome.exe"), from the alert itself.</summary>
    public static string? AppName(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (alert.AppKey is null)
        {
            return null;
        }

        if (alert.AppName is { Length: > 0 } name)
        {
            return name;
        }

        // Alerts recorded before the name was stored: their CPU evidence is named after the application, in the
        // language of that time ("chrome.exe CPU", "Processeur de chrome.exe").
        foreach (var evidence in alert.Evidence)
        {
            if (evidence.Metric.EndsWith(" CPU", StringComparison.Ordinal))
            {
                return evidence.Metric[..^4];
            }

            if (evidence.Metric.StartsWith(LegacyFrenchCpuPrefix, StringComparison.Ordinal))
            {
                return evidence.Metric[LegacyFrenchCpuPrefix.Length..];
            }
        }

        return NameFromKey(alert.AppKey);
    }

    private const string LegacyFrenchCpuPrefix = "Processeur de ";

    private static IEnumerable<Family> Families(IEnumerable<Alert> alerts)
    {
        foreach (var group in alerts.GroupBy(FamilyKey, StringComparer.Ordinal))
        {
            if (group.Key.Length == 0)
            {
                continue;
            }

            var first = group.First();
            var (kind, title, action) = Describe(first);
            var episodes = group
                .OrderBy(a => a.RaisedAt)
                .Select(a => new Episode(a.RaisedAt, a.ResolvedAt ?? a.UpdatedAt, a.AppKey, RecurringProblemDetector.AppName(a)))
                .ToArray();
            yield return new Family(group.Key, kind, title, action, episodes, first.RuleId == "app.cpu" ? first.AppKey : null);
        }
    }

    /// <summary>Alerts of the same rule (and, for per-application alerts, the same application) form a family.</summary>
    private static string FamilyKey(Alert alert) => alert.RuleId switch
    {
        "cpu.sustained" or "memory.sustained" or "memory.growth" or "disk.busy" => alert.RuleId,
        "app.cpu" when alert.AppKey is not null => $"app.cpu:{alert.AppKey}",
        "unusual" => alert.Key,
        // A lasting state (low disk space) is one long problem, not a recurring one.
        _ => string.Empty,
    };

    private static (RecurringProblemKind Kind, string Title, DiagnosisAction Action) Describe(Alert alert) => alert.RuleId switch
    {
        "cpu.sustained" => (RecurringProblemKind.HighCpu, Strings.Recurring_HighCpu, DiagnosisAction.Diagnosis),
        "memory.sustained" => (RecurringProblemKind.HighMemory, Strings.Recurring_HighMemory, DiagnosisAction.AppImpact),
        "memory.growth" => (RecurringProblemKind.MemoryGrowth, Strings.Recurring_MemoryRising, DiagnosisAction.AppImpact),
        "disk.busy" => (RecurringProblemKind.DiskBusy, Strings.Recurring_DiskBusy, DiagnosisAction.Replay),
        "app.cpu" => (RecurringProblemKind.ApplicationCpu, Text.Format(Strings.Recurring_AppCpu, AppName(alert)), DiagnosisAction.AppImpact),
        _ => (RecurringProblemKind.UnusualActivity, alert.Title, DiagnosisAction.Diagnosis),
    };

    /// <summary>Losses of Internet access reported by Windows, merged into episodes.</summary>
    private static Family ConnectivityFamily(IEnumerable<SystemEvent> events)
    {
        var episodes = new List<Episode>();
        foreach (var loss in events.Where(SystemEventDetector.IsConnectivityLoss).OrderBy(e => e.Timestamp))
        {
            if (episodes.Count > 0 && loss.Timestamp - episodes[^1].End <= ConnectivityEpisodeGap)
            {
                episodes[^1] = episodes[^1] with { End = loss.Timestamp };
                continue;
            }

            episodes.Add(new Episode(loss.Timestamp, loss.Timestamp, null, null));
        }

        return new Family("network.connectivity", RecurringProblemKind.ConnectivityLoss, Strings.Recurring_Connectivity, DiagnosisAction.Network, episodes, null);
    }

    private static RecurringProblem? Build(Family family, IReadOnlyList<SystemEvent> appEvents, TimeZoneInfo zone)
    {
        var episodes = family.Episodes;
        var days = episodes.Select(e => TimeZoneInfo.ConvertTime(e.Start, zone).Date).Distinct().Count();
        if (episodes.Count < MinimumOccurrences || days < MinimumDays)
        {
            return null;
        }

        var confidence = episodes.Count >= 5 && days >= 3 ? ConfidenceLevel.High : ConfidenceLevel.Medium;
        var findings = new List<Finding>
        {
            new(Strings.Recurring_Occurrences, Text.Format(Strings.Recurring_OccurrencesText, Text.Plural(episodes.Count, Strings.Count_Episode_One, Strings.Count_Episode_Other), Text.Plural(days, Strings.Duration_Day_One, Strings.Duration_Day_Other), Stamp(episodes[0].Start, zone), Stamp(episodes[^1].Start, zone)), FindingBasis.Observed),
        };

        var pattern = TimePattern(episodes, zone);
        if (pattern is not null)
        {
            findings.Add(new Finding(Strings.Recurring_When, pattern, FindingBasis.Observed));
        }

        var (app, appKey, count) = family.FixedAppKey is { } fixedKey
            ? (episodes[0].AppName, fixedKey, episodes.Count)
            : Associated(episodes, appEvents);
        if (family.FixedAppKey is null && app is not null)
        {
            findings.Add(new Finding(Strings.Recurring_MostAssociated, Text.Format(Strings.Recurring_MostAssociatedText, app, count, episodes.Count), FindingBasis.Inferred)
            {
                Confidence = count * 1.0 / episodes.Count >= 0.75 ? ConfidenceLevel.High : ConfidenceLevel.Medium,
            });
        }

        if (family.Kind == RecurringProblemKind.ConnectivityLoss)
        {
            findings.Add(new Finding(Strings.Recurring_Cause, Strings.Recurring_CauseConnectivity, FindingBasis.Unknown));
        }

        return new RecurringProblem
        {
            Key = family.Key,
            Kind = family.Kind,
            Title = family.Title,
            Description = Text.Format(Strings.Recurring_Description, family.Title, Times(episodes.Count), (int)Window.TotalDays),
            Occurrences = episodes.Count,
            Days = days,
            First = episodes[0].Start,
            Last = episodes[^1].Start,
            TimePattern = pattern,
            AssociatedApp = family.FixedAppKey is null ? app : null,
            AssociatedAppKey = appKey,
            AssociatedCount = family.FixedAppKey is null ? count : 0,
            Confidence = confidence,
            Times = episodes.Select(e => e.Start).ToArray(),
            Findings = findings,
            Action = family.Action,
        };
    }

    /// <summary>"Most events happened between 19:00 and 22:00 (4 of 6)" when at least 60% fall in one 3-hour slot.</summary>
    private static string? TimePattern(IReadOnlyList<Episode> episodes, TimeZoneInfo zone)
    {
        if (episodes.Count < MinimumForTimePattern)
        {
            return null;
        }

        var hours = new int[24];
        foreach (var episode in episodes)
        {
            hours[TimeZoneInfo.ConvertTime(episode.Start, zone).Hour]++;
        }

        var bestStart = 0;
        var best = -1;
        for (var start = 0; start < 24; start++)
        {
            var count = hours[start] + hours[(start + 1) % 24] + hours[(start + 2) % 24];
            if (count > best)
            {
                best = count;
                bestStart = start;
            }
        }

        if (best < episodes.Count * TimePatternShare)
        {
            return null;
        }

        // Narrow the slot to the hours that actually have episodes ("between 20:00 and 21:00", not 18:00–21:00).
        var offsets = Enumerable.Range(0, 3).Where(offset => hours[(bestStart + offset) % 24] > 0).ToArray();
        var first = (bestStart + offsets[0]) % 24;
        var last = (bestStart + offsets[^1] + 1) % 24;
        return Text.Format(Strings.Recurring_Pattern, first, last, best, episodes.Count);
    }

    /// <summary>The application seen in at least half of the episodes (and in two at least), from the alerts and the application events around them.</summary>
    private static (string? Name, string? Key, int Count) Associated(IReadOnlyList<Episode> episodes, IReadOnlyList<SystemEvent> appEvents)
    {
        var counts = new Dictionary<string, (string Name, int Count)>(StringComparer.Ordinal);
        foreach (var episode in episodes)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            if (episode.AppKey is { } key)
            {
                seen[key] = episode.AppName ?? key;
            }

            foreach (var appEvent in appEvents)
            {
                if (appEvent.Timestamp >= episode.Start - AssociationWindow && appEvent.Timestamp <= episode.End)
                {
                    seen.TryAdd(appEvent.AppKey!, EventAppName(appEvent));
                }
            }

            foreach (var (appKey, name) in seen)
            {
                counts[appKey] = counts.TryGetValue(appKey, out var existing) ? (existing.Name, existing.Count + 1) : (name, 1);
            }
        }

        var top = counts.OrderByDescending(c => c.Value.Count).ThenBy(c => c.Key, StringComparer.Ordinal).FirstOrDefault();
        return top.Key is not null && top.Value.Count >= 2 && top.Value.Count * 2 >= episodes.Count
            ? (top.Value.Name, top.Key, top.Value.Count)
            : (null, null, 0);
    }

    /// <summary>
    /// The application an event is about, from its key ("path:C:\TOOLS\BUILD.EXE" → "build.exe"): event titles are
    /// translated, so they are never parsed.
    /// </summary>
    private static string EventAppName(SystemEvent systemEvent) =>
        systemEvent.AppKey is { } key ? NameFromKey(key) : systemEvent.Title;

    private static string NameFromKey(string appKey)
    {
        var separator = appKey.IndexOf(':', StringComparison.Ordinal);
        var identity = separator >= 0 ? appKey[(separator + 1)..] : appKey;
        return Path.GetFileName(identity).ToLowerInvariant();
    }

    private static string Times(int count) => count switch
    {
        1 => Strings.Times_Once,
        2 => Strings.Times_Twice,
        _ => Text.Format(Strings.Times_N, count),
    };

    private static string Stamp(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone).ToString("ddd HH:mm", CultureInfo.CurrentCulture);

    private sealed record Episode(DateTimeOffset Start, DateTimeOffset End, string? AppKey, string? AppName);

    private sealed record Family(string Key, RecurringProblemKind Kind, string Title, DiagnosisAction Action, IReadOnlyList<Episode> Episodes, string? FixedAppKey);
}

/// <summary>
/// Detects recurring problems from the local history on demand, caching the result for a few minutes (the history of
/// alerts changes slowly; there is no reason to read a week of data more often).
/// </summary>
public sealed class RecurringProblemService(IHistoryRepository repository, TimeProvider? timeProvider = null)
{
    /// <summary>A result younger than this is reused.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private const int MaxEvents = 5000;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RecurringProblemReport _latest = RecurringProblemReport.Unknown;

    /// <summary>The latest result (may be <see cref="RecurringProblemReport.Unknown"/>).</summary>
    public RecurringProblemReport Latest => Volatile.Read(ref _latest);

    /// <summary>Recurring problems of the last seven days; reuses a recent result unless <paramref name="force"/>.</summary>
    public async Task<RecurringProblemReport> GetAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (!force && Latest is { } cached && cached.Time != DateTimeOffset.MinValue && now - cached.Time < CacheDuration)
            {
                return cached;
            }

            var from = now - RecurringProblemDetector.Window;
            var alerts = await repository.GetAlertsAsync(from, cancellationToken).ConfigureAwait(false);
            var events = await repository.GetEventsAsync(from, now, MaxEvents, cancellationToken).ConfigureAwait(false);
            var storage = await repository.GetStorageInfoAsync(cancellationToken).ConfigureAwait(false);
            var report = RecurringProblemDetector.Detect(alerts, events, now, storage.OldestData, TimeZoneInfo.Local);
            Volatile.Write(ref _latest, report);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }
}
