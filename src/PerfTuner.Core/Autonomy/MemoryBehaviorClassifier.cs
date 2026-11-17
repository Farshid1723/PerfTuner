using PerfTuner.Contracts;

namespace PerfTuner.Core.Autonomy;

public readonly record struct MemoryStats(
    double AvailableMb,
    double TotalMb,
    double CommitPercent,
    double CommitGrowthMbPerMin,
    double HardFaultsPerSec,
    double SoftFaultsPerSec);

public enum MemoryActionKind
{
    None,
    /// <summary>Lower memory priority of the largest background apps (pages them out first).</summary>
    DeprioritizeBackground,
    /// <summary>DeprioritizeBackground + one bounded background trim.</summary>
    DeprioritizeAndTrim,
}

public readonly record struct MemoryVerdict(MemoryBehavior Behavior, string Explanation, MemoryActionKind Action, string Decision);

/// <summary>
/// Heuristic memory governor (F3): classifies BEHAVIOUR, not just usage. Two systems at 85 %
/// RAM can need opposite treatment — one is stable (do nothing), one is thrashing (act).
/// <list type="table">
/// <item><term>Thrashing</term><description>≥ 500 hard faults/s AND &lt; 8 % available: pages are evicted and immediately read back.</description></item>
/// <item><term>Pressure</term><description>&lt; 12 % available, OR commit ≥ 90 %, OR ≥ 150 hard faults/s with &lt; 25 % available.</description></item>
/// <item><term>AllocationBurst</term><description>commit growing ≥ 500 MB/min: a transient spike — observe.</description></item>
/// <item><term>WorkingSetChurn</term><description>≥ 20 000 soft faults/s with little hard faulting: pages move between
///   working sets and the standby list. Trimming would make it WORSE — explicitly no action.</description></item>
/// <item><term>Stable</term><description>none of the above.</description></item>
/// </list>
/// Decisions are proposals; the service applies them only in Balanced/Advanced profiles,
/// through contracts, with rate limits.
/// </summary>
public static class MemoryBehaviorClassifier
{
    public static MemoryVerdict Classify(in MemoryStats s)
    {
        var availablePct = s.TotalMb > 0 ? 100 * s.AvailableMb / s.TotalMb : 100;

        if (s.HardFaultsPerSec >= 500 && availablePct < 8)
        {
            return new(MemoryBehavior.Thrashing,
                $"{s.HardFaultsPerSec:F0} hard faults/s with only {availablePct:F0}% RAM free: pages are evicted and read straight back from disk.",
                MemoryActionKind.DeprioritizeAndTrim,
                "Lower the memory priority of the largest background apps and trim them once, so the foreground app keeps its pages.");
        }
        // Hard faults alone are not pressure: apps reading memory-mapped files fault too. Only count them when RAM is also getting short.
        if (availablePct < 12 || s.CommitPercent >= 90 || (s.HardFaultsPerSec >= 150 && availablePct < 25))
        {
            return new(MemoryBehavior.Pressure,
                $"Memory is tight: {availablePct:F0}% free, commit {s.CommitPercent:F0}%, {s.HardFaultsPerSec:F0} hard faults/s.",
                MemoryActionKind.DeprioritizeBackground,
                "Lower the memory priority of the largest background apps (their pages are reclaimed first; nothing is trimmed).");
        }
        if (s.CommitGrowthMbPerMin >= 500)
        {
            return new(MemoryBehavior.AllocationBurst,
                $"Commit is growing {s.CommitGrowthMbPerMin:F0} MB/min — an app is allocating quickly (loading, compiling, caching).",
                MemoryActionKind.None,
                "Observe only: bursts are normally transient. The leak detector tracks it if it never levels off.");
        }
        if (s.SoftFaultsPerSec >= 20_000 && s.HardFaultsPerSec < 50)
        {
            return new(MemoryBehavior.WorkingSetChurn,
                $"{s.SoftFaultsPerSec:F0} soft faults/s but few hard faults: pages shuttle between working sets and the standby list in RAM.",
                MemoryActionKind.None,
                "No action: trimming working sets now would increase churn. This is cheap in-RAM activity.");
        }
        return new(MemoryBehavior.Stable,
            $"Memory is stable: {availablePct:F0}% free, commit {s.CommitPercent:F0}%, {s.HardFaultsPerSec:F0} hard faults/s.",
            MemoryActionKind.None, "No action needed.");
    }
}

/// <summary>
/// Regression detection (F2). Each KPI keeps a slow EWMA baseline (half-life ≈ 6 h of minute
/// samples) and a fast 10-minute mean. A regression is reported when the baseline has at
/// least 60 minutes of history and the recent mean is worse by the KPI's threshold and above
/// its absolute floor (so "1 ms → 2 ms" never counts as a regression).
/// </summary>
public sealed class RegressionDetector
{
    public sealed record KpiDefinition(string Name, Subsystem Subsystem, string Unit, double RelativeThreshold, double AbsoluteFloor);

    public static readonly KpiDefinition[] Kpis =
    [
        new("Disk p99 latency", Subsystem.Disk, "ms", 0.5, 20),
        new("Stalls per hour", Subsystem.Cpu, "/h", 1.0, 3),
        new("Hard page faults", Subsystem.Memory, "/s", 1.0, 100),
        new("TCP retransmissions", Subsystem.Network, "/s", 1.0, 5),
        new("Time CPU-limited", Subsystem.Power, "%", 1.0, 10),
    ];

    private static readonly double Alpha = 1 - Math.Exp(-Math.Log(2) / 360.0);
    private readonly double[] _baseline = new double[Kpis.Length];
    private readonly int[] _baselineMinutes = new int[Kpis.Length];
    private readonly Queue<double>[] _recent = [.. Kpis.Select(_ => new Queue<double>())];

    /// <summary>Feeds one per-minute value for every KPI (same order as <see cref="Kpis"/>).</summary>
    public void ObserveMinute(ReadOnlySpan<double> values)
    {
        if (values.Length != Kpis.Length) { throw new ArgumentException("one value per KPI", nameof(values)); }
        for (var i = 0; i < Kpis.Length; i++)
        {
            var v = values[i];
            if (double.IsNaN(v)) { continue; }
            _baseline[i] = _baselineMinutes[i] == 0 ? v : _baseline[i] + (Alpha * (v - _baseline[i]));
            _baselineMinutes[i]++;
            _recent[i].Enqueue(v);
            while (_recent[i].Count > 10) { _recent[i].Dequeue(); }
        }
    }

    public double BaselineHours => _baselineMinutes.Max() / 60.0;

    public List<(KpiDefinition Kpi, double Baseline, double Current)> Regressions()
    {
        var result = new List<(KpiDefinition, double, double)>();
        for (var i = 0; i < Kpis.Length; i++)
        {
            if (_baselineMinutes[i] < 60 || _recent[i].Count < 5) { continue; }
            var current = _recent[i].Average();
            var k = Kpis[i];
            if (current >= k.AbsoluteFloor && current > _baseline[i] * (1 + k.RelativeThreshold))
            {
                result.Add((k, Math.Round(_baseline[i], 2), Math.Round(current, 2)));
            }
        }
        return result;
    }

    public (double[] Baseline, int[] Minutes) Export() => ((double[])_baseline.Clone(), (int[])_baselineMinutes.Clone());

    public void Import(double[] baseline, int[] minutes)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(minutes);
        if (baseline.Length != Kpis.Length || minutes.Length != Kpis.Length) { return; }
        for (var i = 0; i < Kpis.Length; i++)
        {
            if (double.IsFinite(baseline[i]) && baseline[i] >= 0 && minutes[i] >= 0)
            {
                _baseline[i] = baseline[i];
                _baselineMinutes[i] = Math.Min(minutes[i], 100_000);
            }
        }
    }
}
