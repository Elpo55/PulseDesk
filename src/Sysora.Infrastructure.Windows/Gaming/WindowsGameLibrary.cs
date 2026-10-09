using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sysora.Core.Gaming;
using Sysora.Core.Interfaces;

namespace Sysora.Infrastructure.Gaming;

/// <summary>
/// The games of the current user: the executables Game Bar matched to a known game
/// (<c>HKCU\System\GameConfigStore\Children\*\MatchedExeFullPath</c>) and the games the launchers report as installed
/// (Steam library manifests, Epic Games manifests, Riot Client product settings, GOG Galaxy's registry entries).
/// Read-only and offline: Sysora never changes these files or settings and never starts a game.
/// </summary>
public sealed class WindowsGameLibrary(ILogger<WindowsGameLibrary> logger) : IGameLibrary
{
    private const string ChildrenKey = @"System\GameConfigStore\Children";
    private const string MatchedPathValue = "MatchedExeFullPath";
    private const string GogGamesKey = @"SOFTWARE\WOW6432Node\GOG.com\Games";
    private const int MaxCachedProductNames = 512;

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string?> _productNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _recognized = Empty;
    private InstalledGameIndex _installed = InstalledGameIndex.Empty;
    private bool _errorLogged;

    public IReadOnlySet<string> RecognizedGames => Volatile.Read(ref _recognized);

    public InstalledGameIndex InstalledGames => Volatile.Read(ref _installed);

    public bool Refresh()
    {
        var recognizedChanged = RefreshRecognized();
        var installedChanged = RefreshInstalled();
        return recognizedChanged || installedChanged;
    }

    /// <summary>Where the launchers keep their files on this PC.</summary>
    public static LauncherLocations Locations()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var steam = new List<string>();
        if (ReadString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath") is { } userSteam)
        {
            steam.Add(userSteam.Replace('/', '\\'));
        }

        if (ReadString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath") is { } machineSteam)
        {
            steam.Add(machineSteam);
        }

        return new LauncherLocations
        {
            SteamFolders = steam.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            EpicManifestFolders = [Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests")],
            RiotMetadataFolders = [Path.Combine(programData, "Riot Games", "Metadata")],
            OtherGames = ReadGogGames(),
        };
    }

    public string? GetProductName(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (_productNames.TryGetValue(executablePath, out var cached))
        {
            return cached;
        }

        string? name = null;
        try
        {
            name = FileVersionInfo.GetVersionInfo(executablePath).ProductName?.Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No version information: the name comes from the folder or the executable.
        }

        if (_productNames.Count >= MaxCachedProductNames)
        {
            _productNames.Clear();
        }

        name = string.IsNullOrEmpty(name) ? null : name;
        _productNames[executablePath] = name;
        return name;
    }

    private bool RefreshRecognized()
    {
        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(ChildrenKey);
            foreach (var name in children?.GetSubKeyNames() ?? [])
            {
                using var child = children!.OpenSubKey(name);
                if (child?.GetValue(MatchedPathValue) is string { Length: > 0 } path)
                {
                    games.Add(path.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            if (!_errorLogged)
            {
                _errorLogged = true;
                logger.LogWarning(ex, "The list of games recognized by Windows could not be read.");
            }

            return false;
        }

        if (games.SetEquals(RecognizedGames))
        {
            return false;
        }

        Volatile.Write(ref _recognized, games);
        return true;
    }

    private bool RefreshInstalled()
    {
        IReadOnlyList<InstalledGame> games;
        try
        {
            games = LauncherGameScanner.Scan(Locations());
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "The games installed with a launcher could not be read.");
            return false;
        }

        if (games.SequenceEqual(InstalledGames.Games))
        {
            return false;
        }

        Volatile.Write(ref _installed, new InstalledGameIndex(games));
        logger.LogInformation(
            "Games installed with a launcher: {Steam} Steam, {Epic} Epic Games, {Riot} Riot, {Gog} GOG.",
            games.Count(g => g.Launcher == GameLauncher.Steam),
            games.Count(g => g.Launcher == GameLauncher.EpicGames),
            games.Count(g => g.Launcher == GameLauncher.Riot),
            games.Count(g => g.Launcher == GameLauncher.Gog));
        return true;
    }

    /// <summary>GOG Galaxy registers each installed game under its own key (gameName, path).</summary>
    private static IReadOnlyList<InstalledGame> ReadGogGames()
    {
        var games = new List<InstalledGame>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(GogGamesKey);
            foreach (var id in root?.GetSubKeyNames() ?? [])
            {
                using var game = root!.OpenSubKey(id);
                if (game?.GetValue("path") is string { Length: > 0 } path)
                {
                    var name = game.GetValue("gameName") as string is { Length: > 0 } n ? n.Trim() : Path.GetFileName(path.TrimEnd('\\'));
                    games.Add(new InstalledGame(GameLauncher.Gog, id, name, path));
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // GOG Galaxy's entries are optional.
        }

        return games;
    }

    private static string? ReadString(RegistryKey hive, string key, string value)
    {
        try
        {
            using var opened = hive.OpenSubKey(key);
            return opened?.GetValue(value) as string is { Length: > 0 } text ? text.Trim() : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
