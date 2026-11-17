using PerfTuner.Contracts;
using PerfTuner.Core.Safety;

namespace PerfTuner.Core.Tuning;

/// <summary>A process as the planner sees it (already filtered through <see cref="ProcessPolicy"/>).</summary>
public readonly record struct ProcView(int Pid, long CreateTime, string Name, double CpuPercent, bool Controllable);

/// <summary>An active change as the planner sees it.</summary>
public readonly record struct ManagedView(long Id, int Pid, string Owner);

public enum PlanKind
{
    /// <summary>EcoQoS + BelowNormal (Windows "efficiency mode").</summary>
    ApplyEfficiency,
    /// <summary>AboveNormal priority for the foreground app.</summary>
    ApplyBoost,
    ApplyRule,
    Revert,
}

public readonly record struct PlannedAction(PlanKind Kind, long ChangeId, ProcView Process, string Owner, AppRule? Rule);

public readonly record struct PlanInput(
    long NowMs,
    TuningState State,
    IReadOnlyList<ProcView> Processes,
    int? ForegroundPid,
    IReadOnlyList<ManagedView> Managed,
    bool TuningAllowed);

/// <summary>
/// Pure decision logic for the automatic features (Focus / Quiet modes, Auto-calm, app rules).
/// It never touches the OS: it turns a snapshot into a list of intended actions, which the
/// service executes through the journal. That keeps every heuristic unit-testable.
/// <para>Invariants:</para>
/// <list type="bullet">
/// <item>Reverting is always allowed; applying requires Safe Mode off AND breaker closed.</item>
/// <item>Background throttling needs a known foreground app — if we cannot tell what the user
///   is looking at, we do not throttle anything new (we might hit the game they are playing).</item>
/// <item>The moment a throttled app becomes the foreground app, its throttle is released.</item>
/// <item>Caps bound how many processes a mode may manage at once.</item>
/// </list>
/// </summary>
public sealed class AutomationPlanner
{
    public const double FocusBackgroundCpuPercent = 2.0;
    public const double QuietBackgroundCpuPercent = 1.0;
    public const int FocusCap = 32;
    public const int QuietCap = 48;
    public const long AutoCalmSustainMs = 30_000;
    public const long AutoCalmReleaseMs = 120_000;

    private readonly Dictionary<int, long> _hotSince = [];
    private readonly Dictionary<int, long> _coolSince = [];

    public List<PlannedAction> Plan(in PlanInput input)
    {
        var actions = new List<PlannedAction>();
        var state = input.State;
        var fg = input.ForegroundPid;
        var byPid = new Dictionary<int, ProcView>(input.Processes.Count);
        foreach (var p in input.Processes)
        {
            byPid[p.Pid] = p;
        }

        var managedPids = new HashSet<int>();
        var reverted = new HashSet<long>();
        void Revert(ManagedView m)
        {
            if (reverted.Add(m.Id))
            {
                actions.Add(new PlannedAction(PlanKind.Revert, m.Id, default, m.Owner, null));
            }
        }

        foreach (var m in input.Managed)
        {
            // Manual changes and user-started experiments are never touched by the planner.
            var automatic = m.Owner is not (ChangeOwners.Manual or ChangeOwners.Experiment);
            if (!automatic)
            {
                managedPids.Add(m.Pid);
                continue;
            }

            var isRule = m.Owner.StartsWith(ChangeOwners.RulePrefix, StringComparison.Ordinal);
            if (state.SafeMode
                || (ChangeOwners.IsMode(m.Owner) && ModeOwnerObsolete(m.Owner, state.Mode))
                || (m.Owner == ChangeOwners.AutoCalm && !state.AutoCalm)
                || (isRule && !HasRule(state, m.Owner[ChangeOwners.RulePrefix.Length..])))
            {
                Revert(m);
                continue;
            }

            // Release throttles on the app the user just switched to.
            if (fg == m.Pid && m.Owner is ChangeOwners.FocusBackground or ChangeOwners.Quiet or ChangeOwners.AutoCalm or ChangeOwners.Governor)
            {
                Revert(m);
                continue;
            }

            // Foreground boost follows the foreground app.
            if (m.Owner == ChangeOwners.FocusForeground && fg is not null && fg != m.Pid)
            {
                Revert(m);
                continue;
            }

            managedPids.Add(m.Pid);
        }

        var allowNew = input.TuningAllowed && !state.SafeMode;

        // ---- app rules (apply even to the foreground app: the user asked for it explicitly)
        if (allowNew && state.Rules.Count > 0)
        {
            foreach (var p in input.Processes)
            {
                if (!p.Controllable || managedPids.Contains(p.Pid))
                {
                    continue;
                }
                var rule = FindRule(state, p.Name);
                if (rule is not null)
                {
                    actions.Add(new PlannedAction(PlanKind.ApplyRule, 0, p, ChangeOwners.RulePrefix + rule.ImageName, rule));
                    managedPids.Add(p.Pid);
                }
            }
        }

        // ---- modes
        if (allowNew && fg is { } foreground && state.Mode != TuningMode.Off)
        {
            if (state.Mode == TuningMode.Focus && byPid.TryGetValue(foreground, out var fgProc) && fgProc.Controllable && !managedPids.Contains(foreground))
            {
                actions.Add(new PlannedAction(PlanKind.ApplyBoost, 0, fgProc, ChangeOwners.FocusForeground, null));
                managedPids.Add(foreground);
            }

            var owner = state.Mode == TuningMode.Focus ? ChangeOwners.FocusBackground : ChangeOwners.Quiet;
            var threshold = state.Mode == TuningMode.Focus ? FocusBackgroundCpuPercent : QuietBackgroundCpuPercent;
            var cap = state.Mode == TuningMode.Focus ? FocusCap : QuietCap;
            var current = 0;
            foreach (var m in input.Managed)
            {
                if (m.Owner == owner && !reverted.Contains(m.Id)) { current++; }
            }
            foreach (var p in input.Processes) // caller supplies them sorted by CPU, busiest first
            {
                if (current >= cap) { break; }
                if (!p.Controllable || p.Pid == foreground || managedPids.Contains(p.Pid) || p.CpuPercent < threshold)
                {
                    continue;
                }
                actions.Add(new PlannedAction(PlanKind.ApplyEfficiency, 0, p, owner, null));
                managedPids.Add(p.Pid);
                current++;
            }
        }

        // ---- auto-calm: sustained background hogs
        PlanAutoCalm(input, byPid, managedPids, allowNew, actions, Revert);

        return actions;
    }

    private void PlanAutoCalm(in PlanInput input, Dictionary<int, ProcView> byPid, HashSet<int> managedPids, bool allowNew,
        List<PlannedAction> actions, Action<ManagedView> revert)
    {
        var state = input.State;
        var threshold = (double)state.AutoCalmThresholdPercent;

        // Forget trackers for processes that went away.
        foreach (var pid in _hotSince.Keys.Where(pid => !byPid.ContainsKey(pid)).ToList()) { _hotSince.Remove(pid); }
        foreach (var pid in _coolSince.Keys.Where(pid => !byPid.ContainsKey(pid)).ToList()) { _coolSince.Remove(pid); }

        if (!state.AutoCalm)
        {
            _hotSince.Clear();
            _coolSince.Clear();
            return;
        }

        // Release calmed processes that have stayed quiet long enough.
        foreach (var m in input.Managed)
        {
            if (m.Owner != ChangeOwners.AutoCalm || !byPid.TryGetValue(m.Pid, out var p))
            {
                continue;
            }
            if (p.CpuPercent < threshold / 2)
            {
                if (!_coolSince.TryGetValue(m.Pid, out var since))
                {
                    _coolSince[m.Pid] = input.NowMs;
                }
                else if (input.NowMs - since >= AutoCalmReleaseMs)
                {
                    revert(m);
                    _coolSince.Remove(m.Pid);
                }
            }
            else
            {
                _coolSince.Remove(m.Pid);
            }
        }

        if (!allowNew || input.ForegroundPid is not { } fg)
        {
            return;
        }

        foreach (var p in input.Processes)
        {
            if (!p.Controllable || p.Pid == fg || managedPids.Contains(p.Pid))
            {
                _hotSince.Remove(p.Pid);
                continue;
            }
            if (p.CpuPercent >= threshold)
            {
                if (!_hotSince.TryGetValue(p.Pid, out var since))
                {
                    _hotSince[p.Pid] = input.NowMs;
                }
                else if (input.NowMs - since >= AutoCalmSustainMs)
                {
                    actions.Add(new PlannedAction(PlanKind.ApplyEfficiency, 0, p, ChangeOwners.AutoCalm, null));
                    managedPids.Add(p.Pid);
                    _hotSince.Remove(p.Pid);
                }
            }
            else
            {
                _hotSince.Remove(p.Pid);
            }
        }
    }

    private static bool ModeOwnerObsolete(string owner, TuningMode mode) => owner switch
    {
        ChangeOwners.FocusForeground or ChangeOwners.FocusBackground => mode != TuningMode.Focus,
        ChangeOwners.Quiet => mode != TuningMode.Quiet,
        _ => false,
    };

    private static bool HasRule(TuningState state, string imageName) => FindRule(state, imageName) is not null;

    private static AppRule? FindRule(TuningState state, string imageName)
    {
        foreach (var r in state.Rules)
        {
            if (string.Equals(r.ImageName, imageName, StringComparison.OrdinalIgnoreCase))
            {
                return r;
            }
        }
        return null;
    }
}
