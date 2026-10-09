using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Sysora.Core.Interfaces;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;

namespace Sysora.Infrastructure.Linux;

/// <summary>
/// CPU usage from /proc/stat (overall and per logical processor) and clock speed from /proc/cpuinfo.
/// Temperature is not read: its location under /sys depends on the hardware and driver.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxCpuMetricProvider(string procFolder = "/proc") : ICpuMetricProvider
{
    /// <summary>Delay between the two readings needed before the first value is meaningful.</summary>
    private static readonly TimeSpan PrimingDelay = TimeSpan.FromMilliseconds(250);

    private (CpuTicks All, IReadOnlyList<CpuTicks> PerProcessor)? _previous;
    private double _peakGhz;

    public async Task<CpuMetrics> CollectAsync(CancellationToken cancellationToken)
    {
        if (_previous is null)
        {
            _previous = await ReadStatAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(PrimingDelay, cancellationToken).ConfigureAwait(false);
        }

        var before = _previous.Value;
        var now = await ReadStatAsync(cancellationToken).ConfigureAwait(false);
        _previous = now;

        var usage = CpuTicks.Usage(before.All, now.All) ?? throw new MetricSampleSkippedException("No processor time passed between two readings of /proc/stat.");
        var perProcessor = before.PerProcessor.Count == now.PerProcessor.Count
            ? now.PerProcessor.Select((ticks, i) => CpuTicks.Usage(before.PerProcessor[i], ticks) ?? 0).ToArray()
            : [];

        double? ghz = null;
        try
        {
            ghz = ProcFiles.ParseCurrentFrequencyGHz(await File.ReadAllTextAsync(Path.Combine(procFolder, "cpuinfo"), cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The clock speed is optional.
        }

        if (ghz > _peakGhz)
        {
            _peakGhz = ghz.Value;
        }

        return new CpuMetrics(usage, null, ghz, now.PerProcessor.Count > 0 ? now.PerProcessor.Count : Environment.ProcessorCount)
        {
            LogicalProcessorUsage = perProcessor,
            PeakFrequencyGHz = _peakGhz > 0 ? _peakGhz : null,
        };
    }

    private async Task<(CpuTicks All, IReadOnlyList<CpuTicks> PerProcessor)> ReadStatAsync(CancellationToken cancellationToken) =>
        ProcFiles.ParseStat(await File.ReadAllTextAsync(Path.Combine(procFolder, "stat"), cancellationToken).ConfigureAwait(false));
}

/// <summary>Physical memory from /proc/meminfo (MemTotal, MemAvailable, Cached).</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxMemoryMetricProvider(string procFolder = "/proc") : IMemoryMetricProvider
{
    public async Task<MemoryMetrics> CollectAsync(CancellationToken cancellationToken) =>
        ProcFiles.ParseMemInfo(await File.ReadAllTextAsync(Path.Combine(procFolder, "meminfo"), cancellationToken).ConfigureAwait(false));
}

/// <summary>Time since the system started, from /proc/uptime (includes suspend).</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSystemMetricProvider(string procFolder = "/proc") : ISystemMetricProvider
{
    public async Task<SystemMetrics> CollectAsync(CancellationToken cancellationToken) =>
        new(ProcFiles.ParseUptime(await File.ReadAllTextAsync(Path.Combine(procFolder, "uptime"), cancellationToken).ConfigureAwait(false)));
}

/// <summary>
/// Capacity of the mounted disk file systems listed in /proc/self/mounts. File systems mounted under /media or /run/media
/// are shown as removable; the one mounted at "/" is the system drive.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxStorageMetricProvider(ILogger<LinuxStorageMetricProvider> logger, string mountsFile = "/proc/self/mounts") : IStorageMetricProvider
{
    private readonly HashSet<string> _reportedFailures = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<StorageMetrics>> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new List<StorageMetrics>();
        foreach (var mount in ProcFiles.ParseMounts(await File.ReadAllTextAsync(mountsFile, cancellationToken).ConfigureAwait(false)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var drive = new DriveInfo(mount.MountPoint);
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                var removable = mount.MountPoint.StartsWith("/media/", StringComparison.Ordinal) || mount.MountPoint.StartsWith("/run/media/", StringComparison.Ordinal);
                result.Add(StorageMetrics.FromTotalAndFree(mount.MountPoint, (ulong)drive.TotalSize, (ulong)drive.TotalFreeSpace) with
                {
                    Label = mount.MountPoint == "/" ? null : Path.GetFileName(mount.MountPoint),
                    FileSystem = mount.FileSystem,
                    Kind = removable ? DriveKind.Removable : DriveKind.Fixed,
                    IsSystemDrive = mount.MountPoint == "/",
                });
                _reportedFailures.Remove(mount.MountPoint);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (_reportedFailures.Add(mount.MountPoint))
                {
                    logger.LogInformation("File system {MountPoint} could not be read: {Message}", mount.MountPoint, ex.Message);
                }
            }
        }

        return result;
    }
}
