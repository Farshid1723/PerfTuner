using System.ComponentModel;
using System.Diagnostics;
using PerfTuner.Platform.Windows;

namespace PerfTuner.Client;

/// <summary>The only operations the elevated helper will perform. Nothing else is accepted.</summary>
public enum ElevatedVerb
{
    Install,
    Start,
    Stop,
    Uninstall,
}

/// <summary>
/// Privilege separation for service management. The GUI runs as a standard user; for a
/// privileged operation it relaunches ITS OWN executable with the "runas" verb, which shows
/// the Windows UAC consent prompt. The elevated instance accepts exactly one allow-listed
/// verb, performs it with no UI, and exits with a <see cref="ServiceOperationResult"/> code.
/// No paths or other data cross the elevation boundary — the payload location is derived
/// by the elevated process from its own location.
/// </summary>
public static class ElevatedOperations
{
    public const string Switch = "--elevated-op";

    /// <summary>From the standard-user GUI: request consent and run the verb elevated.</summary>
    public static async Task<ServiceOperationResult> RunElevatedAsync(ElevatedVerb verb, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("unknown process path");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add(Switch);
        psi.ArgumentList.Add(verb.ToString().ToLowerInvariant());

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: user declined UAC
        {
            return ServiceOperationResult.Cancelled;
        }
        if (process is null)
        {
            return ServiceOperationResult.Cancelled;
        }
        using (process)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return Enum.IsDefined(typeof(ServiceOperationResult), process.ExitCode)
                ? (ServiceOperationResult)process.ExitCode
                : ServiceOperationResult.UnknownOperation;
        }
    }

    /// <summary>
    /// In the elevated helper: parse the command line strictly and execute.
    /// Returns null if this process was not started as an elevated helper.
    /// </summary>
    public static ServiceOperationResult? TryRunFromCommandLine(string[] args, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args[0] != Switch)
        {
            return null;
        }
        if (args.Length != 2 || !TryParseVerb(args[1], out var verb))
        {
            return ServiceOperationResult.UnknownOperation;
        }

        var payload = Path.Combine(AppContext.BaseDirectory, "Service");
        var paths = ProductPaths.Production;
        return verb switch
        {
            ElevatedVerb.Install => ServiceInstaller.InstallOrUpdate(payload, paths, log),
            ElevatedVerb.Start => ServiceInstaller.Start(log),
            ElevatedVerb.Stop => ServiceInstaller.Stop(log),
            ElevatedVerb.Uninstall => UninstallAll(paths, log),
            _ => ServiceOperationResult.UnknownOperation,
        };
    }

    /// <summary>Uninstall also undoes this user's persistent settings changes (power plan, visual effects, startup apps).</summary>
    public static ServiceOperationResult UninstallAll(ProductPaths paths, Action<string> log)
    {
        try
        {
            var (reverted, failed) = new PerfTuner.Platform.Windows.Settings.SettingsJournal().RevertAll();
            log($"Settings restored: {reverted} reverted, {failed} failed");
            var machine = new PerfTuner.Platform.Windows.Settings.MachineJournal();
            var (machineOk, machineFailed, machineSkipped) = machine.RevertAll();
            log($"Machine settings (page file) restored: {machineOk} reverted, {machineFailed} failed, {machineSkipped} skipped (changed by someone else since)");
            machine.DeleteIfEmpty(); // kept (admin-only, outside the data folder) while anything is left to undo
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException or System.Management.ManagementException)
        {
            log($"Settings restore failed: {ex.Message}");
        }
        return ServiceInstaller.Uninstall(paths, log);
    }

    private static bool TryParseVerb(string text, out ElevatedVerb verb)
    {
        switch (text)
        {
            case "install": verb = ElevatedVerb.Install; return true;
            case "start": verb = ElevatedVerb.Start; return true;
            case "stop": verb = ElevatedVerb.Stop; return true;
            case "uninstall": verb = ElevatedVerb.Uninstall; return true;
            default: verb = default; return false;
        }
    }

    public static string Describe(ServiceOperationResult result) => result switch
    {
        ServiceOperationResult.Success => "Done.",
        ServiceOperationResult.Cancelled => "Cancelled at the Windows permission prompt.",
        ServiceOperationResult.NotElevated => "Administrator permission is required.",
        ServiceOperationResult.PayloadMissing => "Service files were not found next to the app (expected a 'Service' folder).",
        ServiceOperationResult.CopyFailed => "Copying service files failed. See the helper log.",
        ServiceOperationResult.ScmFailed => "Windows refused to register the service. See the helper log.",
        ServiceOperationResult.StartFailed => "The service did not start within 30 seconds.",
        ServiceOperationResult.StopFailed => "The service did not stop within 30 seconds; nothing was removed.",
        ServiceOperationResult.DeleteFailed => "The service could not be unregistered.",
        ServiceOperationResult.CleanupIncomplete => "Service removed, but some files could not be deleted (see log).",
        _ => "Unknown result.",
    };
}
