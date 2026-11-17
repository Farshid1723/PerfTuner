using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using PerfTuner.Contracts;
using PerfTuner.Core.Security;
using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows;

/// <summary>Outcome of a privileged service-management operation (also used as the elevated helper's exit code).</summary>
public enum ServiceOperationResult
{
    Success = 0,
    NotElevated = 10,
    PayloadMissing = 11,
    CopyFailed = 12,
    ScmFailed = 13,
    StartFailed = 14,
    StopFailed = 20,
    DeleteFailed = 21,
    CleanupIncomplete = 22,
    UnknownOperation = 30,
    Cancelled = 31,
}

/// <summary>
/// Install / update / start / stop / uninstall of the backend service.
/// Runs ONLY in the short-lived elevated helper process (the GUI relaunches itself with
/// "runas", which shows the UAC consent prompt). Every step is idempotent so a
/// half-finished run can simply be repeated.
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceExeName = "PerfTuner.Service.exe";

    /// <summary>
    /// Least privilege for a LocalSystem service: the SCM strips every privilege not listed
    /// here from the service's token.
    /// SeSystemProfilePrivilege — kernel ETW session; SeIncreaseBasePriorityPrivilege —
    /// Milestone 2 priority changes; SeImpersonatePrivilege — read IPC callers' tokens;
    /// SeChangeNotifyPrivilege — basic path traversal (required by nearly everything).
    /// Notably absent: SeDebug, SeTcb, SeBackup/Restore, SeLoadDriver, SeTakeOwnership.
    /// </summary>
    private static readonly string[] RequiredPrivileges =
    [
        "SeChangeNotifyPrivilege",
        "SeSystemProfilePrivilege",
        "SeIncreaseBasePriorityPrivilege",
        "SeImpersonatePrivilege",
    ];

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Install, or update in place if already installed. Leaves the service running.</summary>
    public static ServiceOperationResult InstallOrUpdate(string payloadDirectory, ProductPaths paths, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        if (!IsElevated()) { return ServiceOperationResult.NotElevated; }

        var sourceExe = Path.Combine(payloadDirectory, ServiceExeName);
        if (!File.Exists(sourceExe))
        {
            log($"Service payload not found at {payloadDirectory}");
            return ServiceOperationResult.PayloadMissing;
        }

        // Updating: stop first so binaries are not locked (stop also rolls back tuning).
        if (ServiceControl.Query().State is not (ServiceRunState.NotInstalled or ServiceRunState.Stopped))
        {
            var stop = Stop(log);
            if (stop != ServiceOperationResult.Success) { return stop; }
        }

        try
        {
            DirectoryHardening.CreateHardened(paths.InstallRoot);
            DirectoryHardening.CreateHardened(paths.ServiceInstallDirectory);
            // Installed with Setup, the app's own Service folder IS the install directory: nothing to copy.
            if (!SamePath(payloadDirectory, paths.ServiceInstallDirectory))
            {
                CopyTree(payloadDirectory, paths.ServiceInstallDirectory);
            }
            DirectoryHardening.CreateHardened(paths.DataRoot);
            if (!File.Exists(paths.ConfigFile))
            {
                var defaults = Path.Combine(paths.ServiceInstallDirectory, "config.default.json");
                if (File.Exists(defaults))
                {
                    File.Copy(defaults, paths.ConfigFile);
                }
            }
            log($"Copied service binaries to {paths.ServiceInstallDirectory}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Copy failed: {ex.Message}");
            return ServiceOperationResult.CopyFailed;
        }

        var binaryPath = "\"" + Path.Combine(paths.ServiceInstallDirectory, ServiceExeName) + "\"";
        try
        {
            RegisterService(binaryPath);
            log("Service registered with the Service Control Manager");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log($"SCM error: {ex.Message}");
            return ServiceOperationResult.ScmFailed;
        }

        return Start(log);
    }

    public static ServiceOperationResult Start(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!IsElevated()) { return ServiceOperationResult.NotElevated; }
        try
        {
            using var sc = new ServiceController(ProtocolConstants.ServiceName);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            log("Service running");
            return ServiceOperationResult.Success;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            log($"Start failed: {ex.Message}");
            return ServiceOperationResult.StartFailed;
        }
    }

    /// <summary>Stopping the service rolls back every active tuning change (service shutdown path).</summary>
    public static ServiceOperationResult Stop(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!IsElevated()) { return ServiceOperationResult.NotElevated; }
        try
        {
            using var sc = new ServiceController(ProtocolConstants.ServiceName);
            if (sc.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
            {
                sc.Stop();
            }
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            log("Service stopped");
            return ServiceOperationResult.Success;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            log($"Stop failed: {ex.Message}");
            return ServiceOperationResult.StopFailed;
        }
    }

    /// <summary>
    /// Stop → delete service → remove orphaned ETW session → remove binaries, config, logs, state.
    /// If the service will not stop, NOTHING is removed: we never delete a service that might
    /// still hold un-reverted changes.
    /// </summary>
    public static ServiceOperationResult Uninstall(ProductPaths paths, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        if (!IsElevated()) { return ServiceOperationResult.NotElevated; }

        if (ServiceControl.Query().State != ServiceRunState.NotInstalled)
        {
            var stop = Stop(log);
            if (stop != ServiceOperationResult.Success)
            {
                log("Uninstall aborted: the service did not stop, so nothing was removed.");
                return stop;
            }
            try
            {
                DeleteServiceRegistration();
                WaitUntilGone(TimeSpan.FromSeconds(15));
                log("Service registration removed");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                log($"Delete failed: {ex.Message}");
                return ServiceOperationResult.DeleteFailed;
            }
        }

        if (!ServiceControl.StopOrphanedEtwSession("PerfTuner-KernelIO"))
        {
            log("Warning: could not stop a leftover ETW session");
        }

        // If Setup installed the app into the same folder (v0.9+), the files belong to the app: only the registration
        // and the engine's data go here. Setup's own uninstaller removes the program files.
        var appInstalled = File.Exists(Path.Combine(paths.InstallRoot, ProductLayout.AppExeName));
        if (appInstalled) { log("App is installed in the same folder: program files kept (removed by Setup's uninstaller)"); }
        var complete = (appInstalled || TryDeleteTree(paths.InstallRoot, log)) & TryDeleteTree(paths.DataRoot, log);
        // The named pipe is a kernel object owned by the service process: it disappeared when
        // the process exited. There are no registry keys, scheduled tasks, or drivers to remove.
        return complete ? ServiceOperationResult.Success : ServiceOperationResult.CleanupIncomplete;
    }

    private static unsafe void RegisterService(string binaryPath)
    {
        var scm = Advapi32.OpenSCManager(null, null, Advapi32.ScManagerConnect | Advapi32.ScManagerCreateService);
        if (scm == 0) { throw ServiceControl.LastError("OpenSCManager"); }
        try
        {
            var svc = Advapi32.OpenService(scm, ProtocolConstants.ServiceName, Advapi32.ServiceAllAccess);
            if (svc == 0)
            {
                // account: null = LocalSystem, with its privileges trimmed below.
                svc = Advapi32.CreateService(scm, ProtocolConstants.ServiceName, ProtocolConstants.ServiceDisplayName,
                    Advapi32.ServiceAllAccess, Advapi32.ServiceWin32OwnProcess, Advapi32.ServiceAutoStart,
                    Advapi32.ServiceErrorNormal, binaryPath, null, 0, null, null, null);
                if (svc == 0) { throw ServiceControl.LastError("CreateService"); }
            }
            else
            {
                const uint NoChange = 0xFFFFFFFF;
                if (!Advapi32.ChangeServiceConfig(svc, NoChange, Advapi32.ServiceAutoStart, NoChange, binaryPath, null, 0, null, null, null, ProtocolConstants.ServiceDisplayName))
                {
                    throw ServiceControl.LastError("ChangeServiceConfig");
                }
            }

            try
            {
                fixed (char* description = "Monitors CPU, memory, disk and network with low overhead and applies safe, reversible tuning. Removing it restores all settings.")
                {
                    var info = description;
                    Check(Advapi32.ChangeServiceConfig2(svc, Advapi32.ConfigDescription, &info), "description");
                }

                var delayed = 1; // delayed auto-start: do not compete with boot
                Check(Advapi32.ChangeServiceConfig2(svc, Advapi32.ConfigDelayedAutoStart, &delayed), "delayed start");

                // Restart after 5 s, then 60 s, then give up for the day (avoid crash loops).
                var actions = stackalloc ScAction[3];
                actions[0] = new ScAction { Type = 1, Delay = 5_000 };
                actions[1] = new ScAction { Type = 1, Delay = 60_000 };
                actions[2] = new ScAction { Type = 0, Delay = 0 };
                var failure = new ServiceFailureActions { ResetPeriodSeconds = 86_400, ActionCount = 3, Actions = actions };
                Check(Advapi32.ChangeServiceConfig2(svc, Advapi32.ConfigFailureActions, &failure), "failure actions");

                var sidType = Advapi32.ServiceSidTypeUnrestricted;
                Check(Advapi32.ChangeServiceConfig2(svc, Advapi32.ConfigServiceSidInfo, &sidType), "service SID");

                var multiSz = string.Join('\0', RequiredPrivileges) + "\0\0";
                fixed (char* privileges = multiSz)
                {
                    var info = privileges;
                    Check(Advapi32.ChangeServiceConfig2(svc, Advapi32.ConfigRequiredPrivileges, &info), "required privileges");
                }
            }
            finally
            {
                Advapi32.CloseServiceHandle(svc);
            }
        }
        finally
        {
            Advapi32.CloseServiceHandle(scm);
        }
    }

    private static void DeleteServiceRegistration()
    {
        var scm = Advapi32.OpenSCManager(null, null, Advapi32.ScManagerConnect);
        if (scm == 0) { throw ServiceControl.LastError("OpenSCManager"); }
        try
        {
            var svc = Advapi32.OpenService(scm, ProtocolConstants.ServiceName, Advapi32.Delete);
            if (svc == 0)
            {
                if (Marshal.GetLastPInvokeError() == Advapi32.ErrorServiceDoesNotExist) { return; }
                throw ServiceControl.LastError("OpenService");
            }
            try
            {
                if (!Advapi32.DeleteService(svc) && Marshal.GetLastPInvokeError() != Advapi32.ErrorServiceMarkedForDelete)
                {
                    throw ServiceControl.LastError("DeleteService");
                }
            }
            finally
            {
                Advapi32.CloseServiceHandle(svc);
            }
        }
        finally
        {
            Advapi32.CloseServiceHandle(scm);
        }
    }

    private static void WaitUntilGone(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (ServiceControl.Query().State != ServiceRunState.NotInstalled && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(250);
        }
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static void Check(bool ok, string what)
    {
        if (!ok) { throw ServiceControl.LastError($"ChangeServiceConfig2({what})"); }
    }

    /// <summary>Copies a directory tree, refusing reparse points (no junction tricks).</summary>
    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var relative = Path.GetRelativePath(source, file);
            if (!PathGuard.TryResolveUnderRoot(destination, relative, out var target))
            {
                throw new IOException($"Refusing to copy '{relative}' outside the install directory");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool TryDeleteTree(string path, Action<string> log)
    {
        try
        {
            if (Directory.Exists(path))
            {
                if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Never recurse through a junction: just unlink it.
                    Directory.Delete(path);
                }
                else
                {
                    Directory.Delete(path, recursive: true);
                }
                log($"Removed {path}");
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Could not remove {path}: {ex.Message}");
            return false;
        }
    }
}
