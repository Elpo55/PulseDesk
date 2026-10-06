using System.ComponentModel;
using System.Runtime.InteropServices;
using Sysora.Core.Interfaces;
using Sysora.Core.Models;
using Sysora.Infrastructure.Windows;

namespace Sysora.Infrastructure.Performance;

/// <summary>
/// Memory metrics from <c>GlobalMemoryStatusEx</c> (physical memory) and <c>GetPerformanceInfo</c>
/// (commit charge, system cache, kernel pools). Two cheap system calls, no performance counters.
/// </summary>
public sealed class WindowsMemoryMetricProvider : IMemoryMetricProvider
{
    public Task<MemoryMetrics> CollectAsync(CancellationToken cancellationToken)
    {
        var status = new NativeMethods.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var metrics = MemoryMetrics.FromTotalAndAvailable(status.TotalPhys, status.AvailPhys);

        if (NativeMethods.GetPerformanceInfo(out var performance, (uint)Marshal.SizeOf<NativeMethods.PerformanceInformation>()))
        {
            var pageSize = (ulong)performance.PageSize;
            metrics = metrics with
            {
                CommittedBytes = (ulong)performance.CommitTotal * pageSize,
                CommitLimitBytes = (ulong)performance.CommitLimit * pageSize,
                CachedBytes = (ulong)performance.SystemCache * pageSize,
                PagedPoolBytes = (ulong)performance.KernelPaged * pageSize,
                NonPagedPoolBytes = (ulong)performance.KernelNonpaged * pageSize,
            };
        }

        return Task.FromResult(metrics);
    }
}
