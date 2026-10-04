namespace PulseDesk.Infrastructure;

/// <summary>
/// Local folders used by PulseDesk. Everything lives under <c>%LOCALAPPDATA%\PulseDesk</c>:
/// nothing is stored elsewhere and nothing leaves the machine.
/// </summary>
public sealed class PulseDeskPaths
{
    public PulseDeskPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseDesk"))
    {
    }

    /// <param name="dataDirectory">Root folder (overridable for tests).</param>
    public PulseDeskPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    /// <summary><c>%LOCALAPPDATA%\PulseDesk</c>.</summary>
    public string DataDirectory { get; }

    /// <summary><c>%LOCALAPPDATA%\PulseDesk\Logs</c>.</summary>
    public string LogsDirectory => Path.Combine(DataDirectory, "Logs");

    /// <summary><c>%LOCALAPPDATA%\PulseDesk\settings.json</c>.</summary>
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
}
