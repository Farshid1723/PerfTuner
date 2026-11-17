# Safety policy

## Non-negotiables (enforced by design, not by configuration)

- User mode only: **no drivers**, no raw disk access, no registry-wide or boot-configuration changes, no power-plan or network-stack rewrites.
- Every tuning action must be **scoped** (one process), **bounded** (allow-listed values), **reversible** (original value captured first), **journaled** (before it takes effect), and **rate-limited**.
- **Safe Mode is on by default**: monitoring plus minimal reversible actions only.
- If an API's safety is uncertain, the feature stays monitoring-only.

## Tuning scope

| Capability | Milestone | API (documented, user-mode) | Bounds | Revert |
|---|---|---|---|---|
| Monitoring toggles (per-process IO, interval) | **M1 (done)** | internal | interval 500–2000 ms | runtime only; never persisted |

> **v0.2 status:** the process-level actions below are implemented as specified (see `FEATURES.md`), with one refinement: CPU confinement uses **CPU Sets** with a floor of 2 logical processors instead of affinity masks. Persistent *system settings* (power plan, processor power, visual effects, transparency, startup apps) are applied by the elevated GUI through documented APIs and recorded in a per-user settings journal. Because they're deliberate, lasting choices, they aren't undone by a service stop or a breaker trip, only by Revert, Revert everything, or Uninstall.
| Process priority class | M2 | `SetPriorityClass` | Only `BelowNormal ↔ Normal ↔ AboveNormal`. **Never `High` or `Realtime`**, never on protected processes, never on `System`, `csrss`, `wininit`, `lsass`, `services`, `smss`, `dwm`, `winlogon`, `audiodg`, or anything in session 0 we don't own | restore captured class |
| Background-mode IO/memory priority for a process | M2 | `SetProcessInformation(ProcessMemoryPriority)`, `SetPriorityClass(PROCESS_MODE_BACKGROUND_BEGIN)` only on self-tagged helpers | memory priority ≥ `MEMORY_PRIORITY_LOW` | restore |
| CPU affinity (opt-in, per selected process) | M2 | `SetProcessAffinityMask` / CPU Sets (`SetProcessDefaultCpuSets`) | must leave ≥ 50 % of logical CPUs and ≥ 2 cores; never all P-cores removed on hybrid CPUs | restore captured mask |
| Working-set limit (opt-in) | M2 | `SetProcessWorkingSetSizeEx` with **soft** limits only (`QUOTA_LIMITS_HARDWS_*_DISABLE`) | min ≥ 16 MB; a hard cap is never used because it can thrash or hang the target | restore captured min/max + flags |
| EcoQoS / power throttling for background apps | M2 (candidate) | `SetProcessInformation(ProcessPowerThrottling)` | background, non-foreground apps only | clear the throttling flag |
| Network QoS / TCP tuning | **Deferred** | No documented, per-process, reversible user-mode API without admin-wide policy (QoS policies are machine-wide GPO/registry; `netsh` changes are global). Monitoring only. | — | — |
| Disk IO priority of *other* processes | **Deferred** | `NtSetInformationProcess(ProcessIoPriority)` is undocumented. Monitoring only. | — | — |

## Rate limits and guard rails (M2)

- At most **1 change per process per 30 s**, at most **10 changes per minute** overall, and **≤ 256 active changes** (journal capacity; a change that can't be journaled is reverted immediately).
- A heuristic only proposes a change after the condition has held for a sustain window (default 30 s), and must see improvement within an evaluation window or the change is reverted (closed-loop, no ratcheting).
- The foreground window's process is never de-prioritised.
- Changes are applied **only if** Safe Mode rules permit it, the circuit breaker is `Closed`, and the caller (for manual actions) is an elevated admin.

## Rollback: "last known safe state"

The last known safe state is simply **the system with an empty change journal**, because every change records how to undo itself.

1. **Journal before effect.** `ChangeJournal.TryRecord` holds an `IReversibleChange` that captured the original value and the target identity (**PID + process creation time**, so a recycled PID is never touched).
2. **Rollback order** is LIFO, which is always correct when changes stack on the same target. `Revert()` is idempotent and never throws. The result is `Reverted`, `TargetGone` (the process exited, so nothing is left to undo) or `Failed` (logged at Error).
3. **Triggers for full rollback:** breaker trip, service stop (including uninstall, OS shutdown, and GUI "Stop"), admin `revertAll` (M2), and a start after an unclean shutdown.
4. **Crash safety (M2):** the journal is persisted to `%ProgramData%\PerfTuner\state\journal.json` (hardened ACL) on every change. On start, if the `running.marker` from a previous run still exists, the service replays the persisted journal's reverts before doing anything else, then starts with the breaker **tripped**.
   - Note: priority, affinity and working-set settings are per-process and die with the process. A reboot is therefore always a complete rollback. Nothing is persisted system-wide.

## Circuit breaker

```
            trip condition                    cooldown elapsed
  Closed ───────────────────▶ Open ───────────────────────────▶ HalfOpen
    ▲                          ▲                                   │
    │  healthy for sustain     │  any stress while in probation     │
    └──────────────────────────┼────────────────────────────────────┘
                               │
         ≥ maxTripsPerHour ───▶ Latched  (tuning off until an admin resets)
```

| Trigger (instability signal) | Default | Config bounds |
|---|---|---|
| System CPU ≥ threshold, sustained | 97 % for 30 s | 50–100 %, 5–600 s |
| Memory load ≥ threshold, sustained | 95 % for 30 s | 50–99 % |
| Commit charge ≥ threshold, sustained | 95 % for 30 s | 50–99 % |
| Collector faults (sampling or ETW failures) in a rolling minute | 5 | 1–60 |
| Previous run ended uncleanly (marker present) | always | — |
| Run marker can't be written | always | — |
| *(M2)* A reverted change fails, a target process crashes within 60 s of a change, or a GUI-reported hang | always | — |

**Behaviour on trip:** tuning is disabled at once, the whole journal is rolled back synchronously *inside* the trip (before the next tick can apply anything), and the reason is logged and shown in the GUI. The breaker then cools down (300 s default), moves to probation (`HalfOpen`), and closes only after a full healthy sustain window. Any stress during probation re-trips it immediately. Three trips in an hour **latch** it, and it stays off until an admin resets it.

**Self-protection (M1, live):** the `OverheadGovernor` watches the service's own CPU over a rolling 30-sample window. Above the budget (0.5 %), it switches off per-process attribution (the most event-rate-sensitive work) and flags samples as `Degraded`. It restores detail only when usage is below half the budget (hysteresis). Memory above budget is logged.


## Profiles, contracts and experiments (v0.4)

- **Contracts.** Every action maps to a *tuning contract*: scope, bounds, rate limit, reversibility, abort conditions, expected outcome and minimum profile. This covers manual actions, modes, rules, the memory governor and experiments. `TuningEngine.ApplyAction` checks the contract against the active profile **before** any policy, rate-limit or breaker check. The catalog is fixed in code; there is no configuration surface that can add a contract.
- **Profiles.** **Safe** (default) = manual only. **Balanced** = plus automation and the memory governor. **Advanced** = plus autonomous experiments. Switching to Safe reverts every automatic change at once (governor and autopilot included). User-started experiments are an explicit request and are left alone.
- **Experiments.** A contract only counts as helping when it measurably improves its primary KPI and no guard KPI (stalls, disk p99, hard faults) regresses by more than 25 %. Otherwise the change is rolled back.
  - Experiments abort and roll back when the breaker trips, the target exits, or the user reverts the change.
  - Only one experiment runs at a time.
  - Autonomous experiments run at most once every 30 minutes, and never repeat on the same app within 6 h.
  - Every decision is written to a persisted audit trail (50 entries) and to the timeline.
- **Memory governor.** It acts on at most 3 background processes, and only under sustained pressure or thrashing. It does nothing on working-set churn, where trimming would hurt. It trims at most once every 5 minutes and releases everything after 60 s of stable memory.
- **Visibility tools.** Timeline, IO/network/wait-state views and the latency scan are read-only. The on-demand kernel traces use private sessions with bounded duration and memory.

## Space explorer (v0.8)

- Deleting goes to the Recycle Bin by default; "Delete permanently" needs a ticked "I understand this can't be undone" plus a confirmation.
- Moving uses Windows' folder picker; moves can be moved back from History, one operation at a time.
- File operations are the user's own housekeeping, not tuning: **Undo everything, the circuit breaker and uninstall never move or restore files.**
- Locked locations (see SECURITY.md) can't be selected for deletion or moving, with or without administrator rights.
- The scan runs in background mode (low CPU and I/O priority), at most three threads, and can be stopped any time.
