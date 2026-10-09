using Microsoft.Extensions.Logging;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Interfaces;
using Sysora.Core.Settings;
using Sysora.Localization;

namespace Sysora.Core.Troubleshooting;

/// <summary>State of the troubleshooting mode.</summary>
/// <param name="IsRunning">True while an investigation runs.</param>
/// <param name="Start">When it started.</param>
/// <param name="Planned">Planned duration.</param>
/// <param name="Samples">Measurements recorded so far.</param>
/// <param name="Events">Events recorded so far.</param>
/// <param name="Alerts">Alerts raised so far.</param>
public sealed record TroubleshootingStatus(bool IsRunning, DateTimeOffset? Start, TimeSpan Planned, int Samples, int Events, int Alerts)
{
    public static TroubleshootingStatus Idle { get; } = new(false, null, TimeSpan.Zero, 0, 0, 0);
}

/// <summary>
/// Runs a troubleshooting investigation for a limited time: the monitor switches to detailed collection (also while
/// the window is hidden), every measurement and event of the period is recorded at a constant cost, and when the time
/// is up the monitor automatically returns to the user's intensity and a report is produced and kept.
/// </summary>
public sealed class TroubleshootingService : IAsyncDisposable
{
    /// <summary>Durations offered to the user.</summary>
    public static IReadOnlyList<TimeSpan> DurationOptions { get; } =
        [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)];

    /// <summary>Longest investigation allowed: detailed collection must not become the normal mode.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(60);

    /// <summary>Reports kept in the list.</summary>
    public static readonly TimeSpan ListedHistory = TimeSpan.FromDays(30);

    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(2);

    private readonly IMetricsMonitor _monitor;
    private readonly IPerformanceHistory _history;
    private readonly DiagnosisService? _diagnosis;
    private readonly IHistoryRepository _repository;
    private readonly SettingsService _settings;
    private readonly ILogger<TroubleshootingService> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private TroubleshootingRecorder? _recorder;
    private Guid _id;
    private TimeSpan _planned;
    private ITimer? _timer;
    private DateTimeOffset _lastStatus;
    private TroubleshootingReport? _lastReport;

    public TroubleshootingService(
        IMetricsMonitor monitor,
        IPerformanceHistory history,
        DiagnosisService? diagnosis,
        IHistoryRepository repository,
        SettingsService settings,
        ILogger<TroubleshootingService> logger,
        TimeProvider? timeProvider = null)
    {
        _monitor = monitor;
        _history = history;
        _diagnosis = diagnosis;
        _repository = repository;
        _settings = settings;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised (on a background thread) when an investigation starts, progresses or ends.</summary>
    public event EventHandler<TroubleshootingStatus>? StatusChanged;

    /// <summary>Raised (on a background thread) when a report is ready.</summary>
    public event EventHandler<TroubleshootingReport>? Completed;

    public TroubleshootingStatus Status
    {
        get
        {
            lock (_lock)
            {
                return StatusLocked();
            }
        }
    }

    /// <summary>The report of the last investigation of this session, if any.</summary>
    public TroubleshootingReport? LastReport => Volatile.Read(ref _lastReport);

    /// <summary>Starts an investigation. Returns false when one is already running.</summary>
    public bool Start(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "The duration must be positive.");
        }

        duration = duration > MaxDuration ? MaxDuration : duration;
        TroubleshootingStatus status;
        lock (_lock)
        {
            if (_recorder is not null)
            {
                return false;
            }

            var now = _time.GetUtcNow();
            _recorder = new TroubleshootingRecorder(now);
            _id = Guid.NewGuid();
            _planned = duration;
            _lastStatus = now;
            _history.SnapshotRecorded += OnSnapshotRecorded;
            _history.EventRecorded += OnEventRecorded;
            _timer = _time.CreateTimer(_ => _ = StopAsync(TroubleshootingEndReason.Completed), null, duration, Timeout.InfiniteTimeSpan);
            status = StatusLocked();
        }

        _monitor.SetInvestigationMode(true);
        _monitor.RequestRefresh(Models.MetricKind.All);
        _history.AddEvent(new SystemEvent(_time.GetUtcNow(), SystemEventKind.InvestigationStarted, Text.Format(Strings.Trouble_Event_Started, MetricFormatter.DurationCompact(duration)), Strings.Trouble_Event_StartedDetail));
        _logger.LogInformation("Troubleshooting investigation started for {Duration}.", duration);
        StatusChanged?.Invoke(this, status);
        return true;
    }

    /// <summary>Ends the investigation in progress and returns its report (null when none was running).</summary>
    public async Task<TroubleshootingReport?> StopAsync(TroubleshootingEndReason reason = TroubleshootingEndReason.StoppedByUser)
    {
        TroubleshootingRecorder recorder;
        Guid id;
        TimeSpan planned;
        lock (_lock)
        {
            if (_recorder is null)
            {
                return null;
            }

            recorder = _recorder;
            id = _id;
            planned = _planned;
            _recorder = null;
            _history.SnapshotRecorded -= OnSnapshotRecorded;
            _history.EventRecorded -= OnEventRecorded;
            _timer?.Dispose();
            _timer = null;
        }

        // Back to normal first: whatever happens next, detailed collection stops now.
        _monitor.SetInvestigationMode(false);
        var end = _time.GetUtcNow();
        DiagnosisReport? diagnosis = null;
        if (_diagnosis is not null && reason != TroubleshootingEndReason.SysoraClosed)
        {
            try
            {
                diagnosis = _diagnosis.Diagnose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "The closing diagnosis of the investigation failed.");
            }
        }

        TroubleshootingReport report;
        lock (_lock)
        {
            report = recorder.Build(id, end, planned, reason, diagnosis, _monitor.Current);
        }

        Volatile.Write(ref _lastReport, report);
        _history.AddEvent(new SystemEvent(end, SystemEventKind.InvestigationEnded, report.Headline, report.Summary));
        _logger.LogInformation("Troubleshooting investigation ended ({Reason}): {Headline}", reason, report.Headline);
        if (_settings.Current.History.RecordHistory)
        {
            try
            {
                await _repository.SaveTroubleshootingReportAsync(report, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "The troubleshooting report could not be saved.");
            }
        }

        StatusChanged?.Invoke(this, TroubleshootingStatus.Idle);
        Completed?.Invoke(this, report);
        return report;
    }

    /// <summary>Reports of the last investigations, most recent first (plus the last one of this session when not saved).</summary>
    public async Task<IReadOnlyList<TroubleshootingReport>> GetReportsAsync(CancellationToken cancellationToken)
    {
        var stored = await _repository.GetTroubleshootingReportsAsync(_time.GetUtcNow() - ListedHistory, cancellationToken).ConfigureAwait(false);
        var reports = stored.ToList();
        if (LastReport is { } last && reports.All(r => r.Id != last.Id))
        {
            reports.Add(last);
        }

        return reports.OrderByDescending(r => r.Start).ToArray();
    }

    /// <summary>Called when Sysora exits: an investigation in progress is ended and kept.</summary>
    public async ValueTask DisposeAsync() => await StopAsync(TroubleshootingEndReason.SysoraClosed).ConfigureAwait(false);

    private TroubleshootingStatus StatusLocked() =>
        _recorder is { } recorder
            ? new TroubleshootingStatus(true, recorder.Start, _planned, recorder.SampleCount, recorder.EventCount, recorder.AlertCount)
            : TroubleshootingStatus.Idle;

    private void OnSnapshotRecorded(object? sender, MetricSnapshot snapshot)
    {
        TroubleshootingStatus? status = null;
        lock (_lock)
        {
            if (_recorder is null)
            {
                return;
            }

            _recorder.Add(snapshot);
            if (snapshot.Timestamp - _lastStatus >= StatusInterval)
            {
                _lastStatus = snapshot.Timestamp;
                status = StatusLocked();
            }
        }

        if (status is not null)
        {
            StatusChanged?.Invoke(this, status);
        }
    }

    private void OnEventRecorded(object? sender, SystemEvent systemEvent)
    {
        if (systemEvent.Kind is SystemEventKind.InvestigationStarted or SystemEventKind.InvestigationEnded)
        {
            return;
        }

        lock (_lock)
        {
            _recorder?.Add(systemEvent);
        }
    }
}
