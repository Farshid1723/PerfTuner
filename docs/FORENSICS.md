# Forensics, visibility and safe autonomy (v0.4)

Most tuning tools show *what* is using resources. v0.4 adds tools that explain *why* the PC felt slow, and that prove whether a fix helped. Everything here uses documented Windows interfaces: kernel ETW through TraceEvent, PDH counters, and the process APIs. There are no drivers, no undocumented calls, no shelling out and no file paths taken from the GUI. Every change is bounded, reversible and protected by the circuit breaker.

The GUI shows all of it on the **Forensics** tab. Profiles and contracts are on the **Tuning** tab, and telemetry health appears in the header.

| # | Feature | Where the data comes from | Needs the installed (elevated) service |
|---|---|---|---|
| F1 | Causality timeline + **Explain this stall** | Stall detector over per-interval signals; process context captured at the moment of the stall | Richer with ETW; works in fallback |
| F2 | Regression autopilot (baselines + proof) | 5 KPIs, per-minute, EWMA baseline (6 h half-life), persisted | Partly (disk p99 needs ETW) |
| F3 | Heuristic memory governor | Memory behaviour classifier (5 classes) | No |
| F4 | Experiment mode (A/B) | Process CPU time / working set + system guard KPIs | No |
| F5 | Safe tuning contracts + profiles | Contract catalog enforced in `TuningEngine.ApplyAction` | No |
| F6 | IO latency histograms (p50/p90/p99, queue vs device) | ETW `DiskIO` completions: `ElapsedTimeMSec`, `DiskServiceTimeMSec` | **Yes** |
| F7 | Network queue health / microbursts | ETW `TcpIp`/`UdpIp` send/recv, binned at 100 ms; `TcpIpRetransmit` | **Yes** |
| F8 | CPU wait-state lens | On-demand ETW `ContextSwitch` + `Dispatcher` (ReadyThread) trace | **Yes** |
| F9 | Overhead-aware telemetry health | Sample age, ETW event rate, dropped events, governor state | No |

## F1: causality timeline and "Explain this stall"

- **Detector** (`Core/Forensics/Timeline.cs`). Each interval, the detector evaluates CPU %, disk p99 (last second), disk queue length, hard faults/s, available RAM %, CPU performance limit and TCP retransmits/s.
  - Rules use hysteresis. For example, CPU saturation must last 3 consecutive intervals.
  - Each rule has a 30 s cooldown, so one incident produces one event.
  - Paging and thrashing only count when RAM is actually short. Apps that read memory-mapped files also cause hard faults, and those are not pressure.
- **Timeline**: a bounded ring of 1,000 events. It holds stalls, throttling transitions, network bursts, regressions, dropped telemetry, and **every tuning action and decision**. The service never hides its own changes.
- **Context capture**: when a stall fires, the service takes a process snapshot (one syscall) and stores:
  - the top CPU users;
  - the top disk users, in MB/s from ETW attribution;
  - the top hard-fault processes;
  - the largest private-memory users.

  Each entry records whether the process is in the background and controllable. The 20 samples before the stall are kept with it, so older stalls can still be explained after the history ring has moved on.
- **Explainer** (`StallExplainer`): a rule-based scorer over the window [t − 15 s, t + 3 s]. Each hypothesis gets a confidence of *signal strength × temporal overlap*. The hypotheses are:
  - CPU contention
  - power or thermal limit
  - storage saturation
  - memory thrash
  - network
  - driver DPC/ISR latency (from the last latency scan)
  - a recent tuning change

  Every sentence cites a measured value and a time. When one background process dominates, the finding names a **suggested contract**, which you can run as an A/B experiment with one click. If nothing reaches 25 % confidence, the result says so honestly and points to the wait-state lens.

## F2: regression autopilot

- **KPIs**, one value per minute:
  - disk p99 latency
  - stalls per hour
  - hard faults/s
  - TCP retransmits/s
  - % of time CPU-limited
- **Baseline**: an EWMA with a 6 h half-life, persisted in `state\forensics.json` so it survives restarts. The recent value is the mean of the last 10 minutes.
- **When a regression is flagged**: only after at least 60 minutes of baseline, when the recent value is worse by the KPI's relative threshold (+50 % or +100 %), **and** above an absolute floor. A move like 1 ms → 2 ms is never reported.
- **Output**: suspected causes (current top contributor, tuning changes in the last 2 h, power and network hints) and **suggestions**. A suggestion is a contract plus a target process, and is only offered for controllable background processes.
- **Advanced profile**: at most one autonomous experiment every 30 minutes, taken from a current regression's suggestions. The same contract is never retried on the same app within 6 h.

## F3: memory governor

| Behaviour | Rule | Action (Balanced/Advanced only) |
|---|---|---|
| Thrashing | ≥ 500 hard faults/s and < 8 % RAM available | Low memory priority for the ≤ 3 largest background apps (≥ 200 MB private), plus **one** trim (at most every 5 min) |
| Pressure | < 12 % available, commit ≥ 90 %, or ≥ 150 hard faults/s with < 25 % available | Low memory priority for the ≤ 3 largest background apps (nothing trimmed) |
| Allocation burst | Memory use growing ≥ 500 MB/min | Observe only (the leak detector covers the persistent case) |
| Working-set churn | ≥ 20,000 soft faults/s, few hard faults | **Explicitly nothing**: trimming would make churn worse |
| Stable | — | — |

- **Timing**: the governor acts only on pressure that lasts ≥ 10 s, and at most once a minute.
- **Foreground**: it never touches the foreground app, and releases an app the moment you switch to it.
- **Release**: everything is restored after 60 s of stable memory, or immediately in the Safe profile.
- **Accounting**: all changes are journaled under the owner `governor`.

## F4: experiment mode

```
Baseline 30 s (nothing changed) → apply (contract-checked, journaled) → Treatment 30 s → decide
```

- **KPIs**:
  - target CPU % (from process CPU time)
  - target working set
  - system stalls
  - disk p99
  - hard faults/s
- **Decision**: a change is **kept** only if the contract's primary KPI improves by its minimum effect (efficiency mode ≥ 10 % CPU, soft cap ≥ 15 % working set), or the contract accepts neutral results, **and** no guard KPI (stalls, disk p99, hard faults) worsens by more than 25 % above its floor. Otherwise it is rolled back automatically.
- **Aborts** (with rollback):
  - the circuit breaker trips;
  - the target exits (detected through the process exit time, so it works even while other handles are open);
  - you revert the change;
  - for autonomous runs, the profile leaves Advanced.
- **Audit trail**: the last 50 records (KPIs before and after, decision, manual or autopilot) are persisted and shown on the Forensics tab.
- **Rollback of kept changes**: a manual experiment's accepted change stays until you revert it, and is always rolled back when the service stops. Autopilot changes are reverted as soon as the profile drops below Advanced.

## F5: contracts and profiles

Every change the service can make maps to a **contract**: scope, bounds, rate limit, reversibility, abort conditions, expected outcome and minimum profile. `TuningEngine.ApplyAction` refuses any action whose contract the active profile doesn't allow. The full table is on the Tuning tab and in `Core/Autonomy/TuningContracts.cs`.

| Profile | Allows |
|---|---|
| **Safe** (default) | Only changes you click. Manual A/B experiments are allowed, because they are an explicit request. |
| **Balanced** | Plus Focus/Quiet modes, auto-calm, app rules and the memory governor |
| **Advanced** | Plus the regression autopilot (autonomous experiments) |

## F6: IO latency

- **Source**: every ETW `DiskIO` completion records total latency and, when the storage driver reports it, device service time. *Queue* time is total − device.
- **Histograms**: 28 log2 buckets (1 µs … ~4.5 min), allocation-free.
- **Views**: one system histogram per 60 s window (read, write, queue, device, all), plus a fixed 128-slot per-process table. Memory stays about 30 KB however many processes do IO.
- **Stall detector input**: a 1-second p99.
- **GUI**: p50/p90/p99/max per class, a histogram chart and per-process latency. The current disk queue length comes from PDH.

## F7: network microbursts

- **Binning**: bytes are binned by the **ETW event timestamp** (not arrival time) into 100 ms bins, in a 600-bin (60 s) ring. Each bin records its top 4 contributing PIDs.
- **What counts as a burst**: a bin is a burst when it carries ≥ 10 Mbit/s **and** ≥ 5× the median of the preceding 5 s. Adjacent bins merge.
- **Evaluation**: bins are judged only once they are ≥ 1.5 s old (ETW delivers late) and after a full 5 s of baseline exists, so start-up never produces false positives.
- **Health**: Good, Bursty, or Lossy (retransmits ≥ 2 % of packets in the last minute).

## F8: CPU wait-state lens

This is an on-demand, private ETW session (`PerfTuner-WaitLens`, 3–30 s, Admin only) with `ContextSwitch` + `Dispatcher` events. It splits each process's time into:

- **Running**: switch-in → switch-out.
- **Ready (waiting for a core)**: readied or preempted → switch-in. This is CPU contention that CPU % graphs don't show.
- **Blocked**, by the kernel's wait reason:
  - disk/driver IO (Executive)
  - paging (PageIn/FreePage/VirtualMemory)
  - locks/IPC (UserRequest/LPC/EventPair)
  - idle sleeps

It is bounded to 200,000 threads and 4,096 processes. The result includes a system *ready %* and a per-process diagnosis such as "CPU-starved" or "mostly blocked on paging".

## F9: telemetry health

The header shows a colour dot and a line of text:

- **Freshness**: the age of the last pushed sample; stale after 5 s.
- **Source**: ETW, or the PDH fallback.
- **Events**: kernel events/s and dropped events.
- **State**: healthy, lossy (drops in the last minute), degraded (the overhead governor reduced detail to stay within the 0.5 % CPU budget), or stale.

Dropped events also create a timeline event, so explanations for that moment are marked as based on incomplete data.

## Overhead

- **ETW hot path**: the consumer thread does fixed-slot, lock-protected adds. There is no per-event allocation; TraceEvent reuses its event objects.
- **Per interval**: one PDH query (pages input, page faults, disk queue), plus a process snapshot every 5 s.
- **State**: everything is a fixed-size ring or table. Measured in dev mode, the service stays at about 55 MB working set (runtime included) with CPU well under budget.

## Verifying

```powershell
perftuner-cli --dev --forensics             # timeline, explain, IO/network/memory/regressions/contracts/experiments, wait lens
perftuner-cli --dev --experiment-selftest   # real A/B run on a throw-away ping.exe: phases, apply, decision, audit, abort-on-exit
perftuner-cli --forensics                   # against the installed service (elevated prompt): ETW-backed F6/F7/F8
```
