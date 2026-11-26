using System.Text.RegularExpressions;

namespace PerfTuner.Core.Undocumented;

/// <summary>What an undocumented setting really does on current Windows, shown next to it.</summary>
public enum Verdict
{
    /// <summary>Has a real, visible effect.</summary>
    Works,
    /// <summary>Real effect, but only in specific situations.</summary>
    Niche,
    /// <summary>Changes a value Windows reads, with no measurable gain.</summary>
    Placebo,
    /// <summary>Ignored by current Windows.</summary>
    Dead,
    /// <summary>Works, but usually makes things worse or breaks something.</summary>
    Risky,
    /// <summary>Lowers the PC's protection.</summary>
    SecurityRisk,
    /// <summary>Only read while Windows Setup runs.</summary>
    SetupOnly,
}

public enum UndocValueType { DWord, String, ExpandString, Binary }

/// <summary>How an item is applied and undone.</summary>
public enum UndocKind
{
    /// <summary>Fixed registry values; undo puts back what was there (or removes what wasn't).</summary>
    Registry,
    /// <summary>The same values on every network adapter's TCP/IP key.</summary>
    PerAdapterRegistry,
    /// <summary>Removes registry keys; undo recreates them.</summary>
    RemoveKey,
    /// <summary>Boot settings of the running Windows (bcdedit, fixed arguments).</summary>
    Boot,
    /// <summary>Sets Windows services to Disabled and stops them; undo restores the start type.</summary>
    Services,
    /// <summary>A special folder on the desktop; undo removes it.</summary>
    Folder,
    /// <summary>A one-off action that can't be undone.</summary>
    Action,
    /// <summary>A virtual-desktop helper run by the app itself (on/off preference).</summary>
    Desktop,
}

public sealed record UndocValue(bool Machine, string Path, string Name, UndocValueType Type, object Data);

public sealed record UndocKey(bool Machine, string Path);

/// <summary>One boot setting: <c>bcdedit /set {current} Element Value</c>.</summary>
public sealed record BootValue(string Element, string Value);

public sealed record UndocumentedItem(
    string Id,
    string Group,
    string Title,
    string Description,
    Verdict Verdict,
    string VerdictNote,
    string TakesEffect,
    bool RequiresAdmin,
    UndocKind Kind)
{
    public IReadOnlyList<UndocValue> Values { get; init; } = [];
    public IReadOnlyList<UndocKey> Keys { get; init; } = [];
    public IReadOnlyList<BootValue> Boot { get; init; } = [];
    public IReadOnlyList<string> Services { get; init; } = [];

    /// <summary>First Windows build the item applies to (0 = any).</summary>
    public int MinBuild { get; init; }

    /// <summary>First Windows build the item no longer applies to (exclusive).</summary>
    public int MaxBuild { get; init; } = int.MaxValue;

    /// <summary>Shown when the item doesn't apply to this Windows build.</summary>
    public string BuildNote { get; init; } = "";

    public bool AppliesTo(int build) => build >= MinBuild && build < MaxBuild;

    /// <summary>Risky and security-risk items get a second, explicit warning before they are applied.</summary>
    public bool Dangerous => Verdict is Verdict.Risky or Verdict.SecurityRisk;

    public bool Undoable => Kind != UndocKind.Action;
}

/// <summary>
/// The opt-in "Undocumented" page: settings Windows reads but Microsoft never documented,
/// plus a few documented ones that are widely misused. Every item carries an honest verdict
/// (works, niche, placebo, dead, risky…). Nothing outside this fixed list can be written,
/// and undo records are checked against it before anything is put back.
/// </summary>
public static class UndocumentedCatalog
{
    public const string GroupDesktops = "Virtual desktops";
    public const string GroupLook = "Look and behaviour";
    public const string GroupPrivacy = "Search and privacy";
    public const string GroupSpeed = "Speed and responsiveness";
    public const string GroupGaming = "Gaming, mouse and network";
    public const string GroupBoot = "Boot and timers";
    public const string GroupSetup = "Installing Windows";
    public const string GroupMyths = "Myths: little or no effect";
    public const string GroupRisky = "Risky: usually makes things worse";
    public const string GroupActions = "One-off actions (can't be undone)";

    /// <summary>The desktop folder name Explorer shows as "all settings in one list".</summary>
    public const string AllSettingsFolderName = "All settings.{ED7BA470-8E54-465E-825C-99712043E01C}";

    public const string AdapterPathPrefix = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";

    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string MemoryManagement = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string Multimedia = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string ProcessorPower = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\";
    private const string Search = @"Software\Microsoft\Windows\CurrentVersion\Search";

    private static UndocValue U(string path, string name, int value) => new(false, path, name, UndocValueType.DWord, value);
    private static UndocValue M(string path, string name, int value) => new(true, path, name, UndocValueType.DWord, value);
    private static UndocValue US(string path, string name, string value) => new(false, path, name, UndocValueType.String, value);
    private static UndocValue MS(string path, string name, string value) => new(true, path, name, UndocValueType.String, value);

    // 100 % display scaling: a straight line, so pointer speed no longer depends on how fast the mouse moves.
    private static readonly byte[] CurveX =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0xC0, 0xCC, 0x0C, 0, 0, 0, 0, 0, 0x80, 0x99, 0x19, 0, 0, 0, 0, 0,
        0x40, 0x66, 0x26, 0, 0, 0, 0, 0, 0x00, 0x33, 0x33, 0, 0, 0, 0, 0,
    ];
    private static readonly byte[] CurveY =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x38, 0, 0, 0, 0, 0, 0, 0, 0x70, 0, 0, 0, 0, 0,
        0, 0, 0xA8, 0, 0, 0, 0, 0, 0, 0, 0xE0, 0, 0, 0, 0, 0,
    ];

    public static IReadOnlyList<UndocumentedItem> All { get; } =
    [
        // ------------------------------------------------------------ virtual desktops
        new("vd-tray", GroupDesktops, "Desktop number in the notification area",
            "A small icon shows which virtual desktop you're on (1, 2, 3…). Click it to go to the next desktop.",
            Verdict.Works, "Reads Explorer's private list of desktops, which a Windows update can change.",
            "Immediately", false, UndocKind.Desktop) { MinBuild = 22000, BuildNote = "Windows 11 only." },
        new("vd-overlay", GroupDesktops, "Show the desktop name when switching",
            "A short overlay in the middle of the screen shows the desktop's number and name each time you switch.",
            Verdict.Works, "Reads Explorer's private list of desktops, which a Windows update can change.",
            "Immediately", false, UndocKind.Desktop) { MinBuild = 22000, BuildNote = "Windows 11 only." },
        new("vd-hotkeys", GroupDesktops, "Hotkeys to jump between desktops",
            "Ctrl+Win+Alt+1…9 jumps straight to that desktop; Ctrl+Win+Alt+0 goes back to the desktop you were on before. Uses Windows' own Win+Ctrl+←/→ steps.",
            Verdict.Works, "Reads Explorer's private list of desktops, which a Windows update can change.",
            "Immediately", false, UndocKind.Desktop) { MinBuild = 22000, BuildNote = "Windows 11 only." },

        // ------------------------------------------------------------ look and behaviour
        new("classic-context-menu", GroupLook, "Full right-click menu straight away",
            "Right-clicking in File Explorer shows the complete classic menu immediately, without \"Show more options\".",
            Verdict.Works, "", "After Explorer restarts or you sign out", false, UndocKind.Registry)
        {
            Values = [US(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "")],
            MinBuild = 22000, BuildNote = "Windows 11 only.",
        },
        new("classic-alt-tab", GroupLook, "Classic Alt+Tab",
            "Alt+Tab shows the compact grid of icons from older Windows instead of large window previews.",
            Verdict.Works, "", "Immediately", false, UndocKind.Registry)
        { Values = [U(Explorer, "AltTabSettings", 1)] },
        new("no-shortcut-arrow", GroupLook, "No arrow on shortcut icons",
            "Removes the small arrow drawn on top of every shortcut icon.",
            Verdict.Works, "Shortcuts then look exactly like the files they point to.", "After you sign out", true, UndocKind.Registry)
        { Values = [new(true, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons", "29", UndocValueType.ExpandString, @"%SystemRoot%\System32\imageres.dll,-17")] },
        new("no-shortcut-suffix", GroupLook, "No \"- Shortcut\" in new shortcut names",
            "New shortcuts are named after the file itself, without \"- Shortcut\" at the end.",
            Verdict.Works, "", "New shortcuts", false, UndocKind.Registry)
        { Values = [US(Explorer + @"\NamingTemplates", "ShortcutNameTemplate", "%s.lnk")] },
        new("wallpaper-full-quality", GroupLook, "Wallpaper in full quality",
            "Windows stops recompressing JPEG wallpapers to about 85 % quality, so photos stay sharp.",
            Verdict.Works, "", "Next time you set a wallpaper", false, UndocKind.Registry)
        { Values = [U(@"Control Panel\Desktop", "JPEGImportQuality", 100)] },
        new("hide-home", GroupLook, "Hide \"Home\" in File Explorer",
            "Removes Home from the navigation pane on the left.",
            Verdict.Works, "", "New Explorer windows", false, UndocKind.Registry)
        {
            Values = [U(@"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", "System.IsPinnedToNameSpaceTree", 0)],
            MinBuild = 22621, BuildNote = "Windows 11 22H2 and later.",
        },
        new("hide-gallery", GroupLook, "Hide \"Gallery\" in File Explorer",
            "Removes Gallery from the navigation pane on the left.",
            Verdict.Works, "", "New Explorer windows", false, UndocKind.Registry)
        {
            Values = [U(@"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}", "System.IsPinnedToNameSpaceTree", 0)],
            MinBuild = 22621, BuildNote = "Windows 11 22H2 and later.",
        },
        new("hide-onedrive-pane", GroupLook, "Hide OneDrive in File Explorer",
            "Removes OneDrive from the navigation pane. OneDrive itself keeps syncing.",
            Verdict.Works, "OneDrive may add it back when it updates itself.", "New Explorer windows", false, UndocKind.Registry)
        { Values = [U(@"Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}", "System.IsPinnedToNameSpaceTree", 0)] },
        new("no-folder-discovery", GroupLook, "No automatic folder type",
            "File Explorer stops guessing whether a folder holds pictures, music or documents, so very large folders open faster.",
            Verdict.Works, "Folders forget a view you set per type; you can still set views by hand.", "New Explorer windows", false, UndocKind.Registry)
        { Values = [US(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell", "FolderType", "NotSpecified")] },
        new("small-taskbar", GroupLook, "Small taskbar",
            "A thinner taskbar with smaller icons.",
            Verdict.Works, "", "After Explorer restarts", false, UndocKind.Registry)
        {
            Values = [U(Explorer + @"\Advanced", "TaskbarSi", 0)],
            MinBuild = 22000, MaxBuild = 22621, BuildNote = "Only Windows 11 21H2; ignored from 22H2 on.",
        },
        new("no-setup-nag", GroupLook, "No \"Let's finish setting up\" screen",
            "Stops the full-screen prompt that appears after updates and asks you to finish setting up Windows.",
            Verdict.Works, "", "Immediately", false, UndocKind.Registry)
        { Values = [U(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0)] },
        new("no-3d-objects", GroupLook, "Remove \"3D Objects\" from This PC",
            "Removes the 3D Objects folder from This PC. The folder on disk isn't touched.",
            Verdict.Works, "", "New Explorer windows", true, UndocKind.RemoveKey)
        {
            Keys =
            [
                new(true, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}"),
                new(true, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}"),
            ],
            MaxBuild = 22000, BuildNote = "Windows 10 only; Windows 11 no longer has it.",
        },
        new("all-settings-folder", GroupLook, "\"All settings\" folder on the desktop",
            "Adds a folder (often called \"God Mode\") that lists every Control Panel setting in one place.",
            Verdict.Works, "", "Immediately", false, UndocKind.Folder),

        // ------------------------------------------------------------ search and privacy
        new("bing-search-off", GroupPrivacy, "No Bing in Start search (per user)",
            "The per-user switch that stops Start search from sending what you type to Bing.",
            Verdict.Niche, "Ignored by many Windows 11 builds; the documented policy on the Windows page is the reliable way.", "Next search", false, UndocKind.Registry)
        { Values = [U(Search, "BingSearchEnabled", 0)] },
        new("cortana-consent-off", GroupPrivacy, "Cortana consent off",
            "Withdraws the consent Cortana used for search.",
            Verdict.Dead, "Cortana was removed from Windows 11; this value is no longer read.", "Immediately", false, UndocKind.Registry)
        { Values = [U(Search, "CortanaConsent", 0)] },
        new("search-location-off", GroupPrivacy, "Search doesn't use your location",
            "Start search stops using your location for local results.",
            Verdict.Niche, "Only matters while web results are on.", "Next search", false, UndocKind.Registry)
        { Values = [U(Search, "AllowSearchToUseLocation", 0)] },
        new("location-service-off", GroupPrivacy, "Location service off at its source",
            "Turns the location service off for the whole PC directly in its own configuration.",
            Verdict.Works, "Maps, weather and Find my device stop knowing where you are. The documented switch on the Windows page does the same more cleanly.", "After restart", true, UndocKind.Registry)
        { Values = [M(@"SYSTEM\CurrentControlSet\Services\lfsvc\Service\Configuration", "Status", 0)] },
        new("telemetry-zero", GroupPrivacy, "Diagnostic data: off (security level)",
            "Sets the diagnostic-data policy to 0 (\"security\").",
            Verdict.Placebo, "Only Enterprise, Education and Server honour it; Home and Pro treat it as \"required\", which the Windows page already sets.", "After restart", true, UndocKind.Registry)
        { Values = [M(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0)] },
        new("telemetry-services-off", GroupPrivacy, "Diagnostic services off",
            "Disables and stops the Connected User Experiences and Telemetry service and the WAP push message service.",
            Verdict.Risky, "Not supported by Microsoft. Feedback Hub, some troubleshooters and compatibility checks stop working.", "Immediately", true, UndocKind.Services)
        { Services = ["DiagTrack", "dmwappushservice"] },

        // ------------------------------------------------------------ speed and responsiveness
        new("startup-delay-off", GroupSpeed, "No delay before startup apps",
            "Apps that start with Windows launch right away instead of after Explorer's built-in pause.",
            Verdict.Works, "Sign-in can feel busier for a few seconds while everything starts at once.", "Next sign-in", false, UndocKind.Registry)
        { Values = [U(Explorer + @"\Serialize", "StartupDelayInMSec", 0)] },
        new("startup-idle-wait-off", GroupSpeed, "Don't wait for idle before startup apps",
            "Windows 11 no longer waits for the system to go idle before it starts startup apps.",
            Verdict.Works, "", "Next sign-in", false, UndocKind.Registry)
        { Values = [U(Explorer + @"\Serialize", "WaitForIdleState", 0)], MinBuild = 22000, BuildNote = "Windows 11 only." },
        new("power-throttling-off", GroupSpeed, "Power throttling off",
            "Background apps are never put into efficiency mode by Windows.",
            Verdict.Niche, "More power use and heat; background apps rarely get noticeably faster. PerfTuner's own modes already decide per app.", "After restart", true, UndocKind.Registry)
        { Values = [M(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1)] },
        new("global-timer-resolution", GroupSpeed, "Machine-wide timer resolution",
            "Restores the older behaviour where one app asking for a precise timer makes it precise for the whole system. Some games' frame pacing depends on it.",
            Verdict.Niche, "Slightly more power use whenever any app asks for a precise timer.", "After restart", true, UndocKind.Registry)
        { Values = [M(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1)], MinBuild = 20348, BuildNote = "Windows 11 only." },
        new("show-hidden-power-options", GroupSpeed, "Show hidden processor power options",
            "Shows core parking (minimum and maximum cores) and the processor boost mode in the classic Power Options panel.",
            Verdict.Works, "Only shows the options; nothing changes until you change them there.", "Next time Power Options opens", true, UndocKind.Registry)
        {
            Values =
            [
                M(ProcessorPower + "0cc5b647-c1df-4637-891a-dec35c318583", "Attributes", 2),
                M(ProcessorPower + "ea062031-0e34-4ff1-9b6d-eb1059334028", "Attributes", 2),
                M(ProcessorPower + "be337238-0d82-4146-a960-4f3749d470c7", "Attributes", 2),
            ],
        },
        new("gpu-scheduling-on", GroupSpeed, "Hardware-accelerated GPU scheduling",
            "Lets the graphics card schedule its own work. Same as the switch in Settings › Display › Graphics, for PCs where Settings hides it.",
            Verdict.Niche, "Needs a recent graphics driver; can cause stutter on some cards.", "After restart", true, UndocKind.Registry)
        { Values = [M(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2)] },
        new("close-hung-apps", GroupSpeed, "Close hung apps fast at shutdown",
            "At shutdown, apps that don't respond are closed after 2 seconds without asking.",
            Verdict.Risky, "Unsaved work in a hung or busy app is lost without a prompt.", "Next shutdown", false, UndocKind.Registry)
        {
            Values =
            [
                US(@"Control Panel\Desktop", "AutoEndTasks", "1"),
                US(@"Control Panel\Desktop", "HungAppTimeout", "2000"),
                US(@"Control Panel\Desktop", "WaitToKillAppTimeout", "2000"),
            ],
        },

        // ------------------------------------------------------------ gaming, mouse, network
        new("fullscreen-optimizations-off", GroupGaming, "Fullscreen optimizations off (all games)",
            "Games run in true exclusive fullscreen instead of Windows' optimized borderless mode.",
            Verdict.Niche, "Mixed results on Windows 11: helps a few older games, can hurt alt-tab and HDR.", "Next game start", false, UndocKind.Registry)
        {
            Values =
            [
                U(@"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2),
                U(@"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1),
                U(@"System\GameConfigStore", "GameDVR_FSEBehavior", 2),
                U(@"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1),
            ],
        },
        new("linear-mouse-curve", GroupGaming, "Linear mouse curve",
            "Replaces Windows' pointer acceleration curve with a straight line, for games that force \"Enhance pointer precision\" on.",
            Verdict.Niche, "Made for 100 % display scaling. If you simply want no acceleration, turn off pointer precision on the Windows page instead.", "Next sign-in", false, UndocKind.Registry)
        {
            Values =
            [
                new(false, @"Control Panel\Mouse", "SmoothMouseXCurve", UndocValueType.Binary, CurveX),
                new(false, @"Control Panel\Mouse", "SmoothMouseYCurve", UndocValueType.Binary, CurveY),
            ],
        },
        new("nagle-off", GroupGaming, "Send small network packets at once (Nagle off)",
            "Every network adapter acknowledges and sends small packets immediately instead of bundling them.",
            Verdict.Niche, "Only helps older online games that don't do this themselves; slightly more network traffic.", "Next connection", true, UndocKind.PerAdapterRegistry)
        {
            Values =
            [
                M(AdapterPathPrefix + "{adapter}", "TcpAckFrequency", 1),
                M(AdapterPathPrefix + "{adapter}", "TCPNoDelay", 1),
            ],
        },

        // ------------------------------------------------------------ boot and timers
        new("no-dynamic-tick", GroupBoot, "Steady timer tick",
            "The system timer keeps ticking at a fixed rate instead of pausing when the PC is idle.",
            Verdict.Niche, "Costs battery on laptops; any gain in input latency is tiny.", "After restart", true, UndocKind.Boot)
        { Boot = [new("disabledynamictick", "yes")] },
        new("platform-tick", GroupBoot, "Hardware timer tick",
            "Uses the platform's hardware timer as the source of the system tick.",
            Verdict.Niche, "Results differ per PC; measure before keeping it.", "After restart", true, UndocKind.Boot)
        { Boot = [new("useplatformtick", "yes")] },
        new("tsc-enhanced", GroupBoot, "Enhanced CPU clock synchronization",
            "Asks Windows to synchronize the processors' time-stamp counters more strictly.",
            Verdict.Placebo, "Modern CPUs already have a stable counter.", "After restart", true, UndocKind.Boot)
        { Boot = [new("tscsyncpolicy", "enhanced")] },
        new("force-hpet", GroupBoot, "Always use the HPET timer",
            "Forces the slower High Precision Event Timer as the only clock source.",
            Verdict.Risky, "Usually raises timer overhead and lowers frame rates; often recommended by mistake.", "After restart", true, UndocKind.Boot)
        { Boot = [new("useplatformclock", "yes")] },
        new("f8-boot-menu", GroupBoot, "F8 boot menu",
            "Brings back the classic boot menu: press F8 while the PC starts to reach Safe Mode.",
            Verdict.Works, "Start-up takes a moment longer while Windows waits for F8.", "Next start", true, UndocKind.Boot)
        { Boot = [new("bootmenupolicy", "legacy")] },

        // ------------------------------------------------------------ installing Windows
        new("upgrade-unsupported-pc", GroupSetup, "Allow Windows 11 upgrades on unsupported PCs",
            "Lets Windows 11 Setup upgrade a PC whose processor or TPM isn't on Microsoft's list (a TPM 1.2 is still needed).",
            Verdict.SetupOnly, "Only read by Setup started from a Windows 11 ISO. Microsoft may withhold updates from such PCs.", "Next time Setup runs", true, UndocKind.Registry)
        { Values = [M(@"SYSTEM\Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU", 1)] },
        new("setup-skip-checks", GroupSetup, "Skip Setup's TPM, Secure Boot and RAM checks",
            "Tells Windows Setup not to check for TPM 2.0, Secure Boot and 4 GB of memory.",
            Verdict.SetupOnly, "Only read while Windows Setup runs. Microsoft may withhold updates from such PCs.", "Next time Setup runs", true, UndocKind.Registry)
        {
            Values =
            [
                M(@"SYSTEM\Setup\LabConfig", "BypassTPMCheck", 1),
                M(@"SYSTEM\Setup\LabConfig", "BypassSecureBootCheck", 1),
                M(@"SYSTEM\Setup\LabConfig", "BypassRAMCheck", 1),
            ],
        },
        new("setup-local-account", GroupSetup, "Allow a local account in first-time setup",
            "Lets first-time setup (after a reset) continue without a network and a Microsoft account.",
            Verdict.Dead, "Removed from recent Windows 11 builds; only read during first-time setup.", "Next first-time setup", true, UndocKind.Registry)
        { Values = [M(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE", "BypassNRO", 1)] },

        // ------------------------------------------------------------ myths
        new("io-page-lock-limit", GroupMyths, "IoPageLockLimit",
            "Raises the amount of memory that may be locked for disk transfers.",
            Verdict.Dead, "Ignored since Windows XP SP1.", "After restart", true, UndocKind.Registry)
        { Values = [M(MemoryManagement, "IoPageLockLimit", 983040)] },
        new("second-level-cache", GroupMyths, "SecondLevelDataCache",
            "Tells Windows the size of the processor's L2 cache.",
            Verdict.Dead, "Windows reads the cache size from the processor itself.", "After restart", true, UndocKind.Registry)
        { Values = [M(MemoryManagement, "SecondLevelDataCache", 1024)] },
        new("no-paging-executive", GroupMyths, "Keep the kernel in memory",
            "Stops Windows from paging out kernel code and drivers.",
            Verdict.Placebo, "With today's memory sizes the kernel is practically never paged out.", "After restart", true, UndocKind.Registry)
        { Values = [M(MemoryManagement, "DisablePagingExecutive", 1)] },
        new("irp-stack-size", GroupMyths, "IRPStackSize",
            "Gives the file-sharing server more stack locations per request.",
            Verdict.Placebo, "Only matters when other PCs read files shared from this one, with certain filter drivers.", "After restart", true, UndocKind.Registry)
        { Values = [M(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "IRPStackSize", 32)] },
        new("system-responsiveness", GroupMyths, "SystemResponsiveness = 10",
            "Reserves less CPU time for background work while audio or video plays.",
            Verdict.Placebo, "Documented, but no measurable gain on current Windows.", "After restart", true, UndocKind.Registry)
        { Values = [M(Multimedia, "SystemResponsiveness", 10)] },
        new("network-throttling-off", GroupMyths, "Network throttling off",
            "Lifts the limit on network processing while multimedia plays.",
            Verdict.Placebo, "Documented; the limit hasn't slowed typical connections for years.", "After restart", true, UndocKind.Registry)
        { Values = [M(Multimedia, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF))] },
        new("games-task-priority", GroupMyths, "Higher priority for the \"Games\" multimedia task",
            "Raises the CPU and GPU priority of the multimedia scheduler's Games task.",
            Verdict.Placebo, "Only applies to games that register with that scheduler, which few do.", "After restart", true, UndocKind.Registry)
        {
            Values =
            [
                M(Multimedia + @"\Tasks\Games", "GPU Priority", 8),
                M(Multimedia + @"\Tasks\Games", "Priority", 6),
                MS(Multimedia + @"\Tasks\Games", "Scheduling Category", "High"),
                MS(Multimedia + @"\Tasks\Games", "SFIO Priority", "High"),
            ],
        },
        new("priority-separation", GroupMyths, "Win32PrioritySeparation = 0x26",
            "Short, variable time slices with the biggest boost for the app in front.",
            Verdict.Placebo, "Documented; differences are within measurement noise on current Windows.", "Immediately", true, UndocKind.Registry)
        { Values = [M(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26)] },
        new("defender-off-policy", GroupMyths, "DisableAntiSpyware policy",
            "The old policy that turned Microsoft Defender off.",
            Verdict.Dead, "Ignored since 2020 and blocked by Tamper Protection. Defender may report the value as tampering.", "After restart", true, UndocKind.Registry)
        { Values = [M(@"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1)] },

        // ------------------------------------------------------------ risky
        new("large-system-cache", GroupRisky, "Large system cache",
            "Lets the file cache grow at the expense of apps' memory.",
            Verdict.Risky, "Meant for file servers; on a desktop it usually makes apps slower.", "After restart", true, UndocKind.Registry)
        { Values = [M(MemoryManagement, "LargeSystemCache", 1)] },
        new("mmcss-off", GroupRisky, "Multimedia scheduler off",
            "Disables the Multimedia Class Scheduler service that gives audio and video playback steady CPU time.",
            Verdict.Risky, "Causes crackling audio and stutter in games and video: the opposite of what it's sold as.", "After restart", true, UndocKind.Registry)
        { Values = [M(@"SYSTEM\CurrentControlSet\Services\MMCSS", "Start", 4)] },
        new("gpu-preemption-off", GroupRisky, "GPU preemption off",
            "Stops Windows from interrupting long-running graphics work.",
            Verdict.Risky, "Can make the screen freeze and the graphics driver reset (\"display driver stopped responding\").", "After restart", true, UndocKind.Registry)
        { Values = [M(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Scheduler", "EnablePreemption", 0)] },
        new("ipv6-off", GroupRisky, "IPv6 off",
            "Turns IPv6 off on every network adapter (except loopback).",
            Verdict.Risky, "Microsoft advises against it: some Windows features, VPNs and providers need IPv6. Restart takes 5 seconds longer.", "After restart", true, UndocKind.Registry)
        { Values = [M(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0xFF)] },
        new("cpu-mitigations-off", GroupRisky, "Spectre and Meltdown protection off",
            "Turns off Windows' protection against the Spectre and Meltdown processor flaws.",
            Verdict.SecurityRisk, "Documented by Microsoft, but lets malicious code read other programs' memory. A few percent faster on older CPUs.", "After restart", true, UndocKind.Registry)
        {
            Values =
            [
                M(MemoryManagement, "FeatureSettingsOverride", 3),
                M(MemoryManagement, "FeatureSettingsOverrideMask", 3),
            ],
        },

        // ------------------------------------------------------------ one-off actions
        new("clear-prefetch", GroupActions, "Empty the Prefetch folder",
            "Deletes the files Windows uses to start apps faster.",
            Verdict.Risky, "The next starts of every app are slower until Windows rebuilds them, which it does anyway.", "Immediately", true, UndocKind.Action),
        new("clear-event-logs", GroupActions, "Clear all event logs",
            "Empties every Windows event log.",
            Verdict.Risky, "Deletes the history that \"Why was my PC slow?\", the Security page and support staff rely on.", "Immediately", true, UndocKind.Action),
    ];

    public static UndocumentedItem? Find(string id) => All.FirstOrDefault(i => i.Id == id);

    public static string VerdictText(Verdict v) => v switch
    {
        Verdict.Works => "Works",
        Verdict.Niche => "Niche",
        Verdict.Placebo => "Placebo",
        Verdict.Dead => "No effect",
        Verdict.Risky => "Risky",
        Verdict.SecurityRisk => "Security risk",
        Verdict.SetupOnly => "Setup only",
        _ => v.ToString(),
    };

    // ------------------------------------------------------------------ undo allow-lists

    private static readonly Regex AdapterKey = new(@"^SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant);

    public static bool IsAdapterPath(string path) => path is not null && AdapterKey.IsMatch(path);

    /// <summary>Whether an undo record for a registry value belongs to this catalog.</summary>
    public static bool MayRestoreValue(bool machine, string path, string name) =>
        path is not null && name is not null && All.Any(i => i.Values.Any(v =>
            v.Machine == machine && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && (i.Kind == UndocKind.PerAdapterRegistry ? IsAdapterPath(path) : v.Path.Equals(path, StringComparison.OrdinalIgnoreCase))));

    /// <summary>Keys the catalog removes (undo recreates them) or creates (undo removes them again when empty).</summary>
    public static bool MayRestoreKey(bool machine, string path) =>
        path is not null && All.Any(i =>
            i.Keys.Any(k => k.Machine == machine && k.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            || (i.Kind == UndocKind.Registry && i.Values.Any(v => v.Machine == machine && v.Path.Equals(path, StringComparison.OrdinalIgnoreCase))));

    /// <summary>A created-key root must be the value's key itself or one of its ancestors, at least three levels deep.</summary>
    public static bool IsCreatedRoot(string path, string root) =>
        path is not null && root is not null && root.Split('\\').Length >= 3
        && (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] BootWords = ["yes", "no", "legacy", "standard", "enhanced", "default"];

    /// <summary>Boot elements may only be put back to a known word (or removed: null).</summary>
    public static bool MayRestoreBoot(string element, string? value) =>
        element is not null && All.Any(i => i.Boot.Any(b => b.Element.Equals(element, StringComparison.Ordinal)))
        && (value is null || BootWords.Contains(value, StringComparer.Ordinal));

    /// <summary>Start types: 2 automatic, 3 manual, 4 disabled.</summary>
    public static bool MayRestoreService(string name, int startType) =>
        name is not null && startType is >= 2 and <= 4
        && (All.Any(i => i.Services.Contains(name, StringComparer.OrdinalIgnoreCase)) || name.Equals(DocumentedServices.Search, StringComparison.OrdinalIgnoreCase));

    /// <summary>Normalizes what bcdedit printed for an element to a word it accepts back (or null if unknown).</summary>
    public static string? BootWord(string? shown)
    {
        if (string.IsNullOrWhiteSpace(shown)) { return null; }
        var w = shown.Trim().ToLowerInvariant();
        return BootWords.Contains(w, StringComparer.Ordinal) ? w : null;
    }
}

/// <summary>Documented services PerfTuner switches on its normal pages.</summary>
public static class DocumentedServices
{
    /// <summary>Windows Search (indexing).</summary>
    public const string Search = "WSearch";
}
