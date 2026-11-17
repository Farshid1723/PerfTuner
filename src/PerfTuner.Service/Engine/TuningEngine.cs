using System.Text.Json;
using System.Text.Json.Serialization;
using PerfTuner.Contracts;
using PerfTuner.Core.Safety;
using PerfTuner.Core.Tuning;
using PerfTuner.Platform.Windows;
using PerfTuner.Platform.Windows.Tuning;

namespace PerfTuner.Service.Engine;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(TuningState))]
internal sealed partial class TuningStateJsonContext : JsonSerializerContext;

/// <summary>
/// Executes tuning. Single entry point for every change to another process, whether it
/// comes from a user click (IPC) or from automation (modes, auto-calm, rules):
/// <code>
///  request/plan → ProcessPolicy (may this process be touched?) → breaker closed?
///              → ActionThrottle (rate limits) → apply via Platform (captures original)
///              → ChangeJournal.TryRecord (persisted) — if the journal is full, undo at once
/// </code>
/// All operations run under one lock: they are short syscalls, and serializing them makes
/// "apply" and "revert" on the same process impossible to interleave.
/// </summary>
internal sealed partial class TuningEngine : IDisposable
{
    private const long SnapshotIntervalMs = 2_000;
    private const long ProcessListInterestMs = 10_000;
    private const long ForegroundStaleMs = 10_000;
    private const long PruneIntervalMs = 10_000;

    private readonly Lock _gate = new();
    private readonly SafetyCoordinator _safety;
    private readonly ILogger<TuningEngine> _logger;
    private readonly string _stateFile;
    private readonly int _selfPid = Environment.ProcessId;
    private readonly ProcessSnapshotter _snapshotter = new();
    private readonly List<ProcSample> _samples = new(512);
    private readonly AutomationPlanner _planner = new();
    private readonly ActionThrottle _throttle = new();
    private readonly Queue<HistoryEntry> _history = new();
    private readonly CpuTopology _topology;

    private TuningState _state;
    private ProcessListResponse? _lastList;
    private long _lastSnapshotMs = -1_000_000_000_000; // not long.MinValue: (now - last) must not overflow
    private long _lastPruneMs;
    private long _listWantedUntilMs;
    private int? _foregroundPid;
    private long _foregroundAtMs = long.MinValue;

    public TuningEngine(RuntimeState runtime, SafetyCoordinator safety, ProductPaths paths, ILogger<TuningEngine> logger)
    {
        _safety = safety;
        _logger = logger;
        _stateFile = Path.Combine(paths.StateDirectory, "tuning.json");
        _state = LoadState(_stateFile, runtime.Config.SafeMode);
        _topology = CpuTopology.Detect();
        _safety.Tripped += message => AddHistory(message);
    }

    public bool HybridCpu => _topology.IsHybrid;

    public TuningState CurrentState
    {
        get { lock (_gate) { return _state with { Rules = [.. _state.Rules] }; } }
    }

    public bool ForegroundKnown(long nowMs) => Volatile.Read(ref _foregroundAtMs) > nowMs - ForegroundStaleMs;

    /// <summary>The foreground process reported by the GUI, if fresh.</summary>
    public int? ForegroundPid(long nowMs)
    {
        lock (_gate) { return ForegroundKnown(nowMs) ? _foregroundPid : null; }
    }

    public bool TuningAllowed => _safety.AllowsTuning;

    // ----------------------------------------------------------------- engine tick

    /// <summary>Called by the telemetry engine every interval. Cheap when nothing needs process data.</summary>
    public void OnTick(long nowMs)
    {
        lock (_gate)
        {
            var automationActive = !_state.SafeMode && (_state.Mode != TuningMode.Off || _state.AutoCalm || _state.Rules.Count > 0);
            var hasAutomaticChanges = HasAutomaticChanges();
            var wanted = nowMs < _listWantedUntilMs;

            if (nowMs - _lastPruneMs >= PruneIntervalMs)
            {
                _lastPruneMs = nowMs;
                _safety.Journal.Prune();
            }

            if (!(automationActive || hasAutomaticChanges || wanted) || nowMs - _lastSnapshotMs < SnapshotIntervalMs)
            {
                return;
            }
            _lastSnapshotMs = nowMs;
            _snapshotter.Snapshot(_samples);
            _samples.Sort(static (a, b) => b.CpuPercent.CompareTo(a.CpuPercent));

            if (automationActive || hasAutomaticChanges)
            {
                RunAutomation(nowMs);
            }
            _lastList = BuildList(ProtocolConstants.MaxProcessListCount);
        }
    }

    private bool HasAutomaticChanges()
    {
        foreach (var e in _safety.Journal.Snapshot())
        {
            if (e.IsAutomatic && e.Owner != ChangeOwners.Experiment) { return true; }
        }
        return false;
    }

    private void RunAutomation(long nowMs)
    {
        var views = new List<ProcView>(_samples.Count);
        foreach (var s in _samples)
        {
            views.Add(new ProcView(s.Pid, s.CreateTime, s.Name, s.CpuPercent,
                ProcessPolicy.CanControl(s.Pid, s.Name, s.SessionId, _selfPid, out _)));
        }
        var managed = new List<ManagedView>();
        foreach (var e in _safety.Journal.Snapshot())
        {
            managed.Add(new ManagedView(e.Id, e.Change.TargetPid, e.Owner));
        }
        int? fg = ForegroundKnown(nowMs) ? _foregroundPid : null;

        var plan = _planner.Plan(new PlanInput(nowMs, _state, views, fg, managed, _safety.AllowsTuning));
        foreach (var action in plan)
        {
            switch (action.Kind)
            {
                case PlanKind.Revert:
                    if (_safety.Journal.Revert(action.ChangeId) is { } r)
                    {
                        AddHistory($"Auto: reverted {r.Description}");
                    }
                    break;
                case PlanKind.ApplyEfficiency:
                    ApplyAutomatic(action.Process, action.Owner, nowMs, (ProcView p, out string e) =>
                        EfficiencyModeChange.TryApply(p.Pid, p.CreateTime, p.Name, out e));
                    break;
                case PlanKind.ApplyBoost:
                    ApplyAutomatic(action.Process, action.Owner, nowMs, (ProcView p, out string e) =>
                        PriorityChange.TryApply(p.Pid, p.CreateTime, p.Name, PriorityLevel.AboveNormal, out e));
                    break;
                case PlanKind.ApplyRule when action.Rule is { } rule:
                    ApplyRule(action.Process, rule, action.Owner, nowMs);
                    break;
            }
        }
    }

    private delegate IReversibleChange? Applier(ProcView p, out string error);

    private void ApplyAutomatic(ProcView p, string owner, long nowMs, Applier apply)
    {
        if (!_throttle.TryAcquire(p.Pid, automatic: true, nowMs))
        {
            return;
        }
        var change = apply(p, out var error);
        if (change is null)
        {
            LogAutoFailed(p.Name, p.Pid, error);
            return;
        }
        Record(change, owner, SourceName(owner));
    }

    /// <summary>The user-facing name of whatever made an automatic change (shown in history and tray notifications).</summary>
    public static string SourceName(string owner) => owner switch
    {
        ChangeOwners.FocusForeground or ChangeOwners.FocusBackground => "Turbo",
        ChangeOwners.Quiet => "Silent",
        ChangeOwners.AutoCalm => "Smart",
        ChangeOwners.Governor => "MemGuard",
        ChangeOwners.Experiment => "ProofRun",
        ChangeOwners.Autopilot => "Autopilot",
        ChangeOwners.Manual => "Manual",
        var o when o.StartsWith(ChangeOwners.RulePrefix, StringComparison.Ordinal) => "Rule",
        _ => "Auto",
    };

    private void ApplyRule(ProcView p, AppRule rule, string owner, long nowMs)
    {
        if (!_throttle.TryAcquire(p.Pid, automatic: true, nowMs))
        {
            return;
        }
        string error;
        if (rule.EfficiencyMode == true && EfficiencyModeChange.TryApply(p.Pid, p.CreateTime, p.Name, out error) is { } eff)
        {
            Record(eff, owner, "Rule");
        }
        else if (rule.Priority is { } priority && PriorityChange.TryApply(p.Pid, p.CreateTime, p.Name, priority, out error) is { } pri)
        {
            Record(pri, owner, "Rule");
        }
        if (rule.Cores is { } cores && CpuSetChange.TryApply(p.Pid, p.CreateTime, p.Name, _topology.Resolve(cores), Label(cores), out error) is { } cpu)
        {
            Record(cpu, owner, "Rule");
        }
        if (rule.MemoryPriority is { } mem && MemoryPriorityChange.TryApply(p.Pid, p.CreateTime, p.Name, mem, out error) is { } mp)
        {
            Record(mp, owner, "Rule");
        }
    }

    private long Record(IReversibleChange change, string owner, string source)
    {
        var id = _safety.Journal.TryRecord(change, owner);
        if (id < 0)
        {
            change.Revert(); // never hold a change we cannot track
            AddHistory($"{source}: journal full — undid {change.Description}");
            return -1;
        }
        AddHistory($"{source}: {change.Description}");
        return id;
    }

    // ----------------------------------------------------------------- IPC operations

    public ProcessListResponse GetProcesses(uint id, int maxCount, long nowMs)
    {
        lock (_gate)
        {
            _listWantedUntilMs = nowMs + ProcessListInterestMs;
            if (_lastList is null || nowMs - _lastSnapshotMs > SnapshotIntervalMs * 2)
            {
                _snapshotter.Snapshot(_samples);
                _samples.Sort(static (a, b) => b.CpuPercent.CompareTo(a.CpuPercent));
                _lastSnapshotMs = nowMs;
                _lastList = BuildList(ProtocolConstants.MaxProcessListCount);
            }
            var list = _lastList;
            var processes = list.Processes.Length > maxCount ? list.Processes[..maxCount] : list.Processes;
            return list with { Id = id, Processes = processes };
        }
    }

    private ProcessListResponse BuildList(int max)
    {
        var applied = new Dictionary<int, List<string>>();
        foreach (var e in _safety.Journal.Snapshot())
        {
            if (!applied.TryGetValue(e.Change.TargetPid, out var kinds))
            {
                applied[e.Change.TargetPid] = kinds = [];
            }
            kinds.Add(e.Change.Kind + (e.IsAutomatic ? "*" : ""));
        }

        var count = Math.Min(max, _samples.Count);
        var entries = new ProcessEntry[count];
        for (var i = 0; i < count; i++)
        {
            var s = _samples[i];
            var controllable = ProcessPolicy.CanControl(s.Pid, s.Name, s.SessionId, _selfPid, out var reason);
            entries[i] = new ProcessEntry
            {
                Pid = s.Pid,
                CreateTime = s.CreateTime,
                Name = s.Name,
                CpuPercent = Math.Round(s.CpuPercent, 1),
                WorkingSetMb = Math.Round(s.WorkingSetBytes / 1048576.0, 1),
                PrivateMb = Math.Round(s.PrivateBytes / 1048576.0, 1),
                Priority = ProcessTarget.PriorityNameFromBase(s.BasePriority),
                SessionId = s.SessionId,
                Controllable = controllable,
                Reason = controllable ? null : reason,
                Applied = applied.TryGetValue(s.Pid, out var k) ? string.Join(", ", k) : "",
            };
        }
        int? fg = ForegroundKnown(Environment.TickCount64) ? _foregroundPid : null;
        return new ProcessListResponse { Id = 0, Processes = entries, ForegroundPid = fg, TotalProcesses = _samples.Count };
    }

    /// <summary>
    /// Applies one process action under its tuning contract (F5). <paramref name="owner"/> is
    /// "manual" for user clicks; the memory governor and experiments pass their own owner and
    /// are rate-limited as automatic actions.
    /// </summary>
    public ActionResultResponse ApplyAction(ProcessActionRequest r, long nowMs, string owner = ChangeOwners.Manual)
    {
        var automatic = owner != ChangeOwners.Manual;
        lock (_gate)
        {
            var name = NameOf(r.Pid, r.CreateTime, out var session);
            if (name is null)
            {
                return Result(r.Id, false, "Process not found (it may have exited). Refresh the list.");
            }
            if (!ProcessPolicy.CanControl(r.Pid, name, session, _selfPid, out var reason))
            {
                return Result(r.Id, false, $"{name} is not changeable: {reason}.");
            }
            var contract = PerfTuner.Core.Autonomy.TuningContracts.ForAction(r.Action, r.Priority, r.Cores);
            if (!PerfTuner.Core.Autonomy.TuningContracts.Allows(_state.Profile, contract))
            {
                return Result(r.Id, false, $"Contract '{contract.Name}' needs the {contract.MinimumProfile} profile (active: {_state.Profile}).");
            }
            var reversible = r.Action is not (ProcessAction.TrimMemory or ProcessAction.EndProcess);
            if (reversible && !_safety.AllowsTuning)
            {
                return Result(r.Id, false, $"Tuning is paused by the circuit breaker ({_safety.State}). Reset it on the Changes tab once the system is stable.");
            }
            if (!_throttle.TryAcquire(r.Pid, automatic, nowMs))
            {
                return Result(r.Id, false, "Too fast — wait 2 seconds between actions on the same process.");
            }

            string error = string.Empty;
            IReversibleChange? change = null;
            switch (r.Action)
            {
                case ProcessAction.SetPriority:
                    RevertExisting(r.Pid, PriorityChange.KindName);
                    RevertExisting(r.Pid, EfficiencyModeChange.KindName); // efficiency also sets priority
                    change = PriorityChange.TryApply(r.Pid, r.CreateTime, name, r.Priority!.Value, out error);
                    break;
                case ProcessAction.EfficiencyMode:
                    RevertExisting(r.Pid, EfficiencyModeChange.KindName);
                    if (r.Enable == false)
                    {
                        return Result(r.Id, true, $"Efficiency mode off for {name}.");
                    }
                    RevertExisting(r.Pid, PriorityChange.KindName);
                    change = EfficiencyModeChange.TryApply(r.Pid, r.CreateTime, name, out error);
                    break;
                case ProcessAction.CpuCores:
                    RevertExisting(r.Pid, CpuSetChange.KindName);
                    change = CpuSetChange.TryApply(r.Pid, r.CreateTime, name, _topology.Resolve(r.Cores!.Value), Label(r.Cores.Value), out error);
                    break;
                case ProcessAction.MemoryPriority:
                    RevertExisting(r.Pid, MemoryPriorityChange.KindName);
                    if (r.MemoryPriority == MemoryPriorityLevel.Normal)
                    {
                        return Result(r.Id, true, $"Memory priority of {name} restored.");
                    }
                    change = MemoryPriorityChange.TryApply(r.Pid, r.CreateTime, name, r.MemoryPriority!.Value, out error);
                    break;
                case ProcessAction.SoftMemoryCap:
                    RevertExisting(r.Pid, WorkingSetCapChange.KindName);
                    change = WorkingSetCapChange.TryApply(r.Pid, r.CreateTime, name, r.CapMb!.Value, out error);
                    break;
                case ProcessAction.TrimMemory:
                    var trimmed = ProcessOneShots.TrimWorkingSet(r.Pid, r.CreateTime, out error);
                    if (trimmed) { AddHistory($"Manual: trimmed memory of {name} ({r.Pid})"); }
                    return Result(r.Id, trimmed, trimmed ? $"Trimmed memory of {name}. Pages it still uses come back on demand." : error);
                case ProcessAction.EndProcess:
                    var ended = ProcessOneShots.Terminate(r.Pid, r.CreateTime, out error);
                    if (ended)
                    {
                        AddHistory($"Manual: ended process {name} ({r.Pid}) — irreversible");
                        LogEndProcess(name, r.Pid);
                    }
                    return Result(r.Id, ended, ended ? $"Ended {name}." : error);
            }

            if (change is null)
            {
                return Result(r.Id, false, $"Could not change {name}: {error}");
            }
            var changeId = Record(change, owner, SourceName(owner));
            return changeId < 0
                ? Result(r.Id, false, "Too many active changes (256). Revert some first.")
                : new ActionResultResponse { Id = r.Id, Success = true, Message = change.Description, ChangeId = changeId };
        }
    }

    private void RevertExisting(int pid, string kind)
    {
        foreach (var e in _safety.Journal.Snapshot())
        {
            if (e.Change.TargetPid == pid && e.Change.Kind == kind)
            {
                _safety.Journal.Revert(e.Id);
            }
        }
    }

    public ActionResultResponse Revert(uint id, long changeId)
    {
        lock (_gate)
        {
            var result = _safety.Journal.Revert(changeId);
            if (result is null)
            {
                return Result(id, false, "That change is no longer active.");
            }
            var (outcome, description) = result.Value;
            AddHistory($"Reverted ({outcome}): {description}");
            return Result(id, outcome != RevertOutcome.Failed, outcome switch
            {
                RevertOutcome.Reverted => $"Reverted: {description}",
                RevertOutcome.TargetGone => "The process had already exited; nothing to undo.",
                _ => $"Revert failed: {description}",
            });
        }
    }

    public ActionResultResponse RevertAll(uint id)
    {
        lock (_gate)
        {
            var report = _safety.Journal.RollbackAll();
            var msg = $"Reverted {report.Reverted} change(s); {report.TargetGone} had already exited; {report.Failed} failed.";
            AddHistory("Revert all: " + msg);
            return Result(id, report.Failed == 0, msg);
        }
    }

    public ActionResultResponse TrimBackground(uint id, long nowMs) => TrimBackground(id, nowMs, "Manual", out _);

    /// <summary>
    /// RAM Refresh: asks every background app (not the foreground one, not protected processes) to
    /// hand back unused working-set pages, then measures how much RAM was actually released.
    /// </summary>
    public ActionResultResponse TrimBackground(uint id, long nowMs, string source, out long freedBytes)
    {
        lock (_gate)
        {
            _snapshotter.Snapshot(_samples);
            _lastSnapshotMs = nowMs;
            int? fg = ForegroundKnown(nowMs) ? _foregroundPid : null;
            var trimmed = new List<(int Pid, long CreateTime, long Before)>();
            foreach (var s in _samples)
            {
                if (s.Pid == fg || s.WorkingSetBytes < 32L * 1024 * 1024 ||
                    !ProcessPolicy.CanControl(s.Pid, s.Name, s.SessionId, _selfPid, out _))
                {
                    continue;
                }
                if (ProcessOneShots.TrimWorkingSet(s.Pid, s.CreateTime, out _))
                {
                    trimmed.Add((s.Pid, s.CreateTime, s.WorkingSetBytes));
                }
            }
            freedBytes = 0;
            foreach (var (pid, create, before) in trimmed)
            {
                if (ProcessMeter.WorkingSetBytes(pid, create) is { } after && after < before)
                {
                    freedBytes += before - after;
                }
            }
            var msg = trimmed.Count == 0
                ? "Nothing to free: no background app holds a meaningful amount of RAM."
                : $"Freed {FormatBytes(freedBytes)} of RAM from {trimmed.Count} background app(s). Pages still needed come back on demand.";
            AddHistory($"{source}: {msg}");
            return Result(id, true, msg);
        }
    }

    private static string FormatBytes(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):F1} GB" : $"{b / (1L << 20)} MB";

    public ActionResultResponse SetOptions(SetTuningOptionsRequest r)
    {
        lock (_gate)
        {
            var next = _state with
            {
                SafeMode = r.SafeMode ?? _state.SafeMode,
                Profile = r.SafeMode switch
                {
                    true => TuningProfile.Safe,
                    false when _state.Profile == TuningProfile.Safe => TuningProfile.Balanced,
                    _ => _state.Profile,
                },
                Mode = r.Mode ?? _state.Mode,
                AutoCalm = r.AutoCalm ?? _state.AutoCalm,
                AutoCalmThresholdPercent = r.AutoCalmThresholdPercent ?? _state.AutoCalmThresholdPercent,
            };
            _state = next;
            SaveState();
            // Switching features off takes effect immediately, not at the next tick.
            _safety.Journal.RollbackWhere(e =>
                (next.SafeMode && e.IsAutomatic && e.Owner != ChangeOwners.Experiment)
                || (ChangeOwners.IsMode(e.Owner) && next.Mode == TuningMode.Off)
                || (e.Owner == ChangeOwners.AutoCalm && !next.AutoCalm));
            var msg = $"Safe Mode {(next.SafeMode ? "on" : "off")}, mode {next.Mode}, auto-calm {(next.AutoCalm ? $"on (≥ {next.AutoCalmThresholdPercent}% CPU)" : "off")}.";
            AddHistory("Settings: " + msg);
            return Result(r.Id, true, msg + (next.SafeMode && (next.Mode != TuningMode.Off || next.AutoCalm) ? " Automatic features wait until Safe Mode is off." : ""));
        }
    }

    public ActionResultResponse SetRules(uint id, AppRule[] rules)
    {
        lock (_gate)
        {
            _state = _state with { Rules = [.. rules] };
            SaveState();
            var names = new HashSet<string>(rules.Select(x => ChangeOwners.RulePrefix + x.ImageName), StringComparer.OrdinalIgnoreCase);
            _safety.Journal.RollbackWhere(e => e.Owner.StartsWith(ChangeOwners.RulePrefix, StringComparison.Ordinal) && !names.Contains(e.Owner));
            AddHistory($"Settings: {rules.Length} app rule(s) saved");
            return Result(id, true, $"{rules.Length} app rule(s) saved." + (_state.SafeMode ? " Rules apply once Safe Mode is off." : ""));
        }
    }

    public void ReportForeground(int pid, long nowMs)
    {
        lock (_gate)
        {
            _foregroundPid = pid;
            Volatile.Write(ref _foregroundAtMs, nowMs);
        }
    }

    public ActionResultResponse ResetBreaker(uint id)
    {
        _safety.ResetBreaker();
        AddHistory("Circuit breaker reset by administrator");
        return Result(id, true, "Circuit breaker reset. Tuning is allowed again.");
    }

    public TuningProfile Profile
    {
        get { lock (_gate) { return _state.Profile; } }
    }

    /// <summary>Switches profile; Safe also enables Safe Mode (and reverts automatic changes).</summary>
    public ActionResultResponse SetProfile(uint id, TuningProfile profile)
    {
        var result = SetOptions(new SetTuningOptionsRequest { Id = id, SafeMode = profile == TuningProfile.Safe });
        lock (_gate)
        {
            _state = _state with { Profile = profile, SafeMode = profile == TuningProfile.Safe };
            SaveState();
        }
        AddHistory($"Profile → {profile}");
        return result with { Message = $"Profile {profile}. " + result.Message };
    }

    public ContractsResponse GetContracts(uint id) => new()
    {
        Id = id,
        ActiveProfile = Profile,
        Contracts = [.. PerfTuner.Core.Autonomy.TuningContracts.All.Select(c => c.ToView())],
    };

    /// <summary>Active changes with their time, for "Explain this stall".</summary>
    public List<(long TimeUnixMs, string Description)> RecentChanges(long sinceUnixMs)
        => [.. _safety.Journal.Snapshot().Where(e => e.CreatedUnixMs >= sinceUnixMs).Select(e => (e.CreatedUnixMs, e.Change.Description))];

    /// <summary>Reverts every change owned by <paramref name="owner"/>.</summary>
    public int RevertOwner(string owner)
    {
        lock (_gate)
        {
            var report = _safety.Journal.RollbackWhere(e => e.Owner == owner);
            return report.Reverted + report.TargetGone;
        }
    }

    public bool IsChangeActive(long changeId) => _safety.Journal.Snapshot().Any(e => e.Id == changeId);

    public void Note(string message) => AddHistory(message);

    public ChangesResponse GetChanges(uint id)
    {
        var active = _safety.Journal.Snapshot()
            .Select(e => new ChangeView { Id = e.Id, Kind = e.Change.Kind, Description = e.Change.Description, Owner = e.Owner, CreatedUnixMs = e.CreatedUnixMs })
            .ToArray();
        HistoryEntry[] history;
        lock (_history)
        {
            history = [.. _history.Reverse()];
        }
        return new ChangesResponse { Id = id, Active = active, History = history };
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>Name of a controllable process, or null with a reason (used by experiments).</summary>
    public string? DescribeControllable(int pid, long createTime, out string reason)
    {
        lock (_gate)
        {
            var name = NameOf(pid, createTime, out var session);
            if (name is null)
            {
                reason = "Process not found (it may have exited).";
                return null;
            }
            if (!ProcessPolicy.CanControl(pid, name, session, _selfPid, out var why))
            {
                reason = $"{name} is not changeable: {why}.";
                return null;
            }
            reason = "";
            return name;
        }
    }

    private string? NameOf(int pid, long create, out int session)
    {
        foreach (var s in _samples)
        {
            if (s.Pid == pid && s.CreateTime == create)
            {
                session = s.SessionId;
                return s.Name;
            }
        }
        _snapshotter.Snapshot(_samples);
        _samples.Sort(static (a, b) => b.CpuPercent.CompareTo(a.CpuPercent));
        foreach (var s in _samples)
        {
            if (s.Pid == pid && s.CreateTime == create)
            {
                session = s.SessionId;
                return s.Name;
            }
        }
        session = 0;
        return null;
    }

    private static string Label(CpuCorePreset preset) => preset switch
    {
        CpuCorePreset.EfficiencyCores => "efficiency cores only",
        CpuCorePreset.PerformanceCores => "performance cores only",
        _ => "half of the cores",
    };

    private void AddHistory(string message)
    {
        lock (_history)
        {
            if (message.Length > 200) { message = message[..200]; }
            _history.Enqueue(new HistoryEntry { TimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Message = message });
            while (_history.Count > ProtocolConstants.MaxHistoryEntries) { _history.Dequeue(); }
        }
        LogHistory(message);
        HistoryAdded?.Invoke(message);
    }

    /// <summary>Every tuning action / decision (for the causality timeline). Handlers must be fast and lock-free w.r.t. this engine.</summary>
    public event Action<string>? HistoryAdded;

    private static ActionResultResponse Result(uint id, bool ok, string message) => new() { Id = id, Success = ok, Message = message };

    private void SaveState()
    {
        try
        {
            var tmp = _stateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, TuningStateJsonContext.Default.TuningState));
            File.Move(tmp, _stateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save tuning state");
        }
    }

    private static TuningState LoadState(string path, bool defaultSafeMode)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length <= 64 * 1024)
            {
                var state = JsonSerializer.Deserialize(File.ReadAllText(path), TuningStateJsonContext.Default.TuningState);
                if (TuningState.IsValid(state, out _))
                {
                    // Files from v0.2/v0.3 have no profile: derive it from Safe Mode.
                    if (state!.SafeMode) { state.Profile = TuningProfile.Safe; }
                    else if (state.Profile == TuningProfile.Safe) { state.Profile = TuningProfile.Balanced; }
                    return state;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new TuningState { SafeMode = defaultSafeMode };
    }

    public void Dispose() => _snapshotter.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Tuning: {Message}")]
    private partial void LogHistory(string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Automatic action on {Name} ({Pid}) skipped: {Error}")]
    private partial void LogAutoFailed(string name, int pid, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Process {Name} ({Pid}) ended by administrator request")]
    private partial void LogEndProcess(string name, int pid);
}
