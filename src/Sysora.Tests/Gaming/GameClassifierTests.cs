using Sysora.Core.Analysis;
using Sysora.Core.Gaming;
using Sysora.Core.Settings;

namespace Sysora.Tests.Gaming;

public sealed class GameClassifierTests
{
    private const string SteamGame = @"D:\SteamLibrary\steamapps\common\Supermarket Together\Supermarket Together.exe";
    private const string Recognized = @"D:\GAMES\Forza Horizon 5\ForzaHorizon5.exe";

    private static readonly IReadOnlySet<string> Windows = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Recognized };

    [Fact]
    public void ExecutableRecognizedByWindows_IsAGameWithHighConfidence()
    {
        var match = GameClassifier.Classify(Recognized.ToUpperInvariant(), new GamingSettings(), Windows);

        Assert.NotNull(match);
        Assert.Equal(GameDetectionSource.WindowsRecognized, match.Source);
        Assert.Equal(ConfidenceLevel.High, match.Confidence);
    }

    [Fact]
    public void ExecutableInGameLibrary_IsALikelyGameWithMediumConfidence()
    {
        var match = GameClassifier.Classify(SteamGame, new GamingSettings(), new HashSet<string>());

        Assert.NotNull(match);
        Assert.Equal(GameDetectionSource.GameLibrary, match.Source);
        Assert.Equal(ConfidenceLevel.Medium, match.Confidence);
        Assert.Equal("Steam", match.Library);
        Assert.Contains("not confirmed", match.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Game\Engine\Binaries\Win64\CrashReportClient.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Game\UnityCrashHandler64.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Game\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Game\Launcher.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Steamworks Shared\_CommonRedist\vcredist\vc_redist.x64.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\wallpaper_engine\wallpaper64.exe")]
    [InlineData(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE")]
    public void HelpersToolsAndOrdinaryApplications_AreNotGames(string path)
    {
        Assert.Null(GameClassifier.Classify(path, new GamingSettings(), new HashSet<string>()));
    }

    [Fact]
    public void GameTitleContainingCrash_IsNotMistakenForACrashReporter()
    {
        var match = GameClassifier.Classify(@"D:\SteamLibrary\steamapps\common\Crash Bandicoot\CrashBandicootNSaneTrilogy.exe", new GamingSettings(), new HashSet<string>());

        Assert.NotNull(match);
    }

    [Fact]
    public void ExcludedExecutable_IsNeverAGame_EvenWhenRecognizedOrAdded()
    {
        var settings = new GamingSettings { ExcludedGames = [Recognized], AddedGames = [Recognized] };

        Assert.Null(GameClassifier.Classify(Recognized, settings, Windows));
    }

    [Fact]
    public void ExecutableMarkedByTheUser_IsAGameAnywhere()
    {
        var match = GameClassifier.Classify(@"C:\Tools\Emulator\emu.exe", new GamingSettings { AddedGames = [@"c:\tools\emulator\EMU.exe"] }, new HashSet<string>());

        Assert.Equal(GameDetectionSource.UserMarked, match?.Source);
    }

    [Fact]
    public void SystemExecutable_IsNotAGame_EvenWhenListedByWindows()
    {
        var system = @"C:\Windows\System32\notepad.exe";

        Assert.Null(GameClassifier.Classify(system, new GamingSettings(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { system }));
    }

    [Fact]
    public void DisabledDetectionSources_AreNotUsed()
    {
        var settings = new GamingSettings { DetectLibraryGames = false, DetectWindowsGames = false };

        Assert.Null(GameClassifier.Classify(SteamGame, settings, new HashSet<string>()));
        Assert.Null(GameClassifier.Classify(Recognized, settings, Windows));
    }

    [Fact]
    public void ProtectedProcessWithoutPath_IsNotAGame()
    {
        Assert.Null(GameClassifier.Classify(null, new GamingSettings(), Windows));
    }

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Supermarket Together\Supermarket Together.exe", "Unity Player", "Supermarket Together")]
    [InlineData(@"D:\GAMES\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", null, "VALORANT")]
    [InlineData(@"D:\Games\Other\Thing-Win64-Shipping.exe", "UnrealGame", "Thing")]
    [InlineData(@"D:\GAMES\Forza Horizon 5\ForzaHorizon5.exe", "Forza Horizon 5", "Forza Horizon 5")]
    public void DisplayName_PrefersMeaningfulNames(string path, string? product, string expected)
    {
        Requires.WindowsPaths();
        Assert.Equal(expected, GameClassifier.DisplayName(path, product));
    }

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Game Title\bin\win64\game.exe", @"D:\SteamLibrary\steamapps\common\Game Title\")]
    [InlineData(@"D:\Games\Title\bin\game.exe", @"D:\Games\Title\bin\")]
    [InlineData(@"D:\Games\Title\game.exe", @"D:\Games\Title\")]
    [InlineData(@"D:\Games\game.exe", null)]
    [InlineData(@"D:\game.exe", null)]
    public void InstallFolder_IsSpecificToTheGame(string path, string? expected)
    {
        Requires.WindowsPaths();
        Assert.Equal(expected, GameClassifier.InstallFolder(path));
    }
}
