namespace PerfTuner.Core.Telemetry;

/// <summary>Per-process IO byte counts for one interval.</summary>
public struct ProcessIoCounters
{
    public int Pid;
    public long DiskBytes;
    public long NetBytes;

    public readonly long Total => DiskBytes + NetBytes;
}

/// <summary>System-wide IO byte counts for one interval.</summary>
public readonly record struct IoTotals(long DiskReadBytes, long DiskWriteBytes, long NetRxBytes, long NetTxBytes, bool TableOverflowed);

/// <summary>
/// Hot-path sink for IO events (ETW callbacks can deliver tens of thousands per second).
/// <para>
/// Design: totals are plain longs and per-process counts live in a fixed-capacity
/// open-addressing hash table (parallel arrays, linear probing, bounded probe length).
/// Nothing is allocated after construction. If the table fills, further PIDs are
/// counted only in the totals and the interval is flagged as overflowed — we drop
/// detail, never data integrity, and never grow memory.
/// </para>
/// <para>
/// One uncontended lock per event (~20 ns) is cheaper and far simpler to reason
/// about than a lock-free double-buffer here; the reader takes the lock once per
/// aggregation interval.
/// </para>
/// </summary>
public sealed class IoAccumulator
{
    private const int MaxProbe = 16;

    private readonly Lock _gate = new();
    private readonly int[] _pids;          // 0 = empty slot
    private readonly long[] _disk;
    private readonly long[] _net;
    private readonly int _mask;
    private int _used;
    private bool _overflow;

    private long _diskRead;
    private long _diskWrite;
    private long _netRx;
    private long _netTx;

    /// <param name="capacityPowerOfTwo">Hash table size; must be a power of two (e.g. 1024).</param>
    public IoAccumulator(int capacityPowerOfTwo = 1024)
    {
        if (capacityPowerOfTwo < 16 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityPowerOfTwo), "must be a power of two ≥ 16");
        }
        _pids = new int[capacityPowerOfTwo];
        _disk = new long[capacityPowerOfTwo];
        _net = new long[capacityPowerOfTwo];
        _mask = capacityPowerOfTwo - 1;
    }

    /// <summary>When false, only totals are kept (cheaper; used when degraded).</summary>
    public bool TrackPerProcess { get; set; } = true;

    public void AddDiskRead(int pid, long bytes) => Add(pid, bytes, isDisk: true, isReadOrRx: true);
    public void AddDiskWrite(int pid, long bytes) => Add(pid, bytes, isDisk: true, isReadOrRx: false);
    public void AddNetRx(int pid, long bytes) => Add(pid, bytes, isDisk: false, isReadOrRx: true);
    public void AddNetTx(int pid, long bytes) => Add(pid, bytes, isDisk: false, isReadOrRx: false);

    private void Add(int pid, long bytes, bool isDisk, bool isReadOrRx)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (isDisk)
            {
                if (isReadOrRx) { _diskRead += bytes; } else { _diskWrite += bytes; }
            }
            else
            {
                if (isReadOrRx) { _netRx += bytes; } else { _netTx += bytes; }
            }

            // PID 0 (Idle) and -1 (unknown, common for deferred disk completions) are
            // counted in totals only.
            if (!TrackPerProcess || pid <= 0)
            {
                return;
            }

            var slot = FindOrInsertSlot(pid);
            if (slot < 0)
            {
                _overflow = true;
                return;
            }
            if (isDisk) { _disk[slot] += bytes; } else { _net[slot] += bytes; }
        }
    }

    private int FindOrInsertSlot(int pid)
    {
        var h = Hash(pid) & _mask;
        for (var i = 0; i < MaxProbe; i++)
        {
            var slot = (h + i) & _mask;
            var existing = _pids[slot];
            if (existing == pid)
            {
                return slot;
            }
            if (existing == 0)
            {
                // Keep load factor ≤ 75 % so probes stay short.
                if (_used >= (_pids.Length * 3) / 4)
                {
                    return -1;
                }
                _pids[slot] = pid;
                _used++;
                return slot;
            }
        }
        return -1;
    }

    private static int Hash(int pid)
    {
        // Fibonacci hashing spreads the (multiple-of-4) Windows PIDs well.
        return (int)(((uint)pid * 2654435769u) >> 7);
    }

    /// <summary>
    /// Atomically reads and resets the interval. The top <c>top.Length</c> processes by
    /// total bytes are written to <paramref name="top"/> (descending); returns how many.
    /// Allocation-free: selection is an insertion into the caller's fixed span.
    /// </summary>
    public IoTotals SnapshotAndReset(Span<ProcessIoCounters> top, out int topCount)
    {
        lock (_gate)
        {
            topCount = 0;
            if (top.Length > 0 && _used > 0)
            {
                for (var i = 0; i < _pids.Length; i++)
                {
                    if (_pids[i] == 0)
                    {
                        continue;
                    }
                    var candidate = new ProcessIoCounters { Pid = _pids[i], DiskBytes = _disk[i], NetBytes = _net[i] };
                    InsertTopN(top, ref topCount, candidate);
                }
            }

            var totals = new IoTotals(_diskRead, _diskWrite, _netRx, _netTx, _overflow);

            _diskRead = _diskWrite = _netRx = _netTx = 0;
            _overflow = false;
            if (_used > 0)
            {
                Array.Clear(_pids);
                Array.Clear(_disk);
                Array.Clear(_net);
                _used = 0;
            }
            return totals;
        }
    }

    private static void InsertTopN(Span<ProcessIoCounters> top, ref int count, in ProcessIoCounters candidate)
    {
        var total = candidate.Total;
        if (total <= 0)
        {
            return;
        }
        if (count == top.Length && total <= top[count - 1].Total)
        {
            return;
        }

        var pos = count < top.Length ? count : top.Length - 1;
        while (pos > 0 && top[pos - 1].Total < total)
        {
            if (pos < top.Length)
            {
                top[pos] = top[pos - 1];
            }
            pos--;
        }
        top[pos] = candidate;
        if (count < top.Length)
        {
            count++;
        }
    }
}
