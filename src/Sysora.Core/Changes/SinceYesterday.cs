using System.Globalization;
using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.Interfaces;

namespace Sysora.Core.Changes;

/// <summary>One thing that changed since yesterday.</summary>
public sealed record SinceYesterdayItem
{
    /// <summary>Area of the PC, e.g. "Applications", "Usage".</summary>
    public required string Area { get; init; }

    public required string Title { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public required ChangeImportance Importance { get; init; }

    /// <summary>What the change means.</summary>
    public required string Explanation { get; init; }

    /// <summary>When it happened, as precisely as known ("between 09:12 and 15:12").</summary>
    public required string When { get; init; }

    /// <summary>Where the information comes from.</summary>
    public required string Origin { get; init; }

    public required ConfidenceLevel Confidence { get; init; }

    /// <summary>The change is dated within this period, when known.</summary>
    public DateTimeOffset? After { get; init; }

    public DateTimeOffset? Before { get; init; }

    public DiagnosisAction Action { get; init; }

    public string? AppKey { get; init; }

    /// <summary>A change that matters (high importance) rather than a minor one.</summary>
    public bool IsSignificant => Importance == ChangeImportance.High;
}

/// <summary>"What changed since yesterday?" in a few lines: counts, the changes, and the areas that did not change.</summary>
public sealed record SinceYesterdaySummary
{
    public required DateTimeOffset Time { get; init; }

    /// <summary>False when there is no snapshot from yesterday to compare with.</summary>
    public required bool HasReference { get; init; }

    /// <summary>Time of the reference snapshot.</summary>
    public DateTimeOffset? ReferenceTime { get; init; }

    public int Significant { get; init; }

    public int Minor { get; init; }

    /// <summary>Changes, significant first.</summary>
    public IReadOnlyList<SinceYesterdayItem> Items { get; init; } = [];

    /// <summary>Areas compared and found unchanged.</summary>
    public IReadOnlyList<string> UnchangedAreas { get; init; } = [];

    /// <summary>Areas that could not be compared, with the reason.</summary>
    public IReadOnlyList<string> NotCompared { get; init; } = [];

    /// <summary>True when most of the areas compared did not change.</summary>
    public bool MostlyUnchanged { get; init; }

    /// <summary>"1 significant change · 3 minor changes".</summary>
    public required string Headline { get; init; }

    /// <summary>Limits of the comparison.</summary>
    public required string Note { get; init; }

    public static SinceYesterdaySummary Loading { get; } = new()
    {
        Time = DateTimeOffset.MinValue,
        HasReference = false,
        Headline = "Comparing with yesterday…",
        Note = string.Empty,
    };
}

/// <summary>
/// Builds the "since yesterday" summary (pure, deterministic) from the comparison with yesterday's snapshot, today's
/// activity compared with yesterday's, and the alerts of both days. Only differences actually observed are listed; an
/// area that could not be compared is never called unchanged.
/// </summary>
public static class SinceYesterdayBuilder
{
    private const string Applications = "Applications";
    private const string Startup = "Startup programs";
    private const string Windows = "Windows version";
    private const string Firmware = "Firmware (BIOS)";
    private const string Memory = "Installed memory";
    private const string Devices = "Devices";
    private const string DiskSpace = "Disk space";
    private const string Usage = "Usage";
    private const string Alerts = "Alerts";

    /// <param name="inventory">Current state compared with yesterday's snapshot.</param>
    /// <param name="behavior">Today's activity compared with yesterday's, when computed.</param>
    /// <param name="alerts">Alerts of at least the last two days.</param>
    /// <param name="now">Current time.</param>
    /// <param name="zone">Time zone defining "today" and "yesterday".</param>
    public static SinceYesterdaySummary Build(ChangeComparison inventory, StateComparisonResult? behavior, IReadOnlyList<Alert> alerts, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(zone);
        if (inventory.ReferenceSnapshot is not { } reference)
        {
            return new SinceYesterdaySummary
            {
                Time = now,
                HasReference = false,
                Headline = "Not enough history yet",
                Note = inventory.Note,
            };
        }

        var current = inventory.Changes;
        var items = new List<SinceYesterdayItem>();
        var behaviorKnown = behavior is { Before.HasData: true, After.HasData: true };
        foreach (var change in current)
        {
            // Today's activity is compared directly below; the 24-hour averages of the snapshots would repeat it.
            if (behaviorKnown && change.Type is ChangeType.CpuUsageChanged or ChangeType.MemoryUsageChanged)
            {
                continue;
            }

            items.Add(FromChange(change));
        }

        if (behaviorKnown)
        {
            var thin = behavior!.After.To - behavior.After.From < TimeSpan.FromHours(1);
            foreach (var row in behavior.Rows.Where(r => r.IsComparable && r.Importance >= ChangeImportance.Medium
                && r.Metric is not (StateMetric.SystemDriveFree or StateMetric.ProcessCount)))
            {
                items.Add(new SinceYesterdayItem
                {
                    Area = Usage,
                    Title = $"{row.Name} {row.ChangeText} today",
                    OldValue = $"{row.BeforeText} yesterday",
                    NewValue = $"{row.AfterText} today",
                    Importance = row.Importance,
                    Explanation = $"{row.Explanation.Replace("in the second period", "today", StringComparison.Ordinal)} Averages over each day.",
                    When = "Today so far, compared with yesterday",
                    Origin = $"{behavior.Before.Source} (yesterday) and {behavior.After.Source.ToLowerInvariant()} (today).",
                    Confidence = thin ? ConfidenceLevel.Low : ConfidenceLevel.Medium,
                    Action = DiagnosisAction.Compare,
                });
            }
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var midnight = new DateTimeOffset(local.Date, zone.GetUtcOffset(local.Date));
        var today = alerts.Where(a => a.RaisedAt >= midnight && a.RaisedAt <= now).ToArray();
        var yesterday = alerts.Count(a => a.RaisedAt >= midnight.AddDays(-1) && a.RaisedAt < midnight);
        if (Math.Abs(today.Length - yesterday) >= 2)
        {
            var more = today.Length > yesterday;
            items.Add(new SinceYesterdayItem
            {
                Area = Alerts,
                Title = more ? $"More alerts today ({today.Length})" : $"Fewer alerts today ({today.Length})",
                OldValue = $"{MetricFormatter.Plural(yesterday, "alert")} yesterday",
                NewValue = $"{MetricFormatter.Plural(today.Length, "alert")} today",
                Importance = more && today.Any(a => a.Severity == AlertSeverity.Critical) ? ChangeImportance.High : more ? ChangeImportance.Medium : ChangeImportance.Low,
                Explanation = more ? "More lasting or unusual problems were detected today than yesterday." : "Fewer problems were detected today than yesterday.",
                When = "Today so far, compared with yesterday",
                Origin = "Alerts recorded by Sysora.",
                Confidence = ConfidenceLevel.High,
                Action = DiagnosisAction.Alerts,
            });
        }

        // Areas compared: an area is "unchanged" only when both snapshots had the data to compare it.
        var compared = new List<string>();
        var notCompared = new List<string>();
        void Area(string area, bool available, string reason)
        {
            if (available)
            {
                compared.Add(area);
            }
            else
            {
                notCompared.Add($"{area}: {reason}");
            }
        }

        Area(Applications, reference.Inventory.AppsAvailable, "the list of installed applications could not be read in one of the snapshots");
        Area(Startup, reference.Inventory.StartupAvailable, "the startup programs could not be read in one of the snapshots");
        Area(Windows, reference.OsBuild is not null, "the Windows build was not recorded");
        Area(Firmware, reference.BiosVersion is not null, "the BIOS version is not reported by this PC");
        Area(Memory, reference.InstalledMemoryBytes is not null, "the installed memory is not reported by the firmware");
        Area(Devices, true, string.Empty);
        Area(DiskSpace, reference.Inventory.Devices.Any(d => d.Category == "Volume"), "no volume was recorded");
        Area(Usage, behaviorKnown, "not enough history for today or yesterday");
        Area(Alerts, true, string.Empty);

        var changedAreas = items.Select(i => i.Area).ToHashSet(StringComparer.Ordinal);
        var unchanged = compared.Where(a => !changedAreas.Contains(a)).ToArray();
        var ordered = items
            .OrderByDescending(i => i.Importance)
            .ThenBy(i => i.Area, StringComparer.Ordinal)
            .ThenBy(i => i.Title, StringComparer.CurrentCulture)
            .ToArray();
        var significant = ordered.Count(i => i.IsSignificant);
        var minor = ordered.Length - significant;
        var mostlyUnchanged = compared.Count > 0 && unchanged.Length >= compared.Count * 0.6;
        var headline = ordered.Length == 0
            ? "No change since yesterday"
            : string.Join(" · ", new[]
            {
                significant > 0 ? MetricFormatter.Plural(significant, "significant change") : null,
                minor > 0 ? MetricFormatter.Plural(minor, "minor change") : null,
            }.Where(p => p is not null));
        return new SinceYesterdaySummary
        {
            Time = now,
            HasReference = true,
            ReferenceTime = reference.CapturedAt,
            Significant = significant,
            Minor = minor,
            Items = ordered,
            UnchangedAreas = unchanged,
            NotCompared = notCompared,
            MostlyUnchanged = mostlyUnchanged,
            Headline = headline,
            Note = $"Compared with the snapshot of {reference.CapturedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}. Only differences Sysora observed are listed; their origin is stated when it is known.",
        };
    }

    private static SinceYesterdayItem FromChange(DetectedChange change) => new()
    {
        Area = change.Type switch
        {
            ChangeType.AppInstalled or ChangeType.AppRemoved or ChangeType.AppUpdated or ChangeType.NewFrequentApp => Applications,
            ChangeType.StartupProgramAdded or ChangeType.StartupProgramRemoved or ChangeType.StartupProgramEnabled or ChangeType.StartupProgramDisabled => Startup,
            ChangeType.WindowsUpdated => Windows,
            ChangeType.FirmwareUpdated => Firmware,
            ChangeType.MemoryChanged => Memory,
            ChangeType.DeviceAdded or ChangeType.DeviceRemoved => Devices,
            ChangeType.DiskSpaceChanged => DiskSpace,
            _ => Usage,
        },
        Title = change.Title,
        OldValue = change.OldValue,
        NewValue = change.NewValue,
        Importance = change.Importance,
        Explanation = change.Explanation,
        When = change.After is { } after
            ? $"Between {after.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} and {change.Before.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}"
            : $"Before {change.Before.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}",
        Origin = change.Origin,
        Confidence = change.Type is ChangeType.CpuUsageChanged or ChangeType.MemoryUsageChanged or ChangeType.NewFrequentApp ? ConfidenceLevel.Medium : ConfidenceLevel.High,
        After = change.After,
        Before = change.Before,
        Action = change.Action,
        AppKey = change.AppKey,
    };
}

/// <summary>
/// Computes the "since yesterday" summary on demand and keeps it for half an hour: it compares snapshots of the PC
/// (installed applications, Store packages…), which are worth reading again only rarely. "Check now" forces it.
/// </summary>
public sealed class SinceYesterdayService(
    IChangeDetectionService changes,
    StateComparisonService comparisons,
    AlertService alerts,
    TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SinceYesterdaySummary _latest = SinceYesterdaySummary.Loading;

    public SinceYesterdaySummary Latest => Volatile.Read(ref _latest);

    public async Task<SinceYesterdaySummary> GetAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (!force && Latest is { } cached && cached.Time != DateTimeOffset.MinValue && now - cached.Time < CacheDuration)
            {
                return cached;
            }

            var inventory = await changes.CompareAsync(BaselineReference.Yesterday, cancellationToken).ConfigureAwait(false);
            StateComparisonResult? behavior = null;
            try
            {
                behavior = await comparisons.CompareAsync(new ComparisonRequest(ComparisonPreset.TodayVsYesterday), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // Without the stored history, the inventory changes are still listed.
            }

            var summary = SinceYesterdayBuilder.Build(inventory, behavior, alerts.Alerts, now, TimeZoneInfo.Local);
            Volatile.Write(ref _latest, summary);
            return summary;
        }
        finally
        {
            _gate.Release();
        }
    }
}
