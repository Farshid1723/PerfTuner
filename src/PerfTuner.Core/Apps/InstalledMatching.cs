namespace PerfTuner.Core.Apps;

public enum InstallSource { InstalledPrograms, StorePackage, Shortcut }

/// <summary>Something on this PC that looks like an installed app (an "Installed apps" entry, an MSIX package or a shortcut).</summary>
public sealed record InstalledEntry(InstallSource Source, string Name, string? Version, string? IconPath)
{
    /// <summary>The icon path is a shortcut (.lnk) rather than an icon resource.</summary>
    public bool IconFromShortcut { get; init; } = Source == InstallSource.Shortcut;
}

/// <summary>How a catalog app is present on this PC.</summary>
public enum InstallState { NotInstalled, Managed, InstalledElsewhere }

/// <summary>
/// Recognises catalog apps that Windows Package Manager can't connect to a package (per-user installs, Store/MSIX
/// copies, apps that just unpack into a folder): by the name in Windows' installed-programs list, the MSIX package name,
/// or a Start-menu/desktop shortcut.
/// </summary>
public static class InstalledMatching
{
    /// <summary>Names an app uses on Windows when they differ from its catalog name.</summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Google.Chrome"] = ["Google Chrome"],
        ["Microsoft.Teams"] = ["=Microsoft Teams", "=Microsoft Teams classic", "MSTeams"],
        ["Telegram.TelegramDesktop"] = ["Telegram Desktop", "TelegramDesktop"],
        ["OpenWhisperSystems.Signal"] = ["Signal"],
        ["Rakuten.Viber"] = ["Viber"],
        ["TeamSpeakSystems.TeamSpeakClient"] = ["TeamSpeak"],
        ["Microsoft.VisualStudioCode"] = ["Microsoft Visual Studio Code", "Visual Studio Code"],
        ["Python.Python.3.13"] = ["Python 3.13"],
        ["OpenJS.NodeJS.LTS"] = ["Node.js"],
        ["GoLang.Go"] = ["Go Programming Language"],
        ["Microsoft.DotNet.SDK.9"] = ["Microsoft .NET SDK 9"],
        ["Microsoft.PowerShell"] = ["PowerShell 7"],
        ["Microsoft.WindowsTerminal"] = ["Windows Terminal", "WindowsTerminal"],
        ["SublimeHQ.SublimeText.4"] = ["Sublime Text"],
        ["Microsoft.VisualStudio.2022.Community"] = ["Visual Studio Community 2022"],
        ["Amazon.Corretto.21.JDK"] = ["Amazon Corretto"],
        ["Adobe.Acrobat.Reader.64-bit"] = ["Adobe Acrobat"],
        ["SumatraPDF.SumatraPDF"] = ["SumatraPDF"],
        ["ONLYOFFICE.DesktopEditors"] = ["ONLYOFFICE"],
        ["EpicGames.EpicGamesLauncher"] = ["Epic Games Launcher"],
        ["ElectronicArts.EADesktop"] = ["EA app"],
        ["HeroicGamesLauncher.HeroicGamesLauncher"] = ["Heroic"],
        ["VideoLAN.VLC"] = ["VLC media player", "VLC"],
        ["GIMP.GIMP.3"] = ["GIMP"],
        ["Spotify.Spotify"] = ["Spotify", "SpotifyMusic"],
        ["dotPDN.PaintDotNet"] = ["paint.net", "Paint.NET"],
        ["clsid2.mpc-hc"] = ["MPC-HC"],
        ["CodecGuide.K-LiteCodecPack.Standard"] = ["K-Lite Codec Pack"],
        ["7zip.7zip"] = ["7-Zip"],
        ["voidtools.Everything"] = ["Everything"],
        ["Microsoft.PowerToys"] = ["PowerToys"],
        ["Google.GoogleDrive"] = ["Google Drive"],
        ["Nextcloud.NextcloudDesktop"] = ["Nextcloud"],
        ["File-New-Project.EarTrumpet"] = ["EarTrumpet"],
        ["Microsoft.Sysinternals.ProcessExplorer"] = ["Process Explorer", "procexp"],
        ["Microsoft.Sysinternals.Autoruns"] = ["Autoruns"],
        ["Microsoft.Sysinternals.ProcessMonitor"] = ["Process Monitor", "procmon"],
        ["Microsoft.Sysinternals.TCPView"] = ["TCPView"],
        ["Guru3D.Afterburner"] = ["MSI Afterburner"],
        ["Oracle.VirtualBox"] = ["Oracle VirtualBox", "Oracle VM VirtualBox", "VirtualBox"],
        ["Proton.ProtonVPN"] = ["Proton VPN", "ProtonVPN"],
        ["Microsoft.DotNet.DesktopRuntime.9"] = ["Microsoft Windows Desktop Runtime - 9"],
        ["Microsoft.DotNet.DesktopRuntime.8"] = ["Microsoft Windows Desktop Runtime - 8"],
        ["Microsoft.VCRedist.2015+.x64"] = ["Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Visual C++ 2015-2019 Redistributable (x64)"],
        ["Microsoft.VCRedist.2015+.x86"] = ["Microsoft Visual C++ 2015-2022 Redistributable (x86)", "Microsoft Visual C++ 2015-2019 Redistributable (x86)"],
        ["Microsoft.EdgeWebView2Runtime"] = ["Microsoft Edge WebView2 Runtime"],
        ["Oracle.JavaRuntimeEnvironment"] = ["Java 8 Update", "Java(TM)"],
        ["Hibbiki.Chromium"] = ["Chromium"],
        ["eloston.ungoogled-chromium"] = ["Ungoogled Chromium"],
        ["Zen-Team.Zen-Browser"] = ["Zen Browser"],
        ["Mozilla.Firefox"] = ["Mozilla Firefox", "Firefox"],
        ["Mozilla.Thunderbird"] = ["Mozilla Thunderbird", "Thunderbird"],
        ["Brave.Brave"] = ["Brave"],
        ["TorProject.TorBrowser"] = ["Tor Browser"],
        ["Insecure.Nmap"] = ["Nmap", "Zenmap"],
        ["WiresharkFoundation.Wireshark"] = ["Wireshark"],
        ["CPUID.CPU-Z"] = ["CPUID CPU-Z", "CPU-Z"],
        ["CPUID.HWMonitor"] = ["CPUID HWMonitor", "HWMonitor"],
        ["REALiX.HWiNFO"] = ["HWiNFO"],
        ["TechPowerUp.GPU-Z"] = ["GPU-Z", "TechPowerUp GPU-Z"],
        ["Malwarebytes.Malwarebytes"] = ["Malwarebytes"],
    };

    /// <summary>Names to look for; a leading "=" means the whole name must match (no "Name …" variants).</summary>
    public static IReadOnlyList<string> NamesFor(CatalogApp app) =>
        !Aliases.TryGetValue(app.Id, out var names) ? [app.Name]
        : names.Any(n => n.Equals("=" + app.Name, StringComparison.OrdinalIgnoreCase)) ? names : [.. names, app.Name];

    private static bool Matches(InstalledEntry e, string name)
    {
        if (name.StartsWith('='))
        {
            var exact = name[1..];
            return e.Source == InstallSource.StorePackage ? PackageMatches(e.Name, exact) : e.Name.Trim().Equals(exact, StringComparison.OrdinalIgnoreCase);
        }
        return e.Source == InstallSource.StorePackage ? PackageMatches(e.Name, name) : NameMatches(e.Name, name);
    }

    /// <summary>
    /// "Name" matches "Name", "Name 1.2", "Name (x64)", "Name - 9.0.1" but not "NameOther" or "GitHub" for "Git".
    /// </summary>
    public static bool NameMatches(string candidate, string name)
    {
        candidate = candidate.Trim();
        if (!candidate.StartsWith(name, StringComparison.OrdinalIgnoreCase)) { return false; }
        if (candidate.Length == name.Length) { return true; }
        return !char.IsLetterOrDigit(candidate[name.Length]);
    }

    /// <summary>An MSIX package name such as "91750D7E.Slack": the part after the publisher prefix must equal a name (spaces ignored).</summary>
    public static bool PackageMatches(string packageName, string name)
    {
        var dot = packageName.IndexOf('.', StringComparison.Ordinal);
        var tail = dot < 0 ? packageName : packageName[(dot + 1)..];
        var compact = name.Replace(" ", "", StringComparison.Ordinal);
        return tail.Equals(compact, StringComparison.OrdinalIgnoreCase) || packageName.Equals(compact, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The best local entry for an app: an installed-programs entry first, then a Store package, then a shortcut. If the
    /// best entry has no icon, the icon of another match (usually its shortcut) is used.
    /// </summary>
    public static InstalledEntry? Find(CatalogApp app, IEnumerable<InstalledEntry> entries)
    {
        var names = NamesFor(app);
        var matches = entries.Where(e => names.Any(n => Matches(e, n))).OrderBy(e => e.Source).ThenByDescending(e => e.IconPath is not null).ToList();
        if (matches.Count == 0) { return null; }
        var best = matches[0];
        if (best.IconPath is null && matches.FirstOrDefault(m => m.IconPath is not null) is { } withIcon)
        {
            best = best with { IconPath = withIcon.IconPath, IconFromShortcut = withIcon.Source == InstallSource.Shortcut };
        }
        return best;
    }
}
