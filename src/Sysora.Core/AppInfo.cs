using System.Reflection;
using Sysora.Localization;

namespace Sysora.Core;

/// <summary>
/// Product identity of Sysora (name, tagline, version, author, project URL, license).
/// </summary>
/// <remarks>
/// Values are defined once in <c>Directory.Build.props</c> and stamped into the assemblies at build time.
/// Read them from here instead of hard-coding them elsewhere.
/// </remarks>
public static class AppInfo
{
    private static readonly Assembly Source = typeof(AppInfo).Assembly;

    /// <summary>Product name, e.g. "Sysora".</summary>
    public static string Name { get; } =
        Source.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Sysora";

    /// <summary>Product tagline: "Your PC, Explained."</summary>
    public static string Tagline { get; } = GetMetadata("Sysora.Tagline");

    /// <summary>One-line product description.</summary>
    public static string Description { get; } =
        Source.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? string.Empty;

    /// <summary>Longer description used in the About section.</summary>
    public static string LongDescription => Strings.App_LongDescription;

    /// <summary>Full informational version, including the source revision when available (e.g. "0.1.0+3f2a1c9").</summary>
    public static string InformationalVersion { get; } =
        Source.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Display version without build metadata (e.g. "0.1.0").</summary>
    public static string Version { get; } = InformationalVersion.Split('+')[0];

    /// <summary>Author(s) of the project.</summary>
    public static string Authors { get; } = GetMetadata("Sysora.Authors");

    /// <summary>Public project page (GitHub repository).</summary>
    public static string ProjectUrl { get; } = GetMetadata("Sysora.ProjectUrl");

    /// <summary>SPDX license identifier.</summary>
    public static string License { get; } = GetMetadata("Sysora.License");

    /// <summary>Copyright notice.</summary>
    public static string Copyright { get; } =
        Source.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

    private static string GetMetadata(string key) =>
        Source.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value ?? string.Empty;
}
