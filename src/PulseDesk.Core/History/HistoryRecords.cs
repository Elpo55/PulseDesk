namespace PulseDesk.Core.History;

/// <summary>Data written to the long-term history in one transaction.</summary>
/// <param name="SystemUsage">Completed system buckets.</param>
/// <param name="Events">Events to keep.</param>
public sealed record HistoryBatch(IReadOnlyList<SystemUsageAggregate> SystemUsage, IReadOnlyList<SystemEvent> Events)
{
    /// <summary>Completed application usage buckets.</summary>
    public IReadOnlyList<AppUsageBucket> AppUsage { get; init; } = [];

    /// <summary>True when there is nothing to write.</summary>
    public bool IsEmpty => SystemUsage.Count == 0 && Events.Count == 0 && AppUsage.Count == 0;
}

/// <summary>How long the long-term history keeps each kind of data.</summary>
/// <param name="Detail">Minute-level system data and five-minute application data.</param>
/// <param name="Summary">Hourly summaries, events, alerts and changes.</param>
public sealed record HistoryRetention(TimeSpan Detail, TimeSpan Summary)
{
    /// <summary>Upper bound of the database size; beyond it the oldest detailed data is removed first.</summary>
    public long MaxDatabaseBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Creates the retention from the user's settings.</summary>
    public static HistoryRetention From(Settings.HistorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new HistoryRetention(TimeSpan.FromDays(settings.DetailRetentionDays), TimeSpan.FromDays(settings.SummaryRetentionDays));
    }
}

/// <summary>Size and coverage of the local history database.</summary>
/// <param name="SizeBytes">Size on disk (0 for an in-memory database).</param>
/// <param name="OldestData">Time of the oldest system data, when any.</param>
/// <param name="Location">File path, or a description of where data is kept.</param>
public sealed record HistoryStorageInfo(long SizeBytes, DateTimeOffset? OldestData, string Location);
