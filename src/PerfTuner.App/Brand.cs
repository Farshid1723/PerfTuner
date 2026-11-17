namespace PerfTuner.App;

/// <summary>A named, advertisable feature: short brand name, plain-language tagline, hover hint and icon glyph (Segoe Fluent Icons).</summary>
public sealed record Feature(string Name, string Tagline, string Hint, string Glyph);

/// <summary>
/// Every user-facing feature name in one place, so the GUI, tray menu and docs always agree.
/// Names are short and unique; the tagline says in plain words what the feature does; the hint
/// holds the detail that used to be on-screen text.
/// </summary>
public static class Brand
{
    public const string Product = "PerfTuner";
    public const string Slogan = "Faster, calmer Windows — every change undoable.";

    // ---------------------------------------------------------------- pages
    public static Feature Pulse { get; } = new("Pulse", "Live health at a glance",
        "Your PC's vital signs in real time, with a single PulseScore from 0 to 100.", "");
    public static Feature AppPilot { get; } = new("AppPilot", "Control any app",
        "Pick an app and change its priority, CPU cores, memory or GPU. Every change can be undone in UndoVault. Windows and security processes are protected.", "");
    public static Feature Boost { get; } = new("Boost", "One-click speed modes",
        "Modes that act automatically for you, and the TuneLevel that decides how much the app may do on its own.", "");
    public static Feature WinTune { get; } = new("WinTune", "Windows settings, the safe way",
        "Power, visuals, startup apps and cleanup. Each change records the old value and can be undone in UndoVault (and on uninstall).", "");
    public static Feature StallSense { get; } = new("StallSense", "Why was my PC slow?",
        "Finds freezes and slowdowns and explains the cause in plain words, with the evidence.", "");
    public static Feature HealthCheck { get; } = new("HealthCheck", "Hidden problems, found",
        "Throttling, memory leaks, driver lag, slow boots and drive health.", "");
    public static Feature Autopilot { get; } = new("Autopilot", "Tuning that proves itself",
        "Learns what's normal for this PC, spots when things get worse, and tests fixes with a before/after measurement.", "");
    public static Feature UndoVault { get; } = new("UndoVault", "Undo anything",
        "Everything PerfTuner changed is listed here, each with its own Undo button.", "");
    public static Feature Settings { get; } = new("Settings", "Engine, tray and about",
        "How PerfTuner runs: standalone engine or always-on Windows service.", "");

    // ---------------------------------------------------------------- features
    public static Feature PulseScore { get; } = new("PulseScore", "0–100 health score",
        "Starts at 100 and drops for sustained high CPU, low free memory, CPU throttling, stalls, and stale data.", "");
    public static Feature FocusBoost { get; } = new("FocusBoost", "Speed up the app you're using",
        "Raises the app in front of you and puts busy background apps into efficiency mode. Follows you as you switch apps.", "");
    public static Feature SilentRun { get; } = new("SilentRun", "Cooler and quieter",
        "Efficiency mode for every busy background app: less heat, less fan noise, longer battery.", "");
    public static Feature HogTamer { get; } = new("HogTamer", "Calm down CPU hogs",
        "A background app above the threshold for 30 s is calmed with efficiency mode; released after 2 minutes of quiet, or when you switch to it.", "");
    public static Feature RamRefresh { get; } = new("RAM Refresh", "Free memory now",
        "Asks background apps to hand back memory they aren't using. Harmless: pages come back on demand. The app you're using is skipped.", "");
    public static Feature StickyRules { get; } = new("StickyRules", "Always apply to an app",
        "Settings that follow an app every time it starts. Create them in AppPilot.", "");
    public static Feature TuneLevel { get; } = new("TuneLevel", "How much may run automatically",
        "Guard: nothing automatic. Cruise: modes, rules and MemGuard may act. Autopilot: Cruise + automatic proven experiments.", "");
    public static Feature SafeBounds { get; } = new("SafeBounds", "The limits every change obeys",
        "Each kind of change has fixed limits: what it may touch, how far, how often, how it's undone and when it stops.", "");
    public static Feature TimeTrail { get; } = new("TimeTrail", "Everything that happened",
        "Slowdowns, throttling, network bursts, regressions and every change PerfTuner made, in order.", "");
    public static Feature DriftWatch { get; } = new("DriftWatch", "Notices when your PC gets slower",
        "Learns this PC's normal disk speed, stall rate, paging, network errors and throttling, and flags lasting changes (after 1 hour of learning).", "");
    public static Feature ProofRun { get; } = new("ProofRun", "Test a fix before keeping it",
        "Measures 30 s before and 30 s after a change. Kept only if it helps and nothing else gets worse; otherwise undone automatically.", "");
    public static Feature MemGuard { get; } = new("MemGuard", "Smart memory protection",
        "Reads how memory behaves (stable, burst, churn, pressure, thrashing) and only acts when that helps. Needs TuneLevel Cruise or Autopilot.", "");
    public static Feature DiskLens { get; } = new("DiskLens", "How long disk reads really take",
        "Disk response times (typical, slow, worst) split into waiting in Windows' queue vs. the drive itself, per app.", "");
    public static Feature BurstRadar { get; } = new("BurstRadar", "Catch network spikes",
        "Network traffic at 1/10-second resolution; short spikes that cause lag in games and calls are caught and the app responsible named.", "");
    public static Feature WaitLens { get; } = new("WaitLens", "What are apps waiting for?",
        "Records a few seconds and shows, per app: running, waiting for a free CPU core, or stuck on disk, memory or locks.", "");
    public static Feature ClockCheck { get; } = new("ClockCheck", "Is your CPU held back?",
        "Real CPU speed and whether Windows limits it (heat, power plan, battery).", "");
    public static Feature LeakHunter { get; } = new("LeakHunter", "Apps that never stop growing",
        "Finds apps whose memory keeps growing steadily without levelling off (after 15 minutes of watching).", "\uE721");
    public static Feature LagScan { get; } = new("LagScan", "Drivers that cause stutter",
        "Measures how long each driver blocks the CPU — the cause of audio crackle, stutter and input lag.", "");
    public static Feature BootStory { get; } = new("BootStory", "What slows your startup",
        "How long boots and shutdowns took, and which apps, services or drivers delayed them.", "");
    public static Feature DriveDoctor { get; } = new("DriveDoctor", "Drive health",
        "Wear, temperature, power-on hours and errors of each drive.", "");
    public static Feature PowerTune { get; } = new("PowerTune", "Power plan and CPU limits",
        "Switch power plans and set processor boost and min/max speed. Lower max = cooler and quieter.", "");
    public static Feature FastVisuals { get; } = new("FastVisuals", "Snappier animations",
        "Turns off costly visual effects and transparency.", "");
    public static Feature QuickSwitches { get; } = new("QuickSwitches", "Hand-picked Windows 11 settings",
        "Documented settings only. Each records the previous value and can be undone.", "");
    public static Feature BootTrim { get; } = new("BootTrim", "Fewer apps at startup",
        "Disables startup apps the same way Task Manager does; the app itself is not modified.", "");
    public static Feature CleanSweep { get; } = new("CleanSweep", "Clean up and optimize drives",
        "Deletes temp files older than 2 days (with preview) and runs Windows' own drive optimization (TRIM / defrag).", "");
    public static Feature SafetyNet { get; } = new("SafetyNet", "Automatic emergency stop",
        "If the PC becomes unstable, all tuning is paused and undone automatically.", "");
    public static Feature DataFresh { get; } = new("DataFresh", "How fresh the data is",
        "Age of the latest sample, data source, kernel events per second and any dropped events.", "");
    public static Feature GpuSwitch { get; } = new("GPU Switch", "Choose the graphics card",
        "Use the high-performance or power-saving GPU for this app (applies the next time it starts).", "");

    /// <summary>Change-journal owner → the feature name users know.</summary>
    public static string OwnerName(string owner) => owner switch
    {
        "manual" => "You",
        "focus-foreground" or "focus-background" => "Turbo mode",
        "quiet" => "Silent mode",
        "auto-calm" => "Smart mode",
        "governor" => "Memory care",
        "experiment" or "autopilot" => "Automatic fixes",
        var o when o.StartsWith("rule:", StringComparison.Ordinal) => $"App rule ({o[5..]})",
        var o => o,
    };

    /// <summary>Engine source names (history / notifications) → what users read.</summary>
    public static string SourceName(string source) => source switch
    {
        "Turbo" => "Turbo mode",
        "Smart" => "Smart mode",
        "Silent" => "Silent mode",
        "MemGuard" => "Memory care",
        "ProofRun" or "Experiment" or "Autopilot" => "Automatic fixes",
        "Rule" => "App rules",
        "Watch" => "PerfTuner noticed something",
        var s => s,
    };
}

/// <summary>A Toolbox entry: the feature, the page it lives on, and an optional badge.</summary>
public sealed record ToolEntry(Feature Feature, int Page, string? Badge = null)
{
    public string PageText => Page.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool HasBadge => Badge is not null;
}

public sealed record ToolGroup(string Title, IReadOnlyList<ToolEntry> Tools);

public static class Toolbox
{
    private const string Unique = "UNIQUE";

    public static IReadOnlyList<ToolGroup> Groups { get; } =
    [
        new("Speed", [
            new(Brand.FocusBoost, 1), new(Brand.SilentRun, 1), new(Brand.HogTamer, 1), new(Brand.RamRefresh, 1),
            new(Brand.AppPilot, 6), new(Brand.StickyRules, 1), new(Brand.GpuSwitch, 6), new(Brand.TuneLevel, 1),
        ]),
        new("Clean & Windows", [
            new(Brand.CleanSweep, 2), new(Brand.BootTrim, 2), new(Brand.FastVisuals, 2), new(Brand.QuickSwitches, 2), new(Brand.PowerTune, 2),
        ]),
        new("Diagnose — only in PerfTuner", [
            new(Brand.StallSense, 3, Unique), new(Brand.TimeTrail, 3, Unique), new(Brand.DiskLens, 3, Unique),
            new(Brand.BurstRadar, 3, Unique), new(Brand.WaitLens, 3, Unique), new(Brand.MemGuard, 3, Unique),
        ]),
        new("Health", [
            new(Brand.ClockCheck, 7), new(Brand.LeakHunter, 7, Unique), new(Brand.LagScan, 7), new(Brand.BootStory, 7), new(Brand.DriveDoctor, 7),
        ]),
        new("Autopilot & safety", [
            new(Brand.DriftWatch, 8, Unique), new(Brand.ProofRun, 8, Unique), new(Brand.SafeBounds, 8, Unique),
            new(Brand.UndoVault, 5), new(Brand.SafetyNet, 5), new(Brand.DataFresh, 9),
        ]),
    ];
}
