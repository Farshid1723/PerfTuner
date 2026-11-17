namespace PerfTuner.Core.Buffers;

/// <summary>
/// Fixed-capacity, overwrite-oldest ring of value types. Storage is allocated once
/// in the constructor; <see cref="Add"/> and <see cref="CopyLatest"/> never allocate.
/// Thread-safe for one writer and any number of readers (a short lock guards the
/// head/count pair; the critical sections are a few nanoseconds).
/// </summary>
public sealed class RingBuffer<T> where T : struct
{
    private readonly T[] _items;
    private readonly Lock _gate = new();
    private int _head;   // next write position
    private int _count;
    private long _totalWritten;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get { lock (_gate) { return _count; } }
    }

    /// <summary>Total items ever written (including overwritten ones).</summary>
    public long TotalWritten
    {
        get { lock (_gate) { return _totalWritten; } }
    }

    public void Add(in T item)
    {
        lock (_gate)
        {
            _items[_head] = item;
            _head = (_head + 1) % _items.Length;
            if (_count < _items.Length)
            {
                _count++;
            }
            _totalWritten++;
        }
    }

    public bool TryGetLatest(out T item)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                item = default;
                return false;
            }
            item = _items[(_head - 1 + _items.Length) % _items.Length];
            return true;
        }
    }

    /// <summary>
    /// Copies up to <c>destination.Length</c> most recent items, oldest first.
    /// Returns the number of items copied.
    /// </summary>
    public int CopyLatest(Span<T> destination)
    {
        lock (_gate)
        {
            var n = Math.Min(destination.Length, _count);
            var start = (_head - n + _items.Length) % _items.Length;
            var firstRun = Math.Min(n, _items.Length - start);
            _items.AsSpan(start, firstRun).CopyTo(destination);
            if (firstRun < n)
            {
                _items.AsSpan(0, n - firstRun).CopyTo(destination[firstRun..]);
            }
            return n;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _head = 0;
            _count = 0;
        }
    }
}
