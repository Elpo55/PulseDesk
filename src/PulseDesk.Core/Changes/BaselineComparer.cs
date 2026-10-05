using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Diagnosis;
using PulseDesk.Core.Formatting;

namespace PulseDesk.Core.Changes;

/// <summary>
/// Compares two snapshots of the PC and lists what changed (pure and deterministic). Only differences
/// between the two snapshots are reported; when the cause of a change cannot be known, the change says so.
/// </summary>
public static partial class BaselineComparer
{
    /// <summary>Text used when PulseDesk cannot tell what caused a change.</summary>
    public const string UnknownOrigin = "Change detected, origin unknown.";

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
    /// days. Available from the very first snapshot: these dates come from Windows, not from PulseDesk. Many installers
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
                Title = $"{a.Name} installed or updated",
                Importance = ChangeImportance.Low,
                Explanation = "Windows records this date when an application is installed; many installers also rewrite it when they update the application.",
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
            changes.Add(Updated(previous, app, period, "Matched by name and publisher in Windows' list of installed applications."));
        }

        foreach (var app in newById.Values.Where(a => oldById.TryGetValue(a.Id, out var old) && !string.Equals(old.Version, a.Version, StringComparison.Ordinal)))
        {
            changes.Add(Updated(oldById[app.Id], app, period, "Version read from Windows' list of installed applications."));
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
            Title = $"{a.Name} removed",
            OldValue = a.Version,
            Importance = ChangeImportance.Low,
            Explanation = "The application is no longer in Windows' list of installed applications.",
            Origin = $"Present in the snapshot of {Date(period.From)}, absent from the snapshot of {Date(period.To)}.",
            Evidence = [Evidence("Installed applications", a)],
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
            ? $"Install date ({date!.Value.ToString("d", CultureInfo.CurrentCulture)}) recorded by the installer in Windows' list of installed applications."
            : $"In Windows' list of installed applications, absent from the snapshot of {Date(period!.Value.From)}.";

        return new DetectedChange
        {
            Id = ChangeId(ChangeType.AppInstalled, app.Id, date?.ToString("o", CultureInfo.InvariantCulture)),
            Type = ChangeType.AppInstalled,
            DetectedAt = detectedAt,
            After = after,
            Before = before,
            Subject = app.Name,
            Title = $"{app.Name} installed",
            NewValue = app.Version,
            Importance = ChangeImportance.Medium,
            Explanation = app.Publisher is { } publisher
                ? $"New application from {publisher}. New software can add background processes and startup items."
                : "New application. New software can add background processes and startup items.",
            Origin = origin,
            Evidence = [Evidence("Installed applications", app)],
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
        Title = $"{current.Name} updated",
        OldValue = old.Version ?? MetricFormatter.NotAvailable,
        NewValue = current.Version ?? MetricFormatter.NotAvailable,
        Importance = ChangeImportance.Low,
        Explanation = "A new version can behave differently (performance, background activity).",
        Origin = origin,
        Evidence = [Evidence("Before", old), Evidence("After", current)],
    };

    private static void CompareStartup(IReadOnlyList<StartupProgram> before, IReadOnlyList<StartupProgram> after, Period period, List<DetectedChange> changes)
    {
        var oldById = before.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var newById = after.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var origin = $"Compared the startup entries of {Date(period.From)} and {Date(period.To)}.";

        foreach (var program in newById.Values)
        {
            if (!oldById.TryGetValue(program.Id, out var old))
            {
                changes.Add(Startup(ChangeType.StartupProgramAdded, program, period, $"New startup program: {program.Name}", ChangeImportance.High,
                    "It now starts automatically when you sign in, which can make startup longer and use resources in the background.", origin, null, program.Command));
            }
            else if (old.Enabled != program.Enabled && old.Enabled is not null && program.Enabled is not null)
            {
                var enabled = program.Enabled == true;
                changes.Add(Startup(enabled ? ChangeType.StartupProgramEnabled : ChangeType.StartupProgramDisabled, program, period,
                    enabled ? $"Startup program enabled: {program.Name}" : $"Startup program disabled: {program.Name}",
                    enabled ? ChangeImportance.Medium : ChangeImportance.Low,
                    enabled ? "It will start automatically again when you sign in." : "It no longer starts automatically when you sign in.",
                    origin, old.Enabled == true ? "Enabled" : "Disabled", enabled ? "Enabled" : "Disabled"));
            }
        }

        foreach (var program in oldById.Values.Where(p => !newById.ContainsKey(p.Id)))
        {
            changes.Add(Startup(ChangeType.StartupProgramRemoved, program, period, $"Startup program removed: {program.Name}", ChangeImportance.Low,
                "It no longer starts automatically when you sign in.", origin, program.Command, null));
        }
    }

    private static DetectedChange Startup(ChangeType type, StartupProgram program, Period period, string title, ChangeImportance importance, string explanation, string origin, string? oldValue, string? newValue) => new()
    {
        Id = ChangeId(type, program.Id, oldValue, newValue, Day(period.To)),
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
        Evidence = [new AnalysisEvidence("Startup entry", $"{program.Name}: {program.Command}") { Source = program.Location }],
    };

    private static void CompareSystem(SystemBaseline older, SystemBaseline newer, Period period, List<DetectedChange> changes)
    {
        if (older.OsBuild is { } oldBuild && newer.OsBuild is { } newBuild && oldBuild != newBuild)
        {
            var oldText = Join(older.OsVersion, oldBuild);
            var newText = Join(newer.OsVersion, newBuild);
            changes.Add(System(ChangeType.WindowsUpdated, "Windows", "Windows was updated", oldText, newText, ChangeImportance.Medium,
                "Windows updates can change drivers and background services; the first hours after an update often show extra disk and CPU activity.",
                period, "Build number read from the registry (CurrentVersion)."));
        }

        if (older.BiosVersion is { } oldBios && newer.BiosVersion is { } newBios && oldBios != newBios)
        {
            changes.Add(System(ChangeType.FirmwareUpdated, "BIOS", "Firmware (BIOS) updated", oldBios, newBios, ChangeImportance.Medium,
                "A firmware update can change power management and hardware behavior.", period, "BIOS version read from the registry (SMBIOS data copied by Windows)."));
        }

        if (older.InstalledMemoryBytes is { } oldMemory && newer.InstalledMemoryBytes is { } newMemory && oldMemory != newMemory)
        {
            changes.Add(System(ChangeType.MemoryChanged, "Memory", "Installed memory changed", MetricFormatter.Bytes(oldMemory), MetricFormatter.Bytes(newMemory), ChangeImportance.High,
                "The amount of physical memory changed (a module added, removed or not detected).", period, "Installed memory reported by the firmware."));
        }
    }

    private static DetectedChange System(ChangeType type, string subject, string title, string oldValue, string newValue, ChangeImportance importance, string explanation, Period period, string source) => new()
    {
        Id = ChangeId(type, subject, oldValue, newValue),
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
        Origin = $"Compared the snapshots of {Date(period.From)} and {Date(period.To)}.",
        Evidence = [new AnalysisEvidence(subject, $"{oldValue} → {newValue}") { Source = source, From = period.From, To = period.To }],
    };

    private static void CompareDevices(IReadOnlyList<DeviceInfo> before, IReadOnlyList<DeviceInfo> after, Period period, List<DetectedChange> changes)
    {
        static string Key(DeviceInfo d) => $"{d.Category}|{d.Id}";
        var oldKeys = before.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newKeys = after.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var origin = $"Compared the devices of {Date(period.From)} and {Date(period.To)}.";

        foreach (var device in after.Where(d => !oldKeys.Contains(Key(d))))
        {
            changes.Add(Device(ChangeType.DeviceAdded, device, period, $"New {device.Category.ToLowerInvariant()} device: {device.Name}", origin));
        }

        foreach (var device in before.Where(d => !newKeys.Contains(Key(d))))
        {
            changes.Add(Device(ChangeType.DeviceRemoved, device, period, $"{device.Category} device no longer present: {device.Name}", origin));
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
            ? "A device appeared since the previous snapshot (new hardware, a driver change or a virtual adapter)."
            : "A device present in the previous snapshot was not found (removed, disabled, or its driver changed).",
        Origin = origin,
        Evidence = [new AnalysisEvidence(device.Category, device.Name) { Reference = device.Id }],
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
                new($"Volume {volume.Id}", $"{MetricFormatter.Bytes(usedBefore)} → {MetricFormatter.Bytes(usedAfter)} used of {MetricFormatter.Bytes(total)}")
                {
                    From = period.From,
                    To = period.To,
                    Source = MetricSources.Storage,
                },
            };
            if (grew && installed.Count > 0)
            {
                evidence.Add(new AnalysisEvidence("Installed in the same period", string.Join(", ", installed))
                {
                    Reference = "Possible contributors, not confirmed: PulseDesk does not measure the size of individual files.",
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
                Title = grew ? $"{MetricFormatter.Bytes(delta)} more used on {volume.Id}" : $"{MetricFormatter.Bytes(-delta)} freed on {volume.Id}",
                OldValue = $"{MetricFormatter.Bytes(usedBefore)} used",
                NewValue = $"{MetricFormatter.Bytes(usedAfter)} used",
                Importance = grew && (Math.Abs(delta) >= 20.0 * 1024 * 1024 * 1024 || Math.Abs(delta) >= total * 0.10) ? ChangeImportance.High
                    : grew ? ChangeImportance.Medium : ChangeImportance.Low,
                Explanation = grew
                    ? "Used space on this volume grew noticeably between the two snapshots."
                    : "Space was freed on this volume between the two snapshots.",
                Origin = UnknownOrigin + " PulseDesk measures free space, not individual files.",
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

        void Add(ChangeType type, string name, double? oldValue, double? newValue)
        {
            if (oldValue is not { } o || newValue is not { } n || Math.Abs(n - o) < UsageChangePoints)
            {
                return;
            }

            var delta = n - o;
            var evidence = new List<AnalysisEvidence>
            {
                new($"Average {name.ToLowerInvariant()}", $"{MetricFormatter.Percent(o)} → {MetricFormatter.Percent(n)}")
                {
                    Reference = string.Create(CultureInfo.CurrentCulture, $"Average over the 24 hours before each snapshot ({before.MonitoredMinutes} and {after.MonitoredMinutes} minutes of measurements)"),
                    From = period.From,
                    To = period.To,
                    Source = "Per-minute averages of the local history",
                },
            };
            if (installed.Count > 0)
            {
                evidence.Add(new AnalysisEvidence("Installed in the same period", string.Join(", ", installed))
                {
                    Reference = "Possible contributors, not confirmed.",
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
                Title = string.Create(CultureInfo.CurrentCulture, $"Average {name.ToLowerInvariant()} {(delta > 0 ? "+" : string.Empty)}{delta:0} points"),
                OldValue = MetricFormatter.Percent(o),
                NewValue = MetricFormatter.Percent(n),
                Importance = Math.Abs(delta) >= 2 * UsageChangePoints ? ChangeImportance.High : ChangeImportance.Medium,
                Explanation = delta > 0
                    ? $"The PC used noticeably more {name.ToLowerInvariant()} on average than on the reference day."
                    : $"The PC used noticeably less {name.ToLowerInvariant()} on average than on the reference day.",
                Origin = UnknownOrigin,
                Evidence = evidence,
                Action = DiagnosisAction.AppImpact,
            });
        }

        Add(ChangeType.MemoryUsageChanged, "Memory usage", before.MemoryAverage, after.MemoryAverage);
        Add(ChangeType.CpuUsageChanged, "CPU usage", before.CpuAverage, after.CpuAverage);
    }

    private static bool SameProduct(InstalledApp a, InstalledApp b) =>
        string.Equals(Normalize(a.Name), Normalize(b.Name), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Publisher ?? string.Empty, b.Publisher ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string name) => VersionPattern().Replace(name, string.Empty).Trim();

    private static AnalysisEvidence Evidence(string metric, InstalledApp app) =>
        new(metric, Join(app.Name, app.Version, app.Publisher))
        {
            Source = app.Source.Length > 0 ? $"Windows installed applications ({app.Source})" : "Windows installed applications",
            Reference = app.InstallDate is { } date ? $"Install date recorded by the installer: {date.ToString("d", CultureInfo.CurrentCulture)}" : null,
        };

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Date(DateTimeOffset time) => time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static string Day(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\bv?\d+(\.\d+)+\b|\(\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    private readonly record struct Period(DateTimeOffset From, DateTimeOffset To);
}
