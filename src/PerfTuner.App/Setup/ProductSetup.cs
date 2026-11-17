using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32;
using PerfTuner.Client;
using PerfTuner.Core.Security;
using PerfTuner.Core.Setup;
using PerfTuner.Platform.Windows;

namespace PerfTuner.App.Setup;

/// <summary>What the person chose in Setup.</summary>
public sealed record SetupOptions(bool DesktopShortcut, bool StartAtSignIn, bool InstallService);

/// <summary>An existing installation, from its Windows "Installed apps" entry.</summary>
public sealed record InstalledProduct(string Version, SetupOptions Options);

/// <summary>
/// What a standard per-machine installer does, done with documented Windows interfaces only:
/// files into %ProgramFiles%\PerfTuner (hardened folder, every file checked against its SHA-256), Start-menu and
/// optional desktop shortcut (IShellLink), optional start at sign-in (Task Scheduler, highest privileges, as the app
/// needs administrator rights), optional always-on engine service, and the "Installed apps" entry with uninstall.
/// Upgrades in place: the service is stopped first (which undoes its live tuning), files no longer shipped are removed.
/// Every location is fixed (<see cref="ProductLayout"/>); nothing comes from user input.
/// </summary>
public static class ProductSetup
{
    public static string Version => typeof(ProductSetup).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static InstalledProduct? Installed()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ProductLayout.UninstallKey);
        if (key?.GetValue("DisplayVersion") is not string version) { return null; }
        var service = ServiceControl.Query().State != ServiceRunState.NotInstalled;
        return new InstalledProduct(version, new SetupOptions(
            key.GetValue("PerfTunerDesktopShortcut") is 1, key.GetValue("PerfTunerStartAtSignIn") is 1, service));
    }

    /// <summary>PerfTuner programs running from the install folder (other than this process): they lock files.</summary>
    public static IReadOnlyList<string> RunningCopies()
    {
        var root = ProductLayout.InstallRoot + "\\";
        var list = new List<string>();
        foreach (var name in new[] { "PerfTuner", "perftuner-cli" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        if (p.Id != Environment.ProcessId && p.MainModule?.FileName is { } file && file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            list.Add($"{p.ProcessName} (process {p.Id})");
                        }
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        // exited or not inspectable: not one of ours to worry about
                    }
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Installs or upgrades from <paramref name="sourceDirectory"/> (Setup's verified unpack folder, which holds
    /// payload.manifest and the uninstaller). Throws <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>
    /// with a readable message on failure.
    /// </summary>
    public static void Install(string sourceDirectory, SetupOptions options, Action<string> progress)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);
        var root = ProductLayout.InstallRoot;
        if (ServiceInstaller.SamePath(sourceDirectory, root)) { throw new IOException("Setup can't install from the install folder itself."); }
        var entries = PayloadManifest.Parse(File.ReadAllText(Path.Combine(sourceDirectory, PayloadManifest.FileName)));
        var uninstallerSource = Path.Combine(sourceDirectory, ProductLayout.UninstallerName);
        if (!File.Exists(uninstallerSource)) { throw new IOException("The uninstaller is missing from the Setup package."); }

        var running = RunningCopies();
        if (running.Count > 0) { throw new IOException("PerfTuner is running from the install folder. Close it first: " + string.Join(", ", running)); }

        var service = ServiceControl.Query().State;
        if (service is not (ServiceRunState.NotInstalled or ServiceRunState.Stopped))
        {
            progress("Stopping the PerfTuner engine (this undoes its live tuning)…");
            if (ServiceInstaller.Stop(_ => { }) != ServiceOperationResult.Success) { throw new IOException("The PerfTuner engine service didn't stop, so nothing was changed."); }
        }

        progress("Copying files…");
        DirectoryHardening.CreateHardened(root);
        var installed = new List<PayloadEntry>();
        foreach (var e in entries)
        {
            installed.Add(CopyVerified(Path.Combine(sourceDirectory, e.Path), root, e));
        }
        installed.Add(CopyVerified(uninstallerSource, root, new PayloadEntry(ProductLayout.UninstallerName, new FileInfo(uninstallerSource).Length, HashOf(uninstallerSource))));

        var manifestPath = Path.Combine(root, ProductLayout.InstallManifestName);
        if (File.Exists(manifestPath))
        {
            try
            {
                foreach (var stale in PayloadManifest.Stale(PayloadManifest.Parse(File.ReadAllText(manifestPath)), installed))
                {
                    if (PathGuard.TryResolveUnderRoot(root, stale, out var path) && File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                    {
                        File.Delete(path);
                    }
                }
            }
            catch (FormatException)
            {
                // an unreadable old list: leave old files in place rather than guess
            }
        }
        File.WriteAllText(manifestPath, PayloadManifest.Format(installed));

        var exe = Path.Combine(root, ProductLayout.AppExeName);
        progress("Adding shortcuts…");
        ShellLink.Create(ProductLayout.StartMenuShortcut, exe, "", "Windows performance tuner: every change undoable");
        if (options.DesktopShortcut) { ShellLink.Create(ProductLayout.DesktopShortcut, exe, "", "Windows performance tuner: every change undoable"); }
        else { DeleteFileIfExists(ProductLayout.DesktopShortcut); }

        if (options.StartAtSignIn) { progress("Setting up start at sign-in…"); SignInTask.Register(exe); }
        else { SignInTask.Delete(); }

        if (options.InstallService)
        {
            progress("Installing the always-on engine…");
            var log = new List<string>();
            var result = ServiceInstaller.InstallOrUpdate(Path.Combine(root, "Service"), ProductPaths.Production, log.Add);
            if (result != ServiceOperationResult.Success) { throw new IOException("The engine service couldn't be installed: " + ElevatedOperations.Describe(result)); }
        }
        else if (service != ServiceRunState.NotInstalled)
        {
            progress("Removing the always-on engine…");
            ServiceInstaller.Uninstall(ProductPaths.Production, _ => { });
        }

        progress("Registering with Windows…");
        WriteUninstallEntry(root, options, installed.Sum(e => e.Size));
    }

    /// <summary>
    /// The uninstall steps that need the app: undo PerfTuner's changes to Windows settings (this account's and the
    /// machine-wide ones), remove the engine service, the sign-in task, the shortcuts and the "Installed apps" entry.
    /// Program files are removed afterwards by the uninstaller (a running program can't delete itself).
    /// Files you moved or deleted with PerfTuner are never touched.
    /// </summary>
    public static IReadOnlyList<string> RemoveIntegration()
    {
        var log = new List<string>();
        var result = ElevatedOperations.UninstallAll(ProductPaths.Production, log.Add);
        log.Add("Engine: " + ElevatedOperations.Describe(result));
        if (result is not (ServiceOperationResult.Success or ServiceOperationResult.CleanupIncomplete))
        {
            // Keep everything (including the Installed-apps entry) so uninstall can simply be run again.
            throw new IOException("The PerfTuner engine service couldn't be removed: " + ElevatedOperations.Describe(result) + " Nothing else was removed; try again after a restart.");
        }
        SignInTask.Delete();
        DeleteFileIfExists(ProductLayout.StartMenuShortcut);
        DeleteFileIfExists(ProductLayout.DesktopShortcut);
        Registry.LocalMachine.DeleteSubKeyTree(ProductLayout.UninstallKey, throwOnMissingSubKey: false);
        log.Add("Shortcuts, sign-in task and Installed-apps entry removed.");
        return log;
    }

    private static PayloadEntry CopyVerified(string source, string root, PayloadEntry entry)
    {
        if (!PathGuard.TryResolveUnderRoot(root, entry.Path, out var target)) { throw new IOException($"Refusing to write '{entry.Path}' outside the install folder."); }
        var dir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);
        if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) { throw new IOException($"'{dir}' is a link; refusing to write into it."); }
        if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint)) { File.Delete(target); }
        File.Copy(source, target, overwrite: true);
        var hash = HashOf(target);
        if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"'{entry.Path}' didn't copy correctly (checksum mismatch). Run Setup again.");
        }
        return entry with { Sha256 = hash };
    }

    internal static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path)) { File.Delete(path); }
    }

    private static void WriteUninstallEntry(string root, SetupOptions options, long bytes)
    {
        using var key = Registry.LocalMachine.CreateSubKey(ProductLayout.UninstallKey, writable: true);
        var uninstaller = Path.Combine(root, ProductLayout.UninstallerName);
        key.SetValue("DisplayName", "PerfTuner");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "PerfTuner contributors");
        key.SetValue("DisplayIcon", Path.Combine(root, ProductLayout.AppExeName) + ",0");
        key.SetValue("InstallLocation", root);
        key.SetValue("UninstallString", $"\"{uninstaller}\"");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /S");
        key.SetValue("URLInfoAbout", "https://github.com/Farshid1723/PerfTuner");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("PerfTunerDesktopShortcut", options.DesktopShortcut ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("PerfTunerStartAtSignIn", options.StartAtSignIn ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>
    /// "Start PerfTuner when I sign in": a logon task for members of Administrators (the app needs administrator rights,
    /// so for other accounts it couldn't start anyway), run with highest privileges, in the tray. Starting elevated at
    /// sign-in without a prompt is what this option is; Setup says so next to the checkbox.
    /// </summary>
    private static class SignInTask
    {
        public static void Register(string exe)
        {
            var xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo><Description>Starts PerfTuner in the notification area when you sign in.</Description></RegistrationInfo>
                  <Triggers><LogonTrigger><Enabled>true</Enabled><Delay>PT15S</Delay></LogonTrigger></Triggers>
                  <Principals><Principal id="Author"><GroupId>S-1-5-32-544</GroupId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>7</Priority>
                  </Settings>
                  <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--tray</Arguments></Exec></Actions>
                </Task>
                """;
            WithRoot(root => root.RegisterTask(ProductLayout.SignInTaskName, xml, 6 /* create or update */, null, null, 4 /* group */, null));
        }

        public static void Delete() => WithRoot(root =>
        {
            try { root.DeleteTask(ProductLayout.SignInTaskName, 0); }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { /* not there */ }
        });

        private static void WithRoot(Action<dynamic> action)
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("Task Scheduler is not available.");
            dynamic service = Activator.CreateInstance(type)!;
            try
            {
                service.Connect();
                action(service.GetFolder("\\"));
            }
            finally
            {
                Marshal.FinalReleaseComObject(service);
            }
        }
    }
}
