using Microsoft.Extensions.Logging;
using Sysora.Core.Interfaces;

namespace Sysora.Core.Settings;

/// <summary>Arguments of <see cref="SettingsService.Changed"/>.</summary>
public sealed class SettingsChangedEventArgs(AppSettings previous, AppSettings current) : EventArgs
{
    public AppSettings Previous { get; } = previous;

    public AppSettings Current { get; } = current;
}

/// <summary>
/// Holds the current settings, validates every change and saves changes to disk shortly after they happen
/// (several quick edits produce a single write).
/// </summary>
public sealed class SettingsService : IAsyncDisposable
{
    /// <summary>Delay between the last change and the write to disk.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ITimer? _saveTimer;
    private AppSettings _current = AppSettings.Default;
    private bool _dirty;

    public SettingsService(ISettingsStore store, ILogger<SettingsService> logger, TimeProvider? timeProvider = null)
    {
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after a change, on the thread that made it.</summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>The current, validated settings.</summary>
    public AppSettings Current => Volatile.Read(ref _current);

    /// <summary>
    /// Loads settings from the store. A missing document yields defaults; an unreadable one is
    /// quarantined (kept aside for the user) and replaced by defaults. Never throws for I/O errors.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        string? json;
        try
        {
            json = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Settings could not be read; using defaults.");
            return;
        }

        if (json is null)
        {
            _logger.LogInformation("No settings file found; using defaults.");
            return;
        }

        if (SettingsSerializer.TryDeserialize(json, out var settings, out var error))
        {
            Volatile.Write(ref _current, settings);
            return;
        }

        _logger.LogWarning("Settings file is invalid ({Error}); it was set aside and defaults are used.", error);
        try
        {
            await _store.QuarantineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Invalid settings file could not be set aside.");
        }
    }

    /// <summary>Applies a change, validates the result and schedules a save.</summary>
    /// <param name="change">Returns the new settings from the current ones, typically with a <c>with</c> expression.</param>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        AppSettings previous, updated;
        lock (_lock)
        {
            previous = _current;
            updated = SettingsValidator.Normalize(change(previous));
            if (updated == previous)
            {
                return;
            }

            Volatile.Write(ref _current, updated);
            _dirty = true;
            _saveTimer ??= _timeProvider.CreateTimer(_ => _ = SaveAsync(CancellationToken.None), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, updated));
    }

    /// <summary>Writes pending changes immediately (called on exit).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _saveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        lock (_lock)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
        }

        _writeGate.Dispose();
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings snapshot;
            lock (_lock)
            {
                if (!_dirty)
                {
                    return;
                }

                snapshot = _current;
                _dirty = false;
            }

            await _store.WriteAsync(SettingsSerializer.Serialize(snapshot), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Settings saved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_lock)
            {
                _dirty = true;
            }

            _logger.LogError(ex, "Settings could not be saved.");
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
