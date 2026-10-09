using Sysora.Core.Models;
using Sysora.Localization;

namespace Sysora.Core.Analysis;

/// <summary>
/// Identifies an application across its processes and over time.
/// </summary>
/// <remarks>
/// Two processes are treated as the same application only with sufficient evidence: the same executable
/// path. When Windows denies access to the path (protected processes), the image name is the only evidence
/// available; such applications are flagged with <see cref="IsIdentifiedByPath"/> = false so the UI can say so.
/// Processes with the same name but different paths (several "Update.exe") stay separate applications.
/// </remarks>
/// <param name="Key">Stable, case-insensitive key ("path:C:\…" or "name:…").</param>
/// <param name="Name">Image name, e.g. "chrome.exe".</param>
/// <param name="ExecutablePath">Full executable path, when known.</param>
public sealed record AppIdentity(string Key, string Name, string? ExecutablePath)
{
    private const string PathPrefix = "path:";
    private const string NamePrefix = "name:";

    /// <summary>True when the application is identified by its executable path (strong evidence).</summary>
    public bool IsIdentifiedByPath => ExecutablePath is not null;

    /// <summary>Human-readable description of how the application was identified.</summary>
    public string IdentificationEvidence => IsIdentifiedByPath
        ? Strings.Identity_ByPath
        : Strings.Identity_ByName;

    /// <summary>Builds the identity of a process.</summary>
    public static AppIdentity For(ProcessMetrics process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return Create(process.Name, process.ExecutablePath);
    }

    /// <summary>Builds an identity from a name and an optional path.</summary>
    public static AppIdentity Create(string name, string? executablePath) =>
        string.IsNullOrWhiteSpace(executablePath)
            ? new AppIdentity(NamePrefix + name.ToUpperInvariant(), name, null)
            : new AppIdentity(PathPrefix + executablePath.ToUpperInvariant(), name, executablePath);
}

/// <summary>Resource usage of all the processes of one application at one instant.</summary>
/// <param name="Identity">The application.</param>
/// <param name="InstanceCount">Number of processes.</param>
/// <param name="CpuPercent">Combined CPU usage, percent of total capacity (processes sampled for the first time count as 0).</param>
/// <param name="PrivateWorkingSetBytes">Combined private working set.</param>
/// <param name="IoBytesPerSecond">Combined read and write I/O rate.</param>
public sealed record AppGroup(AppIdentity Identity, int InstanceCount, double CpuPercent, ulong PrivateWorkingSetBytes, double IoBytesPerSecond)
{
    /// <summary>Start time of the application's oldest process, when Windows reports it.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>True when at least one of its processes runs in a user session (not a service).</summary>
    public bool InUserSession { get; init; }

    /// <summary>Converts the group to a compact history sample.</summary>
    public History.AppSample ToSample() =>
        new(Identity.Key, Identity.Name, InstanceCount, CpuPercent, PrivateWorkingSetBytes, IoBytesPerSecond);
}

/// <summary>
/// Groups processes by application (<see cref="AppIdentity"/>). Keeps a cache of identities so repeated
/// grouping of the same processes allocates almost nothing.
/// </summary>
/// <remarks>Not thread-safe: each consumer owns its instance.</remarks>
public sealed class AppGrouper
{
    /// <summary>Above this many cached identities, the cache is cleared (bounds memory on busy build machines).</summary>
    private const int MaxCachedIdentities = 4096;

    private readonly Dictionary<string, AppIdentity> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppIdentity> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Accumulator> _groups = new(StringComparer.Ordinal);

    /// <summary>Returns the identity of a process, reusing cached instances.</summary>
    public AppIdentity Identify(ProcessMetrics process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var cache = process.ExecutablePath is { Length: > 0 } ? _byPath : _byName;
        var lookup = process.ExecutablePath is { Length: > 0 } path ? path : process.Name;
        if (!cache.TryGetValue(lookup, out var identity))
        {
            if (_byPath.Count + _byName.Count >= MaxCachedIdentities)
            {
                _byPath.Clear();
                _byName.Clear();
            }

            identity = AppIdentity.For(process);
            cache[lookup] = identity;
        }

        return identity;
    }

    /// <summary>Groups processes by application.</summary>
    public IReadOnlyList<AppGroup> Group(IEnumerable<ProcessMetrics> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        _groups.Clear();
        foreach (var process in processes)
        {
            var identity = Identify(process);
            ref var accumulator = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_groups, identity.Key, out _);
            accumulator.Identity ??= identity;
            accumulator.Count++;
            accumulator.Cpu += process.CpuPercent ?? 0;
            accumulator.Memory += process.PrivateWorkingSetBytes;
            accumulator.Io += process.IoBytesPerSecond ?? 0;
            accumulator.UserSession |= process.SessionId > 0;
            if (process.StartTime is { } started && (accumulator.Started is null || started < accumulator.Started))
            {
                accumulator.Started = started;
            }
        }

        var result = new AppGroup[_groups.Count];
        var i = 0;
        foreach (var accumulator in _groups.Values)
        {
            result[i++] = new AppGroup(accumulator.Identity!, accumulator.Count, accumulator.Cpu, accumulator.Memory, accumulator.Io)
            {
                StartedAt = accumulator.Started,
                InUserSession = accumulator.UserSession,
            };
        }

        _groups.Clear();
        return result;
    }

    /// <summary>
    /// The applications worth showing in a history sample: the top <paramref name="perCriterion"/> by CPU,
    /// by memory and by I/O (an application appears once even when it ranks in several lists).
    /// </summary>
    public static IReadOnlyList<AppGroup> SelectTop(IReadOnlyList<AppGroup> groups, int perCriterion)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perCriterion);
        var selected = new List<AppGroup>(perCriterion * 3);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        void AddTop(Func<AppGroup, double> key, double minimum)
        {
            foreach (var group in groups.Where(g => key(g) > minimum).OrderByDescending(key).ThenBy(g => g.Identity.Key, StringComparer.Ordinal).Take(perCriterion))
            {
                if (keys.Add(group.Identity.Key))
                {
                    selected.Add(group);
                }
            }
        }

        AddTop(g => g.CpuPercent, 0);
        AddTop(g => g.PrivateWorkingSetBytes, 0);
        AddTop(g => g.IoBytesPerSecond, 0);
        return selected;
    }

    private struct Accumulator
    {
        public AppIdentity? Identity;
        public int Count;
        public double Cpu;
        public ulong Memory;
        public double Io;
        public DateTimeOffset? Started;
        public bool UserSession;
    }
}
