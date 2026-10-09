using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sysora.Core.Interfaces;
using Sysora.Core.Models;
using Sysora.Infrastructure.Hardware;
using Sysora.Infrastructure.Windows;

namespace Sysora.Infrastructure.SystemInfo;

/// <summary>
/// Machine description from read-only sources: the registry (OS version, CPU name, SMBIOS data that
/// Windows copies there at boot), <c>GetLogicalProcessorInformationEx</c> and DXGI. No WMI query is made,
/// which keeps startup fast. Collected once and cached.
/// </summary>
public sealed class WindowsSystemInfoProvider(ILogger<WindowsSystemInfoProvider> logger) : ISystemInfoProvider
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SystemInformation? _cached;

    public async Task<SystemInformation> GetAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _cached) is { } cached)
        {
            return cached;
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SystemInformation> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var information = await Task.Run(Collect, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _cached, information);
            return information;
        }
        finally
        {
            _gate.Release();
        }
    }

    private SystemInformation Collect()
    {
        var topology = Safe(ProcessorTopology.Read, "processor topology")
            ?? new ProcessorTopology(Environment.ProcessorCount, null, null, null, null, null);

        return new SystemInformation
        {
            OperatingSystem = ReadOperatingSystem(),
            ComputerName = Environment.MachineName,
            Processor = new ProcessorInfo
            {
                Name = SystemInfoNormalizer.CleanProcessorName(ReadString(ProcessorKey, "ProcessorNameString")),
                Vendor = ReadString(ProcessorKey, "VendorIdentifier"),
                Sockets = topology.Sockets,
                PhysicalCores = topology.PhysicalCores,
                LogicalProcessors = topology.LogicalProcessors,
                BaseFrequencyGHz = topology.BaseFrequencyGHz,
                L2CacheBytes = topology.L2CacheBytes,
                L3CacheBytes = topology.L3CacheBytes,
            },
            Gpus = Safe(DxgiAdapterEnumerator.Enumerate, "graphics adapters") ?? [],
            InstalledMemoryBytes = NativeMethods.GetPhysicallyInstalledSystemMemory(out var installedKb) ? installedKb * 1024 : null,
            UsableMemoryBytes = ReadUsableMemory(),
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Firmware = new FirmwareInfo
            {
                SystemManufacturer = Firmware("SystemManufacturer"),
                SystemModel = Firmware("SystemProductName"),
                BoardManufacturer = Firmware("BaseBoardManufacturer"),
                BoardProduct = Firmware("BaseBoardProduct"),
                BiosVendor = Firmware("BIOSVendor"),
                BiosVersion = Firmware("BIOSVersion"),
                BiosReleaseDate = Firmware("BIOSReleaseDate"),
            },
            BootTime = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }

    private static OperatingSystemInfo ReadOperatingSystem()
    {
        var version = Environment.OSVersion.Version;
        var ubr = ReadInt(CurrentVersionKey, "UBR");
        var installDate = ReadInt(CurrentVersionKey, "InstallDate");

        return new OperatingSystemInfo
        {
            ProductName = SystemInfoNormalizer.NormalizeProductName(ReadString(CurrentVersionKey, "ProductName"), version.Build),
            DisplayVersion = ReadString(CurrentVersionKey, "DisplayVersion") ?? ReadString(CurrentVersionKey, "ReleaseId"),
            Build = ubr is { } revision
                ? $"{version.Major}.{version.Minor}.{version.Build}.{revision}"
                : $"{version.Major}.{version.Minor}.{version.Build}",
            InstallDate = installDate is > 0 ? DateTimeOffset.FromUnixTimeSeconds((uint)installDate.Value).ToLocalTime() : null,
        };
    }

    private static ulong? ReadUsableMemory()
    {
        var status = new NativeMethods.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status) ? status.TotalPhys : null;
    }

    private static string? Firmware(string valueName) =>
        SystemInfoNormalizer.CleanFirmwareValue(ReadString(BiosKey, valueName));

    private static string? ReadString(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static int? ReadInt(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private T? Safe<T>(Func<T> read, string what)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read {What}.", what);
            return null;
        }
    }
}

/// <summary>Uptime from the system tick count (includes time spent asleep, like Task Manager).</summary>
public sealed class WindowsSystemMetricProvider : ISystemMetricProvider
{
    public Task<SystemMetrics> CollectAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SystemMetrics(TimeSpan.FromMilliseconds(Environment.TickCount64)));
}
