# IPC protocol v4

## Transport

| Item | Value |
|---|---|
| Channel | Named pipe `\\.\pipe\PerfTuner.v1` (dev instance: `PerfTuner.dev.v1`) — byte mode, full duplex, async |
| Framing | `[uint32 LE payload length][UTF-8 JSON payload]` |
| Max frame | client → service **16 KiB**; service → client **256 KiB** |
| Encoding | JSON, camelCase, enums as strings, `type` discriminator first |
| Serializer | `System.Text.Json` source generator (`ProtocolJsonContext`) — no reflection |
| Instances | `maxClients` (default 4, max 8) + 1 overflow instance that answers `busy` |

## Connection life cycle

```
client                                             service
  │ connect (TokenImpersonationLevel.Identification)  │
  │ verify server: GetNamedPipeServerProcessId        │  CallerIdentity.TryResolve (token via
  │   == SCM pid of PerfTunerSvc                      │   ImpersonateNamedPipeClient): SID, groups,
  │                                                   │   elevation; reject anonymous/guest/network
  │ ── hello ───────────────────────────────────────► │  must arrive within 5 s, must be first
  │ ◄───────────────────────────────────── helloAck ─ │  role = Admin iff elevated administrator
  │ ── getHistory / getConfig / subscribeMetrics ───► │  per request: size → guard → schema →
  │ ◄──────────────────────────────── responses ───── │    rate limit → re-auth → authorize
  │ ◄──────────────── metrics (push, id 0) … ──────── │  ≤ 1 per aggregation interval
  │ ── getStatus (every 2 s) ───────────────────────► │
```

## Requests (client → service)

All requests have `type` (discriminator) and `id` (uint32, **non-zero**, echoed in the response).

| type | Fields | Bounds | Role |
|---|---|---|---|
| `hello` | `protocolVersion: int`, `clientName: string` | version == 4; name 1–64 chars `[A-Za-z0-9 ._-]` | any (must be first) |
| `ping` | — | — | Viewer |
| `getStatus` | — | — | Viewer |
| `subscribeMetrics` | `enable: bool` | — | Viewer |
| `getHistory` | `maxPoints: int` | 1–300 | Viewer |
| `getConfig` | — | — | Viewer |
| `setMonitoringOptions` | `enablePerProcessIo?: bool`, `aggregationIntervalMs?: int` | ≥ 1 field set; interval 500–2000 | **Admin** |
| `getProcesses` | `maxCount: int` | 1–300 | Viewer |
| `processAction` | `pid`, `createTime`, `action` (`SetPriority`, `EfficiencyMode`, `CpuCores`, `MemoryPriority`, `SoftMemoryCap`, `TrimMemory`, `EndProcess`) plus **exactly** the one parameter that action needs (`priority` ∈ BelowNormal/Normal/AboveNormal, `enable`, `cores`, `memoryPriority`, `capMb` 64–65536, `confirm:true` for EndProcess) | pid > 4; extra or missing parameters are rejected | **Admin** |
| `getChanges` | — | — | Viewer |
| `revertChange` | `changeId: long` | > 0 | **Admin** |
| `revertAll` | — | — | **Admin** |
| `setTuningOptions` | `safeMode?`, `mode?` (Off/Focus/Quiet), `autoCalm?`, `autoCalmThresholdPercent?` | ≥ 1 field; threshold 5–100 | **Admin** |
| `resetBreaker` | — | — | **Admin** |
| `reportForeground` | `pid: int` | > 0 | **Admin** |
| `getRules` / `setRules` | `rules: AppRule[]` | ≤ 32, unique allow-listed names, ≥ 1 action each | Viewer / **Admin** |
| `trimBackgroundMemory` | — | — | **Admin** |

### Added in v3 (insights) and v4 (forensics)

| type | Fields | Bounds | Role |
|---|---|---|---|
| `getInsights` / `getBootReport` | — | — | Viewer |
| `startLatencyScan` | `seconds` | 5–60 | **Admin** |
| `getTimeline` | `sinceUnixMs`, `maxEvents` | 1–200 events | Viewer |
| `explainStall` | `eventId` | > 0 | Viewer |
| `getIoLatency` / `getNetworkHealth` / `getMemoryGovernor` / `getRegressions` / `getExperiments` / `getContracts` / `getWaitAnalysis` | — | — | Viewer |
| `startWaitAnalysis` | `seconds` | 3–30 | **Admin** (kernel trace) |
| `setProfile` | `profile` (Safe/Balanced/Advanced) | defined enum | **Admin** |
| `startExperiment` | `contractId`, `pid`, `createTime` | id `[A-Za-z0-9 ._-]` ≤ 32; pid > 4; createTime > 0; contract must be experimentable and allowed by the active profile | **Admin** |

v4 `status` adds `profile`, `telemetryEventsPerSec`, `telemetryEventsLost` and `telemetryHealth`. `MetricSample` adds `cpuEffectiveMhz` and `cpuLimitPercent` (v3). All v4 responses are bounded: timeline ≤ 200 events, experiments ≤ 50, bursts ≤ 30, network series 2 × 600 values, per-process latency ≤ 20 rows. None of them come close to the 256 KiB frame cap.

Responses added in v2: `processes`, `actionResult` (`success`, `message`, `changeId?`), `changes` (`active[]`, `history[]`) and `rules`. `status` gains `mode`, `autoCalm`, `autoCalmThresholdPercent`, `rulesCount`, `hybridCpu`, `foregroundKnown` and `tuningAllowed`. The request frame limit is 16 KiB (for rule lists).

## Responses (service → client)

| type | Fields |
|---|---|
| `helloAck` | `protocolVersion`, `serverVersion`, `sessionId`, `role` (`Viewer`/`Admin`), `aggregationIntervalMs`, `maxRequestsPerSecond` |
| `pong` | `serverTimeUnixMs` |
| `status` | `serverVersion`, `uptimeSeconds`, `safeMode`, `telemetrySource`, `perProcessIoEnabled`, `degraded`, `breaker` (`Closed`/`Open`/`HalfOpen`/`Latched`), `breakerReason?`, `activeChanges`, `connectedClients`, `aggregationIntervalMs`, `serviceCpuPercent`, `servicePrivateMb`, `cpuBudgetPercent`, `memoryBudgetMb` |
| `metrics` (push, `id` = 0) | `sample: MetricSample`, `topIo: ProcessIo[]` (≤ 10) |
| `history` | `samples: MetricSample[]` (≤ 300) |
| `config` | effective, validated config subset + `configWarnings[]` |
| `ack` | — |
| `error` | `code`, `message` (≤ 256 chars, never echoes input) |

`MetricSample`: `timestampUnixMs, intervalMs, cpuPercent, memoryLoadPercent, commitPercent, memoryAvailableMb, memoryTotalMb, diskReadBytesPerSec, diskWriteBytesPerSec, netRxBytesPerSec, netTxBytesPerSec, serviceCpuPercent, servicePrivateMb, flags` (`EtwIo | FallbackIo | Degraded | ProcessTableOverflow`).

`ProcessIo`: `pid, name (≤ 64, control chars replaced), diskBytesPerSec, netBytesPerSec`.

## Validation pipeline (every inbound frame)

1. **Frame**: length must be 1..16384. Zero, oversized or truncated → `ProtocolViolation` → **connection closed immediately** (nothing is echoed).
2. **Structural guard** (`StrictJsonGuard`): valid UTF-8 JSON; root is an object; depth ≤ 6; no comments or trailing commas; **no duplicate property names**. This blocks `{"type":"ping","type":"setMonitoringOptions"}`-style smuggling, which System.Text.Json would otherwise resolve as last-wins. → `malformed`
3. **Schema** (strict source-generated deserialization): known `type` only; **unknown properties rejected** (`UnmappedMemberHandling.Disallow`); `required` members enforced; exact JSON types (no string→number coercion). → `schema`
4. **Semantics** (`RequestValidator`): non-zero id, numeric bounds, name allow-list. Unknown request types fail closed. → `schema`
5. **Rate limit**: token bucket per connection (default 20 req/s, burst 40). → `rate_limited`
6. **Re-authentication**: the caller's token is re-read through the pipe. The user SID must match the handshake identity, and Admin sessions must still be elevated. → `unauthorized`, connection closed
7. **Authorization**: role table (`RequiredRole`). Unlisted types default to Admin (fail closed). → `forbidden`
8. **Dispatch** to a fixed `switch`. There's no reflection-based routing, no shell, no file paths, and nothing is evaluated.

Steps 2–5 and 7 count as strikes. **Three strikes close the connection.**

## Error codes

`bad_frame, malformed, schema, handshake_required, unsupported_version, unauthorized, forbidden, rate_limited, busy, internal`

## Bandwidth budget

| Stream | Size | Rate | Per client |
|---|---|---|---|
| metrics push | ~0.5–1.2 KiB (with top-5) | ≤ 2 Hz (interval 500 ms min), default 1 Hz | ≤ 2.4 KiB/s |
| status poll | ~0.6 KiB | 0.5 Hz | 0.3 KiB/s |
| history (on connect) | ≤ 300 × ~0.4 KiB ≈ 120 KiB | once per connection | — |
| **Total steady state** | | | **≤ 3 KiB/s per client, ≤ 24 KiB/s at the 8-client maximum** |

## Versioning

`protocolVersion` is checked during the handshake, and any mismatch is refused (`unsupported_version`). Additive changes are still breaking, because unknown fields are rejected. Every wire change therefore bumps the version, and the service may serve N and N-1 during an upgrade window (M3).
