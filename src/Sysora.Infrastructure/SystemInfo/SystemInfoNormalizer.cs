namespace Sysora.Infrastructure.SystemInfo;

/// <summary>Pure helpers that clean up values read from the registry and firmware.</summary>
internal static class SystemInfoNormalizer
{
    /// <summary>First build number of Windows 11.</summary>
    public const int FirstWindows11Build = 22000;

    /// <summary>Placeholder strings that firmware vendors leave in SMBIOS fields.</summary>
    private static readonly HashSet<string> FirmwarePlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "To be filled by O.E.M.", "Default string", "System Product Name",
        "System manufacturer", "System Version", "Not Applicable", "Not Specified", "None", "O.E.M.", "OEM",
        "Type1ProductConfigId", "Base Board Product Name", "Base Board Manufacturer", "0", "Undefined",
    };

    /// <summary>
    /// Windows 11 still reports "Windows 10" in the registry's ProductName for compatibility;
    /// the build number tells them apart.
    /// </summary>
    public static string NormalizeProductName(string? productName, int build)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
        return build >= FirstWindows11Build && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase)
            ? "Windows 11" + name["Windows 10".Length..]
            : name;
    }

    /// <summary>Returns null for empty values and well-known firmware placeholders.</summary>
    public static string? CleanFirmwareValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().TrimEnd('\0').Trim();
        return trimmed.Length == 0 || FirmwarePlaceholders.Contains(trimmed) ? null : trimmed;
    }

    /// <summary>Collapses the runs of spaces some CPUs have in their brand string.</summary>
    public static string? CleanProcessorName(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
