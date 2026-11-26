namespace PerfTuner.App;

/// <summary>One help entry: a short hint (shown by the "?" buttons) and the full text on the Help page.</summary>
public sealed record HelpTopic(string Id, string Page, string Title, string Hint, string Body)
{
    /// <summary>Tab index of the page this topic belongs to (for "Go to …" on the Help page).</summary>
    public string PageIndex => Page switch
    {
        "Speed" => "1",
        "Clean" => "2",
        "Startup" => "3",
        "Apps" => "4",
        "Troubleshoot" => "5",
        "History" => "6",
        "Settings" => "7",
        "Cockpit" => "11",
        "Security" => "12",
        "Get apps" => "13",
        "Windows" => "14",
        _ => "0",
    };
}

/// <summary>All help content in one place. "?" buttons refer to a topic by <see cref="HelpTopic.Id"/>.</summary>
public static class HelpTopics
{
    public static IReadOnlyList<HelpTopic> All { get; } =
    [
        // ---------------------------------------------------------------- Home
        new("health", "Home", "Health score",
            "A 0–100 score of how your PC is doing right now. Scan & Fix checks for problems and fixes them.",
            "The health score starts at 100 and goes down when the processor is busy for a long time, free memory is low, Windows slows the CPU down (heat, battery, power settings), or PerfTuner's data is out of date. A score above 85 means everything is fine.\n\nScan & Fix runs a short checkup: junk files, memory, background apps using a lot of CPU, CPU slowdowns, memory leaks, drive health, startup apps and Windows settings. Each finding is a real measurement. Click \"Show details\" under a finding to see exactly what it's made of; for Windows settings you tick the individual settings you want. You choose what to fix, and everything except deleting junk files can be undone in History."),
        new("mode", "Home", "Modes (Turbo · Smart · Silent)",
            "Pick one mode, or Off. Only one can be on at a time; switching undoes the previous mode.",
            "Turbo gives the app you're using top priority, holds back busy background apps and switches the power plan to maximum performance. Use it for games, video editing or anything heavy.\n\nSmart is hands-off everyday care. It calms down background apps that use a lot of CPU for 30 seconds or more, and frees memory automatically when free RAM drops below 20 %. It doesn't touch the power plan.\n\nSilent turns off the CPU's turbo boost and limits it to 80 %. The PC runs much cooler, fans get quieter and battery lasts longer. Background apps run in efficiency mode.\n\nOff means nothing happens automatically. Whenever you switch mode or choose Off, everything the previous mode changed is put back first, including power settings."),
        new("quick", "Home", "Quick actions",
            "Shortcuts to the things people use most. Each one is explained on its own page.",
            "Free up RAM asks background apps to hand back memory they aren't using. Clean junk files shows how much space you can get back. Faster startup opens the Startup page. Why is my PC slow? opens Troubleshoot. Control an app opens Apps. Undo everything puts every change PerfTuner made back the way it was."),
        new("vitals", "Home", "Live charts",
            "Processor, memory, disk and network use over the last minute.",
            "Processor and memory are shown as a percentage of what your PC has. Disk and network show how much data is moving per second. The small text under each chart adds detail, such as the CPU speed or how much memory is free.\n\nThe dot at the bottom of the sidebar shows whether the data is live (green), limited (amber, for example without administrator rights) or stale (red)."),
        new("activity", "Home", "What PerfTuner did",
            "Everything a mode or automatic fix did on its own, newest first.",
            "Each automatic action is listed here with the time and what caused it (Turbo, Smart, Silent, memory care or automatic fixes). If notifications are on (Settings), you also get a small pop-up from the tray icon. The full list is on the History page, where each change can be undone."),

        // ---------------------------------------------------------------- Speed
        new("ram", "Speed", "Free up RAM",
            "Background apps hand back memory they aren't using. Safe: anything still needed comes back on its own.",
            "Windows lets apps keep memory they used once but no longer need. Free up RAM asks every background app to return that memory, which helps when your PC is running low. The app you're using is skipped. PerfTuner measures how much was freed and tells you.\n\nIn Smart mode (and with memory care in the other modes), this also happens automatically when free memory drops below 20 %, at most every 3 minutes."),
        new("autofix", "Speed", "Automatic fixes",
            "PerfTuner tests fixes for slowdowns on its own and keeps only those that measurably help.",
            "PerfTuner learns what's normal for your PC: how fast the disk answers, how often slowdowns happen, memory paging, network errors and CPU slowdowns. After about an hour it notices when one of these gets worse.\n\nWith automatic fixes on, it picks a likely cause (for example a background app that keeps the disk busy) and tests a fix: 30 seconds measured before and 30 seconds after. The fix is kept only if it helps and nothing else gets worse; otherwise it is undone at once. You get a notification each time, and every test appears in History."),
        new("power", "Speed", "Power settings",
            "Power plan, turbo boost and CPU speed limits. Turbo and Silent set these for you.",
            "The power plan decides how eagerly Windows uses the processor. Turbo boost lets the CPU go above its normal speed for short bursts, which is fast but hot. A lower maximum CPU speed makes the PC cooler and quieter. A higher minimum makes it react faster but uses more power. Every change is recorded and can be undone in History."),

        // ---------------------------------------------------------------- Clean
        new("junk", "Clean", "Junk files",
            "Temporary files, crash dumps, old error reports and the Recycle Bin. Shows sizes first; you choose.",
            "Scan measures each type of junk without deleting anything. Clean selected removes only what you ticked. Temporary files are deleted only if they are older than 2 days, and files that are in use are skipped. Deleting is permanent: these files don't go to the Recycle Bin, so this is the only action that can't be undone."),
        new("tweaks", "Windows", "Windows settings",
            "Hand-picked, documented Windows settings. Switch one on, and off again to restore what you had.",
            "Each switch changes one documented Windows setting. PerfTuner records the old value first, so switching it off puts back exactly what was there before (also on uninstall). Some settings take effect immediately, some after you sign out or restart; each one says when. Settings marked \"needs full power\" need administrator rights."),
        new("drives", "Clean", "Optimize a drive",
            "Runs Windows' own drive optimization: TRIM for SSDs, defragmenting for hard disks.",
            "This is the same optimization Windows runs on a schedule. On an SSD it takes seconds and tells the drive which space is free. On a hard disk it rearranges files so they read faster, which can take a long time. It is safe to use while you work."),

        // ---------------------------------------------------------------- Startup
        new("boot", "Startup", "Boot time",
            "How long the last starts took, measured by Windows itself.",
            "Windows records how long each start takes until the desktop appears and until the PC is really ready to use. Under 30 seconds is fast, 30–60 seconds is average, and more than that is slow. Reading this record needs full power (administrator)."),
        new("startup-recs", "Startup", "Recommendations",
            "Concrete things that make your PC start faster, each with a button.",
            "Recommendations come from what PerfTuner measured: startup apps with a high impact, too many apps starting with Windows, Fast Startup being off, and drivers or services that Windows reports as slowing the start. Every change can be undone in History."),
        new("startup-apps", "Startup", "Apps that start with Windows",
            "Click a column title to sort. Turn off what you don't need right after signing in.",
            "Impact rates how much CPU and disk an app used when it started (High, Medium, Low). Starts after boot shows how many seconds after power-on it started. CPU time and Disk read show what it used. Boot delay is the extra time Windows measured this app adding to recent starts.\n\nTurning an app off only stops it from starting automatically. You can still open it any time, and turn it back on here or in History. The numbers are most accurate within 15 minutes of starting the PC."),

        // ---------------------------------------------------------------- Apps
        new("apps", "Apps", "Control an app",
            "Make an app faster or slower, put it in efficiency mode, or free its memory.",
            "Faster gives the app the CPU before others. Slower makes it give way. Efficiency mode makes it use less power and heat, like in Task Manager. Free its RAM asks it to hand back unused memory. Powerful GPU / Battery GPU picks the graphics card the next time it starts.\n\nWindows' own processes and security software are protected and can't be changed. Changes to running apps last until the app closes or PerfTuner exits, and can be undone in History."),
        new("rules", "Apps", "Every time this app starts",
            "Rules apply a setting automatically each time the app starts.",
            "For example, \"Always efficient\" puts a chat or cloud-sync app into efficiency mode every time it runs. Rules work while a mode is on. Remove a rule from the list at the bottom of the Apps page."),

        // ---------------------------------------------------------------- Troubleshoot
        new("troubleshoot", "Troubleshoot", "Troubleshoot",
            "Pick a question; PerfTuner measures and answers in plain words, with a fix button when there is one.",
            "Why was my PC slow just now? explains the latest slowdown PerfTuner recorded (in the last 30 minutes) and what caused it. Games stutter checks drivers for 10 seconds. Internet feels laggy looks for traffic spikes and lost packets. Opening files is slow measures your drive. PC gets slower after a while checks whether Windows is holding the CPU back. Memory keeps filling up looks for memory pressure and apps that never stop growing. Is my drive healthy? reads the drives' own health reports.\n\nSome checks need full power (administrator). The answer tells you if so."),

        // ---------------------------------------------------------------- History
        new("history", "History", "History and undo",
            "Everything PerfTuner changed, each with its own Undo, and Undo everything.",
            "Changes to running apps are undone automatically when the app closes or PerfTuner exits. Changes to Windows settings stay until you undo them, and are also undone if you uninstall PerfTuner. Ended apps and deleted junk files can't be brought back.\n\nIf PerfTuner ever notices the PC becoming unstable, it pauses all automatic tuning, undoes it and shows a notice here with a Resume button."),

        // ---------------------------------------------------------------- Settings
        new("themes", "Settings", "Colour themes",
            "27 themes: 6 classic (soft, rounded) and 21 metal (sharp, machined). Changes instantly.",
            "Classic themes: Lagoon, Ember and Violet Night are dark; Paper, Mint and Sunset are light. They have rounded corners and flat surfaces.\n\nMetal themes have square corners, panels with a light top edge and a dark bottom edge like machined metal plates, a faint brushed texture and a metal title bar. 14 are dark (Gunmetal, Titanium, Black Chrome, Carbon, Tungsten, Cobalt, four Anodized colours, Copper, Bronze, Damascus, Rust) and 7 are light (Brushed Steel, Aluminium, Chrome, Brass, Rose Gold, Verdigris, Gold). Every metal theme is checked for easy-to-read contrast. Your choice is remembered."),
        new("behaviour", "Settings", "Behaviour",
            "Notifications, the tray icon and automatic fixes.",
            "Notifications show a small pop-up from the tray icon when PerfTuner does something on its own. Keep running in the tray means closing the window doesn't stop PerfTuner; right-click the tray icon to switch modes, free RAM or exit. Automatic fixes is explained on the Speed page."),
        new("engine", "Settings", "Background service and full power",
            "PerfTuner's engine runs while the app is open. Full power (administrator) unlocks the deep checks.",
            "Nothing needs to be installed: the app starts its own engine and stops it, undoing live changes, when you exit. With administrator rights (\"full power\") PerfTuner can also read boot times, check drivers, disk and network timing, and change power and system-wide settings.\n\nThe optional always-on service keeps PerfTuner working before you sign in and while the app is closed. Install, update or remove it here."),

        // ---------------------------------------------------------------- Cockpit
        new("cockpit", "Cockpit", "Cockpit",
            "Your processor, memory, disk and network as living machinery. Click a part to look inside.",
            "Each tile shows one part of your PC with an animated icon. The busier the part, the faster the icon moves: the processor's cores light up and its glow pulses, the memory chips fill up, the disk spins or its lights flicker, and network waves move with the traffic.\n\nThe colour tells you how the part is doing: your theme colour means calm, amber means busy, red means strained. After a fix (freeing RAM, switching mode, optimizing or scanning a drive, changing the page file) the icon plays a short cool-down: it turns green, slows down and shows a check mark.\n\nThe Cockpit only asks the engine for extra measurements while it is open, so it costs nothing in the background. The animations pause when the window is minimized and stay still if you turned off animation effects in Windows."),
        new("cpu-detail", "Cockpit", "Processor",
            "Model, current speed, and whether Windows is holding the processor back.",
            "Speed now is how fast the processor really runs right now. Held back means Windows is slowing it down on purpose: because it's too hot, because the PC runs on battery, or because of the power settings. Turbo mode and a high-performance power plan remove the power-setting limits; heat can only be fixed with better airflow (clean the vents, don't block them).\n\nStatus: busy from 60 % load, strained from 90 % or whenever the processor is held back."),
        new("memory-detail", "Cockpit", "Memory (RAM)",
            "What's in use, what Windows keeps as cache, and what is free.",
            "In use is memory apps are working with. Cached is memory Windows fills with recently used files so they open faster; it's handed back instantly when an app needs it, so a full cache is a good thing. Available is what apps can get right away (free plus cache).\n\nCommitted memory is everything apps have reserved, in RAM and in the page file. When it reaches the limit, apps can fail or close. Status: busy when less than 30 % is available, strained below 15 % or when committed memory passes 90 % of the limit."),
        new("virtual-memory", "Cockpit", "Virtual memory (page file)",
            "Disk space Windows uses as extra memory. Letting Windows manage it is best for almost every PC.",
            "When RAM is full, Windows moves memory that isn't being used right now to the page file on disk. Windows also needs a page file on the Windows drive to save crash information.\n\nPerfTuner only suggests a change when it measured a reason: no page file at all, a custom size that's too small for how you use this PC (memory use came close to the limit) or too small for crash information, or a page file only on a hard disk while a fast SSD has room. PerfTuner never turns the page file off and never removes it from the Windows drive.\n\nThe advanced fixed size is limited to a safe range: at least what crash information needs, at most three times your RAM and a quarter of the drive's free space. Every change takes effect after a restart (PerfTuner never restarts the PC) and can be undone in History, by any administrator account and also on uninstall."),
        new("disk-detail", "Cockpit", "Drives and disk problems",
            "File system, space, health, temperature and wear of every drive, and problems in plain words.",
            "Drives are the parts with a letter (C:, D:…). File system shows the format and its version (for example NTFS 3.1); the version and the \"marked for a check\" flag need full power. Allocation is the cluster size. Physical disks are the actual SSDs and hard disks: Windows' health verdict, temperature, wear (how much of the rated write endurance is used), hours powered on and uncorrected errors.\n\nProblems PerfTuner looks for: an almost full drive, a drive Windows marked for a check or reports as unhealthy, uncorrected errors, high wear, overheating, and heavy fragmentation on hard disks. Disk status turns busy or strained when the disk is slow to answer (needs full power) or much busier than usual."),
        new("disk-scan", "Cockpit", "Analyze, optimize and scan",
            "Windows' own tools, run safely from here. All need full power.",
            "Analyze measures how fragmented a drive is. Fragmentation slows hard disks; SSDs don't care.\n\nOptimize runs Windows' own drive optimization: TRIM on SSDs (seconds), defragmenting on hard disks (can take a while; you can keep working).\n\nScan for errors checks the file system while the drive stays usable. If it finds errors that can only be fixed offline, Fix at next restart asks Windows to repair the drive the next time you restart. That repair can take from minutes to over an hour on big hard disks; it doesn't delete files, but as a repair it can't be undone."),
        new("network-detail", "Cockpit", "Network",
            "Traffic right now, lost packets, and your connected adapters.",
            "Download and upload show the traffic right now. Lost packets (packets that had to be sent again) are the usual cause of lag in games and calls: a few per thousand is normal, above 2 % the connection itself is the problem, often weak Wi-Fi. Checking for lost packets needs full power. Check my internet runs the full check on the Troubleshoot page."),
        new("space", "Cockpit", "Space explorer",
            "Find what's using space on a drive: a size tree, a treemap or a sunburst, plus reports.",
            "PerfTuner counts every file and folder on the drive (with administrator rights it can also count folders that only Windows can read). Results appear while it scans; you can stop any time and keep what was counted.\n\nTree shows the folders sorted by size, each with a bar for its share of the folder above; click the arrow to open a folder in place, or double-click to make it the current folder. Treemap draws the current folder as blocks sized by space and coloured by file type, so a single huge file stands out. Sunburst shows rings around the current folder; the inner ring is the biggest level. Switch between them at the top; your choice is remembered.\n\nReports: biggest files and folders, old and big files (100 MB or more, not changed for a year), the oldest files, files of one type (videos, disk images, installers…), how much each type uses in total, and duplicate files: files of 1 MB or more with exactly the same content (compared by size, then by a SHA-256 fingerprint of the content). Each copy set has a number; keep one copy and delete the others.\n\nLinks (junctions) are shown but not followed, so nothing is counted twice. Files that live only in the cloud (OneDrive) count as 0 because they use no space."),
        new("space-actions", "Cockpit", "Deleting and moving files",
            "Recycle Bin by default, permanent delete only after an extra tick, move with Windows' own folder picker.",
            "Select items in the tree, the treemap, the sunburst or a report (Ctrl or Shift selects several), then: Delete sends them to the Recycle Bin, so you can restore them; space is freed once the bin is emptied. Delete permanently skips the bin and can't be undone; tick \"I understand\" first. Move to… puts them in a folder you pick in Windows' own folder picker, on a local drive.\n\nLocked items (🔒) are never touched: Windows, installed programs, program data, system files, other users' folders, your main folders themselves (Documents, Downloads…: pick what's inside), and PerfTuner itself. Inside your AppData folder you get an extra warning, because apps keep their data there.\n\nEvery operation shows what will happen first, and is listed in History under \"Files you moved or deleted\": moves can be moved back from there one at a time. Undo everything and uninstall never move your files."),

        // ---------------------------------------------------------------- Security (v0.9)
        new("security", "Security", "Security check-up",
            "Finds gaps in Windows' protection and explains each one. Nothing changes until you approve a fix.",
            "PerfTuner reads what Windows itself reports, with documented Windows interfaces only: whether antivirus and firewall are on and up to date (Windows Security Center), SmartScreen, account control (UAC), Windows Update, Secure Boot, the TPM security chip, BitLocker drive encryption, memory integrity, LSA protection, and network exposure such as the old SMB1 file sharing, Remote Desktop and name broadcasting (LLMNR).\n\nThe \"Attacker's-eye view\" group lists what penetration testers check first on a Windows PC: services whose program path has spaces but no quotes, the \"always install elevated\" policy, folders on the system PATH that ordinary users can write to, automatic sign-in, the guest account, AutoRun for removable drives and the old PowerShell 2.0. Programs listening for network connections are listed for your information.\n\nEach item says what was found and why it matters. OK means nothing to do; Improve means a recommended hardening; Fix this means a real gap. Copy report puts a Markdown summary on the clipboard; it stays on this PC unless you paste it somewhere."),
        new("security-fixes", "Security", "Reviewing a fix",
            "Every fix shows exactly what it changes, when it takes effect and any side effects, and can be undone.",
            "Review fix shows the registry values the fix would set, when the change takes effect (now, after signing out or after a restart) and any warning, for example that LSA protection can stop very old security add-ons from loading. Nothing happens unless you choose Yes.\n\nFixes need full power (administrator). PerfTuner records each previous value before changing it, so every fix can be undone in History › Changes to Windows settings, or with Undo everything. Settings Windows manages for you, such as Secure Boot, TPM or BitLocker, are explained with a button to open the right Windows page instead of being changed here."),
        new("update-leftovers", "Clean", "Windows Update leftovers",
            "Space Windows Update keeps after updates are installed: downloads, delivery optimization, old components and orphaned installer packages.",
            "Update downloads (SoftwareDistribution\\Download) are files Windows already installed from. Emptying it stops the Windows Update services for a moment and starts them again afterwards; Windows downloads again whatever it still needs.\n\nDelivery Optimization cache holds update pieces shared with other PCs. Emptying it is safe.\n\nOld Windows components (WinSxS) runs Windows' own scheduled component cleanup, the same as \"DISM /Online /Cleanup-Image /StartComponentCleanup\": it removes component versions replaced by updates more than 30 days ago. It runs in the background and can take up to an hour; afterwards those updates can no longer be uninstalled.\n\nOrphaned installer packages are .msi and .msp files in Windows' installer cache (C:\\Windows\\Installer) that Windows Installer no longer lists for any installed program or update. PerfTuner asks Windows Installer itself, only offers files older than 30 days, and moves them to a folder you choose instead of deleting them. If a repair or uninstall later asks for one, move it back from History › Files you moved or deleted."),
        new("repair-tools", "Troubleshoot", "Repair tools",
            "Repair Windows' system files, reset Windows Update, create a restore point, and open classic Windows panels.",
            "Repair Windows checks the Windows component store for damage with Windows' own servicing (the same engine as \"DISM /RestoreHealth\") and, if it finds damage that can be repaired, repairs it using Windows Update. It can take 10–30 minutes and needs an internet connection.\n\nReset Windows Update is for when updates keep failing: it stops the update services, renames the SoftwareDistribution and catroot2 working folders (nothing is deleted; the old folders stay with a date in their name), and starts the services again. Windows rebuilds the folders and forgets its update history list, not the installed updates.\n\nCreate a restore point takes a System Restore snapshot. It needs System Protection turned on for drive C:; Windows allows one restore point per day from apps.\n\nThe classic panels open Windows' own Control Panel pages."),
        new("get-apps", "Get apps", "Get apps",
            "Install, update and remove apps from a hand-picked list, several at once, with Microsoft's Windows Package Manager.",
            "Click tiles to select apps (a green dot means open source), then choose Install, Update or Uninstall. PerfTuner shows the list again and runs nothing until you confirm. Apps are installed one after another, silently, by Windows Package Manager (winget), which is part of Windows' App Installer. Downloads come from Microsoft's winget package repository; each app's licence is its publisher's.\n\nClick a tile to select it and see its details on the right. Check what's installed asks Windows Package Manager which listed apps it manages, and also looks at Windows' installed-apps list, Microsoft Store packages and Start-menu shortcuts. Apps installed another way are marked \"Installed (another way)\": Windows Package Manager can't update or remove those copies; they usually update themselves. Icons of installed apps are read from the apps' own files on this PC; apps that aren't installed show their initials. Update all apps updates every app winget knows how to update, including apps that aren't in PerfTuner's list.\n\nHow it stays safe: PerfTuner starts winget.exe from its protected package folder (never a copy in a folder you can write to), passes only fixed options and the identifier of an app from its own list, and never runs a command line. If App Installer is missing, get it from the Microsoft Store."),
        new("windows-features", "Windows", "Windows features",
            "Turn optional parts of Windows on or off: Linux subsystem, Sandbox, Hyper-V, .NET 3.5, legacy media, NFS, Telnet, OpenSSH server.",
            "These are the same optional features as in \"Turn Windows features on or off\" and Settings › System › Optional features. PerfTuner asks Windows' own servicing engine (the DISM API) to turn them on or off, so nothing is downloaded from anywhere else; .NET 3.5 and the OpenSSH server fetch their files from Windows Update.\n\nMost changes need a restart to finish. Features your Windows edition doesn't include (for example Sandbox and Hyper-V on Home) are shown as not available."),
        new("dns", "Windows", "DNS",
            "Choose which servers translate web addresses for this PC, and go back to your previous setting at any time.",
            "DNS servers turn names like example.com into addresses. By default Windows uses the ones your router hands out. Public resolvers can be faster, keep fewer logs, or block malware, ads and trackers.\n\nPerfTuner changes the IPv4 DNS servers of every active network connection with Windows' documented network-configuration interface. Before the first change it saves each connection's setting where only administrators can write, so Restore previous puts back exactly what was there, including automatic. Only IPv4 DNS is changed: if your network also hands out IPv6 DNS servers, Windows can still use those, so blocking presets may be bypassed there. Changes take effect immediately. VPN apps that manage DNS themselves keep doing so."),
        new("undocumented", "Undocumented", "Undocumented settings",
            "Hidden Windows settings Microsoft never documented, plus a few documented ones that are widely misused. Each says honestly what it does. Might be dangerous.",
            "Every other page uses only what Microsoft documents. This page is the exception, and it stays hidden behind a warning until you confirm it once.\n\nEach item carries a label: Works (a real, visible effect), Niche (real, but only in certain situations), Placebo (Windows reads it, but there's no measurable gain), No effect (current Windows ignores it), Risky (usually makes things worse), Security risk (lowers protection) and Setup only (read only while Windows Setup runs). Items that don't exist on your Windows version are greyed out.\n\nPerfTuner asks before every change, and asks a second time for risky ones. It records what was there before, so switching an item off (or Undo in History) puts back exactly the previous value, recreates a removed key or restores a service's start type. The two one-off actions at the bottom can't be undone.\n\nBoot and timer items change the running Windows' boot settings with Windows' own bcdedit, with fixed arguments only. The virtual-desktop helpers read Explorer's own list of desktops and switch with Windows' Win+Ctrl+←/→ shortcut, so they don't use any hidden Windows interface; a Windows update that changes that list can still stop them working. Microsoft can change or remove any of these settings in an update."),
        new("network-repair", "Troubleshoot", "Network repair",
            "Five steps for when the internet works on other devices but not on this PC.",
            "Clear DNS cache makes Windows look up web addresses again (helps when a site moved). Renew IP address asks your router for a new address; connections drop for a few seconds. Reset Winsock restores Windows' network socket catalog, which VPN, proxy or security programs sometimes leave broken. Reset TCP/IP rewrites Windows' TCP/IP settings to their defaults and removes addresses and DNS servers you set by hand. The last two need a restart and run Windows' own netsh with fixed arguments. Windows' network reset (in Settings) goes further: it removes and reinstalls every network adapter."),
        new("built-in-apps", "Get apps", "Built-in apps and Store repair",
            "Remove built-in apps you don't use, and repair a Microsoft Store that won't open.",
            "The list shows only built-in apps installed for your account from a fixed list (News, Weather, Solitaire, Xbox apps, Clipchamp and others). Removing uses Windows' own package manager, for your account only; other accounts keep them, and you can reinstall them from Microsoft Store. PerfTuner can't undo a removal itself.\n\nClear Store cache runs Windows' own WSReset. Re-register Microsoft Store and Re-register all my apps register the apps again from their installed files, which fixes a Store or built-in apps that close right away or don't open."),
    ];

    public static HelpTopic Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}
