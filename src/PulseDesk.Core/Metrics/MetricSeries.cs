namespace PulseDesk.Core.Metrics;

/// <summary>One timestamped measurement.</summary>
public readonly record struct MetricSample(DateTimeOffset Timestamp, double Value);

/// <summary>Summary statistics over a set of samples.</summary>
public readonly record struct SeriesStatistics(double Average, double Minimum, double Maximum, int Count);

/// <summary>
/// Thread-safe, fixed-size history of one metric. Samples are appended by the monitoring loop
/// and read by the UI; old samples are overwritten once the capacity is reached.
/// </summary>
public sealed class MetricSeries
{
    private readonly RingBuffer<MetricSample> _buffer;
    private readonly Lock _lock = new();

    public MetricSeries(string key, int capacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        _buffer = new RingBuffer<MetricSample>(capacity);
    }

    /// <summary>Identifier of the series (see <see cref="SeriesKeys"/>).</summary>
    public string Key { get; }

    /// <summary>Maximum number of samples kept.</summary>
    public int Capacity => _buffer.Capacity;

    /// <summary>Number of samples currently stored.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _buffer.Count;
            }
        }
    }

    /// <summary>The most recent sample, if any.</summary>
    public MetricSample? Latest
    {
        get
        {
            lock (_lock)
            {
                return _buffer.TryGetNewest(out var sample) ? sample : null;
            }
        }
    }

    /// <summary>
    /// Appends a sample. Non-finite values and samples older than the latest one are ignored,
    /// so the series always stays ordered by time.
    /// </summary>
    public void Add(MetricSample sample)
    {
        if (!double.IsFinite(sample.Value))
        {
            return;
        }

        lock (_lock)
        {
            if (_buffer.TryGetNewest(out var newest) && sample.Timestamp < newest.Timestamp)
            {
                return;
            }

            _buffer.Add(sample);
        }
    }

    /// <summary>Returns a copy of the samples taken at or after <paramref name="since"/>, oldest first.</summary>
    public MetricSample[] GetSamples(DateTimeOffset since)
    {
        lock (_lock)
        {
            var start = FindFirstIndexAtOrAfter(since);
            var result = new MetricSample[_buffer.Count - start];
            _buffer.CopyTo(result, start);
            return result;
        }
    }

    /// <summary>Computes average, minimum and maximum of samples taken at or after <paramref name="since"/>.</summary>
    public SeriesStatistics? GetStatistics(DateTimeOffset since)
    {
        lock (_lock)
        {
            var start = FindFirstIndexAtOrAfter(since);
            var count = _buffer.Count - start;
            if (count == 0)
            {
                return null;
            }

            double sum = 0, min = double.MaxValue, max = double.MinValue;
            for (var i = start; i < _buffer.Count; i++)
            {
                var value = _buffer[i].Value;
                sum += value;
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            return new SeriesStatistics(sum / count, min, max, count);
        }
    }

    /// <summary>Removes all samples.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }

    // Samples are ordered by time, so a binary search finds the window start in O(log n).
    private int FindFirstIndexAtOrAfter(DateTimeOffset since)
    {
        int low = 0, high = _buffer.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (_buffer[mid].Timestamp < since)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
