using Microsoft.Extensions.Logging;
using PulseDesk.Core.History;
using PulseDesk.Core.Interfaces;

namespace PulseDesk.Core.Analysis;

/// <summary>
/// Keeps the usual-behavior baseline of the PC up to date from the local history (every 30 minutes).
/// </summary>
public sealed class BaselineService : IAsyncDisposable
{
    /// <summary>How often the baseline is recomputed.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    private readonly IHistoryRepository _repository;
    private readonly ILogger<BaselineService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private UsageBaseline _current = UsageBaseline.Empty;

    public BaselineService(IHistoryRepository repository, ILogger<BaselineService> logger, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised on a background thread when the baseline is recomputed.</summary>
    public event EventHandler<UsageBaseline>? Changed;

    /// <summary>The latest baseline (collecting until enough history exists).</summary>
    public UsageBaseline Current => Volatile.Read(ref _current);

    /// <summary>Computes the baseline now, then every <see cref="RefreshInterval"/>.</summary>
    public void Start()
    {
        _timer ??= _time.CreateTimer(_ => _ = RefreshAsync(CancellationToken.None), null, TimeSpan.Zero, RefreshInterval);
    }

    /// <summary>Recomputes the baseline from the last seven days of history.</summary>
    public async Task<UsageBaseline> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var minutes = await _repository.GetSystemUsageAsync(now - BaselineCalculator.Window, now, HistoryResolution.Minute, cancellationToken).ConfigureAwait(false);
            var baseline = BaselineCalculator.Compute(minutes, now);
            Volatile.Write(ref _current, baseline);
            Changed?.Invoke(this, baseline);
            return baseline;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            _logger.LogWarning(ex, "The usage baseline could not be computed.");
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
            _timer = null;
        }
    }
}
