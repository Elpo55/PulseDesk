using Sysora.Core.Analysis;
using Sysora.Core.Settings;
using Sysora.Localization;

namespace Sysora.Core.Gaming;

/// <summary>Why Sysora treats an executable as a game.</summary>
public enum GameDetectionSource
{
    /// <summary>The user marked it as a game.</summary>
    UserMarked,

    /// <summary>Windows recognizes it as a game (Game Bar's list for this user).</summary>
    WindowsRecognized,

    /// <summary>It is installed in a game library folder (Steam, Epic, Xbox, GOG...): likely a game.</summary>
    GameLibrary,

    /// <summary>It is installed in the folder of a game a launcher reports as installed (Steam, Epic Games, Riot, GOG).</summary>
    Launcher,
}

/// <summary>An executable identified as a game, with the evidence and how much it can be trusted.</summary>
/// <param name="ExecutablePath">Full path of the game's executable.</param>
/// <param name="Source">What identified it.</param>
/// <param name="Confidence">High for the user's choice and Windows' list; Medium for a library folder.</param>
/// <param name="Evidence">Plain-language reason, e.g. "Windows recognizes this executable as a game (Game Bar)".</param>
public sealed record GameMatch(string ExecutablePath, GameDetectionSource Source, ConfidenceLevel Confidence, string Evidence)
{
    /// <summary>Name of the game library folder or launcher that identified it.</summary>
    public string? Library { get; init; }

    /// <summary>The game's name as its launcher reports it, when a launcher identified it.</summary>
    public string? GameName { get; init; }

    /// <summary>The game's folder as its launcher reports it, when a launcher identified it.</summary>
    public string? InstallFolder { get; init; }
}

/// <summary>
/// Decides whether an executable is a game, from evidence only: the user's own lists, the games Windows recognizes,
/// and well-known game library folders. A process is never assumed to be a game because of its resource usage.
/// Pure and deterministic.
/// </summary>
public static class GameClassifier
{
    /// <summary>Game library folders: (path fragment, library name). Fragments are matched case-insensitively.</summary>
    private static readonly (string Fragment, string Library)[] Libraries =
    [
        (@"\steamapps\common\", "Steam"),
        (@"\Epic Games\", "Epic Games"),
        (@"\XboxGames\", "Xbox"),
        (@"\GOG Galaxy\Games\", "GOG"),
        (@"\GOG Games\", "GOG"),
        (@"\Riot Games\", "Riot Games"),
        (@"\EA Games\", "EA"),
        (@"\Origin Games\", "EA"),
        (@"\Ubisoft Game Launcher\games\", "Ubisoft"),
        (@"\Amazon Games\Library\", "Amazon Games"),
        (@"\Battle.net\Games\", "Battle.net"),
    ];

    /// <summary>
    /// Executable names (fragments) found in library folders that are not the game itself: crash reporters,
    /// anti-cheat services, launchers, installers, redistributables, helpers.
    /// </summary>
    private static readonly string[] HelperFragments =
    [
        // "crash" alone would also match game titles ("Crash Bandicoot"): only its helper forms.
        "crashreport", "crashhandler", "crash_handler", "crashpad", "crashsender", "crashdump", "report", "launcher", "unins", "setup", "install", "redist", "dxsetup", "prereq", "anticheat",
        "easyanticheat", "battleye", "beservice", "helper", "updater", "update", "service", "overlay", "bootstrap",
        "webview", "cefsharp", "cefprocess", "handler", "dedicated", "editor", "benchmark", "config", "settings", "touchup",
        "uploader", "socialclub", "eac_", "vc_", "dotnet", "ue4prereq", "ueprereq", "leagueclient", "riotclient",
    ];

    /// <summary>Folders of well-known tools distributed through game stores (not games).</summary>
    private static readonly string[] NonGameFolders =
    [
        @"\steamapps\common\Steamworks Shared\", @"\steamapps\common\SteamVR\", @"\steamapps\common\wallpaper_engine\",
        @"\steamapps\common\OBS Studio\", @"\steamapps\common\Blender\", @"\steamapps\common\Lossless Scaling\",
        @"\steamapps\common\Soundpad\", @"\steamapps\common\VTube Studio\", @"\steamapps\common\Krita\",
        @"\steamapps\common\Aseprite\", @"\steamapps\common\Steam Controller Configs\", @"\steamapps\common\Proton",
        @"\steamapps\common\SteamLinuxRuntime", @"\_CommonRedist\", @"\Epic Games\Launcher\", @"\Epic Games\DirectXRedist\",
        @"\Riot Games\Riot Client\",
    ];

    /// <summary>Generic product names that say nothing about the game (engine or runtime names).</summary>
    private static readonly string[] GenericProductNames =
    [
        "unreal engine", "unrealgame", "ue4game", "unity player", "unityplayer", "bootstrappackagedgame", "java", "openjdk",
        "javaw", "electron", "godot engine",
    ];

    /// <summary>Executable name suffixes added by engines ("VALORANT-Win64-Shipping").</summary>
    private static readonly string[] EngineSuffixes = ["-Win64-Shipping", "-WinGDK-Shipping", "-Shipping", "_BE", "_EAC"];

    /// <summary>Folder names skipped when deriving a game name from its install folder.</summary>
    private static readonly HashSet<string> TechnicalFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "binaries", "win64", "win32", "x64", "x86", "live", "game", "retail", "client", "content", "app", "build",
        "release", "shipping", "windows", "windows-x64", "windowsnoeditor", "wingdk",
    };

    /// <summary>Classifies an executable. Returns null when there is no evidence that it is a game.</summary>
    /// <param name="executablePath">Full executable path (null for protected processes, which are never games).</param>
    /// <param name="settings">Detection settings and the user's lists.</param>
    /// <param name="recognized">Executables Windows recognizes as games.</param>
    public static GameMatch? Classify(string? executablePath, GamingSettings settings, IReadOnlySet<string> recognized) =>
        Classify(executablePath, settings, recognized, InstalledGameIndex.Empty);

    /// <summary>Classifies an executable. Returns null when there is no evidence that it is a game.</summary>
    /// <param name="executablePath">Full executable path (null for protected processes, which are never games).</param>
    /// <param name="settings">Detection settings and the user's lists.</param>
    /// <param name="recognized">Executables Windows recognizes as games.</param>
    /// <param name="installed">Games the launchers report as installed.</param>
    public static GameMatch? Classify(string? executablePath, GamingSettings settings, IReadOnlySet<string> recognized, InstalledGameIndex installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recognized);
        if (string.IsNullOrWhiteSpace(executablePath) || Contains(settings.ExcludedGames, executablePath))
        {
            return null;
        }

        if (Contains(settings.AddedGames, executablePath))
        {
            return new GameMatch(executablePath, GameDetectionSource.UserMarked, ConfidenceLevel.High, Strings.Game_Detect_UserMarked);
        }

        if (IsSystemPath(executablePath))
        {
            return null;
        }

        if (!IsHelper(executablePath) && Confirmed(settings, executablePath) is { } confirmed)
        {
            return new GameMatch(executablePath, GameDetectionSource.Launcher, ConfidenceLevel.High, Text.Format(Strings.Game_Detect_Confirmed, confirmed.Name))
            {
                GameName = confirmed.Name,
                InstallFolder = confirmed.Folder,
            };
        }

        if (settings.DetectLauncherGames && !IsHelper(executablePath) && installed.Find(executablePath) is { } game
            && !Contains(settings.IgnoredLauncherGames, game.Key))
        {
            return new GameMatch(executablePath, GameDetectionSource.Launcher, ConfidenceLevel.High, Text.Format(Strings.Game_Detect_Launcher, game.LauncherName, game.Name))
            {
                Library = game.LauncherName,
                GameName = game.Name,
                InstallFolder = game.InstallFolder,
            };
        }

        if (settings.DetectWindowsGames && recognized.Contains(executablePath))
        {
            return new GameMatch(executablePath, GameDetectionSource.WindowsRecognized, ConfidenceLevel.High,
                Strings.Game_Detect_Windows);
        }

        if (settings.DetectLibraryGames && LibraryOf(executablePath) is { } library && !IsHelper(executablePath))
        {
            return new GameMatch(executablePath, GameDetectionSource.GameLibrary, ConfidenceLevel.Medium,
                Text.Format(Strings.Game_Detect_Library, library))
            {
                Library = library,
            };
        }

        return null;
    }

    /// <summary>Name of the game library folder containing <paramref name="executablePath"/>, or null.</summary>
    public static string? LibraryOf(string executablePath)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        foreach (var folder in NonGameFolders)
        {
            if (executablePath.Contains(folder, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        foreach (var (fragment, library) in Libraries)
        {
            if (executablePath.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return library;
            }
        }

        return null;
    }

    /// <summary>
    /// The folder holding the game's files: its folder inside a library, otherwise the executable's folder when it is
    /// specific enough (never a drive root or a top-level folder shared by other programs). Ends with a separator.
    /// </summary>
    public static string? InstallFolder(string executablePath)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        foreach (var (fragment, _) in Libraries)
        {
            var index = executablePath.IndexOf(fragment, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var gameStart = index + fragment.Length;
            var end = executablePath.IndexOf('\\', gameStart);
            if (end > gameStart)
            {
                return executablePath[..(end + 1)];
            }
        }

        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        // "C:\Games\Title" (two levels) is specific; "C:\Games" or "C:\" is not.
        var root = Path.GetPathRoot(directory) ?? string.Empty;
        var levels = directory[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
        return levels >= 2 ? directory + '\\' : null;
    }

    /// <summary>True for executables that accompany games but are not the game (crash reporters, anti-cheat, launchers...).</summary>
    public static bool IsHelper(string executablePath)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        var name = Path.GetFileNameWithoutExtension(executablePath);
        return HelperFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A readable name for a game: its product name when meaningful, otherwise its install folder (library folders),
    /// otherwise the executable name without engine suffixes.
    /// </summary>
    public static string DisplayName(string executablePath, string? productName)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        if (productName?.Trim() is { Length: > 0 } product && !IsGenericProductName(product))
        {
            return product;
        }

        if (FolderName(executablePath) is { } folder)
        {
            return folder;
        }

        var name = Path.GetFileNameWithoutExtension(executablePath);
        foreach (var suffix in EngineSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    /// <summary>The game's folder inside a library ("…\steamapps\common\Supermarket Together\…" → "Supermarket Together").</summary>
    private static string? FolderName(string executablePath)
    {
        foreach (var (fragment, _) in Libraries)
        {
            var index = executablePath.IndexOf(fragment, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var rest = executablePath[(index + fragment.Length)..];
            var end = rest.IndexOf('\\');
            if (end > 0 && !TechnicalFolders.Contains(rest[..end]))
            {
                return rest[..end];
            }
        }

        return null;
    }

    /// <summary>"Unreal Engine", "Unity Player", "Java(TM) Platform SE binary"…: engine or runtime names, not game names.</summary>
    private static bool IsGenericProductName(string product) =>
        GenericProductNames.Any(g =>
            product.Equals(g, StringComparison.OrdinalIgnoreCase)
            || (product.StartsWith(g, StringComparison.OrdinalIgnoreCase) && product.Length > g.Length && !char.IsLetterOrDigit(product[g.Length])));

    private static ConfirmedGame? Confirmed(GamingSettings settings, string executablePath)
    {
        if (settings.ConfirmedGames.Count == 0)
        {
            return null;
        }

        var path = InstalledGameIndex.Normalize(executablePath);
        return settings.ConfirmedGames
            .Where(g => path.StartsWith(InstalledGameIndex.Normalize(g.Folder), StringComparison.OrdinalIgnoreCase))
            .MaxBy(g => g.Folder.Length);
    }

    private static bool IsSystemPath(string path) =>
        path.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\Windows\SystemApps\", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase);

    private static bool Contains(IReadOnlyList<string> list, string path)
    {
        foreach (var item in list)
        {
            if (string.Equals(item, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
