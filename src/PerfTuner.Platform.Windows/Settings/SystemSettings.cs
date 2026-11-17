using Microsoft.Win32;
using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows.Settings;

// ===========================================================================
// Persistent, user-visible settings. Each "Apply" returns the values it replaced so the
// caller can journal them and revert later. All APIs are documented Win32.
// ===========================================================================

public sealed record PowerPlan(Guid Id, string Name, bool Active);

/// <summary>Processor power settings of a scheme (AC = plugged in, DC = battery).</summary>
public sealed record ProcessorPower(uint BoostAc, uint BoostDc, uint MinAc, uint MinDc, uint MaxAc, uint MaxDc);

public static unsafe class PowerSettings
{
    private const uint AccessScheme = 16;
    public static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    public static readonly Guid MinProcessorState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    public static readonly Guid MaxProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

    /// <summary>Boost modes exposed to the user (value = PERFBOOSTMODE index).</summary>
    public static readonly (uint Value, string Name)[] BoostModes =
    [
        (0, "Disabled (coolest, quietest)"),
        (1, "Enabled"),
        (2, "Aggressive (default on most PCs)"),
        (3, "Efficient enabled"),
        (4, "Efficient aggressive"),
    ];

    public static Guid GetActive()
    {
        Check(SettingsApi.PowerGetActiveScheme(0, out var guid), "PowerGetActiveScheme");
        var result = *guid;
        SettingsApi.LocalFree((nint)guid);
        return result;
    }

    public static IReadOnlyList<PowerPlan> List()
    {
        var active = GetActive();
        var plans = new List<PowerPlan>();
        for (uint index = 0; index < 64; index++)
        {
            Guid scheme;
            var size = (uint)sizeof(Guid);
            if (SettingsApi.PowerEnumerate(0, null, null, AccessScheme, index, (byte*)&scheme, ref size) != 0)
            {
                break;
            }
            plans.Add(new PowerPlan(scheme, ReadName(scheme), scheme == active));
        }
        return plans;
    }

    public static void SetActive(Guid scheme) => Check(SettingsApi.PowerSetActiveScheme(0, scheme), "PowerSetActiveScheme");

    public static ProcessorPower ReadProcessor(Guid scheme)
    {
        uint Read(Guid setting, bool ac)
        {
            var rc = ac
                ? SettingsApi.PowerReadACValueIndex(0, scheme, ProcessorSubgroup, setting, out var v)
                : SettingsApi.PowerReadDCValueIndex(0, scheme, ProcessorSubgroup, setting, out v);
            return rc == 0 ? v : 0;
        }
        return new ProcessorPower(Read(BoostMode, true), Read(BoostMode, false),
            Read(MinProcessorState, true), Read(MinProcessorState, false),
            Read(MaxProcessorState, true), Read(MaxProcessorState, false));
    }

    /// <summary>Writes processor settings to a scheme and re-activates it so they take effect.
    /// Bounds are enforced here, not just in the UI: min 0–100, max 50–100, min ≤ max, boost 0–4.</summary>
    public static void WriteProcessor(Guid scheme, ProcessorPower p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.BoostAc > 4 || p.BoostDc > 4 || p.MinAc > 100 || p.MinDc > 100 || p.MaxAc is < 50 or > 100 || p.MaxDc is < 50 or > 100 || p.MinAc > p.MaxAc || p.MinDc > p.MaxDc)
        {
            throw new ArgumentOutOfRangeException(nameof(p), "processor power values outside safe bounds");
        }
        Check(SettingsApi.PowerWriteACValueIndex(0, scheme, ProcessorSubgroup, BoostMode, p.BoostAc), "boost AC");
        Check(SettingsApi.PowerWriteDCValueIndex(0, scheme, ProcessorSubgroup, BoostMode, p.BoostDc), "boost DC");
        Check(SettingsApi.PowerWriteACValueIndex(0, scheme, ProcessorSubgroup, MinProcessorState, p.MinAc), "min AC");
        Check(SettingsApi.PowerWriteDCValueIndex(0, scheme, ProcessorSubgroup, MinProcessorState, p.MinDc), "min DC");
        Check(SettingsApi.PowerWriteACValueIndex(0, scheme, ProcessorSubgroup, MaxProcessorState, p.MaxAc), "max AC");
        Check(SettingsApi.PowerWriteDCValueIndex(0, scheme, ProcessorSubgroup, MaxProcessorState, p.MaxDc), "max DC");
        if (scheme == GetActive())
        {
            SetActive(scheme);
        }
    }

    private static string ReadName(Guid scheme)
    {
        var buffer = stackalloc byte[512];
        uint size = 512;
        if (SettingsApi.PowerReadFriendlyName(0, scheme, null, null, buffer, ref size) != 0 || size < 2)
        {
            return scheme.ToString();
        }
        return new string((char*)buffer, 0, (int)(size / 2)).TrimEnd('\0');
    }

    private static void Check(uint rc, string what)
    {
        if (rc != 0)
        {
            throw new InvalidOperationException($"{what} failed (error {rc})");
        }
    }
}

/// <summary>
/// "Adjust for best performance": turns off animations, fades, shadows and smooth scrolling,
/// exactly the per-user switches behind System → Advanced → Performance Options.
/// Font smoothing is deliberately left alone (readability).
/// </summary>
public static unsafe class VisualEffects
{
    private const uint SpiGetAnimation = 0x0048, SpiSetAnimation = 0x0049;
    private const uint SpiGetDragFullWindows = 0x0026, SpiSetDragFullWindows = 0x0025;

    /// <summary>(get, set, name) for simple BOOL settings passed via pvParam.</summary>
    private static readonly (uint Get, uint Set, string Name)[] BoolSettings =
    [
        (0x1002, 0x1003, "Menu animation"),
        (0x1004, 0x1005, "Combo box animation"),
        (0x1006, 0x1007, "Smooth list scrolling"),
        (0x1014, 0x1015, "Menu fade"),
        (0x1016, 0x1017, "Tooltip animation"),
        (0x101A, 0x101B, "Cursor shadow"),
        (0x1024, 0x1025, "Window shadows"),
        (0x1042, 0x1043, "Client-area animation"),
    ];

    public const int SettingCount = 10;

    /// <summary>Current values in a fixed order (8 BOOL settings, min/max animation, drag full windows).</summary>
    public static int[] Capture()
    {
        var values = new int[SettingCount];
        for (var i = 0; i < BoolSettings.Length; i++)
        {
            int v = 0;
            SettingsApi.SystemParametersInfo(BoolSettings[i].Get, 0, (nint)(&v), 0);
            values[i] = v;
        }
        var anim = stackalloc int[2];
        anim[0] = 8; // ANIMATIONINFO.cbSize
        SettingsApi.SystemParametersInfo(SpiGetAnimation, 8, (nint)anim, 0);
        values[8] = anim[1];
        int drag = 0;
        SettingsApi.SystemParametersInfo(SpiGetDragFullWindows, 0, (nint)(&drag), 0);
        values[9] = drag;
        return values;
    }

    public static bool IsBestPerformance(int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Take(9).All(v => v == 0);
    }

    /// <summary>Best performance: everything off except full-window dragging (keeps UI usable).</summary>
    public static void ApplyBestPerformance()
    {
        var target = Capture();
        for (var i = 0; i < 9; i++) { target[i] = 0; }
        Apply(target);
    }

    public static void Apply(int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != SettingCount) { throw new ArgumentException("wrong length", nameof(values)); }
        const uint Flags = SettingsApi.SpifUpdateIniFile | SettingsApi.SpifSendChange;
        for (var i = 0; i < BoolSettings.Length; i++)
        {
            SettingsApi.SystemParametersInfo(BoolSettings[i].Set, 0, values[i] != 0 ? 1 : 0, Flags);
        }
        var anim = stackalloc int[2];
        anim[0] = 8;
        anim[1] = values[8] != 0 ? 1 : 0;
        SettingsApi.SystemParametersInfo(SpiSetAnimation, 8, (nint)anim, Flags);
        SettingsApi.SystemParametersInfo(SpiSetDragFullWindows, values[9] != 0 ? 1u : 0u, 0, Flags);
    }
}

/// <summary>Windows transparency effects (Settings → Personalization → Colors).</summary>
public static class Transparency
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static int? Get()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("EnableTransparency") as int?;
    }

    public static void Set(int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (value is null)
        {
            key.DeleteValue("EnableTransparency", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("EnableTransparency", value.Value, RegistryValueKind.DWord);
        }
    }
}
