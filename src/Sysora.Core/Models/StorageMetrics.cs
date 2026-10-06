using Sysora.Core.Metrics;

namespace Sysora.Core.Models;

/// <summary>
/// Capacity of one volume.
/// </summary>
/// <param name="Drive">Root of the volume, e.g. "C:\".</param>
/// <param name="TotalBytes">Volume capacity.</param>
/// <param name="UsedBytes">Space in use.</param>
/// <param name="FreeBytes">Free space on the volume (not limited by per-user quotas).</param>
public sealed record StorageMetrics(
    string Drive,
    ulong TotalBytes,
    ulong UsedBytes,
    ulong FreeBytes)
{
    /// <summary>Volume label, when set.</summary>
    public string? Label { get; init; }

    /// <summary>File system name (NTFS, ReFS, exFAT...), when available.</summary>
    public string? FileSystem { get; init; }

    /// <summary>Kind of drive.</summary>
    public DriveKind Kind { get; init; } = DriveKind.Unknown;

    /// <summary>True when Windows is installed on this volume.</summary>
    public bool IsSystemDrive { get; init; }

    /// <summary>Share of the volume in use, 0–100.</summary>
    public double UsedPercent => Percentages.Of(UsedBytes, TotalBytes);

    /// <summary>Drive letter without the trailing separator, e.g. "C:".</summary>
    public string Letter => Drive.TrimEnd('\\', '/');

    /// <summary>Creates metrics from total and free space, deriving the used amount.</summary>
    public static StorageMetrics FromTotalAndFree(string drive, ulong totalBytes, ulong freeBytes)
    {
        var free = Math.Min(freeBytes, totalBytes);
        return new StorageMetrics(drive, totalBytes, totalBytes - free, free);
    }
}

/// <summary>Kind of storage volume.</summary>
public enum DriveKind
{
    Unknown,
    Fixed,
    Removable,
    Network,
    Optical,
    RamDisk,
}

/// <summary>
/// Activity of one volume over the last sampling interval.
/// </summary>
/// <param name="Drive">Drive letter, e.g. "C:".</param>
public sealed record DiskActivityMetrics(string Drive)
{
    /// <summary>Share of time the volume was busy, 0–100.</summary>
    public double? ActiveTimePercent { get; init; }

    /// <summary>Read throughput in bytes per second.</summary>
    public double? ReadBytesPerSecond { get; init; }

    /// <summary>Write throughput in bytes per second.</summary>
    public double? WriteBytesPerSecond { get; init; }
}
