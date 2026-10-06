namespace Sysora.Core.Models;

/// <summary>
/// Identifies a process instance. Windows reuses process IDs, so the creation time is part of the identity.
/// </summary>
/// <param name="ProcessId">Process ID.</param>
/// <param name="CreationTimeTicks">Creation time as UTC file-time ticks (0 when unknown).</param>
public readonly record struct ProcessIdentity(int ProcessId, long CreationTimeTicks);

/// <summary>
/// Resource usage of one process over the last sampling interval.
/// </summary>
/// <param name="Identity">Process identity.</param>
/// <param name="Name">Image name (e.g. "chrome.exe").</param>
public sealed record ProcessMetrics(ProcessIdentity Identity, string Name)
{
    /// <summary>Process ID.</summary>
    public int ProcessId => Identity.ProcessId;

    /// <summary>ID of the process that created this one (it may no longer exist).</summary>
    public int ParentProcessId { get; init; }

    /// <summary>Start time, when known.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>
    /// Full path of the executable, when Windows grants access to it (null for protected processes).
    /// Used to tell apart different programs sharing an image name (for example several "Update.exe").
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Share of total CPU capacity used, 0–100. Null on the first sample of a process.</summary>
    public double? CpuPercent { get; init; }

    /// <summary>Private working set: physical memory used exclusively by the process (Task Manager "Memory").</summary>
    public ulong PrivateWorkingSetBytes { get; init; }

    /// <summary>Full working set, including memory shared with other processes.</summary>
    public ulong WorkingSetBytes { get; init; }

    /// <summary>Private committed memory (private bytes).</summary>
    public ulong PrivateBytes { get; init; }

    /// <summary>Read I/O rate (files, devices and network), bytes per second. Null on the first sample.</summary>
    public double? IoReadBytesPerSecond { get; init; }

    /// <summary>Write I/O rate (files, devices and network), bytes per second. Null on the first sample.</summary>
    public double? IoWriteBytesPerSecond { get; init; }

    /// <summary>Total read and write I/O rate, bytes per second.</summary>
    public double? IoBytesPerSecond =>
        IoReadBytesPerSecond is null && IoWriteBytesPerSecond is null
            ? null
            : (IoReadBytesPerSecond ?? 0) + (IoWriteBytesPerSecond ?? 0);

    /// <summary>Number of threads.</summary>
    public int ThreadCount { get; init; }

    /// <summary>Number of open handles.</summary>
    public int HandleCount { get; init; }

    /// <summary>Terminal Services session the process runs in.</summary>
    public int SessionId { get; init; }
}

/// <summary>
/// All processes sampled at one point in time.
/// </summary>
/// <param name="Processes">Running processes (the idle pseudo-process is excluded).</param>
public sealed record ProcessSnapshot(IReadOnlyList<ProcessMetrics> Processes)
{
    /// <summary>Number of processes.</summary>
    public int ProcessCount => Processes.Count;

    /// <summary>Total number of threads.</summary>
    public int ThreadCount { get; init; }

    /// <summary>Total number of handles.</summary>
    public int HandleCount { get; init; }

    /// <summary>An empty snapshot.</summary>
    public static ProcessSnapshot Empty { get; } = new([]);
}

/// <summary>
/// Details of a process that may require additional access rights. Each field is null when it
/// could not be read (for example, access denied for protected system processes).
/// </summary>
/// <param name="Identity">Process identity.</param>
public sealed record ProcessDetails(ProcessIdentity Identity)
{
    /// <summary>Full path of the executable.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Why the path could not be read, when it could not.</summary>
    public string? ExecutablePathError { get; init; }

    /// <summary>File description from the executable's version resource.</summary>
    public string? Description { get; init; }

    /// <summary>Company name from the executable's version resource.</summary>
    public string? Company { get; init; }

    /// <summary>File version from the executable's version resource.</summary>
    public string? FileVersion { get; init; }

    /// <summary>True when Windows flags the process as critical (ending it would stop the system).</summary>
    public bool? IsCritical { get; init; }
}
