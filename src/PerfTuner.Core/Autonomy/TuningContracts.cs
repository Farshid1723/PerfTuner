using PerfTuner.Contracts;

namespace PerfTuner.Core.Autonomy;

public enum KpiKind
{
    None,
    TargetCpuPercent,
    TargetWorkingSetMb,
    SystemHardFaultsPerSec,
    SystemStalls,
    DiskP99Ms,
}

/// <summary>
/// A safe tuning contract (F5): the ONLY way the service may change system behaviour.
/// Every action — manual, automatic, governor or experiment — must map to a contract whose
/// minimum profile is active; bounds, rate limits and rollback are enforced by the engine,
/// abort conditions by the circuit breaker and the experiment runner.
/// </summary>
public sealed record TuningContract(
    string Id,
    string Name,
    string Scope,
    string Bounds,
    string RateLimit,
    string Reversibility,
    string[] AbortConditions,
    string ExpectedOutcome,
    TuningProfile MinimumProfile,
    bool Experimentable,
    ProcessAction? Action,
    KpiKind PrimaryKpi,
    double MinEffectPercent,
    bool AcceptIfNeutral)
{
    public TuningContractView ToView() => new()
    {
        Id = Id, Name = Name, Scope = Scope, Bounds = Bounds, RateLimit = RateLimit, Reversibility = Reversibility,
        AbortConditions = AbortConditions, ExpectedOutcome = ExpectedOutcome, MinimumProfile = MinimumProfile, Experimentable = Experimentable,
    };
}

public static class TuningContracts
{
    private static readonly string[] CommonAbort =
    [
        "Circuit breaker trips (sustained CPU ≥ 97 %, memory ≥ 95 %, commit ≥ 95 %, collector faults)",
        "Target process exits or its identity (PID + creation time) changes",
        "Service stops (all changes rolled back)",
    ];

    public static IReadOnlyList<TuningContract> All { get; } =
    [
        new("priority-below", "Lower priority (Below normal)", "One user-session process", "Normal → Below normal only",
            "1 per process / 2 s manual; 1 per process / 30 s and 20 / min automatic", "Exact: previous priority class restored",
            CommonAbort, "Foreground apps win CPU contention against this process; stalls not higher.",
            TuningProfile.Safe, true, ProcessAction.SetPriority, KpiKind.SystemStalls, 0, true),

        new("priority-above", "Raise priority (Above normal)", "One user-session process (foreground)", "Normal → Above normal; never High/Realtime",
            "Same as priority-below", "Exact: previous priority class restored",
            CommonAbort, "The boosted app gets CPU first under contention.",
            TuningProfile.Safe, false, ProcessAction.SetPriority, KpiKind.None, 0, true),

        new("efficiency-mode", "Efficiency mode (EcoQoS)", "One user-session process", "EcoQoS on + Below normal priority",
            "Same as priority-below", "Exact: power-throttling state and priority restored",
            CommonAbort, "Target uses ≥ 10 % less CPU; runs on efficient cores / low clocks.",
            TuningProfile.Safe, true, ProcessAction.EfficiencyMode, KpiKind.TargetCpuPercent, 10, false),

        new("cpu-efficiency-cores", "Confine to efficiency cores", "One user-session process (hybrid CPUs)", "≥ 2 logical CPUs; CPU Sets (soft)",
            "Same as priority-below", "Exact: previous CPU Sets restored",
            CommonAbort, "Performance cores stay free for foreground work; stalls not higher.",
            TuningProfile.Safe, true, ProcessAction.CpuCores, KpiKind.SystemStalls, 0, true),

        new("memory-priority-low", "Low memory priority", "One user-session process", "MEMORY_PRIORITY_LOW (2) only",
            "Same as priority-below", "Exact: previous memory priority restored",
            CommonAbort, "Its pages are reclaimed first; system hard faults not higher.",
            TuningProfile.Safe, true, ProcessAction.MemoryPriority, KpiKind.SystemHardFaultsPerSec, 0, true),

        new("soft-memory-cap", "Soft working-set cap", "One user-session process", "64 MB – 64 GB; soft limit only (never a hard cap)",
            "Same as priority-below", "Exact: previous min/max and flags restored",
            CommonAbort, "Target working set ≥ 15 % smaller without more hard faults.",
            TuningProfile.Safe, true, ProcessAction.SoftMemoryCap, KpiKind.TargetWorkingSetMb, 15, false),

        new("trim-memory", "Trim working set (one-shot)", "One user-session process", "EmptyWorkingSet once",
            "1 per process / 2 s", "Not needed: pages fault back on use", CommonAbort,
            "Frees RAM held but unused.", TuningProfile.Safe, false, ProcessAction.TrimMemory, KpiKind.None, 0, true),

        new("focus-mode", "Focus / Game mode", "System-safe subset: user-session processes only", "Boost 1 app; ≤ 32 background apps in efficiency mode",
            "20 automatic actions / min", "Exact per process; all reverted when mode is switched off",
            CommonAbort, "Foreground app gets priority; background CPU drops.", TuningProfile.Balanced, false, null, KpiKind.None, 0, true),

        new("quiet-mode", "Quiet mode", "System-safe subset: user-session processes only", "≤ 48 background apps in efficiency mode",
            "20 automatic actions / min", "Exact per process", CommonAbort, "Lower background CPU, heat and noise.",
            TuningProfile.Balanced, false, null, KpiKind.None, 0, true),

        new("auto-calm", "Auto-calm CPU hogs", "System-safe subset: user-session processes only", "Threshold 5–100 %; sustained 30 s; released after 2 min",
            "1 per process / 30 s", "Exact per process", CommonAbort, "Background hogs stop starving the foreground app.",
            TuningProfile.Balanced, false, null, KpiKind.None, 0, true),

        new("memory-governor", "Memory governor", "System-safe subset: ≤ 3 largest background apps", "Low memory priority; ≤ 1 trim / 5 min; only under Pressure/Thrashing",
            "1 decision / min", "Exact per process; released when memory is Stable for 5 min", CommonAbort,
            "Fewer hard faults for the foreground app under memory pressure.", TuningProfile.Balanced, false, null, KpiKind.None, 0, true),

        new("app-rules", "Per-app rules", "Named user-session apps", "Actions of the contracts above", "1 per process / 30 s",
            "Exact per process", CommonAbort, "Your chosen settings follow the app across restarts.", TuningProfile.Balanced, false, null, KpiKind.None, 0, true),

        new("autopilot", "Regression autopilot (autonomous experiments)", "One experimentable contract at a time", "≤ 1 experiment / 30 min; 30 s + 30 s windows",
            "1 / 30 min", "Automatic rollback unless the expected outcome is proven", CommonAbort,
            "Only changes that measurably help are kept.", TuningProfile.Advanced, false, null, KpiKind.None, 0, true),
    ];

    public static TuningContract? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>Maps a manual process action to its contract (priority level decides below/above).</summary>
    public static TuningContract ForAction(ProcessAction action, PriorityLevel? priority, CpuCorePreset? cores) => action switch
    {
        ProcessAction.SetPriority when priority == PriorityLevel.AboveNormal => Find("priority-above")!,
        ProcessAction.SetPriority => Find("priority-below")!,
        ProcessAction.EfficiencyMode => Find("efficiency-mode")!,
        ProcessAction.CpuCores => Find("cpu-efficiency-cores")!,
        ProcessAction.MemoryPriority => Find("memory-priority-low")!,
        ProcessAction.SoftMemoryCap => Find("soft-memory-cap")!,
        ProcessAction.TrimMemory => Find("trim-memory")!,
        _ => new TuningContract("end-process", "End process (irreversible)", "One user-session process", "Explicit confirmation required",
            "1 per process / 2 s", "NOT reversible", CommonAbort, "Process terminated.", TuningProfile.Safe, false, action, KpiKind.None, 0, true),
    };

    public static bool Allows(TuningProfile active, TuningContract contract) => active >= contract.MinimumProfile;
}

/// <summary>
/// Experiment decision (F4): accept only when the contract's expected outcome is PROVEN on its
/// primary KPI (or the contract accepts a neutral result) AND no guard KPI regressed.
/// Guards: stalls, disk p99, hard faults — each may not worsen by more than 25 % beyond a floor.
/// </summary>
public static class ExperimentEvaluator
{
    public static (bool Accept, string Decision) Evaluate(TuningContract contract, IReadOnlyList<KpiValue> kpis)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(kpis);

        foreach (var guard in kpis.Where(k => k.Name is "System stalls" or "Disk p99" or "Hard faults/s"))
        {
            var floor = guard.Name switch { "System stalls" => 1, "Disk p99" => 20, _ => 100 };
            if (guard.After > floor && guard.After > guard.Before * 1.25)
            {
                return (false, $"Rolled back: {guard.Name} got worse ({guard.Before:0.#} → {guard.After:0.#}{(guard.Unit.Length > 0 ? " " + guard.Unit : "")}).");
            }
        }

        var primaryName = PrimaryName(contract.PrimaryKpi);
        var primary = kpis.FirstOrDefault(k => k.Name == primaryName);
        if (primary is null || contract.PrimaryKpi == KpiKind.None)
        {
            return contract.AcceptIfNeutral
                ? (true, "Kept: no guard metric got worse.")
                : (false, "Rolled back: the expected outcome could not be measured.");
        }

        var change = primary.Before <= 0 ? (primary.After > 0 ? 100 : 0) : 100 * (primary.After - primary.Before) / primary.Before;
        var improvement = primary.LowerIsBetter ? -change : change;
        var values = $"{primary.Before:0.#} → {primary.After:0.#}{(primary.Unit.Length > 0 ? " " + primary.Unit : "")}";
        if (improvement >= Math.Max(1, contract.MinEffectPercent))
        {
            return (true, $"Kept: {primary.Name} improved {improvement:F0}% ({values}); no guard metric got worse.");
        }
        if (contract.AcceptIfNeutral && Math.Abs(improvement) < 10)
        {
            return (true, $"Kept: {primary.Name} unchanged ({values}) and no guard metric got worse.");
        }
        return (false, $"Rolled back: expected {primary.Name} to improve ≥ {contract.MinEffectPercent:F0}%, measured {improvement:F0}% ({values}).");
    }

    public static string PrimaryName(KpiKind kind) => kind switch
    {
        KpiKind.TargetCpuPercent => "Target CPU",
        KpiKind.TargetWorkingSetMb => "Target working set",
        KpiKind.SystemHardFaultsPerSec => "Hard faults/s",
        KpiKind.SystemStalls => "System stalls",
        KpiKind.DiskP99Ms => "Disk p99",
        _ => "",
    };
}
