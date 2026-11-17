namespace PerfTuner.Core.Insights;

public readonly record struct LeakFinding(int Pid, long CreateTime, string Name, double PrivateMb, double GrowthMbPerHour, double ObservedMinutes, double RSquared);

/// <summary>
/// Detects processes whose private memory grows steadily — the signature of a leak — as
/// opposed to normal "grow then plateau" or sawtooth (GC) patterns.
/// <para>
/// Per process it keeps a bounded ring of (time, private MB) samples (default: one per 30 s,
/// 120 samples = 1 hour) and fits a least-squares line. A process is a suspect when ALL hold:
/// <list type="bullet">
/// <item>observed for ≥ 15 minutes;</item>
/// <item>slope ≥ 30 MB/hour AND total growth over the window ≥ max(50 MB, 15 % of start);</item>
/// <item>R² ≥ 0.85 (the growth is linear, not noise);</item>
/// <item>≥ 75 % of consecutive steps are non-decreasing (not a sawtooth that happens to trend up);</item>
/// <item>the most recent third of the window is still growing (it has not plateaued).</item>
/// </list>
/// Memory is bounded: at most <c>maxTracked</c> processes (only those ≥ 32 MB), fixed-size rings.
/// </para>
/// </summary>
public sealed class LeakDetector
{
    public const double MinMinutes = 15;
    public const double MinSlopeMbPerHour = 30;
    public const double MinRSquared = 0.85;
    public const double MinTrackedMb = 32;

    private readonly int _capacity;
    private readonly int _maxTracked;
    private readonly Dictionary<(int Pid, long Create), Series> _series = [];

    public LeakDetector(int capacity = 120, int maxTracked = 400)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTracked, 1);
        _capacity = capacity;
        _maxTracked = maxTracked;
    }

    public int TrackedCount => _series.Count;

    /// <summary>Longest observation window currently held (minutes).</summary>
    public double ObservationMinutes { get; private set; }

    /// <summary>Records one sample per live process. Processes not in the batch are forgotten.</summary>
    public void Observe(double timeMinutes, IReadOnlyList<(int Pid, long CreateTime, string Name, double PrivateMb)> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var alive = new HashSet<(int, long)>(processes.Count);
        foreach (var p in processes)
        {
            var key = (p.Pid, p.CreateTime);
            alive.Add(key);
            if (!_series.TryGetValue(key, out var s))
            {
                if (p.PrivateMb < MinTrackedMb || _series.Count >= _maxTracked)
                {
                    continue;
                }
                s = new Series(_capacity, p.Name);
                _series[key] = s;
            }
            s.Add(timeMinutes, p.PrivateMb);
        }

        foreach (var key in _series.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _series.Remove(key);
        }
        ObservationMinutes = _series.Count == 0 ? 0 : _series.Values.Max(s => s.SpanMinutes);
    }

    public List<LeakFinding> Suspects()
    {
        var result = new List<LeakFinding>();
        foreach (var ((pid, create), s) in _series)
        {
            if (s.Evaluate() is { } f)
            {
                result.Add(f with { Pid = pid, CreateTime = create });
            }
        }
        result.Sort((a, b) => b.GrowthMbPerHour.CompareTo(a.GrowthMbPerHour));
        return result;
    }

    private sealed class Series(int capacity, string name)
    {
        private readonly double[] _t = new double[capacity];
        private readonly double[] _v = new double[capacity];
        private int _head;
        private int _count;

        public double SpanMinutes => _count < 2 ? 0 : _t[Index(_count - 1)] - _t[Index(0)];

        public void Add(double t, double v)
        {
            _t[_head] = t;
            _v[_head] = v;
            _head = (_head + 1) % capacity;
            if (_count < capacity) { _count++; }
        }

        private int Index(int i) => (_head - _count + i + capacity) % capacity;

        public LeakFinding? Evaluate()
        {
            if (_count < 8 || SpanMinutes < MinMinutes)
            {
                return null;
            }

            double meanT = 0, meanV = 0;
            for (var i = 0; i < _count; i++) { meanT += _t[Index(i)]; meanV += _v[Index(i)]; }
            meanT /= _count;
            meanV /= _count;

            double sxy = 0, sxx = 0, syy = 0;
            var nonDecreasing = 0;
            for (var i = 0; i < _count; i++)
            {
                var dt = _t[Index(i)] - meanT;
                var dv = _v[Index(i)] - meanV;
                sxy += dt * dv;
                sxx += dt * dt;
                syy += dv * dv;
                if (i > 0 && _v[Index(i)] >= _v[Index(i - 1)] - 0.5) { nonDecreasing++; }
            }
            if (sxx <= 0 || syy <= 0)
            {
                return null;
            }

            var slopePerMinute = sxy / sxx;
            var r2 = (sxy * sxy) / (sxx * syy);
            var first = _v[Index(0)];
            var last = _v[Index(_count - 1)];
            var growth = last - first;
            var monotonicShare = (double)nonDecreasing / (_count - 1);

            // Recent third must still be rising (not a plateau after warm-up).
            var third = Math.Max(2, _count / 3);
            var recentGrowth = last - _v[Index(_count - third)];
            var recentSpan = _t[Index(_count - 1)] - _t[Index(_count - third)];
            var recentSlope = recentSpan > 0 ? recentGrowth / recentSpan : 0;

            var slopePerHour = slopePerMinute * 60;
            if (slopePerHour < MinSlopeMbPerHour
                || growth < Math.Max(50, first * 0.15)
                || r2 < MinRSquared
                || monotonicShare < 0.75
                || recentSlope * 60 < MinSlopeMbPerHour / 2)
            {
                return null;
            }

            return new LeakFinding(0, 0, name, Math.Round(last, 1), Math.Round(slopePerHour, 1), Math.Round(SpanMinutes, 1), Math.Round(r2, 3));
        }
    }
}

/// <summary>Rolling share of time the CPU spent power/thermal limited (bounded ring of flags).</summary>
public sealed class LimitTracker(int window = 600)
{
    private readonly bool[] _limited = new bool[window];
    private int _next;
    private int _count;
    private int _limitedCount;

    public void Observe(double limitPercent)
    {
        var limited = limitPercent is > 0 and < 99.5;
        if (_count == _limited.Length && _limited[_next]) { _limitedCount--; }
        _limited[_next] = limited;
        if (limited) { _limitedCount++; }
        _next = (_next + 1) % _limited.Length;
        if (_count < _limited.Length) { _count++; }
    }

    public double LimitedSharePercent => _count == 0 ? 0 : 100.0 * _limitedCount / _count;
}
