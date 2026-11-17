using PerfTuner.Contracts;
using PerfTuner.Core.Autonomy;
using PerfTuner.Core.Forensics;
using PerfTuner.Core.Visibility;

namespace PerfTuner.Core.Tests;

public class LatencyHistogramTests
{
    [Fact]
    public void Percentiles_land_in_the_right_buckets()
    {
        var h = new LatencyHistogram();
        for (var i = 0; i < 90; i++) { h.AddMs(1); }      // ~1 ms
        for (var i = 0; i < 9; i++) { h.AddMs(20); }      // ~20 ms
        h.AddMs(500);                                      // outlier
        Assert.Equal(100, h.Count);
        Assert.InRange(h.PercentileMs(0.5), 0.5, 2);
        Assert.InRange(h.PercentileMs(0.95), 10, 40);
        Assert.InRange(h.PercentileMs(0.999), 250, 500);
        Assert.Equal(500, h.MaxMs, 3);
    }

    [Fact]
    public void Adding_samples_does_not_allocate()
    {
        var h = new LatencyHistogram();
        h.AddMs(1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++) { h.AddUs(i % 5000); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Ignores_invalid_samples()
    {
        var h = new LatencyHistogram();
        h.AddMs(double.NaN);
        h.AddMs(-1);
        h.AddMs(double.PositiveInfinity);
        Assert.Equal(0, h.Count);
    }
}

public class BurstDetectorTests
{
    [Fact]
    public void Detects_a_microburst_against_a_quiet_baseline_and_attributes_it()
    {
        var d = new BurstDetector();
        const long t0 = 1_000_000_000_000;
        for (var k = 0; k < 100; k++) { d.Add(t0 + (k * 100), 2_000, receive: true, pid: 10); }   // ~0.16 Mbit/s
        d.Add(t0 + 10_000, 1_500_000, receive: true, pid: 42);                                    // 120 Mbit/s for 100 ms
        for (var k = 101; k < 130; k++) { d.Add(t0 + (k * 100), 2_000, receive: true, pid: 10); }

        var bursts = d.Evaluate(t0 + 20_000);
        var b = Assert.Single(bursts);
        Assert.True(b.Receive);
        Assert.Equal(42, b.Pid);
        Assert.InRange(b.Mbps, 100, 130);
    }

    [Fact]
    public void Steady_high_throughput_is_not_a_burst()
    {
        var d = new BurstDetector();
        const long t0 = 1_000_000_000_000;
        for (var k = 0; k < 200; k++) { d.Add(t0 + (k * 100), 1_500_000, receive: false, pid: 5); }
        Assert.Empty(d.Evaluate(t0 + 30_000));
    }
}

public class StallDetectorTests
{
    private static StallSignals S(long t, double cpu = 10, double p99 = 1, double q = 0, double hf = 0, double avail = 50, double limit = 100, double retx = 0)
        => new(t, cpu, p99, q, hf, avail, limit, retx);

    [Fact]
    public void Cpu_stall_needs_three_intervals_and_has_cooldown()
    {
        var d = new StallDetector();
        Assert.Empty(d.Evaluate(S(0, cpu: 99)));
        Assert.Empty(d.Evaluate(S(1000, cpu: 99)));
        var issue = Assert.Single(d.Evaluate(S(2000, cpu: 99)));
        Assert.True(issue.IsStall);
        Assert.Empty(d.Evaluate(S(3000, cpu: 99)));          // cooldown
        Assert.Single(d.Evaluate(S(40_000, cpu: 99)));       // after cooldown
    }

    [Fact]
    public void Thrashing_is_critical_and_throttling_fires_on_transition()
    {
        var d = new StallDetector();
        var thrash = Assert.Single(d.Evaluate(S(0, hf: 2000, avail: 5)));
        Assert.Equal(Severity.Critical, thrash.Severity);
        Assert.Single(d.Evaluate(S(1000, limit: 70)));
        Assert.Empty(d.Evaluate(S(2000, limit: 70)));        // no repeat while still limited
    }
}

public class StallExplainerTests
{
    private static MetricSample M(long t, double cpu, double limit = 100) => new() { TimestampUnixMs = t, CpuPercent = cpu, CpuLimitPercent = limit };

    [Fact]
    public void Cpu_contention_is_explained_with_the_top_process_and_a_suggestion()
    {
        var timeline = new Timeline();
        var stall = timeline.Add(100_000, Subsystem.Cpu, Severity.Warning, "CPU saturated", "", isStall: true);
        var samples = Enumerable.Range(0, 18).Select(i => M(85_000 + (i * 1000), 98)).ToList();
        var ctx = new StallContext([new Culprit(77, 1, "compiler", 80, Background: true)], [], [], []);

        var r = StallExplainer.Explain(1, stall, timeline.Window(85_000, 103_000), samples, ctx, null, []);

        Assert.True(r.Found);
        var top = r.Causes[0];
        Assert.Equal(Subsystem.Cpu, top.Subsystem);
        Assert.Contains("compiler", top.Title);
        Assert.Equal("efficiency-mode", top.SuggestedContractId);
        Assert.Equal(77, top.SuggestedPid);
        Assert.Contains("98%", r.Story);
    }

    [Fact]
    public void Disk_and_memory_evidence_rank_above_weak_signals_and_changes_are_listed()
    {
        var timeline = new Timeline();
        timeline.Add(95_000, Subsystem.Memory, Severity.Critical, "Memory thrashing", "2000 hard faults/s", isStall: true);
        var stall = timeline.Add(100_000, Subsystem.Disk, Severity.Warning, "Storage stall", "p99 400 ms", isStall: true);
        var samples = Enumerable.Range(0, 18).Select(i => M(85_000 + (i * 1000), 30)).ToList();

        var r = StallExplainer.Explain(1, stall, timeline.Window(85_000, 103_000), samples, null, null, [(90_000L, "chrome: efficiency mode")]);

        Assert.Equal(Subsystem.Memory, r.Causes[0].Subsystem);
        Assert.Contains(r.Causes, c => c.Subsystem == Subsystem.Disk);
        Assert.Contains(r.Causes, c => c.Subsystem == Subsystem.Tuning);
        Assert.DoesNotContain(r.Causes, c => c.Subsystem == Subsystem.Cpu);
    }

    [Fact]
    public void No_evidence_gives_an_honest_answer()
    {
        var timeline = new Timeline();
        var stall = timeline.Add(100_000, Subsystem.Cpu, Severity.Warning, "Stall", "", isStall: true);
        var r = StallExplainer.Explain(1, stall, [], [M(99_000, 5)], null, null, []);
        Assert.False(r.Found);
        Assert.Contains("no single subsystem", r.Story);
    }
}

public class MemoryClassifierTests
{
    [Theory]
    [InlineData(500, 16000, 70, 0, 800, 100, MemoryBehavior.Thrashing)]
    [InlineData(1500, 16000, 70, 0, 10, 100, MemoryBehavior.Pressure)]
    [InlineData(8000, 16000, 92, 0, 10, 100, MemoryBehavior.Pressure)]
    [InlineData(8000, 16000, 50, 900, 10, 100, MemoryBehavior.AllocationBurst)]
    [InlineData(8000, 16000, 50, 0, 10, 40000, MemoryBehavior.WorkingSetChurn)]
    [InlineData(8000, 16000, 50, 0, 10, 100, MemoryBehavior.Stable)]
    [InlineData(8000, 16000, 50, 0, 400, 100, MemoryBehavior.Stable)] // file-backed faults with plenty of RAM are not pressure
    public void Classifies_behaviour(double avail, double total, double commit, double growth, double hard, double soft, MemoryBehavior expected)
        => Assert.Equal(expected, MemoryBehaviorClassifier.Classify(new MemoryStats(avail, total, commit, growth, hard, soft)).Behavior);

    [Fact]
    public void Churn_explicitly_takes_no_action()
        => Assert.Equal(MemoryActionKind.None, MemoryBehaviorClassifier.Classify(new MemoryStats(8000, 16000, 50, 0, 10, 40000)).Action);
}

public class RegressionDetectorTests
{
    [Fact]
    public void Flags_a_sustained_regression_after_a_baseline_exists()
    {
        var d = new RegressionDetector();
        for (var m = 0; m < 120; m++) { d.ObserveMinute([10, 1, 20, 0, 0]); }
        Assert.Empty(d.Regressions());
        for (var m = 0; m < 10; m++) { d.ObserveMinute([60, 1, 20, 0, 0]); }
        var r = Assert.Single(d.Regressions());
        Assert.Equal("Disk p99 latency", r.Kpi.Name);
    }

    [Fact]
    public void Tiny_absolute_values_are_never_regressions()
    {
        var d = new RegressionDetector();
        for (var m = 0; m < 120; m++) { d.ObserveMinute([1, 0, 1, 0, 0]); }
        for (var m = 0; m < 10; m++) { d.ObserveMinute([5, 0, 5, 0, 0]); }
        Assert.Empty(d.Regressions());
    }
}

public class ContractAndExperimentTests
{
    private static KpiValue K(string name, double before, double after, bool lower = true) => new() { Name = name, Before = before, After = after, Unit = "", LowerIsBetter = lower };

    [Fact]
    public void Every_manual_action_maps_to_a_contract_and_profiles_gate_automation()
    {
        foreach (var a in Enum.GetValues<ProcessAction>())
        {
            Assert.NotNull(TuningContracts.ForAction(a, PriorityLevel.BelowNormal, CpuCorePreset.HalfCores));
        }
        Assert.False(TuningContracts.Allows(TuningProfile.Safe, TuningContracts.Find("focus-mode")!));
        Assert.True(TuningContracts.Allows(TuningProfile.Balanced, TuningContracts.Find("focus-mode")!));
        Assert.False(TuningContracts.Allows(TuningProfile.Balanced, TuningContracts.Find("autopilot")!));
        Assert.All(TuningContracts.All, c => Assert.NotEmpty(c.AbortConditions));
    }

    [Fact]
    public void Accepts_only_proven_improvement()
    {
        var eff = TuningContracts.Find("efficiency-mode")!;
        Assert.True(ExperimentEvaluator.Evaluate(eff, [K("Target CPU", 40, 20), K("System stalls", 0, 0)]).Accept);
        Assert.False(ExperimentEvaluator.Evaluate(eff, [K("Target CPU", 40, 39), K("System stalls", 0, 0)]).Accept);
    }

    [Fact]
    public void Guard_regression_forces_rollback_even_if_primary_improves()
    {
        var eff = TuningContracts.Find("efficiency-mode")!;
        var (accept, decision) = ExperimentEvaluator.Evaluate(eff, [K("Target CPU", 40, 10), K("Disk p99", 10, 80)]);
        Assert.False(accept);
        Assert.Contains("Disk p99", decision);
    }

    [Fact]
    public void Neutral_contracts_are_kept_when_nothing_gets_worse()
    {
        var below = TuningContracts.Find("priority-below")!;
        Assert.True(ExperimentEvaluator.Evaluate(below, [K("System stalls", 1, 1)]).Accept);
    }
}
