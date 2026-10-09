using System.Text.Json;

namespace Sysora.Core.Gaming;

/// <summary>Game launchers whose local files Sysora can read to know which games are installed.</summary>
public enum GameLauncher
{
    Steam,
    EpicGames,
    Riot,
    Gog,
}

/// <summary>A game a launcher reports as installed on this PC, read from the launcher's local files only.</summary>
/// <param name="Launcher">The launcher that installed it.</param>
/// <param name="Id">The launcher's identifier of the game (Steam app ID, Epic app name, Riot product, GOG game ID).</param>
/// <param name="Name">The game's name as the launcher shows it.</param>
/// <param name="InstallFolder">Folder holding the game's files.</param>
public sealed record InstalledGame(GameLauncher Launcher, string Id, string Name, string InstallFolder)
{
    /// <summary>Stable key of the game, used to remember the user's choices ("Steam:730").</summary>
    public string Key => $"{Launcher}:{Id}";

    /// <summary>Name of the launcher as its publisher writes it (a brand, never translated).</summary>
    public string LauncherName => LauncherNames.Of(Launcher);
}

/// <summary>Launcher names as their publishers write them.</summary>
public static class LauncherNames
{
    public static string Of(GameLauncher launcher) => launcher switch
    {
        GameLauncher.Steam => "Steam",
        GameLauncher.EpicGames => "Epic Games",
        GameLauncher.Riot => "Riot Games",
        _ => "GOG",
    };
}

/// <summary>A game in the launchers' list, looked up by the path of a running executable.</summary>
public sealed class InstalledGameIndex
{
    private readonly (string Folder, InstalledGame Game)[] _folders;

    public InstalledGameIndex(IEnumerable<InstalledGame> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        Games = games.ToArray();

        // Longest folders first: a game installed inside another game's folder (rare, but launchers allow it) wins.
        _folders = Games
            .Select(g => (Folder: Normalize(g.InstallFolder), Game: g))
            .Where(g => g.Folder.Length > 3)
            .OrderByDescending(g => g.Folder.Length)
            .ToArray();
    }

    public static InstalledGameIndex Empty { get; } = new([]);

    /// <summary>Every game the launchers report.</summary>
    public IReadOnlyList<InstalledGame> Games { get; }

    /// <summary>The installed game whose folder contains <paramref name="executablePath"/>, or null.</summary>
    public InstalledGame? Find(string executablePath)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        var path = Normalize(executablePath);
        foreach (var (folder, game) in _folders)
        {
            if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                return game;
            }
        }

        return null;
    }

    /// <summary>Forward slashes and a trailing separator, so "D:/Games/X" and "D:\Games\X\bin\x.exe" compare.</summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Trim().Replace('\\', '/');
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }
}

/// <summary>
/// Reads the installed games from the launchers' own local files (Steam library manifests, Epic Games manifests, Riot
/// Client product settings) and GOG Galaxy's registry entries given by the platform. Offline and read-only: nothing is
/// sent anywhere, no game is started and no file is changed. Missing launchers and unreadable files are skipped.
/// </summary>
public static class LauncherGameScanner
{
    /// <summary>Steam tools and runtimes that are not games.</summary>
    private static readonly HashSet<string> SteamNonGames = new(StringComparer.Ordinal)
    {
        "228980", // Steamworks Common Redistributables
        "250820", // SteamVR
        "1070560", // Steam Linux Runtime
        "1391110", // Steam Linux Runtime - Soldier
        "1628350", // Steam Linux Runtime - Sniper
        "1493710", // Proton Experimental
        "2180100", // Proton Hotfix
        "431960", // Wallpaper Engine
        "1905180", // OBS Studio
        "365670", // Blender
        "993090", // Lossless Scaling
    };

    /// <summary>Reads every launcher found in <paramref name="locations"/>. Never throws for a missing or unreadable file.</summary>
    public static IReadOnlyList<InstalledGame> Scan(LauncherLocations locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var games = new List<InstalledGame>();
        foreach (var root in locations.SteamFolders)
        {
            games.AddRange(ScanSteam(root));
        }

        foreach (var folder in locations.EpicManifestFolders)
        {
            games.AddRange(ScanEpic(folder));
        }

        foreach (var folder in locations.RiotMetadataFolders)
        {
            games.AddRange(ScanRiot(folder));
        }

        games.AddRange(locations.OtherGames);

        // The same folder reported twice (two Steam roots pointing at one library) is one game.
        return games
            .Where(g => FolderExists(g.InstallFolder))
            .GroupBy(g => InstalledGameIndex.Normalize(g.InstallFolder), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>Games of a Steam installation: every library listed in libraryfolders.vdf, then each app manifest.</summary>
    public static IEnumerable<InstalledGame> ScanSteam(string steamFolder)
    {
        var libraries = new List<string> { steamFolder };
        if (ReadText(Path.Combine(steamFolder, "steamapps", "libraryfolders.vdf")) is { } folders)
        {
            libraries.AddRange(SteamFiles.LibraryFolders(folders));
        }

        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(library, "steamapps");
            foreach (var manifest in EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                if (ReadText(manifest) is { } text && SteamFiles.AppManifest(text) is { } app
                    && app.IsFullyInstalled && !SteamNonGames.Contains(app.AppId)
                    && !app.Name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new InstalledGame(GameLauncher.Steam, app.AppId, app.Name, Path.Combine(apps, "common", app.InstallDir));
                }
            }
        }
    }

    /// <summary>Games of the Epic Games Launcher: one JSON manifest (.item) per installed application.</summary>
    public static IEnumerable<InstalledGame> ScanEpic(string manifestFolder)
    {
        foreach (var manifest in EnumerateFiles(manifestFolder, "*.item"))
        {
            if (ReadText(manifest) is { } text && EpicFiles.Manifest(text) is { } game)
            {
                yield return game;
            }
        }
    }

    /// <summary>Games of the Riot Client: one product_settings.yaml per installed product and patch line.</summary>
    public static IEnumerable<InstalledGame> ScanRiot(string metadataFolder)
    {
        foreach (var directory in EnumerateDirectories(metadataFolder))
        {
            var product = Path.GetFileName(directory);
            var settings = Path.Combine(directory, product + ".product_settings.yaml");
            if (ReadText(settings) is { } text && RiotFiles.ProductSettings(product, text) is { } game)
            {
                yield return game;
            }
        }
    }

    private static bool FolderExists(string folder)
    {
        try
        {
            return Directory.Exists(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            // Launcher files are small; a huge file is not one of them.
            var info = new FileInfo(path);
            return info.Exists && info.Length < 4 * 1024 * 1024 ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}

/// <summary>Where the launchers keep their files on this PC (given by the platform layer).</summary>
public sealed record LauncherLocations
{
    /// <summary>Steam installation folders (the folder holding steamapps\libraryfolders.vdf).</summary>
    public IReadOnlyList<string> SteamFolders { get; init; } = [];

    /// <summary>Folders holding the Epic Games Launcher's .item manifests.</summary>
    public IReadOnlyList<string> EpicManifestFolders { get; init; } = [];

    /// <summary>Riot Client metadata folders (one subfolder per product).</summary>
    public IReadOnlyList<string> RiotMetadataFolders { get; init; } = [];

    /// <summary>Games already read by the platform from another source (GOG Galaxy's registry entries on Windows).</summary>
    public IReadOnlyList<InstalledGame> OtherGames { get; init; } = [];
}

/// <summary>Parsers of Steam's KeyValues text files (libraryfolders.vdf, appmanifest_*.acf). Pure.</summary>
public static class SteamFiles
{
    /// <summary>An app manifest: what Steam knows about one installed app.</summary>
    public sealed record SteamApp(string AppId, string Name, string InstallDir, int StateFlags)
    {
        /// <summary>Steam's "fully installed" flag (4); an app still downloading or uninstalling lacks it.</summary>
        public bool IsFullyInstalled => (StateFlags & 4) != 0;
    }

    /// <summary>Library folder paths listed in libraryfolders.vdf (old and new formats).</summary>
    public static IReadOnlyList<string> LibraryFolders(string text)
    {
        var root = Parse(text);
        if (Child(root, "libraryfolders") is not { } folders)
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var (key, value) in folders)
        {
            if (!key.All(char.IsDigit))
            {
                continue;
            }

            // New format: "0" { "path" "D:\\SteamLibrary" ... }; old format: "1" "D:\\SteamLibrary".
            var path = value switch
            {
                string direct => direct,
                Dictionary<string, object> library when library.TryGetValue("path", out var p) && p is string s => s,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>An appmanifest_*.acf file, or null when it is not one.</summary>
    public static SteamApp? AppManifest(string text)
    {
        if (Child(Parse(text), "AppState") is not { } state
            || Value(state, "appid") is not { Length: > 0 } id
            || Value(state, "installdir") is not { Length: > 0 } folder)
        {
            return null;
        }

        var name = Value(state, "name") is { Length: > 0 } n ? n : folder;
        _ = int.TryParse(Value(state, "StateFlags"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var flags);
        return new SteamApp(id, name, folder, flags);
    }

    /// <summary>Parses KeyValues text: quoted keys followed by a quoted value or a { block }. Comments (//) are skipped.</summary>
    public static Dictionary<string, object> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var position = 0;
        return ParseBlock(text, ref position);
    }

    private static Dictionary<string, object> ParseBlock(string text, ref int position)
    {
        var block = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (NextToken(text, ref position) is { } token)
        {
            if (token == "}")
            {
                break;
            }

            if (token == "{")
            {
                continue;
            }

            var next = NextToken(text, ref position);
            if (next is null)
            {
                break;
            }

            block[token] = next == "{" ? ParseBlock(text, ref position) : next;
        }

        return block;
    }

    private static string? NextToken(string text, ref int position)
    {
        while (position < text.Length)
        {
            var c = text[position];
            if (char.IsWhiteSpace(c))
            {
                position++;
            }
            else if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                position = text.IndexOf('\n', position) is var end and >= 0 ? end + 1 : text.Length;
            }
            else if (c is '{' or '}')
            {
                position++;
                return c.ToString();
            }
            else if (c == '"')
            {
                var value = new System.Text.StringBuilder();
                position++;
                while (position < text.Length && text[position] != '"')
                {
                    if (text[position] == '\\' && position + 1 < text.Length)
                    {
                        position++;
                    }

                    value.Append(text[position]);
                    position++;
                }

                position++;
                return value.ToString();
            }
            else
            {
                // An unquoted token (allowed by the format): up to the next whitespace or brace.
                var start = position;
                while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"'))
                {
                    position++;
                }

                return text[start..position];
            }
        }

        return null;
    }

    private static Dictionary<string, object>? Child(Dictionary<string, object> block, string key) =>
        block.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;

    private static string? Value(Dictionary<string, object> block, string key) =>
        block.TryGetValue(key, out var value) ? value as string : null;
}

/// <summary>Parser of the Epic Games Launcher's .item manifests (JSON). Pure.</summary>
public static class EpicFiles
{
    /// <summary>The installed game a manifest describes, or null when it is not a complete game installation.</summary>
    public static InstalledGame? Manifest(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || String(root, "InstallLocation") is not { Length: > 0 } folder
                || String(root, "AppName") is not { Length: > 0 } appName)
            {
                return null;
            }

            if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            // Engines and plugins are installed through the same launcher: only "games" are kept when the manifest says.
            if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array
                && !categories.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var name = String(root, "DisplayName") is { Length: > 0 } display ? display.Trim() : appName;
            return new InstalledGame(GameLauncher.EpicGames, appName, name, folder);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Parser of the Riot Client's product_settings.yaml files. Pure.</summary>
public static class RiotFiles
{
    private static readonly Dictionary<string, string> Products = new(StringComparer.OrdinalIgnoreCase)
    {
        ["valorant"] = "VALORANT",
        ["league_of_legends"] = "League of Legends",
        ["teamfighttactics"] = "Teamfight Tactics",
        ["bacon"] = "Legends of Runeterra",
        ["lion"] = "2XKO",
    };

    /// <summary>
    /// The installed game of a product folder ("valorant.live"), from its product_settings.yaml, or null when the product
    /// is not installed (no install path) or is the Riot Client itself.
    /// </summary>
    public static InstalledGame? ProductSettings(string productFolder, string yaml)
    {
        ArgumentNullException.ThrowIfNull(productFolder);
        ArgumentNullException.ThrowIfNull(yaml);
        string? path = null;
        foreach (var line in yaml.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("product_install_full_path:", StringComparison.Ordinal))
            {
                path = trimmed["product_install_full_path:".Length..].Trim().Trim('"', '\'');
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var dot = productFolder.IndexOf('.', StringComparison.Ordinal);
        var product = dot > 0 ? productFolder[..dot] : productFolder;
        var patchline = dot > 0 ? productFolder[(dot + 1)..] : "live";
        if (product.Contains("riot_client", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = Products.TryGetValue(product, out var known) ? known : FolderName(path);
        if (!patchline.StartsWith("live", StringComparison.OrdinalIgnoreCase))
        {
            name += $" ({patchline.ToUpperInvariant()})";
        }

        return new InstalledGame(GameLauncher.Riot, productFolder, name, path);
    }

    private static string FolderName(string path)
    {
        var parts = path.Replace('\\', '/').TrimEnd('/').Split('/');
        var last = parts[^1];
        return last.Equals("live", StringComparison.OrdinalIgnoreCase) && parts.Length > 1 ? parts[^2] : last;
    }
}
