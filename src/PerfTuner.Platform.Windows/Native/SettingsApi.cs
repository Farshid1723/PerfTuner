using System.Runtime.InteropServices;

namespace PerfTuner.Platform.Windows.Native;

/// <summary>SystemParametersInfo (user32) and the power-scheme API (powrprof). Both documented.</summary>
internal static unsafe partial class SettingsApi
{
    public const uint SpifUpdateIniFile = 0x01;
    public const uint SpifSendChange = 0x02;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint action, uint uiParam, nint pvParam, uint winIni);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerGetActiveScheme(nint userRootPowerKey, out Guid* activePolicy);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSetActiveScheme(nint userRootPowerKey, in Guid scheme);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerEnumerate(nint rootPowerKey, Guid* schemeGuid, Guid* subGroup, uint accessFlags, uint index, byte* buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadFriendlyName(nint rootPowerKey, in Guid scheme, Guid* subGroup, Guid* setting, byte* buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadACValueIndex(nint rootPowerKey, in Guid scheme, in Guid subGroup, in Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadDCValueIndex(nint rootPowerKey, in Guid scheme, in Guid subGroup, in Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteACValueIndex(nint rootPowerKey, in Guid scheme, in Guid subGroup, in Guid setting, uint value);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteDCValueIndex(nint rootPowerKey, in Guid scheme, in Guid subGroup, in Guid setting, uint value);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint mem);
}
