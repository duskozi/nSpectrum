using System.Numerics;

namespace nSpectrum.Core.Concurrency;

/// <summary>
/// Lock-free single-producer / single-consumer ring buffer of <typeparamref name="T"/>.
/// The capture thread writes, the DSP thread reads. No locks, no allocation after construction.
/// </summary>
public sealed class SpscRingBuffer<T> where T : unmanaged
{
    private readonly T[] _buffer;
    private readonly int _mask;
    private long _head; // next write position, written only by the producer
    private long _tail; // next read position, written only by the consumer
    private long _overflowed;

    /// <param name="minimumCapacity">Rounded up to a power of two.</param>
    public SpscRingBuffer(int minimumCapacity)
    {
        if (minimumCapacity < 2) throw new ArgumentOutOfRangeException(nameof(minimumCapacity));
        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)minimumCapacity);
        _buffer = new T[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    /// <summary>Items ready to read.</summary>
    public int Count => (int)(Volatile.Read(ref _head) - Volatile.Read(ref _tail));

    /// <summary>Free space, as seen by the producer.</summary>
    public int FreeCount => _buffer.Length - Count;

    /// <summary>Records items the producer chose to drop (e.g. a partial multichannel frame).</summary>
    public void ReportDropped(long count) => Interlocked.Add(ref _overflowed, count);

    /// <summary>Total items the producer reported as dropped via <see cref="ReportDropped"/>.</summary>
    public long Overflowed => Interlocked.Read(ref _overflowed);

    /// <summary>Producer side. Writes as much as fits and returns how many items were written.</summary>
    public int Write(ReadOnlySpan<T> items)
    {
        long head = _head;
        long tail = Volatile.Read(ref _tail);
        int free = _buffer.Length - (int)(head - tail);
        int n = Math.Min(free, items.Length);
        if (n == 0) return 0;

        int start = (int)(head & _mask);
        int first = Math.Min(n, _buffer.Length - start);
        items[..first].CopyTo(_buffer.AsSpan(start));
        if (first < n)
            items[first..n].CopyTo(_buffer.AsSpan(0));
        Volatile.Write(ref _head, head + n);
        return n;
    }

    /// <summary>Consumer side. Reads up to <c>destination.Length</c> items and returns how many were read.</summary>
    public int Read(Span<T> destination)
    {
        long tail = _tail;
        long head = Volatile.Read(ref _head);
        int n = Math.Min((int)(head - tail), destination.Length);
        if (n == 0) return 0;

        int start = (int)(tail & _mask);
        int first = Math.Min(n, _buffer.Length - start);
        _buffer.AsSpan(start, first).CopyTo(destination);
        if (first < n)
            _buffer.AsSpan(0, n - first).CopyTo(destination[first..]);
        Volatile.Write(ref _tail, tail + n);
        return n;
    }

    /// <summary>Consumer side. Discards everything currently buffered.</summary>
    public void Clear() => Volatile.Write(ref _tail, Volatile.Read(ref _head));
}
