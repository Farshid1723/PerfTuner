using System.Runtime.InteropServices;
using Microsoft.Win32;
using PerfTuner.Platform.Windows.Settings;

namespace PerfTuner.App.SystemTools;

/// <summary>Usage of one page file (MB), from Win32_PageFileUsage.</summary>
public sealed record PageFileUsageInfo(string Letter, long AllocatedMb, long CurrentMb, long PeakMb)
{
    public double UsedFraction => AllocatedMb > 0 ? Math.Clamp((double)CurrentMb / AllocatedMb, 0, 1) : 0;
    public double PeakFraction => AllocatedMb > 0 ? Math.Clamp((double)PeakMb / AllocatedMb, 0, 1) : 0;
    public string Text => $"{Letter}:  {CurrentMb:N0} MB used of {AllocatedMb:N0} MB · peak {PeakMb:N0} MB";
}

/// <summary>Everything the Memory view shows, in bytes.</summary>
public sealed record MemorySnapshot(
    long PhysicalTotal,
    long PhysicalAvailable,
    long SystemCache,
    long CommitTotal,
    long CommitLimit,
    long CommitPeak,
    PageFileConfig Config,
    IReadOnlyList<PageFileUsageInfo> Usage,
    int CrashDumpType)
{
    public long InUse => PhysicalTotal - PhysicalAvailable;
    public double InUseFraction => PhysicalTotal > 0 ? (double)InUse / PhysicalTotal : 0;
    public double CacheFraction => PhysicalTotal > 0 ? Math.Min((double)SystemCache / PhysicalTotal, 1 - InUseFraction) : 0;
    public double CommitFraction => CommitLimit > 0 ? Math.Clamp((double)CommitTotal / CommitLimit, 0, 1) : 0;
    public double CommitPeakFraction => CommitLimit > 0 ? Math.Clamp((double)CommitPeak / CommitLimit, 0, 1) : 0;
}

/// <summary>
/// Read-only memory and page-file information: GetPerformanceInfo (documented, psapi), Win32_PageFileUsage, the
/// page-file configuration (<see cref="PageFile.Read"/>) and the configured crash-dump type (registry, read-only).
/// </summary>
public static partial class MemoryInspector
{
    public static MemorySnapshot Read()
    {
        var info = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!GetPerformanceInfo(ref info, info.Size))
        {
            throw new InvalidOperationException("GetPerformanceInfo failed");
        }
        var page = (long)info.PageSize;
        PageFileConfig config;
        try { config = PageFile.Read(); }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { config = new PageFileConfig(true, []); }
        return new MemorySnapshot(
            (long)info.PhysicalTotal * page,
            (long)info.PhysicalAvailable * page,
            (long)info.SystemCache * page,
            (long)info.CommitTotal * page,
            (long)info.CommitLimit * page,
            (long)info.CommitPeak * page,
            config,
            ReadUsage(),
            CrashDumpType());
    }

    private static List<PageFileUsageInfo> ReadUsage()
    {
        var result = new List<PageFileUsageInfo>();
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");
            foreach (System.Management.ManagementObject o in s.Get())
            {
                using (o)
                {
                    var name = o["Name"] as string ?? "";
                    if (name.Length < 2 || name[1] != ':') { continue; }
                    static long L(object? v) => Convert.ToInt64(v ?? 0u, System.Globalization.CultureInfo.InvariantCulture);
                    result.Add(new PageFileUsageInfo(char.ToUpperInvariant(name[0]).ToString(), L(o["AllocatedBaseSize"]), L(o["CurrentUsage"]), L(o["PeakUsage"])));
                }
            }
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        return result;
    }

    /// <summary>HKLM\SYSTEM\CurrentControlSet\Control\CrashControl\CrashDumpEnabled (7 = automatic when absent).</summary>
    private static int CrashDumpType()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl");
        return key?.GetValue("CrashDumpEnabled") is int v ? v : 7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(ref PerformanceInformation info, uint size);
}
