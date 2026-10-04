namespace PulseDesk.Core.Models;

/// <summary>
/// Mostly static description of the machine, collected at startup.
/// Every field that Windows may not expose is nullable.
/// </summary>
public sealed record SystemInformation
{
    /// <summary>Operating system details.</summary>
    public required OperatingSystemInfo OperatingSystem { get; init; }

    /// <summary>Computer (NetBIOS) name.</summary>
    public required string ComputerName { get; init; }

    /// <summary>Processor details.</summary>
    public required ProcessorInfo Processor { get; init; }

    /// <summary>Graphics adapters (software adapters excluded).</summary>
    public IReadOnlyList<GpuAdapterInfo> Gpus { get; init; } = [];

    /// <summary>Physically installed memory, when reported by the firmware.</summary>
    public ulong? InstalledMemoryBytes { get; init; }

    /// <summary>Memory usable by Windows.</summary>
    public ulong? UsableMemoryBytes { get; init; }

    /// <summary>Operating system architecture (e.g. "X64").</summary>
    public required string OsArchitecture { get; init; }

    /// <summary>Architecture PulseDesk itself runs as.</summary>
    public required string ProcessArchitecture { get; init; }

    /// <summary>Motherboard, BIOS and system model, when available.</summary>
    public FirmwareInfo Firmware { get; init; } = new();

    /// <summary>Time Windows last started, when known.</summary>
    public DateTimeOffset? BootTime { get; init; }
}

/// <summary>Operating system details.</summary>
public sealed record OperatingSystemInfo
{
    /// <summary>Product name, e.g. "Windows 11 Pro".</summary>
    public required string ProductName { get; init; }

    /// <summary>Feature update version, e.g. "24H2".</summary>
    public string? DisplayVersion { get; init; }

    /// <summary>Full build, e.g. "10.0.26100.4652".</summary>
    public required string Build { get; init; }

    /// <summary>When Windows was installed, when known.</summary>
    public DateTimeOffset? InstallDate { get; init; }
}

/// <summary>Processor details.</summary>
public sealed record ProcessorInfo
{
    /// <summary>Marketing name, e.g. "AMD Ryzen 7 7800X3D 8-Core Processor".</summary>
    public string? Name { get; init; }

    /// <summary>Vendor identifier, e.g. "AuthenticAMD".</summary>
    public string? Vendor { get; init; }

    /// <summary>Number of physical processor packages.</summary>
    public int? Sockets { get; init; }

    /// <summary>Number of physical cores.</summary>
    public int? PhysicalCores { get; init; }

    /// <summary>Number of logical processors (hardware threads).</summary>
    public required int LogicalProcessors { get; init; }

    /// <summary>Rated (base) clock speed reported by Windows.</summary>
    public double? BaseFrequencyGHz { get; init; }

    /// <summary>Total L2 cache size.</summary>
    public ulong? L2CacheBytes { get; init; }

    /// <summary>Total L3 cache size.</summary>
    public ulong? L3CacheBytes { get; init; }
}

/// <summary>Graphics adapter details.</summary>
/// <param name="AdapterId">LUID of the adapter (stable for the boot session).</param>
/// <param name="Name">Adapter name.</param>
public sealed record GpuAdapterInfo(string AdapterId, string Name)
{
    /// <summary>Dedicated video memory.</summary>
    public ulong? DedicatedMemoryBytes { get; init; }

    /// <summary>Shared system memory the adapter may use.</summary>
    public ulong? SharedMemoryBytes { get; init; }
}

/// <summary>Motherboard, BIOS and system model information from the SMBIOS tables.</summary>
public sealed record FirmwareInfo
{
    /// <summary>System manufacturer, e.g. "ASUSTeK COMPUTER INC.".</summary>
    public string? SystemManufacturer { get; init; }

    /// <summary>System model.</summary>
    public string? SystemModel { get; init; }

    /// <summary>Motherboard manufacturer.</summary>
    public string? BoardManufacturer { get; init; }

    /// <summary>Motherboard model.</summary>
    public string? BoardProduct { get; init; }

    /// <summary>BIOS vendor.</summary>
    public string? BiosVendor { get; init; }

    /// <summary>BIOS version.</summary>
    public string? BiosVersion { get; init; }

    /// <summary>BIOS release date as reported by the firmware.</summary>
    public string? BiosReleaseDate { get; init; }
}

/// <summary>Frequently changing system-wide values.</summary>
/// <param name="Uptime">Time since Windows last started (includes sleep).</param>
public sealed record SystemMetrics(TimeSpan Uptime);
