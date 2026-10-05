using System.Globalization;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Diagnosis;
using PulseDesk.Core.Formatting;

namespace PulseDesk.Core.Changes;

/// <summary>An application PulseDesk has recorded, with when it was first and last seen running.</summary>
/// <param name="Key">Application key (see <see cref="AppIdentity"/>).</param>
/// <param name="Name">Image name.</param>
/// <param name="ExecutablePath">Executable path, when known.</param>
/// <param name="FirstSeen">First time it was recorded.</param>
/// <param name="LastSeen">Last time it was recorded.</param>
public sealed record KnownApp(string Key, string Name, string? ExecutablePath, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

/// <summary>
/// Finds applications that recently started running regularly. An application only counts as "new" when
/// PulseDesk was already recording before it first appeared: on a new installation everything would
/// otherwise look new.
/// </summary>
public static class FrequentAppDetector
{
    /// <summary>Applications first seen within this period are examined.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>PulseDesk must have been recording for at least this long before an application first appeared.</summary>
    public static readonly TimeSpan MinimumPriorHistory = TimeSpan.FromDays(1);

    /// <summary>Running time since first seen from which an application counts as running regularly.</summary>
    public static readonly TimeSpan MinimumActive = TimeSpan.FromMinutes(30);

    /// <summary>Number of process starts from which an application counts as running regularly.</summary>
    public const int MinimumLaunches = 5;

    public static IReadOnlyList<DetectedChange> Detect(IReadOnlyList<KnownApp> catalog, IReadOnlyList<AppUsageStatistics> recentUsage, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(recentUsage);
        if (catalog.Count == 0)
        {
            return [];
        }

        var recordingSince = catalog.Min(a => a.FirstSeen);
        var usage = recentUsage.ToDictionary(u => u.Identity.Key, StringComparer.Ordinal);
        var changes = new List<DetectedChange>();
        foreach (var app in catalog.Where(a => a.FirstSeen >= now - Window && a.FirstSeen - recordingSince >= MinimumPriorHistory))
        {
            if (!usage.TryGetValue(app.Key, out var stats)
                || (stats.ActiveSeconds < MinimumActive.TotalSeconds && stats.Launches < MinimumLaunches))
            {
                continue;
            }

            var firstSeen = app.FirstSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            var activity = $"Running {MetricFormatter.DurationCompact(TimeSpan.FromSeconds(stats.ActiveSeconds))}, {MetricFormatter.Plural(stats.Launches, "start")} since {firstSeen}";
            changes.Add(new DetectedChange
            {
                Id = BaselineComparer.ChangeId(ChangeType.NewFrequentApp, app.Key),
                Type = ChangeType.NewFrequentApp,
                DetectedAt = now,
                After = app.FirstSeen,
                Before = now,
                Subject = app.Name,
                Title = $"{app.Name} now runs regularly",
                NewValue = activity,
                Importance = ChangeImportance.Medium,
                Explanation = "A program PulseDesk had not seen before started running regularly. It can be a new application, an update helper or a background task.",
                Origin = $"First seen by PulseDesk on {firstSeen}; PulseDesk has been recording since {recordingSince.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}.",
                Evidence =
                [
                    new AnalysisEvidence("Activity", activity)
                    {
                        Source = app.ExecutablePath ?? "Identified by process name only",
                        From = app.FirstSeen,
                        To = now,
                    },
                ],
                AppKey = app.Key,
                Action = DiagnosisAction.AppImpact,
            });
        }

        return changes;
    }
}
