using PerfTuner.Core.Apps;

namespace PerfTuner.Core.Tests;

public sealed class AppCatalogTests
{
    [Fact]
    public void Every_id_is_valid_unique_and_in_a_known_category()
    {
        Assert.All(AppCatalog.Apps, a => Assert.True(AppCatalog.IsValidId(a.Id), a.Id));
        Assert.Equal(AppCatalog.Apps.Count, AppCatalog.Apps.Select(a => a.Id.ToUpperInvariant()).Distinct().Count());
        Assert.All(AppCatalog.Apps, a => Assert.Contains(a.Category, AppCatalog.Categories));
        Assert.True(AppCatalog.Apps.Count >= 100);
    }

    [Theory]
    [InlineData("Mozilla.Firefox", true)]
    [InlineData("Microsoft.VCRedist.2015+.x64", true)]
    [InlineData("Notepad++.Notepad++", true)]
    [InlineData("--id", false)]
    [InlineData("a b.c", false)]
    [InlineData("x.y;calc", false)]
    [InlineData("\"quoted\".x", false)]
    [InlineData("NoDot", false)]
    [InlineData(null, false)]
    public void Id_validation(string? id, bool valid) => Assert.Equal(valid, AppCatalog.IsValidId(id));

    [Fact]
    public void Filter_by_text_and_category()
    {
        Assert.Contains(AppCatalog.Filter("fire", null), a => a.Id == "Mozilla.Firefox");
        Assert.All(AppCatalog.Filter(null, "Games"), a => Assert.Equal("Games", a.Category));
        Assert.Empty(AppCatalog.Filter("zzzz-nothing", null));
    }
}

public sealed class WinGetCommandsTests
{
    [Fact]
    public void Install_arguments_are_fixed_words_plus_the_catalog_id()
    {
        var args = WinGetCommands.Arguments(WinGetAction.Install, "VideoLAN.VLC");
        Assert.Equal(["install", "--id", "VideoLAN.VLC", "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity", "--accept-package-agreements", "--silent"], args);
        Assert.Equal("--all", WinGetCommands.Arguments(WinGetAction.UpgradeAll, null)[1]);
    }

    [Theory]
    [InlineData("Some.Unknown")]
    [InlineData("VideoLAN.VLC --override x")]
    [InlineData(null)]
    public void Ids_outside_the_catalog_are_refused(string? id) =>
        Assert.Throws<ArgumentException>(() => WinGetCommands.Arguments(WinGetAction.Install, id));

    [Theory]
    [InlineData("  ██████████▒▒▒▒  50 MB / 100 MB", null)]
    [InlineData("-", null)]
    [InlineData("   \\  ", null)]
    [InlineData("100%", null)]
    [InlineData("Successfully installed", "Successfully installed")]
    [InlineData("Found VLC media player [VideoLAN.VLC] Version 3.0.21", "Found VLC media player [VideoLAN.VLC] Version 3.0.21")]
    public void Clean_drops_progress_noise(string raw, string? expected) => Assert.Equal(expected, WinGetCommands.Clean(raw));

    [Fact]
    public void Known_result_codes_have_plain_words()
    {
        Assert.Equal("Done", WinGetCommands.Describe(0, ""));
        Assert.Equal("Already installed and up to date", WinGetCommands.Describe(unchecked((int)0x8A15002B), ""));
        Assert.Equal("Already installed", WinGetCommands.Describe(unchecked((int)0x8A15010D), ""));
        Assert.True(WinGetCommands.CountsAsSuccess(unchecked((int)0x8A150061)));
        Assert.False(WinGetCommands.CountsAsSuccess(unchecked((int)0x8A150056)));
        Assert.StartsWith("Stopped (code 0x80070005)", WinGetCommands.Describe(unchecked((int)0x80070005), ""), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_keeps_only_catalog_apps()
    {
        const string json = """{"Sources":[{"Packages":[{"PackageIdentifier":"videolan.vlc"},{"PackageIdentifier":"Someone.Else"}]}]}""";
        Assert.Equal(["VideoLAN.VLC"], WinGetCommands.ParseExport(json));
    }
}

public sealed class DnsPresetsTests
{
    [Fact]
    public void Presets_hold_valid_addresses_and_match_back()
    {
        Assert.All(PerfTuner.Core.Network.DnsPresets.All, p => Assert.True(PerfTuner.Core.Network.DnsPresets.AreAddresses(p.Servers), p.Id));
        Assert.Equal("quad9", PerfTuner.Core.Network.DnsPresets.Match(["9.9.9.9", "149.112.112.112"], false)?.Id);
        Assert.Equal("auto", PerfTuner.Core.Network.DnsPresets.Match(["192.168.1.1"], true)?.Id);
        Assert.Null(PerfTuner.Core.Network.DnsPresets.Match(["192.168.1.1"], false));
    }

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("1.1.1", false)]
    [InlineData("example.com", false)]
    [InlineData("1.1.1.1; calc", false)]
    public void Address_check(string s, bool ok) => Assert.Equal(ok, PerfTuner.Core.Network.DnsPresets.AreAddresses([s]));
}

public sealed class InstalledMatchingTests
{
    [Theory]
    [InlineData("Vivaldi", "Vivaldi", true)]
    [InlineData("7-Zip 24.08 (x64)", "7-Zip", true)]
    [InlineData("Python 3.13.0 (64-bit)", "Python 3.13", true)]
    [InlineData("Postman x86_64 11.31.3", "Postman", true)]
    [InlineData("GitHub Desktop", "Git", false)]
    [InlineData("Git", "Git", true)]
    [InlineData("Google Chrome", "Go", false)]
    [InlineData("Go Programming Language amd64 go1.23", "Go Programming Language", true)]
    [InlineData("Zoom Workplace (64-bit)", "Zoom", true)]
    [InlineData("Discordia", "Discord", false)]
    [InlineData("Microsoft Windows Desktop Runtime - 9.0.11 (x64)", "Microsoft Windows Desktop Runtime - 9", true)]
    [InlineData("Microsoft Windows Desktop Runtime - 8.0.11 (x64)", "Microsoft Windows Desktop Runtime - 9", false)]
    public void Installed_program_names(string candidate, string name, bool expected) => Assert.Equal(expected, InstalledMatching.NameMatches(candidate, name));

    [Theory]
    [InlineData("91750D7E.Slack", "Slack", true)]
    [InlineData("MSTeams", "MSTeams", true)]
    [InlineData("Microsoft.WindowsTerminal", "Windows Terminal", true)]
    [InlineData("SomeVendor.SlackTools", "Slack", false)]
    public void Store_package_names(string package, string name, bool expected) => Assert.Equal(expected, InstalledMatching.PackageMatches(package, name));

    [Fact]
    public void Finds_per_user_store_and_unpacked_apps()
    {
        InstalledEntry[] local =
        [
            new(InstallSource.InstalledPrograms, "Discord", "1.0.9266", @"C:\Users\x\AppData\Local\Discord\app.ico"),
            new(InstallSource.StorePackage, "91750D7E.Slack", "4.50.121.0", null),
            new(InstallSource.Shortcut, "Tor Browser", null, @"C:\Users\x\Desktop\Tor Browser.lnk"),
            new(InstallSource.InstalledPrograms, "Chromium", "133.0", null),
        ];
        Assert.Equal("1.0.9266", InstalledMatching.Find(AppCatalog.Find("Discord.Discord")!, local)?.Version);
        Assert.Equal(InstallSource.StorePackage, InstalledMatching.Find(AppCatalog.Find("SlackTechnologies.Slack")!, local)?.Source);
        Assert.Equal(InstallSource.Shortcut, InstalledMatching.Find(AppCatalog.Find("TorProject.TorBrowser")!, local)?.Source);
        Assert.NotNull(InstalledMatching.Find(AppCatalog.Find("Hibbiki.Chromium")!, local));
        Assert.Null(InstalledMatching.Find(AppCatalog.Find("eloston.ungoogled-chromium")!, local));
        Assert.Null(InstalledMatching.Find(AppCatalog.Find("Mozilla.Firefox")!, local));
    }

    [Fact]
    public void Office_add_ins_are_not_mistaken_for_the_app_and_icons_come_from_shortcuts()
    {
        InstalledEntry[] local =
        [
            new(InstallSource.InstalledPrograms, "Microsoft Teams Meeting Add-in for Microsoft Office", "1.24", null),
            new(InstallSource.InstalledPrograms, "Postman x86_64 11.31.3", "11.31.3", null),
            new(InstallSource.Shortcut, "Postman", null, @"C:\Users\x\Start Menu\Postman.lnk"),
        ];
        Assert.Null(InstalledMatching.Find(AppCatalog.Find("Microsoft.Teams")!, local));
        var postman = InstalledMatching.Find(AppCatalog.Find("Postman.Postman")!, local)!;
        Assert.Equal(InstallSource.InstalledPrograms, postman.Source);
        Assert.True(postman.IconFromShortcut);
        Assert.EndsWith("Postman.lnk", postman.IconPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_alias_belongs_to_a_catalog_app() =>
        Assert.All(AppCatalog.Apps, a => Assert.NotEmpty(InstalledMatching.NamesFor(a)));
}
