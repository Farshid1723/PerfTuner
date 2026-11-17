namespace PerfTuner.Core.Apps;

/// <summary>One app PerfTuner can install, update or remove through Windows Package Manager.</summary>
/// <param name="Id">The package identifier in Microsoft's winget community repository (exact match).</param>
public sealed record CatalogApp(string Id, string Name, string Category, string Description, bool OpenSource);

/// <summary>
/// The built-in list of apps (v0.10). Only these identifiers are ever passed to Windows Package Manager: nothing typed
/// by the user reaches it. Every identifier is checked against <see cref="IsValidId"/> by the tests.
/// </summary>
public static class AppCatalog
{
    public static IReadOnlyList<string> Categories { get; } =
        ["Browsers", "Communication", "Development", "Documents", "Games", "Media", "Utilities", "System tools", "Network & security", "Runtimes"];

    public static IReadOnlyList<CatalogApp> Apps { get; } =
    [
        new("Mozilla.Firefox", "Firefox", "Browsers", "Mozilla's independent, privacy-minded browser", true),
        new("Google.Chrome", "Google Chrome", "Browsers", "Google's browser", false),
        new("Brave.Brave", "Brave", "Browsers", "Chromium-based browser with a built-in ad and tracker blocker", true),
        new("Vivaldi.Vivaldi", "Vivaldi", "Browsers", "Highly customisable Chromium-based browser", false),
        new("Opera.Opera", "Opera", "Browsers", "Chromium-based browser with a built-in VPN", false),
        new("LibreWolf.LibreWolf", "LibreWolf", "Browsers", "Firefox variant tuned for privacy", true),
        new("TorProject.TorBrowser", "Tor Browser", "Browsers", "Browses through the Tor network for anonymity", true),
        new("Waterfox.Waterfox", "Waterfox", "Browsers", "Firefox-based browser without telemetry", true),
        new("Zen-Team.Zen-Browser", "Zen Browser", "Browsers", "Firefox-based browser with vertical tabs", true),
        new("Hibbiki.Chromium", "Chromium", "Browsers", "The open-source base of Chrome", true),
        new("eloston.ungoogled-chromium", "Ungoogled Chromium", "Browsers", "Chromium without Google services", true),
        new("Discord.Discord", "Discord", "Communication", "Voice, video and text chat for communities", false),
        new("SlackTechnologies.Slack", "Slack", "Communication", "Team messaging", false),
        new("Microsoft.Teams", "Microsoft Teams", "Communication", "Meetings and team chat", false),
        new("Zoom.Zoom", "Zoom", "Communication", "Video meetings", false),
        new("Telegram.TelegramDesktop", "Telegram", "Communication", "Cloud messaging", true),
        new("OpenWhisperSystems.Signal", "Signal", "Communication", "End-to-end encrypted messaging", true),
        new("Element.Element", "Element", "Communication", "Secure chat on the Matrix network", true),
        new("Mozilla.Thunderbird", "Thunderbird", "Communication", "Email, calendar and contacts", true),
        new("Rakuten.Viber", "Viber", "Communication", "Messaging and calls", false),
        new("TeamSpeakSystems.TeamSpeakClient", "TeamSpeak", "Communication", "Voice chat for gaming", false),
        new("Git.Git", "Git", "Development", "Version control system", true),
        new("Microsoft.VisualStudioCode", "Visual Studio Code", "Development", "Code editor", false),
        new("VSCodium.VSCodium", "VSCodium", "Development", "Code editor built from the open-source VS Code sources", true),
        new("GitHub.GitHubDesktop", "GitHub Desktop", "Development", "Git client for GitHub repositories", true),
        new("Python.Python.3.13", "Python 3.13", "Development", "Python programming language", true),
        new("OpenJS.NodeJS.LTS", "Node.js LTS", "Development", "JavaScript runtime, long-term support release", true),
        new("GoLang.Go", "Go", "Development", "Go programming language", true),
        new("Rustlang.Rustup", "Rustup", "Development", "Installer for the Rust toolchain", true),
        new("Microsoft.DotNet.SDK.9", ".NET 9 SDK", "Development", "Build .NET apps", true),
        new("Microsoft.PowerShell", "PowerShell 7", "Development", "Modern cross-platform PowerShell", true),
        new("Microsoft.WindowsTerminal", "Windows Terminal", "Development", "Tabbed terminal for command lines", true),
        new("Neovim.Neovim", "Neovim", "Development", "Extensible text editor", true),
        new("Notepad++.Notepad++", "Notepad++", "Development", "Lightweight code and text editor", true),
        new("SublimeHQ.SublimeText.4", "Sublime Text", "Development", "Fast text editor", false),
        new("JetBrains.Toolbox", "JetBrains Toolbox", "Development", "Installs and updates JetBrains IDEs", false),
        new("Kitware.CMake", "CMake", "Development", "Build system generator", true),
        new("Docker.DockerDesktop", "Docker Desktop", "Development", "Containers on Windows", false),
        new("Postman.Postman", "Postman", "Development", "API testing", false),
        new("Microsoft.VisualStudio.2022.Community", "Visual Studio 2022 Community", "Development", "Full IDE for .NET and C++", false),
        new("astral-sh.uv", "uv", "Development", "Fast Python package manager", true),
        new("pnpm.pnpm", "pnpm", "Development", "Fast Node.js package manager", true),
        new("JesseDuffield.lazygit", "lazygit", "Development", "Terminal UI for Git", true),
        new("Amazon.Corretto.21.JDK", "Amazon Corretto 21", "Development", "Java 21 (LTS) development kit", true),
        new("Adobe.Acrobat.Reader.64-bit", "Adobe Acrobat Reader", "Documents", "Read and sign PDF files", false),
        new("SumatraPDF.SumatraPDF", "SumatraPDF", "Documents", "Small, fast PDF and e-book reader", true),
        new("TheDocumentFoundation.LibreOffice", "LibreOffice", "Documents", "Office suite: documents, spreadsheets, presentations", true),
        new("ONLYOFFICE.DesktopEditors", "ONLYOFFICE", "Documents", "Office suite compatible with Microsoft formats", true),
        new("Obsidian.Obsidian", "Obsidian", "Documents", "Notes in local Markdown files", false),
        new("Joplin.Joplin", "Joplin", "Documents", "Notes and to-dos with sync", true),
        new("DigitalScholar.Zotero", "Zotero", "Documents", "Collect and cite research sources", true),
        new("calibre.calibre", "calibre", "Documents", "E-book library and converter", true),
        new("Notion.Notion", "Notion", "Documents", "Notes, docs and wikis", false),
        new("Valve.Steam", "Steam", "Games", "Game store and launcher", false),
        new("EpicGames.EpicGamesLauncher", "Epic Games Launcher", "Games", "Game store and launcher", false),
        new("GOG.Galaxy", "GOG Galaxy", "Games", "Game launcher for GOG and other stores", false),
        new("ElectronicArts.EADesktop", "EA app", "Games", "Game launcher for EA games", false),
        new("Ubisoft.Connect", "Ubisoft Connect", "Games", "Game launcher for Ubisoft games", false),
        new("HeroicGamesLauncher.HeroicGamesLauncher", "Heroic Games Launcher", "Games", "Open-source launcher for Epic, GOG and Amazon games", true),
        new("Playnite.Playnite", "Playnite", "Games", "One library for games from every launcher", true),
        new("PrismLauncher.PrismLauncher", "Prism Launcher", "Games", "Manages Minecraft instances", true),
        new("VideoLAN.VLC", "VLC media player", "Media", "Plays almost any video or audio file", true),
        new("OBSProject.OBSStudio", "OBS Studio", "Media", "Screen recording and live streaming", true),
        new("Audacity.Audacity", "Audacity", "Media", "Audio recording and editing", true),
        new("GIMP.GIMP.3", "GIMP", "Media", "Image editor", true),
        new("BlenderFoundation.Blender", "Blender", "Media", "3D modelling, animation and rendering", true),
        new("HandBrake.HandBrake", "HandBrake", "Media", "Video converter", true),
        new("Spotify.Spotify", "Spotify", "Media", "Music streaming", false),
        new("KDE.Krita", "Krita", "Media", "Digital painting", true),
        new("Inkscape.Inkscape", "Inkscape", "Media", "Vector graphics editor", true),
        new("ShareX.ShareX", "ShareX", "Media", "Screenshots and screen recordings", true),
        new("dotPDN.PaintDotNet", "Paint.NET", "Media", "Easy image editor", false),
        new("IrfanSkiljan.IrfanView", "IrfanView", "Media", "Fast image viewer", false),
        new("clsid2.mpc-hc", "MPC-HC", "Media", "Lightweight media player", true),
        new("CodecGuide.K-LiteCodecPack.Standard", "K-Lite Codec Pack", "Media", "Codecs for playing more video formats", false),
        new("7zip.7zip", "7-Zip", "Utilities", "Archive manager (zip, 7z, rar…)", true),
        new("voidtools.Everything", "Everything", "Utilities", "Finds files by name instantly", false),
        new("AntibodySoftware.WizTree", "WizTree", "Utilities", "Fast disk space analyser", false),
        new("Microsoft.PowerToys", "PowerToys", "Utilities", "Power-user tools from Microsoft", true),
        new("Rufus.Rufus", "Rufus", "Utilities", "Creates bootable USB drives", true),
        new("Ventoy.Ventoy", "Ventoy", "Utilities", "Bootable USB with many ISO files", true),
        new("KeePassXCTeam.KeePassXC", "KeePassXC", "Utilities", "Offline password manager", true),
        new("Bitwarden.Bitwarden", "Bitwarden", "Utilities", "Password manager", true),
        new("Dropbox.Dropbox", "Dropbox", "Utilities", "Cloud file sync", false),
        new("Google.GoogleDrive", "Google Drive", "Utilities", "Cloud file sync", false),
        new("Nextcloud.NextcloudDesktop", "Nextcloud", "Utilities", "Sync with your own Nextcloud server", true),
        new("qBittorrent.qBittorrent", "qBittorrent", "Utilities", "BitTorrent client", true),
        new("AnyDesk.AnyDesk", "AnyDesk", "Utilities", "Remote desktop", false),
        new("AutoHotkey.AutoHotkey", "AutoHotkey", "Utilities", "Keyboard and mouse automation", true),
        new("Klocman.BulkCrapUninstaller", "Bulk Crap Uninstaller", "Utilities", "Removes many programs and their leftovers", true),
        new("LocalSend.LocalSend", "LocalSend", "Utilities", "Share files with nearby devices", true),
        new("File-New-Project.EarTrumpet", "EarTrumpet", "Utilities", "Per-app volume control", true),
        new("Ghisler.TotalCommander", "Total Commander", "Utilities", "Two-panel file manager", false),
        new("CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", "System tools", "Drive health (S.M.A.R.T.)", true),
        new("CrystalDewWorld.CrystalDiskMark", "CrystalDiskMark", "System tools", "Drive speed test", true),
        new("CPUID.CPU-Z", "CPU-Z", "System tools", "Processor and memory details", false),
        new("TechPowerUp.GPU-Z", "GPU-Z", "System tools", "Graphics card details", false),
        new("REALiX.HWiNFO", "HWiNFO", "System tools", "Detailed hardware information and sensors", false),
        new("CPUID.HWMonitor", "HWMonitor", "System tools", "Temperatures, voltages and fans", false),
        new("Microsoft.Sysinternals.ProcessExplorer", "Process Explorer", "System tools", "Advanced task manager from Microsoft", false),
        new("Microsoft.Sysinternals.Autoruns", "Autoruns", "System tools", "Everything that starts with Windows", false),
        new("Microsoft.Sysinternals.ProcessMonitor", "Process Monitor", "System tools", "Live file, registry and process activity", false),
        new("Microsoft.Sysinternals.TCPView", "TCPView", "System tools", "Live network connections", false),
        new("Guru3D.Afterburner", "MSI Afterburner", "System tools", "Graphics card tuning and monitoring", false),
        new("Oracle.VirtualBox", "VirtualBox", "System tools", "Run virtual machines", true),
        new("WiresharkFoundation.Wireshark", "Wireshark", "Network & security", "Network traffic analyser", true),
        new("Insecure.Nmap", "Nmap", "Network & security", "Network scanner", true),
        new("PuTTY.PuTTY", "PuTTY", "Network & security", "SSH and Telnet client", true),
        new("WinSCP.WinSCP", "WinSCP", "Network & security", "SFTP and FTP file transfer", true),
        new("WireGuard.WireGuard", "WireGuard", "Network & security", "Fast, modern VPN", true),
        new("OpenVPNTechnologies.OpenVPNConnect", "OpenVPN Connect", "Network & security", "OpenVPN client", false),
        new("Proton.ProtonVPN", "Proton VPN", "Network & security", "VPN service", true),
        new("Malwarebytes.Malwarebytes", "Malwarebytes", "Network & security", "Second-opinion malware scanner", false),
        new("Microsoft.DotNet.DesktopRuntime.9", ".NET 9 Desktop Runtime", "Runtimes", "Needed by .NET 9 desktop apps (PerfTuner uses it)", true),
        new("Microsoft.DotNet.DesktopRuntime.8", ".NET 8 Desktop Runtime", "Runtimes", "Needed by .NET 8 desktop apps", true),
        new("Microsoft.VCRedist.2015+.x64", "Visual C++ Redistributable (x64)", "Runtimes", "Needed by many games and apps", false),
        new("Microsoft.VCRedist.2015+.x86", "Visual C++ Redistributable (x86)", "Runtimes", "Needed by older 32-bit apps", false),
        new("Microsoft.EdgeWebView2Runtime", "WebView2 Runtime", "Runtimes", "Needed by apps with web-based screens", false),
        new("Oracle.JavaRuntimeEnvironment", "Java Runtime", "Runtimes", "Runs Java apps", false),
    ];

    /// <summary>A plain winget package identifier: Publisher.Name[.More], letters, digits and . - _ + only.</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: >= 3 and <= 128 } && char.IsAsciiLetterOrDigit(id[0]) && id.Contains('.', StringComparison.Ordinal)
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');

    public static CatalogApp? Find(string id) => Apps.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Search by name, description or category (case-insensitive), optionally within one category.</summary>
    public static IEnumerable<CatalogApp> Filter(string? text, string? category)
    {
        var t = text?.Trim() ?? "";
        return Apps.Where(a => (string.IsNullOrEmpty(category) || a.Category == category)
            && (t.Length == 0 || a.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(t, StringComparison.OrdinalIgnoreCase)
                || a.Id.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }
}
