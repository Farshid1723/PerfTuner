using PerfTuner.Contracts;

namespace PerfTuner.Core.Visibility;

/// <summary>
/// Disk IO latency accounting (F6), fed from ETW DiskIO completions:
/// <list type="bullet">
/// <item>System histograms per 60 s window: read total, write total, queue (total − device
///   service time) and device time; the previous window is kept for display.</item>
/// <item>A 1-second system histogram, harvested every interval for the stall detector.</item>
/// <item>Per-process histograms in a fixed 128-slot table (linear probe); overflow goes to an
///   "other" slot, so memory is fixed (~30 KB) no matter how many processes do IO.</item>
/// </list>
/// All updates are allocation-free; one short lock per IO.
/// </summary>
public sealed class IoLatencyTracker
{
    public const int WindowSeconds = 60;
    private const int Slots = 128;

    private readonly Lock _gate = new();
    private Window _current = new();
    private Window _previous = new();
    private readonly LatencyHistogram _second = new();
    private long _windowStartMs;

    private sealed class Window
    {
        public readonly LatencyHistogram Read = new();
        public readonly LatencyHistogram Write = new();
        public readonly LatencyHistogram Queue = new();
        public readonly LatencyHistogram Device = new();
        public readonly LatencyHistogram All = new();
        public readonly int[] Pids = new int[Slots + 1];
        public readonly long[] Reads = new long[Slots + 1];
        public readonly long[] Writes = new long[Slots + 1];
        public readonly long[] Bytes = new long[Slots + 1];
        public readonly LatencyHistogram[] PerProcess = [.. Enumerable.Range(0, Slots + 1).Select(_ => new LatencyHistogram())];
        public bool HasDeviceTime;

        public void Clear()
        {
            Read.Clear(); Write.Clear(); Queue.Clear(); Device.Clear(); All.Clear();
            Array.Clear(Pids); Array.Clear(Reads); Array.Clear(Writes); Array.Clear(Bytes);
            foreach (var h in PerProcess) { h.Clear(); }
            HasDeviceTime = false;
        }
    }

    public void Record(int pid, bool write, int bytes, double elapsedMs, double deviceMs)
    {
        lock (_gate)
        {
            var w = _current;
            (write ? w.Write : w.Read).AddMs(elapsedMs);
            w.All.AddMs(elapsedMs);
            _second.AddMs(elapsedMs);
            if (deviceMs > 0 && deviceMs <= elapsedMs)
            {
                w.HasDeviceTime = true;
                w.Device.AddMs(deviceMs);
                w.Queue.AddMs(elapsedMs - deviceMs);
            }

            var slot = SlotFor(w, pid);
            if (write) { w.Writes[slot]++; } else { w.Reads[slot]++; }
            w.Bytes[slot] += Math.Max(0, bytes);
            w.PerProcess[slot].AddMs(elapsedMs);
        }
    }

    private static int SlotFor(Window w, int pid)
    {
        if (pid <= 0) { return Slots; } // "other / unattributed"
        var start = (int)((uint)pid * 2654435769u >> 25) % Slots;
        for (var i = 0; i < 16; i++)
        {
            var s = (start + i) % Slots;
            if (w.Pids[s] == pid) { return s; }
            if (w.Pids[s] == 0) { w.Pids[s] = pid; return s; }
        }
        return Slots;
    }

    /// <summary>Per-interval: returns the last second's p99 (ms) and rolls the 60 s window when due.</summary>
    public (double P99Ms, long Count) HarvestSecond(long nowMs)
    {
        lock (_gate)
        {
            var result = (_second.PercentileMs(0.99), _second.Count);
            _second.Clear();
            if (_windowStartMs == 0) { _windowStartMs = nowMs; }
            if (nowMs - _windowStartMs >= WindowSeconds * 1000L)
            {
                (_previous, _current) = (_current, _previous);
                _current.Clear();
                _windowStartMs = nowMs;
            }
            return result;
        }
    }

    public readonly record struct ProcessRow(int Pid, long Reads, long Writes, long Bytes, LatencyPercentiles Latency);

    /// <summary>Snapshot of the most complete window (previous full window, or current if it has more data).</summary>
    public (LatencyPercentiles Read, LatencyPercentiles Write, LatencyPercentiles Queue, LatencyPercentiles Device, long[] Histogram, bool HasDeviceTime, List<ProcessRow> Processes) Snapshot()
    {
        lock (_gate)
        {
            var w = _previous.All.Count >= _current.All.Count ? _previous : _current;
            var histogram = new long[LatencyHistogram.Buckets];
            w.All.CopyCounts(histogram);
            var rows = new List<ProcessRow>();
            for (var s = 0; s <= Slots; s++)
            {
                if (w.PerProcess[s].Count == 0) { continue; }
                rows.Add(new ProcessRow(s == Slots ? 0 : w.Pids[s], w.Reads[s], w.Writes[s], w.Bytes[s], w.PerProcess[s].ToPercentiles()));
            }
            rows.Sort((a, b) => b.Latency.P99Ms.CompareTo(a.Latency.P99Ms));
            return (w.Read.ToPercentiles(), w.Write.ToPercentiles(), w.Queue.ToPercentiles(), w.Device.ToPercentiles(), histogram, w.HasDeviceTime, rows);
        }
    }

    /// <summary>Top processes by disk throughput (MB/s) in the current window (for stall context).</summary>
    public List<(int Pid, double MegabytesPerSec)> TopByThroughput(int n, long nowMs)
    {
        lock (_gate)
        {
            var seconds = Math.Max(1, (nowMs - _windowStartMs) / 1000.0);
            var list = new List<(int, double)>();
            for (var s = 0; s < Slots; s++)
            {
                if (_current.Pids[s] > 0 && _current.Bytes[s] > 0) { list.Add((_current.Pids[s], _current.Bytes[s] / 1048576.0 / seconds)); }
            }
            return [.. list.OrderByDescending(x => x.Item2).Take(n)];
        }
    }
}
