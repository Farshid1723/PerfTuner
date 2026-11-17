using System.Runtime.InteropServices;

namespace PerfTuner.Platform.Windows.Maintenance;

/// <summary>
/// An optional part of Windows. <paramref name="Parts"/> are DISM feature names, or (when <paramref name="IsCapability"/>)
/// one Features-on-Demand capability name. All parts must be on for the feature to count as on.
/// </summary>
public sealed record WindowsFeature(string Id, string Title, string Description, IReadOnlyList<string> Parts, bool IsCapability = false, string? Note = null)
{
    /// <summary>What "off" turns off. WSL leaves Virtual Machine Platform on, because WSL 2 tools such as Docker share it.</summary>
    public IReadOnlyList<string> PartsToDisable => Id == "wsl" ? Parts.Take(1).ToList() : Parts;

    /// <summary>What decides on/off in the switch (WSL: the Linux subsystem itself).</summary>
    public IReadOnlyList<string> PartsForState => Id == "wsl" ? Parts.Take(1).ToList() : Parts;
}

public enum FeatureState { Off, On, Partly, NotAvailable }

/// <summary>Turning optional Windows features on and off with the documented DISM API (the engine behind "Turn Windows features on or off").</summary>
public static partial class RepairTools
{
    public static IReadOnlyList<WindowsFeature> Features { get; } =
    [
        new("wsl", "Windows Subsystem for Linux", "Run Linux distributions and tools next to Windows.",
            ["Microsoft-Windows-Subsystem-Linux", "VirtualMachinePlatform"], Note: "Afterwards, install a distribution from the Microsoft Store."),
        new("sandbox", "Windows Sandbox", "A throw-away desktop for trying unknown programs safely; everything is deleted when you close it.",
            ["Containers-DisposableClientVM"], Note: "Pro, Enterprise and Education editions; needs virtualization in the firmware."),
        new("hyperv", "Hyper-V", "Microsoft's virtual machine platform.",
            ["Microsoft-Hyper-V-All"], Note: "Pro, Enterprise and Education editions."),
        new("netfx3", ".NET Framework 3.5", "Needed by some older programs and games (includes 2.0 and 3.0).",
            ["NetFx3"], Note: "Downloads its files from Windows Update."),
        new("legacy-media", "Legacy media components", "DirectPlay and Windows Media Player legacy, needed by some old games and programs.",
            ["DirectPlay", "WindowsMediaPlayer"]),
        new("nfs", "Client for NFS", "Open shared folders from Linux and NAS servers that use NFS.",
            ["ServicesForNFS-ClientOnly", "ClientForNFS-Infrastructure"], Note: "Pro, Enterprise and Education editions."),
        new("telnet", "Telnet client", "Simple terminal client for old network devices.", ["TelnetClient"]),
        new("ssh-server", "OpenSSH server", "Lets you sign in to this PC remotely over SSH. Opens port 22 to your network once its service is started.",
            ["OpenSSH.Server~~~~0.0.1.0"], IsCapability: true, Note: "Start and configure the \"OpenSSH SSH Server\" service afterwards."),
    ];

    /// <summary>The state of every feature in <see cref="Features"/>, read in one DISM session.</summary>
    public static IReadOnlyDictionary<string, FeatureState> GetFeatureStates() => WithSession((_, _) => { }, session =>
        Features.ToDictionary(f => f.Id, f =>
        {
            var states = f.PartsForState.Select(p => PartState(session, p, f.IsCapability)).ToList();
            if (states.Any(s => s is null)) { return FeatureState.NotAvailable; }
            return states.All(s => s == true) ? FeatureState.On : states.Any(s => s == true) ? FeatureState.Partly : FeatureState.Off;
        }));

    /// <summary>Turns a feature on or off. Returns true if Windows needs a restart to finish.</summary>
    public static unsafe bool SetFeature(WindowsFeature feature, bool on, Action<uint, uint> progress) => WithSession(progress, session =>
    {
        var restart = false;
        var cb = (nint)(delegate* unmanaged[Stdcall]<uint, uint, nint, void>)&OnProgress;
        foreach (var part in on ? feature.Parts : feature.PartsToDisable)
        {
            int hr;
            if (feature.IsCapability)
            {
                hr = on ? DismAddCapability(session, part, false, 0, 0, 0, cb, 0) : DismRemoveCapability(session, part, 0, cb, 0);
            }
            else
            {
                hr = on ? DismEnableFeature(session, part, null, 0, false, 0, 0, true, 0, cb, 0) : DismDisableFeature(session, part, null, false, 0, cb, 0);
            }
            if (hr == RebootRequired) { restart = true; continue; }
            Check(hr);
        }
        return restart;
    });

    /// <summary>true = on, false = off, null = not available on this edition.</summary>
    private static unsafe bool? PartState(uint session, string name, bool capability)
    {
        nint info;
        var hr = capability ? DismGetCapabilityInfo(session, name, out info) : DismGetFeatureInfo(session, name, null, 0, out info);
        if (hr < 0) { return null; }
        try
        {
            // Both structures start with a name pointer followed by the state (DismApi.h, 1-byte packing).
            var state = *(int*)((byte*)info + IntPtr.Size);
            return state is 4 or 5 /* installed, install pending */ ? true : false;
        }
        finally
        {
            _ = DismDelete(info);
        }
    }

    private const int RebootRequired = 0x00000BC2; // ERROR_SUCCESS_REBOOT_REQUIRED as returned by the DISM API

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismGetFeatureInfo(uint session, string featureName, string? identifier, int packageIdentifier, out nint featureInfo);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismGetCapabilityInfo(uint session, string name, out nint info);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismEnableFeature(uint session, string featureName, string? identifier, int packageIdentifier,
        [MarshalAs(UnmanagedType.Bool)] bool limitAccess, nint sourcePaths, uint sourcePathCount, [MarshalAs(UnmanagedType.Bool)] bool enableAll,
        nint cancelEvent, nint progress, nint userData);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismDisableFeature(uint session, string featureName, string? packageName, [MarshalAs(UnmanagedType.Bool)] bool removePayload,
        nint cancelEvent, nint progress, nint userData);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismAddCapability(uint session, string name, [MarshalAs(UnmanagedType.Bool)] bool limitAccess, nint sourcePaths, uint sourcePathCount,
        nint cancelEvent, nint progress, nint userData);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismRemoveCapability(uint session, string name, nint cancelEvent, nint progress, nint userData);

    [LibraryImport("dismapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismDelete(nint dismStructure);
}
