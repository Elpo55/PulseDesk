using Microsoft.UI.Dispatching;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Models;
using PulseDesk.Core.Monitoring;

namespace PulseDesk.App.Services;

/// <summary>
/// Bridges the background monitoring loop to the UI thread. Updates are coalesced: however many
/// collection rounds complete while the UI is busy, at most one UI update is queued at a time. While the
/// window is hidden or minimized, nothing is dispatched at all.
/// </summary>
public sealed class UiMetricsHub : IDisposable
{
    private readonly IMetricsMonitor _monitor;
    private readonly HealthService _health;
    private readonly DispatcherQueue _dispatcher;
    private readonly Lock _lock = new();
    private MetricKind _pending;
    private bool _scheduled;
    private bool _isUiVisible = true;

    public UiMetricsHub(IMetricsMonitor monitor, HealthService health, DispatcherQueue dispatcher)
    {
        _monitor = monitor;
        _health = health;
        _dispatcher = dispatcher;
        _monitor.MetricsUpdated += OnMetricsUpdated;
        _health.Changed += OnHealthChanged;
    }

    /// <summary>Raised on the UI thread with the metrics refreshed since the previous notification.</summary>
    public event EventHandler<MetricKind>? Updated;

    /// <summary>Raised on the UI thread when health indicators change.</summary>
    public event EventHandler<HealthReport>? HealthChanged;

    public IMetricsMonitor Monitor => _monitor;

    /// <summary>Latest snapshot.</summary>
    public SystemSnapshot Snapshot => _monitor.Current;

    /// <summary>Latest health report.</summary>
    public HealthReport Health => _health.Current;

    /// <summary>
    /// False while the window is hidden or minimized: updates are not dispatched. Setting it back to true
    /// immediately refreshes everything.
    /// </summary>
    public bool IsUiVisible
    {
        get
        {
            lock (_lock)
            {
                return _isUiVisible;
            }
        }

        set
        {
            lock (_lock)
            {
                if (_isUiVisible == value)
                {
                    return;
                }

                _isUiVisible = value;
                if (!value)
                {
                    return;
                }

                _pending = MetricKind.All;
            }

            Schedule();
            _dispatcher.TryEnqueue(() => HealthChanged?.Invoke(this, _health.Current));
        }
    }

    public void Dispose()
    {
        _monitor.MetricsUpdated -= OnMetricsUpdated;
        _health.Changed -= OnHealthChanged;
    }

    private void OnMetricsUpdated(object? sender, SystemMetricsUpdatedEventArgs e)
    {
        lock (_lock)
        {
            _pending |= e.Updated;
            if (!_isUiVisible)
            {
                return;
            }
        }

        Schedule();
    }

    private void OnHealthChanged(object? sender, HealthReport report)
    {
        if (IsUiVisible)
        {
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => HealthChanged?.Invoke(this, report));
        }
    }

    private void Schedule()
    {
        lock (_lock)
        {
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
        }

        // Low priority: input and rendering always come first, which keeps the UI responsive under load.
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, Flush))
        {
            lock (_lock)
            {
                _scheduled = false;
            }
        }
    }

    private void Flush()
    {
        MetricKind kinds;
        lock (_lock)
        {
            kinds = _pending;
            _pending = MetricKind.None;
            _scheduled = false;
        }

        if (kinds != MetricKind.None)
        {
            Updated?.Invoke(this, kinds);
        }
    }
}
