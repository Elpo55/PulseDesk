using Microsoft.Extensions.Logging.Abstractions;
using Sysora.Core.History;
using Sysora.Infrastructure.Storage;

namespace Sysora.Tests.History;

public sealed class HistoryRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = TestData.Start;
    private readonly HistoryRepository _repository = HistoryRepository.InMemory(NullLogger<HistoryRepository>.Instance);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _repository.InitializeAsync(Token);

    public ValueTask DisposeAsync() => _repository.DisposeAsync();

    [Fact]
    public async Task AppendedMinutes_CanBeReadBackForAPeriod()
    {
        await _repository.AppendAsync(new HistoryBatch([Minute(T0, 10), Minute(T0.AddMinutes(1), 20), Minute(T0.AddMinutes(2), 30)], []), Token);

        var period = await _repository.GetSystemUsageAsync(T0.AddMinutes(1), T0.AddMinutes(3), HistoryResolution.Minute, Token);

        Assert.Equal(2, period.Count);
        Assert.Equal(T0.AddMinutes(1), period[0].Start);
        Assert.Equal(20, period[0].Cpu!.Value.Average);
        Assert.Equal(30, period[1].Cpu!.Value.Maximum);
        Assert.Null(period[0].Gpu);
        Assert.Equal(TestData.TotalMemory, period[0].MemoryTotalBytes);
    }

    [Fact]
    public async Task SameMinuteWrittenTwice_IsMergedWithWeightedAverage()
    {
        await _repository.AppendAsync(new HistoryBatch([Minute(T0, 10, samples: 30)], []), Token);
        await _repository.AppendAsync(new HistoryBatch([Minute(T0, 40, samples: 10)], []), Token);

        var merged = Assert.Single(await _repository.GetSystemUsageAsync(T0, T0.AddHours(1), HistoryResolution.Minute, Token));

        Assert.Equal(40, merged.SampleCount);
        Assert.Equal(17.5, merged.Cpu!.Value.Average, 6);
        Assert.Equal(40, merged.Cpu.Value.Maximum);
        Assert.Equal(40, merged.Cpu.Value.Count);
    }

    [Fact]
    public async Task Maintenance_RollsCompleteHoursUpIntoWeightedHourlySummaries()
    {
        var minutes = Enumerable.Range(0, 120).Select(i => Minute(T0.AddMinutes(i), i < 60 ? 10 : 50)).ToList();
        await _repository.AppendAsync(new HistoryBatch(minutes, []), Token);

        await _repository.RunMaintenanceAsync(new HistoryRetention(TimeSpan.FromDays(7), TimeSpan.FromDays(90)), T0.AddHours(2).AddMinutes(10), Token);

        var hours = await _repository.GetSystemUsageAsync(T0, T0.AddDays(1), HistoryResolution.Hour, Token);
        Assert.Equal(2, hours.Count);
        Assert.Equal(10, hours[0].Cpu!.Value.Average, 6);
        Assert.Equal(50, hours[1].Cpu!.Value.Average, 6);
        Assert.Equal(60 * 60, hours[0].SampleCount);
        Assert.Equal(60 * 59, hours[0].MonitoredSeconds, 6);
    }

    [Fact]
    public async Task Maintenance_DeletesDetailAfterRetention_ButKeepsHourlySummary()
    {
        await _repository.AppendAsync(new HistoryBatch([Minute(T0, 10)], [new SystemEvent(T0, SystemEventKind.MonitoringStarted, "Monitoring started")]), Token);

        await _repository.RunMaintenanceAsync(new HistoryRetention(TimeSpan.FromDays(1), TimeSpan.FromDays(30)), T0.AddDays(2), Token);

        Assert.Empty(await _repository.GetSystemUsageAsync(T0, T0.AddDays(3), HistoryResolution.Minute, Token));
        Assert.Single(await _repository.GetSystemUsageAsync(T0, T0.AddDays(3), HistoryResolution.Hour, Token));
        Assert.Single(await _repository.GetEventsAsync(T0, T0.AddDays(3), 10, Token));

        await _repository.RunMaintenanceAsync(new HistoryRetention(TimeSpan.FromDays(1), TimeSpan.FromDays(30)), T0.AddDays(40), Token);

        Assert.Empty(await _repository.GetSystemUsageAsync(T0, T0.AddDays(3), HistoryResolution.Hour, Token));
        Assert.Empty(await _repository.GetEventsAsync(T0, T0.AddDays(3), 10, Token));
    }

    [Fact]
    public async Task Events_AreReturnedOldestFirst_LimitedToMostRecent()
    {
        var events = Enumerable.Range(0, 5).Select(i => new SystemEvent(T0.AddMinutes(i), SystemEventKind.AppExited, $"e{i}") { AppKey = "name:A" }).ToList();
        await _repository.AppendAsync(new HistoryBatch([], events), Token);

        var latest = await _repository.GetEventsAsync(T0, T0.AddHours(1), 3, Token);

        Assert.Equal(["e2", "e3", "e4"], latest.Select(e => e.Title));
        Assert.Equal("name:A", latest[0].AppKey);
    }

    [Fact]
    public async Task Clear_RemovesEverything()
    {
        await _repository.AppendAsync(new HistoryBatch([Minute(T0, 10)], [new SystemEvent(T0, SystemEventKind.DataGap, "gap")]), Token);

        await _repository.ClearAsync(Token);

        Assert.Empty(await _repository.GetSystemUsageAsync(T0, T0.AddDays(1), HistoryResolution.Minute, Token));
        Assert.Empty(await _repository.GetEventsAsync(T0, T0.AddDays(1), 10, Token));
        Assert.Null((await _repository.GetStorageInfoAsync(Token)).OldestData);
    }

    [Fact]
    public async Task FileDatabase_PersistsAcrossInstances()
    {
        var directory = Directory.CreateTempSubdirectory("sysora-tests-");
        var file = Path.Combine(directory.FullName, "history.db");
        try
        {
            await using (var first = new HistoryRepository(file, NullLogger<HistoryRepository>.Instance))
            {
                await first.InitializeAsync(Token);
                await first.AppendAsync(new HistoryBatch([Minute(T0, 33)], []), Token);
            }

            await using var second = new HistoryRepository(file, NullLogger<HistoryRepository>.Instance);
            var stored = Assert.Single(await second.GetSystemUsageAsync(T0, T0.AddHours(1), HistoryResolution.Minute, Token));
            Assert.Equal(33, stored.Cpu!.Value.Average);
            Assert.True((await second.GetStorageInfoAsync(Token)).SizeBytes > 0);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SystemDriveFreeSpace_IsStoredAndRolledUp()
    {
        var minutes = Enumerable.Range(0, 60).Select(i => Minute(T0.AddMinutes(i), 10) with { SystemDriveFree = new AggregateValue(100.0 + i, 100.0 + i, 60) }).ToList();
        await _repository.AppendAsync(new HistoryBatch(minutes, []), Token);
        await _repository.RunMaintenanceAsync(new HistoryRetention(TimeSpan.FromDays(7), TimeSpan.FromDays(90)), T0.AddHours(1).AddMinutes(10), Token);

        var minute = (await _repository.GetSystemUsageAsync(T0, T0.AddMinutes(1), HistoryResolution.Minute, Token))[0];
        var hour = Assert.Single(await _repository.GetSystemUsageAsync(T0, T0.AddHours(1), HistoryResolution.Hour, Token));

        Assert.Equal(100, minute.SystemDriveFree!.Value.Average);
        Assert.Equal(129.5, hour.SystemDriveFree!.Value.Average, 6);
        Assert.Equal(TestData.TotalMemory, hour.MemoryTotalBytes);
    }

    [Fact]
    public async Task DatabaseOfAnEarlierVersion_GetsTheNewColumns_AndKeepsItsData()
    {
        var directory = Directory.CreateTempSubdirectory("sysora-tests-");
        var file = Path.Combine(directory.FullName, "history.db");
        try
        {
            // The system_usage table as created by Sysora 1.1 (no free-space column).
            await using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
            {
                await old.OpenAsync(Token);
                await using var command = old.CreateCommand();
                var metrics = string.Join(", ", new[] { "cpu", "mem", "disk", "rx", "tx", "gpu", "proc" }.Select(m => $"{m}_avg REAL, {m}_max REAL, {m}_n INTEGER NOT NULL DEFAULT 0"));
                command.CommandText = $"""
                    CREATE TABLE system_usage(resolution INTEGER NOT NULL, start INTEGER NOT NULL, samples INTEGER NOT NULL, seconds REAL NOT NULL, {metrics}, mem_total INTEGER, PRIMARY KEY(resolution, start)) WITHOUT ROWID;
                    INSERT INTO system_usage(resolution, start, samples, seconds, cpu_avg, cpu_max, cpu_n) VALUES(60, {T0.ToUnixTimeMilliseconds()}, 60, 59, 42, 50, 60);
                    """;
                await command.ExecuteNonQueryAsync(Token);
            }

            await using var repository = new HistoryRepository(file, NullLogger<HistoryRepository>.Instance);
            await repository.AppendAsync(new HistoryBatch([Minute(T0.AddMinutes(1), 10) with { SystemDriveFree = new AggregateValue(5, 5, 60) }], []), Token);
            var stored = await repository.GetSystemUsageAsync(T0, T0.AddHours(1), HistoryResolution.Minute, Token);

            Assert.Equal(2, stored.Count);
            Assert.Equal(42, stored[0].Cpu!.Value.Average);
            Assert.Null(stored[0].SystemDriveFree);
            Assert.Equal(5, stored[1].SystemDriveFree!.Value.Average);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AppUsage_IsSummarizedOverAPeriod_WithTimeWeightedAverages()
    {
        await _repository.AppendAsync(Apps(T0, ("a.exe", 10, 300)), Token);
        await _repository.AppendAsync(Apps(T0.AddMinutes(5), ("a.exe", 30, 100), ("b.exe", 2, 300)), Token);

        var usage = await _repository.GetAppUsageAsync(T0, T0.AddMinutes(10), Token);

        var a = Assert.Single(usage, u => u.Identity.Name == "a.exe");
        Assert.Equal(600, a.MonitoredSeconds, 6);
        Assert.Equal(400, a.ActiveSeconds, 6);
        Assert.Equal(((10 * 300) + (30 * 100)) / 400.0, a.CpuAverage, 6);
        Assert.Equal(T0, a.FirstSeen);
        Assert.Equal(10, a.FirstHalf!.Value.CpuAverage, 6);
        Assert.Equal(30, a.SecondHalf!.Value.CpuAverage, 6);
        Assert.Equal(@"C:\Apps\a.exe", a.Identity.ExecutablePath);
    }

    [Fact]
    public async Task AppUsage_AfterRollUp_IsNotCountedTwice()
    {
        for (var i = 0; i < 24; i++)
        {
            await _repository.AppendAsync(Apps(T0.AddMinutes(5 * i), ("a.exe", 10, 300)), Token);
        }

        // Detailed buckets older than one hour are deleted: the first hour is only left as an hourly summary,
        // the second hour exists both ways.
        await _repository.RunMaintenanceAsync(new HistoryRetention(TimeSpan.FromHours(1), TimeSpan.FromDays(90)), T0.AddHours(2).AddMinutes(10), Token);
        var usage = Assert.Single(await _repository.GetAppUsageAsync(T0, T0.AddHours(2), Token));
        var hourly = await _repository.GetAppTimelineAsync(Sysora.Core.Analysis.AppIdentity.Create("a.exe", @"C:\Apps\a.exe").Key, T0, T0.AddHours(2), HistoryResolution.Hour, Token);

        Assert.Equal(7200, usage.MonitoredSeconds, 6);
        Assert.Equal(7200, usage.ActiveSeconds, 6);
        Assert.Equal(10, usage.CpuAverage, 6);
        Assert.Equal(2, hourly.Count);
        Assert.Equal(3600, hourly[0].ActiveSeconds, 6);
    }

    private static HistoryBatch Apps(DateTimeOffset start, params (string Name, double Cpu, double Active)[] apps) =>
        new([], [])
        {
            AppUsage =
            [
                new AppUsageBucket(start, HistoryResolution.FiveMinutes, 300, 150, apps.Select(a => new AppUsageAggregate
                {
                    AppKey = Sysora.Core.Analysis.AppIdentity.Create(a.Name, $@"C:\Apps\{a.Name}").Key,
                    Name = a.Name,
                    ExecutablePath = $@"C:\Apps\{a.Name}",
                    Start = start,
                    Resolution = HistoryResolution.FiveMinutes,
                    Samples = (int)(a.Active / 2),
                    ActiveSeconds = a.Active,
                    CpuAverage = a.Cpu,
                    CpuMaximum = a.Cpu,
                    MemoryAverageBytes = 100,
                    MemoryMaximumBytes = 100,
                }).ToList()),
            ],
        };

    private static SystemUsageAggregate Minute(DateTimeOffset start, double cpu, int samples = 60) => new()
    {
        Start = start,
        Resolution = HistoryResolution.Minute,
        SampleCount = samples,
        MonitoredSeconds = samples - 1,
        Cpu = new AggregateValue(cpu, cpu, samples),
        Memory = new AggregateValue(40, 45, samples),
        MemoryTotalBytes = TestData.TotalMemory,
    };
}
