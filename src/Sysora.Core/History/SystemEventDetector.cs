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

    /// <summary>An application is reported as started only when its first process started this recently.</summary>
    public static readonly TimeSpan StartRecency = TimeSpan.FromMinutes(2);

    /// <summary>Minimum time between two "started" events for the same application (an application that restarts often stays quiet).</summary>
    public static readonly TimeSpan AppStartInterval = TimeSpan.FromMinutes(30);

    /// <summary>Most started applications followed at once, so their closing can be reported.</summary>
    public const int MaxFollowedStarts = 200;

    /// <summary>Background helpers that start and stop constantly: never reported as application launches.</summary>
    private static readonly HashSet<string> IgnoredLaunches = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost.exe", "dllhost.exe", "rundll32.exe", "backgroundTaskHost.exe", "RuntimeBroker.exe", "WerFault.exe",
        "SearchProtocolHost.exe", "SearchFilterHost.exe", "smartscreen.exe", "taskhostw.exe", "WmiPrvSE.exe",
        "TextInputHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SystemSettingsBroker.exe",
        "CompPkgSrv.exe", "consent.exe", "sihost.exe", "ctfmon.exe", "msedgewebview2.exe", "crashpad_handler.exe",
    };

    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Title of the event recorded when Windows reports Internet access again.</summary>
    public const string InternetAvailableTitle = "Internet access available";

    /// <summary>Title of the event recorded when Windows reports limited Internet access.</summary>
    public const string InternetLimitedTitle = "Internet access limited";

    /// <summary>Title of the event recorded when only the local network remains reachable.</summary>
    public const string InternetLostTitle = "Internet access lost (local network only)";

    /// <summary>Title of the event recorded when Windows reports no network connection.</summary>
    public const string NetworkLostTitle = "Network connection lost";

    /// <summary>True for a connectivity event that reports a loss or a limitation of Internet access.</summary>
    public static bool IsConnectivityLoss(SystemEvent systemEvent)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);
        return systemEvent.Kind == SystemEventKind.ConnectivityChanged
            && systemEvent.Title is InternetLimitedTitle or InternetLostTitle or NetworkLostTitle;
    }

    private readonly HashSet<string> _highCpu = new(StringComparer.Ordinal);
    private readonly HashSet<string> _highMemory = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, SystemEventKind Kind), DateTimeOffset> _lastAppEvent = [];
    private Dictionary<string, AppGroup> _notable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingStart> _pendingStarts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Started, string Name)> _followed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastStart = new(StringComparer.Ordinal);
    private readonly HashSet<string> _present = new(StringComparer.Ordinal);
    private bool _processesSampled;
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
                NetworkConnectivity.InternetAccess => InternetAvailableTitle,
                NetworkConnectivity.ConstrainedInternetAccess => InternetLimitedTitle,
                NetworkConnectivity.LocalAccess => InternetLostTitle,
                _ => NetworkLostTitle,
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

        DetectStarts(apps, present, time, events);

        foreach (var (key, last) in _notable)
        {
            if (!present.ContainsKey(key))
            {
                var ran = _followed.Remove(key, out var followed) ? $"Ran for {MetricFormatter.DurationCompact(time - followed.Started)}. " : string.Empty;
                events.Add(new SystemEvent(time, SystemEventKind.AppExited, $"{last.Identity.Name} exited",
                    Invariant($"{ran}Was using {last.CpuPercent:0.#}% CPU and {MetricFormatter.Bytes(last.PrivateWorkingSetBytes)} of memory"))
                { AppKey = key });
                _highCpu.Remove(key);
                _highMemory.Remove(key);
            }
        }

        foreach (var (key, (started, name)) in _followed.Count == 0 ? [] : _followed.Where(f => !present.ContainsKey(f.Key) && !_notable.ContainsKey(f.Key)).ToArray())
        {
            _followed.Remove(key);
            events.Add(new SystemEvent(time, SystemEventKind.AppExited, $"{name} closed", $"Ran for {MetricFormatter.DurationCompact(time - started)}") { AppKey = key });
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

    /// <summary>
    /// Applications that started since the previous process sample: confirmed one sample later (a process that ran for
    /// a second is not a launch), only in a user session, outside the Windows folder, and not background helpers. The
    /// very first sample only records what is running: those applications were started before Sysora saw them.
    /// </summary>
    private void DetectStarts(IReadOnlyList<AppGroup> apps, Dictionary<string, AppGroup> present, DateTimeOffset time, List<SystemEvent> events)
    {
        foreach (var (key, pending) in _pendingStarts.Count == 0 ? [] : _pendingStarts.ToArray())
        {
            _pendingStarts.Remove(key);
            if (!present.TryGetValue(key, out var app)
                || (_lastStart.TryGetValue(key, out var last) && pending.FirstSeen - last < AppStartInterval))
            {
                continue;
            }

            _lastStart[key] = pending.FirstSeen;
            if (_followed.Count < MaxFollowedStarts)
            {
                _followed[key] = (pending.StartedAt, app.Identity.Name);
            }

            var where = app.Identity.ExecutablePath is { } path ? Path.GetDirectoryName(path) : null;
            events.Add(new SystemEvent(pending.FirstSeen, SystemEventKind.AppStarted, $"{app.Identity.Name} started", where) { AppKey = key });
        }

        if (_processesSampled)
        {
            foreach (var app in apps)
            {
                var key = app.Identity.Key;
                if (!_present.Contains(key) && IsLaunch(app, time))
                {
                    _pendingStarts[key] = new PendingStart(time, app.StartedAt!.Value);
                }
            }
        }

        _processesSampled = true;
        _present.Clear();
        _present.UnionWith(present.Keys);
        if (_lastStart.Count > 0)
        {
            foreach (var stale in _lastStart.Where(e => time - e.Value > AppStartInterval).Select(e => e.Key).ToArray())
            {
                _lastStart.Remove(stale);
            }
        }
    }

    private static bool IsLaunch(AppGroup app, DateTimeOffset time) =>
        app.InUserSession
        && app.StartedAt is { } started
        && time - started <= StartRecency
        && app.Identity.ExecutablePath is { } path
        && !IgnoredLaunches.Contains(app.Identity.Name)
        && (WindowsDirectory.Length == 0 || !path.StartsWith(WindowsDirectory, StringComparison.OrdinalIgnoreCase));

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

    private readonly record struct PendingStart(DateTimeOffset FirstSeen, DateTimeOffset StartedAt);
}
