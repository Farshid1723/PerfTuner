using System.Runtime.InteropServices;
using PerfTuner.Platform.Windows.Settings;

namespace PerfTuner.Platform.Windows.Cleanup;

/// <summary>A fixed, allow-listed place where Windows or apps leave disposable files.</summary>
public sealed record JunkSource(string Id, string Name, string Description, IReadOnlyList<string> Roots, TimeSpan MinAge, bool NeedsAdmin);

/// <summary>
/// The junk locations PerfTuner may clean. Roots are built only from well-known folders —
/// never from user input — and cleaning reuses <see cref="TempCleaner"/>: old files only,
/// no reparse points, no system files, files in use are skipped.
/// </summary>
public static class JunkSources
{
    public static IReadOnlyList<JunkSource> All()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return
        [
            new("temp", "Temporary files", "Files apps left in your temp folder more than 2 days ago.",
                [Path.GetTempPath()], TimeSpan.FromDays(2), false),
            new("windows-temp", "Windows temporary files", "Old files in the Windows temp folder (installers, updates).",
                [Path.Combine(windows, "Temp")], TimeSpan.FromDays(2), true),
            new("crash-dumps", "App crash dumps", "Memory dumps written when apps crashed. Only useful to developers.",
                [Path.Combine(local, "CrashDumps")], TimeSpan.FromDays(1), false),
            new("error-reports", "Windows error reports", "Problem reports already sent (or queued) to Microsoft.",
                [Path.Combine(local, "Microsoft", "Windows", "WER"), Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                 Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue")], TimeSpan.FromDays(1), true),
        ];
    }

    public static TempScan Scan(JunkSource source) => TempCleaner.Scan(source.Roots, source.MinAge);

    public static (int Deleted, long Freed, int Skipped) Clean(JunkSource source) => TempCleaner.Clean(source.Roots, source.MinAge);
}

/// <summary>The Recycle Bin, through the documented shell API.</summary>
public static partial class RecycleBin
{
    public static (long Bytes, long Items) Query()
    {
        var info = new ShQueryRbInfo { CbSize = (uint)Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? (info.Size, info.NumItems) : (0, 0);
    }

    /// <summary>Empties all Recycle Bins without the shell's own confirmation (the GUI already asked).</summary>
    public static bool Empty() => SHEmptyRecycleBinW(0, null, 0x1 | 0x2 | 0x4) is 0 or unchecked((int)0x8000FFFF); // S_OK, or E_UNEXPECTED when already empty

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public uint CbSize;
        public long Size;
        public long NumItems;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBinW(string? rootPath, ref ShQueryRbInfo info);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBinW(nint hwnd, string? rootPath, uint flags);
}
