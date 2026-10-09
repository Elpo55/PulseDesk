using Microsoft.Extensions.Logging;
using Sysora.Core.Interfaces;
using Sysora.Core.Models;

namespace Sysora.Infrastructure.Storage;

/// <summary>
/// Volume capacity from <see cref="DriveInfo"/> (GetDiskFreeSpaceEx / GetVolumeInformation).
/// </summary>
/// <remarks>
/// Network drives are skipped on purpose: querying a disconnected mapped drive can block for many
/// seconds, which would stall the monitoring loop.
/// </remarks>
public sealed class WindowsStorageMetricProvider(ILogger<WindowsStorageMetricProvider> logger) : IStorageMetricProvider
{
    private static readonly string? SystemRoot = Path.GetPathRoot(Environment.SystemDirectory);
    private readonly HashSet<string> _reportedFailures = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<StorageMetrics>> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new List<StorageMetrics>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = ToKind(drive.DriveType);
            if (kind is DriveKind.Network or DriveKind.Unknown)
            {
                continue;
            }

            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                result.Add(StorageMetrics.FromTotalAndFree(drive.Name, (ulong)drive.TotalSize, (ulong)drive.TotalFreeSpace) with
                {
                    Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                    FileSystem = drive.DriveFormat,
                    Kind = kind,
                    IsSystemDrive = string.Equals(drive.Name, SystemRoot, StringComparison.OrdinalIgnoreCase),
                });
                _reportedFailures.Remove(drive.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A single unreadable volume (e.g. a locked BitLocker drive) must not hide the others.
                if (_reportedFailures.Add(drive.Name))
                {
                    logger.LogInformation("Volume {Drive} could not be read: {Message}", drive.Name, ex.Message);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<StorageMetrics>>(result);
    }

    private static DriveKind ToKind(DriveType type) => type switch
    {
        DriveType.Fixed => DriveKind.Fixed,
        DriveType.Removable => DriveKind.Removable,
        DriveType.Network => DriveKind.Network,
        DriveType.CDRom => DriveKind.Optical,
        DriveType.Ram => DriveKind.RamDisk,
        _ => DriveKind.Unknown,
    };
}
