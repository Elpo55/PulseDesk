using Sysora.Core.Formatting;
using Sysora.Core.Models;

namespace Sysora.Core.Simulation;

/// <summary>
/// A fictional computer producing plausible, smoothly varying metrics. Used only in demo mode
/// (<c>--demo</c>) and in tests, so the UI can be developed without depending on the real machine.
/// The UI always flags demo mode prominently: simulated values must never pass for real ones.
/// </summary>
public sealed class SimulatedMachine
{
    public const string ComputerName = "DEMO-PC";
    public const int Cores = 8;
    public const int LogicalProcessors = 16;
    public const ulong TotalMemory = 32UL * 1024 * 1024 * 1024;

    /// <summary>Executable of the simulated game, which runs eight minutes out of every twelve.</summary>
    public const string DemoGamePath = @"C:\Demo\Games\Demo Quest\DemoQuest.exe";

    private const int DemoGameProcessId = 13000;
    private static readonly TimeSpan GameCycle = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan GameStartsAt = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan GameLength = TimeSpan.FromMinutes(8);

    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly DateTimeOffset _bootTime;
    private readonly DateTimeOffset _createdAt;
    private readonly List<SimulatedProcess> _processes;
    private readonly Lock _lock = new();
    private double _cpuNoise;
    private double _peakFrequency;

    public SimulatedMachine(TimeProvider? timeProvider = null, int seed = 42)
    {
        _time = timeProvider ?? TimeProvider.System;
        _random = new Random(seed);
        _createdAt = _time.GetUtcNow();
        _bootTime = _createdAt - new TimeSpan(3, 14, 21, 0);
        _processes = CreateProcesses(_time.GetUtcNow());
    }

    /// <summary>Seconds since the simulation started, used as the phase of all signals.</summary>
    private double Phase => (_time.GetUtcNow() - _bootTime).TotalSeconds;

    public TimeSpan Uptime => _time.GetUtcNow() - _bootTime;

    public DateTimeOffset BootTime => _bootTime;

    /// <summary>Start of the simulated game in progress, or null while it is not running.</summary>
    public DateTimeOffset? GameRunningSince
    {
        get
        {
            var now = _time.GetUtcNow();
            var inCycle = TimeSpan.FromTicks((now - _createdAt).Ticks % GameCycle.Ticks);
            return inCycle >= GameStartsAt && inCycle < GameStartsAt + GameLength ? now - (inCycle - GameStartsAt) : null;
        }
    }

    private bool IsGameRunning => GameRunningSince is not null;

    public CpuMetrics SampleCpu()
    {
        lock (_lock)
        {
            _cpuNoise = (_cpuNoise * 0.7) + ((_random.NextDouble() - 0.5) * 12);
            var usage = Wave(22, 14, 45) + _cpuNoise + (IsGameRunning ? 24 : 0);
            var perCore = Enumerable.Range(0, LogicalProcessors)
                .Select(i => Clamp(usage + (Math.Sin(Phase / 7 + i) * 15) + ((_random.NextDouble() - 0.5) * 10)))
                .ToArray();
            var frequency = 3.6 + (Clamp(usage) / 100 * 1.4);
            _peakFrequency = Math.Max(_peakFrequency, frequency);
            return new CpuMetrics(
                Clamp(usage),
                TemperatureCelsius: 48 + (Clamp(usage) * 0.3),
                CurrentFrequencyGHz: frequency,
                LogicalProcessors)
            {
                LogicalProcessorUsage = perCore,
                BaseFrequencyGHz = 3.6,
                PeakFrequencyGHz = _peakFrequency,
            };
        }
    }

    public MemoryMetrics SampleMemory()
    {
        var usedShare = Wave(0.46, 0.06, 120) + (IsGameRunning ? 0.17 : 0);
        var used = (ulong)(TotalMemory * Math.Clamp(usedShare, 0.05, 0.98));
        return new MemoryMetrics(TotalMemory, used, TotalMemory - used)
        {
            CommittedBytes = used + (4UL << 30),
            CommitLimitBytes = TotalMemory + (8UL << 30),
            CachedBytes = 6UL << 30,
            PagedPoolBytes = 900UL << 20,
            NonPagedPoolBytes = 420UL << 20,
        };
    }

    public IReadOnlyList<GpuMetrics> SampleGpus()
    {
        var game = IsGameRunning;
        var usage = game ? Clamp(Wave(92, 5, 17)) : Clamp(Wave(30, 25, 30));
        return
        [
            new GpuMetrics("demo-gpu-0", "Simulated GPU (demo)")
            {
                UsagePercent = usage,
                DedicatedMemoryTotalBytes = 8UL << 30,
                DedicatedMemoryUsedBytes = (ulong)((2.5 + (usage / 100 * 3)) * ByteSize.BytesPerGigabyte),
                SharedMemoryTotalBytes = 16UL << 30,
                SharedMemoryUsedBytes = 300UL << 20,
                TemperatureCelsius = 45 + (usage * 0.35),
                Engines =
                [
                    new GpuEngineUsage("3D", usage),
                    new GpuEngineUsage("Copy", Clamp(usage / 6)),
                    new GpuEngineUsage("VideoDecode", Clamp(Wave(5, 5, 20))),
                ],
                Processes = game ? [new GpuProcessUsage(DemoGameProcessId, Clamp(usage - 2))] : [],
            },
        ];
    }

    public IReadOnlyList<StorageMetrics> SampleStorage() =>
    [
        StorageMetrics.FromTotalAndFree(@"C:\", 1000UL << 30, 588UL << 30) with
        {
            Label = "System", FileSystem = "NTFS", Kind = DriveKind.Fixed, IsSystemDrive = true,
        },
        StorageMetrics.FromTotalAndFree(@"D:\", 2000UL << 30, 260UL << 30) with
        {
            Label = "Data", FileSystem = "NTFS", Kind = DriveKind.Fixed,
        },
        StorageMetrics.FromTotalAndFree(@"E:\", 64UL << 30, 50UL << 30) with
        {
            Label = "USB", FileSystem = "exFAT", Kind = DriveKind.Removable,
        },
    ];

    public IReadOnlyList<DiskActivityMetrics> SampleDiskActivity() =>
    [
        new DiskActivityMetrics("C:")
        {
            ActiveTimePercent = Clamp(Wave(8, 7, 25)),
            ReadBytesPerSecond = Math.Max(0, Wave(3, 3, 15)) * ByteSize.BytesPerMegabyte,
            WriteBytesPerSecond = Math.Max(0, Wave(1.5, 1.5, 11)) * ByteSize.BytesPerMegabyte,
        },
        new DiskActivityMetrics("D:") { ActiveTimePercent = 1, ReadBytesPerSecond = 0, WriteBytesPerSecond = 12_000 },
        new DiskActivityMetrics("E:") { ActiveTimePercent = 0, ReadBytesPerSecond = 0, WriteBytesPerSecond = 0 },
    ];

    public NetworkMetrics SampleNetwork()
    {
        var down = Math.Max(0, Wave(40e6, 38e6, 20));
        var up = Math.Max(0, Wave(6e6, 5e6, 13));
        var elapsedSeconds = Phase;
        return new NetworkMetrics(
            NetworkConnectivity.InternetAccess,
            [
                new NetworkInterfaceMetrics("demo-eth", "Ethernet", "Simulated Ethernet adapter (demo)", NetworkInterfaceKind.Ethernet)
                {
                    LinkSpeedBitsPerSecond = 1_000_000_000,
                    IPv4Addresses = ["192.168.1.42"],
                    IPv6Addresses = ["fe80::1c2b:3d4e:5f60:7182"],
                    HasGateway = true,
                    BytesReceived = (long)(elapsedSeconds * 2.5e6),
                    BytesSent = (long)(elapsedSeconds * 0.4e6),
                    ReceiveBitsPerSecond = down,
                    SendBitsPerSecond = up,
                },
            ]);
    }

    public ProcessSnapshot SampleProcesses()
    {
        lock (_lock)
        {
            var metrics = _processes.Select(p => p.Sample(Phase, _random)).ToList();
            if (GameRunningSince is { } since)
            {
                metrics.Add(new ProcessMetrics(new ProcessIdentity(DemoGameProcessId, since.UtcTicks), "DemoQuest.exe")
                {
                    ParentProcessId = 9300,
                    StartTime = since,
                    ExecutablePath = DemoGamePath,
                    CpuPercent = Clamp(Wave(21, 6, 23)),
                    PrivateWorkingSetBytes = (ulong)(Wave(5.4, 0.4, 90) * ByteSize.BytesPerGigabyte),
                    WorkingSetBytes = (ulong)(6.1 * ByteSize.BytesPerGigabyte),
                    PrivateBytes = (ulong)(6.5 * ByteSize.BytesPerGigabyte),
                    IoReadBytesPerSecond = Math.Max(0, Wave(4, 4, 31)) * ByteSize.BytesPerMegabyte,
                    IoWriteBytesPerSecond = 200_000,
                    ThreadCount = 96,
                    HandleCount = 1800,
                    SessionId = 1,
                });
            }

            return new ProcessSnapshot(metrics)
            {
                ThreadCount = metrics.Sum(m => m.ThreadCount),
                HandleCount = metrics.Sum(m => m.HandleCount),
            };
        }
    }

    /// <summary>Path shown in process details for a simulated process.</summary>
    public string? GetProcessPath(ProcessIdentity identity)
    {
        if (identity.ProcessId == DemoGameProcessId)
        {
            return DemoGamePath;
        }

        lock (_lock)
        {
            var process = _processes.FirstOrDefault(p => p.Identity == identity);
            return process is null || process.IsSystem ? null : $@"C:\Demo\{Path.GetFileNameWithoutExtension(process.Name)}\{process.Name}";
        }
    }

    /// <summary>Removes a simulated process. Returns false when it does not exist or is a system process.</summary>
    public bool TryTerminate(ProcessIdentity identity, out bool isSystem)
    {
        lock (_lock)
        {
            var process = _processes.FirstOrDefault(p => p.Identity == identity);
            isSystem = process?.IsSystem ?? false;
            return process is not null && !process.IsSystem && _processes.Remove(process);
        }
    }

    public SystemInformation GetSystemInformation() => new()
    {
        ComputerName = ComputerName,
        OperatingSystem = new OperatingSystemInfo
        {
            ProductName = "Windows 11 Pro (demo)",
            DisplayVersion = "24H2",
            Build = "10.0.26100.1000",
        },
        Processor = new ProcessorInfo
        {
            Name = "Simulated 8-Core Processor (demo)",
            Vendor = "Demo",
            Sockets = 1,
            PhysicalCores = Cores,
            LogicalProcessors = LogicalProcessors,
            BaseFrequencyGHz = 3.6,
            L2CacheBytes = 8UL << 20,
            L3CacheBytes = 32UL << 20,
        },
        Gpus = [new GpuAdapterInfo("demo-gpu-0", "Simulated GPU (demo)") { DedicatedMemoryBytes = 8UL << 30, SharedMemoryBytes = 16UL << 30 }],
        InstalledMemoryBytes = TotalMemory,
        UsableMemoryBytes = TotalMemory,
        OsArchitecture = "X64",
        ProcessArchitecture = "X64",
        Firmware = new FirmwareInfo
        {
            SystemManufacturer = "Demo Computers",
            SystemModel = "Model D",
            BoardManufacturer = "Demo Boards",
            BoardProduct = "DB-1000",
            BiosVendor = "Demo BIOS",
            BiosVersion = "1.0.0",
            BiosReleaseDate = "01/01/2026",
        },
        BootTime = _bootTime,
    };

    private double Wave(double baseline, double amplitude, double periodSeconds) =>
        baseline + (amplitude * Math.Sin(2 * Math.PI * Phase / periodSeconds));

    private static double Clamp(double percent) => Math.Clamp(percent, 0, 100);

    private static List<SimulatedProcess> CreateProcesses(DateTimeOffset now)
    {
        var processes = new List<SimulatedProcess>
        {
            new(4, "System", 3, 0.4, 0.2, IsSystem: true),
            new(120, "Registry", 4, 0, 45, IsSystem: true),
            new(2300, "Memory Compression", 4, 0, 380, IsSystem: true),
            new(9001, "editor.exe", 1, 4, 580, IsSystem: false),
            new(9100, "chat.exe", 1, 2, 640, IsSystem: false),
            new(9200, "music.exe", 1, 1.2, 210, IsSystem: false),
            new(9300, "explorer.exe", 1, 0.6, 140, IsSystem: false),
            new(9400, "Sysora.exe", 1, 0.3, 85, IsSystem: false),
        };

        for (var i = 0; i < 6; i++)
        {
            processes.Add(new(12452 + (i * 4), "browser.exe", 1, i == 0 ? 6 : 1.5, i == 0 ? 520 : 220, IsSystem: false));
        }

        foreach (var process in processes)
        {
            process.StartTime = now - TimeSpan.FromMinutes(process.ProcessId % 600);
        }

        return processes;
    }

    private sealed record SimulatedProcess(int ProcessId, string Name, int ParentId, double BaseCpu, double BaseMemoryMb, bool IsSystem)
    {
        public DateTimeOffset StartTime { get; set; }

        public ProcessIdentity Identity => new(ProcessId, StartTime.UtcTicks);

        public ProcessMetrics Sample(double phase, Random random)
        {
            var cpu = Math.Max(0, BaseCpu * (1 + (0.6 * Math.Sin((phase / 9) + ProcessId))) + ((random.NextDouble() - 0.5) * BaseCpu * 0.3));
            var memory = (ulong)(BaseMemoryMb * (1 + (0.05 * Math.Sin((phase / 60) + ProcessId))) * ByteSize.BytesPerMegabyte);
            return new ProcessMetrics(Identity, Name)
            {
                ParentProcessId = ParentId,
                StartTime = StartTime,
                ExecutablePath = IsSystem ? null : $@"C:\Demo\{Path.GetFileNameWithoutExtension(Name)}\{Name}",
                CpuPercent = cpu,
                PrivateWorkingSetBytes = memory,
                WorkingSetBytes = memory + (memory / 3),
                PrivateBytes = memory + (memory / 5),
                IoReadBytesPerSecond = cpu * 20_000,
                IoWriteBytesPerSecond = cpu * 8_000,
                ThreadCount = 8 + (ProcessId % 40),
                HandleCount = 200 + (ProcessId % 900),
                SessionId = IsSystem ? 0 : 1,
            };
        }
    }
}
