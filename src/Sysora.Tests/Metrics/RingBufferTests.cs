using Sysora.Core.Metrics;

namespace Sysora.Tests.Metrics;

public sealed class RingBufferTests
{
    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(-3));
    }

    [Fact]
    public void Add_BelowCapacity_KeepsInsertionOrder()
    {
        var buffer = new RingBuffer<int>(4);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        Assert.Equal(3, buffer.Count);
        Assert.False(buffer.IsFull);
        Assert.Equal([1, 2, 3], buffer.ToArray());
    }

    [Fact]
    public void Add_BeyondCapacity_OverwritesOldest()
    {
        var buffer = new RingBuffer<int>(3);
        for (var i = 1; i <= 7; i++)
        {
            buffer.Add(i);
        }

        Assert.True(buffer.IsFull);
        Assert.Equal(3, buffer.Count);
        Assert.Equal([5, 6, 7], buffer.ToArray());
        Assert.Equal(5, buffer[0]);
        Assert.Equal(7, buffer[2]);
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var buffer = new RingBuffer<int>(3);
        buffer.Add(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1]);
    }

    [Fact]
    public void TryGetNewest_ReturnsLastAddedItem()
    {
        var buffer = new RingBuffer<string>(2);
        Assert.False(buffer.TryGetNewest(out _));

        buffer.Add("a");
        buffer.Add("b");
        buffer.Add("c");

        Assert.True(buffer.TryGetNewest(out var newest));
        Assert.Equal("c", newest);
    }

    [Fact]
    public void CopyTo_WithStartIndex_CopiesAcrossWrapBoundary()
    {
        var buffer = new RingBuffer<int>(4);
        for (var i = 1; i <= 6; i++)
        {
            buffer.Add(i); // physical layout wraps: [5, 6, 3, 4]
        }

        var destination = new int[3];
        var copied = buffer.CopyTo(destination, startIndex: 1);

        Assert.Equal(3, copied);
        Assert.Equal([4, 5, 6], destination);
    }

    [Fact]
    public void CopyTo_DestinationSmallerThanContent_CopiesWhatFits()
    {
        var buffer = new RingBuffer<int>(4);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        var destination = new int[2];
        Assert.Equal(2, buffer.CopyTo(destination));
        Assert.Equal([1, 2], destination);
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var buffer = new RingBuffer<int>(2);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Clear();

        Assert.Empty(buffer);
        buffer.Add(9);
        Assert.Equal([9], buffer.ToArray());
    }

    [Fact]
    public void Enumeration_ModifiedDuringIteration_Throws()
    {
        var buffer = new RingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var item in buffer)
            {
                buffer.Add(item);
            }
        });
    }

    [Fact]
    public void LongRun_MemoryStaysBounded()
    {
        var buffer = new RingBuffer<double>(100);
        for (var i = 0; i < 1_000_000; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal(100, buffer.Count);
        Assert.Equal(999_900, buffer[0]);
        Assert.Equal(999_999, buffer[99]);
    }
}
