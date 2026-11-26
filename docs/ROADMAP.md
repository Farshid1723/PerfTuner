# Implementation plan

## Shipped

- **v0.1** — Milestone 1: service, IPC, ETW telemetry, GUI dashboard, installer from the GUI.
- **v0.2** — 20+ reversible tuning functions, modes, app rules, system settings, Changes tab.
- **v0.3** — Insights (CPU limit, leaks, driver latency, boot analysis, drive health), Windows 11 switches, GPU preference, new icon.
- **v0.11** — Undocumented page (opt-in, behind a warning): about 50 hidden Windows settings in 10 groups, each with an honest verdict (works, niche, placebo, no effect, risky, security risk, setup only), asked twice for risky ones, undoable (machine-wide undo records in an administrators-only journal); boot and timer settings through bcdedit; virtual-desktop number in the tray, name overlay and hotkeys. Network repair on Troubleshoot (DNS cache, IP address, Winsock, TCP/IP); remove built-in apps and repair Microsoft Store on Get apps; search indexing on/off and five more documented settings on Windows; mode shortcuts Ctrl+Alt+Shift+F1…F4.
- **v0.10.2** — Scan & Fix: every finding has "Show details" with what's behind it (the settings, apps, leaking processes, drives or startup apps); the Windows settings finding lists each setting with what it does and when it takes effect, and only the ones you tick are applied. The scan now suggests only the speed-and-convenience settings (privacy, look-and-feel and Windows Update settings stay a personal choice on the Windows page), and it no longer counts Fast Startup when hibernation is off.
- **v0.10.1** — Get apps: detects apps installed without Windows Package Manager (installed-programs list, Microsoft Store/MSIX packages, Start-menu and desktop shortcuts) and says so instead of failing to update them; real icons of installed apps read from their own files (initial badges for the rest; no third-party logos ship with PerfTuner); a Details panel for the app you click, with what's installed and the package repository's description; correct winget result messages.
- **v0.10** — Get apps page (119 apps in 10 categories, search, install / update / uninstall several at once, update all, installed check; Microsoft's Windows Package Manager started from its protected package folder with fixed arguments); Windows page (35+ grouped settings incl. Windows Update behaviour, optional Windows features through the DISM API, DNS provider with saved and restorable previous setting); more classic control panels; downloads shrunk from 115 MB to about 3 MB each (framework-dependent on .NET 9 Desktop Runtime, Brotli-compressed, identical files stored once, launcher restricted to the machine-wide .NET install).
- **v0.9** — Security page (21 checks in four groups, reviewed and undoable fixes, Markdown report); Windows Update leftovers (download and Delivery Optimization caches, WinSxS component cleanup via Windows' scheduled task, orphaned installer packages via the MSI API, moved not deleted); repair tools (DISM API check/restore health with progress, Windows Update reset by renaming its folders, restore point, classic panels); 13 more reversible Windows settings (privacy and Explorer); duplicate-file report; administrator rights by default; **PerfTuner-Portable.exe** (unpack-once, admin-only folder, SHA-256 checked on every start) and **PerfTuner-Setup.exe** (Program Files, shortcuts, start at sign-in, engine service, Installed-apps entry, in-place upgrade, silent mode, uninstall that reverts changes).
- **v0.8.1** — Fix: deleted or moved items stayed in the tree, treemap, sunburst and reports, and weren't recorded in History. Windows reports a successful delete/move with a success code other than S_OK (COPYENGINE_S_DONT_PROCESS_CHILDREN); the outcome is now decided from the disk plus Windows' Recycle Bin copy. Verified on a real PC (permanent delete, recycle, move).
- **v0.8** — Space explorer: parallel background scan of a fixed drive; tree / treemap / sunburst views (custom-drawn, theme-coloured, shared selection); reports (biggest files/folders, old & big, oldest, by type, per-type totals); Recycle Bin / permanent delete / move via `IFileOperation` with per-item outcome, protection rules, file-operations journal and "move back" in History.
- **v0.7** — Cockpit page with animated, theme-coloured hardware icons (load-driven motion, cool-down after fixes); CPU / Memory / Disk / Network detail; disk inspector (FS version, dirty flag, health, wear, fragmentation analysis, optimize, online scan, repair at next restart); virtual memory view with page-file advisor and reversible changes (machine journal in ProgramData); 21 metal themes with shape tokens and custom window chrome (snap layouts kept).
- **v0.6.1** — Themed dialogs (replace the grey Windows message box), Help page with a "?" hint on every section ("More help" jumps to the topic), About page (creator, repository, capabilities and strengths).
- **v0.6** — Redesign after real use. Sidebar navigation, six colour themes, three mutually exclusive modes (Turbo / Smart / Silent) with power-plan integration, tray notifications for automatic actions, memory care that frees RAM, a Startup page (boot time, recommendations, sortable impact table, Fast Startup), a Troubleshoot page with plain questions, more junk sources (Recycle Bin, crash dumps, error reports), and on/off Windows tweaks. Toolbox and the expert Diagnose views removed.
- **v0.5** — No install needed: the GUI runs its own standalone engine (kill-on-close job object, graceful rollback on exit) and lives in the system tray. New dark UI with big top tabs, a 1-click PulseScan (scan → summary → fix), round Speed Up tiles, a Toolbox, hover hints instead of long text, layouts that scale when maximized, and branded feature names ([NAMES.md](NAMES.md)). Protocol v5 (`shutdownEngine`); per-user standalone pipe; the breaker trips after a crash only if changes were in effect.
- **v0.4** — Forensics and safe autonomy F1–F9: causality timeline + stall explanation, regression autopilot, A/B experiments, memory governor, tuning contracts and profiles, IO latency histograms, network microbursts, CPU wait-state lens, telemetry health.

## Milestone 1: working skeleton ✅ (this delivery)

- [x] Solution structure, central build policy (warnings as errors, analyzers), central package versions
- [x] Windows service (Worker Service + `UseWindowsService`) with a `--console` developer mode
- [x] Telemetry: CPU and memory (native syscalls), **kernel ETW** disk and network with per-process attribution, PDH fallback, self-overhead sampling
- [x] Ring buffer, bounded accumulator, overhead governor, circuit breaker and change journal wired into the service life cycle (journal empty until M2)
- [x] Named-pipe IPC: restrictive DACL, anti-squatting, caller identity from the token, per-request re-authentication, role authorization, strict schema, rate limiting, timeouts
- [x] Validated, bounded config (fail-safe defaults); structured, bounded logging
- [x] WPF GUI: live dashboard (4 KPIs, 4 charts, top-IO processes), service status and health, **Install/Update, Start, Stop, Uninstall** through a UAC-consented helper
- [x] CLI probe with live metrics and an 11-check IPC security self-test
- [x] 60 unit tests (including zero-allocation assertions) and 5 micro-benchmarks
- [x] Verified end to end on Windows 11 (dev mode)

**Exit criteria met:** builds clean, tests green, service runs within budget, GUI shows live data, self-test passes.

## Milestone 2: safe profiles and reversible tuning ✅ (v0.2, delivered early)

Delivered: per-process priority, efficiency mode, CPU sets, memory priority, soft memory cap, trim and end process; Focus, Quiet and Auto-calm; app rules; persisted journal with crash replay; protocol v2; Processes, Tuning, System and Changes tabs; system settings (power, processor, visual effects, transparency, startup apps, drive optimization, temp cleanup) with a separate undo journal; 40 new unit tests and an end-to-end `--tuning-selftest` (16 checks, verified on Windows 11). See `FEATURES.md`.

Original plan, kept for reference:

1. `IReversibleChange` implementations: `PriorityChange`, `AffinityChange` (CPU Sets), `WorkingSetSoftLimitChange`, `PowerThrottlingChange`. Each captures the original value and the target identity (PID + creation time).
2. **Persisted journal** plus crash-replay on start (see `SAFETY_POLICY.md`).
3. Policy engine: an allow/deny list (critical processes are hard-coded denies), per-process and global rate limits, foreground protection.
4. **Profiles** (JSON, schema-validated, bounded): `Monitor only` (default), `Balanced` (de-prioritise sustained background CPU hogs one step), `Focus` (raise the foreground app one step, EcoQoS for background). Each profile is a small declarative rule set; no scripts.
5. Heuristics with closed-loop evaluation: propose → apply → evaluate within N s → keep or revert.
6. IPC v2: `applyProfile`, `revertAll`, `resetBreaker`, `listChanges`, all Admin-only. Protocol version bump.
7. GUI: profile picker, active-changes list with per-item revert, breaker panel with reset.
8. Tests: fake process table for policy tests; property tests for "apply N changes then rollback = original state".

## Milestone 3: installer, uninstaller and full UI controls

1. **Installer: WiX v5 MSI** (per-machine). Installs the GUI and service, registers the service with the same hardened settings (required privileges, failure actions, delayed start, SID type), creates hardened data dirs, and adds a Start-menu shortcut. MSIX was evaluated: packaged services need the restricted `packagedServices` capability and Store/enterprise signing, and complicate LocalSystem ETW, so it's deferred. The GUI's self-install remains as "repair/dev" tooling.
2. Uninstall (MSI and GUI) runs the same sequence: stop (which triggers rollback) → verify an empty journal → delete service → stop any orphaned ETW session → remove files, config, logs and state. An upgrade keeps config.
3. Code signing (Authenticode) for all binaries; the service verifies the GUI signature on connect.
4. GUI: settings page (edits config via an admin-only IPC request that validates server-side, never a raw file write), log viewer (read-only, last N lines), tray icon with a minimal footprint mode, per-process drill-down.
5. Localization scaffolding (en, de).

## Milestone 4: tests, hardening and performance validation

1. Move to **.NET 10 LTS** (.NET 8 and 9 reach end of support on 10 Nov 2026).
2. Windows CI runner: integration tests that install the service in a VM, run `perftuner-cli --selftest`, and run the load test with budget gates (`PERFORMANCE.md`).
3. Fuzzing: SharpFuzz/libFuzzer on `MessageCodec.TryDecodeRequest` and `ConfigLoader.Parse`.
4. Security review: threat-model refresh, an elevated-helper review, and a pipe DACL test from a second standard account and a network logon.
5. Chaos tests: kill the service mid-change (journal replay), stop the ETW session externally (fallback), fill the disk (logging bounded), and flood IPC.
6. GUI footprint: ReadyToRun, trimming unused ScottPlot OpenGL assets, idle CPU measurement.
7. Evaluate splitting telemetry into a low-privilege `NT SERVICE\` account (Performance Log Users) and keeping LocalSystem only for tuning.
