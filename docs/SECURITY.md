# Security design and threat model

## Trust boundaries

```
 ┌──────────── standard user session ────────────┐        ┌──── LocalSystem (trimmed privileges) ────┐
 │ PerfTuner.exe (GUI, asInvoker)                 │  pipe  │ PerfTuner.Service.exe                    │
 │ perftuner-cli.exe                              │◄──────►│   ETW session, SCM-managed               │
 │ any other process of any local user  (hostile) │        │   %ProgramFiles%\PerfTuner (admin-only)  │
 └──────────────────────┬─────────────────────────┘        │   %ProgramData%\PerfTuner  (hardened)    │
                        │ UAC consent (runas)               └──────────────────────────────────────────┘
                 ┌──────▼────────────────────────┐
                 │ PerfTuner.exe --elevated-op X │  one allow-listed verb, no UI, exits
                 └───────────────────────────────┘
```

**Assets:** system stability (the service can change process scheduling in M2), service integrity (it runs as LocalSystem), user privacy (process names, IO activity).

## STRIDE on the GUI ↔ service boundary

| Threat | Scenario | Mitigations |
|---|---|---|
| **Spoofing (server)** | Malware creates `\\.\pipe\PerfTuner.v1` first and harvests GUI connections, or feeds fake data | Service creates the first instance with `FirstPipeInstance` and **refuses to serve** if the name is taken. The client checks `GetNamedPipeServerProcessId == SCM pid of PerfTunerSvc` before sending anything. Users lack `FILE_CREATE_PIPE_INSTANCE` on our pipe, so they can't add rogue instances. |
| **Spoofing (client)** | A process lies about who it is | Identity comes from the kernel-authenticated pipe token (`ImpersonateNamedPipeClient`), never from message content. `clientName` is cosmetic and allow-listed. |
| **Tampering** | Malformed, oversized, or schema-confusing frames | 4 KiB cap, structural guard (duplicate keys, depth), strict schema (unknown `type` or field → reject), semantic bounds, 3-strike disconnect. |
| **Tampering (files)** | User plants `config.json`, or replaces service binaries or a log dir junction | Binaries live in `%ProgramFiles%\PerfTuner` (admin-only). The data dir gets a **protected DACL** (SYSTEM/Admins full, Users read) that the service itself enforces at start-up before reading config. Reparse points are refused. Config size is capped (64 KiB) and fully validated, and anything invalid falls back to defaults. |
| **Repudiation** | Admin changes monitoring options without a trace | Structured logs of every session start/end, identity (pid, image, role), rejected requests (code only), and every mutating request. |
| **Information disclosure** | Remote or other-session snooping | Pipe DACL grants SYSTEM, Administrators and INTERACTIVE only, with **NETWORK explicitly denied**. Process names are the only per-process data, trimmed to 64 chars. No payloads are logged. |
| **Denial of service** | Connection floods, slowloris, request floods, slow readers | Max clients + busy answer, 5 s handshake timeout, token-bucket rate limit, 5 s write timeout, per-client 2-slot DropOldest push queue, bounded everything (see `ARCHITECTURE.md` §4). A session exception can't crash the service. |
| **Elevation of privilege** | Viewer triggers privileged action; code execution via IPC | Role = Admin only if the caller token is an **elevated** administrator (UAC-filtered tokens are deny-only). A fail-closed authorization table. No shell, no process launch, no file paths, no reflection or dynamic dispatch reachable from IPC. Identity-level impersonation means the service can identify the caller but never act as them. |

## Privilege separation

- **GUI**: `requestedExecutionLevel = requireAdministrator` since v0.9 (before: `asInvoker`, then `highestAvailable`). Almost every feature (security fixes, update cache cleanup, repair tools, page file, per-process tuning) needs administrator rights, so Windows asks once at start (explicit consent through UAC) instead of a "full power" restart. The GUI still never elevates itself in-process, and every change still needs a confirmation in the UI.
- **Elevated helper**: the same exe relaunched with `runas`, which shows the UAC prompt, so the user gives explicit consent. It accepts exactly one argument from `{install, start, stop, uninstall}`, derives all paths from its own location and known folders (no path crosses the boundary), writes a bounded log, and exits with a result code.
- **Service**: LocalSystem with a **required-privileges list**, so the SCM strips everything else from the token: `SeChangeNotify`, `SeSystemProfile` (kernel ETW), `SeIncreaseBasePriority` (M2), `SeImpersonate` (identify pipe callers). There's no SeDebug, SeTcb, SeLoadDriver, SeBackup/Restore or SeTakeOwnership. The service SID type is unrestricted, so resources can be ACL'd to `NT SERVICE\PerfTunerSvc` in M2. Recovery: restart after 5 s, then 60 s, then stop for the day, so there's no crash loop.
  - *Why not LocalService/virtual account?* A kernel ETW session and cross-session process management need administrative rights. Trimming LocalSystem's privileges gives most of the benefit. M4 evaluates splitting telemetry into a `NT SERVICE\` virtual account in the *Performance Log Users* group.

## Logging

- Serilog **compact JSON** (CLEF). Values are structured properties, never concatenated, so injected newlines can't forge entries. Client names are allow-listed anyway.
- **Bounded retention**: daily roll plus size roll (1–50 MB, default 10) and a retained-file count (1–30, default 7). The worst case is a fixed disk bound.
- **No secrets**: the product handles none. IPC payloads, tokens and environment are never logged, and error messages sent to clients never echo input.

## Verification

- Unit tests: `ProtocolTests` covers malformed, schema and bounds cases plus duplicate-key smuggling. `PathGuardTests` covers traversal.
- Live self-test: `perftuner-cli --selftest` runs 11 hostile-traffic checks against a running service (all passing on Windows 11 build 26100).
- M4: fuzzing the frame decoder (SharpFuzz), Authenticode check of the GUI image by the service, and an external review of the elevated helper.


## Standalone engine (v0.5)

- **What it is.** When no service is installed, the GUI starts `PerfTuner.Service.exe --console` as a hidden child with the GUI's own token. It runs in a kill-on-close job object, so it cannot outlive the GUI.
- **Who can connect.** Its pipe is per user (`PerfTuner.standalone.v1.<SID>`), and the DACL allows only that user plus SYSTEM, never NETWORK. The first pipe instance is created exclusively, so another process can't squat the name.
- **No borrowed elevation.** If the engine is elevated (it was started by an elevated GUI), a *non-elevated* process of the same user gets only the Viewer role. It cannot use the engine's elevation.
- **Stopping.** `shutdownEngine` is Admin-only and accepted only by a standalone engine; the installed service refuses it. It rolls back all live tuning before the engine exits.
- **Elevation prompt.** The GUI manifest is `highestAvailable`: administrators get one UAC prompt at launch, and standard users run limited (monitoring plus their own processes).

## Space explorer file operations (v0.8, deliberate amendment)

The original rule was "no arbitrary file operations from GUI inputs". On the owner's request, v0.8 lets the user delete and move files they select in the Space explorer. It stays safe because:

- **In-process, never over IPC.** Scanning and file operations run in the GUI process with the user's own rights (or the already-elevated GUI's). Nothing about files crosses the service pipe; the service is unchanged.
- **Only scanned items.** Operations accept nodes of the scanned tree (or report rows that point to them). Their paths come from directory enumeration under the chosen fixed-drive root — never typed, pasted or dropped. Right before acting, each path is re-checked: still under the scanned drive, still the same type, not a junction or symbolic link, not marked System.
- **Locked locations** (`ProtectionRules`, unit-tested): the drive root, `%WINDIR%`, Program Files, ProgramData, `System Volume Information`, `$Recycle.Bin`, `Recovery`, `Boot`, page / hibernation / swap files, the `Users` folder itself, other users' profiles, Default and Public, the user's profile root and main shell folders (their contents are allowed), `AppData\Local|LocalLow|Roaming` themselves (contents allowed with a warning), PerfTuner's own folders, anything with the System attribute, `..` segments and 8.3 short names.
- **Documented Shell API.** `IFileOperation` (Recycle Bin by default with `FOFX_RECYCLEONDELETE` + `FOF_WANTNUKEWARNING`; permanent delete only after a ticked confirmation; move with Windows' conflict dialogs). The move destination comes only from the Windows folder picker, must be on a local fixed drive, outside locked locations and outside the moved items.
- **Records.** A per-user journal (`%LOCALAPPDATA%\PerfTuner\file-operations.json`) keeps what happened, per real outcome reported by the progress sink. Logs and the activity list contain counts and sizes only, no file names.

## v0.9 additions

**Security page.** Read-only checks use documented interfaces only (Windows Security Center `WscGetSecurityProviderHealth`, Defender WMI `MSFT_MpComputerStatus`, `Win32_DeviceGuard`, `Win32_EncryptableVolume`, `Tbsi_GetDeviceInfo`, Windows Update Agent COM, `GetExtendedTcpTable`, the registry). Fixes are fixed registry values defined in code (`SecurityFix`), shown in full before they're applied, recorded in the settings journal first and undoable; a per-user fix may only write under `HKCU\SOFTWARE\Policies`. Nothing reads or stores secrets: the auto sign-in check only tests whether a `DefaultPassword` value exists. The "Open" buttons accept only `windowsdefender://` and `ms-settings:` addresses from the fixed check list.

**Update leftovers and repairs.** Cache cleanup stops and restarts the owning services and deletes inside two fixed folders only (no links followed). WinSxS cleanup runs Windows' own `StartComponentCleanup` scheduled task (Task Scheduler COM). Orphaned installer packages are computed from the MSI API's `LocalPackage` list; if that list is empty nothing is called orphaned, only top-level `.msi/.msp` files older than 30 days qualify, they are moved (never deleted) and "move back" only returns `.msi/.msp` files into exactly `%WINDIR%\Installer`. Repair Windows uses the DISM API (`dismapi.dll`) online session, not `dism.exe`. No command line or shell is started (since v0.10, winget.exe for Get apps is the only program started with arguments PerfTuner builds; see below).

**Release packages.** The launcher is a small framework-dependent single-file .NET program that runs on the machine-wide .NET 9 Desktop Runtime; it never unpacks code into a user-writable temp folder (the classic DLL-planting risk of self-extracting installers). Every PerfTuner exe is built with `AppHostDotNetSearch=Global`, so its .NET host ignores a `hostfxr.dll` placed next to it (e.g. in Downloads) and the `DOTNET_ROOT` variable, and only uses `%ProgramFiles%\dotnet` (verified: a planted `hostfxr.dll` next to the download is not loaded). It restricts native library loading to System32 (`SetDefaultDllDirectories` + `DefaultDllImportSearchPaths`), so a DLL planted next to it in Downloads isn't loaded. It opens its own exe once, without write/delete sharing, after checking that the opened file is the image Windows started (NT path of the handle = mapped image path), and reads the file list, hashes and content only through that handle: the package can't be swapped after UAC approved it. A missing or unknown footer is an error, never "uninstall" (the uninstaller has its own `U` footer). It unpacks only under `%ProgramFiles%\PerfTuner-Portable` / `%ProgramFiles%\PerfTuner-Setup`, where only administrators can create anything (so no other account can prepare a folder or a link there; folders not owned by Administrators/SYSTEM or links are refused). The payload is one Brotli stream: the file list (`payload.manifest`), then each distinct file's content in list order. Every listed file must have a safe relative path (`PayloadManifest.IsSafeRelativePath`, unit-tested: no `..`, drive, stream, forbidden characters) and must match its SHA-256 and size while being written; the portable copy is re-checked on every start. Setup copies into `%ProgramFiles%\PerfTuner`, checks each copied file's hash again, and records `install.manifest`; the uninstaller deletes only files listed there and never follows links, and it stops (keeping the Installed-apps entry) if PerfTuner can't undo its changes or remove its service. The "start at sign-in" task is limited to Administrators and starts PerfTuner elevated without a prompt (that's what the option means, and Setup says so); the app and engine disable .NET startup hooks (`StartupHookSupport=false`). Limitations: the packages are not Authenticode-signed yet; the integrity checks protect against corruption and non-admin tampering, not against an attacker who already has administrator rights; .NET profiler environment variables in the user's environment still apply to an elevated start, as for any .NET program.

**Undo records are not trusted.** The settings journal lives in the user profile, which any program the user runs can edit. Since v0.9 a registry undo record is only applied if it names a value PerfTuner itself writes (the settings catalog or a security fix); a service-path record only if the current value is exactly the quoted path PerfTuner wrote and the record holds its unquoted original. Orphaned installer packages are fingerprinted (SHA-256) before they're moved, in an administrators-only file (`%ProgramData%\PerfTuner-Undo\installer-packages.json`); "move back" writes a fresh copy into `%WINDIR%\Installer` only if the content still matches, so a file swapped in the (user-writable) destination folder never reaches the installer cache. The orphan check refuses to run while an installation is in progress and stops on any Windows Installer error instead of guessing.

## v0.10 additions

**Get apps (Windows Package Manager).** The only program PerfTuner starts with arguments it builds itself (besides its own engine; Windows' own panels are opened by their fixed System32 paths). winget downloads installers into `%TEMP%`, so PerfTuner points winget's TEMP/TMP at the administrators-only `%ProgramData%\PerfTuner-Undo\winget-temp`: an installer can't be swapped between winget's hash check and its start. User-scope apps install for the account PerfTuner runs as. `winget.exe` is located through the documented package API (`GetPackagesByPackageFamily` / `GetPackagePathByFullName` for `Microsoft.DesktopAppInstaller_8wekyb3d8bbwe`) and must live under `%ProgramFiles%\WindowsApps`; the per-user execution alias in `%LOCALAPPDATA%\Microsoft\WindowsApps` (a user-writable folder) is never used. Arguments are an array of fixed words plus a package identifier that must be in the built-in catalog and match a strict pattern (`WinGetCommands.Arguments`, unit-tested); no shell, no command line, nothing typed by the user. The installed-apps check writes winget's export to the administrators-only `%ProgramData%\PerfTuner-Undo` and deletes it afterwards. Packages come from Microsoft's community repository; PerfTuner doesn't verify installers itself — winget checks each installer's SHA-256 against the repository manifest.

**Windows features.** Turned on and off through the DISM API (`DismEnableFeature` / `DismDisableFeature`, `DismAddCapability` / `DismRemoveCapability`) for a fixed list of features; one DISM session at a time.

**DNS.** IPv4 DNS servers are set with WMI `Win32_NetworkAdapterConfiguration.SetDNSServerSearchOrder`, only to preset addresses. The previous setting of each adapter is saved in the administrators-only `%ProgramData%\PerfTuner-Undo\dns.json` (owner and link checked when read); restore re-validates every address and only touches adapters that are present; saved settings of adapters that aren't connected are kept for the next restore. If the saved file exists but can't be trusted or read, PerfTuner refuses to change DNS rather than lose the original. Only IPv4 DNS is changed: IPv6 DNS servers handed out by the network stay in use, so blocking resolvers can be bypassed over IPv6.
