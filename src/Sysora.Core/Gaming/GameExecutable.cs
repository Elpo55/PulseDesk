namespace Sysora.Core.Gaming;

/// <summary>Why a file the user chose cannot be followed as a game.</summary>
public enum GameExecutableProblem
{
    None,

    /// <summary>The file is not a program (.exe).</summary>
    NotAProgram,

    /// <summary>The file does not exist, or its folder cannot be opened.</summary>
    NotFound,

    /// <summary>The file could not be checked (invalid path, access denied).</summary>
    Unreadable,

    /// <summary>The file is part of Windows.</summary>
    SystemProgram,
}

/// <summary>Checks a program the user chose as a game. Nothing is started, opened for writing or changed.</summary>
public static class GameExecutable
{
    /// <summary>Checks <paramref name="path"/>; <paramref name="windowsFolder"/> is the Windows folder (empty when unknown).</summary>
    public static GameExecutableProblem Check(string? path, string windowsFolder)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return GameExecutableProblem.NotFound;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return GameExecutableProblem.Unreadable;
            }

            if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return GameExecutableProblem.NotAProgram;
            }

            if (!File.Exists(path))
            {
                return GameExecutableProblem.NotFound;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return GameExecutableProblem.Unreadable;
        }

        var windows = windowsFolder.TrimEnd('\\', '/');
        return windows.Length > 0 && InstalledGameIndex.Normalize(path).StartsWith(InstalledGameIndex.Normalize(windows), StringComparison.OrdinalIgnoreCase)
            ? GameExecutableProblem.SystemProgram
            : GameExecutableProblem.None;
    }
}
