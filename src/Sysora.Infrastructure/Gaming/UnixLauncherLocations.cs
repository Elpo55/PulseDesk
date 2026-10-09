using Sysora.Core.Gaming;

namespace Sysora.Infrastructure.Gaming;

/// <summary>
/// Where the launchers keep their files on Linux and macOS, for <see cref="LauncherGameScanner"/>. Only folders that exist
/// are scanned, and nothing outside them is read.
/// </summary>
public static class UnixLauncherLocations
{
    /// <summary>Steam (native, Flatpak and Snap packages) on Linux. The Epic Games and Riot launchers have no Linux version.</summary>
    public static LauncherLocations Linux(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        return new LauncherLocations
        {
            SteamFolders =
            [
                Path.Combine(home, ".local", "share", "Steam"),
                Path.Combine(home, ".steam", "steam"),
                Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
                Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
            ],
        };
    }

    /// <summary>Steam, the Epic Games Launcher and the Riot Client on macOS.</summary>
    public static LauncherLocations MacOS(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        var support = Path.Combine(home, "Library", "Application Support");
        return new LauncherLocations
        {
            SteamFolders = [Path.Combine(support, "Steam")],
            EpicManifestFolders = [Path.Combine(support, "Epic", "EpicGamesLauncher", "Data", "Manifests")],
            RiotMetadataFolders = ["/Users/Shared/Riot Games/Metadata"],
        };
    }

    /// <summary>The locations for the current system, or none on other systems.</summary>
    public static LauncherLocations Current()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return new LauncherLocations();
        }

        return OperatingSystem.IsLinux() ? Linux(home)
            : OperatingSystem.IsMacOS() ? MacOS(home)
            : new LauncherLocations();
    }
}
