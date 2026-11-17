using PerfTuner.Core.Setup;

namespace PerfTuner.Core.Tests;

public sealed class PayloadManifestTests
{
    private static readonly string H = new('a', 64);

    [Theory]
    [InlineData("PerfTuner.exe")]
    [InlineData(@"Service\PerfTuner.Service.exe")]
    [InlineData(@"runtimes\win-x64\native\x.dll")]
    public void Normal_paths_are_safe(string p) => Assert.True(PayloadManifest.IsSafeRelativePath(p));

    [Theory]
    [InlineData("")]
    [InlineData(@"..\evil.exe")]
    [InlineData(@"Service\..\..\evil.exe")]
    [InlineData(@"\Windows\evil.exe")]
    [InlineData(@"C:\evil.exe")]
    [InlineData("a/b.exe")]
    [InlineData("file.txt:stream")]
    [InlineData(@"dir.\x")]
    [InlineData(@"dir \x")]
    [InlineData(@"a\\b")]
    [InlineData(@".\a")]
    public void Escapes_are_refused(string p) => Assert.False(PayloadManifest.IsSafeRelativePath(p));

    [Fact]
    public void Round_trip()
    {
        var entries = new[] { new PayloadEntry(@"Service\b.dll", 20, H), new PayloadEntry("A.exe", 10, H.ToUpperInvariant()) };
        var parsed = PayloadManifest.Parse(PayloadManifest.Format(entries));
        Assert.Equal(["A.exe", @"Service\b.dll"], parsed.Select(e => e.Path));
        Assert.Equal(10, parsed[0].Size);
        Assert.Equal(H, parsed[0].Sha256);
    }

    [Theory]
    [InlineData("abc 1 a.exe")]
    [InlineData("zz 1 a.exe")]
    [InlineData("")]
    public void Malformed_manifests_throw(string text) => Assert.Throws<FormatException>(() => PayloadManifest.Parse(text));

    [Fact]
    public void Unsafe_path_or_duplicate_throws()
    {
        Assert.Throws<FormatException>(() => PayloadManifest.Parse($"{H} 1 ..\\x.exe\n"));
        Assert.Throws<FormatException>(() => PayloadManifest.Parse($"{H} 1 a.exe\n{H} 2 A.EXE\n"));
        Assert.Throws<FormatException>(() => PayloadManifest.Parse($"{H} -1 a.exe\n"));
    }

    [Fact]
    public void Stale_lists_files_gone_from_the_new_release() =>
        Assert.Equal(["old.dll"], PayloadManifest.Stale(
            [new PayloadEntry("a.exe", 1, H), new PayloadEntry("old.dll", 1, H)],
            [new PayloadEntry("A.EXE", 1, H), new PayloadEntry("new.dll", 1, H)]));
}
