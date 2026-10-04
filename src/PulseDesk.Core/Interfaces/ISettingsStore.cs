namespace PulseDesk.Core.Interfaces;

/// <summary>
/// Persists the settings document (JSON text). Serialization and validation are handled by
/// <see cref="Settings.SettingsSerializer"/>; the store only moves text to and from durable storage.
/// </summary>
public interface ISettingsStore
{
    /// <summary>Reads the stored document, or returns null when none exists yet.</summary>
    Task<string?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Writes the document atomically (a crash never leaves a half-written file).</summary>
    Task WriteAsync(string content, CancellationToken cancellationToken);

    /// <summary>Keeps a copy of an unreadable document so the user can recover it, then removes it.</summary>
    Task QuarantineAsync(CancellationToken cancellationToken);
}
