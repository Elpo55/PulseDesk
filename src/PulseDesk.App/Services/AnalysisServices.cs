using Microsoft.Extensions.Logging;
using PulseDesk.Core.Alerts;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Changes;
using PulseDesk.Core.History;
using PulseDesk.Core.Interfaces;

namespace PulseDesk.App.Services;

/// <summary>
/// Starts and stops the background analysis services (history, application impact, alerts, change
/// detection) around the monitoring loop. They must exist before the first measurement, so the history
/// starts with the first sample, and must flush before PulseDesk exits.
/// </summary>
public sealed class AnalysisServices(
    IPerformanceHistory history,
    HistoryRecorder recorder,
    ProcessHistory processHistory,
    BaselineService baseline,
    AlertService alerts,
    ChangeDetectionService changes,
    ILogger<AnalysisServices> logger)
{
    /// <summary>Opens the local history and subscribes every analysis service to the monitor.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Replay buffer: {Capacity} snapshots ({Duration}).", history.Capacity, history.Duration);
        processHistory.BucketCompleted += OnAppBucketCompleted;
        await recorder.StartAsync(cancellationToken).ConfigureAwait(false);
        baseline.Start();
        await alerts.StartAsync(cancellationToken).ConfigureAwait(false);
        changes.Start();
    }

    /// <summary>Writes pending history and stops background work. Called after the monitor has stopped.</summary>
    public async Task StopAsync()
    {
        await changes.DisposeAsync().ConfigureAwait(false);
        await alerts.StopAsync().ConfigureAwait(false);
        processHistory.BucketCompleted -= OnAppBucketCompleted;
        if (processHistory.FlushBucket() is { } partial)
        {
            recorder.Record(partial);
        }

        await baseline.DisposeAsync().ConfigureAwait(false);
        await recorder.StopAsync().ConfigureAwait(false);
    }

    private void OnAppBucketCompleted(object? sender, Core.History.AppUsageBucket bucket) => recorder.Record(bucket);
}
