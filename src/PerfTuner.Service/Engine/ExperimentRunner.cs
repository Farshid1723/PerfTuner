using PerfTuner.Contracts;
using PerfTuner.Core.Autonomy;
using PerfTuner.Core.Safety;
using PerfTuner.Platform.Windows.Tuning;

namespace PerfTuner.Service.Engine;

/// <summary>System-level KPIs over a wall-clock window, provided by the forensics engine.</summary>
internal readonly record struct WindowKpis(int Stalls, double DiskP99Ms, double HardFaultsPerSec);

/// <summary>
/// Experiment mode (F4): A/B test one tuning contract on one process.
/// <code>
///  Baseline (30 s, nothing changed) ─▶ apply via TuningEngine (contract-checked, journaled)
///  ─▶ Treatment (30 s) ─▶ ExperimentEvaluator: keep only proven, guard-safe results; else roll back
/// </code>
/// Aborts (and rolls back) when the circuit breaker trips, the target exits or changes identity,
/// the user reverts the change, or — for autonomous runs — the profile drops below Advanced.
/// Every run lands in a bounded, persisted audit trail (50 records).
/// </summary>
internal sealed class ExperimentRunner
{
    public const long PhaseMs = 30_000;
    private const int MaxHistory = 50;
    private const long AutonomousIntervalMs = 30 * 60_000;
    private const long RetryAfterMs = 6 * 3600_000;

    private readonly TuningEngine _tuning;
    private readonly SafetyCoordinator _safety;
    private readonly Func<long, long, WindowKpis> _window;
    private readonly Action _persist;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<ExperimentRecord> _history = [];
    private readonly Dictionary<string, long> _tried = new(StringComparer.OrdinalIgnoreCase);

    private Running? _run;
    private long _nextId = 1;
    private long _lastAutonomousMs = -1_000_000_000_000;
    private long _lastAutopilotSweepMs = -1_000_000_000_000;

    private sealed class Running
    {
        public required ExperimentRecord Record;
        public required TuningContract Contract;
        public required int Pid;
        public required long CreateTime;
        public required string Name;
        public required string Owner;
        public long PhaseStartTick;
        public long PhaseStartWall;
        public long CpuStart;
        public bool Treatment;
        public long ChangeId = -1;
        public double[] Baseline = [];
    }

    public ExperimentRunner(TuningEngine tuning, SafetyCoordinator safety, Func<long, long, WindowKpis> window, Action persist, ILogger logger)
    {
        _tuning = tuning;
        _safety = safety;
        _window = window;
        _persist = persist;
        _logger = logger;
    }

    public ExperimentsResponse Get(uint id)
    {
        lock (_gate)
        {
            return new ExperimentsResponse { Id = id, Current = _run?.Record, History = [.. _history] };
        }
    }

    public List<ExperimentRecord> ExportHistory()
    {
        lock (_gate) { return [.. _history]; }
    }

    public void ImportHistory(IEnumerable<ExperimentRecord>? records)
    {
        if (records is null) { return; }
        lock (_gate)
        {
            foreach (var r in records.Take(MaxHistory))
            {
                if (r is null || r.Kpis is null || r.ContractId is null || r.Target is null || r.Decision is null) { continue; }
                // A run interrupted by a service stop was rolled back with the journal.
                _history.Add(r.State is ExperimentState.Baseline or ExperimentState.Treatment
                    ? r with { State = ExperimentState.Aborted, Decision = "Aborted: the service stopped (change rolled back)." }
                    : r);
                _nextId = Math.Max(_nextId, r.Id + 1);
            }
        }
    }

    public ActionResultResponse Start(uint id, string contractId, int pid, long createTime, bool autonomous, long tickMs, long wallMs)
    {
        var contract = TuningContracts.Find(contractId);
        if (contract is null || !contract.Experimentable || contract.Action is null)
        {
            return Fail(id, $"'{contractId}' is not an experimentable contract.");
        }
        var profile = _tuning.Profile;
        if (!TuningContracts.Allows(profile, contract))
        {
            return Fail(id, $"Contract '{contract.Name}' needs the {contract.MinimumProfile} profile.");
        }
        if (!_safety.AllowsTuning)
        {
            return Fail(id, $"Tuning is paused by the circuit breaker ({_safety.State}).");
        }
        var name = _tuning.DescribeControllable(pid, createTime, out var reason);
        if (name is null)
        {
            return Fail(id, reason);
        }
        var cpu = ProcessMeter.CpuTime100ns(pid, createTime);
        if (cpu is null)
        {
            return Fail(id, "Cannot measure the process (it may have exited).");
        }

        lock (_gate)
        {
            if (_run is not null)
            {
                return Fail(id, "An experiment is already running.");
            }
            var record = new ExperimentRecord
            {
                Id = _nextId++, ContractId = contract.Id, Target = $"{name} ({pid})", State = ExperimentState.Baseline,
                StartedUnixMs = wallMs, Kpis = [], Autonomous = autonomous,
                Decision = $"Measuring baseline for {PhaseMs / 1000} s — nothing has been changed yet.",
            };
            _run = new Running
            {
                Record = record, Contract = contract, Pid = pid, CreateTime = createTime, Name = name,
                Owner = autonomous ? ChangeOwners.Autopilot : ChangeOwners.Experiment,
                PhaseStartTick = tickMs, PhaseStartWall = wallMs, CpuStart = cpu.Value,
            };
            _tried[contract.Id + "|" + name] = tickMs;
            if (autonomous) { _lastAutonomousMs = tickMs; }
        }
        _tuning.Note($"Experiment: started {contract.Name} on {name} ({pid}){(autonomous ? " — autonomous" : "")}");
        return new ActionResultResponse { Id = id, Success = true, Message = $"Experiment started: {PhaseMs / 1000} s baseline, then {PhaseMs / 1000} s with '{contract.Name}' on {name}." };
    }

    /// <summary>True if this contract/process pair was tried recently (autopilot never retries within 6 h).</summary>
    public bool RecentlyTried(string contractId, string name, long tickMs)
    {
        lock (_gate)
        {
            return _tried.TryGetValue(contractId + "|" + name, out var t) && tickMs - t < RetryAfterMs;
        }
    }

    public bool AutonomousDue(long tickMs)
    {
        lock (_gate) { return _run is null && tickMs - _lastAutonomousMs >= AutonomousIntervalMs; }
    }

    public void OnTick(long tickMs, long wallMs)
    {
        // Autonomous changes live only while the Advanced profile is active.
        if (_tuning.Profile < TuningProfile.Advanced && tickMs - _lastAutopilotSweepMs >= 10_000)
        {
            _lastAutopilotSweepMs = tickMs;
            if (_tuning.RevertOwner(ChangeOwners.Autopilot) > 0)
            {
                _tuning.Note("Autopilot: profile below Advanced — autonomous changes reverted");
            }
        }

        Running? run;
        lock (_gate) { run = _run; }
        if (run is null) { return; }

        var cpu = ProcessMeter.CpuTime100ns(run.Pid, run.CreateTime);
        if (!_safety.AllowsTuning) { Finish(run, ExperimentState.Aborted, "Aborted: the circuit breaker tripped.", [], wallMs, revert: true); return; }
        if (cpu is null) { Finish(run, ExperimentState.Aborted, "Aborted: the target process exited.", [], wallMs, revert: true); return; }
        if (run.Record.Autonomous && _tuning.Profile < TuningProfile.Advanced) { Finish(run, ExperimentState.Aborted, "Aborted: profile changed from Advanced.", [], wallMs, revert: true); return; }
        if (run.Treatment && !_tuning.IsChangeActive(run.ChangeId)) { Finish(run, ExperimentState.Aborted, "Aborted: the change was reverted by the user.", [], wallMs, revert: false); return; }

        if (tickMs - run.PhaseStartTick < PhaseMs) { return; }

        var values = Measure(run, cpu.Value, tickMs, wallMs);
        if (!run.Treatment)
        {
            run.Baseline = values;
            var result = _tuning.ApplyAction(BuildRequest(run), tickMs, run.Owner);
            if (!result.Success || result.ChangeId is not { } changeId)
            {
                Finish(run, ExperimentState.Aborted, "Aborted: could not apply — " + result.Message, [], wallMs, revert: false);
                return;
            }
            lock (_gate)
            {
                run.ChangeId = changeId;
                run.Treatment = true;
                run.PhaseStartTick = tickMs;
                run.PhaseStartWall = wallMs;
                run.CpuStart = cpu.Value;
                run.Record = run.Record with { State = ExperimentState.Treatment, Decision = $"Applied: {result.Message}. Measuring for {PhaseMs / 1000} s…" };
            }
            return;
        }

        var kpis = Kpis(run.Baseline, values);
        var (accept, decision) = ExperimentEvaluator.Evaluate(run.Contract, kpis);
        Finish(run, accept ? ExperimentState.Accepted : ExperimentState.RolledBack, decision, kpis, wallMs, revert: !accept);
    }

    private static double[] MeasureEmpty => [0, 0, 0, 0, 0];

    private double[] Measure(Running run, long cpuNow, long tickMs, long wallMs)
    {
        var elapsedMs = Math.Max(1, tickMs - run.PhaseStartTick);
        var cpuPct = 100.0 * (cpuNow - run.CpuStart) / (elapsedMs * 10_000.0 * Environment.ProcessorCount);
        var ws = ProcessMeter.WorkingSetBytes(run.Pid, run.CreateTime) is { } b ? b / 1048576.0 : 0;
        var w = _window(run.PhaseStartWall, wallMs);
        return [Math.Round(Math.Max(0, cpuPct), 2), Math.Round(ws, 1), w.Stalls, Math.Round(w.DiskP99Ms, 1), Math.Round(w.HardFaultsPerSec, 1)];
    }

    private static KpiValue[] Kpis(double[] before, double[] after)
    {
        if (before.Length != 5) { before = MeasureEmpty; }
        return
        [
            new() { Name = "Target CPU", Before = before[0], After = after[0], Unit = "%", LowerIsBetter = true },
            new() { Name = "Target working set", Before = before[1], After = after[1], Unit = "MB", LowerIsBetter = true },
            new() { Name = "System stalls", Before = before[2], After = after[2], Unit = "", LowerIsBetter = true },
            new() { Name = "Disk p99", Before = before[3], After = after[3], Unit = "ms", LowerIsBetter = true },
            new() { Name = "Hard faults/s", Before = before[4], After = after[4], Unit = "/s", LowerIsBetter = true },
        ];
    }

    private static ProcessActionRequest BuildRequest(Running run)
    {
        var baseReq = new ProcessActionRequest { Id = 0, Pid = run.Pid, CreateTime = run.CreateTime, Action = run.Contract.Action!.Value };
        return run.Contract.Id switch
        {
            "priority-below" => baseReq with { Priority = PriorityLevel.BelowNormal },
            "efficiency-mode" => baseReq with { Enable = true },
            "cpu-efficiency-cores" => baseReq with { Cores = CpuCorePreset.EfficiencyCores },
            "memory-priority-low" => baseReq with { MemoryPriority = MemoryPriorityLevel.Low },
            "soft-memory-cap" => baseReq with
            {
                // Cap at 75 % of the current working set, within the contract bounds.
                CapMb = (int)Math.Clamp((ProcessMeter.WorkingSetBytes(run.Pid, run.CreateTime) ?? 0) / 1048576.0 * 0.75,
                    ProtocolConstants.MinSoftCapMb, ProtocolConstants.MaxSoftCapMb),
            },
            _ => baseReq,
        };
    }

    private void Finish(Running run, ExperimentState state, string decision, KpiValue[] kpis, long wallMs, bool revert)
    {
        if (revert && run.ChangeId >= 0)
        {
            _tuning.Revert(0, run.ChangeId);
        }
        var record = run.Record with { State = state, Decision = decision, Kpis = kpis, FinishedUnixMs = wallMs };
        lock (_gate)
        {
            if (!ReferenceEquals(_run, run)) { return; }
            _run = null;
            _history.Insert(0, record);
            if (_history.Count > MaxHistory) { _history.RemoveAt(_history.Count - 1); }
        }
        _tuning.Note($"Experiment {state}: {run.Contract.Name} on {run.Name} — {decision}");
        _logger.LogInformation("Experiment {Id} {State}: {Contract} on {Target}", record.Id, state, run.Contract.Id, record.Target);
        _persist();
    }

    /// <summary>Service stopping: the journal rolls everything back; record the abort.</summary>
    public void Shutdown(long wallMs)
    {
        Running? run;
        lock (_gate) { run = _run; }
        if (run is not null)
        {
            Finish(run, ExperimentState.Aborted, "Aborted: the service stopped (change rolled back).", [], wallMs, revert: true);
        }
    }

    private static ActionResultResponse Fail(uint id, string message) => new() { Id = id, Success = false, Message = message };
}
