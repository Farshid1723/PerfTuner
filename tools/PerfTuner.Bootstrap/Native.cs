using System.IO;
using System.Runtime.InteropServices;

namespace PerfTuner.Bootstrap;

internal static unsafe partial class Native
{
    private const uint MbOk = 0x0, MbIconError = 0x10, MbIconInformation = 0x40, MbSetForeground = 0x10000;
    private const uint MoveFileDelayUntilReboot = 0x4;


    public static void Message(string title, string text, bool error) =>
        _ = MessageBoxW(0, text, title, MbOk | MbSetForeground | (error ? MbIconError : MbIconInformation));

    /// <summary>Windows deletes the file or (empty) folder at the next restart (documented MoveFileEx use).</summary>
    public static void ScheduleDeleteOnRestart(string path) => _ = MoveFileExW(path, null, MoveFileDelayUntilReboot);

    /// <summary>Later LoadLibrary calls (including the runtime's own) only look in System32 (documented SetDefaultDllDirectories).</summary>
    public static void RestrictDllSearchToSystem32() => _ = SetDefaultDllDirectories(0x00000800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);

    /// <summary>
    /// True if <paramref name="file"/> is the same file this process was started from: the NT path of the open handle
    /// equals the NT path Windows reports for the mapped exe image. A file renamed away and replaced after start fails this.
    /// </summary>
    public static unsafe bool IsRunningImage(Microsoft.Win32.SafeHandles.SafeFileHandle file)
    {
        var image = GetModuleHandleW(null);
        var mapped = new char[1024];
        uint m;
        fixed (char* p = mapped) { m = K32GetMappedFileNameW(GetCurrentProcess(), image, p, (uint)mapped.Length); }
        var opened = new char[1024];
        uint o;
        fixed (char* p = opened) { o = GetFinalPathNameByHandleW(file, p, (uint)opened.Length, 0x2 /* VOLUME_NAME_NT */); }
        if (m == 0 || o == 0 || m >= mapped.Length || o >= opened.Length) { return false; }
        return new string(mapped, 0, (int)m).Equals(new string(opened, 0, (int)o), StringComparison.OrdinalIgnoreCase);
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDefaultDllDirectories(uint flags);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint K32GetMappedFileNameW(nint process, nint address, char* name, uint size);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, char* path, uint size, uint flags);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hwnd, string text, string caption, uint type);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existing, string? newName, uint flags);
}
