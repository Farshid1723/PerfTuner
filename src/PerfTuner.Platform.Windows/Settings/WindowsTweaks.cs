using Microsoft.Win32;
using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows.Settings;

/// <summary>One registry value a tweak sets. Only values listed in <see cref="WindowsTweaks.Catalog"/> can ever be written.</summary>
public sealed record RegistryValueSpec(bool Machine, string KeyPath, string ValueName, RegistryValueKind Kind, object Value)
{
    public RegistryKey Hive => Machine ? Registry.LocalMachine : Registry.CurrentUser;
}

public sealed record WindowsTweak(
    string Id,
    string Title,
    string Description,
    string TakesEffect,
    bool RequiresAdmin,
    IReadOnlyList<RegistryValueSpec> Values,
    string Category = "Speed & convenience");

/// <summary>
/// Fixed, allow-listed Windows 11 switches. Each is a small set of per-user or policy
/// registry values that Windows documents or exposes in its own UI; the previous value
/// (or its absence) is journaled so every tweak can be undone exactly.
/// </summary>
public static class WindowsTweaks
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string WindowsUpdatePolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    public static IReadOnlyList<WindowsTweak> Catalog { get; } =
    [
        new("end-task", "\"End task\" in the taskbar",
            "Adds End task to the right-click menu of taskbar apps — kill a frozen app without opening Task Manager (Windows 11 23H2+; same switch as Settings → System → For developers).",
            "Immediately", false,
            [new(false, Advanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask", RegistryValueKind.DWord, 1)]),

        new("no-web-search", "No web results in Start search",
            "Start menu search stays local: no Bing suggestions or web results, so typing in Start is faster and nothing you type is sent online.",
            "After sign-out or Explorer restart", false,
            [new(false, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", RegistryValueKind.DWord, 1)]),

        new("game-dvr-off", "Game DVR background recording off",
            "Stops Xbox Game Bar from continuously buffering gameplay video in the background (saves GPU encoder time, disk writes and memory). Game Bar itself keeps working.",
            "Next app launch", false,
            [
                new(false, @"System\GameConfigStore", "GameDVR_Enabled", RegistryValueKind.DWord, 0),
                new(false, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", RegistryValueKind.DWord, 0),
            ]),

        new("fast-menus", "Snappier menus",
            "Cascading menus open after 100 ms instead of Windows' default 400 ms.",
            "After sign-out", false,
            [new(false, @"Control Panel\Desktop", "MenuShowDelay", RegistryValueKind.String, "100")]),

        new("delivery-lan-only", "Update sharing: local network only",
            "Delivery Optimization will share Windows/Store updates only with PCs on your own network, never upload to internet peers (saves upload bandwidth; Windows Update keeps working).",
            "Immediately (next download)", true,
            [new(true, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", RegistryValueKind.DWord, 1)]),

        new("fast-startup", "Fast Startup",
            "After a shutdown, Windows resumes its core from a small hibernation file instead of starting from scratch, so the PC boots noticeably faster. (Restart still does a full fresh start.) Needs hibernation to be available.",
            "Next shutdown", true,
            [new(true, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", RegistryValueKind.DWord, 1)]),

        // ---------------------------------------------------------------- privacy (only documented settings and policies)
        new("ad-id-off", "No advertising ID",
            "Apps can't use your advertising ID to show personalised ads (same switch as Settings › Privacy › General).",
            "Immediately", false,
            [new(false, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", RegistryValueKind.DWord, 0)], "Privacy"),
        new("tailored-off", "No tailored experiences",
            "Microsoft doesn't use your diagnostic data to show personalised tips, ads and recommendations (Settings › Privacy › Diagnostics & feedback).",
            "Immediately", false,
            [new(false, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", RegistryValueKind.DWord, 0)], "Privacy"),
        new("suggestions-off", "No suggestions and ads in Start, Settings and the lock screen",
            "Turns off suggested apps in Start, \"suggested content\" in Settings, tips and tricks, and promotional lock-screen content (the toggles Windows offers in Personalization and Notifications).",
            "Immediately", false,
            [
                new(false, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", RegistryValueKind.DWord, 0),
                new(false, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", RegistryValueKind.DWord, 0),
                new(false, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338393Enabled", RegistryValueKind.DWord, 0),
                new(false, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353694Enabled", RegistryValueKind.DWord, 0),
                new(false, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", RegistryValueKind.DWord, 0),
                new(false, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "RotatingLockScreenOverlayEnabled", RegistryValueKind.DWord, 0),
            ], "Privacy"),
        new("activity-history-off", "No activity history",
            "Windows stops collecting the list of apps and files you used (policy \"Allow publishing of user activities\" = off).",
            "Immediately", true,
            [
                new(true, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", RegistryValueKind.DWord, 0),
                new(true, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", RegistryValueKind.DWord, 0),
            ], "Privacy"),
        new("telemetry-required", "Diagnostic data: required only",
            "Limits diagnostic data sent to Microsoft to the required minimum (policy \"Allow diagnostic data\" = Required). Windows Update and security keep working.",
            "After restart", true,
            [new(true, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", RegistryValueKind.DWord, 1)], "Privacy"),
        new("recall-off", "No AI snapshots (Recall)",
            "Windows doesn't save snapshots of your screen for AI search (policy \"Turn off saving snapshots for Windows\"). Only matters on Copilot+ PCs.",
            "After sign-out", false,
            [new(false, @"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", RegistryValueKind.DWord, 1)], "Privacy"),
        new("copilot-off", "Copilot button off",
            "Hides Windows Copilot (policy \"Turn off Windows Copilot\"). The Copilot app can still be opened if installed.",
            "After sign-out", false,
            [new(false, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", RegistryValueKind.DWord, 1)], "Privacy"),
        new("widgets-off", "Widgets (news and interests) off",
            "Removes the Widgets board and its news feed from the taskbar (policy \"Allow widgets\" = off). Saves the memory of its background process.",
            "After sign-out", true,
            [new(true, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", RegistryValueKind.DWord, 0)], "Privacy"),

        // ---------------------------------------------------------------- Windows behaviour
        new("show-extensions", "Show file extensions",
            "Explorer shows \".exe\", \".pdf\" and so on, so a file called \"invoice.pdf.exe\" can't pretend to be a document.",
            "Next Explorer window", false,
            [new(false, Advanced, "HideFileExt", RegistryValueKind.DWord, 0)], "Windows"),
        new("show-hidden", "Show hidden files",
            "Explorer also shows hidden files and folders (system files stay hidden).",
            "Next Explorer window", false,
            [new(false, Advanced, "Hidden", RegistryValueKind.DWord, 1)], "Windows"),
        new("verbose-status", "Detailed messages at start-up and shut-down",
            "Shows what Windows is doing (\"Stopping services…\") instead of just \"Please wait\": useful when a start or shut-down hangs.",
            "Next start", true,
            [new(true, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "VerboseStatus", RegistryValueKind.DWord, 1)], "Windows"),
        new("long-paths", "Allow long file paths",
            "Programs that support it can use paths longer than 260 characters (deep project folders, node_modules).",
            "Programs started afterwards", true,
            [new(true, @"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", RegistryValueKind.DWord, 1)], "Windows"),
        new("sticky-keys-off", "No Sticky Keys pop-up",
            "Pressing Shift five times no longer asks to turn on Sticky Keys (handy in games). Sticky Keys stays available in Accessibility settings.",
            "Immediately", false,
            [new(false, @"Control Panel\Accessibility\StickyKeys", "Flags", RegistryValueKind.String, "506")], "Windows"),

        // ---------------------------------------------------------------- v0.10: more privacy and Windows behaviour switches
        new("location-off", "Location off for this PC",
            "Apps and Windows can't use this PC's location (same as Settings › Privacy › Location › Location services = off).",
            "Immediately", true,
            [new(true, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", RegistryValueKind.String, "Deny")], "Privacy"),
        new("background-apps-off", "Store apps don't run in the background",
            "Apps from the Microsoft Store can't keep running in the background (policy \"Let Windows apps run in the background\" = Force deny). Saves memory and battery; notifications from those apps may arrive late.",
            "After sign-out", true,
            [new(true, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", RegistryValueKind.DWord, 2)], "Privacy"),
        new("consumer-features-off", "No automatically installed promoted apps",
            "Windows stops installing suggested third-party apps and games for new accounts (policy \"Turn off Microsoft consumer experiences\"). Officially honoured by Enterprise and Education editions.",
            "Next sign-in", true,
            [new(true, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", RegistryValueKind.DWord, 1)], "Privacy"),
        new("start-recommendations-off", "No recommendations in Start",
            "Start stops recommending tips, shortcuts and new apps (Settings › Personalization › Start › \"Show recommendations…\" = off).",
            "Immediately", false,
            [new(false, Advanced, "Start_IrisRecommendations", RegistryValueKind.DWord, 0)], "Privacy"),
        new("dark-mode", "Dark mode for Windows and apps",
            "Windows and apps use the dark colour mode (Settings › Personalization › Colors).",
            "Immediately", false,
            [
                new(false, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", RegistryValueKind.DWord, 0),
                new(false, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", RegistryValueKind.DWord, 0),
            ], "Windows"),
        new("taskbar-left", "Taskbar icons on the left",
            "Start and the taskbar icons sit on the left like in earlier Windows (Settings › Personalization › Taskbar › Taskbar alignment).",
            "Immediately", false,
            [new(false, Advanced, "TaskbarAl", RegistryValueKind.DWord, 0)], "Windows"),
        new("task-view-off", "No Task View button",
            "Hides the Task View button from the taskbar (Win+Tab still works).",
            "Immediately", false,
            [new(false, Advanced, "ShowTaskViewButton", RegistryValueKind.DWord, 0)], "Windows"),
        new("search-icon-only", "Search as a small icon",
            "The taskbar shows a search icon instead of the wide search box (Settings › Personalization › Taskbar › Search).",
            "Immediately", false,
            [new(false, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", RegistryValueKind.DWord, 1)], "Windows"),
        new("explorer-this-pc", "File Explorer opens \"This PC\"",
            "New File Explorer windows show your drives instead of Home with recent files.",
            "Next Explorer window", false,
            [new(false, Advanced, "LaunchTo", RegistryValueKind.DWord, 1)], "Windows"),
        new("scrollbars-visible", "Always show scrollbars",
            "Scrollbars stay visible instead of shrinking away (Settings › Accessibility › Visual effects).",
            "New windows", false,
            [new(false, @"Control Panel\Accessibility", "DynamicScrollbars", RegistryValueKind.DWord, 0)], "Windows"),
        new("numlock-on", "Num Lock on at sign-in",
            "The number pad works straight away after you sign in.",
            "Next sign-in", false,
            [new(false, @"Control Panel\Keyboard", "InitialKeyboardIndicators", RegistryValueKind.String, "2")], "Windows"),
        new("bsod-details", "Show details on a crash screen",
            "If Windows crashes, the blue screen also shows the error parameters, which helps finding the faulty driver.",
            "Next crash", true,
            [new(true, @"SYSTEM\CurrentControlSet\Control\CrashControl", "DisplayParameters", RegistryValueKind.DWord, 1)], "Windows"),
        new("logon-blur-off", "Clear background on the sign-in screen",
            "The sign-in screen shows your lock-screen picture sharp instead of blurred.",
            "Next sign-in", true,
            [new(true, @"SOFTWARE\Policies\Microsoft\Windows\System", "DisableAcrylicBackgroundOnLogon", RegistryValueKind.DWord, 1)], "Windows"),
        new("game-mode", "Game Mode on",
            "Windows gives games priority and holds back Windows Update installs and notifications while you play (Settings › Gaming › Game Mode).",
            "Next game start", false,
            [new(false, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueKind.DWord, 1)], "Speed & convenience"),
        // ---------------------------------------------------------------- Windows Update behaviour (documented Windows Update for Business policies)
        new("wu-defer-features", "Big Windows upgrades a year later",
            "New Windows versions (feature updates) arrive 365 days after release, once early problems are fixed. Monthly security updates are not delayed by this. Pro, Enterprise and Education editions.",
            "Next update check", true,
            [
                new(true, WindowsUpdatePolicy, "DeferFeatureUpdates", RegistryValueKind.DWord, 1),
                new(true, WindowsUpdatePolicy, "DeferFeatureUpdatesPeriodInDays", RegistryValueKind.DWord, 365),
            ], "Windows Update"),
        new("wu-defer-quality", "Monthly updates 4 days later",
            "Monthly quality and security updates install 4 days after release, so a faulty update is usually pulled before it reaches you. Pro, Enterprise and Education editions.",
            "Next update check", true,
            [
                new(true, WindowsUpdatePolicy, "DeferQualityUpdates", RegistryValueKind.DWord, 1),
                new(true, WindowsUpdatePolicy, "DeferQualityUpdatesPeriodInDays", RegistryValueKind.DWord, 4),
            ], "Windows Update"),
        new("wu-no-drivers", "No driver updates through Windows Update",
            "Windows Update stops replacing drivers you installed from the PC or graphics-card maker. Get driver updates from the maker instead.",
            "Next update check", true,
            [new(true, WindowsUpdatePolicy, "ExcludeWUDriversInQualityUpdate", RegistryValueKind.DWord, 1)], "Windows Update"),
    ];

    /// <summary>Fast Startup only works when hibernation is enabled on this PC.</summary>
    public static bool HibernationAvailable()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            return key?.GetValue("HibernateEnabled") is int v && v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>The group Scan &amp; Fix suggests from. The other groups (privacy, Windows look and behaviour, Windows Update) are personal choices made on the Windows page.</summary>
    public const string ScanCategory = "Speed & convenience";

    /// <summary>
    /// The settings Scan &amp; Fix suggests: speed-and-convenience settings that aren't on yet and can be
    /// applied with the current rights (Fast Startup only when hibernation is enabled). The scan lists
    /// these and the fix applies the ones the user ticks, so both always work from the same list.
    /// </summary>
    public static IReadOnlyList<WindowsTweak> ScanSuggestions(bool elevated)
    {
        var hibernation = HibernationAvailable();
        return Catalog.Where(t => t.Category == ScanCategory
                                  && (!t.RequiresAdmin || elevated)
                                  && (t.Id != "fast-startup" || hibernation)
                                  && !IsApplied(t)).ToList();
    }

    public static WindowsTweak? Find(string id) => Catalog.FirstOrDefault(t => t.Id == id);

    /// <summary>Whether a registry value belongs to a catalog setting (undo records are checked against this).</summary>
    public static bool MayRestore(bool machine, string path, string name) =>
        Catalog.Any(t => t.Values.Any(v => v.Machine == machine && v.KeyPath.Equals(path, StringComparison.OrdinalIgnoreCase) && v.ValueName.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public static bool IsApplied(WindowsTweak tweak)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        foreach (var v in tweak.Values)
        {
            using var key = v.Hive.OpenSubKey(v.KeyPath);
            var current = key?.GetValue(v.ValueName);
            if (current is null || !string.Equals(Convert.ToString(current, System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Applies a catalog tweak and records one journal entry per value.</summary>
    public static void Apply(WindowsTweak tweak, SettingsJournal journal)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        ArgumentNullException.ThrowIfNull(journal);
        if (!Catalog.Contains(tweak))
        {
            throw new InvalidOperationException("unknown tweak");
        }
        foreach (var v in tweak.Values)
        {
            journal.Record(RegistryWriter.Write(v, $"{tweak.Title}"));
        }
    }

    // ------------------------------------------------------------------ per-app GPU

    private const string GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>
    /// Per-app graphics preference — the same setting as Settings → System → Display → Graphics.
    /// 1 = power saving (integrated GPU), 2 = high performance (discrete GPU).
    /// </summary>
    public static void SetGpuPreference(string exePath, int preference, SettingsJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (preference is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }
        if (string.IsNullOrEmpty(exePath) || !Path.IsPathFullyQualified(exePath) || exePath.Contains(';', StringComparison.Ordinal)
            || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exePath))
        {
            throw new ArgumentException("not a valid application path", nameof(exePath));
        }
        var spec = new RegistryValueSpec(false, GpuKey, exePath, RegistryValueKind.String, $"GpuPreference={preference};");
        journal.Record(RegistryWriter.Write(spec, $"GPU preference for {Path.GetFileName(exePath)} → {(preference == 2 ? "high performance" : "power saving")}"));
    }

    // ------------------------------------------------------------------ pointer precision

    private const uint SpiGetMouse = 0x0003, SpiSetMouse = 0x0004;

    public static unsafe int[] GetMouseParams()
    {
        var p = stackalloc int[3];
        SettingsApi.SystemParametersInfo(SpiGetMouse, 0, (nint)p, 0);
        return [p[0], p[1], p[2]];
    }

    public static unsafe void SetMouseParams(int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != 3 || values[2] is < 0 or > 2) { throw new ArgumentException("invalid mouse parameters", nameof(values)); }
        var p = stackalloc int[3];
        p[0] = values[0]; p[1] = values[1]; p[2] = values[2];
        SettingsApi.SystemParametersInfo(SpiSetMouse, 0, (nint)p, SettingsApi.SpifUpdateIniFile | SettingsApi.SpifSendChange);
    }

    /// <summary>"Enhance pointer precision" off = raw 1:1 mouse movement (preferred for gaming and precise work).</summary>
    public static bool PointerPrecisionEnabled() => GetMouseParams()[2] != 0;

    public static void DisablePointerPrecision(SettingsJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var before = GetMouseParams();
        SetMouseParams([0, 0, 0]);
        journal.Record(new SettingChange { Kind = SettingKind.MouseParameters, IntValues = before, Description = "Enhance pointer precision → off" });
    }
}

/// <summary>Writes one allow-listed value and returns the journal entry that undoes it.</summary>
public static class RegistryWriter
{
    public static SettingChange Write(RegistryValueSpec v, string description)
    {
        ArgumentNullException.ThrowIfNull(v);
        using var key = v.Hive.CreateSubKey(v.KeyPath, writable: true);
        var previous = key.GetValue(v.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var previousKind = previous is null ? (RegistryValueKind?)null : key.GetValueKind(v.ValueName);
        key.SetValue(v.ValueName, v.Value, v.Kind);
        return new SettingChange
        {
            Kind = SettingKind.RegistryValue,
            Description = description,
            RegMachine = v.Machine,
            RegPath = v.KeyPath,
            RegName = v.ValueName,
            RegOriginalKind = previousKind?.ToString(),
            RegOriginal = previous switch
            {
                null => null,
                byte[] b => Convert.ToBase64String(b),
                string[] m => string.Join('\0', m),
                _ => Convert.ToString(previous, System.Globalization.CultureInfo.InvariantCulture),
            },
        };
    }

    public static void Restore(SettingChange c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var hive = c.RegMachine ? Registry.LocalMachine : Registry.CurrentUser;
        using var key = hive.CreateSubKey(c.RegPath!, writable: true);
        if (c.RegOriginal is null || c.RegOriginalKind is null)
        {
            key.DeleteValue(c.RegName!, throwOnMissingValue: false);
            return;
        }
        var kind = Enum.Parse<RegistryValueKind>(c.RegOriginalKind);
        object value = kind switch
        {
            RegistryValueKind.DWord => int.Parse(c.RegOriginal, System.Globalization.CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => long.Parse(c.RegOriginal, System.Globalization.CultureInfo.InvariantCulture),
            RegistryValueKind.Binary => Convert.FromBase64String(c.RegOriginal),
            RegistryValueKind.MultiString => c.RegOriginal.Split('\0'),
            _ => c.RegOriginal,
        };
        key.SetValue(c.RegName!, value, kind);
    }
}
