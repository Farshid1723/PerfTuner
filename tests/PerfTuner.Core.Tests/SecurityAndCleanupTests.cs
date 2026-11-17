using PerfTuner.Core.Security;
using PerfTuner.Core.Space;

namespace PerfTuner.Core.Tests;

public class SecurityRulesTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Vendor App\svc.exe", true)]
    [InlineData(@"C:\Program Files\Vendor App\svc.exe -k run", true)]
    [InlineData(@"""C:\Program Files\Vendor App\svc.exe"" -k run", false)]
    [InlineData(@"C:\Tools\svc.exe", false)]                                  // no space: nothing to exploit
    [InlineData(@"C:\Windows\system32\svchost.exe -k netsvcs -p", false)]     // space only in arguments
    [InlineData(@"\SystemRoot\System32\drivers\x.sys", false)]                // drivers aren't .exe
    [InlineData(@"system32\DRIVERS\y.sys", false)]
    [InlineData("", false)]
    public void Unquoted(string imagePath, bool expected) => Assert.Equal(expected, SecurityRules.IsUnquotedServicePath(imagePath));

    [Theory]
    [InlineData(@"C:\Program Files\Vendor App\svc.exe", @"""C:\Program Files\Vendor App\svc.exe""")]
    [InlineData(@"C:\Program Files\Vendor App\svc.EXE /run now", @"""C:\Program Files\Vendor App\svc.EXE"" /run now")]
    public void Quote(string imagePath, string expected) => Assert.Equal(expected, SecurityRules.QuoteServicePath(imagePath));

    [Fact]
    public void Quote_refuses_safe_paths() => Assert.Throws<ArgumentException>(() => SecurityRules.QuoteServicePath(@"C:\Tools\svc.exe"));

    [Theory]
    [InlineData(0u, SecurityStatus.Good)]
    [InlineData(5u, SecurityStatus.Good)]
    [InlineData(8u, SecurityStatus.Warn)]
    [InlineData(30u, SecurityStatus.Bad)]
    public void Signature_age(uint days, SecurityStatus expected) => Assert.Equal(expected, SecurityRules.SignatureAge(days));

    [Theory]
    [InlineData(10, SecurityStatus.Good)]
    [InlineData(40, SecurityStatus.Warn)]
    [InlineData(100, SecurityStatus.Bad)]
    public void Update_age(int days, SecurityStatus expected) => Assert.Equal(expected, SecurityRules.UpdateAge(TimeSpan.FromDays(days)));

    [Fact]
    public void Score_counts_good_out_of_known()
    {
        var s = SecurityRules.Score([SecurityStatus.Good, SecurityStatus.Good, SecurityStatus.Bad, SecurityStatus.Unknown, SecurityStatus.Info]);
        Assert.Equal((2, 3), s);
    }
}

public class InstallerOrphansTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Unreferenced_old_msi_and_msp_are_orphans()
    {
        var files = new[]
        {
            new InstallerFile(@"C:\Windows\Installer\1a2b.msi", 10, Now.AddDays(-400)),
            new InstallerFile(@"C:\Windows\Installer\3c4d.msp", 20, Now.AddDays(-400)),
            new InstallerFile(@"C:\Windows\Installer\5e6f.MSP", 30, Now.AddDays(-400)),
            new InstallerFile(@"C:\Windows\Installer\new.msp", 40, Now.AddDays(-3)),     // too recent: kept
        };
        var referenced = new[] { @"c:\windows\installer\1A2B.msi" };
        var orphans = InstallerOrphans.Find(files, referenced, Now)!;
        Assert.Equal([@"C:\Windows\Installer\5e6f.MSP", @"C:\Windows\Installer\3c4d.msp"], orphans.Select(o => o.Path)); // biggest first
    }

    [Fact]
    public void No_references_means_cant_tell() =>
        Assert.Null(InstallerOrphans.Find([new InstallerFile(@"C:\Windows\Installer\a.msi", 1, Now.AddDays(-400))], [], Now));

    [Fact]
    public void Only_top_level_msi_msp_in_installer_folder()
    {
        var files = new[]
        {
            new InstallerFile(@"C:\Windows\Installer\$PatchCache$\x.msp", 1, Now.AddDays(-400)),
            new InstallerFile(@"C:\Windows\Installer\icon.ico", 1, Now.AddDays(-400)),
            new InstallerFile(@"D:\Other\z.msp", 1, Now.AddDays(-400)),
        };
        Assert.Empty(InstallerOrphans.Find(files, [@"C:\Windows\Installer\keep.msi"], Now, @"C:\Windows\Installer")!);
    }
}

public class DuplicateTests
{
    private static SpaceNode F(string name, long size) => SpaceNode.NewFile(name, size, DateTime.UtcNow);

    [Fact]
    public void Size_groups_only_with_two_or_more_and_above_minimum()
    {
        var root = SpaceNode.NewFolder(@"C:\");
        root.Add(F("a", 5_000_000));
        root.Add(F("b", 5_000_000));
        root.Add(F("c", 7_000_000));
        root.Add(F("d", 10));
        root.Add(F("e", 10));
        root.Recalculate();
        var groups = Duplicates.SizeGroups(root, minBytes: 1_000_000);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void Groups_sorted_by_wasted_space()
    {
        var root = SpaceNode.NewFolder(@"C:\");
        foreach (var n in new[] { "x1", "x2" }) { root.Add(F(n, 2_000_000)); }
        foreach (var n in new[] { "y1", "y2", "y3" }) { root.Add(F(n, 3_000_000)); }
        root.Recalculate();
        var g = Duplicates.SizeGroups(root, 1_000_000);
        Assert.Equal(3_000_000, g[0][0].Size); // 6 MB wasted beats 2 MB
        Assert.Equal(6_000_000, Duplicates.Wasted(g[0]));
    }
}
