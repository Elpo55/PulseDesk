using System.Runtime.InteropServices;

namespace Sysora.Infrastructure.Windows;

/// <summary>Receives one instance of a wildcard counter without allocating its name.</summary>
internal delegate void PdhInstanceVisitor(ReadOnlySpan<char> instance, double value);

/// <summary>
/// A set of performance counters sampled together. Rate counters (percentages, bytes/sec) need two
/// collections; their value covers the time between the last two calls to <see cref="Collect"/>.
/// </summary>
/// <remarks>Not thread-safe: each provider owns its query and calls it from the monitoring loop.</remarks>
internal sealed class PdhQuery : IDisposable
{
    private readonly List<PdhCounter> _counters = [];
    private nint _handle;

    public PdhQuery()
    {
        var status = Pdh.PdhOpenQuery(null, 0, out _handle);
        if (status != Pdh.Success)
        {
            throw new PdhException("PdhOpenQuery", status);
        }
    }

    /// <summary>Number of completed collections.</summary>
    public int CollectCount { get; private set; }

    /// <summary>Adds a counter by its English path, e.g. <c>\Processor Information(_Total)\% Processor Utility</c>.</summary>
    /// <exception cref="PdhException">The counter does not exist on this system.</exception>
    public PdhCounter AddCounter(string englishPath)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        var status = Pdh.PdhAddEnglishCounter(_handle, englishPath, 0, out var counter);
        if (status != Pdh.Success)
        {
            throw new PdhException($"Adding counter '{englishPath}'", status);
        }

        var result = new PdhCounter(counter, englishPath);
        _counters.Add(result);
        return result;
    }

    /// <summary>Adds a counter, or returns null when it does not exist on this system.</summary>
    public PdhCounter? TryAddCounter(string englishPath)
    {
        try
        {
            return AddCounter(englishPath);
        }
        catch (PdhException)
        {
            return null;
        }
    }

    /// <summary>Samples all counters of the query.</summary>
    public void Collect()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        var status = Pdh.PdhCollectQueryData(_handle);
        if (status is not Pdh.Success and not Pdh.NoData)
        {
            throw new PdhException("PdhCollectQueryData", status);
        }

        CollectCount++;
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        foreach (var counter in _counters)
        {
            counter.ReleaseBuffer();
        }

        _ = Pdh.PdhCloseQuery(_handle);
        _handle = 0;
    }
}

/// <summary>One counter of a <see cref="PdhQuery"/>.</summary>
internal sealed unsafe class PdhCounter
{
    private readonly nint _handle;
    private void* _buffer;
    private uint _bufferSize;

    internal PdhCounter(nint handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    public string Path { get; }

    /// <summary>Current value of a single-instance counter, or null when no valid value is available yet.</summary>
    public double? GetValue()
    {
        var status = Pdh.PdhGetFormattedCounterValue(_handle, Pdh.FormatDouble | Pdh.FormatNoCap100, out _, out var value);
        return status == Pdh.Success && Pdh.IsValid(value.CStatus) && double.IsFinite(value.DoubleValue)
            ? value.DoubleValue
            : null;
    }

    /// <summary>
    /// Visits every instance of a wildcard counter (e.g. <c>\GPU Engine(*)\Utilization Percentage</c>).
    /// Instances without valid data are skipped.
    /// </summary>
    /// <returns>False when the counter has no data yet.</returns>
    public bool VisitInstances(PdhInstanceVisitor visitor)
    {
        // Instances can appear between the size query and the read; retry a few times.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var size = _bufferSize;
            var status = Pdh.PdhGetFormattedCounterArray(_handle, Pdh.FormatDouble | Pdh.FormatNoCap100, ref size, out var count, _buffer);
            if (status == Pdh.MoreData)
            {
                Grow(size);
                continue;
            }

            if (status != Pdh.Success)
            {
                return false;
            }

            var items = (Pdh.FormattedValueItem*)_buffer;
            for (var i = 0; i < count; i++)
            {
                var item = items[i];
                if (item.Name is null || !Pdh.IsValid(item.Value.CStatus) || !double.IsFinite(item.Value.DoubleValue))
                {
                    continue;
                }

                visitor(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(item.Name), item.Value.DoubleValue);
            }

            return true;
        }

        return false;
    }

    internal void ReleaseBuffer()
    {
        NativeMemory.Free(_buffer);
        _buffer = null;
        _bufferSize = 0;
    }

    private void Grow(uint required)
    {
        // Leave headroom so instances that appear later do not force a reallocation every time.
        var size = Math.Max(required + (required / 4), 4096u);
        NativeMemory.Free(_buffer);
        _buffer = NativeMemory.Alloc(size);
        _bufferSize = size;
    }
}
