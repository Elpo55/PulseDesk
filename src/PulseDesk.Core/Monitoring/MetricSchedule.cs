using PulseDesk.Core.Models;

namespace PulseDesk.Core.Monitoring;

/// <summary>
/// Decides which metrics are due at a given time. Each metric has its own interval; metrics due
/// within a small tolerance of each other are collected together to limit wake-ups.
/// </summary>
/// <remarks>
/// Time is expressed as elapsed time on a monotonic clock, so changing the system clock does not
/// disturb the schedule. Not thread-safe: the monitor serializes access.
/// </remarks>
internal sealed class MetricSchedule
{
    /// <summary>Metrics due within this tolerance are collected in the same round.</summary>
    public static readonly TimeSpan CoalesceTolerance = TimeSpan.FromMilliseconds(30);

    private readonly Dictionary<MetricKind, Entry> _entries = [];

    /// <summary>Sets the interval of a metric, or disables it with null. A newly enabled metric is due immediately.</summary>
    public void Configure(MetricKind kind, TimeSpan? interval, TimeSpan now)
    {
        if (interval is not { } value)
        {
            _entries.Remove(kind);
            return;
        }

        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Intervals must be positive.");
        }

        if (_entries.TryGetValue(kind, out var existing))
        {
            // Keep the cadence, but never wait longer than the new interval.
            var nextDue = existing.NextDue - existing.Interval + value;
            _entries[kind] = new Entry(value, nextDue < existing.NextDue ? nextDue : existing.NextDue);
        }
        else
        {
            _entries[kind] = new Entry(value, now);
        }
    }

    /// <summary>The metrics currently scheduled.</summary>
    public MetricKind Enabled => _entries.Keys.Aggregate(MetricKind.None, (all, kind) => all | kind);

    /// <summary>Interval of a metric, or null when it is disabled.</summary>
    public TimeSpan? GetInterval(MetricKind kind) =>
        _entries.TryGetValue(kind, out var entry) ? entry.Interval : null;

    /// <summary>Marks metrics as due now (manual refresh). Disabled metrics are ignored.</summary>
    public void MarkDue(MetricKind kinds, TimeSpan now)
    {
        foreach (var kind in _entries.Keys.ToArray())
        {
            if ((kinds & kind) != 0)
            {
                _entries[kind] = _entries[kind] with { NextDue = now };
            }
        }
    }

    /// <summary>
    /// Returns the metrics due at <paramref name="now"/> and schedules their next collection.
    /// The next due time keeps a steady cadence; if the loop fell behind, it restarts from now.
    /// </summary>
    public MetricKind TakeDue(TimeSpan now)
    {
        var due = MetricKind.None;
        foreach (var (kind, entry) in _entries.ToArray())
        {
            if (entry.NextDue > now + CoalesceTolerance)
            {
                continue;
            }

            due |= kind;
            var next = entry.NextDue + entry.Interval;
            _entries[kind] = entry with { NextDue = next > now ? next : now + entry.Interval };
        }

        return due;
    }

    /// <summary>Time until the next metric is due (zero when overdue), or null when nothing is scheduled.</summary>
    public TimeSpan? TimeUntilNextDue(TimeSpan now)
    {
        if (_entries.Count == 0)
        {
            return null;
        }

        var next = _entries.Values.Min(e => e.NextDue);
        return next > now ? next - now : TimeSpan.Zero;
    }

    private readonly record struct Entry(TimeSpan Interval, TimeSpan NextDue);
}
