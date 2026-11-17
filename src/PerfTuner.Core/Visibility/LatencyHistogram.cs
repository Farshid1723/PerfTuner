using PerfTuner.Contracts;

namespace PerfTuner.Core.Visibility;

/// <summary>
/// Fixed log2 latency histogram: bucket i counts samples in [2^i, 2^(i+1)) microseconds,
/// 28 buckets = 1 µs … 134 s. Adding a sample is two instructions and never allocates;
/// percentiles are resolved to the bucket's geometric midpoint (≤ ±41 % error, standard for
/// log histograms) and the exact maximum is tracked separately.
/// </summary>
public sealed class LatencyHistogram
{
    public const int Buckets = ProtocolConstants.LatencyHistogramBuckets;
    private readonly long[] _counts = new long[Buckets];
    private long _total;
    private double _maxUs;

    public long Count => _total;
    public double MaxMs => _maxUs / 1000.0;

    public void AddMs(double milliseconds) => AddUs(milliseconds * 1000.0);

    public void AddUs(double microseconds)
    {
        if (!(microseconds >= 0) || double.IsInfinity(microseconds))
        {
            return;
        }
        var bucket = microseconds < 1 ? 0 : Math.Min(Buckets - 1, (int)Math.Log2(microseconds));
        _counts[bucket]++;
        _total++;
        if (microseconds > _maxUs) { _maxUs = microseconds; }
    }

    public double PercentileMs(double p)
    {
        if (_total == 0) { return 0; }
        var target = (long)Math.Ceiling(p * _total);
        long cumulative = 0;
        for (var i = 0; i < Buckets; i++)
        {
            cumulative += _counts[i];
            if (cumulative >= target)
            {
                var midpointUs = Math.Pow(2, i) * Math.Sqrt(2);
                return Math.Min(midpointUs, _maxUs) / 1000.0;
            }
        }
        return MaxMs;
    }

    public void CopyCounts(Span<long> destination) => _counts.AsSpan().CopyTo(destination);

    public void MergeFrom(LatencyHistogram other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (var i = 0; i < Buckets; i++) { _counts[i] += other._counts[i]; }
        _total += other._total;
        _maxUs = Math.Max(_maxUs, other._maxUs);
    }

    public void Clear()
    {
        Array.Clear(_counts);
        _total = 0;
        _maxUs = 0;
    }

    public LatencyPercentiles ToPercentiles() => new()
    {
        Count = _total,
        P50Ms = Math.Round(PercentileMs(0.50), 3),
        P90Ms = Math.Round(PercentileMs(0.90), 3),
        P99Ms = Math.Round(PercentileMs(0.99), 3),
        MaxMs = Math.Round(MaxMs, 3),
    };
}

public readonly record struct BurstRecord(long TimeUnixMs, bool Receive, double Mbps, double BaselineMbps, int DurationMs, int Pid);

/// <summary>
/// Network microburst detector (F7). Bytes are binned by the ETW event timestamp into 100 ms
/// bins (ring of 600 = 60 s). A bin is a burst when it carries ≥ 10 Mbit/s AND ≥ 5× the median
/// of the preceding 5 s. Adjacent burst bins merge into one burst. Each bin remembers the top
/// 4 contributing PIDs (fixed slots) for attribution. Only completed bins at least 1.5 s old are
/// evaluated, because ETW delivers events up to ~1 s late.
/// </summary>
public sealed class BurstDetector
{
    private const int Bins = 600;
    private const int BinMs = 100;
    private const int Slots = 4;
    private const int BaselineBins = 50;
    private const long MinBurstBytes = 125_000; // 10 Mbit/s over 100 ms

    private readonly Lock _gate = new();
    private readonly long[] _rx = new long[Bins];
    private readonly long[] _tx = new long[Bins];
    private readonly int[] _pid = new int[Bins * Slots];
    private readonly long[] _pidBytes = new long[Bins * Slots];
    private readonly long[] _scratch = new long[BaselineBins];
    private readonly Queue<BurstRecord> _bursts = new();
    private long _currentBin = -1;
    private long _evaluatedBin = -1;
    private long _firstBin = -1;
    private (long StartBin, double PeakMbps, double Baseline, int Pid, int Bins)? _openRx;
    private (long StartBin, double PeakMbps, double Baseline, int Pid, int Bins)? _openTx;

    public void Add(long unixMs, long bytes, bool receive, int pid)
    {
        if (bytes <= 0) { return; }
        var bin = unixMs / BinMs;
        lock (_gate)
        {
            if (bin > _currentBin)
            {
                var advance = Math.Min(Bins, bin - _currentBin);
                for (long b = bin - advance + 1; b <= bin; b++) { ClearBin(b); }
                _currentBin = bin;
                if (_evaluatedBin < 0) { _evaluatedBin = bin - 1; _firstBin = bin; }
            }
            else if (bin <= _currentBin - Bins)
            {
                return; // too old
            }

            var i = (int)(bin % Bins);
            if (receive) { _rx[i] += bytes; } else { _tx[i] += bytes; }
            if (pid > 0)
            {
                var start = i * Slots;
                for (var s = 0; s < Slots; s++)
                {
                    if (_pid[start + s] == pid || _pid[start + s] == 0)
                    {
                        _pid[start + s] = pid;
                        _pidBytes[start + s] += bytes;
                        break;
                    }
                }
            }
        }
    }

    private void ClearBin(long bin)
    {
        var i = (int)(bin % Bins);
        _rx[i] = 0;
        _tx[i] = 0;
        Array.Clear(_pid, i * Slots, Slots);
        Array.Clear(_pidBytes, i * Slots, Slots);
    }

    /// <summary>Evaluates newly completed bins; returns bursts that just closed.</summary>
    public List<BurstRecord> Evaluate(long nowUnixMs)
    {
        var closed = new List<BurstRecord>();
        lock (_gate)
        {
            if (_currentBin < 0) { return closed; }
            var lastComplete = Math.Min(_currentBin - 1, (nowUnixMs - 1500) / BinMs);
            // Never judge a bin before a full 5 s of real baseline exists (no start-up false positives).
            var firstJudgeable = Math.Max(_firstBin + BaselineBins, _currentBin - Bins + BaselineBins + 1);
            for (var b = Math.Max(_evaluatedBin + 1, firstJudgeable); b <= lastComplete; b++)
            {
                EvaluateBin(b, true, closed);
                EvaluateBin(b, false, closed);
                _evaluatedBin = b;
            }
            foreach (var c in closed)
            {
                _bursts.Enqueue(c);
                while (_bursts.Count > 100) { _bursts.Dequeue(); }
            }
        }
        return closed;
    }

    private void EvaluateBin(long bin, bool rx, List<BurstRecord> closed)
    {
        var series = rx ? _rx : _tx;
        var bytes = series[(int)(bin % Bins)];
        for (var k = 0; k < BaselineBins; k++)
        {
            _scratch[k] = series[(int)((bin - BaselineBins + k) % Bins)];
        }
        Array.Sort(_scratch);
        var medianMbps = ToMbps(_scratch[BaselineBins / 2]);
        var mbps = ToMbps(bytes);
        var isBurst = bytes >= MinBurstBytes && mbps >= 5 * Math.Max(medianMbps, 1.0);

        ref var slot = ref rx ? ref _openRx : ref _openTx;
        if (slot is { } open)
        {
            if (isBurst)
            {
                slot = open with { PeakMbps = Math.Max(open.PeakMbps, mbps), Bins = open.Bins + 1 };
                return;
            }
            closed.Add(new BurstRecord(open.StartBin * BinMs, rx, Math.Round(open.PeakMbps, 1), Math.Round(open.Baseline, 1), open.Bins * BinMs, open.Pid));
            slot = null;
        }
        if (isBurst)
        {
            slot = (bin, mbps, medianMbps, TopPid((int)(bin % Bins)), 1);
        }
    }

    private int TopPid(int binIndex)
    {
        int best = 0;
        long bestBytes = 0;
        for (var s = 0; s < Slots; s++)
        {
            if (_pidBytes[(binIndex * Slots) + s] > bestBytes)
            {
                bestBytes = _pidBytes[(binIndex * Slots) + s];
                best = _pid[(binIndex * Slots) + s];
            }
        }
        return best;
    }

    private static double ToMbps(long bytesPerBin) => bytesPerBin * 8.0 * (1000.0 / BinMs) / 1_000_000.0;

    public BurstRecord[] RecentBursts()
    {
        lock (_gate) { return [.. _bursts.Reverse()]; }
    }

    /// <summary>Last 60 s (oldest first) in Mbit/s per 100 ms bin.</summary>
    public (double[] Rx, double[] Tx, double PeakRx, double PeakTx) Series()
    {
        lock (_gate)
        {
            var rx = new double[Bins];
            var tx = new double[Bins];
            if (_currentBin < 0) { return (rx, tx, 0, 0); }
            for (var k = 0; k < Bins; k++)
            {
                var b = _currentBin - Bins + 1 + k;
                if (b < 0) { continue; }
                rx[k] = Math.Round(ToMbps(_rx[(int)(b % Bins)]), 2);
                tx[k] = Math.Round(ToMbps(_tx[(int)(b % Bins)]), 2);
            }
            return (rx, tx, rx.Max(), tx.Max());
        }
    }
}
