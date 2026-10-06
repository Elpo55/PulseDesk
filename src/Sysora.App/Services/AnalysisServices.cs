using Microsoft.Extensions.Logging;
using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.Changes;
using Sysora.Core.Gaming;
using Sysora.Core.History;
using Sysora.Core.Interfaces;
using Sysora.Core.Monitoring;

namespace Sysora.App.Services;

/// <summary>
/// Starts and stops the background analysis services (history, application impact, alerts, change
/// detection, gaming sessions, sleep and resume) around the monitoring loop. They must exist before the first measurement, so the history
/// starts with the first sample, and must flush before Sysora exits.
/// </summary>
public sealed class AnalysisServices(
    IPerformanceHistory history,
    HistoryRecorder recorder,
    ProcessHistory processHistory,
    BaselineService baseline,
    AlertService alerts,
    ChangeDetectionService changes,
    GameSessionService games,
    PowerTransitionService power,
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
        games.Start();
        power.Start();
    }

    /// <summary>Writes pending history and stops background work. Called after the monitor has stopped.</summary>
    public async Task StopAsync()
    {
        power.Dispose();

        // A game still running keeps what was measured until now.
        await games.StopAsync().ConfigureAwait(false);
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
