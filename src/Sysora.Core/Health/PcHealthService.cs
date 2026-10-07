using Microsoft.Extensions.Logging;
using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.Interfaces;
using Sysora.Core.Settings;

namespace Sysora.Core.Health;

/// <summary>
/// Computes the PC Health score on demand (when its page or the dashboard is visible), from the data the other
/// services already keep: recent history, latest snapshot, baseline, alerts and recurring problems. Nothing runs in
/// the background on its own.
/// </summary>
public sealed class PcHealthService(
    IPerformanceHistory history,
    IMetricsMonitor monitor,
    BaselineService baseline,
    AlertService alerts,
    RecurringProblemService recurring,
    SettingsService settings,
    ILogger<PcHealthService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private PcHealthReport _latest = PcHealthReport.Empty;

    /// <summary>The most recent report.</summary>
    public PcHealthReport Latest => Volatile.Read(ref _latest);

    /// <summary>Recomputes the score off the calling thread.</summary>
    public async Task<PcHealthReport> RefreshAsync(CancellationToken cancellationToken)
    {
        RecurringProblemReport problems;
        try
        {
            problems = await recurring.GetAsync(force: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // The score still works without the history: stability is then reported as not available.
            logger.LogDebug(ex, "Recurring problems could not be read for the PC Health score.");
            problems = recurring.Latest;
        }

        return await Task.Run(() => Compute(problems), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Computes the score on the calling thread with the given recurring problems.</summary>
    public PcHealthReport Compute(RecurringProblemReport problems)
    {
        var current = settings.Current;
        var report = PcHealthScorer.Compute(new PcHealthInput
        {
            Now = _time.GetUtcNow(),
            Snapshot = monitor.Current,
            Recent = history.GetRecent(PcHealthScorer.Window),
            Baseline = baseline.Current,
            Alerts = alerts.Alerts,
            Recurring = problems,
            Thresholds = current.Alerts,
            GpuMonitoringEnabled = current.Monitoring.GpuEnabled,
        });
        Volatile.Write(ref _latest, report);
        return report;
    }
}
