using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sysora.Core.Interfaces;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;
using Sysora.Infrastructure.Linux;

namespace Sysora.Infrastructure.MacOS;

/// <summary>
/// Overall CPU usage from the Mach host statistics (HOST_CPU_LOAD_INFO: user, system, idle and nice ticks).
/// Per-processor usage, clock speed and temperature are not read.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacCpuMetricProvider : ICpuMetricProvider
{
    private static readonly TimeSpan PrimingDelay = TimeSpan.FromMilliseconds(250);

    private CpuTicks? _previous;

    public async Task<CpuMetrics> CollectAsync(CancellationToken cancellationToken)
    {
        if (_previous is null)
        {
            _previous = MacNative.CpuTicks();
            await Task.Delay(PrimingDelay, cancellationToken).ConfigureAwait(false);
        }

        var now = MacNative.CpuTicks();
        var usage = CpuTicks.Usage(_previous.Value, now) ?? throw new MetricSampleSkippedException("No processor time passed between two readings.");
        _previous = now;
        return new CpuMetrics(usage, null, null, Environment.ProcessorCount);
    }
}

/// <summary>
/// Physical memory: total from sysctl hw.memsize; available = free + inactive pages from the Mach VM statistics
/// (HOST_VM_INFO64), the estimate most macOS tools use. Swap and compressed memory are not reported.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacMemoryMetricProvider : IMemoryMetricProvider
{
    public Task<MemoryMetrics> CollectAsync(CancellationToken cancellationToken)
    {
        var total = MacNative.SysctlUInt64("hw.memsize") ?? throw new InvalidOperationException("sysctl hw.memsize is not available.");
        var (free, inactive) = MacNative.FreeAndInactivePages();
        return Task.FromResult(MemoryMetrics.FromTotalAndAvailable(total, (free + inactive) * MacNative.PageSize));
    }
}

/// <summary>Time since the system started (sysctl kern.boottime, includes sleep).</summary>
[SupportedOSPlatform("macos")]
public sealed class MacSystemMetricProvider(TimeProvider? time = null) : ISystemMetricProvider
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<SystemMetrics> CollectAsync(CancellationToken cancellationToken)
    {
        var boot = MacNative.BootTime() ?? throw new InvalidOperationException("sysctl kern.boottime is not available.");
        var uptime = _time.GetUtcNow() - boot;
        return Task.FromResult(new SystemMetrics(uptime > TimeSpan.Zero ? uptime : TimeSpan.Zero));
    }
}

/// <summary>The few macOS system calls Sysora uses. Read-only.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacNative
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const int HostCpuLoadInfo = 3;
    private const int HostCpuLoadInfoCount = 4;
    private const int HostVmInfo64 = 4;
    private const int HostVmInfo64Count = 38;

    private static readonly Lazy<uint> Host = new(MachHostSelf);
    private static readonly Lazy<ulong> KernelPageSize = new(ReadPageSize);

    /// <summary>Size of the pages counted by the VM statistics.</summary>
    public static ulong PageSize => KernelPageSize.Value;

    /// <summary>All processors' ticks: idle, and the sum of user, system, idle and nice.</summary>
    public static CpuTicks CpuTicks()
    {
        var ticks = stackalloc uint[HostCpuLoadInfoCount];
        var count = (uint)HostCpuLoadInfoCount;
        var status = HostStatistics(Host.Value, HostCpuLoadInfo, (int*)ticks, ref count);
        if (status != 0)
        {
            throw new InvalidOperationException($"host_statistics(HOST_CPU_LOAD_INFO) failed ({status}).");
        }

        return new CpuTicks(ticks[2], (ulong)ticks[0] + ticks[1] + ticks[2] + ticks[3]);
    }

    /// <summary>Free and inactive pages (the first and third fields of vm_statistics64).</summary>
    public static (ulong Free, ulong Inactive) FreeAndInactivePages()
    {
        var info = stackalloc int[HostVmInfo64Count];
        var count = (uint)HostVmInfo64Count;
        var status = HostStatistics64(Host.Value, HostVmInfo64, info, ref count);
        if (status != 0)
        {
            throw new InvalidOperationException($"host_statistics64(HOST_VM_INFO64) failed ({status}).");
        }

        return ((uint)info[0], (uint)info[2]);
    }

    public static ulong? SysctlUInt64(string name)
    {
        ulong value = 0;
        var size = (nuint)sizeof(ulong);
        if (SysctlByName(name, &value, ref size, null, 0) != 0)
        {
            return null;
        }

        return size == sizeof(uint) ? (uint)value : value;
    }

    /// <summary>kern.boottime: a struct timeval (64-bit seconds, 32-bit microseconds).</summary>
    public static DateTimeOffset? BootTime()
    {
        var timeval = stackalloc long[2];
        var size = (nuint)(2 * sizeof(long));
        if (SysctlByName("kern.boottime", timeval, ref size, null, 0) != 0 || timeval[0] <= 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(timeval[0]).AddTicks((int)timeval[1] * TimeSpan.TicksPerMicrosecond);
    }

    /// <summary>
    /// The kernel's page size (vm_kernel_page_size), which the VM statistics count in: 16 KB on Apple silicon even for
    /// processes translated by Rosetta. Falls back to hw.pagesize.
    /// </summary>
    private static ulong ReadPageSize()
    {
        if (NativeLibrary.TryLoad(LibSystem, out var library) && NativeLibrary.TryGetExport(library, "vm_kernel_page_size", out var address))
        {
            var size = *(nuint*)address;
            if (size > 0)
            {
                return size;
            }
        }

        return SysctlUInt64("hw.pagesize") is { } pageSize and > 0 ? pageSize : 4096;
    }

    [LibraryImport(LibSystem, EntryPoint = "mach_host_self")]
    private static partial uint MachHostSelf();

    [LibraryImport(LibSystem, EntryPoint = "host_statistics")]
    private static partial int HostStatistics(uint host, int flavor, int* info, ref uint count);

    [LibraryImport(LibSystem, EntryPoint = "host_statistics64")]
    private static partial int HostStatistics64(uint host, int flavor, int* info, ref uint count);

    [LibraryImport(LibSystem, EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SysctlByName(string name, void* oldValue, ref nuint oldSize, void* newValue, nuint newSize);
}
