using System.Collections;

namespace PulseDesk.Core.Metrics;

/// <summary>
/// Fixed-capacity circular buffer. When full, adding an item overwrites the oldest one,
/// so memory use stays constant no matter how long PulseDesk runs.
/// </summary>
/// <remarks>Not thread-safe; wrap it with a lock when shared (see <see cref="MetricSeries"/>).</remarks>
/// <typeparam name="T">Item type.</typeparam>
public sealed class RingBuffer<T> : IReadOnlyList<T>
{
    private readonly T[] _items;
    private int _start;
    private int _count;
    private int _version;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _items = new T[capacity];
    }

    /// <summary>Maximum number of items kept.</summary>
    public int Capacity => _items.Length;

    /// <summary>Number of items currently stored.</summary>
    public int Count => _count;

    /// <summary>True when the next <see cref="Add"/> will overwrite the oldest item.</summary>
    public bool IsFull => _count == _items.Length;

    /// <summary>Gets an item by age: index 0 is the oldest, <c>Count - 1</c> the newest.</summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _items[PhysicalIndex(index)];
        }
    }

    /// <summary>Appends an item, overwriting the oldest one when the buffer is full.</summary>
    public void Add(T item)
    {
        if (_count < _items.Length)
        {
            _items[PhysicalIndex(_count)] = item;
            _count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }

        _version++;
    }

    /// <summary>Gets the most recently added item.</summary>
    public bool TryGetNewest(out T item)
    {
        if (_count == 0)
        {
            item = default!;
            return false;
        }

        item = _items[PhysicalIndex(_count - 1)];
        return true;
    }

    /// <summary>Removes all items.</summary>
    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        _count = 0;
        _version++;
    }

    /// <summary>Copies items from oldest to newest, starting at <paramref name="startIndex"/>.</summary>
    /// <returns>The number of items copied.</returns>
    public int CopyTo(Span<T> destination, int startIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        var length = Math.Min(Math.Max(_count - startIndex, 0), destination.Length);
        if (length == 0)
        {
            return 0;
        }

        var first = PhysicalIndex(startIndex);
        var firstPart = Math.Min(length, _items.Length - first);
        _items.AsSpan(first, firstPart).CopyTo(destination);
        _items.AsSpan(0, length - firstPart).CopyTo(destination[firstPart..]);
        return length;
    }

    /// <summary>Returns the items from oldest to newest.</summary>
    public T[] ToArray()
    {
        var result = new T[_count];
        CopyTo(result);
        return result;
    }

    public IEnumerator<T> GetEnumerator()
    {
        var version = _version;
        for (var i = 0; i < _count; i++)
        {
            if (version != _version)
            {
                throw new InvalidOperationException("The buffer was modified during enumeration.");
            }

            yield return _items[PhysicalIndex(i)];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private int PhysicalIndex(int logicalIndex) => (_start + logicalIndex) % _items.Length;
}
