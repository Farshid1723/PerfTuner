# Names in PerfTuner (v0.10)

Since v0.6, the app uses **plain names** that tell you what something does. The few brand names that remain are for the three modes, and each one has a plain tagline.

## Pages (left sidebar)

| Page | What you do there |
|---|---|
| **Home** | Health score, live CPU / memory / disk / network, the mode switch, quick actions, and what PerfTuner did recently |
| **Cockpit** | CPU, memory, disk and network as animated hardware; click a part for details: disk problems and tools, RAM and virtual memory (page file) with a recommendation, network adapters. **Space explorer** ("Find what's using space" on a drive): size tree, treemap or sunburst, reports (including duplicate files), delete / move |
| **Security** | A plain-language security check-up in four groups (Protection, Device security, Network exposure, Attacker's-eye view), a score, and fixes you review and approve; every fix can be undone in History |
| **Speed** | Pick a mode, free up RAM, turn on automatic fixes, and (optionally) adjust power settings |
| **Clean** | Remove junk files, Windows Update leftovers (update caches, old components, orphaned installer packages), optimize a drive |
| **Windows** | Documented Windows settings in groups (speed, privacy, Windows behaviour, Windows Update), optional Windows features, DNS provider |
| **Startup** | Last boot time and trend, recommendations, and a sortable table of startup apps with their impact |
| **Get apps** | Install, update and remove apps from a built-in list in one go (Microsoft's Windows Package Manager) |
| **Apps** | Pick a running app and make it faster, slower or more efficient, or free its memory; set "every time it starts" rules |
| **Troubleshoot** | Everyday questions with a Check button, a plain answer and a fix button; repair tools (Repair Windows, Reset Windows Update, restore point) and classic Windows panels |
| **History** | Everything that changed (undo one, or undo all) and everything PerfTuner did on its own |
| **Settings** | Colour theme (6 classic + 21 metal), notifications, tray, automatic fixes, background service (optional always-on service) |
| **Help** | Every topic explained in plain words. The small **?** next to a section title shows a short hint; *More help* opens that topic here |
| **About** | Who created PerfTuner, the source-code repository, what it can do and why you can trust it |

## The three modes

Only one mode can be on at a time. Switching mode, or choosing **Off**, first undoes everything the previous mode changed.

| Mode | Tagline | What it does |
|---|---|---|
| **Turbo** | Maximum speed for the app you're using | The app in front gets priority (this follows you when you switch apps); busy background apps go into efficiency mode; the power plan switches to High / Ultimate Performance, or aggressive turbo boost if Windows has no such plan |
| **Smart** | Hands-off care, all day | Background apps that hog the CPU for 30 s are calmed down; when free RAM falls below 20 %, memory is freed automatically (at most every 3 minutes, and it reports how much); the power plan is not touched |
| **Silent** | Cooler, quieter, longer battery | CPU turbo boost is turned off and the CPU is capped at 80 %, for much less heat and fan noise; every busy background app runs in efficiency mode |

Everything a mode does automatically appears under **What PerfTuner did** and as a tray notification. Notifications are limited to one per source every 45 s.

## Other names you'll see

- **Automatic fixes** (formerly Autopilot / DriftWatch / ProofRun): PerfTuner learns what's normal for this PC. When something gets slower, it tests a fix (30 s measured before, 30 s after) and keeps it only if it measurably helps.
- **Memory care** (formerly MemGuard): the automatic memory release that Smart mode uses.
- **Scan & Fix**: the one-button checkup on Home.

In the source code, `src/PerfTuner.App/Brand.cs` maps the engine's internal owner names to these labels.

## Downloads

| File | What it is |
|---|---|
| **PerfTuner-Portable.exe** | Runs without installing (unpacks once per version into an administrators-only folder and checks every file on each start) |
| **PerfTuner-Setup.exe** | The installer: Program Files, Start menu, optional desktop shortcut, start at sign-in and always-on engine; uninstall from *Installed apps* |
| **PerfTuner-Uninstall.exe** | Put next to the installed app by Setup; undoes PerfTuner's changes, then removes it |
