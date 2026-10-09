using Sysora.Core.Interfaces;

namespace Sysora.Core.Simulation;

/// <summary>
/// Settings of a demo session: they start from the user's stored settings (so the demo looks like their Sysora) but
/// changes stay in memory. A demo never writes, replaces or sets aside the real settings file.
/// </summary>
public sealed class DemoSettingsStore(ISettingsStore stored) : ISettingsStore
{
    private readonly Lock _lock = new();
    private string? _content;
    private bool _written;

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_written)
            {
                return _content;
            }
        }

        try
        {
            return await stored.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task WriteAsync(string content, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _content = content;
            _written = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>An unreadable file is left where it is: the demo simply starts from the defaults.</summary>
    public Task QuarantineAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _content = null;
            _written = true;
        }

        return Task.CompletedTask;
    }
}
