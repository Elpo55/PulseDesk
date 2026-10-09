using Microsoft.Extensions.Logging.Abstractions;
using Sysora.Core.Gaming;
using Sysora.Infrastructure.Gaming;
using Sysora.Infrastructure.Linux;
using Sysora.Infrastructure.MacOS;

namespace Sysora.Tests.Platforms;

/// <summary>
/// The Linux and macOS adapters. Parsers are tested with real file samples on every system; the providers themselves
/// are only run on the system they are written for (in CI: the GitHub Ubuntu and macOS runners), and skipped elsewhere.
/// </summary>
public sealed class UnixAdapterTests : IDisposable
{
    private const string Stat = """
        cpu  4705 356 584 3699 23 0 23 0 0 0
        cpu0 1393 280 290 1767 11 0 14 0 0 0
        cpu1 3312 76 294 1932 12 0 9 0 0 0
        intr 114930548 113199788 3 0 5 263 0 4 [... lots more numbers ...]
        ctxt 1990473
        btime 1062191376
        processes 2915
        procs_running 1
        """;

    private const string MemInfo = """
        MemTotal:       16314436 kB
        MemFree:         1050880 kB
        MemAvailable:    9612432 kB
        Buffers:          402200 kB
        Cached:          7682864 kB
        SwapCached:            0 kB
        CommitLimit:    10254728 kB
        Committed_AS:   21003316 kB
        HugePages_Total:       0
        """;

    private const string Mounts = """
        sysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0
        proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0
        udev /dev devtmpfs rw,nosuid,relatime,size=8131236k,nr_inodes=2032809,mode=755 0 0
        tmpfs /run tmpfs rw,nosuid,nodev,noexec,relatime,size=1634384k,mode=755 0 0
        /dev/nvme0n1p2 / ext4 rw,relatime,errors=remount-ro 0 0
        /dev/loop3 /snap/core22/1380 squashfs ro,nodev,relatime,errors=continue 0 0
        /dev/loop4 /snap/firefox/4173 squashfs ro,nodev,relatime,errors=continue 0 0
        /dev/nvme0n1p1 /boot/efi vfat rw,relatime,fmask=0077,dmask=0077 0 0
        /dev/sda1 /media/sam/My\040Backup exfat rw,nosuid,nodev,relatime 0 0
        /dev/nvme0n1p2 /var/snap/docker ext4 rw,relatime 0 0
        /dev/nvme0n1p2 / ext4 rw,relatime 0 0
        overlay /var/lib/docker/overlay2/abc/merged overlay rw,relatime 0 0
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sysora-unix-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ProcStatGivesOverallAndPerProcessorTicks()
    {
        var (all, perProcessor) = ProcFiles.ParseStat(Stat);

        Assert.Equal(3699UL + 23, all.Idle);
        Assert.Equal(4705UL + 356 + 584 + 3699 + 23 + 0 + 23 + 0, all.Total);
        Assert.Equal(2, perProcessor.Count);
        Assert.Throws<FormatException>(() => ProcFiles.ParseStat("intr 1 2 3"));
    }

    [Fact]
    public void CpuUsageComesFromTheTicksBetweenTwoReadings()
    {
        Assert.Equal(75, CpuTicks.Usage(new CpuTicks(100, 1000), new CpuTicks(150, 1200)));
        Assert.Equal(0, CpuTicks.Usage(new CpuTicks(100, 1000), new CpuTicks(300, 1200)));
        Assert.Null(CpuTicks.Usage(new CpuTicks(100, 1000), new CpuTicks(100, 1000)));
    }

    [Fact]
    public void MemInfoUsesTheKernelsAvailableEstimate()
    {
        var memory = ProcFiles.ParseMemInfo(MemInfo);

        Assert.Equal(16314436UL * 1024, memory.TotalBytes);
        Assert.Equal(9612432UL * 1024, memory.AvailableBytes);
        Assert.Equal((16314436UL - 9612432) * 1024, memory.UsedBytes);
        Assert.Equal(7682864UL * 1024, memory.CachedBytes);
        Assert.Null(memory.CommittedBytes);
        Assert.Null(memory.CommitLimitBytes);
    }

    [Fact]
    public void MemInfoWithoutMemAvailableFallsBackToFreeBuffersAndCache()
    {
        var memory = ProcFiles.ParseMemInfo("MemTotal: 1000 kB\nMemFree: 100 kB\nBuffers: 50 kB\nCached: 250 kB\n");

        Assert.Equal(400UL * 1024, memory.AvailableBytes);
        Assert.Throws<FormatException>(() => ProcFiles.ParseMemInfo("MemFree: 100 kB"));
    }

    [Fact]
    public void UptimeAndFrequencyAreRead()
    {
        Assert.Equal(TimeSpan.FromSeconds(350735.47), ProcFiles.ParseUptime("350735.47 234388.90\n"));
        Assert.Throws<FormatException>(() => ProcFiles.ParseUptime("soon"));
        Assert.Equal(2.5, ProcFiles.ParseCurrentFrequencyGHz("processor\t: 0\ncpu MHz\t\t: 2000.000\nprocessor\t: 1\ncpu MHz\t\t: 3000.000\n"));
        Assert.Null(ProcFiles.ParseCurrentFrequencyGHz("processor\t: 0\nBogoMIPS\t: 48.00\n"));
    }

    [Fact]
    public void OnlyDiskFileSystemsAreListedOnce()
    {
        var mounts = ProcFiles.ParseMounts(Mounts);

        Assert.Equal(["/", "/media/sam/My Backup", "/var/snap/docker"], mounts.Select(m => m.MountPoint));
        Assert.Equal(["ext4", "exfat", "ext4"], mounts.Select(m => m.FileSystem));
    }

    [Fact]
    public void LinuxSteamLibrariesAreFound()
    {
        var home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        var locations = UnixLauncherLocations.Linux(home);
        var steam = locations.SteamFolders[0];
        var apps = Directory.CreateDirectory(Path.Combine(steam, "steamapps")).FullName;
        File.WriteAllText(Path.Combine(apps, "libraryfolders.vdf"), "\"libraryfolders\" { }");
        File.WriteAllText(Path.Combine(apps, "appmanifest_570.acf"), "\"AppState\" { \"appid\" \"570\" \"name\" \"Dota 2\" \"StateFlags\" \"4\" \"installdir\" \"dota 2 beta\" }");
        File.WriteAllText(Path.Combine(apps, "appmanifest_1628350.acf"), "\"AppState\" { \"appid\" \"1628350\" \"name\" \"Steam Linux Runtime 3.0 (sniper)\" \"StateFlags\" \"4\" \"installdir\" \"SteamLinuxRuntime_sniper\" }");
        Directory.CreateDirectory(Path.Combine(apps, "common", "dota 2 beta"));
        Directory.CreateDirectory(Path.Combine(apps, "common", "SteamLinuxRuntime_sniper"));

        var games = LauncherGameScanner.Scan(locations);

        Assert.Equal(["Dota 2"], games.Select(g => g.Name));
        Assert.Contains(locations.SteamFolders, f => f.Contains("com.valvesoftware.Steam", StringComparison.Ordinal));
    }

    [Fact]
    public void MacOSLauncherFoldersAreInApplicationSupport()
    {
        var locations = UnixLauncherLocations.MacOS("/Users/sam");

        Assert.Equal(Path.Combine("/Users/sam", "Library", "Application Support", "Steam"), locations.SteamFolders[0]);
        Assert.EndsWith(Path.Combine("EpicGamesLauncher", "Data", "Manifests"), locations.EpicManifestFolders[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxProvidersReadThisSystem()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Runs on Linux only.");
            return;
        }

        var cpu = await new LinuxCpuMetricProvider().CollectAsync(Token);
        Assert.InRange(cpu.UsagePercent, 0, 100);
        Assert.Equal(Environment.ProcessorCount, cpu.LogicalProcessors);
        Assert.All(cpu.LogicalProcessorUsage, u => Assert.InRange(u, 0, 100));

        var memory = await new LinuxMemoryMetricProvider().CollectAsync(Token);
        Assert.True(memory.TotalBytes > 0);
        Assert.InRange(memory.AvailableBytes, 1UL, memory.TotalBytes);

        var system = await new LinuxSystemMetricProvider().CollectAsync(Token);
        Assert.True(system.Uptime > TimeSpan.Zero);

        var storage = await new LinuxStorageMetricProvider(NullLogger<LinuxStorageMetricProvider>.Instance).CollectAsync(Token);
        Assert.Contains(storage, s => s.IsSystemDrive && s.TotalBytes > 0);
    }

    [Fact]
    public async Task MacOSProvidersReadThisSystem()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Runs on macOS only.");
            return;
        }

        var cpu = await new MacCpuMetricProvider().CollectAsync(Token);
        Assert.InRange(cpu.UsagePercent, 0, 100);

        var memory = await new MacMemoryMetricProvider().CollectAsync(Token);
        Assert.True(memory.TotalBytes > 0);
        Assert.InRange(memory.AvailableBytes, 1UL, memory.TotalBytes);

        var system = await new MacSystemMetricProvider().CollectAsync(Token);
        Assert.InRange(system.Uptime, TimeSpan.FromSeconds(1), TimeSpan.FromDays(3650));
    }
}
