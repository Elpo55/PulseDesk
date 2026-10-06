using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Sysora.Core.Analysis;
using Sysora.Core.Changes;
using Sysora.Core.Interfaces;
using Sysora.Core.Models;
using Sysora.Core.Simulation;
using Sysora.Infrastructure.Storage;
using Sysora.Infrastructure.SystemInfo;

namespace Sysora.Tests.Changes;

public sealed class ChangeDetectionTests
{
    private const ulong Gigabyte = 1024UL * 1024 * 1024;
    private static readonly DateTimeOffset Day1 = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = Day1.AddDays(1);

    [Fact]
    public void NoModification_FindsNoChange()
    {
        var before = Baseline(Day1);
        var after = Baseline(Day2);

        Assert.Empty(BaselineComparer.Compare(before, after));
    }

    [Fact]
    public void NewApplication_IsReported_WithTheInstallerDate()
    {
        var before = Baseline(Day1);
        var installDate = DateOnly.FromDateTime(Day2.ToLocalTime().Date);
        var after = Baseline(Day2, apps: [.. Apps(), new InstalledApp("Machine|new", "Photo Studio") { Version = "3.1", Publisher = "Studio Inc.", InstallDate = installDate }]);

        var change = Assert.Single(BaselineComparer.Compare(before, after));

        Assert.Equal(ChangeType.AppInstalled, change.Type);
        Assert.Equal("Photo Studio installed", change.Title);
        Assert.Equal("3.1", change.NewValue);
        Assert.Equal(ChangeImportance.Medium, change.Importance);
        Assert.Contains("recorded by the installer", change.Origin, StringComparison.Ordinal);
        Assert.True(change.After >= Day1 && change.Before <= Day2);
    }

    [Fact]
    public void ApplicationsUpdated_InPlaceOrUnderANewKey_AreUpdatesNotInstalls()
    {
        var before = Baseline(Day1, apps:
        [
            new InstalledApp("Machine|browser", "Browser") { Version = "120.0", Publisher = "Web Co" },
            new InstalledApp("Machine|{OLD-GUID}", "Runtime 8.0.1 (x64)") { Version = "8.0.1", Publisher = "Runtime Org" },
        ]);
        var after = Baseline(Day2, apps:
        [
            new InstalledApp("Machine|browser", "Browser") { Version = "121.0", Publisher = "Web Co" },
            new InstalledApp("Machine|{NEW-GUID}", "Runtime 8.0.4 (x64)") { Version = "8.0.4", Publisher = "Runtime Org" },
        ]);

        var changes = BaselineComparer.Compare(before, after);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(ChangeType.AppUpdated, c.Type));
        Assert.Contains(changes, c => c.OldValue == "120.0" && c.NewValue == "121.0");
        Assert.Contains(changes, c => c.OldValue == "8.0.1" && c.NewValue == "8.0.4" && c.Origin.Contains("name and publisher", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplicationRemoved_IsReported()
    {
        var after = Baseline(Day2, apps: [Apps()[0]]);

        var change = Assert.Single(BaselineComparer.Compare(Baseline(Day1), after));

        Assert.Equal(ChangeType.AppRemoved, change.Type);
        Assert.Equal("Chat removed", change.Title);
    }

    [Fact]
    public void HigherAverageMemory_IsAChange_WithUnknownOrigin()
    {
        var before = Baseline(Day1, usage: new UsageSummary(1200) { MemoryAverage = 38, CpuAverage = 20 });
        var after = Baseline(Day2, usage: new UsageSummary(900) { MemoryAverage = 50, CpuAverage = 22 });

        var change = Assert.Single(BaselineComparer.Compare(before, after));

        Assert.Equal(ChangeType.MemoryUsageChanged, change.Type);
        Assert.Equal("Average memory usage +12 points", change.Title);
        Assert.Equal("38%", change.OldValue);
        Assert.Equal("50%", change.NewValue);
        Assert.Equal(BaselineComparer.UnknownOrigin, change.Origin);
    }

    [Fact]
    public void UsageWithTooLittleHistory_IsNotCompared()
    {
        var before = Baseline(Day1, usage: new UsageSummary(30) { MemoryAverage = 38 });
        var after = Baseline(Day2, usage: new UsageSummary(900) { MemoryAverage = 70 });

        Assert.Empty(BaselineComparer.Compare(before, after));
    }

    [Fact]
    public void SystemChanges_AreDetected_WithOldAndNewValues()
    {
        var before = Baseline(Day1);
        var after = Baseline(Day2, startup:
            [
                new StartupProgram("HKCU Run|Chat", "Chat", "chat.exe", "HKCU Run") { Enabled = false },
                new StartupProgram("HKCU Run|Updater", "Updater", "updater.exe /bg", "HKCU Run") { Enabled = true },
            ]) with
        {
            OsBuild = "10.0.26100.4770",
            Inventory = Baseline(Day2).Inventory with
            {
                StartupPrograms =
                [
                    new StartupProgram("HKCU Run|Chat", "Chat", "chat.exe", "HKCU Run") { Enabled = false },
                    new StartupProgram("HKCU Run|Updater", "Updater", "updater.exe /bg", "HKCU Run") { Enabled = true },
                ],
                Devices = [new DeviceInfo("Graphics", "GPU|8", "GPU"), Volume(used: 425 * Gigabyte)],
            },
        };

        var changes = BaselineComparer.Compare(before, after);

        var windows = Assert.Single(changes, c => c.Type == ChangeType.WindowsUpdated);
        Assert.Equal("24H2 · 10.0.26100.4652", windows.OldValue);
        Assert.Equal("24H2 · 10.0.26100.4770", windows.NewValue);
        Assert.Equal(ChangeImportance.High, Assert.Single(changes, c => c.Type == ChangeType.StartupProgramAdded).Importance);
        Assert.Single(changes, c => c.Type == ChangeType.StartupProgramDisabled);
        var disk = Assert.Single(changes, c => c.Type == ChangeType.DiskSpaceChanged);
        Assert.Equal("25 GB more used on C:", disk.Title);
        Assert.StartsWith(BaselineComparer.UnknownOrigin, disk.Origin, StringComparison.Ordinal);
    }

    [Fact]
    public void SmallDiskSpaceChange_IsIgnored()
    {
        // 3 GB on a 1000 GB volume: below both the 5 GB and the 2% thresholds.
        var after = Baseline(Day2) with { Inventory = Baseline(Day2).Inventory with { Devices = [new DeviceInfo("Graphics", "GPU|8", "GPU"), Volume(used: 403 * Gigabyte)] } };

        Assert.Empty(BaselineComparer.Compare(Baseline(Day1), after));
    }

    [Fact]
    public void FirstSnapshot_ListsRecentInstallsFromWindowsRecords_Only()
    {
        var today = DateOnly.FromDateTime(Day2.ToLocalTime().Date);
        var current = Baseline(Day2, apps:
        [
            new InstalledApp("Machine|a", "Recent App") { InstallDate = today.AddDays(-2) },
            new InstalledApp("Machine|b", "Old App") { InstallDate = today.AddDays(-200) },
            new InstalledApp("Machine|c", "No Date App"),
            new InstalledApp("Store|d", "Store App") { InstallDate = today.AddDays(-1), Source = "Store" },
        ]);

        var change = Assert.Single(BaselineComparer.RecentlyInstalled(current, 30));

        // The installer's date may be an update date; Store dates always are (left out).
        Assert.Equal("Recent App installed or updated", change.Title);
        Assert.Equal(ChangeImportance.Low, change.Importance);
        Assert.Null(change.OldValue);
    }

    [Fact]
    public void ChangeIds_AreDeterministic()
    {
        Assert.Equal(BaselineComparer.ChangeId(ChangeType.AppInstalled, "x", "1"), BaselineComparer.ChangeId(ChangeType.AppInstalled, "x", "1"));
        Assert.NotEqual(BaselineComparer.ChangeId(ChangeType.AppInstalled, "x", "1"), BaselineComparer.ChangeId(ChangeType.AppRemoved, "x", "1"));
    }

    [Fact]
    public void NewFrequentApp_RequiresPriorHistory()
    {
        var now = Day1.AddDays(10);
        var catalog = new[]
        {
            new KnownApp("name:OLD.EXE", "old.exe", null, Day1, now),
            new KnownApp("name:NEW.EXE", "new.exe", null, now.AddDays(-2), now),
        };
        var usage = new[]
        {
            new AppUsageStatistics { Identity = AppIdentity.Create("new.exe", null), ActiveSeconds = 7200, Launches = 3, Samples = 100 },
        };

        var change = Assert.Single(FrequentAppDetector.Detect(catalog, usage, now));
        Assert.Equal("new.exe now runs regularly", change.Title);

        // When Sysora itself started recording at the same time, nothing can be called new.
        Assert.Empty(FrequentAppDetector.Detect([catalog[1]], usage, now));
    }

    [Fact]
    public void ReferenceSnapshots_ArePickedByDay()
    {
        var now = new DateTimeOffset(DateTime.Now.Date.AddHours(15));
        var snapshots = new[] { Baseline(now.AddDays(-40)), Baseline(now.AddDays(-8)), Baseline(now.AddDays(-1)), Baseline(now.AddHours(-6)) };

        Assert.Same(snapshots[3], ChangeDetectionService.Pick(snapshots, BaselineReference.Today, now));
        Assert.Same(snapshots[2], ChangeDetectionService.Pick(snapshots, BaselineReference.Yesterday, now));
        Assert.Same(snapshots[1], ChangeDetectionService.Pick(snapshots, BaselineReference.SevenDaysAgo, now));
        Assert.Same(snapshots[0], ChangeDetectionService.Pick(snapshots, BaselineReference.ThirtyDaysAgo, now));
        Assert.Null(ChangeDetectionService.Pick(snapshots.Skip(1).ToArray(), BaselineReference.ThirtyDaysAgo, now));
    }

    [Fact]
    public async Task Service_RecordsChangesOnce_AndKeepsOneSnapshotPerDay()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(Day1);
        await using var repository = HistoryRepository.InMemory(NullLogger<HistoryRepository>.Instance);
        await repository.InitializeAsync(token);
        var inventory = new MutableInventory(Inventory(Apps()));
        var service = new ChangeDetectionService(
            inventory,
            new FakeSystemInfoProvider(new SimulatedMachine(time)),
            new StubMonitor(),
            repository,
            NullSettingsStore.Create(time: time),
            NullLogger<ChangeDetectionService>.Instance,
            time);

        await service.RecordAsync(token);
        time.Advance(TimeSpan.FromHours(6));
        inventory.Current = Inventory([.. Apps(), new InstalledApp("Machine|new", "Photo Studio") { Version = "3.1" }]);
        var found = await service.RecordAsync(token);
        time.Advance(TimeSpan.FromHours(1));
        var again = await service.RecordAsync(token);

        Assert.Single(found, c => c.Type == ChangeType.AppInstalled);
        Assert.Single(again, c => c.Type == ChangeType.AppInstalled);
        var timeline = await service.GetTimelineAsync(Day1.AddDays(-1), token);
        Assert.Single(timeline, c => c.Title == "Photo Studio installed");
        Assert.Single(await service.GetSnapshotsAsync(token));
    }

    [Theory]
    [InlineData("20260302", 2026, 3, 2)]
    [InlineData("2026032", 0, 0, 0)]
    [InlineData("notadate", 0, 0, 0)]
    [InlineData(null, 0, 0, 0)]
    public void InstallDate_IsParsedStrictly(string? value, int year, int month, int day)
    {
        var parsed = InventoryParsing.ParseInstallDate(value);
        Assert.Equal(year == 0 ? null : new DateOnly(year, month, day), parsed);
    }

    [Fact]
    public void StartupApprovedFlags_FollowWindowsConvention()
    {
        Assert.True(InventoryParsing.IsApproved(null));
        Assert.True(InventoryParsing.IsApproved([2, 0, 0, 0]));
        Assert.False(InventoryParsing.IsApproved([3, 0, 0, 0]));
        Assert.False(InventoryParsing.IsPhysicalAdapter("Hyper-V Virtual Ethernet Adapter"));
        Assert.False(InventoryParsing.IsPhysicalAdapter("Realtek PCIe GbE Family Controller-WFP Native MAC Layer LightWeight Filter-0000"));
        Assert.False(InventoryParsing.IsPhysicalAdapter("MediaTek Wi-Fi 6E MT7922 (RZ616) 160MHz Wireless LAN Card-QoS Packet Scheduler-0000"));
        Assert.False(InventoryParsing.IsPhysicalAdapter("Microsoft Kernel Debug Network Adapter"));
        Assert.True(InventoryParsing.IsPhysicalAdapter("Intel(R) Wi-Fi 6E AX211 160MHz"));
        Assert.True(InventoryParsing.IsPhysicalAdapter("Realtek PCIe GbE Family Controller"));
    }

    private static IReadOnlyList<InstalledApp> Apps() =>
    [
        new InstalledApp("Machine|editor", "Editor") { Version = "2.0", Publisher = "Edit Co" },
        new InstalledApp("User|chat", "Chat") { Version = "5.0", Publisher = "Chat Inc." },
    ];

    private static DeviceInfo Volume(ulong used) => new("Volume", "C:", "C:") { TotalBytes = 1000 * Gigabyte, UsedBytes = used };

    private static SystemInventory Inventory(IReadOnlyList<InstalledApp> apps) => new()
    {
        Apps = apps,
        AppsAvailable = true,
        StartupAvailable = true,
        StartupPrograms = [new StartupProgram("HKCU Run|Chat", "Chat", "chat.exe", "HKCU Run") { Enabled = true }],
        Devices = [new DeviceInfo("Graphics", "GPU|8", "GPU"), Volume(used: 400 * Gigabyte)],
    };

    private static SystemBaseline Baseline(DateTimeOffset at, IReadOnlyList<InstalledApp>? apps = null, IReadOnlyList<StartupProgram>? startup = null, UsageSummary? usage = null) => new()
    {
        CapturedAt = at,
        OsName = "Windows 11 Pro",
        OsVersion = "24H2",
        OsBuild = "10.0.26100.4652",
        BiosVersion = "1.0",
        InstalledMemoryBytes = 32 * Gigabyte,
        Inventory = Inventory(apps ?? Apps()) with { StartupPrograms = startup ?? Inventory(Apps()).StartupPrograms },
        Usage = usage,
    };

    private sealed class MutableInventory(SystemInventory initial) : ISystemInventoryProvider
    {
        public SystemInventory Current { get; set; } = initial;

        public Task<SystemInventory> CollectAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
    }

    private sealed class StubMonitor : IMetricsMonitor
    {
        public event EventHandler<SystemMetricsUpdatedEventArgs>? MetricsUpdated { add { } remove { } }

        public event EventHandler? StateChanged { add { } remove { } }

        public SystemSnapshot Current => SystemSnapshot.Empty;

        public Sysora.Core.Metrics.MetricHistory History { get; } = new(10);

        public bool IsRunning => true;

        public bool IsPaused => false;

        public Sysora.Core.Monitoring.SelfUsage SelfUsage => new(null, 0, 1);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void SetActivity(MonitoringActivity activity)
        {
        }

        public void RequestRefresh(MetricKind kinds)
        {
        }
    }
}
