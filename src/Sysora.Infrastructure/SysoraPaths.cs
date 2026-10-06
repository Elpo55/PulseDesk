namespace Sysora.Infrastructure;

/// <summary>
/// Local folders used by Sysora. Everything lives under <c>%LOCALAPPDATA%\Sysora</c>:
/// nothing is stored elsewhere and nothing leaves the machine.
/// </summary>
public sealed class SysoraPaths
{
    /// <summary>
    /// Folder name used before the application was renamed to Sysora. Its content (settings, history, logs) is moved
    /// once to the current folder by <see cref="MigrateLegacyData"/>.
    /// </summary>
    public const string LegacyFolderName = "PulseDesk";

    private readonly string? _legacyDataDirectory;

    public SysoraPaths()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sysora"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyFolderName))
    {
    }

    /// <param name="dataDirectory">Root folder (overridable for tests).</param>
    /// <param name="legacyDataDirectory">Folder of a previous installation to migrate from, if any.</param>
    public SysoraPaths(string dataDirectory, string? legacyDataDirectory = null)
    {
        DataDirectory = dataDirectory;
        _legacyDataDirectory = legacyDataDirectory;
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\Sysora</c> (or, for one session, the previous folder when its data could not be moved).
    /// </summary>
    public string DataDirectory { get; private set; }

    /// <summary><c>%LOCALAPPDATA%\Sysora\Logs</c>.</summary>
    public string LogsDirectory => Path.Combine(DataDirectory, "Logs");

    /// <summary><c>%LOCALAPPDATA%\Sysora\settings.json</c>.</summary>
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary><c>%LOCALAPPDATA%\Sysora\history.db</c>: aggregated performance history, events, alerts and changes.</summary>
    public string HistoryDatabaseFile => Path.Combine(DataDirectory, "history.db");

    /// <summary>
    /// Moves the data of a previous installation (settings, history, logs) into <see cref="DataDirectory"/>, once.
    /// Must run before anything opens a file in the data folder. Nothing is overwritten: when the current folder
    /// already holds settings or a history, the previous folder is left untouched. When the move fails, the previous
    /// folder is used for this session so no data seems lost, and the move is tried again at the next start.
    /// Never throws.
    /// </summary>
    public DataMigrationResult MigrateLegacyData()
    {
        var legacy = _legacyDataDirectory;
        if (legacy is null || !Directory.Exists(legacy)
            || string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(DataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return DataMigrationResult.NotNeeded;
        }

        try
        {
            if (!Directory.Exists(DataDirectory))
            {
                // Same volume: a single rename, which moves the history database and its journal together.
                Directory.Move(legacy, DataDirectory);
                return DataMigrationResult.Moved(legacy);
            }

            if (File.Exists(SettingsFile) || File.Exists(HistoryDatabaseFile))
            {
                return DataMigrationResult.NotNeeded;
            }

            // The current folder only holds files written before the move could happen (logs of a first start):
            // move what is missing, entry by entry.
            foreach (var entry in Directory.EnumerateFileSystemEntries(legacy))
            {
                var target = Path.Combine(DataDirectory, Path.GetFileName(entry));
                if (Directory.Exists(entry) && !Directory.Exists(target))
                {
                    Directory.Move(entry, target);
                }
                else if (File.Exists(entry) && !File.Exists(target))
                {
                    File.Move(entry, target);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(legacy).Any())
            {
                Directory.Delete(legacy);
            }

            return DataMigrationResult.Moved(legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Typically a previous version still running and holding the history open: retried at the next start.
            if (!File.Exists(SettingsFile) && !File.Exists(HistoryDatabaseFile))
            {
                DataDirectory = legacy;
            }

            return DataMigrationResult.Failed(legacy, ex);
        }
    }
}

/// <summary>Outcome of <see cref="SysoraPaths.MigrateLegacyData"/>, logged once logging is available.</summary>
/// <param name="Source">Folder the data came from, when there was one.</param>
/// <param name="Error">Why the move failed, when it did.</param>
public sealed record DataMigrationResult(string? Source, Exception? Error)
{
    public static DataMigrationResult NotNeeded { get; } = new(null, null);

    public bool WasMoved => Source is not null && Error is null;

    public static DataMigrationResult Moved(string source) => new(source, null);

    public static DataMigrationResult Failed(string source, Exception error) => new(source, error);
}
