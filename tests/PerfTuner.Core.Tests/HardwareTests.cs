using PerfTuner.Core.Hardware;

namespace PerfTuner.Core.Tests;

public class ThemeCatalogTests
{
    [Fact]
    public void Ratio_white_black_is_21() => Assert.Equal(21.0, ColorContrast.Ratio("#FFFFFF", "#000000"), 2);

    [Fact]
    public void Classic_has_six_unchanged()
    {
        var classic = ThemeCatalog.All.Where(t => t.Family == ThemeFamily.Classic).ToList();
        Assert.Equal(["lagoon", "ember", "violet", "paper", "mint", "sunset"], classic.Select(t => t.Id));
        var lagoon = classic[0];
        Assert.Equal("#0F1419", lagoon.Background);
        Assert.Equal("#2BC4B4", lagoon.Accent);
        Assert.Equal("#E5533D", classic[5].Accent);
    }

    [Fact]
    public void Metal_has_at_least_20_with_dark_and_light()
    {
        var metal = ThemeCatalog.All.Where(t => t.Family == ThemeFamily.Metal).ToList();
        Assert.True(metal.Count >= 20, $"only {metal.Count} metal palettes");
        Assert.Contains(metal, t => t.Dark);
        Assert.Contains(metal, t => !t.Dark);
    }

    [Fact]
    public void Ids_unique() => Assert.Equal(ThemeCatalog.All.Count, ThemeCatalog.All.Select(t => t.Id).Distinct().Count());

    public static TheoryData<string> MetalIds()
    {
        var data = new TheoryData<string>();
        foreach (var t in ThemeCatalog.All.Where(t => t.Family == ThemeFamily.Metal)) { data.Add(t.Id); }
        return data;
    }

    [Theory]
    [MemberData(nameof(MetalIds))]
    public void Metal_palettes_meet_contrast(string id)
    {
        var t = ThemeCatalog.All.Single(p => p.Id == id);
        Assert.True(ColorContrast.Ratio(t.Text, t.Surface) >= 7, $"{id} text/surface {ColorContrast.Ratio(t.Text, t.Surface):F2}");
        Assert.True(ColorContrast.Ratio(t.Text, t.Background) >= 7, $"{id} text/background {ColorContrast.Ratio(t.Text, t.Background):F2}");
        Assert.True(ColorContrast.Ratio(t.Subtle, t.Surface) >= 4.5, $"{id} subtle/surface {ColorContrast.Ratio(t.Subtle, t.Surface):F2}");
        Assert.True(ColorContrast.Ratio(t.OnAccent, t.Accent) >= 4.5, $"{id} onAccent/accent {ColorContrast.Ratio(t.OnAccent, t.Accent):F2}");
    }
}

public class ResourceStatusTests
{
    [Theory]
    [InlineData(30, false, LoadState.Calm)]
    [InlineData(60, false, LoadState.Busy)]
    [InlineData(89.9, false, LoadState.Busy)]
    [InlineData(90, false, LoadState.Strained)]
    [InlineData(10, true, LoadState.Strained)]
    public void Cpu(double pct, bool throttled, LoadState expected) => Assert.Equal(expected, ResourceStatus.Cpu(pct, throttled));

    [Theory]
    [InlineData(50, 40, LoadState.Calm)]
    [InlineData(29, 40, LoadState.Busy)]
    [InlineData(14, 40, LoadState.Strained)]
    [InlineData(50, 91, LoadState.Strained)]
    public void Memory(double availPct, double commitPct, LoadState expected) => Assert.Equal(expected, ResourceStatus.Memory(availPct, commitPct));

    [Theory]
    [InlineData(10e6, 1e6, 20e6, LoadState.Calm)]    // 10× median but below the floor
    [InlineData(30e6, 5e6, 20e6, LoadState.Busy)]    // > 4× median and > floor
    [InlineData(30e6, 10e6, 20e6, LoadState.Calm)]   // only 3× median
    [InlineData(0, 0, 20e6, LoadState.Calm)]
    public void Throughput(double bps, double median, double floor, LoadState expected) => Assert.Equal(expected, ResourceStatus.Throughput(bps, median, floor));
}

public class PageFileAdvisorTests
{
    private const long GB = 1L << 30;

    private static PageFileInputs Inputs(
        long ram = 16 * GB, long peak = 12 * GB, long limit = 24 * GB, bool auto = true, long customMax = 0,
        bool hasPageFile = true, bool onlyOnHdd = false, bool ssdWithRoom = false, long freeOnDrive = 200 * GB, int dumpType = 7)
        => new(ram, peak, limit, auto, customMax, hasPageFile, onlyOnHdd, ssdWithRoom, freeOnDrive, dumpType);

    [Fact]
    public void Keep_when_system_managed_and_peak_low() => Assert.Equal(PageFileVerdict.KeepAsIs, PageFileAdvisor.Advise(Inputs()).Verdict);

    [Fact]
    public void No_page_file_wins_over_everything()
        => Assert.Equal(PageFileVerdict.NoPageFile, PageFileAdvisor.Advise(Inputs(hasPageFile: false, onlyOnHdd: true, ssdWithRoom: true, auto: false)).Verdict);

    [Fact]
    public void Custom_near_limit_switches_to_system_managed_before_ssd_advice()
        => Assert.Equal(PageFileVerdict.SwitchToSystemManaged,
            PageFileAdvisor.Advise(Inputs(auto: false, customMax: 8 * GB, peak: 20 * GB, limit: 24 * GB, onlyOnHdd: true, ssdWithRoom: true)).Verdict);

    [Fact]
    public void Custom_smaller_than_crash_dump_minimum_switches()
        => Assert.Equal(PageFileVerdict.SwitchToSystemManaged,
            PageFileAdvisor.Advise(Inputs(auto: false, customMax: 1 * GB, peak: 4 * GB)).Verdict);

    [Fact]
    public void Hdd_only_with_roomy_ssd_adds_on_ssd()
        => Assert.Equal(PageFileVerdict.AddOnSsd, PageFileAdvisor.Advise(Inputs(onlyOnHdd: true, ssdWithRoom: true)).Verdict);

    [Theory]
    [InlineData(0, 1)]        // none
    [InlineData(3, 1)]        // small
    public void Crash_dump_minimum_small(int dumpType, long expectedGb)
        => Assert.Equal(expectedGb * GB, PageFileAdvisor.CrashDumpMinimum(16 * GB, dumpType));

    [Fact]
    public void Crash_dump_minimum_kernel_is_third_of_ram_at_least_2gb()
    {
        Assert.Equal(48 * GB / 3, PageFileAdvisor.CrashDumpMinimum(48 * GB, 2));
        Assert.Equal(2 * GB, PageFileAdvisor.CrashDumpMinimum(4 * GB, 7));
    }

    [Fact]
    public void Crash_dump_minimum_complete_is_ram_plus_257mb()
        => Assert.Equal(16 * GB + 257L * 1024 * 1024, PageFileAdvisor.CrashDumpMinimum(16 * GB, 1));

    [Fact]
    public void Custom_range_is_clamped()
    {
        var a = PageFileAdvisor.Advise(Inputs(ram: 16 * GB, peak: 20 * GB, freeOnDrive: 400 * GB, dumpType: 7));
        Assert.Equal(16 * GB / 3, a.CustomMinBytes);
        Assert.Equal(48 * GB, a.CustomMaxBytes);                 // min(3×RAM, 100 GB)
        Assert.Equal(16 * GB / 3, a.CustomSuggestedBytes);       // (20−16)×1.25 = 5 GB < minimum 5.33 GB → raised to the minimum
        var b = PageFileAdvisor.Advise(Inputs(ram: 16 * GB, peak: 26 * GB, limit: 40 * GB, freeOnDrive: 400 * GB, dumpType: 7));
        Assert.Equal((long)(10 * GB * 1.25), b.CustomSuggestedBytes); // (26−16)×1.25 = 12.5 GB
    }

    [Fact]
    public void Custom_hidden_when_drive_nearly_full()
    {
        var a = PageFileAdvisor.Advise(Inputs(freeOnDrive: 4 * GB, dumpType: 7)); // max = 1 GB < min
        Assert.Null(a.CustomMinBytes);
        Assert.Null(a.CustomMaxBytes);
        Assert.Null(a.CustomSuggestedBytes);
    }
}
