namespace PerfTuner.Core.Hardware;

public enum PageFileVerdict
{
    /// <summary>System managed and enough headroom: nothing to do (most PCs).</summary>
    KeepAsIs,

    /// <summary>A custom size is too small for how this PC is used, or below the crash-dump minimum.</summary>
    SwitchToSystemManaged,

    /// <summary>No page file at all: crash dumps and some apps need one.</summary>
    NoPageFile,

    /// <summary>The page file is only on a hard disk although an SSD with room exists: add one on the SSD.</summary>
    AddOnSsd,
}

/// <summary>Everything the advice depends on. Sizes in bytes.</summary>
/// <param name="CrashDumpType">HKLM\SYSTEM\CurrentControlSet\Control\CrashControl\CrashDumpEnabled (0 none, 1 complete, 2 kernel, 3 small, 7 automatic).</param>
public sealed record PageFileInputs(
    long RamBytes,
    long PeakCommitBytes,
    long CommitLimitBytes,
    bool SystemManaged,
    long CustomMaxBytes,
    bool HasPageFile,
    bool OnlyOnHdd,
    bool SsdWithRoom,
    long FreeBytesOnDrive,
    int CrashDumpType);

/// <summary>The verdict plus the bounds for the "Advanced: custom size" option (null = option hidden).</summary>
public sealed record PageFileAdvice(
    PageFileVerdict Verdict,
    long CrashDumpMinimumBytes,
    long? CustomMinBytes,
    long? CustomMaxBytes,
    long? CustomSuggestedBytes);

/// <summary>
/// Page-file advice (design 6.4). The honest default is "let Windows manage it"; PerfTuner only recommends a change
/// when it measured a reason. Priority when several apply: NoPageFile → SwitchToSystemManaged → AddOnSsd → KeepAsIs.
/// </summary>
public static class PageFileAdvisor
{
    private const long GB = 1L << 30;
    private const long MB = 1L << 20;

    public static PageFileAdvice Advise(PageFileInputs i)
    {
        ArgumentNullException.ThrowIfNull(i);
        var dumpMin = CrashDumpMinimum(i.RamBytes, i.CrashDumpType);

        var nearLimit = i.CommitLimitBytes > 0 && i.PeakCommitBytes >= 0.8 * i.CommitLimitBytes;
        var verdict =
            !i.HasPageFile ? PageFileVerdict.NoPageFile
            : !i.SystemManaged && (nearLimit || i.CustomMaxBytes < dumpMin) ? PageFileVerdict.SwitchToSystemManaged
            : i.OnlyOnHdd && i.SsdWithRoom ? PageFileVerdict.AddOnSsd
            : PageFileVerdict.KeepAsIs;

        var min = dumpMin;
        var max = Math.Min(3 * i.RamBytes, i.FreeBytesOnDrive / 4);
        if (min > max)
        {
            return new PageFileAdvice(verdict, dumpMin, null, null, null);
        }
        var wanted = (long)((i.PeakCommitBytes - i.RamBytes) * 1.25);
        return new PageFileAdvice(verdict, dumpMin, min, max, Math.Clamp(wanted, min, max));
    }

    /// <summary>The smallest page file on the Windows drive that still lets Windows write the configured crash dump.</summary>
    public static long CrashDumpMinimum(long ramBytes, int crashDumpType) => crashDumpType switch
    {
        1 => ramBytes + 257 * MB,                    // complete memory dump
        2 or 7 => Math.Max(2 * GB, ramBytes / 3),    // kernel / automatic
        _ => 1 * GB,                                 // none, small, unknown
    };
}
