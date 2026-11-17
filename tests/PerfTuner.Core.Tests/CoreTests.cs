using System.Text;
using PerfTuner.Contracts;
using PerfTuner.Core.Buffers;
using PerfTuner.Core.Configuration;
using PerfTuner.Core.Safety;
using PerfTuner.Core.Security;
using PerfTuner.Core.Telemetry;

namespace PerfTuner.Core.Tests;

public class RingBufferTests
{
    [Fact]
    public void Overwrites_oldest_and_copies_in_order()
    {
        var ring = new RingBuffer<int>(3);
        for (var i = 1; i <= 5; i++)
        {
            ring.Add(i);
        }
        Span<int> dest = stackalloc int[10];
        var n = ring.CopyLatest(dest);
        Assert.Equal(3, n);
        Assert.Equal([3, 4, 5], dest[..n].ToArray());
        Assert.True(ring.TryGetLatest(out var last));
        Assert.Equal(5, last);
        Assert.Equal(5, ring.TotalWritten);
    }

    [Fact]
    public void Steady_state_add_does_not_allocate()
    {
        var ring = new RingBuffer<MetricSample>(64);
        var sample = new MetricSample { CpuPercent = 1 };
        ring.Add(sample); // warm-up
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++)
        {
            ring.Add(sample);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

public class IoAccumulatorTests
{
    [Fact]
    public void Totals_and_top_n_are_correct_and_reset()
    {
        var acc = new IoAccumulator(64);
        acc.AddDiskRead(100, 1000);
        acc.AddDiskWrite(100, 500);
        acc.AddNetRx(200, 5000);
        acc.AddNetTx(300, 10);
        acc.AddDiskRead(-1, 7);   // unattributed: totals only

        Span<ProcessIoCounters> top = stackalloc ProcessIoCounters[2];
        var totals = acc.SnapshotAndReset(top, out var count);

        Assert.Equal(1007, totals.DiskReadBytes);
        Assert.Equal(500, totals.DiskWriteBytes);
        Assert.Equal(5000, totals.NetRxBytes);
        Assert.Equal(10, totals.NetTxBytes);
        Assert.Equal(2, count);
        Assert.Equal(200, top[0].Pid);
        Assert.Equal(100, top[1].Pid);

        var empty = acc.SnapshotAndReset(top, out count);
        Assert.Equal(0, empty.DiskReadBytes + empty.NetRxBytes);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Table_overflow_keeps_totals_exact_and_flags()
    {
        var acc = new IoAccumulator(16); // 12 usable slots
        for (var pid = 4; pid < 4 + (100 * 4); pid += 4)
        {
            acc.AddNetRx(pid, 1);
        }
        Span<ProcessIoCounters> top = stackalloc ProcessIoCounters[5];
        var totals = acc.SnapshotAndReset(top, out _);
        Assert.Equal(100, totals.NetRxBytes);
        Assert.True(totals.TableOverflowed);
    }

    [Fact]
    public void Event_flood_does_not_allocate()
    {
        // Load test for the ETW hot path: 1M events across 500 PIDs.
        var acc = new IoAccumulator(1024);
        Span<ProcessIoCounters> top = stackalloc ProcessIoCounters[5];
        acc.AddDiskRead(4, 1);
        acc.SnapshotAndReset(top, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000_000; i++)
        {
            acc.AddDiskRead(4 + ((i % 500) * 4), 4096);
        }
        var totals = acc.SnapshotAndReset(top, out var count);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(4096L * 1_000_000, totals.DiskReadBytes);
        Assert.Equal(5, count);
    }
}

public class TokenBucketTests
{
    [Fact]
    public void Enforces_burst_then_refills()
    {
        var bucket = new TokenBucket(ratePerSecond: 10, burst: 3, nowMs: 0);
        Assert.True(bucket.TryTake(0));
        Assert.True(bucket.TryTake(0));
        Assert.True(bucket.TryTake(0));
        Assert.False(bucket.TryTake(0));
        Assert.True(bucket.TryTake(100));  // 100 ms → 1 token
        Assert.False(bucket.TryTake(100));
    }
}

public class PathGuardTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "perftuner-root");

    [Theory]
    [InlineData("logs")]
    [InlineData("logs/service.json")]
    public void Accepts_children(string child) => Assert.True(PathGuard.TryResolveUnderRoot(Root, child, out _));

    [Theory]
    [InlineData("..")]
    [InlineData("../etc/passwd")]
    [InlineData("logs/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("file.txt:stream")]
    [InlineData("")]
    public void Rejects_escapes(string child) => Assert.False(PathGuard.TryResolveUnderRoot(Root, child, out _));
}

public class ConfigLoaderTests
{
    private static ConfigLoadResult Parse(string json) => ConfigLoader.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Partial_file_keeps_defaults_for_missing_fields()
    {
        var r = Parse("""{ "aggregationIntervalMs": 2000 }""");
        Assert.False(r.UsedDefaults);
        Assert.Equal(2000, r.Config.AggregationIntervalMs);
        Assert.True(r.Config.SafeMode);
    }

    [Theory]
    [InlineData("""{ "aggregationIntervalMs": 50 }""")]
    [InlineData("""{ "historyCapacity": 10000000 }""")]
    [InlineData("""{ "unknownKnob": 1 }""")]
    [InlineData("""{ "logging": { "minimumLevel": "Everything" } }""")]
    [InlineData("""{ "circuitBreaker": { "sustainSeconds": 0 } }""")]
    [InlineData("""{ "schemaVersion": 2 }""")]
    [InlineData("""{ broken""")]
    public void Invalid_config_falls_back_to_defaults(string json)
    {
        var r = Parse(json);
        Assert.True(r.UsedDefaults);
        Assert.NotEmpty(r.Errors);
        Assert.Equal(new ServiceConfig().AggregationIntervalMs, r.Config.AggregationIntervalMs);
    }

    [Fact]
    public void Oversized_config_is_rejected()
    {
        var big = new byte[ConfigLoader.MaxConfigBytes + 1];
        Assert.True(ConfigLoader.Parse(big).UsedDefaults);
    }

    [Fact]
    public void Defaults_serialize_and_validate()
    {
        var json = ConfigLoader.Serialize(new ServiceConfig());
        Assert.False(Parse(json).UsedDefaults);
    }
}

public class CircuitBreakerTests
{
    private static readonly CircuitBreakerOptions Options = new()
    {
        CpuSaturationPercent = 95,
        SustainSeconds = 10,
        CooldownSeconds = 60,
        MaxTripsPerHour = 3,
        MaxCollectorFaultsPerMinute = 3,
    };

    private static HealthSignals Cpu(double v) => new(v, 10, 10, CollectorFault: false);

    [Fact]
    public void Trips_only_after_sustained_saturation_and_rolls_back()
    {
        var trips = 0;
        var breaker = new CircuitBreaker(Options, _ => trips++);

        breaker.Evaluate(Cpu(99), 0);
        breaker.Evaluate(Cpu(99), 9_000);
        Assert.Equal(BreakerState.Closed, breaker.State);

        breaker.Evaluate(Cpu(99), 10_000);
        Assert.Equal(BreakerState.Open, breaker.State);
        Assert.False(breaker.AllowsTuning);
        Assert.Equal(1, trips);
    }

    [Fact]
    public void Spike_that_ends_does_not_trip()
    {
        var breaker = new CircuitBreaker(Options, _ => { });
        breaker.Evaluate(Cpu(99), 0);
        breaker.Evaluate(Cpu(20), 5_000);
        breaker.Evaluate(Cpu(99), 6_000);
        breaker.Evaluate(Cpu(99), 15_000);
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void Recovers_through_half_open_after_cooldown_and_healthy_period()
    {
        var breaker = new CircuitBreaker(Options, _ => { });
        breaker.ForceTrip("test", 0);
        breaker.Evaluate(Cpu(10), 59_000);
        Assert.Equal(BreakerState.Open, breaker.State);
        breaker.Evaluate(Cpu(10), 60_000);
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
        breaker.Evaluate(Cpu(10), 70_000);
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void Instability_during_probation_retrips_immediately()
    {
        var trips = 0;
        var breaker = new CircuitBreaker(Options, _ => trips++);
        breaker.ForceTrip("test", 0);
        breaker.Evaluate(Cpu(10), 60_000);
        breaker.Evaluate(Cpu(99), 61_000);
        Assert.Equal(BreakerState.Open, breaker.State);
        Assert.Equal(2, trips);
    }

    [Fact]
    public void Latches_after_too_many_trips()
    {
        var breaker = new CircuitBreaker(Options, _ => { });
        breaker.ForceTrip("1", 0);
        breaker.Evaluate(Cpu(10), 60_000);   // half-open
        breaker.Evaluate(Cpu(99), 61_000);   // trip 2
        breaker.Evaluate(Cpu(10), 121_000);  // half-open
        breaker.Evaluate(Cpu(99), 122_000);  // trip 3 → latched
        Assert.Equal(BreakerState.Latched, breaker.State);
        breaker.Evaluate(Cpu(10), 10_000_000);
        Assert.Equal(BreakerState.Latched, breaker.State);
        breaker.Reset();
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void Collector_faults_trip()
    {
        var breaker = new CircuitBreaker(Options, _ => { });
        breaker.Evaluate(new HealthSignals(10, 10, 10, true), 0);
        breaker.Evaluate(new HealthSignals(10, 10, 10, true), 1_000);
        breaker.Evaluate(new HealthSignals(10, 10, 10, true), 2_000);
        Assert.Equal(BreakerState.Open, breaker.State);
    }
}

public class ChangeJournalTests
{
    internal sealed class FakeChange(string name, List<string> log, RevertOutcome outcome = RevertOutcome.Reverted, int pid = 100, bool alive = true) : IReversibleChange
    {
        public string Kind => "fake";
        public string Description => name;
        public int TargetPid => pid;
        public bool Alive { get; set; } = alive;
        public bool IsTargetAlive() => Alive;
        public RevertOutcome Revert()
        {
            log.Add(name);
            return outcome;
        }
        public ChangeRecord ToRecord() => new() { Kind = Kind, Description = name, Pid = pid };
    }

    [Fact]
    public void Rolls_back_lifo_and_reports()
    {
        var log = new List<string>();
        var journal = new ChangeJournal();
        journal.TryRecord(new FakeChange("a", log));
        journal.TryRecord(new FakeChange("b", log, RevertOutcome.TargetGone));
        journal.TryRecord(new FakeChange("c", log, RevertOutcome.Failed));

        var report = journal.RollbackAll();
        Assert.Equal(["c", "b", "a"], log);
        Assert.Equal(1, report.Reverted);
        Assert.Equal(1, report.TargetGone);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void Refuses_when_full()
    {
        var journal = new ChangeJournal(capacity: 1);
        Assert.True(journal.TryRecord(new FakeChange("a", [])) > 0);
        Assert.Equal(-1, journal.TryRecord(new FakeChange("b", [])));
    }

    [Fact]
    public void Reverts_single_change_by_id_and_persists_every_mutation()
    {
        var persisted = new List<int>();
        var log = new List<string>();
        var journal = new ChangeJournal(persist: records => persisted.Add(records.Count));
        var a = journal.TryRecord(new FakeChange("a", log));
        journal.TryRecord(new FakeChange("b", log));

        var result = journal.Revert(a);
        Assert.Equal(RevertOutcome.Reverted, result!.Value.Outcome);
        Assert.Equal(["a"], log);
        Assert.Null(journal.Revert(a));
        Assert.Equal([1, 2, 1], persisted);
    }

    [Fact]
    public void Rollback_where_selects_by_owner()
    {
        var log = new List<string>();
        var journal = new ChangeJournal();
        journal.TryRecord(new FakeChange("manual", log), ChangeOwners.Manual);
        journal.TryRecord(new FakeChange("quiet", log), ChangeOwners.Quiet);
        journal.RollbackWhere(e => e.IsAutomatic);
        Assert.Equal(["quiet"], log);
        Assert.Equal(1, journal.Count);
    }

    [Fact]
    public void Prune_drops_changes_on_exited_processes_without_reverting()
    {
        var log = new List<string>();
        var dead = new FakeChange("dead", log, alive: false);
        var journal = new ChangeJournal();
        journal.TryRecord(dead);
        journal.TryRecord(new FakeChange("alive", log));
        Assert.Equal(1, journal.Prune());
        Assert.Empty(log);
        Assert.Equal(1, journal.Count);
    }
}

public class OverheadGovernorTests
{
    [Fact]
    public void Degrades_over_budget_and_restores_with_hysteresis()
    {
        var gov = new OverheadGovernor(budgetPercent: 0.5, windowSamples: 4);
        for (var i = 0; i < 4; i++) { gov.Observe(1.0); }
        Assert.True(gov.Degraded);
        for (var i = 0; i < 4; i++) { gov.Observe(0.4); }
        Assert.True(gov.Degraded);  // 0.4 is under budget but not under half
        for (var i = 0; i < 4; i++) { gov.Observe(0.1); }
        Assert.False(gov.Degraded);
    }
}
