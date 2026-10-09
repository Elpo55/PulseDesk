using System.Globalization;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sysora.Core.Changes;
using Sysora.Core.Interfaces;
using Sysora.Infrastructure.Hardware;

namespace Sysora.Infrastructure.SystemInfo;

/// <summary>
/// Reads the software and hardware inventory from documented, read-only sources:
/// <list type="bullet">
/// <item>installed applications: the Uninstall registry keys (Apps &amp; features) and the current user's
/// Store/MSIX packages (<c>PackageManager.FindPackagesForUser</c>);</item>
/// <item>startup programs: the per-user and per-machine Run keys and Startup folders, with the enabled state
/// Windows records under <c>StartupApproved</c>;</item>
/// <item>devices: graphics adapters (DXGI) and physical network adapters (IP Helper).</item>
/// </list>
/// Nothing is ever written.
/// </summary>
public sealed class WindowsSystemInventoryProvider(ILogger<WindowsSystemInventoryProvider> logger) : ISystemInventoryProvider
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public Task<SystemInventory> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Collect(cancellationToken), cancellationToken);

    private SystemInventory Collect(CancellationToken cancellationToken)
    {
        var apps = Safe(() => ReadInstalledApps(cancellationToken), "installed applications");
        cancellationToken.ThrowIfCancellationRequested();
        var startup = Safe(ReadStartupPrograms, "startup programs");
        var devices = Safe(ReadDevices, "devices") ?? [];
        return new SystemInventory
        {
            Apps = apps ?? [],
            AppsAvailable = apps is not null,
            StartupPrograms = startup ?? [],
            StartupAvailable = startup is not null,
            Devices = devices,
        };
    }

    private IReadOnlyList<InstalledApp> ReadInstalledApps(CancellationToken cancellationToken)
    {
        var apps = new List<InstalledApp>();
        ReadUninstall(Registry.LocalMachine, UninstallKey, "Machine", apps);
        ReadUninstall(Registry.LocalMachine, Uninstall32Key, "Machine (32-bit)", apps);
        ReadUninstall(Registry.CurrentUser, UninstallKey, "User", apps);
        cancellationToken.ThrowIfCancellationRequested();

        var packages = Safe(ReadPackages, "Store applications");
        if (packages is not null)
        {
            apps.AddRange(packages);
        }

        return apps;
    }

    private static void ReadUninstall(RegistryKey root, string path, string source, List<InstalledApp> apps)
    {
        using var key = root.OpenSubKey(path);
        if (key is null)
        {
            return;
        }

        foreach (var name in key.GetSubKeyNames())
        {
            using var entry = key.OpenSubKey(name);
            if (entry?.GetValue("DisplayName") is not string displayName || string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            // Hidden components and updates are not applications the user installed.
            if (entry.GetValue("SystemComponent") is int and 1
                || entry.GetValue("ParentKeyName") is string
                || entry.GetValue("ReleaseType") is string release && release.Contains("Update", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            apps.Add(new InstalledApp($"{source}|{name}", displayName.Trim())
            {
                Version = Clean(entry.GetValue("DisplayVersion") as string),
                Publisher = Clean(entry.GetValue("Publisher") as string),
                InstallDate = InventoryParsing.ParseInstallDate(entry.GetValue("InstallDate") as string),
                Source = source,
            });
        }
    }

    private static IReadOnlyList<InstalledApp> ReadPackages()
    {
        var manager = new global::Windows.Management.Deployment.PackageManager();
        var apps = new List<InstalledApp>();
        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            if (package.IsFramework || package.IsResourcePackage || package.SignatureKind == global::Windows.ApplicationModel.PackageSignatureKind.System)
            {
                continue;
            }

            var id = package.Id;
            string name;
            try
            {
                name = package.DisplayName;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                name = id.Name;
            }

            var version = id.Version;
            apps.Add(new InstalledApp($"Store|{id.FamilyName}", string.IsNullOrWhiteSpace(name) ? id.Name : name.Trim())
            {
                Version = string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"),
                Publisher = Clean(package.PublisherDisplayName),
                InstallDate = package.InstalledDate.Year > 1601 ? DateOnly.FromDateTime(package.InstalledDate.LocalDateTime) : null,
                Source = "Store",
            });
        }

        return apps;
    }

    private static IReadOnlyList<StartupProgram> ReadStartupPrograms()
    {
        var programs = new List<StartupProgram>();
        ReadRunKey(Registry.CurrentUser, RunKey, "HKCU Run", Registry.CurrentUser, "Run", programs);
        ReadRunKey(Registry.LocalMachine, RunKey, "HKLM Run", Registry.LocalMachine, "Run", programs);
        ReadRunKey(Registry.LocalMachine, Run32Key, "HKLM Run (32-bit)", Registry.LocalMachine, "Run32", programs);
        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup folder (user)", Registry.CurrentUser, programs);
        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup folder (all users)", Registry.LocalMachine, programs);
        return programs;
    }

    private static void ReadRunKey(RegistryKey root, string path, string location, RegistryKey approvedRoot, string approvedName, List<StartupProgram> programs)
    {
        using var key = root.OpenSubKey(path);
        if (key is null)
        {
            return;
        }

        using var approved = approvedRoot.OpenSubKey($@"{ApprovedKey}\{approvedName}");
        foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
        {
            programs.Add(new StartupProgram($"{location}|{name}", name, key.GetValue(name)?.ToString() ?? string.Empty, location)
            {
                Enabled = InventoryParsing.IsApproved(approved?.GetValue(name) as byte[]),
            });
        }
    }

    private static void ReadStartupFolder(string folder, string location, RegistryKey approvedRoot, List<StartupProgram> programs)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        using var approved = approvedRoot.OpenSubKey($@"{ApprovedKey}\StartupFolder");
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var fileName = Path.GetFileName(file);
            programs.Add(new StartupProgram($"{location}|{fileName}", Path.GetFileNameWithoutExtension(file), file, location)
            {
                Enabled = InventoryParsing.IsApproved(approved?.GetValue(fileName) as byte[]),
            });
        }
    }

    private static IReadOnlyList<DeviceInfo> ReadDevices()
    {
        var devices = new List<DeviceInfo>();
        foreach (var gpu in DxgiAdapterEnumerator.Enumerate())
        {
            // The adapter LUID changes at every boot: identify graphics adapters by name and memory size.
            devices.Add(new DeviceInfo("Graphics", $"{gpu.Name}|{gpu.DedicatedMemoryBytes}", gpu.Name));
        }

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2
                && InventoryParsing.IsPhysicalAdapter(adapter.Description))
            {
                devices.Add(new DeviceInfo("Network", adapter.Id, adapter.Description));
            }
        }

        return devices;
    }

    private T? Safe<T>(Func<T> read, string what)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Could not read {What}.", what);
            return null;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Pure parsing helpers of the inventory (unit-tested).</summary>
internal static partial class InventoryParsing
{
    private static readonly string[] VirtualMarkers =
    [
        "Virtual", "Hyper-V", "VMware", "VirtualBox", "Loopback", "TAP-", "WAN Miniport", "Bluetooth Device (Personal Area Network)", "Npcap", "VPN",
        "Kernel Debug", "Packet Scheduler", "LightWeight Filter",
    ];

    /// <summary>Parses the yyyyMMdd install date written by installers; null when absent or malformed.</summary>
    public static DateOnly? ParseInstallDate(string? value) =>
        value is { Length: 8 } && DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>
    /// Reads the StartupApproved flag Windows writes when the user toggles a startup app: an even first byte
    /// means enabled, an odd one disabled. No flag means enabled (never toggled).
    /// </summary>
    public static bool IsApproved(byte[]? flags) => flags is not { Length: > 0 } || (flags[0] & 1) == 0;

    /// <summary>
    /// True for real network adapters. Excludes adapters created by software (VPNs, virtual machines, capture
    /// drivers) and the filter modules Windows lists as extra interfaces of a real adapter
    /// ("Adapter-WFP Native MAC Layer LightWeight Filter-0000", "Adapter-QoS Packet Scheduler-0000").
    /// </summary>
    public static bool IsPhysicalAdapter(string description) =>
        !string.IsNullOrWhiteSpace(description)
        && !FilterModuleSuffix().IsMatch(description)
        && !VirtualMarkers.Any(marker => description.Contains(marker, StringComparison.OrdinalIgnoreCase));

    [System.Text.RegularExpressions.GeneratedRegex(@"-\d{4}$")]
    private static partial System.Text.RegularExpressions.Regex FilterModuleSuffix();
}
