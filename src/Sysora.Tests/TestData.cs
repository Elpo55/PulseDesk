using Sysora.Core.History;
using Sysora.Core.Models;

namespace Sysora.Tests;

/// <summary>Builders for deterministic test data.</summary>
internal static class TestData
{
    public static readonly DateTimeOffset Start = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    public const ulong TotalMemory = 16UL << 30;

    /// <summary>A monitor snapshot with the given CPU and memory usage.</summary>
    public static SystemSnapshot System(DateTimeOffset time, double cpu, double memoryPercent = 40, IReadOnlyList<ProcessMetrics>? processes = null, double? disk = 5) =>
        new()
        {
            Timestamp = time,
            Cpu = new CpuMetrics(cpu, null, null, 8),
            Memory = MemoryMetrics.FromTotalAndAvailable(TotalMemory, (ulong)(TotalMemory * (1 - (memoryPercent / 100)))),
            DiskActivity = disk is { } d ? [new DiskActivityMetrics("C:") { ActiveTimePercent = d, ReadBytesPerSecond = 1000, WriteBytesPerSecond = 500 }] : null,
            Processes = processes is null ? null : new ProcessSnapshot(processes),
        };

    /// <summary>A process with the given usage.</summary>
    public static ProcessMetrics Process(int pid, string name, double cpu, double memoryMb, string? path = null, long created = 1, double io = 0) =>
        new(new ProcessIdentity(pid, created), name)
        {
            CpuPercent = cpu,
            PrivateWorkingSetBytes = (ulong)(memoryMb * 1024 * 1024),
            ExecutablePath = path,
            IoReadBytesPerSecond = io,
            IoWriteBytesPerSecond = 0,
        };

    /// <summary>A history snapshot.</summary>
    public static MetricSnapshot Metric(DateTimeOffset time, double? cpu, double? memory = 40, double? disk = 5, IReadOnlyList<AppSample>? apps = null) =>
        new()
        {
            Timestamp = time,
            CpuPercent = cpu,
            MemoryPercent = memory,
            MemoryTotalBytes = TotalMemory,
            MemoryUsedBytes = memory is { } m ? (ulong)(TotalMemory * m / 100) : null,
            DiskActivePercent = disk,
            DiskActiveDrive = "C:",
            ProcessCount = 120,
            TopApps = apps ?? [],
        };

    /// <summary>One snapshot per second from <paramref name="from"/>, with values from <paramref name="cpu"/> and <paramref name="memory"/>.</summary>
    public static List<MetricSnapshot> Series(DateTimeOffset from, int seconds, Func<int, double?> cpu, Func<int, double?>? memory = null, Func<int, double?>? disk = null, Func<int, IReadOnlyList<AppSample>>? apps = null) =>
        Enumerable.Range(0, seconds)
            .Select(i => Metric(from.AddSeconds(i), cpu(i), memory is null ? 40 : memory(i), disk is null ? 5 : disk(i), apps?.Invoke(i)))
            .ToList();

    /// <summary>An application sample.</summary>
    public static AppSample App(string name, double cpu, double memoryMb, double io = 0) =>
        new("name:" + name.ToUpperInvariant(), name, 1, cpu, (ulong)(memoryMb * 1024 * 1024), io);
}

/// <summary>A settings store that keeps nothing (tests never touch the user's settings file).</summary>
internal sealed class NullSettingsStore : Sysora.Core.Interfaces.ISettingsStore
{
    public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task WriteAsync(string content, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task QuarantineAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Settings with optional changes applied.</summary>
    public static Sysora.Core.Settings.SettingsService Create(Func<Sysora.Core.Settings.AppSettings, Sysora.Core.Settings.AppSettings>? change = null, TimeProvider? time = null)
    {
        var settings = new Sysora.Core.Settings.SettingsService(
            new NullSettingsStore(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Sysora.Core.Settings.SettingsService>.Instance,
            time);
        if (change is not null)
        {
            settings.Update(change);
        }

        return settings;
    }
}
