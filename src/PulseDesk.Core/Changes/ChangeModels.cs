using PulseDesk.Core.Analysis;
using PulseDesk.Core.Diagnosis;

namespace PulseDesk.Core.Changes;

/// <summary>An application registered with Windows (Apps &amp; features).</summary>
/// <param name="Id">Stable identifier (registry key or package family).</param>
/// <param name="Name">Display name.</param>
public sealed record InstalledApp(string Id, string Name)
{
    public string? Version { get; init; }

    public string? Publisher { get; init; }

    /// <summary>Install date as recorded by the installer (day precision), when it recorded one.</summary>
    public DateOnly? InstallDate { get; init; }

    /// <summary>Where it was found, e.g. "Machine", "User", "Store".</summary>
    public string Source { get; init; } = string.Empty;
}

/// <summary>A program that starts when the user signs in.</summary>
/// <param name="Id">Stable identifier (location + name).</param>
/// <param name="Name">Entry name.</param>
/// <param name="Command">Command or file started.</param>
/// <param name="Location">Where it is registered, e.g. "HKCU Run" or "Startup folder".</param>
public sealed record StartupProgram(string Id, string Name, string Command, string Location)
{
    /// <summary>False when disabled in Settings › Apps › Startup or Task Manager; null when unknown.</summary>
    public bool? Enabled { get; init; }
}

/// <summary>A hardware device worth following (graphics adapter, network adapter, fixed volume).</summary>
/// <param name="Category">"Graphics", "Network", "Volume".</param>
/// <param name="Id">Stable identifier within the category.</param>
/// <param name="Name">Display name.</param>
public sealed record DeviceInfo(string Category, string Id, string Name)
{
    /// <summary>Size of a volume, when relevant.</summary>
    public ulong? TotalBytes { get; init; }

    /// <summary>Space used on a volume, when relevant.</summary>
    public ulong? UsedBytes { get; init; }
}

/// <summary>Software and hardware read from Windows (read-only).</summary>
public sealed record SystemInventory
{
    public IReadOnlyList<InstalledApp> Apps { get; init; } = [];

    /// <summary>False when the list of installed applications could not be read.</summary>
    public bool AppsAvailable { get; init; }

    public IReadOnlyList<StartupProgram> StartupPrograms { get; init; } = [];

    public bool StartupAvailable { get; init; }

    public IReadOnlyList<DeviceInfo> Devices { get; init; } = [];
}

/// <summary>Average usage of the PC over the day before a snapshot (from the local history).</summary>
/// <param name="MonitoredMinutes">Minutes of history available.</param>
public sealed record UsageSummary(int MonitoredMinutes)
{
    public double? CpuAverage { get; init; }

    public double? MemoryAverage { get; init; }

    public double? DiskAverage { get; init; }
}

/// <summary>
/// The state of the PC at one time: configuration, software, devices, disk space and recent usage. Taken once
/// a day and kept in the local history, so later states can be compared with it.
/// </summary>
public sealed record SystemBaseline
{
    public required DateTimeOffset CapturedAt { get; init; }

    public string? OsName { get; init; }

    public string? OsVersion { get; init; }

    public string? OsBuild { get; init; }

    public string? Processor { get; init; }

    public ulong? InstalledMemoryBytes { get; init; }

    public string? BiosVersion { get; init; }

    public SystemInventory Inventory { get; init; } = new();

    /// <summary>Usage over the 24 hours before the snapshot, when enough history exists.</summary>
    public UsageSummary? Usage { get; init; }
}

/// <summary>Kinds of changes PulseDesk can detect.</summary>
public enum ChangeType
{
    AppInstalled,
    AppRemoved,
    AppUpdated,
    NewFrequentApp,
    StartupProgramAdded,
    StartupProgramRemoved,
    StartupProgramEnabled,
    StartupProgramDisabled,
    WindowsUpdated,
    FirmwareUpdated,
    MemoryChanged,
    DeviceAdded,
    DeviceRemoved,
    DiskSpaceChanged,
    CpuUsageChanged,
    MemoryUsageChanged,
}

/// <summary>How much a change may matter for performance or stability.</summary>
public enum ChangeImportance
{
    Low,
    Medium,
    High,
}

/// <summary>
/// One change detected on the PC, with when it happened (as precisely as PulseDesk knows), the old and new
/// values, and where the information comes from. When the cause is unknown, the change says so.
/// </summary>
public sealed record DetectedChange
{
    /// <summary>Deterministic identifier: the same change is never recorded twice.</summary>
    public required string Id { get; init; }

    public required ChangeType Type { get; init; }

    /// <summary>When PulseDesk detected the change.</summary>
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>The change happened after this time (the earlier snapshot), when known.</summary>
    public DateTimeOffset? After { get; init; }

    /// <summary>The change happened before this time.</summary>
    public required DateTimeOffset Before { get; init; }

    /// <summary>What changed, e.g. an application name or "C:".</summary>
    public required string Subject { get; init; }

    public required string Title { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public required ChangeImportance Importance { get; init; }

    /// <summary>What the change means, in plain words.</summary>
    public required string Explanation { get; init; }

    /// <summary>Where the information comes from, or "Change detected, origin unknown."</summary>
    public required string Origin { get; init; }

    public IReadOnlyList<AnalysisEvidence> Evidence { get; init; } = [];

    /// <summary>Where to look further.</summary>
    public DiagnosisAction Action { get; init; }

    /// <summary>Application concerned, when relevant.</summary>
    public string? AppKey { get; init; }
}

/// <summary>Snapshots offered as comparison references.</summary>
public enum BaselineReference
{
    Today,
    Yesterday,
    SevenDaysAgo,
    ThirtyDaysAgo,
}

/// <summary>Result of comparing the current state with a reference snapshot.</summary>
/// <param name="Reference">Reference requested.</param>
/// <param name="ReferenceSnapshot">The snapshot used, or null when none is old enough.</param>
/// <param name="Changes">Changes found.</param>
/// <param name="Note">Limits of the comparison, shown to the user.</param>
public sealed record ChangeComparison(BaselineReference Reference, SystemBaseline? ReferenceSnapshot, IReadOnlyList<DetectedChange> Changes, string Note);
