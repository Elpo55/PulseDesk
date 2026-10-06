namespace PulseDesk.Core.Monitoring;

/// <summary>
/// Thrown by a provider when its data source works but has no valid value for this one sample (for example, a
/// performance counter that wrapped around between two collections). The monitor then skips the sample: the
/// metric is not reported as unavailable and no value is invented; only repeated skips count as a failure.
/// </summary>
public sealed class MetricSampleSkippedException : Exception
{
    public MetricSampleSkippedException()
        : this("No valid value for this sample.")
    {
    }

    public MetricSampleSkippedException(string message)
        : base(message)
    {
    }

    public MetricSampleSkippedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
