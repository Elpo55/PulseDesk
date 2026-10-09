using System.Globalization;
using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.Interfaces;
using Sysora.Localization;

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
        Headline = Strings.Since_Loading,
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
    private static string Applications => Strings.Since_Area_Applications;

    private static string Startup => Strings.Since_Area_Startup;

    private static string Windows => Strings.Since_Area_Windows;

    private static string Firmware => Strings.Since_Area_Firmware;

    private static string Memory => Strings.Since_Area_Memory;

    private static string Devices => Strings.Since_Area_Devices;

    private static string DiskSpace => Strings.Since_Area_DiskSpace;

    private static string Usage => Strings.Since_Area_Usage;

    private static string Alerts => Strings.Since_Area_Alerts;

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
                Headline = Strings.Since_NotEnough,
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
                    Title = Text.Format(Strings.Since_Usage_Title, row.Name, row.ChangeText),
                    OldValue = Text.Format(Strings.Since_Yesterday, row.BeforeText),
                    NewValue = Text.Format(Strings.Since_Today, row.AfterText),
                    Importance = row.Importance,
                    Explanation = Text.Format(
                        Strings.Since_Usage_Explanation,
                        row.Explanation.Replace(Strings.Since_SecondPeriodPhrase, Strings.Since_TodayPhrase, StringComparison.Ordinal)),
                    When = Strings.Since_When,
                    Origin = Text.Format(Strings.Since_Usage_Origin, behavior.Before.Source, behavior.After.Source.ToLower(CultureInfo.CurrentCulture)),
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
                Title = more
                    ? Text.Format(Strings.Since_MoreAlerts, today.Length)
                    : Text.Format(Strings.Since_FewerAlerts, today.Length),
                OldValue = Text.Format(Strings.Since_Yesterday, Text.Plural(yesterday, Strings.Count_Alert_One, Strings.Count_Alert_Other)),
                NewValue = Text.Format(Strings.Since_Today, Text.Plural(today.Length, Strings.Count_Alert_One, Strings.Count_Alert_Other)),
                Importance = more && today.Any(a => a.Severity == AlertSeverity.Critical) ? ChangeImportance.High : more ? ChangeImportance.Medium : ChangeImportance.Low,
                Explanation = more
                    ? Strings.Since_MoreAlerts_Explanation
                    : Strings.Since_FewerAlerts_Explanation,
                When = Strings.Since_When,
                Origin = Strings.Since_AlertsOrigin,
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
                notCompared.Add(Text.Format(Strings.Common_NameValue, area, reason));
            }
        }

        Area(Applications, reference.Inventory.AppsAvailable, Strings.Since_Reason_Apps);
        Area(Startup, reference.Inventory.StartupAvailable, Strings.Since_Reason_Startup);
        Area(Windows, reference.OsBuild is not null, Strings.Since_Reason_Windows);
        Area(Firmware, reference.BiosVersion is not null, Strings.Since_Reason_Bios);
        Area(Memory, reference.InstalledMemoryBytes is not null, Strings.Since_Reason_Memory);
        Area(Devices, true, string.Empty);
        Area(DiskSpace, reference.Inventory.Devices.Any(d => d.Category == "Volume"), Strings.Since_Reason_Volumes);
        Area(Usage, behaviorKnown, Strings.Since_Reason_Usage);
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
            ? Strings.Since_NoChange
            : string.Join(" · ", new[]
            {
                significant > 0 ? Text.Plural(significant, Strings.Count_SignificantChange_One, Strings.Count_SignificantChange_Other) : null,
                minor > 0 ? Text.Plural(minor, Strings.Count_MinorChange_One, Strings.Count_MinorChange_Other) : null,
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
            Note = Text.Format(Strings.Since_Note, reference.CapturedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
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
            ? Text.Format(Strings.Since_Between, after.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), change.Before.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : Text.Format(Strings.Since_Before, change.Before.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
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
