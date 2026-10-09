using Sysora.Core.Analysis;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Models;
using Sysora.Core.Settings;
using Sysora.Localization;

namespace Sysora.Core.Gaming;

/// <summary>Sessions that started and ended during one <see cref="GameSessionTracker.Observe"/> call.</summary>
/// <param name="Started">Games that just started.</param>
/// <param name="Ended">Sessions that just ended (whatever their length).</param>
public sealed record GameTrackerUpdate(IReadOnlyList<LiveGameSession> Started, IReadOnlyList<GameSession> Ended)
{
    public static GameTrackerUpdate None { get; } = new([], []);

    public bool IsEmpty => Started.Count == 0 && Ended.Count == 0;
}

/// <summary>
/// Follows gaming sessions from the monitor's snapshots: detects when an identified game starts and stops, and
/// accumulates what the PC, the game and the other applications used meanwhile.
/// </summary>
/// <remarks>
/// It adds no collection of its own: it reads the snapshots Sysora already takes, only for the metrics refreshed by
/// each update (so a value is never counted twice). Memory is bounded (timeline points are merged as a session grows,
/// application lists are capped). Not thread-safe: <see cref="GameSessionService"/> serializes calls.
/// </remarks>
public sealed class GameSessionTracker
{
    /// <summary>A game absent for this long is considered closed (a game restarting itself stays one session).</summary>
    public static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(15);

    /// <summary>Timeline points kept per session; beyond, neighboring points are merged (the step doubles).</summary>
    public const int MaxTimelinePoints = 240;

    /// <summary>Background applications listed in a session.</summary>
    public const int MaxBackgroundApps = 5;

    /// <summary>Processes associated with the game listed in a session.</summary>
    public const int MaxAssociatedProcesses = 10;

    /// <summary>Events kept per session.</summary>
    public const int MaxEvents = 30;

    /// <summary>A game process older than this when Sysora first sees it was started before Sysora.</summary>
    private static readonly TimeSpan JoinedLateMargin = TimeSpan.FromSeconds(30);

    private const int MaxClassifiedPaths = 4096;
    private const int MaxTrackedApps = 1000;

    private readonly Func<string, string?> _productName;
    private readonly int _selfProcessId;
    private readonly Dictionary<string, GameMatch?> _classified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionBuilder> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ProcessMetrics>> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppGrouper _grouper = new();
    private GamingSettings _settings;
    private IReadOnlySet<string> _recognized;
    private InstalledGameIndex _installed = InstalledGameIndex.Empty;

    /// <param name="settings">Detection settings and the user's lists.</param>
    /// <param name="recognized">Executables Windows recognizes as games.</param>
    /// <param name="productName">Reads an executable's product name (game title), or null.</param>
    /// <param name="selfProcessId">Sysora's own process ID, to report its overhead during sessions.</param>
    public GameSessionTracker(GamingSettings settings, IReadOnlySet<string> recognized, Func<string, string?>? productName = null, int selfProcessId = -1)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recognized);
        _settings = settings;
        _recognized = recognized;
        _productName = productName ?? (_ => null);
        _selfProcessId = selfProcessId;
    }

    /// <summary>True while at least one game is running.</summary>
    public bool HasActiveSessions => _active.Count > 0;

    /// <summary>Sessions in progress.</summary>
    public IReadOnlyList<LiveGameSession> Active => _active.Values.Select(s => s.ToLive()).OrderBy(s => s.Start).ToArray();

    /// <summary>
    /// Applies new detection settings or a new list of recognized games. Sessions of executables that are no longer
    /// considered games (the user marked them "not a game") are dropped and returned.
    /// </summary>
    public IReadOnlyList<LiveGameSession> Configure(GamingSettings settings, IReadOnlySet<string> recognized) =>
        Configure(settings, recognized, _installed);

    /// <inheritdoc cref="Configure(GamingSettings, IReadOnlySet{string})"/>
    /// <param name="installed">Games the launchers report as installed.</param>
    public IReadOnlyList<LiveGameSession> Configure(GamingSettings settings, IReadOnlySet<string> recognized, InstalledGameIndex installed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recognized);
        ArgumentNullException.ThrowIfNull(installed);
        _settings = settings;
        _recognized = recognized;
        _installed = installed;
        _classified.Clear();

        var dropped = new List<LiveGameSession>();
        foreach (var (path, session) in _active.ToArray())
        {
            if (Classify(path) is null)
            {
                dropped.Add(session.ToLive());
                _active.Remove(path);
            }
        }

        return dropped;
    }

    /// <summary>Processes one monitor update.</summary>
    public GameTrackerUpdate Observe(SystemSnapshot snapshot, MetricKind updated)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var time = snapshot.Timestamp;
        if (time == default || !_settings.Enabled)
        {
            return GameTrackerUpdate.None;
        }

        List<LiveGameSession>? started = null;
        List<GameSession>? ended = null;
        if ((updated & MetricKind.Processes) != 0 && snapshot.Processes is { } processes)
        {
            FindRunningGames(processes);
            foreach (var (path, gameProcesses) in _running)
            {
                if (!_active.ContainsKey(path) && Classify(path) is { } match)
                {
                    var session = new SessionBuilder(match, match.GameName ?? GameName(path), time, EarliestStart(gameProcesses, time));
                    _active[path] = session;
                    (started ??= []).Add(session.ToLive());
                }
            }

            foreach (var (path, session) in _active.ToArray())
            {
                if (_running.TryGetValue(path, out var gameProcesses))
                {
                    session.AddProcesses(time, gameProcesses, processes.Processes, _selfProcessId, _grouper);
                }
                else if (time - session.LastSeen >= EndGrace)
                {
                    _active.Remove(path);
                    (ended ??= []).Add(session.Build(GameSessionEnd.GameClosed));
                }
                else
                {
                    // Not seen in this sample: maybe restarting. Measurements are not attributed to the game meanwhile.
                    session.IsPresent = false;
                }
            }
        }

        foreach (var session in _active.Values)
        {
            if (!session.IsPresent)
            {
                continue;
            }

            session.Beat(time);
            if ((updated & MetricKind.Cpu) != 0 && snapshot.Cpu is { } cpu)
            {
                session.AddCpu(time, cpu.UsagePercent);
            }

            if ((updated & MetricKind.Memory) != 0 && snapshot.Memory is { } memory)
            {
                session.AddMemory(time, memory.UsedPercent, memory.TotalBytes);
            }

            if ((updated & MetricKind.Gpu) != 0 && snapshot.Gpus is { Count: > 0 } gpus)
            {
                session.AddGpu(time, gpus);
            }

            if ((updated & MetricKind.DiskActivity) != 0
                && snapshot.DiskActivity?.Where(d => d.ActiveTimePercent is not null).Max(d => d.ActiveTimePercent) is { } disk)
            {
                session.AddDisk(time, disk);
            }

            if ((updated & MetricKind.Network) != 0 && snapshot.Network is { } network && network.Interfaces.Any(i => i.ReceiveBitsPerSecond is not null))
            {
                session.AddNetwork(network.ReceiveBitsPerSecond, network.SendBitsPerSecond);
            }
        }

        return started is null && ended is null ? GameTrackerUpdate.None : new GameTrackerUpdate(started ?? [], ended ?? []);
    }

    /// <summary>Attaches an event (an alert, a sleep notification) to the sessions in progress.</summary>
    public void AddEvent(SystemEvent systemEvent)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);
        foreach (var session in _active.Values)
        {
            session.AddEvent(new GameSessionEvent(systemEvent.Timestamp, systemEvent.Title));
        }
    }

    /// <summary>Ends every session in progress (Sysora exiting, tracking turned off).</summary>
    public IReadOnlyList<GameSession> EndAll(GameSessionEnd reason)
    {
        // A game already gone from the last process sample was closed, whatever ends the tracking now.
        var ended = _active.Values.Select(s => s.Build(s.IsPresent ? reason : GameSessionEnd.GameClosed)).ToArray();
        _active.Clear();
        return ended;
    }

    private void FindRunningGames(ProcessSnapshot processes)
    {
        foreach (var list in _running.Values)
        {
            list.Clear();
        }

        foreach (var process in processes.Processes)
        {
            if (process.ExecutablePath is not { Length: > 0 } path || Classify(path) is null)
            {
                continue;
            }

            if (!_running.TryGetValue(path, out var list))
            {
                list = [];
                _running[path] = list;
            }

            list.Add(process);
        }

        // Keep the lists of games seen recently (reused next time), drop the empty ones.
        foreach (var (path, list) in _running.ToArray())
        {
            if (list.Count == 0)
            {
                _running.Remove(path);
            }
        }
    }

    private GameMatch? Classify(string path)
    {
        if (_classified.TryGetValue(path, out var match))
        {
            return match;
        }

        if (_classified.Count >= MaxClassifiedPaths)
        {
            _classified.Clear();
        }

        match = GameClassifier.Classify(path, _settings, _recognized, _installed);
        _classified[path] = match;
        return match;
    }

    private string GameName(string path)
    {
        string? product = null;
        try
        {
            product = _productName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The name falls back to the folder or executable name.
        }

        return GameClassifier.DisplayName(path, product);
    }

    private static DateTimeOffset? EarliestStart(List<ProcessMetrics> processes, DateTimeOffset now)
    {
        var earliest = processes.Where(p => p.StartTime is not null).Select(p => p.StartTime!.Value).DefaultIfEmpty(now).Min();
        return now - earliest > JoinedLateMargin ? earliest : null;
    }

    /// <summary>Running sum, maximum and count.</summary>
    private struct Stat
    {
        private double _sum;
        private double _max;
        private int _count;

        public void Add(double value)
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            _max = _count == 0 ? value : Math.Max(_max, value);
            _sum += value;
            _count++;
        }

        public readonly MetricStat? Result => _count == 0 ? null : new MetricStat(_sum / _count, _max, _count);
    }

    /// <summary>Usage of one application across the session's process samples.</summary>
    private sealed class AppAccumulator(AppIdentity identity)
    {
        public AppIdentity Identity { get; } = identity;

        public double CpuSum { get; set; }

        public double CpuMax { get; set; }

        public ulong MemoryMax { get; set; }

        public GameAppUsage ToUsage(int samples) =>
            new(Identity.Key, Identity.Name, Identity.ExecutablePath, samples > 0 ? CpuSum / samples : 0, CpuMax, MemoryMax);
    }

    /// <summary>Time spent above a threshold, counting only periods that last at least a minimum duration.</summary>
    private sealed class ConditionTracker(GameConditionKind kind, double threshold, TimeSpan minimum)
    {
        private DateTimeOffset? _since;
        private DateTimeOffset _last;
        private double _peak;
        private double _total;
        private int _periods;
        private double _longest;
        private double _peakAll;
        private DateTimeOffset? _first;

        public void Add(DateTimeOffset time, double value)
        {
            if (_since is not null && time - _last > SystemUsageAggregator.MaxSampleInterval)
            {
                Close();
            }

            if (value >= threshold)
            {
                _since ??= time;
                _peak = Math.Max(_peak, value);
                _last = time;
            }
            else
            {
                Close();
            }
        }

        public GameCondition? Result()
        {
            Close();
            return _periods == 0 ? null : new GameCondition(kind, threshold, _total, _periods, _longest, _peakAll, _first!.Value);
        }

        private void Close()
        {
            if (_since is { } since && _last - since >= minimum)
            {
                var seconds = (_last - since).TotalSeconds;
                _total += seconds;
                _periods++;
                _longest = Math.Max(_longest, seconds);
                _peakAll = Math.Max(_peakAll, _peak);
                _first ??= since;
            }

            _since = null;
            _peak = 0;
        }
    }

    /// <summary>Per-step averages, merged two by two when there are too many.</summary>
    private sealed class Timeline
    {
        private readonly List<Point> _points = [];
        private int _stepSeconds = 60;
        private Point _current;

        public int StepSeconds => _stepSeconds;

        public void Add(DateTimeOffset time, Field field, double value)
        {
            var start = Align(time);
            if (_current.Start != start)
            {
                if (_current.Start != default)
                {
                    _points.Add(_current);
                    if (_points.Count > MaxTimelinePoints)
                    {
                        Compact();
                        start = Align(time);
                    }
                }

                // After a compaction the new step may already have a point.
                if (_points.Count > 0 && _points[^1].Start == start)
                {
                    _current = _points[^1];
                    _points.RemoveAt(_points.Count - 1);
                }
                else
                {
                    _current = new Point { Start = start };
                }
            }

            _current.Add(field, value);
        }

        public IReadOnlyList<GameTimelinePoint> Build()
        {
            var points = new List<Point>(_points);
            if (_current.Start != default)
            {
                points.Add(_current);
            }

            return points.Select(p => p.ToPoint()).ToArray();
        }

        private DateTimeOffset Align(DateTimeOffset time)
        {
            var minute = SystemUsageAggregate.BucketStart(time, HistoryResolution.Minute);
            return minute - TimeSpan.FromSeconds(minute.ToUnixTimeSeconds() % _stepSeconds);
        }

        /// <summary>Doubles the step and merges neighboring points.</summary>
        private void Compact()
        {
            _stepSeconds *= 2;
            var merged = new List<Point>((_points.Count / 2) + 1);
            foreach (var point in _points)
            {
                var start = Align(point.Start);
                if (merged.Count > 0 && merged[^1].Start == start)
                {
                    merged[^1] = merged[^1].Merge(point);
                }
                else
                {
                    merged.Add(point with { Start = start });
                }
            }

            _points.Clear();
            _points.AddRange(merged);
        }

        public enum Field
        {
            Cpu,
            Gpu,
            Memory,
            GameCpu,
            GameGpu,
            GameMemory,
        }

        private record struct Sum(double Total, int Count)
        {
            public readonly double? Average => Count == 0 ? null : Total / Count;

            public readonly Sum Plus(Sum other) => new(Total + other.Total, Count + other.Count);
        }

        private record struct Point
        {
            public DateTimeOffset Start;
            public Sum Cpu;
            public Sum Gpu;
            public Sum Memory;
            public Sum GameCpu;
            public Sum GameGpu;
            public Sum GameMemory;

            public void Add(Field field, double value)
            {
                var add = new Sum(value, 1);
                switch (field)
                {
                    case Field.Cpu: Cpu = Cpu.Plus(add); break;
                    case Field.Gpu: Gpu = Gpu.Plus(add); break;
                    case Field.Memory: Memory = Memory.Plus(add); break;
                    case Field.GameCpu: GameCpu = GameCpu.Plus(add); break;
                    case Field.GameGpu: GameGpu = GameGpu.Plus(add); break;
                    case Field.GameMemory: GameMemory = GameMemory.Plus(add); break;
                }
            }

            public readonly Point Merge(Point other) => this with
            {
                Cpu = Cpu.Plus(other.Cpu),
                Gpu = Gpu.Plus(other.Gpu),
                Memory = Memory.Plus(other.Memory),
                GameCpu = GameCpu.Plus(other.GameCpu),
                GameGpu = GameGpu.Plus(other.GameGpu),
                GameMemory = GameMemory.Plus(other.GameMemory),
            };

            public readonly GameTimelinePoint ToPoint() => new(Start)
            {
                Cpu = Cpu.Average,
                Gpu = Gpu.Average,
                Memory = Memory.Average,
                GameCpu = GameCpu.Average,
                GameGpu = GameGpu.Average,
                GameMemoryBytes = GameMemory.Average,
            };
        }
    }

    /// <summary>Everything accumulated for one session.</summary>
    private sealed class SessionBuilder
    {
        private readonly GameMatch _match;
        private readonly string _name;
        private readonly string _key;
        private readonly string? _installFolder;
        private readonly DateTimeOffset _start;
        private readonly DateTimeOffset? _gameStartedAt;
        private readonly Dictionary<string, AppAccumulator> _associated = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AppAccumulator> _background = new(StringComparer.Ordinal);
        private readonly HashSet<int> _gameProcessIds = [];
        private readonly Dictionary<string, SampleUsage> _backgroundSample = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SampleUsage> _associatedSample = new(StringComparer.Ordinal);
        private readonly List<GameSessionEvent> _events = [];
        private readonly Timeline _timeline = new();
        private readonly ConditionTracker _cpuSaturated = new(GameConditionKind.CpuSaturated, 95, TimeSpan.FromSeconds(60));
        private readonly ConditionTracker _memoryFull = new(GameConditionKind.MemoryNearlyFull, 90, TimeSpan.FromSeconds(60));
        private readonly ConditionTracker _diskSaturated = new(GameConditionKind.DiskSaturated, 90, TimeSpan.FromSeconds(30));
        private ConditionTracker _videoMemoryFull = NewVideoMemoryTracker();
        private string? _videoAdapterId;
        private bool _videoAdapterConfirmed;
        private Stat _cpu;
        private Stat _memory;
        private Stat _gpu;
        private Stat _videoMemory;
        private Stat _disk;
        private Stat _receive;
        private Stat _send;
        private Stat _gameCpu;
        private Stat _gameMemory;
        private Stat _gameGpu;
        private Stat _gameIo;
        private Stat _selfCpu;
        private Stat _selfMemory;
        private ulong? _memoryTotal;
        private ulong? _videoMemoryTotal;
        private string? _gpuName;
        private DateTimeOffset? _lastBeat;
        private double _monitoredSeconds;
        private int _gaps;
        private double _gapSeconds;
        private int _processSamples;
        private int _maxGameProcesses;

        public SessionBuilder(GameMatch match, string name, DateTimeOffset start, DateTimeOffset? gameStartedAt)
        {
            _match = match;
            _name = name;
            _key = AppIdentity.Create(Path.GetFileName(match.ExecutablePath), match.ExecutablePath).Key;
            _installFolder = match.InstallFolder is { } folder ? InstalledGameIndex.Normalize(folder).Replace('/', Path.DirectorySeparatorChar) : GameClassifier.InstallFolder(match.ExecutablePath);
            _start = start;
            _gameStartedAt = gameStartedAt;
            LastSeen = start;
        }

        public DateTimeOffset LastSeen { get; private set; }

        public void Beat(DateTimeOffset time)
        {
            if (_lastBeat is { } last && time > last)
            {
                var interval = time - last;
                if (interval <= SystemUsageAggregator.MaxSampleInterval)
                {
                    _monitoredSeconds += interval.TotalSeconds;
                }
                else
                {
                    _gaps++;
                    _gapSeconds += interval.TotalSeconds;
                    AddEvent(new GameSessionEvent(last, Text.Format(Strings.Game_Event_Gap, MetricFormatter.DurationCompact(interval))));
                }
            }

            if (_lastBeat is null || time > _lastBeat)
            {
                _lastBeat = time;
            }
        }

        public void AddProcesses(DateTimeOffset time, List<ProcessMetrics> game, IReadOnlyList<ProcessMetrics> all, int selfProcessId, AppGrouper grouper)
        {
            LastSeen = time;
            IsPresent = true;
            _processSamples++;
            _maxGameProcesses = Math.Max(_maxGameProcesses, game.Count);
            _gameProcessIds.Clear();
            double cpu = 0, io = 0;
            ulong memory = 0;
            var hasCpu = false;
            foreach (var process in game)
            {
                _gameProcessIds.Add(process.ProcessId);
                memory += process.PrivateWorkingSetBytes;
                io += process.IoBytesPerSecond ?? 0;
                if (process.CpuPercent is { } value)
                {
                    cpu += value;
                    hasCpu = true;
                }
            }

            _gameMemory.Add(memory);
            _timeline.Add(time, Timeline.Field.GameMemory, memory);
            if (hasCpu)
            {
                _gameCpu.Add(cpu);
                _gameIo.Add(io);
                _timeline.Add(time, Timeline.Field.GameCpu, cpu);
            }

            _backgroundSample.Clear();
            _associatedSample.Clear();
            foreach (var process in all)
            {
                if (_gameProcessIds.Contains(process.ProcessId))
                {
                    continue;
                }

                if (process.ProcessId == selfProcessId)
                {
                    if (process.CpuPercent is { } selfCpu)
                    {
                        _selfCpu.Add(selfCpu);
                    }

                    _selfMemory.Add(process.PrivateWorkingSetBytes);
                    continue;
                }

                // Sum the processes of each application in this sample before keeping its maximum.
                var identity = grouper.Identify(process);
                var sample = IsAssociated(process) ? _associatedSample : _backgroundSample;
                ref var usage = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(sample, identity.Key, out _);
                usage.Identity ??= identity;
                usage.Cpu += process.CpuPercent ?? 0;
                usage.Memory += process.PrivateWorkingSetBytes;
            }

            Accumulate(_associated, _associatedSample);
            Accumulate(_background, _backgroundSample);
        }

        /// <summary>True while the game was running at the latest process sample.</summary>
        public bool IsPresent { get; set; } = true;

        public void AddCpu(DateTimeOffset time, double value)
        {
            _cpu.Add(value);
            _timeline.Add(time, Timeline.Field.Cpu, value);
            _cpuSaturated.Add(time, value);
        }

        public void AddMemory(DateTimeOffset time, double percent, ulong total)
        {
            _memory.Add(percent);
            _memoryTotal = total > 0 ? total : _memoryTotal;
            _timeline.Add(time, Timeline.Field.Memory, percent);
            _memoryFull.Add(time, percent);
        }

        public void AddGpu(DateTimeOffset time, IReadOnlyList<GpuMetrics> gpus)
        {
            double? busiest = null;
            GpuMetrics? gameAdapter = null;
            double? gameUsage = null;
            foreach (var gpu in gpus)
            {
                if (gpu.UsagePercent is { } usage && (busiest is null || usage > busiest))
                {
                    busiest = usage;
                }

                // Per-process usage comes from the same counter as the engine usage: when an adapter reports engines, a
                // game missing from its process list was not using it (0%), not unmeasured.
                if (gpu.Engines.Count == 0)
                {
                    continue;
                }

                var game = 0.0;
                foreach (var process in gpu.Processes)
                {
                    if (_gameProcessIds.Contains(process.ProcessId))
                    {
                        game = Math.Max(game, process.UsagePercent);
                    }
                }

                if (gameUsage is null || game > gameUsage)
                {
                    gameUsage = game;
                    gameAdapter = game > 0 ? gpu : gameAdapter;
                }
            }

            if (busiest is { } value)
            {
                _gpu.Add(value);
                _timeline.Add(time, Timeline.Field.Gpu, value);
            }

            if (gameUsage is { } gameValue && _gameProcessIds.Count > 0)
            {
                _gameGpu.Add(gameValue);
                _timeline.Add(time, Timeline.Field.GameGpu, gameValue);
            }

            AddVideoMemory(time, gpus, gameAdapter);
        }

        /// <summary>
        /// Video memory of one adapter for the whole session: the one the game renders on once known, otherwise the one
        /// with the most dedicated memory (the discrete GPU on laptops). Values of different adapters are never mixed.
        /// </summary>
        private void AddVideoMemory(DateTimeOffset time, IReadOnlyList<GpuMetrics> gpus, GpuMetrics? gameAdapter)
        {
            if (gameAdapter is not null && !_videoAdapterConfirmed)
            {
                _videoAdapterConfirmed = true;
                if (_videoAdapterId is not null && _videoAdapterId != gameAdapter.AdapterId)
                {
                    _videoMemory = default;
                    _videoMemoryFull = NewVideoMemoryTracker();
                }

                _videoAdapterId = gameAdapter.AdapterId;
            }

            _videoAdapterId ??= gpus.OrderByDescending(g => g.DedicatedMemoryTotalBytes ?? 0).ThenByDescending(g => g.UsagePercent ?? -1).First().AdapterId;
            var adapter = gpus.FirstOrDefault(g => g.AdapterId == _videoAdapterId);
            if (adapter is null)
            {
                return;
            }

            _gpuName = adapter.Name;
            if (adapter.DedicatedMemoryUsedBytes is { } used)
            {
                _videoMemory.Add(used);
                if (adapter.DedicatedMemoryTotalBytes is > 0 and var total)
                {
                    _videoMemoryTotal = total;
                    _videoMemoryFull.Add(time, used * 100.0 / total);
                }
            }
        }

        private static ConditionTracker NewVideoMemoryTracker() => new(GameConditionKind.VideoMemoryNearlyFull, 95, TimeSpan.FromSeconds(60));

        public void AddDisk(DateTimeOffset time, double active)
        {
            _disk.Add(active);
            _diskSaturated.Add(time, active);
        }

        public void AddNetwork(double receive, double send)
        {
            _receive.Add(receive);
            _send.Add(send);
        }

        public void AddEvent(GameSessionEvent systemEvent)
        {
            if (_events.Count < MaxEvents)
            {
                _events.Add(systemEvent);
            }
        }

        public LiveGameSession ToLive() => new(_key, _name, _match.ExecutablePath, _start, LastSeen, _match.Evidence)
        {
            Confidence = _match.Confidence,
            Cpu = _cpu.Result,
            Gpu = _gpu.Result,
            Memory = _memory.Result,
            GameCpu = _gameCpu.Result,
            GameMemoryBytes = _gameMemory.Result,
            SelfCpu = _selfCpu.Result,
        };

        public GameSession Build(GameSessionEnd reason) => new()
        {
            Id = Guid.NewGuid(),
            GameKey = _key,
            Name = _name,
            ExecutablePath = _match.ExecutablePath,
            Source = _match.Source,
            Confidence = _match.Confidence,
            DetectionEvidence = _match.Evidence,
            Library = _match.Library,
            Start = _start,
            End = LastSeen,
            EndReason = reason,
            GameStartedAt = _gameStartedAt,
            MonitoredSeconds = Math.Min(_monitoredSeconds, (LastSeen - _start).TotalSeconds),
            DataGaps = _gaps,
            GapSeconds = _gapSeconds,
            ProcessSamples = _processSamples,
            Cpu = _cpu.Result,
            Memory = _memory.Result,
            MemoryTotalBytes = _memoryTotal,
            Gpu = _gpu.Result,
            VideoMemoryBytes = _videoMemory.Result,
            VideoMemoryTotalBytes = _videoMemoryTotal,
            GpuName = _gpuName,
            Disk = _disk.Result,
            NetworkReceive = _receive.Result,
            NetworkSend = _send.Result,
            GameCpu = _gameCpu.Result,
            GameMemoryBytes = _gameMemory.Result,
            GameGpu = _gameGpu.Result,
            GameIoBytesPerSecond = _gameIo.Result,
            GameProcessCount = _maxGameProcesses,
            SelfCpu = _selfCpu.Result,
            SelfMemoryBytes = _selfMemory.Result,
            AssociatedProcesses = _associated.Values
                .Select(a => a.ToUsage(_processSamples))
                .OrderByDescending(a => a.CpuAverage)
                .ThenByDescending(a => a.MemoryMaximumBytes)
                .Take(MaxAssociatedProcesses)
                .ToArray(),
            BackgroundApps = _background.Values
                .Select(a => a.ToUsage(_processSamples))
                .Where(a => a.CpuAverage > 0)
                .OrderByDescending(a => a.CpuAverage)
                .Take(MaxBackgroundApps)
                .ToArray(),
            Conditions = new[] { _cpuSaturated.Result(), _memoryFull.Result(), _videoMemoryFull.Result(), _diskSaturated.Result() }
                .OfType<GameCondition>()
                .ToArray(),
            Timeline = _timeline.Build(),
            TimelineStepSeconds = _timeline.StepSeconds,
            Events = _events.ToArray(),
        };

        /// <summary>Started by the game, or installed in the game's folder.</summary>
        private bool IsAssociated(ProcessMetrics process) =>
            _gameProcessIds.Contains(process.ParentProcessId)
            || (_installFolder is not null && process.ExecutablePath is { } path && path.StartsWith(_installFolder, StringComparison.OrdinalIgnoreCase));

        private static void Accumulate(Dictionary<string, AppAccumulator> apps, Dictionary<string, SampleUsage> sample)
        {
            foreach (var (key, usage) in sample)
            {
                if (!apps.TryGetValue(key, out var app))
                {
                    if (apps.Count >= MaxTrackedApps)
                    {
                        continue;
                    }

                    app = new AppAccumulator(usage.Identity!);
                    apps[key] = app;
                }

                app.CpuSum += usage.Cpu;
                app.CpuMax = Math.Max(app.CpuMax, usage.Cpu);
                app.MemoryMax = Math.Max(app.MemoryMax, usage.Memory);
            }
        }
    }

    /// <summary>Usage of one application in one process sample.</summary>
    private struct SampleUsage
    {
        public AppIdentity? Identity;
        public double Cpu;
        public ulong Memory;
    }
}
