using Microsoft.Extensions.Logging;
using Sysora.Core.Analysis;
using Sysora.Core.History;
using Sysora.Core.Interfaces;
using Sysora.Core.Settings;

namespace Sysora.Core.Alerts;

/// <summary>
/// Runs the alert engine in the background, independently of the UI: every few seconds it evaluates the rules
/// on the recent history, records alerts on the replay timeline and in the local history, and notifies
/// subscribers. Alerts of previous sessions are loaded at start.
/// </summary>
public sealed class AlertService : IAsyncDisposable
{
    /// <summary>Interval between two evaluations (alerts are about minutes, not seconds).</summary>
    public static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(5);

    /// <summary>Ongoing alerts are saved at most this often (raises, resolutions and status changes are saved at once).</summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(1);

    /// <summary>Alerts of previous sessions shown in the list.</summary>
    private static readonly TimeSpan LoadedHistory = TimeSpan.FromDays(7);

    private readonly IAlertEngine _engine;
    private readonly IPerformanceHistory _history;
    private readonly IMetricsMonitor _monitor;
    private readonly BaselineService _baseline;
    private readonly SettingsService _settings;
    private readonly IHistoryRepository _repository;
    private readonly ILogger<AlertService> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private DateTimeOffset _lastEvaluation;
    private DateTimeOffset _lastSave;
    private int _evaluating;
    private bool _started;

    public AlertService(
        IAlertEngine engine,
        IPerformanceHistory history,
        IMetricsMonitor monitor,
        BaselineService baseline,
        SettingsService settings,
        IHistoryRepository repository,
        ILogger<AlertService> logger,
        TimeProvider? timeProvider = null)
    {
        _engine = engine;
        _history = history;
        _monitor = monitor;
        _baseline = baseline;
        _settings = settings;
        _repository = repository;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised on a background thread when alerts change (raised, updated, resolved or seen).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on a background thread for each new or reopened alert.</summary>
    public event EventHandler<Alert>? AlertRaised;

    /// <summary>Current alerts, most recent first.</summary>
    public IReadOnlyList<Alert> Alerts
    {
        get
        {
            lock (_lock)
            {
                return _engine.Alerts.Reverse().ToArray();
            }
        }
    }

    /// <summary>Number of alerts not looked at yet.</summary>
    public int NewCount
    {
        get
        {
            lock (_lock)
            {
                return _engine.Alerts.Count(a => a.Status == AlertStatus.New);
            }
        }
    }

    /// <summary>Loads recent alerts from the history and starts evaluating.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Alert> stored = [];
        try
        {
            stored = await _repository.GetAlertsAsync(_time.GetUtcNow() - LoadedHistory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Previous alerts could not be loaded.");
        }

        // The engine decides what an alert still active at the end of the previous session becomes.
        lock (_lock)
        {
            _engine.Load(stored);
            _started = true;
        }

        _history.SnapshotRecorded += OnSnapshotRecorded;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks an alert as seen.</summary>
    public void MarkSeen(Guid id)
    {
        Alert? seen;
        lock (_lock)
        {
            seen = _engine.MarkSeen(id);
        }

        if (seen is not null)
        {
            Save([seen]);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Marks every new alert as seen.</summary>
    public void MarkAllSeen()
    {
        IReadOnlyList<Alert> seen;
        lock (_lock)
        {
            seen = _engine.MarkAllSeen();
        }

        if (seen.Count > 0)
        {
            Save(seen);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Evaluates the rules now (tests and diagnostics); normally driven by new snapshots.</summary>
    public AlertEvaluation EvaluateNow()
    {
        var now = _time.GetUtcNow();
        var context = new AlertContext(now, _monitor.Current, _history.GetRecent(_history.Duration), _baseline.Current, _settings.Current.SmartAlerts);
        AlertEvaluation evaluation;
        IReadOnlyList<Alert> active;
        lock (_lock)
        {
            if (!_started)
            {
                return AlertEvaluation.None;
            }

            evaluation = _engine.Evaluate(context);
            active = _engine.Alerts.Where(a => a.IsActive).ToArray();
            _lastEvaluation = now;
        }

        foreach (var alert in evaluation.Raised)
        {
            _history.AddEvent(new SystemEvent(now, SystemEventKind.AlertRaised, alert.Title, alert.Value) { AppKey = alert.AppKey });
            AlertRaised?.Invoke(this, alert);
        }

        foreach (var alert in evaluation.Resolved)
        {
            _history.AddEvent(new SystemEvent(alert.ResolvedAt ?? now, SystemEventKind.AlertResolved, $"Resolved: {alert.Title}") { AppKey = alert.AppKey });
        }

        if (evaluation.Suppressed > 0)
        {
            _logger.LogInformation("{Count} alert(s) not raised: hourly limit reached.", evaluation.Suppressed);
        }

        if (evaluation.Raised.Count > 0 || evaluation.Resolved.Count > 0)
        {
            Save([.. evaluation.Raised, .. evaluation.Resolved]);
            _lastSave = now;
        }
        else if (active.Count > 0 && now - _lastSave >= SaveInterval)
        {
            Save(active);
            _lastSave = now;
        }

        if (evaluation.HasChanges)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return evaluation;
    }

    /// <summary>Stops evaluating and saves the latest state of ongoing alerts.</summary>
    public async Task StopAsync()
    {
        _history.SnapshotRecorded -= OnSnapshotRecorded;
        Alert[] active;
        lock (_lock)
        {
            active = _engine.Alerts.Where(a => a.IsActive).ToArray();
        }

        if (active.Length > 0 && _settings.Current.History.RecordHistory)
        {
            try
            {
                await _repository.SaveAlertsAsync(active, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Alerts could not be saved on exit.");
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void OnSnapshotRecorded(object? sender, MetricSnapshot snapshot)
    {
        if (snapshot.Timestamp - _lastEvaluation < EvaluationInterval || Interlocked.Exchange(ref _evaluating, 1) == 1)
        {
            return;
        }

        // Evaluate off the monitoring thread so collection is never delayed.
        ThreadPool.UnsafeQueueUserWorkItem(
            static service =>
            {
                try
                {
                    service.EvaluateNow();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    service._logger.LogError(ex, "Alert evaluation failed.");
                }
                finally
                {
                    Volatile.Write(ref service._evaluating, 0);
                }
            },
            this,
            preferLocal: false);
    }

    private void Save(IReadOnlyList<Alert> alerts)
    {
        // "Record history" off: alerts stay in memory for this session only.
        if (!_settings.Current.History.RecordHistory)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _repository.SaveAlertsAsync(alerts, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "Alerts could not be saved.");
            }
        });
    }
}
