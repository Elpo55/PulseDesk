using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Models;
using PulseDesk.Core.Settings;

namespace PulseDesk.Core.Monitoring;

/// <summary>
/// Keeps an up-to-date <see cref="HealthReport"/> by evaluating every monitor update in the background,
/// independently of what the UI shows.
/// </summary>
public sealed class HealthService : IDisposable
{
    private readonly IMetricsMonitor _monitor;
    private readonly SettingsService _settings;
    private readonly HealthEvaluator _evaluator;
    private readonly Lock _lock = new();
    private HealthReport _current = HealthReport.Empty;

    public HealthService(IMetricsMonitor monitor, SettingsService settings)
    {
        _monitor = monitor;
        _settings = settings;
        _evaluator = new HealthEvaluator(settings.Current.Alerts);
        _monitor.MetricsUpdated += OnMetricsUpdated;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised on a background thread when the indicators change.</summary>
    public event EventHandler<HealthReport>? Changed;

    /// <summary>Latest report.</summary>
    public HealthReport Current => Volatile.Read(ref _current);

    public void Dispose()
    {
        _monitor.MetricsUpdated -= OnMetricsUpdated;
        _settings.Changed -= OnSettingsChanged;
    }

    private void OnMetricsUpdated(object? sender, SystemMetricsUpdatedEventArgs e) => Evaluate(e.Snapshot, e.Updated);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Alerts == e.Current.Alerts)
        {
            return;
        }

        lock (_lock)
        {
            _evaluator.Configure(e.Current.Alerts);
        }

        Evaluate(_monitor.Current, MetricKind.None);
    }

    private void Evaluate(SystemSnapshot snapshot, MetricKind updated)
    {
        HealthReport report;
        lock (_lock)
        {
            report = _evaluator.Evaluate(snapshot, updated);
            if (report.HasSameIndicators(_current))
            {
                return;
            }

            Volatile.Write(ref _current, report);
        }

        Changed?.Invoke(this, report);
    }
}
