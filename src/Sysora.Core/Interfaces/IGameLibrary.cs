using Sysora.Core.Gaming;

namespace Sysora.Core.Interfaces;

/// <summary>
/// What Windows knows about the games of the current user. Read-only: Sysora never changes these settings.
/// </summary>
public interface IGameLibrary
{
    /// <summary>
    /// Executables Windows recognizes as games (full paths, compared case-insensitively). Returns the last list read;
    /// <see cref="Refresh"/> reads it again. Empty when the information is not available.
    /// </summary>
    IReadOnlySet<string> RecognizedGames { get; }

    /// <summary>
    /// Games the launchers report as installed (Steam, Epic Games, Riot, GOG), read from their local files. Returns the
    /// last list read; <see cref="Refresh"/> reads it again. Empty when no launcher is found.
    /// </summary>
    InstalledGameIndex InstalledGames { get; }

    /// <summary>Re-reads the recognized and installed games. Returns true when either list changed. Never throws.</summary>
    bool Refresh();

    /// <summary>Product name from the executable's version information, or null when it has none.</summary>
    string? GetProductName(string executablePath);
}
