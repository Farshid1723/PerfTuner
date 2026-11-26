## What's new in 0.11

- **Undocumented page (opt-in, might be dangerous).** About 50 hidden Windows settings, each labelled with what it really does: works, niche, placebo, no effect, risky, security risk or setup only. Classic right-click menu, classic Alt+Tab, no startup delay, hidden power options, timer and boot settings, network and gaming tweaks, the well-known myths, and the risky ones. PerfTuner asks before every change (risky ones twice) and can undo them all; machine-wide undo records are kept where only administrators can write.
- **Virtual desktops:** desktop number in the notification area, the desktop's name when you switch, and Ctrl+Win+Alt+1…9 / 0 to jump between desktops.
- **Network repair** on Troubleshoot: clear DNS cache, renew IP address, reset Winsock, reset TCP/IP.
- **Built-in apps and Store repair** on Get apps: remove built-in apps you don't use, clear the Store cache, re-register the Store or all your apps.
- **Windows page:** search indexing on/off; Filter Keys and Toggle Keys pop-ups off, no system beep, no notification centre, faster service shutdown, no error reports.
- **Mode shortcuts:** Ctrl+Alt+Shift+F1 Off, F2 Turbo, F3 Smart, F4 Silent (Settings › Behaviour).

## What's new in 0.10.2

- **Scan & Fix shows its details.** Every finding has *Show details*: the settings, apps, leaking processes, drives or startup apps behind it. The Windows settings finding lists each setting with what it does and when it takes effect, and only the ones you tick are applied.
- The scan suggests only speed-and-convenience settings; privacy, look-and-feel and Windows Update settings stay on the Windows page for you to choose. Fast Startup is no longer counted when hibernation is off.

## What PerfTuner does

A Windows 11 performance, maintenance and security tool that explains every action before you apply it and can undo every change it makes.

- **Speed:** Turbo, Smart and Silent modes, free up RAM, automatic fixes that prove they help, startup analysis.
- **Clean:** junk files, Windows Update leftovers, orphaned installer packages (moved, never deleted), drive optimization, and a Space explorer with tree, treemap and sunburst views.
- **Security:** a plain-language check-up with reviewed, undoable fixes.
- **Windows:** 35+ documented settings (speed, privacy, Windows behaviour, Windows Update), optional Windows features, and the DNS provider.
- **Get apps:** install, update and remove popular apps in one go with Microsoft's Windows Package Manager.
- **Repair:** Repair Windows, reset Windows Update, restore points and classic control panels.

## Downloads

| File | What it is |
|---|---|
| **PerfTuner-Setup.exe** | Installer: installs PerfTuner for all users in `C:\Program Files\PerfTuner`, with Start-menu entry, optional desktop shortcut, optional start at sign-in and optional always-on engine. Uninstall from *Settings › Apps › Installed apps*. Silent install: `PerfTuner-Setup.exe /S`. |
| **PerfTuner-Portable.exe** | Runs without installing. Unpacks once per version into an administrators-only folder and checks every file before each start. |
| **SHA256SUMS.txt** | SHA-256 of both files. |

Both need Windows 10 2004 or later / Windows 11 (x64) and administrator rights. PerfTuner runs on Microsoft's free **.NET 9 Desktop Runtime (x64)**, which is why the downloads are only a few megabytes. If it isn't installed, Windows shows Microsoft's prompt to download it: install it, then start the download again.

The files aren't code-signed yet, so Windows SmartScreen may show "Windows protected your PC" the first time: choose *More info › Run anyway* if you trust this release.
