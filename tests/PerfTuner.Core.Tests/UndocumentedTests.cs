using PerfTuner.Core.Undocumented;

namespace PerfTuner.Core.Tests;

public sealed class UndocumentedTests
{
    [Fact]
    public void Ids_are_unique_and_every_item_is_complete()
    {
        var all = UndocumentedCatalog.All;
        Assert.Equal(all.Count, all.Select(i => i.Id).Distinct().Count());
        Assert.All(all, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Title), i.Id);
            Assert.False(string.IsNullOrWhiteSpace(i.Description), i.Id);
            Assert.False(string.IsNullOrWhiteSpace(i.TakesEffect), i.Id);
            Assert.True(i.MinBuild < i.MaxBuild, i.Id);
            if (i.MaxBuild != int.MaxValue || i.MinBuild > 0) { Assert.False(string.IsNullOrWhiteSpace(i.BuildNote), i.Id); }
        });
    }

    [Fact]
    public void Each_kind_carries_exactly_the_data_it_needs()
    {
        Assert.All(UndocumentedCatalog.All, i =>
        {
            switch (i.Kind)
            {
                case UndocKind.Registry or UndocKind.PerAdapterRegistry:
                    Assert.NotEmpty(i.Values);
                    Assert.Empty(i.Keys); Assert.Empty(i.Boot); Assert.Empty(i.Services);
                    break;
                case UndocKind.RemoveKey:
                    Assert.NotEmpty(i.Keys);
                    break;
                case UndocKind.Boot:
                    Assert.NotEmpty(i.Boot);
                    Assert.True(i.RequiresAdmin, i.Id);
                    break;
                case UndocKind.Services:
                    Assert.NotEmpty(i.Services);
                    Assert.True(i.RequiresAdmin, i.Id);
                    break;
                default:
                    Assert.Empty(i.Values); Assert.Empty(i.Keys); Assert.Empty(i.Boot); Assert.Empty(i.Services);
                    break;
            }
        });
    }

    [Fact]
    public void Machine_wide_changes_need_full_power()
    {
        Assert.All(UndocumentedCatalog.All.Where(i => i.Values.Any(v => v.Machine) || i.Keys.Any(k => k.Machine)),
            i => Assert.True(i.RequiresAdmin, i.Id));
    }

    [Fact]
    public void Values_match_their_declared_types()
    {
        Assert.All(UndocumentedCatalog.All.SelectMany(i => i.Values), v =>
        {
            switch (v.Type)
            {
                case UndocValueType.DWord: Assert.IsType<int>(v.Data); break;
                case UndocValueType.Binary: Assert.IsType<byte[]>(v.Data); break;
                default: Assert.IsType<string>(v.Data); break;
            }
        });
    }

    [Fact]
    public void Dangerous_items_are_the_risky_ones_and_actions_cannot_be_undone()
    {
        Assert.True(UndocumentedCatalog.Find("cpu-mitigations-off")!.Dangerous);
        Assert.Equal(Verdict.SecurityRisk, UndocumentedCatalog.Find("cpu-mitigations-off")!.Verdict);
        Assert.True(UndocumentedCatalog.Find("mmcss-off")!.Dangerous);
        Assert.False(UndocumentedCatalog.Find("classic-context-menu")!.Dangerous);
        Assert.False(UndocumentedCatalog.Find("clear-prefetch")!.Undoable);
        Assert.True(UndocumentedCatalog.Find("no-3d-objects")!.Undoable);
    }

    [Fact]
    public void Build_ranges_hide_items_on_the_wrong_Windows()
    {
        var taskbar = UndocumentedCatalog.Find("small-taskbar")!;
        Assert.True(taskbar.AppliesTo(22000));
        Assert.False(taskbar.AppliesTo(22621));
        Assert.False(taskbar.AppliesTo(19045));
        var objects3d = UndocumentedCatalog.Find("no-3d-objects")!;
        Assert.True(objects3d.AppliesTo(19045));
        Assert.False(objects3d.AppliesTo(26100));
        Assert.True(UndocumentedCatalog.Find("classic-alt-tab")!.AppliesTo(26100));
    }

    [Fact]
    public void Undo_records_only_restore_catalog_values()
    {
        Assert.True(UndocumentedCatalog.MayRestoreValue(false, @"Software\Microsoft\Windows\CurrentVersion\Explorer", "AltTabSettings"));
        Assert.True(UndocumentedCatalog.MayRestoreValue(true, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "featuresettingsoverride"));
        Assert.False(UndocumentedCatalog.MayRestoreValue(false, @"Software\Microsoft\Windows\CurrentVersion\Explorer", "AltTabSettings2"));
        Assert.False(UndocumentedCatalog.MayRestoreValue(true, @"Software\Microsoft\Windows\CurrentVersion\Explorer", "AltTabSettings"));
        Assert.False(UndocumentedCatalog.MayRestoreValue(true, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Evil"));
    }

    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{12345678-90ab-cdef-1234-567890abcdef}", true)]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{12345678-90ab-cdef-1234-567890abcdef}\sub", false)]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\..\..\..\Control", false)]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", false)]
    public void Adapter_paths_are_strict(string path, bool expected)
    {
        Assert.Equal(expected, UndocumentedCatalog.IsAdapterPath(path));
        Assert.Equal(expected, UndocumentedCatalog.MayRestoreValue(true, path, "TcpAckFrequency"));
    }

    [Fact]
    public void Keys_boot_and_services_are_allow_listed()
    {
        Assert.True(UndocumentedCatalog.MayRestoreKey(true, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}"));
        Assert.True(UndocumentedCatalog.MayRestoreKey(false, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"));
        Assert.False(UndocumentedCatalog.MayRestoreKey(true, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"));

        Assert.True(UndocumentedCatalog.MayRestoreBoot("disabledynamictick", null));
        Assert.True(UndocumentedCatalog.MayRestoreBoot("bootmenupolicy", "standard"));
        Assert.False(UndocumentedCatalog.MayRestoreBoot("bootmenupolicy", "standard & calc"));
        Assert.False(UndocumentedCatalog.MayRestoreBoot("testsigning", "yes"));
        Assert.False(UndocumentedCatalog.MayRestoreBoot("nointegritychecks", "yes"));

        Assert.True(UndocumentedCatalog.MayRestoreService("DiagTrack", 2));
        Assert.True(UndocumentedCatalog.MayRestoreService("WSearch", 2));
        Assert.False(UndocumentedCatalog.MayRestoreService("WinDefend", 4));
        Assert.False(UndocumentedCatalog.MayRestoreService("DiagTrack", 1));
    }

    [Theory]
    [InlineData(@"Software\Classes\CLSID\{x}\InprocServer32", @"Software\Classes\CLSID\{x}", true)]
    [InlineData(@"Software\Classes\CLSID\{x}\InprocServer32", @"Software\Classes\CLSID\{x}\InprocServer32", true)]
    [InlineData(@"Software\Classes\CLSID\{x}\InprocServer32", @"Software\Classes", false)]
    [InlineData(@"Software\Classes\CLSID\{x}\InprocServer32", @"Software\Classes\CLSID\{y}", false)]
    [InlineData(@"Software\Classes\CLSID\{xy}", @"Software\Classes\CLSID\{x", false)]
    public void Created_key_roots_stay_within_the_value_path(string path, string root, bool expected) =>
        Assert.Equal(expected, UndocumentedCatalog.IsCreatedRoot(path, root));

    [Theory]
    [InlineData("Standard", "standard")]
    [InlineData(" Legacy ", "legacy")]
    [InlineData("Ja", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Boot_words_are_normalized_or_refused(string? shown, string? expected) =>
        Assert.Equal(expected, UndocumentedCatalog.BootWord(shown));

    // ------------------------------------------------------------------ virtual desktops

    private static byte[] Blob(params Guid[] ids) => [.. ids.SelectMany(g => g.ToByteArray())];

    [Fact]
    public void Desktop_ids_round_trip_from_the_registry_layout()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var ids = VirtualDesktopState.ParseIds(Blob(a, b, c));
        Assert.Equal([a, b, c], ids);
        Assert.Equal(1, VirtualDesktopState.IndexOf(ids, b));
        Assert.Equal(-1, VirtualDesktopState.IndexOf(ids, Guid.NewGuid()));
        Assert.Equal(b, VirtualDesktopState.ParseId(b.ToByteArray()));
    }

    [Fact]
    public void Real_registry_bytes_decode_to_explorer_ids()
    {
        // As read from a Windows 11 PC: VirtualDesktopIDs with two desktops.
        var blob = Convert.FromHexString("6B98E1BA80E70E4B87ADDE3E5F14AAB72C426BE545A90748B77EA7E2D327006C");
        var ids = VirtualDesktopState.ParseIds(blob);
        Assert.Equal(new Guid("BAE1986B-E780-4B0E-87AD-DE3E5F14AAB7"), ids[0]);
        Assert.Equal(new Guid("E56B422C-A945-4807-B77E-A7E2D327006C"), ids[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Malformed_desktop_lists_are_empty(byte[]? blob) => Assert.Empty(VirtualDesktopState.ParseIds(blob));

    [Theory]
    [InlineData(0, 2, 3, 2)]
    [InlineData(2, 0, 3, -2)]
    [InlineData(1, 1, 3, 0)]
    [InlineData(0, 5, 3, 0)]
    [InlineData(-1, 1, 3, 0)]
    public void Steps_to_a_desktop(int from, int to, int count, int expected) =>
        Assert.Equal(expected, VirtualDesktopState.Steps(from, to, count));

    [Fact]
    public void History_remembers_the_previous_desktop_and_forgets_removed_ones()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var h = new DesktopHistory();
        h.Visit(a);
        Assert.Null(h.Previous);
        h.Visit(a);
        h.Visit(b);
        Assert.Equal(a, h.Previous);
        h.Visit(a);
        Assert.Equal(b, h.Previous);
        h.Prune([a]);
        Assert.Null(h.Previous);
    }

    [Fact]
    public void Unnamed_desktops_get_a_number() =>
        Assert.Equal("Desktop 3", VirtualDesktopState.DisplayName(2, "  "));
}
