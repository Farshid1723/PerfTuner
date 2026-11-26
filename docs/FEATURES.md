# Features

> Every feature also has a branded name (FocusBoost, StallSense, ProofRun, …). See [NAMES.md](NAMES.md). The tables below use the technical descriptions.

Tuning requires the **Admin** role. Start the app normally and click **Restart as administrator** in the yellow banner; Windows asks for consent (UAC). Without it the app is read-only monitoring.

## Processes tab: per-process actions (through the service)

| # | Action | Windows API | Bounds / safety | Undo |
|---|---|---|---|---|
| 1 | Priority: Below normal / Normal / Above normal | `SetPriorityClass` | Never High or Realtime; a process already at High/Realtime is left alone | Restores the captured class |
| 2 | Efficiency mode on/off (as in Task Manager) | `SetProcessInformation(ProcessPowerThrottling)` + Below-normal priority | — | Restores the throttling state and priority |
| 3 | CPU cores: efficiency cores / performance cores / half the cores | CPU Sets (`SetProcessDefaultCpuSets`) | ≥ 2 logical CPUs; E/P options only on hybrid CPUs | Restores the original CPU sets |
| 4 | Trim memory now | `EmptyWorkingSet` | One-shot, harmless (pages fault back on use) | — |
| 5 | Low / normal memory priority | `SetProcessInformation(ProcessMemoryPriority)` | — | Restores the original |
| 6 | Soft memory cap 512 MB / 1 GB | `SetProcessWorkingSetSizeEx` with **soft** limits | 64 MB – 64 GB; never a hard cap | Restores min, max and flags |
| 7 | "Always …" app rules (efficiency, below/above normal, efficiency cores) | as above | Max 32 rules; applied only with Safe Mode off | Remove the rule, and its changes are reverted |
| + | **End process** (irreversible) | `TerminateProcess` | Confirmation dialog **and** `confirm:true` in the protocol | — |

For every action, the service re-checks the target identity (PID **and** creation time), the policy, rate limits and the circuit breaker. Session-0 system services and a fixed list of Windows, shell and security processes (explorer, dwm, lsass, svchost, Defender, …) can never be changed.

## Tuning tab: automatic features (Safe Mode off)

| # | Feature | Behaviour |
|---|---|---|
| 8 | **Focus / Game mode** | Above-normal priority for the app you're using. Efficiency mode for background apps using ≥ 2 % CPU (max 32). Follows the foreground app as you switch. |
| 9 | **Auto-calm** | A background app over the threshold (default 25 %) for 30 s gets efficiency mode. It's released after 2 min below half the threshold, or at once when you switch to it. |
| 10 | **Trim background memory** | One click trims every background app above 32 MB (skips the foreground app and protected processes). |
| 11 | **Quiet mode** | Efficiency mode for every background app using ≥ 1 % CPU (max 48). No boosting. |
| — | **Safe Mode** (default on) | Nothing happens automatically; only what you click. Turning it on reverts all automatic changes immediately. |

Automatic features only throttle something new when the foreground app is known. The service can't see your desktop, so the elevated GUI reports it every 2 s, and minimised is fine. At most 20 automatic actions per minute and one per process every 30 s.

## System tab: persistent Windows settings (in the elevated GUI, journaled)

| # | Setting | API | Undo |
|---|---|---|---|
| 12 | Switch power plan | `PowerSetActiveScheme` | Previous plan |
| 13 | Processor boost mode, min/max processor state (max ≥ 50 %) | `PowerWrite{AC,DC}ValueIndex` | Previous values |
| 14 | Visual effects → best performance; transparency on/off | `SystemParametersInfo`, `Personalize\EnableTransparency` | Previous values |
| 15 | Startup apps enable/disable | `Explorer\StartupApproved` (same as Task Manager; the app's own entry is untouched) | Previous raw value |
| 16 | Optimize drive (TRIM for SSD, defragment for HDD) | `MSFT_Volume.Optimize` (Storage Management API) | Not needed (maintenance) |
| + | **Temp cleanup** (files older than 2 days; irreversible, preview + confirmation) | file APIs, no reparse points | — |

These settings are **kept** until you revert them (they're deliberate choices). The undo log lives at `%LOCALAPPDATA%\PerfTuner\settings-journal.json`, and **Uninstall restores them all**.

## Changes tab: safety and visibility

| # | Feature |
|---|---|
| 17 | Every active change listed, each with its own **Revert** button (live tuning and system settings separately) |
| 18 | **Revert everything…** (live tuning + all system settings) |
| 19 | Circuit-breaker status and **Reset breaker** |
| 20 | Activity history (last 200 service events plus local actions) |

## Insights tab (v0.3, diagnostics only)

| # | Feature | Source |
|---|---|---|
| 21 | CPU clock and throttling: effective GHz, % of base, **"% Performance Limit"** and share of recent time limited | PDH `Processor Information` |
| 22 | Memory-leak detector: steady, unbounded private-memory growth per app (linear fit, R², ≥ 15 min) | process snapshots |
| 23 | Driver latency scan: worst DPC/ISR per driver (5–60 s) | on-demand kernel ETW |
| 24 | Boot and shutdown analysis: durations and recurring slow apps, services and drivers | `Diagnostics-Performance` event log |
| 25 | Drive health: wear, temperature, errors | Storage Management API |

## System tab additions (v0.3, journaled, reverted on uninstall)

| # | Setting |
|---|---|
| 26 | Windows 11 switches: "End task" in the taskbar, no web results in Start search, Game DVR background recording off, snappier menus, update sharing on the local network only |
| 27 | Per-app GPU preference (power saving / high performance), from the Processes tab |
| 28 | Enhanced pointer precision on/off |

## Forensics tab and autonomy (v0.4)

See **[FORENSICS.md](FORENSICS.md)** for the algorithms, bounds and verification.

| # | Feature |
|---|---|
| 29 | **Causality timeline** of stalls, throttling, bursts, regressions and every tuning action |
| 30 | **Explain this stall**: ranked causes with evidence and confidence, plus a one-click fix to test |
| 31 | **Regression autopilot**: learns this PC's baseline and flags lasting regressions with suspected causes and suggestions |
| 32 | **A/B experiment mode**: 30 s baseline vs 30 s treatment; kept only when proven, rolled back otherwise; persisted audit trail |
| 33 | **Memory governor**: classifies memory *behaviour* (stable / burst / churn / pressure / thrashing) and acts only when that helps |
| 34 | **Safe tuning contracts** and **profiles** (Safe / Balanced / Advanced) |
| 35 | **Disk IO latency histograms**: p50/p90/p99, queue vs device time, per process |
| 36 | **Network microburst detector** at 100 ms resolution, with retransmission health |
| 37 | **CPU wait-state lens**: running vs waiting for a core vs blocked on IO, paging or locks |
| 38 | **Telemetry health / freshness** indicator in the header |

## Cockpit, disk and virtual memory (v0.7)

| Feature | How | Rights | Undo |
|---|---|---|---|
| Animated hardware tiles | `HardwareIcon` vector drawings, 30 fps clock, stopped when hidden/minimized, static if Windows animations are off | user | – |
| Status per part | `ResourceStatus` (Core, unit-tested): CPU 60/90 % or throttled; memory available 30/15 % or commit > 90 %; disk p99 latency 30/150 ms or throughput > 4× median; network retransmits 0.5/2 % | user (latency/loss: admin) | – |
| Volume details | `MSFT_Volume`, `MSFT_Partition`; NTFS/ReFS version via `FSCTL_GET_NTFS_VOLUME_DATA` / `FSCTL_GET_REFS_VOLUME_DATA`; dirty flag via `FSCTL_IS_VOLUME_DIRTY` (handle from a validated letter, `FILE_READ_ATTRIBUTES`) | version/dirty: admin | – |
| Analyze / Optimize | `MSFT_Volume.DefragAnalysis` / `MSFT_Volume.Optimize` | admin | n/a |
| Scan for errors / Fix at next restart | `MSFT_Volume.Repair` (Scan / OfflineScanAndFix), consent dialog, never restarts the PC | admin | repair (not undoable) |
| Memory and page files | `GetPerformanceInfo`, `Win32_PageFileUsage`, `Win32_PageFileSetting`, `Win32_ComputerSystem.AutomaticManagedPagefile`, `CrashControl\CrashDumpEnabled` (read) | user | – |
| Page-file advice | `PageFileAdvisor` (Core, unit-tested): no page file → too small/near limit → HDD-only with roomy SSD → keep | user | – |
| Page-file change | let Windows manage it / add on SSD / fixed size (bounded slider); Windows drive always keeps a page file; never disabled | admin | `MachineJournal` (%ProgramData%\PerfTuner, admin-only), drift check, uninstall reverts |

## Space explorer (v0.8)

| Feature | How | Rights | Undo |
|---|---|---|---|
| Drive scan | `FileSystemEnumerable` (FindFirstFileEx), ≤ 3 background-mode threads, links not followed, cloud-only files = 0 B, > 3 M files → small files summarised per folder | user (admin reads more) | – |
| Tree / Treemap / Sunburst | Custom WPF drawing (`TreemapView`, `SunburstView`), squarified treemap, sunburst with tiny items merged; layouts unit-tested | user | – |
| Reports | `SpaceReports` (Core): biggest files/folders, old & big (≥ 100 MB, ≥ 1 year), oldest, by type, per-type totals | user | – |
| Delete | `IFileOperation`, Recycle Bin (`FOFX_RECYCLEONDELETE` + nuke warning) | user | restore from the Recycle Bin |
| Delete permanently | `IFileOperation` after a ticked confirmation | user | not undoable |
| Move | `IFileOperation.MoveItem`, destination from `OpenFolderDialog` | user | "Move back" in History |
| Protection | `ProtectionRules` (Core, unit-tested) + re-check before acting | – | – |

## Security, update leftovers and repairs (v0.9)

| Feature | How | Rights | Undo |
|---|---|---|---|
| Security check-up (21 checks) | WSC, Defender/DeviceGuard/BitLocker WMI, TBS, WUA COM, IP Helper, registry, ACLs of PATH folders | admin (some checks work without) | – (read-only) |
| Security fixes (UAC, LSA protection, SMB1 server, RDP off / NLA, LLMNR, unquoted service paths, AlwaysInstallElevated, auto sign-in, AutoRun) | fixed registry values, confirmation dialog listing every value | admin | History › Changes to Windows settings |
| Privacy and Explorer settings (13 new) | documented policy/registry values | user or admin | switch off / History |
| Update download + Delivery Optimization caches | stop service → delete contents → restart | admin | not undoable (Windows re-downloads what it needs) |
| WinSxS component cleanup | Windows' `StartComponentCleanup` task | admin | not undoable (Windows' own cleanup) |
| Orphaned installer packages | MSI API `LocalPackage` list vs `%WINDIR%\Installer` | admin | moved, "Move back" in History |
| Repair Windows | DISM API: check, then restore health | admin | – |
| Reset Windows Update | stop services, rename SoftwareDistribution / catroot2 with a date, restart | admin | old folders kept |
| Restore point | WMI `SystemRestore.CreateRestorePoint` | admin | – |
| Duplicate files | same size (scan) → 64 KB SHA-256 → full SHA-256 | user | delete with the normal protected delete |

## Apps, Windows features and DNS (v0.10)

| Feature | How | Rights | Undo |
|---|---|---|---|
| Install / update / uninstall catalog apps, update all | Microsoft's `winget.exe` from its package folder, fixed arguments, catalog ids only | admin | uninstall / reinstall |
| Installed check | `winget export` to an admin-only folder | admin | – (read-only) |
| Windows features (WSL, Sandbox, Hyper-V, .NET 3.5, legacy media, NFS, Telnet, OpenSSH server) | DISM API | admin | switch off again |
| Windows Update behaviour (defer upgrades a year, monthly updates 4 days, no driver updates) | Windows Update for Business policies | admin | History / switch off |
| More settings (location, background apps, promoted apps, Start recommendations, dark mode, taskbar, Explorer, scrollbars, Num Lock, crash details, sign-in blur, Game Mode) | documented registry values and policies | user or admin | switch off / History |
| DNS provider | WMI `SetDNSServerSearchOrder` | admin | "Restore previous" (saved per adapter) |

## Undocumented page, repairs and shortcuts (v0.11)

| Feature | How | Rights | Undo |
|---|---|---|---|
| Undocumented settings (~50 in 10 groups, each with a verdict: works, niche, placebo, no effect, risky, security risk, setup only) | fixed registry values and keys, `bcdedit` for five boot elements, service start types, one desktop folder | user or admin | switch off / History (machine-wide changes in an admin-only journal) |
| Empty Prefetch, clear all event logs | file delete in `%WINDIR%\Prefetch`; `EvtClearLog` for every channel | admin | not undoable |
| Virtual desktops: number in the tray, name overlay, Ctrl+Win+Alt+1…9 / 0 | Explorer's desktop list in `HKCU` + `RegNotifyChangeKeyValue`; Windows' Win+Ctrl+←/→ via `SendInput` | user | switch off |
| Mode shortcuts Ctrl+Alt+Shift+F1…F4 | `RegisterHotKey` | user | switch off |
| Network repair: DNS cache, renew IP, Winsock, TCP/IP | WMI `MSFT_DNSClientCache`, `Win32_NetworkAdapterConfiguration`; `netsh` (fixed arguments) | admin | – (repairs) |
| Remove built-in apps (fixed list) | PackageManager `RemovePackageAsync` (current user) | user | reinstall from Microsoft Store |
| Store repair, re-register apps | `WSReset.exe`; PackageManager `RegisterPackageAsync` | user | – |
| Search indexing on/off | `WSearch` start type (`ChangeServiceConfig`) | admin | switch on / History |
| Filter Keys and Toggle Keys pop-ups, system beep, notification centre, faster service shutdown, error reporting | documented registry values and policies | user or admin | switch off / History |

## Deliberately not included

Hidden Windows feature ids (free-form numbers through an undocumented API), standby-list purging and anything that needs a driver. Everything that is undocumented lives only on the opt-in Undocumented page, labelled with what it really does.
