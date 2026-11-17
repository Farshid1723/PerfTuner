using System.Runtime.InteropServices;

namespace PerfTuner.Platform.Windows.Native;

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct PdhFmtCounterValue
{
    [FieldOffset(0)] public uint CStatus;
    [FieldOffset(8)] public double DoubleValue;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PdhFmtCounterValueItem
{
    public nint Name;
    public PdhFmtCounterValue Value;
}

/// <summary>Performance Data Helper — used ONLY as a fallback when ETW is unavailable.</summary>
internal static partial class Pdh
{
    public const uint FmtDouble = 0x00000200;
    public const uint FmtNoCap100 = 0x00008000;
    public const uint ErrorSuccess = 0;
    public const uint MoreData = 0x800007D2;
    public const uint CstatusValidData = 0x00000000;
    public const uint CstatusNewData = 0x00000001;

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW")]
    public static partial uint OpenQuery(nint dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint AddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhFmtCounterValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial uint GetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(nint query);
}
