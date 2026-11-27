namespace nSpectrum.Core.Concurrency;

/// <summary>
/// Lock-free single-writer / single-reader triple buffer. The writer fills <see cref="WriteBuffer"/>
/// and calls <see cref="Publish"/>; the reader calls <see cref="TryAcquire"/> and gets the newest
/// published buffer. Neither side ever blocks or allocates; frames the reader misses are dropped.
/// </summary>
public sealed class TripleBuffer<T> where T : class
{
    private const int NewFlag = 4;
    private const int IndexMask = 3;

    private readonly T[] _items;
    private int _write;          // owned by the writer
    private int _read;           // owned by the reader
    private int _middle;         // shared: index | NewFlag

    public TripleBuffer(T a, T b, T c)
    {
        _items = [a, b, c];
        _write = 0;
        _middle = 1;
        _read = 2;
    }

    public TripleBuffer(Func<T> factory) : this(factory(), factory(), factory()) { }

    /// <summary>The buffer the writer may fill now.</summary>
    public T WriteBuffer => _items[_write];

    /// <summary>Hands the filled write buffer to the reader and takes a free one.</summary>
    public void Publish()
    {
        int old = Interlocked.Exchange(ref _middle, _write | NewFlag);
        _write = old & IndexMask;
    }

    /// <summary>The buffer most recently acquired by the reader (may be stale).</summary>
    public T ReadBuffer => _items[_read];

    /// <summary>True when the writer has published since the last <see cref="TryAcquire"/>.</summary>
    public bool HasNew => (Volatile.Read(ref _middle) & NewFlag) != 0;

    /// <summary>
    /// Takes the newest published buffer if there is one. Returns false (and the previous buffer)
    /// when nothing new was published.
    /// </summary>
    public bool TryAcquire(out T buffer)
    {
        if ((Volatile.Read(ref _middle) & NewFlag) == 0)
        {
            buffer = _items[_read];
            return false;
        }
        int old = Interlocked.Exchange(ref _middle, _read);
        _read = old & IndexMask;
        buffer = _items[_read];
        return true;
    }
}
