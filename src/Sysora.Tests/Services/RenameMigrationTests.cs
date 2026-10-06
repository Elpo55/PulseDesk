using Sysora.Core.Gaming;
using Sysora.Core.History;
using Sysora.Infrastructure;
using Sysora.Tests.Gaming;

namespace Sysora.Tests.Services;

/// <summary>Data written by the versions released under the previous name stays usable after the rename.</summary>
public sealed class RenameMigrationTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("sysora-migration-tests-");

    private string Current => Path.Combine(_root.FullName, "Sysora");

    private string Legacy => Path.Combine(_root.FullName, SysoraPaths.LegacyFolderName);

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void PreviousFolder_IsMovedWhole()
    {
        WriteLegacy("settings.json", "{}");
        WriteLegacy("history.db", "db");
        WriteLegacy(Path.Combine("Logs", "pulsedesk-20260101.log"), "log");
        var paths = new SysoraPaths(Current, Legacy);

        var result = paths.MigrateLegacyData();

        Assert.True(result.WasMoved);
        Assert.Equal(Current, paths.DataDirectory);
        Assert.False(Directory.Exists(Legacy));
        Assert.Equal("{}", File.ReadAllText(paths.SettingsFile));
        Assert.Equal("db", File.ReadAllText(paths.HistoryDatabaseFile));
        Assert.True(File.Exists(Path.Combine(paths.LogsDirectory, "pulsedesk-20260101.log")));
    }

    [Fact]
    public void NoPreviousFolder_NothingHappens()
    {
        var paths = new SysoraPaths(Current, Legacy);

        var result = paths.MigrateLegacyData();

        Assert.False(result.WasMoved);
        Assert.Null(result.Error);
        Assert.False(Directory.Exists(Current));
    }

    [Fact]
    public void ExistingData_IsNeverOverwritten()
    {
        WriteLegacy("settings.json", "old");
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Current, "settings.json"), "new");
        var paths = new SysoraPaths(Current, Legacy);

        var result = paths.MigrateLegacyData();

        Assert.False(result.WasMoved);
        Assert.Equal("new", File.ReadAllText(paths.SettingsFile));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Legacy, "settings.json")));
    }

    [Fact]
    public void FolderWithOnlyLogs_ReceivesTheMissingEntries()
    {
        WriteLegacy("settings.json", "{}");
        WriteLegacy("history.db", "db");
        Directory.CreateDirectory(Path.Combine(Current, "Logs"));
        File.WriteAllText(Path.Combine(Current, "Logs", "sysora-20260102.log"), "log");
        var paths = new SysoraPaths(Current, Legacy);

        var result = paths.MigrateLegacyData();

        Assert.True(result.WasMoved);
        Assert.True(File.Exists(paths.SettingsFile));
        Assert.True(File.Exists(paths.HistoryDatabaseFile));
        Assert.False(Directory.Exists(Legacy));
    }

    [Fact]
    public void LockedPreviousFolder_IsUsedForTheSession()
    {
        WriteLegacy("history.db", "db");
        var paths = new SysoraPaths(Current, Legacy);

        DataMigrationResult result;
        using (File.Open(Path.Combine(Legacy, "history.db"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = paths.MigrateLegacyData();
        }

        Assert.NotNull(result.Error);
        Assert.Equal(Legacy, paths.DataDirectory);
    }

    [Fact]
    public void GameSessionEndedByTheApp_KeepsItsStoredName()
    {
        var session = GameRecapBuilderTests.Session() with { EndReason = GameSessionEnd.SysoraClosed };

        var json = AnalysisJson.Serialize(session);

        // Sessions recorded before the rename hold this value: it must keep reading back.
        Assert.Contains("\"PulseDeskClosed\"", json, StringComparison.Ordinal);
        Assert.Equal(GameSessionEnd.SysoraClosed, AnalysisJson.DeserializeGameSession(json)?.EndReason);
    }

    private void WriteLegacy(string relativePath, string content)
    {
        var path = Path.Combine(Legacy, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
