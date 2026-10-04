namespace PulseDesk.Core.Monitoring;

/// <summary>
/// Detects a value staying above a threshold for a minimum duration, e.g. "CPU above 90% for 30 seconds".
/// </summary>
/// <remarks>
/// Designed to avoid false positives:
/// <list type="bullet">
/// <item>a short spike never triggers: the value must stay at or above the threshold for the whole duration;</item>
/// <item>any sample below the threshold restarts the wait;</item>
/// <item>once active, the condition clears only when the value drops below <c>threshold - hysteresis</c>,
/// so a value hovering around the threshold does not flap;</item>
/// <item>a gap in the samples longer than the duration (sleep, paused monitoring) restarts the wait,
/// because nothing is known about that period.</item>
/// </list>
/// </remarks>
public sealed class SustainedThresholdDetector
{
    private DateTimeOffset? _aboveSince;
    private DateTimeOffset? _lastSample;

    /// <param name="threshold">Value at or above which the condition starts.</param>
    /// <param name="duration">How long the value must stay at or above the threshold.</param>
    /// <param name="hysteresis">Margin below the threshold required to clear an active condition.</param>
    public SustainedThresholdDetector(double threshold, TimeSpan duration, double hysteresis = 5)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks, nameof(duration));
        ArgumentOutOfRangeException.ThrowIfNegative(hysteresis);
        Threshold = threshold;
        Duration = duration;
        Hysteresis = hysteresis;
    }

    public double Threshold { get; }

    public TimeSpan Duration { get; }

    public double Hysteresis { get; }

    /// <summary>True while the condition holds.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Since when the value has been at or above the threshold (null when below).</summary>
    public DateTimeOffset? AboveSince => _aboveSince;

    /// <summary>Feeds a sample. Samples must arrive in chronological order; older ones are ignored.</summary>
    /// <returns>The value of <see cref="IsActive"/> after the update.</returns>
    public bool Update(double value, DateTimeOffset timestamp)
    {
        if (_lastSample is { } last)
        {
            if (timestamp < last)
            {
                return IsActive;
            }

            if (timestamp - last > Duration && Duration > TimeSpan.Zero)
            {
                Reset();
            }
        }

        _lastSample = timestamp;

        if (IsActive)
        {
            if (value < Threshold - Hysteresis)
            {
                IsActive = false;
                _aboveSince = null;
            }

            return IsActive;
        }

        if (value >= Threshold)
        {
            _aboveSince ??= timestamp;
            IsActive = timestamp - _aboveSince.Value >= Duration;
        }
        else
        {
            _aboveSince = null;
        }

        return IsActive;
    }

    /// <summary>Forgets all previous samples.</summary>
    public void Reset()
    {
        IsActive = false;
        _aboveSince = null;
        _lastSample = null;
    }
}
