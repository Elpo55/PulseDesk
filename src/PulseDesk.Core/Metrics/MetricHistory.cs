using System.Collections.Concurrent;

namespace PulseDesk.Core.Metrics;

/// <summary>
/// Short-term, in-memory history of the metrics shown in charts. Each series is a fixed-size
/// ring buffer, so the total memory used is bounded. Nothing here is written to disk.
/// </summary>
public sealed class MetricHistory
{
    private readonly ConcurrentDictionary<string, MetricSeries> _series = new(StringComparer.Ordinal);

    /// <param name="capacityPerSeries">Number of samples kept per series.</param>
    public MetricHistory(int capacityPerSeries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityPerSeries);
        CapacityPerSeries = capacityPerSeries;
    }

    /// <summary>Number of samples kept per series.</summary>
    public int CapacityPerSeries { get; }

    /// <summary>Keys of the series recorded so far.</summary>
    public IReadOnlyCollection<string> Keys => _series.Keys.ToArray();

    /// <summary>Appends a sample to a series, creating the series on first use.</summary>
    public void Record(string key, DateTimeOffset timestamp, double value) =>
        _series.GetOrAdd(key, static (k, capacity) => new MetricSeries(k, capacity), CapacityPerSeries)
            .Add(new MetricSample(timestamp, value));

    /// <summary>Returns the series with the given key, or null when nothing was recorded for it.</summary>
    public MetricSeries? Get(string key) => _series.GetValueOrDefault(key);

    /// <summary>Returns samples of a series taken at or after <paramref name="since"/> (empty when unknown).</summary>
    public MetricSample[] GetSamples(string key, DateTimeOffset since) =>
        Get(key)?.GetSamples(since) ?? [];

    /// <summary>Removes all series.</summary>
    public void Clear() => _series.Clear();
}

/// <summary>Well-known series keys.</summary>
public static class SeriesKeys
{
    /// <summary>Overall CPU utilization, percent.</summary>
    public const string Cpu = "cpu";

    /// <summary>Physical memory in use, percent.</summary>
    public const string Memory = "memory";

    /// <summary>Total network receive rate, bits per second.</summary>
    public const string NetworkReceive = "net.rx";

    /// <summary>Total network send rate, bits per second.</summary>
    public const string NetworkSend = "net.tx";

    /// <summary>Utilization of one GPU, percent.</summary>
    public static string Gpu(string adapterId) => $"gpu:{adapterId}";

    /// <summary>Active time of one volume, percent.</summary>
    public static string DiskActive(string drive) => $"disk:{drive}";
}
