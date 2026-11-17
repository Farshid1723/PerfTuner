using PerfTuner.Core.Space;

namespace PerfTuner.Core.Tests;

public class SpaceNodeTests
{
    private static SpaceNode File(string name, long size, int ageDays = 0) =>
        SpaceNode.NewFile(name, size, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-ageDays));

    [Fact]
    public void Totals_and_paths()
    {
        var root = SpaceNode.NewFolder(@"C:\");
        var users = SpaceNode.NewFolder("Users");
        var a = SpaceNode.NewFolder("a");
        a.Add(File("x.mp4", 300));
        a.Add(File("y.txt", 100));
        users.Add(a);
        root.Add(users);
        root.Add(File("z.bin", 50));
        root.Recalculate();
        Assert.Equal(450, root.Size);
        Assert.Equal(3, root.Files);
        Assert.Equal(400, users.Size);
        Assert.Equal(@"C:\Users\a\x.mp4", a.Children![0].FullPath);
        Assert.Equal(@"C:\Users", users.FullPath);
    }

    [Fact]
    public void Remove_updates_ancestors()
    {
        var root = SpaceNode.NewFolder(@"C:\");
        var f = SpaceNode.NewFolder("f");
        var big = File("big", 1000);
        f.Add(big);
        f.Add(File("small", 10));
        root.Add(f);
        root.Recalculate();
        big.Remove();
        Assert.Equal(10, f.Size);
        Assert.Equal(10, root.Size);
        Assert.Equal(1, root.Files);
        Assert.Null(big.Parent);
    }

    [Fact]
    public void Replace_child_updates_totals()
    {
        var root = SpaceNode.NewFolder(@"C:\");
        var old = SpaceNode.NewFolder("data");
        old.Add(File("a", 100));
        root.Add(old);
        root.Recalculate();
        var fresh = SpaceNode.NewFolder("data");
        fresh.Add(File("a", 100));
        fresh.Add(File("b", 900));
        fresh.Recalculate();
        root.ReplaceChild(old, fresh);
        Assert.Equal(1000, root.Size);
        Assert.Same(root, fresh.Parent);
    }

    [Fact]
    public void Children_sorted_by_size()
    {
        var root = SpaceNode.NewFolder(@"D:\");
        root.Add(File("s", 1));
        root.Add(File("l", 100));
        root.Add(File("m", 10));
        root.Recalculate();
        Assert.Equal(["l", "m", "s"], root.Children!.Select(c => c.Name));
    }
}

public class ProtectionRulesTests
{
    private static readonly ProtectionContext Ctx = new(
        WindowsDir: @"C:\Windows",
        ProgramFiles: [@"C:\Program Files", @"C:\Program Files (x86)"],
        ProgramData: @"C:\ProgramData",
        UsersRoot: @"C:\Users",
        UserProfile: @"C:\Users\Farshid",
        ExtraLocked: [@"E:\Apps\PerfTuner"],
        OtherProfiles: [@"D:\Profiles\Alice"]);

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Windows", true)]
    [InlineData(@"C:\windows\System32\drivers", true)]
    [InlineData(@"C:\Program Files\App", true)]
    [InlineData(@"C:\Program Files (x86)", true)]
    [InlineData(@"C:\ProgramData\Thing", true)]
    [InlineData(@"C:\System Volume Information", true)]
    [InlineData(@"C:\$Recycle.Bin\S-1-5", true)]
    [InlineData(@"C:\Recovery", true)]
    [InlineData(@"C:\Boot", true)]
    [InlineData(@"C:\Users", true)]
    [InlineData(@"C:\Users\Bob\Videos", true)]
    [InlineData(@"C:\Users\Public\Documents", true)]
    [InlineData(@"C:\Users\Default", true)]
    [InlineData(@"C:\Users\Farshid", true)]
    [InlineData(@"C:\Users\Farshid\AppData", true)]
    [InlineData(@"C:\Users\Farshid\AppData\Local", true)]
    [InlineData(@"C:\Users\Farshid\Documents", true)]
    [InlineData(@"E:\Apps\PerfTuner\PerfTuner.exe", false)]
    [InlineData(@"C:\Users\Farshid\..\Bob", true)]
    [InlineData(@"C:\PROGRA~1\App", true)]
    public void Locked(string path, bool isFolder) =>
        Assert.Equal(ProtectionLevel.Locked, ProtectionRules.Check(path, isFolder, isSystem: false, Ctx).Level);

    [Theory]
    [InlineData(@"C:\Users\Farshid\Documents.", true)]          // Windows would act on "Documents"
    [InlineData(@"C:\Program Files.", true)]
    [InlineData(@"C:\Users\Farshid\Videos\clip.mp4 ", false)]
    [InlineData(@"C:\Users\Farshid\Videos\CON", false)]
    [InlineData(@"C:\Users\Farshid\Videos\nul.txt", false)]
    [InlineData(@"\\?\C:\Windows", true)]
    [InlineData(@"\\server\share\x", true)]
    [InlineData(@"D:\Users\Someone", true)]                      // another installation's profiles
    [InlineData(@"D:\Profiles\Alice\Videos\a.mkv", false)]      // another account's profile elsewhere
    [InlineData(@"D:\Program Files\App", true)]
    [InlineData(@"D:\WindowsApps\Game", true)]
    [InlineData(@"C:\Users\Farshid\AppData\Roaming\Microsoft\Protect", true)]
    [InlineData(@"C:\Users\Farshid\AppData\Local\Packages\App", true)]
    public void Tricky_paths_locked(string path, bool isFolder) =>
        Assert.Equal(ProtectionLevel.Locked, ProtectionRules.Check(path, isFolder, false, Ctx).Level);

    [Theory]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\hiberfil.sys")]
    [InlineData(@"D:\swapfile.sys")]
    [InlineData(@"C:\DumpStack.log.tmp")]
    public void System_files_locked(string path) =>
        Assert.Equal(ProtectionLevel.Locked, ProtectionRules.Check(path, false, false, Ctx).Level);

    [Fact]
    public void System_attribute_locked() =>
        Assert.Equal(ProtectionLevel.Locked, ProtectionRules.Check(@"D:\x.dat", false, isSystem: true, Ctx).Level);

    [Theory]
    [InlineData(@"C:\Users\Farshid\AppData\Local\HugeCache", true)]
    [InlineData(@"C:\Users\Farshid\AppData\Roaming\App\x.log", false)]
    public void AppData_children_warn(string path, bool isFolder) =>
        Assert.Equal(ProtectionLevel.Warn, ProtectionRules.Check(path, isFolder, false, Ctx).Level);

    [Theory]
    [InlineData(@"C:\Users\Farshid\Videos\movie.mkv", false)]
    [InlineData(@"C:\Users\farshid\Downloads\old", true)]
    [InlineData(@"D:\Games\Big", true)]
    [InlineData(@"C:\Temp\x.iso", false)]
    [InlineData(@"C:\Users\Farshid\Documents\Old Project\", true)]
    public void Allowed(string path, bool isFolder) =>
        Assert.Equal(ProtectionLevel.Allowed, ProtectionRules.Check(path, isFolder, false, Ctx).Level);

    [Fact]
    public void Reasons_are_plain() =>
        Assert.Contains("another user", ProtectionRules.Check(@"C:\Users\Bob", true, false, Ctx).Reason, StringComparison.Ordinal);

    [Theory]
    [InlineData(@"C:\Users\Farshid\Videos", @"C:\Users\Farshid\Videos\Sub", true)]
    [InlineData(@"C:\Users\Farshid\Videos", @"C:\Users\Farshid\Videos", true)]
    [InlineData(@"C:\Users\Farshid\Videos", @"C:\Users\Farshid\VideosOld", false)]
    public void Inside(string parent, string path, bool expected) => Assert.Equal(expected, ProtectionRules.IsInside(path, parent));
}

public class SpaceReportsTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
    private const long MB = 1L << 20;

    private static SpaceNode Tree()
    {
        var root = SpaceNode.NewFolder(@"C:\");
        var videos = SpaceNode.NewFolder("Videos");
        videos.Add(SpaceNode.NewFile("old.mkv", 900 * MB, Now.AddDays(-800)));
        videos.Add(SpaceNode.NewFile("new.mp4", 500 * MB, Now.AddDays(-2)));
        var docs = SpaceNode.NewFolder("Docs");
        docs.Add(SpaceNode.NewFile("ancient.txt", 1, Now.AddDays(-3000)));
        docs.Add(SpaceNode.NewFile("setup.iso", 4000 * MB, Now.AddDays(-400)));
        docs.Add(SpaceNode.NewFile("small-old.zip", 50 * MB, Now.AddDays(-500)));
        root.Add(videos);
        root.Add(docs);
        root.Recalculate();
        return root;
    }

    [Fact]
    public void Biggest_files() => Assert.Equal(["setup.iso", "old.mkv", "new.mp4"], SpaceReports.BiggestFiles(Tree(), 3).Select(n => n.Name));

    [Fact]
    public void Biggest_folders_exclude_root() => Assert.Equal(["Docs", "Videos"], SpaceReports.BiggestFolders(Tree(), 5).Select(n => n.Name));

    [Fact]
    public void Oldest() => Assert.Equal("ancient.txt", SpaceReports.Oldest(Tree(), 1).Single().Name);

    [Fact]
    public void Old_and_big_is_year_and_100mb_by_size() =>
        Assert.Equal(["setup.iso", "old.mkv"], SpaceReports.OldAndBig(Tree(), Now, 10).Select(n => n.Name));

    [Fact]
    public void Of_category() => Assert.Equal(["old.mkv", "new.mp4"], SpaceReports.OfCategory(Tree(), FileCategory.Video, 10).Select(n => n.Name));

    [Fact]
    public void Summary_totals_per_category()
    {
        var s = SpaceReports.Summary(Tree());
        Assert.Equal(1400 * MB, s.Single(x => x.Category == FileCategory.Video).Bytes);
        Assert.Equal(4000 * MB, s.Single(x => x.Category == FileCategory.DiskImages).Bytes);
        Assert.Equal(FileCategory.DiskImages, s[0].Category); // sorted biggest first
    }

    [Theory]
    [InlineData("a.MKV", FileCategory.Video)]
    [InlineData("b.flac", FileCategory.Audio)]
    [InlineData("c.vhdx", FileCategory.DiskImages)]
    [InlineData("d.7z", FileCategory.Archives)]
    [InlineData("e.msi", FileCategory.Installers)]
    [InlineData("f.docx", FileCategory.Documents)]
    [InlineData("g.jpg", FileCategory.Images)]
    [InlineData("h.pdb", FileCategory.CodeAndBuild)]
    [InlineData("i.log", FileCategory.DataAndLogs)]
    [InlineData("noext", FileCategory.Other)]
    public void Categories(string name, FileCategory expected) => Assert.Equal(expected, FileCategories.Of(name));
}

public class LayoutTests
{
    [Fact]
    public void Treemap_areas_proportional_and_inside()
    {
        double[] sizes = [60, 25, 10, 5];
        var bounds = new RectD(0, 0, 200, 100);
        var rects = TreemapLayout.Squarify(sizes, bounds);
        Assert.Equal(4, rects.Length);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(sizes[i] / 100 * 20000, rects[i].W * rects[i].H, 3);
            Assert.True(rects[i].X >= -1e-9 && rects[i].Y >= -1e-9 && rects[i].X + rects[i].W <= 200 + 1e-6 && rects[i].Y + rects[i].H <= 100 + 1e-6);
        }
        for (var i = 0; i < 4; i++)
        {
            for (var j = i + 1; j < 4; j++)
            {
                var ox = Math.Min(rects[i].X + rects[i].W, rects[j].X + rects[j].W) - Math.Max(rects[i].X, rects[j].X);
                var oy = Math.Min(rects[i].Y + rects[i].H, rects[j].Y + rects[j].H) - Math.Max(rects[i].Y, rects[j].Y);
                Assert.False(ox > 1e-6 && oy > 1e-6, $"{i} overlaps {j}");
            }
        }
    }

    [Fact]
    public void Treemap_handles_zero_and_empty()
    {
        Assert.Empty(TreemapLayout.Squarify([], new RectD(0, 0, 10, 10)));
        var r = TreemapLayout.Squarify([0, 5], new RectD(0, 0, 10, 10));
        Assert.Equal(0, r[0].W * r[0].H);
        Assert.Equal(100, r[1].W * r[1].H, 6);
    }

    [Fact]
    public void Sunburst_sums_to_360_and_merges_small()
    {
        double[] sizes = [700, 299, 0.5, 0.5];
        var arcs = SunburstLayout.Arcs(sizes, minDegrees: 0.5);
        Assert.Equal(360, arcs.Sum(a => a.Sweep), 6);
        Assert.Contains(arcs, a => a.Index == -1); // merged "smaller items"
        Assert.Equal(3, arcs.Length);
        Assert.Equal(0, arcs[0].Start);
        Assert.Equal(arcs[0].Sweep, arcs[1].Start, 9);
    }
}
