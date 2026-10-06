namespace PulseDesk.Core.Interfaces;

/// <summary>
/// What Windows knows about the games of the current user. Read-only: PulseDesk never changes these settings.
/// </summary>
public interface IGameLibrary
{
    /// <summary>
    /// Executables Windows recognizes as games (full paths, compared case-insensitively). Returns the last list read;
    /// <see cref="Refresh"/> reads it again. Empty when the information is not available.
    /// </summary>
    IReadOnlySet<string> RecognizedGames { get; }

    /// <summary>Re-reads the list of recognized games. Returns true when it changed. Never throws.</summary>
    bool Refresh();

    /// <summary>Product name from the executable's version information, or null when it has none.</summary>
    string? GetProductName(string executablePath);
}
