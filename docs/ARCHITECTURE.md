# Architecture

## 1. Decisions

### GUI stack: **WPF on .NET 9** (not WinUI 3)

| Criterion | WPF (.NET 9) | WinUI 3 (Windows App SDK) |
|---|---|---|
| Win11 look | Built-in **Fluent theme** (`ThemeMode="System"`): Mica, rounded corners, light/dark, accent colour | Native Fluent |
| Deployment | Plain `.exe`, xcopy/MSI; no extra runtime | Requires the Windows App SDK runtime, or self-contained (+~30–60 MB); unpackaged mode has sharp edges |
| Elevation | Relaunch same exe with `runas` works trivially | Packaged apps cannot self-elevate; unpackaged is supported but less mature |
| Chart libraries | Mature, fast (ScottPlot, LiveCharts, OxyPlot) | Fewer, younger |
| Stability / tooling | 18 years of hardening, designer + hot reload | Improving but still has open issues around windowing, tray, and multi-window |
| Idle cost | Retained-mode; zero cost when nothing changes; no rendering while minimised | Similar |

**Choice: WPF.** It gets the Win11 visual language natively in .NET 9, with the lowest deployment risk. It also has the simplest path to the privilege-separated elevation model this product needs. WinUI 3's advantages (newest controls) don't outweigh its packaging and elevation friction for a system utility.

**Charts: ScottPlot 5 (`ScottPlot.WPF`).** It renders with SkiaSharp into one bitmap, so there's no per-point WPF visual tree. A `Signal` plot references a caller-owned `double[]`, so each update is one `Array.Copy` of 120 doubles plus one render. Pan and zoom are disabled.

### Architecture: **A — two processes** (Windows service + GUI client)

| | A: Service + GUI | B: Single process |
|---|---|---|
| Privilege separation | GUI runs as user; only the service has the privileges it needs | Whole UI would need to run elevated (large attack surface) or lose monitoring when not elevated |
| Continuous operation | Monitors and enforces the circuit breaker 24/7, even with no one logged in | Stops when the window closes |
| Fault isolation | A GUI crash (e.g. GPU driver) cannot touch tuning state; service crash → SCM restart + breaker trip | One crash takes down everything, including rollback logic |
| Footprint | Service ~15–20 MB private; GUI only runs when opened | UI framework always resident |
| Cost | IPC boundary to secure (addressed below) | — |

**Choice: A.** The deciding factors are safety and privilege separation. The component that can change system behaviour must run with a minimal, fixed privilege set, must outlive the UI, and must be able to roll back even when the UI is gone or hung. The IPC boundary this creates is small and strictly specified (see `IPC_PROTOCOL.md`, `SECURITY.md`).

### Runtime: .NET 9

The brief allows .NET 8 or 9. .NET 9 is used because WPF's Fluent theme ships in .NET 9, not 8. **Note:** both .NET 8 and .NET 9 reach end of support on **10 Nov 2026**. The target framework lives in one place (`Directory.Build.props`), and moving to .NET 10 LTS is scheduled in Milestone 4.

## 2. Module breakdown

| Project | Kind | Responsibility |
|---|---|---|
| `PerfTuner.Contracts` | portable lib | Wire protocol: message records, limits, framing, strict JSON guard, request validation, source-generated serializer. No OS dependencies. |
| `PerfTuner.Core` | portable lib | Engine logic with no OS dependencies: `RingBuffer<T>`, `IoAccumulator` (hot-path sink), `OverheadGovernor`, `CircuitBreaker`, `ChangeJournal`, `TokenBucket`, `PathGuard`, config model + validator. 100% unit-testable on any OS. |
| `PerfTuner.Platform.Windows` | Windows lib | The only P/Invoke surface (source-generated `LibraryImport`): system/self samplers, PDH fallback, process info, product paths, directory ACL hardening, SCM query, installer. |
| `PerfTuner.Service` | Windows service | Hosting, logging, `TelemetryEngine`, `EtwKernelIoSource`, `PdhIoSource`, `SafetyCoordinator`, `PipeServer`, `ClientSession`, `CallerIdentity`, `MetricsHub`. |
| `PerfTuner.Client` | Windows lib | `PerfTunerClient` (verifies the server, handshake, request correlation, push events) and `ElevatedOperations` (UAC-consented helper). |
| `PerfTuner.App` | WPF exe | MVVM GUI (CommunityToolkit.Mvvm), live charts, service management. Also acts as the headless elevated helper (`--elevated-op`). |
| `perftuner-cli` (tools) | console | Diagnostics: live metrics and an IPC security self-test. |
| `PerfTuner.Core.Tests` | xUnit | Protocol, validation, data structures, breaker, journal, config, allocation tests. |
| `PerfTuner.Benchmarks` | BenchmarkDotNet | Hot-path latency and allocation evidence. |

Dependency direction (no cycles): `Contracts ← Core ← Platform.Windows ← {Service, Client} ← {App, Cli}`.

## 3. Repository layout

```
PerfTuner.sln
Directory.Build.props        # TFMs, warnings-as-errors, analyzers, version
Directory.Packages.props     # central package versions
global.json, .editorconfig
.github/workflows/ci.yml     # build + tests + publish artifacts
build/publish.ps1            # produces artifacts/PerfTuner (GUI + Service\) and artifacts/cli
docs/
  ARCHITECTURE.md  IPC_PROTOCOL.md  SECURITY.md  SAFETY_POLICY.md  PERFORMANCE.md  ROADMAP.md
src/
  PerfTuner.Contracts/   ProtocolConstants, Messages, Metrics, FrameCodec, StrictJsonGuard,
                         RequestValidator, MessageCodec, ProtocolJsonContext
  PerfTuner.Core/        Buffers/RingBuffer, Telemetry/{IoAccumulator,OverheadGovernor},
                         Safety/{CircuitBreaker,ChangeJournal}, Security/{TokenBucket,PathGuard},
                         Configuration/{ServiceConfig,ConfigLoader}
  PerfTuner.Platform.Windows/
                         Native/{Kernel32,Advapi32,Pdh}, Telemetry/{SystemSampler,PdhIoFallback,
                         ProcessNameCache}, ProcessInfo, ProductPaths, DirectoryHardening,
                         ServiceControl, ServiceInstaller
  PerfTuner.Service/     Program, LoggingSetup, config.default.json,
                         Engine/{TelemetryEngine,RuntimeState,SafetyCoordinator},
                         Telemetry/{IIoSource,EtwKernelIoSource,PdhIoSource},
                         Ipc/{PipeServer,ClientSession,CallerIdentity,MetricsHub}
  PerfTuner.Client/      PerfTunerClient, ElevatedOperations
  PerfTuner.App/         App, MainWindow, ViewModels/MainViewModel, Charts/LiveChart, Format
tests/
  PerfTuner.Core.Tests/  PerfTuner.Benchmarks/
tools/
  PerfTuner.Cli/
```

## 4. Monitoring pipeline

```
 ┌─────────────── kernel ────────────────┐
 │ ETW private session "PerfTuner-KernelIO"│  keywords: DiskIO | NetworkTCPIP only
 └──────────────┬─────────────────────────┘
                │ real-time buffers (4 MB, kernel drops on overflow → EventsLost)
   dedicated "ETW consumer" thread (AboveNormal)
                │ per event: one locked add, zero allocation
                ▼
        IoAccumulator  (totals + 1024-slot open-addressing PID table)
                │
   PeriodicTimer tick (500–2000 ms, default 1000)  ── async, no thread blocked between ticks
                │  GetSystemTimes · GlobalMemoryStatusEx · GetProcessTimes/GetProcessMemoryInfo(self)
                │  [PDH fallback polled here if ETW unavailable]
                │  SnapshotAndReset → top-N PIDs (insertion into fixed span)
                ▼
        MetricSample (unmanaged struct) ──► RingBuffer<MetricSample> (3600 preallocated)
                │
                ├─► OverheadGovernor (rolling 30-tick mean of own CPU vs budget → degrade)
                ├─► SafetyCoordinator → CircuitBreaker (sustained CPU / memory / commit / faults)
                └─► if subscribers: encode ONE frame → MetricsHub → per-client 2-slot DropOldest channel
```

**ETW vs PDH choices**

| Metric | Source | Why |
|---|---|---|
| CPU % (system) | `GetSystemTimes` | ETW has no cheap CPU-utilisation event; CSwitch/SampledProfile cost orders of magnitude more. One syscall per interval, same data as Task Manager. |
| Memory load, available, commit | `GlobalMemoryStatusEx` | Instantaneous kernel counters; no event stream exists or is needed. |
| Disk bytes (read/write) | **ETW** kernel `DiskIO` | Per-IO, per-process attribution. |
| Network bytes (rx/tx, TCP+UDP, v4+v6) | **ETW** kernel `NetworkTCPIP` | Per-process attribution via event PID. |
| Disk/network fallback | **PDH** (`PhysicalDisk(_Total)`, `Network Interface(*)`) via `PdhAddEnglishCounter` (locale-independent — matters on German Windows) | Used only when ETW can't start (not elevated / policy / session limit). Totals only. |
| Service self-overhead | `GetProcessTimes`, `K32GetProcessMemoryInfo` | Continuous in-production budget verification. |

**Event filtering.** Only two kernel keywords are enabled. There's no Process/Thread/ImageLoad rundown, no stacks and no file-name events, so the kernel only emits what we count. The private session name (Windows 8+ system-logger mode) means we never take over or stop another tool's `NT Kernel Logger`. Known limitation: writes flushed by the cache manager or lazy writer are attributed to `System` (PID 4). Totals are exact.

**Aggregation.** Everything is aggregated per interval (500–2000 ms) before leaving the service. The GUI receives ≤ 2 messages/s regardless of event rate. The service serializes each push once and shares the same byte array with all subscribers.

**Ring buffers and bounded queues**

| Structure | Bound | Overflow policy |
|---|---|---|
| ETW kernel buffers | 4 MB | Kernel drops events (counted) — never grows service memory |
| `IoAccumulator` PID table | 1024 slots, load ≤ 75 %, probe ≤ 16 | Extra PIDs counted in totals only; interval flagged `ProcessTableOverflow` |
| `RingBuffer<MetricSample>` history | `historyCapacity` (60–7200) | Overwrite oldest |
| Per-client push channel | 2 frames | `DropOldest` — slow client only ever sees the freshest data |
| Per-client write | 5 s timeout | Client disconnected; never back-pressures the service |
| Process-name cache | 256 entries, flushed every 60 s | Clear |

## 5. Threading model

| Thread | Work | Blocking? |
|---|---|---|
| ETW consumer (dedicated, background) | `ProcessTrace` → accumulator | Blocks by design; one thread for the service lifetime |
| Thread pool: engine loop | `PeriodicTimer` tick → sample, aggregate, publish | Async; nothing blocked between ticks |
| Thread pool: pipe accept loop + per-session read/push loops | Async I/O | Async; per-session write lock (`SemaphoreSlim`) |
| GUI: UI thread | Bindings + 4 chart renders per second | Receives work only via `Dispatcher.InvokeAsync(Background)`; pushes are coalesced (at most one pending UI update) |
| GUI: thread pool | Connection loop, request/response, status polling (2 s) | Async |

The service creates 13 threads in total (measured), most of them runtime infrastructure. The GC is workstation, non-concurrent, with `ConserveMemory=5`, so there's no background GC thread and the heap stays small.
