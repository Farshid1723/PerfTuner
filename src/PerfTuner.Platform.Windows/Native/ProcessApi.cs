using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PerfTuner.Platform.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct PowerThrottlingState
{
    public uint Version;      // PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1
    public uint ControlMask;
    public uint StateMask;
}

/// <summary>Documented process-control APIs (processthreadsapi.h, memoryapi.h, psapi.h).</summary>
internal static unsafe partial class ProcessApi
{
    public const uint Terminate = 0x0001;
    public const uint SetQuota = 0x0100;
    public const uint SetInformation = 0x0200;
    public const uint QueryInformation = 0x0400;
    public const uint QueryLimitedInformation = 0x1000;
    public const uint SetLimitedInformation = 0x2000;

    public const uint IdlePriorityClass = 0x40;
    public const uint BelowNormalPriorityClass = 0x4000;
    public const uint NormalPriorityClass = 0x20;
    public const uint AboveNormalPriorityClass = 0x8000;
    public const uint HighPriorityClass = 0x80;
    public const uint RealtimePriorityClass = 0x100;

    public const int ProcessMemoryPriorityClass = 0;   // PROCESS_INFORMATION_CLASS.ProcessMemoryPriority
    public const int ProcessPowerThrottlingClass = 4;  // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling
    public const uint PowerThrottlingExecutionSpeed = 0x1;

    public const uint QuotaLimitsHardwsMinDisable = 0x2;
    public const uint QuotaLimitsHardwsMaxDisable = 0x8;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetPriorityClass(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetPriorityClass(SafeProcessHandle process, uint priorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessInformation(SafeProcessHandle process, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessInformation(SafeProcessHandle process, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessDefaultCpuSets(SafeProcessHandle process, uint* cpuSetIds, uint count, out uint required);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDefaultCpuSets(SafeProcessHandle process, uint* cpuSetIds, uint count);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemCpuSetInformation(byte* information, uint length, out uint returned, nint process, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessWorkingSetSizeEx(SafeProcessHandle process, out nuint min, out nuint max, out uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessWorkingSetSizeEx(SafeProcessHandle process, nuint min, nuint max, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyWorkingSet(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    /// <summary>
    /// Read-only whole-system process snapshot (SystemProcessInformation = 5). Documented in
    /// winternl.h; used only for reading, never for changing state. One call replaces
    /// hundreds of OpenProcess/GetProcessTimes calls.
    /// </summary>
    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}

/// <summary>PSAPI kernel-driver enumeration (documented; read-only).</summary>
internal static unsafe partial class DriverApi
{
    [LibraryImport("kernel32.dll", EntryPoint = "K32EnumDeviceDrivers", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDeviceDrivers(nint* imageBases, uint size, out uint needed);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetDeviceDriverBaseNameW", SetLastError = true)]
    public static partial uint GetDeviceDriverBaseName(nint imageBase, char* baseName, uint size);
}
