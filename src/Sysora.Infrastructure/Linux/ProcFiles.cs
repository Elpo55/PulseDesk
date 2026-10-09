using System.Globalization;
using Sysora.Core.Metrics;
using Sysora.Core.Models;

namespace Sysora.Infrastructure.Linux;

/// <summary>Busy and total time of one processor (or of all of them), in clock ticks since boot.</summary>
internal readonly record struct CpuTicks(ulong Idle, ulong Total)
{
    /// <summary>Share of the time between two readings the processor was busy, 0–100; null when no time passed.</summary>
    public static double? Usage(CpuTicks before, CpuTicks after)
    {
        if (after.Total <= before.Total)
        {
            return null;
        }

        var total = after.Total - before.Total;
        var idle = after.Idle >= before.Idle ? after.Idle - before.Idle : 0;
        return Percentages.Clamp(100.0 * (total - Math.Min(idle, total)) / total);
    }
}

/// <summary>A mounted file system, from /proc/self/mounts.</summary>
internal sealed record LinuxMount(string Device, string MountPoint, string FileSystem);

/// <summary>Parsers of the Linux kernel's text files under /proc. Pure: they never read the disk themselves.</summary>
internal static class ProcFiles
{
    /// <summary>File systems backed by a disk that are worth showing (pseudo and read-only image file systems are left out).</summary>
    private static readonly HashSet<string> DiskFileSystems = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "f2fs", "jfs", "reiserfs", "zfs", "bcachefs",
        "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "hfsplus", "apfs",
    };

    /// <summary>
    /// /proc/stat: the "cpu" line (all processors) then one "cpuN" line per processor. Idle time includes I/O wait;
    /// guest time is already counted in user time, so it is not added again.
    /// </summary>
    public static (CpuTicks All, IReadOnlyList<CpuTicks> PerProcessor) ParseStat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        CpuTicks? all = null;
        var perProcessor = new List<CpuTicks>();
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5)
            {
                continue;
            }

            var values = fields.Skip(1).Take(8).Select(f => ulong.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
            var idle = values[3] + (values.Length > 4 ? values[4] : 0);
            var ticks = new CpuTicks(idle, values.Aggregate(0UL, (sum, v) => sum + v));
            if (fields[0] == "cpu")
            {
                all = ticks;
            }
            else
            {
                perProcessor.Add(ticks);
            }
        }

        return (all ?? throw new FormatException("/proc/stat has no cpu line."), perProcessor);
    }

    /// <summary>
    /// /proc/meminfo, in kB. Available memory is the kernel's own estimate (MemAvailable, Linux 3.14+), or free + buffers +
    /// page cache on older kernels. Commit values are left out: with the default overcommit policy the kernel does not
    /// enforce CommitLimit, so comparing them would raise false alarms.
    /// </summary>
    public static MemoryMetrics ParseMemInfo(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var values = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var number = line[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (ulong.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var kb))
            {
                values[line[..colon]] = kb * 1024;
            }
        }

        if (!values.TryGetValue("MemTotal", out var total) || total == 0)
        {
            throw new FormatException("/proc/meminfo has no MemTotal.");
        }

        var cached = values.GetValueOrDefault("Cached");
        var available = values.TryGetValue("MemAvailable", out var reported)
            ? reported
            : values.GetValueOrDefault("MemFree") + values.GetValueOrDefault("Buffers") + cached;
        return MemoryMetrics.FromTotalAndAvailable(total, available) with { CachedBytes = cached };
    }

    /// <summary>/proc/uptime: seconds since boot (including suspend), then idle seconds.</summary>
    public static TimeSpan ParseUptime(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var first = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new FormatException("/proc/uptime is not a number of seconds.");
    }

    /// <summary>/proc/cpuinfo: the average "cpu MHz" of the processors, in GHz; null when the kernel does not report it (most ARM systems).</summary>
    public static double? ParseCurrentFrequencyGHz(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var mhz = new List<double>();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("cpu MHz", StringComparison.Ordinal)
                && line.IndexOf(':', StringComparison.Ordinal) is var colon and > 0
                && double.TryParse(line[(colon + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && value > 0)
            {
                mhz.Add(value);
            }
        }

        return mhz.Count > 0 ? Math.Round(mhz.Average() / 1000, 2) : null;
    }

    /// <summary>
    /// /proc/self/mounts: disk file systems only (no proc, sysfs, tmpfs, overlay, snap images...), each mount point once.
    /// Spaces and other characters are escaped as octal ("\040").
    /// </summary>
    public static IReadOnlyList<LinuxMount> ParseMounts(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var mounts = new List<LinuxMount>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !fields[0].StartsWith('/') || !DiskFileSystems.Contains(fields[2]))
            {
                continue;
            }

            var mountPoint = Unescape(fields[1]);
            if (mountPoint.StartsWith("/snap/", StringComparison.Ordinal) || mountPoint.StartsWith("/boot/efi", StringComparison.Ordinal) || !seen.Add(mountPoint))
            {
                continue;
            }

            mounts.Add(new LinuxMount(Unescape(fields[0]), mountPoint, fields[2]));
        }

        return mounts;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && IsOctal(value, i + 1))
            {
                result.Append((char)Convert.ToInt32(value.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                result.Append(value[i]);
            }
        }

        return result.ToString();
    }

    private static bool IsOctal(string value, int start) =>
        start + 3 <= value.Length && value.AsSpan(start, 3).IndexOfAnyExceptInRange('0', '7') < 0;
}
