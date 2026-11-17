# Performance budgets and verification

## Budgets

| Budget | Target | Enforced / observed by |
|---|---|---|
| Service CPU (steady state) | **< 0.5 %** of the machine | Continuously self-measured (`SelfSampler`). The `OverheadGovernor` degrades per-process attribution when the rolling mean exceeds the budget. Shown in the GUI. |
| Service memory | **< 100 MB** private | Self-measured every interval; a warning is logged when exceeded. Every buffer is fixed-size by design (see `ARCHITECTURE.md` §4). |
| ETW hot path | ≤ 50 ns/event, **0 B allocated** | BenchmarkDotNet + `IoAccumulatorTests.Event_flood_does_not_allocate` (1M events, asserts 0 bytes) |
| Per-interval work | ≤ 50 µs, ≤ 2 KB allocated (only when a GUI is subscribed) | BenchmarkDotNet |
| IPC | ≤ 2 msgs/s push, frames ≤ 4 KiB in / 256 KiB out, **≤ 3 KiB/s per client** | Protocol constants + `IPC_PROTOCOL.md` |
| GUI | ≤ 4 chart renders/s, no rendering while minimised, coalesced updates | `MainViewModel.OnMetrics`, `MainWindow.OnSample` |

## Engineering decisions that keep C# near C++ overhead

| Technique | Where |
|---|---|
| Kernel ETW with **only two keywords**, a private session and small buffers | `EtwKernelIoSource` |
| Per-event work is one locked add into **preallocated parallel arrays**. TraceEvent reuses event objects, and there's no LINQ, boxing or closures allocated per event (the lambdas are created once). | `IoAccumulator` |
| Fixed-capacity `RingBuffer<T>` of an **unmanaged struct** | `RingBuffer`, `MetricSample` |
| Top-N selection by insertion into a caller-owned `Span<T>` (no sort, no list) | `IoAccumulator.SnapshotAndReset` |
| Cheap syscalls instead of event streams for CPU/memory (`GetSystemTimes`, `GlobalMemoryStatusEx`) | `SystemSampler` |
| **Source-generated** P/Invoke (`LibraryImport`) and JSON (`JsonSerializerContext`) — no reflection, no runtime IL stubs | `Platform.Windows/Native`, `ProtocolJsonContext` |
| Push frames serialized **once** and shared by all subscribers; request reads into a preallocated 4 KiB buffer; responses via a reused `ArrayBufferWriter` | `TelemetryEngine.Publish`, `ClientSession` |
| Bounded `DropOldest` channels; write timeouts | `MetricsHub`, `ClientSession` |
| `PeriodicTimer` async loop; one dedicated ETW thread; no blocking waits on pool threads | `TelemetryEngine`, `EtwKernelIoSource` |
| Workstation non-concurrent GC + `ConserveMemory=5`, invariant globalization, English-only satellite resources | `PerfTuner.Service.csproj` |
| PDH fallback uses a **native, reused** counter-array buffer | `PdhIoFallback` |
| GUI: coalesced dispatcher updates at `Background` priority; ScottPlot `Signal` over fixed arrays | `MainViewModel`, `LiveChart` |

## Measured results

### Micro-benchmarks (BenchmarkDotNet, ShortRun, i7-12700H, .NET 9.0.20)

| Method | Mean | Allocated |
|---|---:|---:|
| ETW event → accumulator | **19.6 ns** | **0 B** |
| Interval snapshot + top-N | 20.6 ns | 0 B |
| Ring buffer add | 22.7 ns | 0 B |
| Encode push frame | 1.35 µs | 440 B |
| Decode + validate request | 0.87 µs | 608 B |

At a heavy 50,000 IO events/s, the hot path costs about 1 ms of CPU per second, which is 0.1 % of one core or **≈ 0.005 %** of this 20-thread machine.

### End-to-end on Windows 11 (build 26100, 20 logical CPUs, 64 GB)

Service in developer console mode (PDH fallback, non-elevated), GUI connected and streaming, after the 11-check hostile self-test:

| Metric | Result | Budget |
|---|---|---|
| Service CPU, steady state (30 s window) | **0.013 %** of machine | < 0.5 % ✅ |
| Service CPU, self-reported per interval | 0.00–0.08 % | < 0.5 % ✅ |
| Service private memory | **15–19 MB** (working set ~55 MB) | < 100 MB ✅ |
| Service threads | 13 | — |
| GUI private memory | ~160 MB (WPF + SkiaSharp + JIT) | not budgeted; M4 target < 120 MB via ReadyToRun + trimming unused ScottPlot OpenGL assets |

The ETW path (installed service) still needs the same measurement under a synthetic IO load. See the procedure below; this is part of M4 sign-off.

## How to verify

```powershell
# 1. Unit + allocation tests (any OS)
dotnet test tests/PerfTuner.Core.Tests -c Release

# 2. Micro-benchmarks
dotnet run -c Release --project tests/PerfTuner.Benchmarks -- --filter * --job short

# 3. End-to-end overhead (installed service, elevated PowerShell)
$svc = Get-Process PerfTuner.Service; $n = [Environment]::ProcessorCount
$t1 = $svc.TotalProcessorTime.TotalSeconds; Start-Sleep 60; $svc.Refresh()
(($svc.TotalProcessorTime.TotalSeconds - $t1) / 60 / $n * 100)    # % of machine, target < 0.5
[math]::Round($svc.PrivateMemorySize64 / 1MB, 1)                   # MB, target < 100

# 4. Load test: generate IO while measuring (3) — e.g. diskspd, and a parallel download
diskspd -c2G -d120 -r -w50 -t8 -o16 -b4K -Sh testfile.dat
# Accept if CPU < 0.5 % and no Degraded flag; if Degraded appears, the governor did its job:
# check the log for "Overhead governor degraded=True" and confirm CPU returned under budget.

# 5. IPC robustness
perftuner-cli --selftest
```

M4 automates steps 3–4 in a CI job on a Windows runner and fails the build if a budget is exceeded.
