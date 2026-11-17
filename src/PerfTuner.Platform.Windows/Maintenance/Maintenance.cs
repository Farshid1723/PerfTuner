using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using PerfTuner.Core.Security;

namespace PerfTuner.Platform.Windows.Maintenance;

/// <summary>A cache Windows Update leaves behind, cleaned with its services stopped (and started again afterwards).</summary>
public sealed record UpdateCache(string Id, string Title, string Description, string Folder, IReadOnlyList<string> Services);

/// <summary>
/// Windows Update leftovers (v0.9). Everything here runs in the elevated GUI, uses documented interfaces only
/// (ServiceController, the Task Scheduler API, the Windows Installer API) and never starts a command line.
/// </summary>
public static partial class UpdateLeftovers
{
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static IReadOnlyList<UpdateCache> Caches { get; } =
    [
        new("wu-download", "Windows Update download cache",
            "Update packages Windows already downloaded and installed. Windows downloads again whatever it still needs.",
            Path.Combine(Win, "SoftwareDistribution", "Download"), ["wuauserv", "bits"]),
        new("delivery-optimization", "Delivery Optimization cache",
            "Update pieces kept to share with other PCs. Safe to empty; Windows refills it only if sharing is on.",
            Path.Combine(Win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"), ["DoSvc"]),
    ];

    public static long Measure(UpdateCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (!Directory.Exists(cache.Folder)) { return 0; }
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(cache.Folder).EnumerateFiles("*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                total += f.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>Stops the cache's services, empties the folder (files in use are skipped), starts the services that were running.</summary>
    public static long Clean(UpdateCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (!Caches.Contains(cache)) { throw new InvalidOperationException("unknown cache"); }
        var before = Measure(cache);
        var restart = StopServices(cache.Services);
        try
        {
            var dir = new DirectoryInfo(cache.Folder);
            if (dir.Exists && !dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                foreach (var e in dir.EnumerateFileSystemInfos())
                {
                    try
                    {
                        if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) { continue; }
                        if (e is DirectoryInfo d) { d.Delete(recursive: true); } else { e.Delete(); }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        finally
        {
            StartServices(restart);
        }
        return Math.Max(0, before - Measure(cache));
    }

    /// <summary>Stops services, returning those that were running (to start again later).</summary>
    public static List<string> StopServices(IEnumerable<string> names)
    {
        var running = new List<string>();
        foreach (var n in names)
        {
            try
            {
                using var sc = new ServiceController(n);
                if (sc.Status == ServiceControllerStatus.Stopped) { continue; }
                running.Add(n);
                if (sc.CanStop) { sc.Stop(); }
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException) { }
        }
        return running;
    }

    public static void StartServices(IEnumerable<string> names)
    {
        foreach (var n in names)
        {
            try
            {
                using var sc = new ServiceController(n);
                if (sc.Status == ServiceControllerStatus.Stopped) { sc.Start(); }
            }
            catch (InvalidOperationException) { }
        }
    }

    // ------------------------------------------------------------------ component store (WinSxS)

    /// <summary>
    /// Starts Windows' own component cleanup ("\Microsoft\Windows\Servicing\StartComponentCleanup", the scheduled task
    /// Microsoft documents for WinSxS) through the Task Scheduler API. It removes superseded component versions older
    /// than 30 days and runs in the background for up to an hour. /ResetBase is deliberately not used.
    /// </summary>
    public static string StartComponentCleanup()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available");
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            dynamic folder = service.GetFolder(@"\Microsoft\Windows\Servicing");
            dynamic task = folder.GetTask("StartComponentCleanup");
            if ((int)task.State == 4) { return "Windows is already cleaning up its component store."; }
            task.Run(null);
            return "Windows started cleaning up old component versions. It runs in the background (up to an hour) and can't be undone; updates installed in the last 30 days keep their uninstall option.";
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    // ------------------------------------------------------------------ orphaned installer packages

    public static string InstallerFolder => Path.Combine(Win, "Installer");

    /// <summary>.msi/.msp files directly in %WINDIR%\Installer.</summary>
    public static List<InstallerFile> InstallerFiles()
    {
        var list = new List<InstallerFile>();
        foreach (var f in new DirectoryInfo(InstallerFolder).EnumerateFiles("*", new System.IO.EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (f.Extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".msp", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new InstallerFile(f.FullName, f.Length, f.LastWriteTimeUtc));
            }
        }
        return list;
    }

    /// <summary>
    /// Every cached package Windows Installer still needs: the LocalPackage of each installed product and patch, in
    /// all contexts (MsiEnumProductsEx / MsiEnumPatchesEx with MSIINSTALLCONTEXT_ALL and "all users").
    /// Throws if the enumeration fails part-way, so a partial list is never mistaken for "everything else is orphaned".
    /// </summary>
    public static List<string> ReferencedPackages()
    {
        // Never judge while something is being installed: its package may not be registered yet.
        using (var inProgress = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress"))
        {
            var running = Mutex.TryOpenExisting(@"Global\_MSIExecute", out var msiMutex);
            msiMutex?.Dispose();
            if (inProgress is not null || running)
            {
                throw new InvalidOperationException("An installation is running right now. Try again when it has finished.");
            }
        }
        var refs = new List<string>();
        const string allUsers = "s-1-1-0";
        for (uint i = 0; ; i++)
        {
            var code = new char[39];
            var sid = new char[256];
            var sidLen = (uint)sid.Length;
            var rc = MsiEnumProductsExW(null, allUsers, ContextAll, i, code, out var ctx, sid, ref sidLen);
            if (rc == ErrorNoMoreItems) { break; }
            if (rc != 0) { throw new InvalidOperationException($"MsiEnumProductsEx failed ({rc})"); }
            var product = new string(code).TrimEnd('\0');
            var userSid = ctx == ContextMachine ? null : new string(sid, 0, (int)sidLen);
            if (Info(p => MsiGetProductInfoExW(product, userSid, ctx, "LocalPackage", p.Buffer, ref p.Length)) is { Length: > 0 } local) { refs.Add(local); }
        }
        for (uint i = 0; ; i++)
        {
            var patch = new char[39];
            var target = new char[39];
            var sid = new char[256];
            var sidLen = (uint)sid.Length;
            var rc = MsiEnumPatchesExW(null, allUsers, ContextAll, PatchStateAll, i, patch, target, out var ctx, sid, ref sidLen);
            if (rc == ErrorNoMoreItems) { break; }
            if (rc != 0) { throw new InvalidOperationException($"MsiEnumPatchesEx failed ({rc})"); }
            var p = new string(patch).TrimEnd('\0');
            var t = new string(target).TrimEnd('\0');
            var userSid = ctx == ContextMachine ? null : new string(sid, 0, (int)sidLen);
            if (Info(b => MsiGetPatchInfoExW(p, t, userSid, ctx, "LocalPackage", b.Buffer, ref b.Length)) is { Length: > 0 } local) { refs.Add(local); }
        }
        return refs;
    }

    private sealed class Buf
    {
        public char[] Buffer = new char[1024];
        public uint Length = 1024;
    }

    private static string? Info(Func<Buf, uint> call)
    {
        var b = new Buf();
        var rc = call(b);
        if (rc == ErrorMoreData)
        {
            b.Buffer = new char[b.Length + 1];
            b.Length++;
            rc = call(b);
        }
        // No cached package for this entry: fine. Any other error means the list would be incomplete, so stop.
        if (rc == ErrorUnknownProperty) { return null; }
        if (rc != 0) { throw new InvalidOperationException($"Windows Installer couldn't describe an installed package ({rc})"); }
        return new string(b.Buffer, 0, (int)b.Length);
    }

    private const uint ContextAll = 7;
    private const uint ContextMachine = 4;
    private const uint PatchStateAll = 15;
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorMoreData = 234;
    private const uint ErrorUnknownProperty = 1608;

    [LibraryImport("msi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiEnumProductsExW(string? productCode, string? userSid, uint context, uint index, [Out] char[] installedProductCode, out uint installedContext, [Out] char[] sid, ref uint sidLength);

    [LibraryImport("msi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiGetProductInfoExW(string productCode, string? userSid, uint context, string property, [Out] char[] value, ref uint valueLength);

    [LibraryImport("msi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiEnumPatchesExW(string? productCode, string? userSid, uint context, uint filter, uint index, [Out] char[] patchCode, [Out] char[] targetProductCode, out uint targetContext, [Out] char[] targetUserSid, ref uint targetUserSidLength);

    [LibraryImport("msi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiGetPatchInfoExW(string patchCode, string productCode, string? userSid, uint context, string property, [Out] char[] value, ref uint valueLength);
}

/// <summary>Repair and reset tools, done with documented APIs.</summary>
public static partial class RepairTools
{
    // ------------------------------------------------------------------ restore point

    /// <summary>Creates a System Restore point through the documented WMI SystemRestore class.</summary>
    public static string CreateRestorePoint(string description)
    {
        using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
        using var input = cls.GetMethodParameters("CreateRestorePoint");
        input["Description"] = description;
        input["RestorePointType"] = 12u; // MODIFY_SETTINGS
        input["EventType"] = 100u;       // BEGIN_SYSTEM_CHANGE
        using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
        var rc = Convert.ToUInt32(output["ReturnValue"], CultureInfo.InvariantCulture);
        return rc switch
        {
            0 => "✔ Restore point created. (Windows creates at most one per 24 hours; if one was made recently, that one is kept.)",
            1058 => "System Protection is turned off for drive C:, so no restore point can be made. Turn it on in System Properties › System Protection.",
            _ => $"✖ Windows couldn't create a restore point (code {rc}).",
        };
    }

    // ------------------------------------------------------------------ reset Windows Update

    private static readonly string[] UpdateServices = ["wuauserv", "bits", "cryptsvc"];

    /// <summary>
    /// The classic Windows Update reset: stop its services, move SoftwareDistribution and catroot2 aside (kept as
    /// .old-&lt;date&gt; folders, so nothing is lost), start the services again. Windows rebuilds both folders.
    /// </summary>
    public static string ResetWindowsUpdate()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var moved = new List<string>();
        var restart = UpdateLeftovers.StopServices(UpdateServices);
        try
        {
            foreach (var dir in new[] { Path.Combine(win, "SoftwareDistribution"), Path.Combine(win, "System32", "catroot2") })
            {
                if (!Directory.Exists(dir)) { continue; }
                var target = dir + ".old-" + stamp;
                try
                {
                    Directory.Move(dir, target);
                    moved.Add(Path.GetFileName(target));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        finally
        {
            UpdateLeftovers.StartServices(restart.Union(UpdateServices));
        }
        return moved.Count == 2
            ? $"✔ Windows Update was reset. The old folders were kept as {string.Join(" and ", moved)}; delete them once updates work again."
            : moved.Count == 1 ? $"Partly reset: {moved[0]} was moved aside, the other folder was in use. Restart the PC and try again."
            : "✖ The update folders were in use and couldn't be moved. Restart the PC and try again.";
    }

    // ------------------------------------------------------------------ repair Windows image (DISM API)

    public enum ImageHealth
    {
        Healthy = 0,
        Repairable = 1,
        NotRepairable = 2,
    }

    private static Action<uint, uint>? _progress;

    /// <summary>
    /// Checks the Windows component store with the documented DISM API (dismapi.dll; the same engine as
    /// "DISM /Online /Cleanup-Image /ScanHealth"). Takes several minutes.
    /// </summary>
    public static unsafe ImageHealth ScanImage(Action<uint, uint> progress)
    {
        return WithSession(progress, session =>
        {
            Check(DismCheckImageHealth(session, true, 0, (nint)(delegate* unmanaged[Stdcall]<uint, uint, nint, void>)&OnProgress, 0, out var health));
            return (ImageHealth)health;
        });
    }

    /// <summary>Repairs the component store from Windows Update (like "DISM /Online /Cleanup-Image /RestoreHealth").</summary>
    public static unsafe void RestoreImage(Action<uint, uint> progress)
    {
        WithSession(progress, session =>
        {
            Check(DismRestoreImageHealth(session, 0, 0, false, 0, (nint)(delegate* unmanaged[Stdcall]<uint, uint, nint, void>)&OnProgress, 0));
            return 0;
        });
    }

    private static readonly Lock DismGate = new();

    /// <summary>One DISM session at a time (the DISM API and the progress callback are process-wide).</summary>
    private static T WithSession<T>(Action<uint, uint> progress, Func<uint, T> work)
    {
        lock (DismGate)
        {
            return WithSessionLocked(progress, work);
        }
    }

    private static T WithSessionLocked<T>(Action<uint, uint> progress, Func<uint, T> work)
    {
        _progress = progress;
        Check(DismInitialize(0, null, null));
        try
        {
            Check(DismOpenSession("DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}", null, null, out var session));
            try { return work(session); }
            finally { DismCloseSession(session); }
        }
        finally
        {
            DismShutdown();
            _progress = null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnProgress(uint current, uint total, nint user) => _progress?.Invoke(current, total);

    private static void Check(int hr)
    {
        if (hr < 0) { Marshal.ThrowExceptionForHR(hr); }
    }

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismInitialize(int logLevel, string? logFilePath, string? scratchDirectory);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismOpenSession(string imagePath, string? windowsDirectory, string? systemDrive, out uint session);

    [LibraryImport("dismapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismCheckImageHealth(uint session, [MarshalAs(UnmanagedType.Bool)] bool scanImage, nint cancelEvent, nint progress, nint userData, out int imageHealth);

    [LibraryImport("dismapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismRestoreImageHealth(uint session, nint sourcePaths, uint sourcePathCount, [MarshalAs(UnmanagedType.Bool)] bool limitAccess, nint cancelEvent, nint progress, nint userData);

    [LibraryImport("dismapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismCloseSession(uint session);

    [LibraryImport("dismapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DismShutdown();
}
