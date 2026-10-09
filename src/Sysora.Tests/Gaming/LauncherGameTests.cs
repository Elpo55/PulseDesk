using Sysora.Core.Analysis;
using Sysora.Core.Gaming;
using Sysora.Core.Settings;

namespace Sysora.Tests.Gaming;

public sealed class LauncherGameTests : IDisposable
{
    private const string LibraryFolders = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"apps"
        		{
        			"228980"		"157818239"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"apps"
        		{
        			"730"		"73760122605"
        		}
        	}
        }
        """;

    private const string AppManifest = """
        "AppState"
        {
        	"appid"		"730"
        	"name"		"Counter-Strike 2"
        	"StateFlags"		"4"
        	"installdir"		"Counter-Strike Global Offensive"
        	// a comment
        	"InstalledDepots"
        	{
        		"731"
        		{
        			"manifest"		"123"
        		}
        	}
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sysora-launchers-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SteamLibraryFoldersAreRead()
    {
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], SteamFiles.LibraryFolders(LibraryFolders));
    }

    [Fact]
    public void SteamOldLibraryFormatIsRead()
    {
        Assert.Equal([@"E:\Games"], SteamFiles.LibraryFolders("\"LibraryFolders\" { \"TimeNextStatsReport\" \"1\" \"1\" \"E:\\\\Games\" }"));
    }

    [Fact]
    public void SteamAppManifestIsRead()
    {
        var app = SteamFiles.AppManifest(AppManifest);

        Assert.NotNull(app);
        Assert.Equal("730", app.AppId);
        Assert.Equal("Counter-Strike 2", app.Name);
        Assert.Equal("Counter-Strike Global Offensive", app.InstallDir);
        Assert.True(app.IsFullyInstalled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a manifest")]
    [InlineData("\"AppState\" { \"name\" \"No id\" }")]
    public void InvalidSteamManifestsAreIgnored(string text)
    {
        Assert.Null(SteamFiles.AppManifest(text));
    }

    [Fact]
    public void EpicGameManifestIsRead()
    {
        var game = EpicFiles.Manifest("""{ "DisplayName": "Rocket League®", "InstallLocation": "D:\\Games\\rocketleague", "AppName": "Sugar", "AppCategories": ["public", "games", "applications"], "bIsIncompleteInstall": false }""");

        Assert.NotNull(game);
        Assert.Equal(GameLauncher.EpicGames, game.Launcher);
        Assert.Equal("Rocket League®", game.Name);
        Assert.Equal(@"D:\Games\rocketleague", game.InstallFolder);
        Assert.Equal("EpicGames:Sugar", game.Key);
    }

    [Theory]
    [InlineData("""{ "DisplayName": "Unreal Engine", "InstallLocation": "C:\\UE_5.4", "AppName": "UE_5.4", "AppCategories": ["engines"] }""")]
    [InlineData("""{ "DisplayName": "Half done", "InstallLocation": "C:\\Games\\X", "AppName": "X", "bIsIncompleteInstall": true }""")]
    [InlineData("""{ "DisplayName": "No folder", "AppName": "Y" }""")]
    [InlineData("{ broken")]
    public void EpicManifestsThatAreNotCompleteGamesAreIgnored(string json)
    {
        Assert.Null(EpicFiles.Manifest(json));
    }

    [Fact]
    public void RiotProductSettingsAreRead()
    {
        var game = RiotFiles.ProductSettings("valorant.live", "auto_patching_enabled_by_player: false\nproduct_install_full_path: \"D:/GAMES/Riot Games/VALORANT/live\"\nproduct_install_root: \"D:/GAMES/Riot Games\"\n");

        Assert.NotNull(game);
        Assert.Equal("VALORANT", game.Name);
        Assert.Equal("D:/GAMES/Riot Games/VALORANT/live".Replace('/', Path.DirectorySeparatorChar), game.InstallFolder);
    }

    [Fact]
    public void RiotTestPatchLinesAreNamedAsSuch()
    {
        var game = RiotFiles.ProductSettings("league_of_legends.pbe", "product_install_full_path: \"D:/GAMES/Riot Games/League of Legends (PBE)\"");

        Assert.Equal("League of Legends (PBE)", game?.Name);
    }

    [Fact]
    public void RiotProductsWithoutAnInstallPathAreNotInstalled()
    {
        Assert.Null(RiotFiles.ProductSettings("bacon.live", "locale_data:\n    available_locales:\n    - \"en_US\""));
    }

    [Fact]
    public void ScannerReadsEveryLauncherAndKeepsOnlyExistingFolders()
    {
        // Steam: one library with an installed game, one tool and one game still downloading.
        var steam = Directory.CreateDirectory(Path.Combine(_root, "Steam", "steamapps")).Parent!.FullName;
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { }");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_730.acf"), AppManifest);
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common", "Counter-Strike Global Offensive"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_228980.acf"), "\"AppState\" { \"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\" \"StateFlags\" \"4\" \"installdir\" \"Steamworks Shared\" }");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common", "Steamworks Shared"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_9.acf"), "\"AppState\" { \"appid\" \"9\" \"name\" \"Downloading\" \"StateFlags\" \"1026\" \"installdir\" \"Downloading\" }");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common", "Downloading"));

        // Epic: one game whose folder was deleted (moved or uninstalled outside the launcher).
        var epic = Directory.CreateDirectory(Path.Combine(_root, "Epic")).FullName;
        File.WriteAllText(Path.Combine(epic, "a.item"), $$"""{ "DisplayName": "Gone", "InstallLocation": "{{Path.Combine(_root, "Gone").Replace("\\", "\\\\", StringComparison.Ordinal)}}", "AppName": "Gone", "AppCategories": ["games"] }""");

        // Riot: VALORANT.
        var riot = Directory.CreateDirectory(Path.Combine(_root, "Riot", "valorant.live")).Parent!.FullName;
        var valorant = Directory.CreateDirectory(Path.Combine(_root, "Riot Games", "VALORANT", "live")).FullName;
        File.WriteAllText(Path.Combine(riot, "valorant.live", "valorant.live.product_settings.yaml"), $"product_install_full_path: \"{valorant.Replace('\\', '/')}\"");

        var games = LauncherGameScanner.Scan(new LauncherLocations
        {
            SteamFolders = [steam, steam],
            EpicManifestFolders = [epic, Path.Combine(_root, "missing")],
            RiotMetadataFolders = [riot],
        });

        Assert.Equal(["Counter-Strike 2", "VALORANT"], games.Select(g => g.Name));
        Assert.Equal([GameLauncher.Steam, GameLauncher.Riot], games.Select(g => g.Launcher));
    }

    [Fact]
    public void MissingLaunchersGiveNoGame()
    {
        Assert.Empty(LauncherGameScanner.Scan(new LauncherLocations { SteamFolders = [Path.Combine(_root, "nothing")] }));
    }

    [Fact]
    public void AGameInALauncherFolderIsIdentifiedWithItsName()
    {
        var index = new InstalledGameIndex([new InstalledGame(GameLauncher.EpicGames, "Fortnite", "Fortnite", @"D:\ELIOR GAMES\Fortnite")]);

        var match = GameClassifier.Classify(@"D:\ELIOR GAMES\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", new GamingSettings(), new HashSet<string>(), index);

        Assert.NotNull(match);
        Assert.Equal(GameDetectionSource.Launcher, match.Source);
        Assert.Equal(ConfidenceLevel.High, match.Confidence);
        Assert.Equal("Fortnite", match.GameName);
        Assert.Equal("Epic Games", match.Library);
    }

    [Fact]
    public void ForwardSlashFoldersMatchWindowsPaths()
    {
        var index = new InstalledGameIndex([new InstalledGame(GameLauncher.Riot, "valorant.live", "VALORANT", "D:/GAMES/Riot Games/VALORANT/live")]);

        Assert.Equal("VALORANT", index.Find(@"D:\GAMES\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe")?.Name);
        Assert.Null(index.Find(@"D:\GAMES\Riot Games\VALORANT\livestream.exe"));
    }

    [Fact]
    public void HelpersInsideALauncherFolderAreNotGames()
    {
        var index = new InstalledGameIndex([new InstalledGame(GameLauncher.Riot, "league_of_legends.live", "League of Legends", @"D:\Riot Games\League of Legends")]);

        Assert.Null(GameClassifier.Classify(@"D:\Riot Games\League of Legends\LeagueClient.exe", new GamingSettings { DetectLibraryGames = false }, new HashSet<string>(), index));
        Assert.NotNull(GameClassifier.Classify(@"D:\Riot Games\League of Legends\Game\League of Legends.exe", new GamingSettings { DetectLibraryGames = false }, new HashSet<string>(), index));
    }

    [Fact]
    public void IgnoredLauncherGamesAreNotFollowed()
    {
        var index = new InstalledGameIndex([new InstalledGame(GameLauncher.Steam, "730", "Counter-Strike 2", @"D:\Lib\cs2")]);
        var settings = new GamingSettings { IgnoredLauncherGames = ["Steam:730"], DetectLibraryGames = false };

        Assert.Null(GameClassifier.Classify(@"D:\Lib\cs2\game\bin\win64\cs2.exe", settings, new HashSet<string>(), index));
    }

    [Fact]
    public void LauncherDetectionCanBeTurnedOff()
    {
        var index = new InstalledGameIndex([new InstalledGame(GameLauncher.Steam, "730", "Counter-Strike 2", @"D:\Lib\cs2")]);
        var settings = new GamingSettings { DetectLauncherGames = false, DetectLibraryGames = false };

        Assert.Null(GameClassifier.Classify(@"D:\Lib\cs2\cs2.exe", settings, new HashSet<string>(), index));
    }

    [Fact]
    public void ConfirmedGamesAreFollowedWithoutTheLauncher()
    {
        var settings = new GamingSettings
        {
            DetectLauncherGames = false,
            DetectLibraryGames = false,
            ConfirmedGames = [new ConfirmedGame("Steam:730", "Counter-Strike 2", @"D:\Lib\cs2")],
        };

        var match = GameClassifier.Classify(@"D:\Lib\cs2\cs2.exe", settings, new HashSet<string>(), InstalledGameIndex.Empty);

        Assert.Equal("Counter-Strike 2", match?.GameName);
        Assert.Equal(ConfidenceLevel.High, match?.Confidence);
    }

    [Fact]
    public void NotAGameWinsOverEveryDetection()
    {
        var index = new InstalledGameIndex([new InstalledGame(GameLauncher.Steam, "730", "Counter-Strike 2", @"D:\Lib\cs2")]);
        var settings = new GamingSettings { ExcludedGames = [@"D:\Lib\cs2\cs2.exe"], ConfirmedGames = [new ConfirmedGame("Steam:730", "Counter-Strike 2", @"D:\Lib\cs2")] };

        Assert.Null(GameClassifier.Classify(@"D:\Lib\cs2\cs2.exe", settings, new HashSet<string>(), index));
    }

    [Fact]
    public void GameChoicesAreSavedAndReadBack()
    {
        var settings = new AppSettings
        {
            Gaming = new GamingSettings
            {
                IgnoredLauncherGames = ["Steam:730"],
                ConfirmedGames = [new ConfirmedGame("Riot:valorant.live", "VALORANT", "D:/GAMES/Riot Games/VALORANT/live")],
                DetectLauncherGames = false,
            },
        };

        Assert.True(SettingsSerializer.TryDeserialize(SettingsSerializer.Serialize(settings), out var read, out _));
        Assert.Equal(settings.Gaming, read.Gaming);
    }

    [Fact]
    public void LauncherDetectionIsOnByDefault()
    {
        Assert.True(new GamingSettings().DetectLauncherGames);
        Assert.True(SettingsSerializer.TryDeserialize("""{ "gaming": { "enabled": true } }""", out var read, out _));
        Assert.True(read.Gaming.DetectLauncherGames);
    }

    [Fact]
    public void ChosenProgramsAreChecked()
    {
        var game = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "Games", "Racer")).FullName, "racer.exe");
        File.WriteAllText(game, "");
        var notes = Path.Combine(_root, "Games", "Racer", "notes.txt");
        File.WriteAllText(notes, "");
        var windows = Directory.CreateDirectory(Path.Combine(_root, "Windows")).FullName;
        var system = Path.Combine(windows, "tool.exe");
        File.WriteAllText(system, "");
        var besideWindows = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "WindowsApps")).FullName, "app.exe");
        File.WriteAllText(besideWindows, "");

        Assert.Equal(GameExecutableProblem.None, GameExecutable.Check(game, windows));
        Assert.Equal(GameExecutableProblem.None, GameExecutable.Check(besideWindows, windows));
        Assert.Equal(GameExecutableProblem.None, GameExecutable.Check(system, ""));
        Assert.Equal(GameExecutableProblem.SystemProgram, GameExecutable.Check(system, windows + Path.DirectorySeparatorChar));
        Assert.Equal(GameExecutableProblem.NotAProgram, GameExecutable.Check(notes, windows));
        Assert.Equal(GameExecutableProblem.NotFound, GameExecutable.Check(Path.Combine(_root, "Moved", "racer.exe"), windows));
        Assert.Equal(GameExecutableProblem.NotFound, GameExecutable.Check("", windows));
        Assert.Equal(GameExecutableProblem.Unreadable, GameExecutable.Check("racer.exe", windows));
    }
}
