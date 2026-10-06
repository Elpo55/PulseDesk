using System.Globalization;
using Sysora.Core.Analysis;
using Sysora.Core.Formatting;
using Sysora.Core.Models;

namespace Sysora.Core.History;

/// <summary>
/// Derives timeline events from consecutive snapshots: applications starting to use a lot of CPU or memory,
/// notable applications exiting, connectivity changes, volumes appearing or disappearing, metrics becoming
/// unavailable. Only changes Sysora measured itself are reported.
/// </summary>
/// <remarks>
/// Built to stay quiet: thresholds have hysteresis, each application event is rate-limited, and only
/// applications that were using significant resources are followed. Not thread-safe.
/// </remarks>
public sealed class SystemEventDetector
{
    /// <summary>An application at or above this share of total CPU capacity produces an event.</summary>
    public const double AppHighCpuPercent = 20;

    /// <summary>An application at or above this share of physical memory produces an event.</summary>
    public const double AppHighMemoryPercent = 20;

    /// <summary>Applications above these levels are followed so their exit can be reported.</summary>
    public const double NotableCpuPercent = 5;

    /// <inheritdoc cref="NotableCpuPercent"/>
    public const double NotableMemoryPercent = 5;

    /// <summary>Minimum time between two events of the same kind for the same application.</summary>
    public static readonly TimeSpan AppEventInterval = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> _highCpu = new(StringComparer.Ordinal);
    private readonly HashSet<string> _highMemory = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, SystemEventKind Kind), DateTimeOffset> _lastAppEvent = [];
    private Dictionary<string, AppGroup> _notable = new(StringComparer.Ordinal);
    private NetworkConnectivity? _connectivity;
    private HashSet<string>? _volumes;
    private MetricKind? _unavailable;

    /// <summary>Returns the events implied by a new snapshot.</summary>
    /// <param name="snapshot">The monitor's latest snapshot.</param>
    /// <param name="updated">Metrics refreshed by this update.</param>
    /// <param name="apps">Applications, when processes were refreshed by this update.</param>
    public IReadOnlyList<SystemEvent> Detect(SystemSnapshot snapshot, MetricKind updated, IReadOnlyList<AppGroup>? apps)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var events = new List<SystemEvent>();
        var time = snapshot.Timestamp;

        DetectAvailability(snapshot, updated, time, events);

        if ((updated & MetricKind.Network) != 0 && snapshot.Network is { } network)
        {
            DetectConnectivity(network.Connectivity, time, events);
        }

        if ((updated & MetricKind.Storage) != 0 && snapshot.Storage is { } storage)
        {
            DetectVolumes(storage, time, events);
        }

        if (apps is not null)
        {
            DetectApps(apps, snapshot.Memory?.TotalBytes ?? 0, time, events);
        }

        return events;
    }

    private void DetectAvailability(SystemSnapshot snapshot, MetricKind updated, DateTimeOffset time, List<SystemEvent> events)
    {
        var current = snapshot.Unavailable;
        var previous = _unavailable ?? MetricKind.None;
        _unavailable = current;
        foreach (var kind in Enum.GetValues<MetricKind>())
        {
            if (kind is MetricKind.None or MetricKind.All || (updated & kind) == 0)
            {
                continue;
            }

            var wasUnavailable = (previous & kind) != 0;
            var isUnavailable = (current & kind) != 0;
            if (isUnavailable && !wasUnavailable)
            {
                events.Add(new SystemEvent(time, SystemEventKind.MetricUnavailable, $"{Describe(kind)} metrics not available"));
            }
            else if (!isUnavailable && wasUnavailable)
            {
                events.Add(new SystemEvent(time, SystemEventKind.MetricRestored, $"{Describe(kind)} metrics available again"));
            }
        }
    }

    private void DetectConnectivity(NetworkConnectivity connectivity, DateTimeOffset time, List<SystemEvent> events)
    {
        if (connectivity == NetworkConnectivity.Unknown)
        {
            return;
        }

        if (_connectivity is { } previous && previous != connectivity)
        {
            var title = connectivity switch
            {
                NetworkConnectivity.InternetAccess => "Internet access available",
                NetworkConnectivity.ConstrainedInternetAccess => "Internet access limited",
                NetworkConnectivity.LocalAccess => "Internet access lost (local network only)",
                _ => "Network connection lost",
            };
            events.Add(new SystemEvent(time, SystemEventKind.ConnectivityChanged, title, "As reported by Windows"));
        }

        _connectivity = connectivity;
    }

    private void DetectVolumes(IReadOnlyList<StorageMetrics> storage, DateTimeOffset time, List<SystemEvent> events)
    {
        var current = new HashSet<string>(storage.Select(s => s.Letter), StringComparer.OrdinalIgnoreCase);
        if (_volumes is { } previous)
        {
            foreach (var volume in storage.Where(s => !previous.Contains(s.Letter)))
            {
                var label = volume.Label is { } l ? $" ({l})" : string.Empty;
                events.Add(new SystemEvent(time, SystemEventKind.VolumeAdded, $"Volume {volume.Letter}{label} connected",
                    $"{MetricFormatter.Bytes(volume.TotalBytes)} {volume.Kind.ToString().ToLowerInvariant()} volume"));
            }

            foreach (var letter in previous.Where(l => !current.Contains(l)).Order(StringComparer.OrdinalIgnoreCase))
            {
                events.Add(new SystemEvent(time, SystemEventKind.VolumeRemoved, $"Volume {letter} disconnected"));
            }
        }

        _volumes = current;
    }

    private void DetectApps(IReadOnlyList<AppGroup> apps, ulong totalMemory, DateTimeOffset time, List<SystemEvent> events)
    {
        var present = new Dictionary<string, AppGroup>(apps.Count, StringComparer.Ordinal);
        foreach (var app in apps)
        {
            present[app.Identity.Key] = app;
        }

        foreach (var app in apps)
        {
            var key = app.Identity.Key;
            var memoryShare = totalMemory > 0 ? app.PrivateWorkingSetBytes * 100.0 / totalMemory : 0;

            if (app.CpuPercent >= AppHighCpuPercent && _highCpu.Add(key) && AllowAppEvent(key, SystemEventKind.AppHighCpu, time))
            {
                events.Add(new SystemEvent(time, SystemEventKind.AppHighCpu,
                    Invariant($"{app.Identity.Name} CPU rose to {app.CpuPercent:0}%"),
                    Invariant($"{MetricFormatter.Bytes(app.PrivateWorkingSetBytes)} memory, {MetricFormatter.Plural(app.InstanceCount, "process", "processes")}"))
                { AppKey = key });
            }
            else if (app.CpuPercent < AppHighCpuPercent / 2)
            {
                _highCpu.Remove(key);
            }

            if (memoryShare >= AppHighMemoryPercent && _highMemory.Add(key) && AllowAppEvent(key, SystemEventKind.AppHighMemory, time))
            {
                events.Add(new SystemEvent(time, SystemEventKind.AppHighMemory,
                    Invariant($"{app.Identity.Name} uses {MetricFormatter.Bytes(app.PrivateWorkingSetBytes)} of memory"),
                    Invariant($"{memoryShare:0}% of physical memory"))
                { AppKey = key });
            }
            else if (memoryShare < AppHighMemoryPercent - 5)
            {
                _highMemory.Remove(key);
            }
        }

        foreach (var (key, last) in _notable)
        {
            if (!present.ContainsKey(key))
            {
                events.Add(new SystemEvent(time, SystemEventKind.AppExited, $"{last.Identity.Name} exited",
                    Invariant($"Was using {last.CpuPercent:0.#}% CPU and {MetricFormatter.Bytes(last.PrivateWorkingSetBytes)} of memory"))
                { AppKey = key });
                _highCpu.Remove(key);
                _highMemory.Remove(key);
            }
        }

        _notable = apps
            .Where(a => a.CpuPercent >= NotableCpuPercent
                || (totalMemory > 0 && a.PrivateWorkingSetBytes * 100.0 / totalMemory >= NotableMemoryPercent))
            .ToDictionary(a => a.Identity.Key, StringComparer.Ordinal);

        // Forget rate-limit entries that no longer matter, so the dictionary stays small.
        foreach (var entry in _lastAppEvent.Where(e => time - e.Value > AppEventInterval).Select(e => e.Key).ToArray())
        {
            _lastAppEvent.Remove(entry);
        }
    }

    private bool AllowAppEvent(string key, SystemEventKind kind, DateTimeOffset time)
    {
        if (_lastAppEvent.TryGetValue((key, kind), out var last) && time - last < AppEventInterval)
        {
            return false;
        }

        _lastAppEvent[(key, kind)] = time;
        return true;
    }

    private static string Describe(MetricKind kind) => kind switch
    {
        MetricKind.Cpu => "CPU",
        MetricKind.Gpu => "GPU",
        MetricKind.DiskActivity => "Disk activity",
        MetricKind.System => "System",
        _ => kind.ToString(),
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
