using System.Runtime.InteropServices;

namespace PerfTuner.Platform.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatusProcess
{
    public uint ServiceType;
    public uint CurrentState;
    public uint ControlsAccepted;
    public uint Win32ExitCode;
    public uint ServiceSpecificExitCode;
    public uint CheckPoint;
    public uint WaitHint;
    public uint ProcessId;
    public uint ServiceFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ScAction
{
    public uint Type;   // 0 = none, 1 = restart
    public uint Delay;  // ms
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ServiceFailureActions
{
    public uint ResetPeriodSeconds;
    public char* RebootMessage;
    public char* Command;
    public uint ActionCount;
    public ScAction* Actions;
}

/// <summary>Service Control Manager and ETW session control. Used only by the installer / uninstaller.</summary>
internal static unsafe partial class Advapi32
{
    public const uint ScManagerConnect = 0x0001;
    public const uint ScManagerCreateService = 0x0002;
    public const uint ServiceQueryStatus = 0x0004;
    public const uint ServiceChangeConfig = 0x0002;
    public const uint ServiceStart = 0x0010;
    public const uint ServiceStop = 0x0020;
    public const uint Delete = 0x00010000;
    public const uint ServiceAllAccess = 0x000F01FF;

    public const uint ServiceWin32OwnProcess = 0x10;
    public const uint ServiceAutoStart = 2;
    public const uint ServiceErrorNormal = 1;

    public const uint ConfigDescription = 1;
    public const uint ConfigFailureActions = 2;
    public const uint ConfigDelayedAutoStart = 3;
    public const uint ConfigServiceSidInfo = 5;
    public const uint ConfigRequiredPrivileges = 6;
    public const uint ServiceSidTypeUnrestricted = 1;

    public const int ErrorServiceDoesNotExist = 1060;
    public const int ErrorServiceMarkedForDelete = 1072;
    public const int ErrorWmiInstanceNotFound = 4201;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenService(nint scm, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateService(
        nint scm, string name, string displayName, uint access, uint serviceType, uint startType, uint errorControl,
        string binaryPath, string? loadOrderGroup, nint tagId, string? dependencies, string? account, string? password);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeServiceConfig(
        nint service, uint serviceType, uint startType, uint errorControl, string binaryPath,
        string? loadOrderGroup, nint tagId, string? dependencies, string? account, string? password, string? displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeServiceConfig2(nint service, uint infoLevel, void* info);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceStatusEx(nint service, int infoLevel, ServiceStatusProcess* buffer, int bufferSize, out int bytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteService(nint service);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int ControlTrace(ulong sessionHandle, string sessionName, void* properties, uint controlCode);
}
