using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Localization;

namespace Sysora.Core.Changes;

/// <summary>
/// Compares two snapshots of the PC and lists what changed (pure and deterministic). Only differences
/// between the two snapshots are reported; when the cause of a change cannot be known, the change says so.
/// </summary>
public static partial class BaselineComparer
{
    /// <summary>Text used when Sysora cannot tell what caused a change.</summary>
    public static string UnknownOrigin => Strings.Changes_UnknownOrigin;

    /// <summary>Minimum change of used space reported, in addition to <see cref="DiskChangeShare"/>.</summary>
    public const ulong MinimumDiskChangeBytes = 5UL * 1024 * 1024 * 1024;

    /// <summary>Minimum change of used space as a share of the volume.</summary>
    public const double DiskChangeShare = 0.02;

    /// <summary>Minimum change of average usage, in percentage points.</summary>
    public const double UsageChangePoints = 10;

    /// <summary>Minimum history behind each average before usage is compared.</summary>
    public const int MinimumUsageMinutes = 120;

    public static IReadOnlyList<DetectedChange> Compare(SystemBaseline older, SystemBaseline newer)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);
        var changes = new List<DetectedChange>();
        var period = new Period(older.CapturedAt, newer.CapturedAt);

        if (older.Inventory.AppsAvailable && newer.Inventory.AppsAvailable)
        {
            CompareApps(older.Inventory.Apps, newer.Inventory.Apps, period, changes);
        }

        if (older.Inventory.StartupAvailable && newer.Inventory.StartupAvailable)
        {
            CompareStartup(older.Inventory.StartupPrograms, newer.Inventory.StartupPrograms, period, changes);
        }

        CompareSystem(older, newer, period, changes);
        CompareDevices(older.Inventory.Devices, newer.Inventory.Devices, period, changes);
        var installed = changes.Where(c => c.Type == ChangeType.AppInstalled).Select(c => c.Subject).ToArray();
        CompareDiskSpace(older.Inventory.Devices, newer.Inventory.Devices, period, installed, changes);
        CompareUsage(older.Usage, newer.Usage, period, installed, changes);
        return changes;
    }

    /// <summary>
    /// Desktop applications whose install date (recorded by their installer) falls in the last <paramref name="days"/>
    /// days. Available from the very first snapshot: these dates come from Windows, not from Sysora. Many installers
    /// rewrite the date when they update an application, so these are reported as "installed or updated". Store apps are
    /// left out: their date is the date of their latest update, not of their installation.
    /// </summary>
    public static IReadOnlyList<DetectedChange> RecentlyInstalled(SystemBaseline current, int days)
    {
        ArgumentNullException.ThrowIfNull(current);
        var today = DateOnly.FromDateTime(current.CapturedAt.ToLocalTime().Date);
        return current.Inventory.Apps
            .Where(a => a.Source != "Store" && a.InstallDate is { } date && date >= today.AddDays(-days) && date <= today)
            .Select(a => Installed(a, period: null, current.CapturedAt) with
            {
                Title = Text.Format(Strings.Changes_InstalledOrUpdated, a.Name),
                Importance = ChangeImportance.Low,
                Explanation = Strings.Changes_InstalledOrUpdated_Explanation,
            })
            .ToArray();
    }

    /// <summary>Deterministic identifier of a change, so that it is recorded only once.</summary>
    public static string ChangeId(ChangeType type, params string?[] parts)
    {
        var text = string.Join('|', parts.Prepend(type.ToString()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 12);
    }

    private static void CompareApps(IReadOnlyList<InstalledApp> before, IReadOnlyList<InstalledApp> after, Period period, List<DetectedChange> changes)
    {
        var oldById = before.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var newById = after.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var added = newById.Values.Where(a => !oldById.ContainsKey(a.Id)).ToList();
        var removed = oldById.Values.Where(a => !newById.ContainsKey(a.Id)).ToList();

        // Installers often register a new version under a new key: the same product (same name without its
        // version number, same publisher) disappearing and appearing is an update, not a removal and an install.
        foreach (var app in added.ToArray())
        {
            var previous = removed.FirstOrDefault(r => SameProduct(r, app));
            if (previous is null)
            {
                continue;
            }

            added.Remove(app);
            removed.Remove(previous);
            changes.Add(Updated(previous, app, period, Strings.Changes_MatchedByName));
        }

        foreach (var app in newById.Values.Where(a => oldById.TryGetValue(a.Id, out var old) && !string.Equals(old.Version, a.Version, StringComparison.Ordinal)))
        {
            changes.Add(Updated(oldById[app.Id], app, period, Strings.Changes_VersionRead));
        }

        changes.AddRange(added.Select(a => Installed(a, period, period.To)));
        changes.AddRange(removed.Select(a => new DetectedChange
        {
            Id = ChangeId(ChangeType.AppRemoved, a.Id, a.Version, Day(period.To)),
            Type = ChangeType.AppRemoved,
            DetectedAt = period.To,
            After = period.From,
            Before = period.To,
            Subject = a.Name,
            Title = Text.Format(Strings.Changes_Removed, a.Name),
            OldValue = a.Version,
            Importance = ChangeImportance.Low,
            Explanation = Strings.Changes_Removed_Explanation,
            Origin = Text.Format(Strings.Changes_Removed_Origin, Date(period.From), Date(period.To)),
            Evidence = [Evidence(Strings.Changes_Ev_InstalledApps, a)],
        }));
    }

    private static DetectedChange Installed(InstalledApp app, Period? period, DateTimeOffset detectedAt)
    {
        // The installer's date is more precise than the snapshots when it falls inside the period.
        var date = app.InstallDate;
        var dayStart = date is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), detectedAt.ToLocalTime().Offset) : (DateTimeOffset?)null;
        var useDate = dayStart is { } start && (period is null || start.AddDays(1) > period.Value.From);
        var after = useDate ? dayStart : period?.From;
        var before = useDate && dayStart!.Value.AddDays(1) < detectedAt ? dayStart.Value.AddDays(1) : detectedAt;
        var origin = useDate
            ? Text.Format(Strings.Changes_Installed_OriginDate, date!.Value.ToString("d", CultureInfo.CurrentCulture))
            : Text.Format(Strings.Changes_Installed_OriginSnapshot, Date(period!.Value.From));

        return new DetectedChange
        {
            Id = ChangeId(ChangeType.AppInstalled, app.Id, date?.ToString("o", CultureInfo.InvariantCulture)),
            Type = ChangeType.AppInstalled,
            DetectedAt = detectedAt,
            After = after,
            Before = before,
            Subject = app.Name,
            Title = Text.Format(Strings.Changes_Installed, app.Name),
            NewValue = app.Version,
            Importance = ChangeImportance.Medium,
            Explanation = app.Publisher is { } publisher
                ? Text.Format(Strings.Changes_Installed_ExplanationPublisher, publisher)
                : Strings.Changes_Installed_Explanation,
            Origin = origin,
            Evidence = [Evidence(Strings.Changes_Ev_InstalledApps, app)],
            Action = DiagnosisAction.AppImpact,
        };
    }

    private static DetectedChange Updated(InstalledApp old, InstalledApp current, Period period, string origin) => new()
    {
        Id = ChangeId(ChangeType.AppUpdated, current.Name, old.Version, current.Version),
        Type = ChangeType.AppUpdated,
        DetectedAt = period.To,
        After = period.From,
        Before = period.To,
        Subject = current.Name,
        Title = Text.Format(Strings.Changes_Updated, current.Name),
        OldValue = old.Version ?? MetricFormatter.NotAvailable,
        NewValue = current.Version ?? MetricFormatter.NotAvailable,
        Importance = ChangeImportance.Low,
        Explanation = Strings.Changes_Updated_Explanation,
        Origin = origin,
        Evidence = [Evidence(Strings.State_Label_Before, old), Evidence(Strings.State_Label_After, current)],
    };

    private static void CompareStartup(IReadOnlyList<StartupProgram> before, IReadOnlyList<StartupProgram> after, Period period, List<DetectedChange> changes)
    {
        var oldById = before.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var newById = after.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var origin = Text.Format(Strings.Changes_Startup_Origin, Date(period.From), Date(period.To));

        foreach (var program in newById.Values)
        {
            if (!oldById.TryGetValue(program.Id, out var old))
            {
                changes.Add(Startup(ChangeType.StartupProgramAdded, program, period, Text.Format(Strings.Changes_Startup_New, program.Name), ChangeImportance.High,
                    Strings.Changes_Startup_New_Explanation, origin, null, program.Command));
            }
            else if (old.Enabled != program.Enabled && old.Enabled is not null && program.Enabled is not null)
            {
                var enabled = program.Enabled == true;
                changes.Add(Startup(enabled ? ChangeType.StartupProgramEnabled : ChangeType.StartupProgramDisabled, program, period,
                    enabled
                        ? Text.Format(Strings.Changes_Startup_Enabled, program.Name)
                        : Text.Format(Strings.Changes_Startup_Disabled, program.Name),
                    enabled ? ChangeImportance.Medium : ChangeImportance.Low,
                    enabled
                        ? Strings.Changes_Startup_Enabled_Explanation
                        : Strings.Changes_Startup_Removed_Explanation,
                    origin, EnabledText(old.Enabled == true), EnabledText(enabled), StateToken(old.Enabled == true), StateToken(enabled)));
            }
        }

        foreach (var program in oldById.Values.Where(p => !newById.ContainsKey(p.Id)))
        {
            changes.Add(Startup(ChangeType.StartupProgramRemoved, program, period, Text.Format(Strings.Changes_Startup_Removed, program.Name), ChangeImportance.Low,
                Strings.Changes_Startup_Removed_Explanation, origin, program.Command, null));
        }
    }

    private static string EnabledText(bool enabled) => enabled ? Strings.Changes_Enabled : Strings.Changes_Disabled;

    // Identifiers stay in English whatever the interface language, so a change is recorded once.
    private static string StateToken(bool enabled) => enabled ? "Enabled" : "Disabled";

    private static DetectedChange Startup(
        ChangeType type, StartupProgram program, Period period, string title, ChangeImportance importance, string explanation, string origin, string? oldValue, string? newValue,
        string? oldToken = null, string? newToken = null) => new()
    {
        Id = ChangeId(type, program.Id, oldToken ?? oldValue, newToken ?? newValue, Day(period.To)),
        Type = type,
        DetectedAt = period.To,
        After = period.From,
        Before = period.To,
        Subject = program.Name,
        Title = title,
        OldValue = oldValue,
        NewValue = newValue,
        Importance = importance,
        Explanation = explanation,
        Origin = origin,
        Evidence = [new AnalysisEvidence(Strings.Changes_Ev_StartupEntry, Text.Format(Strings.Common_NameValue, program.Name, program.Command)) { Source = program.Location }],
    };

    private static void CompareSystem(SystemBaseline older, SystemBaseline newer, Period period, List<DetectedChange> changes)
    {
        if (older.OsBuild is { } oldBuild && newer.OsBuild is { } newBuild && oldBuild != newBuild)
        {
            var oldText = Join(older.OsVersion, oldBuild);
            var newText = Join(newer.OsVersion, newBuild);
            changes.Add(System(ChangeType.WindowsUpdated, "Windows", "Windows", Strings.Changes_Windows_Title, oldText, newText, ChangeImportance.Medium,
                Strings.Changes_Windows_Explanation,
                period, Strings.Changes_Windows_Source));
        }

        if (older.BiosVersion is { } oldBios && newer.BiosVersion is { } newBios && oldBios != newBios)
        {
            changes.Add(System(ChangeType.FirmwareUpdated, "BIOS", "BIOS", Strings.Changes_Bios_Title, oldBios, newBios, ChangeImportance.Medium,
                Strings.Changes_Bios_Explanation, period, Strings.Changes_Bios_Source));
        }

        if (older.InstalledMemoryBytes is { } oldMemory && newer.InstalledMemoryBytes is { } newMemory && oldMemory != newMemory)
        {
            changes.Add(System(ChangeType.MemoryChanged, "Memory", Strings.Changes_Memory_Subject, Strings.Changes_Memory_Title, MetricFormatter.Bytes(oldMemory), MetricFormatter.Bytes(newMemory), ChangeImportance.High,
                Strings.Changes_Memory_Explanation, period, Strings.Changes_Memory_Source,
                oldMemory.ToString(CultureInfo.InvariantCulture), newMemory.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <param name="subjectId">Subject in the identifier of the change (never translated, so a change is recorded once).</param>
    private static DetectedChange System(
        ChangeType type, string subjectId, string subject, string title, string oldValue, string newValue, ChangeImportance importance, string explanation, Period period, string source,
        string? oldId = null, string? newId = null) => new()
    {
        Id = ChangeId(type, subjectId, oldId ?? oldValue, newId ?? newValue),
        Type = type,
        DetectedAt = period.To,
        After = period.From,
        Before = period.To,
        Subject = subject,
        Title = title,
        OldValue = oldValue,
        NewValue = newValue,
        Importance = importance,
        Explanation = explanation,
        Origin = Text.Format(Strings.Changes_System_Origin, Date(period.From), Date(period.To)),
        Evidence = [new AnalysisEvidence(subject, $"{oldValue} → {newValue}") { Source = source, From = period.From, To = period.To }],
    };

    private static void CompareDevices(IReadOnlyList<DeviceInfo> before, IReadOnlyList<DeviceInfo> after, Period period, List<DetectedChange> changes)
    {
        static string Key(DeviceInfo d) => $"{d.Category}|{d.Id}";
        var oldKeys = before.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newKeys = after.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var origin = Text.Format(Strings.Changes_Devices_Origin, Date(period.From), Date(period.To));

        foreach (var device in after.Where(d => !oldKeys.Contains(Key(d))))
        {
            changes.Add(Device(ChangeType.DeviceAdded, device, period, Text.Format(Strings.Changes_Device_New, DeviceCategoryText.Label(device.Category), DeviceCategoryText.Lower(device.Category), device.Name), origin));
        }

        foreach (var device in before.Where(d => !newKeys.Contains(Key(d))))
        {
            changes.Add(Device(ChangeType.DeviceRemoved, device, period, Text.Format(Strings.Changes_Device_Gone, DeviceCategoryText.Label(device.Category), DeviceCategoryText.Lower(device.Category), device.Name), origin));
        }
    }

    private static DetectedChange Device(ChangeType type, DeviceInfo device, Period period, string title, string origin) => new()
    {
        Id = ChangeId(type, device.Category, device.Id, Day(period.To)),
        Type = type,
        DetectedAt = period.To,
        After = period.From,
        Before = period.To,
        Subject = device.Name,
        Title = title,
        Importance = ChangeImportance.Medium,
        Explanation = type == ChangeType.DeviceAdded
            ? Strings.Changes_Device_New_Explanation
            : Strings.Changes_Device_Gone_Explanation,
        Origin = origin,
        Evidence = [new AnalysisEvidence(DeviceCategoryText.Label(device.Category), device.Name) { Reference = device.Id }],
    };

    private static void CompareDiskSpace(IReadOnlyList<DeviceInfo> before, IReadOnlyList<DeviceInfo> after, Period period, IReadOnlyList<string> installed, List<DetectedChange> changes)
    {
        var old = before.Where(d => d.Category == "Volume").ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var volume in after.Where(d => d.Category == "Volume"))
        {
            if (!old.TryGetValue(volume.Id, out var previous)
                || previous.UsedBytes is not { } usedBefore || volume.UsedBytes is not { } usedAfter
                || volume.TotalBytes is not { } total || total == 0)
            {
                continue;
            }

            var delta = (double)usedAfter - usedBefore;
            if (Math.Abs(delta) < Math.Max(MinimumDiskChangeBytes, total * DiskChangeShare))
            {
                continue;
            }

            var grew = delta > 0;
            var evidence = new List<AnalysisEvidence>
            {
                new(Text.Format(Strings.Diag_Volume, volume.Id), Text.Format(Strings.Changes_Space_UsedOf, MetricFormatter.Bytes(usedBefore), MetricFormatter.Bytes(usedAfter), MetricFormatter.Bytes(total)))
                {
                    From = period.From,
                    To = period.To,
                    Source = MetricSources.Storage,
                },
            };
            if (grew && installed.Count > 0)
            {
                evidence.Add(new AnalysisEvidence(Strings.Changes_Ev_InstalledSamePeriod, string.Join(Strings.List_Separator, installed))
                {
                    Reference = Strings.Changes_Ev_PossibleFiles,
                });
            }

            changes.Add(new DetectedChange
            {
                Id = ChangeId(ChangeType.DiskSpaceChanged, volume.Id, Day(period.From), Day(period.To)),
                Type = ChangeType.DiskSpaceChanged,
                DetectedAt = period.To,
                After = period.From,
                Before = period.To,
                Subject = volume.Id,
                Title = grew
                    ? Text.Format(Strings.Changes_Space_More, MetricFormatter.Bytes(delta), volume.Id)
                    : Text.Format(Strings.Changes_Space_Freed, MetricFormatter.Bytes(-delta), volume.Id),
                OldValue = Text.Format(Strings.Changes_Space_Used, MetricFormatter.Bytes(usedBefore)),
                NewValue = Text.Format(Strings.Changes_Space_Used, MetricFormatter.Bytes(usedAfter)),
                Importance = grew && (Math.Abs(delta) >= 20.0 * 1024 * 1024 * 1024 || Math.Abs(delta) >= total * 0.10) ? ChangeImportance.High
                    : grew ? ChangeImportance.Medium : ChangeImportance.Low,
                Explanation = grew
                    ? Strings.Changes_Space_Grew_Explanation
                    : Strings.Changes_Space_Freed_Explanation,
                Origin = UnknownOrigin + " " + Strings.Changes_Space_Origin,
                Evidence = evidence,
                Action = DiagnosisAction.Storage,
            });
        }
    }

    private static void CompareUsage(UsageSummary? before, UsageSummary? after, Period period, IReadOnlyList<string> installed, List<DetectedChange> changes)
    {
        if (before is not { MonitoredMinutes: >= MinimumUsageMinutes } || after is not { MonitoredMinutes: >= MinimumUsageMinutes })
        {
            return;
        }

        void Add(ChangeType type, double? oldValue, double? newValue)
        {
            var memory = type == ChangeType.MemoryUsageChanged;
            var name = memory ? Strings.Diag_Metric_MemoryUsage : Strings.Diag_Metric_CpuUsage;
            var average = memory
                ? Strings.Changes_Usage_AverageMemory
                : Strings.Changes_Usage_AverageCpu;
            if (oldValue is not { } o || newValue is not { } n || Math.Abs(n - o) < UsageChangePoints)
            {
                return;
            }

            var delta = n - o;
            var evidence = new List<AnalysisEvidence>
            {
                new(average, $"{MetricFormatter.Percent(o)} → {MetricFormatter.Percent(n)}")
                {
                    Reference = Text.Format(Strings.Changes_Usage_Reference, before.MonitoredMinutes, after.MonitoredMinutes),
                    From = period.From,
                    To = period.To,
                    Source = Strings.State_Source_Minutes,
                },
            };
            if (installed.Count > 0)
            {
                evidence.Add(new AnalysisEvidence(Strings.Changes_Ev_InstalledSamePeriod, string.Join(Strings.List_Separator, installed))
                {
                    Reference = Strings.Changes_Ev_Possible,
                });
            }

            changes.Add(new DetectedChange
            {
                Id = ChangeId(type, Day(period.From), Day(period.To)),
                Type = type,
                DetectedAt = period.To,
                After = period.From,
                Before = period.To,
                Subject = name,
                Title = Text.Format(Strings.Changes_Usage_Title, average, delta),
                OldValue = MetricFormatter.Percent(o),
                NewValue = MetricFormatter.Percent(n),
                Importance = Math.Abs(delta) >= 2 * UsageChangePoints ? ChangeImportance.High : ChangeImportance.Medium,
                Explanation = delta > 0
                    ? (memory
                        ? Strings.Changes_Usage_MoreMemory
                        : Strings.Changes_Usage_MoreCpu)
                    : (memory
                        ? Strings.Changes_Usage_LessMemory
                        : Strings.Changes_Usage_LessCpu),
                Origin = UnknownOrigin,
                Evidence = evidence,
                Action = DiagnosisAction.AppImpact,
            });
        }

        Add(ChangeType.MemoryUsageChanged, before.MemoryAverage, after.MemoryAverage);
        Add(ChangeType.CpuUsageChanged, before.CpuAverage, after.CpuAverage);
    }

    private static bool SameProduct(InstalledApp a, InstalledApp b) =>
        string.Equals(Normalize(a.Name), Normalize(b.Name), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Publisher ?? string.Empty, b.Publisher ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string name) => VersionPattern().Replace(name, string.Empty).Trim();

    private static AnalysisEvidence Evidence(string metric, InstalledApp app) =>
        new(metric, Join(app.Name, app.Version, app.Publisher))
        {
            Source = app.Source.Length > 0
                ? Text.Format(Strings.Changes_Ev_SourceOf, AppSourceText.Label(app.Source))
                : Strings.Changes_Ev_Source,
            Reference = app.InstallDate is { } date ? Text.Format(Strings.Changes_Ev_InstallDate, date.ToString("d", CultureInfo.CurrentCulture)) : null,
        };

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Date(DateTimeOffset time) => time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static string Day(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\bv?\d+(\.\d+)+\b|\(\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    private readonly record struct Period(DateTimeOffset From, DateTimeOffset To);
}
