using PulseDesk.Core.Alerts;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Changes;
using PulseDesk.Core.Gaming;
using PulseDesk.Core.History;

namespace PulseDesk.Core.Interfaces;

/// <summary>
/// Local, long-term storage of aggregated history. The only component that knows the storage technology;
/// view models never use it directly (they go through Core services).
/// </summary>
/// <remarks>
/// Implementations must be safe to call from any thread and must never send data anywhere: everything
/// stays in a local file (or in memory in demo mode).
/// </remarks>
public interface IHistoryRepository
{
    /// <summary>Opens or creates the storage. Safe to call more than once.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Writes a batch in one transaction.</summary>
    Task AppendAsync(HistoryBatch batch, CancellationToken cancellationToken);

    /// <summary>System buckets of the given resolution starting in [<paramref name="from"/>, <paramref name="to"/>), oldest first.</summary>
    Task<IReadOnlyList<SystemUsageAggregate>> GetSystemUsageAsync(
        DateTimeOffset from, DateTimeOffset to, HistoryResolution resolution, CancellationToken cancellationToken);

    /// <summary>Events in [<paramref name="from"/>, <paramref name="to"/>], most recent last, at most <paramref name="maxCount"/> (the most recent ones).</summary>
    Task<IReadOnlyList<SystemEvent>> GetEventsAsync(
        DateTimeOffset from, DateTimeOffset to, int maxCount, CancellationToken cancellationToken);

    /// <summary>
    /// Usage of every recorded application over [<paramref name="from"/>, <paramref name="to"/>), combining
    /// detailed and hourly data without counting any period twice.
    /// </summary>
    Task<IReadOnlyList<AppUsageStatistics>> GetAppUsageAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>Usage of one application per bucket of the given resolution, oldest first.</summary>
    Task<IReadOnlyList<AppUsageAggregate>> GetAppTimelineAsync(
        string appKey, DateTimeOffset from, DateTimeOffset to, HistoryResolution resolution, CancellationToken cancellationToken);

    /// <summary>Inserts or updates alerts (by id).</summary>
    Task SaveAlertsAsync(IReadOnlyList<Alert> alerts, CancellationToken cancellationToken);

    /// <summary>Alerts raised since <paramref name="since"/>, oldest first.</summary>
    Task<IReadOnlyList<Alert>> GetAlertsAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Keeps the first snapshot of each local day (later snapshots of the same day are ignored).</summary>
    Task SaveBaselineAsync(SystemBaseline baseline, CancellationToken cancellationToken);

    /// <summary>Snapshots taken since <paramref name="since"/>, oldest first.</summary>
    Task<IReadOnlyList<SystemBaseline>> GetBaselinesAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Records changes; a change already recorded (same id) is left as it was first recorded.</summary>
    Task SaveChangesAsync(IReadOnlyList<DetectedChange> changes, CancellationToken cancellationToken);

    /// <summary>Changes detected since <paramref name="since"/>, oldest first.</summary>
    Task<IReadOnlyList<DetectedChange>> GetChangesAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces a gaming session (by id).</summary>
    Task SaveGameSessionAsync(GameSession session, CancellationToken cancellationToken);

    /// <summary>Gaming sessions started since <paramref name="since"/>, oldest first.</summary>
    Task<IReadOnlyList<GameSession>> GetGameSessionsAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Every application recorded in the history, with when it was first and last seen.</summary>
    Task<IReadOnlyList<KnownApp>> GetKnownAppsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Rolls detailed data up into hourly summaries, removes data older than the retention periods and keeps
    /// the database under its size limit.
    /// </summary>
    Task RunMaintenanceAsync(HistoryRetention retention, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Size and coverage of the stored history.</summary>
    Task<HistoryStorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken);

    /// <summary>Deletes all stored history.</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}
