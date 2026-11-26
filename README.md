# PerfTuner — Win11 Performance Tuner

*Faster, calmer Windows — every change undoable.*

A low-overhead Windows 11 performance, maintenance and security tool written in C#/.NET 9. It explains every action in plain words before you apply it, records the previous state, and can undo everything it changed.

## Download

Get the latest release from the **Releases** page:

| File | What it is |
|---|---|
| **PerfTuner-Setup.exe** | Installer (≈3 MB): `%ProgramFiles%\PerfTuner`, Start-menu shortcut, optional desktop shortcut, optional start at sign-in, optional always-on engine service, an entry in *Settings › Apps › Installed apps*, in-place upgrades, `/S` for a silent install. Uninstall undoes PerfTuner's changes to Windows settings before removing the files. |
| **PerfTuner-Portable.exe** | No installation (≈3 MB): unpacks once per version into an administrators-only folder (`%ProgramFiles%\PerfTuner-Portable`), checks every file against its SHA-256 on each start, and runs from there. |
| **SHA256SUMS.txt** | Checksums of both files. |

**Requirements:** Windows 10 2004+ or Windows 11 (x64) and Microsoft's free **.NET 9 Desktop Runtime (x64)**. If the runtime is missing, Windows shows Microsoft's prompt with the download link; install it and start PerfTuner again. PerfTuner asks for administrator rights when it starts, because almost everything it does needs them. The files aren't code-signed yet, so SmartScreen may warn the first time.

## What it does

| Page | |
|---|---|
| **Home** | Health score, live CPU / memory / disk / network, the mode switch (Off · Turbo · Smart · Silent), Scan & Fix |
| **Cockpit** | CPU, memory, disk and network as animated hardware; disk details and tools (analyze, optimize, scan, fix at restart), RAM and page file with a measured recommendation; the **Space explorer** (size tree, treemap or sunburst, reports including duplicate files, safe delete and move) |
| **Security** | A plain-language check-up of protection, device security, network exposure and an attacker's-eye view (unquoted service paths, writable PATH folders, auto sign-in, AutoRun…); fixes show exactly what they change and can be undone |
| **Speed** | Modes, free up RAM, automatic fixes, power settings |
| **Clean** | Junk files, Windows Update leftovers (download and delivery caches, old components, orphaned installer packages: moved, never deleted), optimize a drive |
| **Windows** | 40+ documented Windows settings in groups (speed, privacy, Windows behaviour, Windows Update), optional Windows features (Linux subsystem, Sandbox, Hyper-V, .NET 3.5, OpenSSH server…), search indexing on/off, DNS provider with restore |
| **Undocumented** | Opt-in, behind a warning: about 50 hidden Windows settings, each labelled with what it really does (works, niche, placebo, no effect, risky, security risk); classic right-click menu, startup delay, timer and boot settings, virtual-desktop number, name overlay and hotkeys… Every change asks first and can be undone |
| **Startup** | Boot time and trend, recommendations, startup apps with their impact |
| **Apps** | Make a running app faster, slower or more efficient; rules for every time it starts |
| **Get apps** | Install, update and remove 119 popular apps in one go through Microsoft's Windows Package Manager; remove built-in Windows apps; repair Microsoft Store |
| **Troubleshoot** | Everyday questions with a plain answer and a fix; Repair Windows, reset Windows Update, restore point, network repair (DNS cache, IP address, Winsock, TCP/IP), classic control panels |
| **History** | Everything that changed (undo one or everything) and everything PerfTuner did on its own |
| **Settings, Help, About** | 27 colour themes, notifications, tray, the optional engine service; every topic explained |

How it stays safe: documented Windows interfaces only (no drivers), except on the opt-in Undocumented page, which says what each hidden setting really does; fixed and allow-listed changes (no free-form commands), every change journaled and undoable, protected Windows and program folders locked in the Space explorer, administrator-only storage for undo records of anything applied machine-wide. Apart from its own engine and Windows' own panels, PerfTuner starts only Microsoft's `winget.exe` (from its protected package folder) and Windows' `bcdedit`, `netsh` and `WSReset` (from System32), always with fixed arguments. Details: [SECURITY.md](docs/SECURITY.md) and [SAFETY_POLICY.md](docs/SAFETY_POLICY.md).

| Measured on Windows 11 (20 threads) | |
|---|---|
| Service CPU | 0.013 % of machine (budget 0.5 %) |
| Service private memory | 15–19 MB (budget 100 MB) |
| ETW event hot path | 19.6 ns, 0 bytes allocated |

## Documentation

| Doc | Covers |
|---|---|
| [FEATURES.md](docs/FEATURES.md) | Every tuning function, the API it uses, its bounds and how it's undone |
| [FORENSICS.md](docs/FORENSICS.md) | F1–F9: stall explanation, regressions, experiments, memory governor, contracts, IO/network/wait-state visibility, telemetry health |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | WPF vs WinUI 3, two-process decision, modules, repo tree, monitoring pipeline, ring buffers, threading |
| [IPC_PROTOCOL.md](docs/IPC_PROTOCOL.md) | Message types and fields, validation pipeline, auth, error codes, bandwidth budget |
| [SECURITY.md](docs/SECURITY.md) | Threat model (STRIDE), pipe ACL, privilege separation, logging policy |
| [SAFETY_POLICY.md](docs/SAFETY_POLICY.md) | Tuning bounds, rollback design, circuit breaker triggers and behaviour |
| [PERFORMANCE.md](docs/PERFORMANCE.md) | Budgets, low-overhead techniques, measured results, how to verify |
| [ROADMAP.md](docs/ROADMAP.md) | Version history and what comes next |
| [NAMES.md](docs/NAMES.md) | The names used in the app |

## Prerequisites

- Windows 10 2004+ / Windows 11, x64
- To run: **.NET 9 Desktop Runtime (x64)**
- To build: **.NET 9 SDK** (Windows, or Linux/WSL: `EnableWindowsTargeting` is on)

## Release packages

`build/release/release.sh <out-dir>` publishes the app and engine (framework-dependent), the small launcher (`tools/PerfTuner.Bootstrap`, a single-file exe of about 350 KB that may only use the machine-wide .NET install) and runs `tools/PerfTuner.Pack`, which compresses the app with Brotli (identical files stored once), adds its `payload.manifest` (SHA-256, size and path of every file) and a footer to the launcher: once as **PerfTuner-Portable.exe**, once as **PerfTuner-Setup.exe**. Setup writes the launcher without the app, marked as uninstaller, next to the installed app as `PerfTuner-Uninstall.exe`. `SHA256SUMS.txt` lists the hashes of both downloads.

## Build and test

```powershell
dotnet build PerfTuner.sln -c Release          # warnings are errors
dotnet test  tests/PerfTuner.Core.Tests          # runs on any OS
./build/publish.ps1                              # → artifacts/PerfTuner (GUI + Service\) and artifacts/cli
```

## Run

### A. Standalone (default: nothing installed)

Run `PerfTuner\PerfTuner.exe`. It starts `Service\PerfTuner.Service.exe --console` hidden, with the app's own rights, on a per-user pipe (`PerfTuner.standalone.v1.<your SID>`), and keeps its data in `%LOCALAPPDATA%\PerfTuner\Engine`. Closing the window keeps PerfTuner in the tray; **Exit** in the tray menu shuts everything down and undoes live tuning. `PerfTuner.exe --tray` starts minimized to the tray, and `--page N` opens a page directly.

### A2. Developer mode (engine started by hand)

Two terminals:

```powershell
# 1) backend in the console, as you, on your per-user standalone pipe
PerfTuner\Service\PerfTuner.Service.exe --console

# 2) GUI (or the CLI) against the dev instance
PerfTuner\PerfTuner.exe --dev
perftuner-cli.exe --dev --samples 10
perftuner-cli.exe --dev --selftest
perftuner-cli.exe --dev --tuning-selftest   # applies + verifies + reverts every action on a throw-away ping.exe
perftuner-cli.exe --dev --experiment-selftest  # a real A/B experiment end to end (~90 s)
perftuner-cli.exe --dev --forensics        # timeline, explain, IO / network / memory / regressions / contracts
```

In developer mode the GUI gets full tuning rights without UAC: the console service runs as you, so no privilege boundary is crossed.

Without elevation, kernel ETW is unavailable, so disk and network come from the PDH fallback (system totals only; no per-process list). Engine data lives in `%LOCALAPPDATA%\PerfTuner\Engine`. Delete that folder to clean up. Press Ctrl+C to stop the console service.

### B. Installed service (production path)

1. Run `PerfTuner\PerfTuner.exe`. The GUI runs as your normal user.
2. **Service → Install / Update** and approve the Windows permission prompt. This:
   - copies the service to `%ProgramFiles%\PerfTuner\Service` (admin-only)
   - creates `%ProgramData%\PerfTuner` with a hardened ACL and a default `config.json`
   - registers `PerfTunerSvc` (LocalSystem with a trimmed privilege set, delayed auto-start, bounded restart policy) and starts it
3. The dashboard connects automatically. With the service, kernel ETW is active and **Top IO processes** fills in.
4. **Stop** and **Uninstall** also ask for permission. Uninstall stops the service (rolling back any changes), unregisters it, stops any leftover ETW session, and deletes the program files, config, logs and state. If the service won't stop, nothing is removed.

Equivalent without the GUI, from an elevated prompt: `PerfTuner.exe --elevated-op install|start|stop|uninstall`. The helper logs to `%LOCALAPPDATA%\PerfTuner\helper.log`.

### Configuration

`%ProgramData%\PerfTuner\config.json` (see `src/PerfTuner.Service/config.default.json`) is admin-editable and read at service start. Every field is bounds-checked. Any invalid value, unknown field, or file over 64 KiB makes the service **fall back to built-in safe defaults**, and the reason appears in the GUI under *Config warnings*.

### Logs

`%ProgramData%\PerfTuner\logs\service-*.clef` holds compact JSON, rolled daily and by size, with retention bounded by config. Each line is a JSON object, readable with any CLEF-compatible viewer or a text editor.

## Project layout

```
src/PerfTuner.Contracts         wire protocol (portable)
src/PerfTuner.Core              engine logic (portable, fully unit-tested)
src/PerfTuner.Platform.Windows  all Win32 interop
src/PerfTuner.Service           Windows service
src/PerfTuner.Client            IPC client + elevated operations
src/PerfTuner.App               WPF GUI
tools/PerfTuner.Cli             diagnostics / security self-test
tests/                          unit tests, benchmarks
```

## License

PerfTuner is released under the [MIT License](LICENSE). Apps installed through the Get apps page are their publishers' software under their own licences.
