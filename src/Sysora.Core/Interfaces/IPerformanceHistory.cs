using Sysora.Core.History;

namespace Sysora.Core.Interfaces;

/// <summary>
/// Short-term, high-frequency history of the whole system (one snapshot per CPU sample, 15 minutes by
/// default) and of the events Sysora observed. Held in memory in ring buffers: its size never grows.
/// </summary>
public interface IPerformanceHistory
{
    /// <summary>Raised on a background thread after each new snapshot. Handlers must return quickly.</summary>
    event EventHandler<MetricSnapshot>? SnapshotRecorded;

    /// <summary>Raised on a background thread when an event is recorded.</summary>
    event EventHandler<SystemEvent>? EventRecorded;

    /// <summary>How much time the snapshot buffer covers.</summary>
    TimeSpan Duration { get; }

    /// <summary>Number of snapshots the buffer can hold.</summary>
    int Capacity { get; }

    /// <summary>Most recent snapshot, or null before the first measurement.</summary>
    MetricSnapshot? Latest { get; }

    /// <summary>Snapshots taken in [<paramref name="from"/>, <paramref name="to"/>], oldest first.</summary>
    IReadOnlyList<MetricSnapshot> GetSnapshots(DateTimeOffset from, DateTimeOffset to);

    /// <summary>Snapshots of the last <paramref name="window"/> before the latest snapshot, oldest first.</summary>
    IReadOnlyList<MetricSnapshot> GetRecent(TimeSpan window);

    /// <summary>The snapshot closest to <paramref name="time"/>, or null when the buffer is empty.</summary>
    MetricSnapshot? GetNearest(DateTimeOffset time);

    /// <summary>Events recorded in [<paramref name="from"/>, <paramref name="to"/>], oldest first.</summary>
    IReadOnlyList<SystemEvent> GetEvents(DateTimeOffset from, DateTimeOffset to);

    /// <summary>Records an event observed by another component (for example an alert).</summary>
    void AddEvent(SystemEvent systemEvent);
}
