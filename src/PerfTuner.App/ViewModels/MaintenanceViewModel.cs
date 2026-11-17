using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.Controls;
using PerfTuner.App.Space;
using PerfTuner.Core.Security;
using PerfTuner.Platform.Windows.Maintenance;

namespace PerfTuner.App.ViewModels;

/// <summary>One Windows Update cache with its measured size.</summary>
public sealed partial class CacheRow(UpdateCache cache) : ObservableObject
{
    public UpdateCache Cache { get; } = cache;
    public string Title => Cache.Title;
    public string Description => Cache.Description;
    [ObservableProperty] private string _sizeText = "…";
}

/// <summary>
/// Windows Update leftovers (Clean page) and repair tools (Troubleshoot page), v0.9. Every action is explained in a
/// confirmation first; nothing here starts a command line.
/// </summary>
public sealed partial class MaintenanceViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<CacheRow> Caches { get; } = new(UpdateLeftovers.Caches.Select(c => new CacheRow(c)));
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _cleanMessage = "";
    [ObservableProperty] private string _repairMessage = "";
    [ObservableProperty] private string _repairProgress = "";
    [ObservableProperty] private string _orphanText = "Not checked yet.";
    [ObservableProperty] private bool _hasOrphans;
    private IReadOnlyList<InstallerFile> _orphans = [];
    private bool _measured;

    public void SetActive(bool active)
    {
        if (active && !_measured) { _ = MeasureAsync(); }
    }

    [RelayCommand]
    private async Task MeasureAsync()
    {
        _measured = true;
        foreach (var row in Caches)
        {
            var size = await Task.Run(() => UpdateLeftovers.Measure(row.Cache)).ConfigureAwait(true);
            row.SizeText = Format.Bytes(size);
        }
    }

    private bool NeedAdmin(Action<string> say)
    {
        if (main.IsElevatedProcess) { return false; }
        say("This needs full power (administrator).");
        return true;
    }

    // ------------------------------------------------------------------ update caches

    [RelayCommand]
    private async Task CleanCacheAsync(CacheRow? row)
    {
        if (row is null || Busy || NeedAdmin(m => CleanMessage = m)) { return; }
        if (ThemedDialog.Show($"{row.Description}\n\nPerfTuner briefly stops the {string.Join(" and ", row.Cache.Services)} service(s), empties the folder (files in use are skipped) and starts them again. Deleted files don't go to the Recycle Bin.",
            "Empty the " + row.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        Busy = true;
        CleanMessage = $"Cleaning {row.Title}…";
        try
        {
            var freed = await Task.Run(() => UpdateLeftovers.Clean(row.Cache)).ConfigureAwait(true);
            CleanMessage = $"✔ Freed {Format.Bytes(freed)} from the {row.Title}.";
            main.AddActivity("You", CleanMessage);
            main.Cockpit.CoolDown("disk");
            row.SizeText = Format.Bytes(await Task.Run(() => UpdateLeftovers.Measure(row.Cache)).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CleanMessage = "✖ " + ex.Message;
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task ComponentCleanupAsync()
    {
        if (Busy || NeedAdmin(m => CleanMessage = m)) { return; }
        if (ThemedDialog.Show(
            "Windows keeps older versions of its components after updates, in the WinSxS folder. This starts Windows' own cleanup task (the one Microsoft documents for WinSxS), which removes versions replaced more than 30 days ago.\n\n" +
            "• It runs in the background for up to an hour; you can keep working.\n• Updates from the last 30 days can still be uninstalled; older ones no longer can.\n• PerfTuner does not use the more aggressive \"/ResetBase\" option.",
            "Clean up old Windows components", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        try
        {
            CleanMessage = await Task.Run(UpdateLeftovers.StartComponentCleanup).ConfigureAwait(true);
            main.AddActivity("You", "Started Windows' component store cleanup");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            CleanMessage = "✖ Windows couldn't start the cleanup: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ orphaned installer packages

    [RelayCommand]
    private async Task FindOrphansAsync()
    {
        if (Busy || NeedAdmin(m => OrphanText = m)) { return; }
        Busy = true;
        OrphanText = "Asking Windows Installer which packages it still needs…";
        try
        {
            var (files, refs) = await Task.Run(() => (UpdateLeftovers.InstallerFiles(), UpdateLeftovers.ReferencedPackages())).ConfigureAwait(true);
            var orphans = InstallerOrphans.Find(files, refs, DateTime.UtcNow, UpdateLeftovers.InstallerFolder);
            if (orphans is null)
            {
                _orphans = [];
                OrphanText = "Windows Installer listed no installed packages, so PerfTuner can't tell what is safe to move. Nothing will be touched.";
            }
            else
            {
                _orphans = orphans;
                OrphanText = orphans.Count == 0
                    ? $"No orphaned packages: all {files.Count} files in the installer cache are still needed."
                    : $"{orphans.Count} of {files.Count} packages ({Format.Bytes(orphans.Sum(o => o.Size))}) are no longer used by any installed program or update.";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _orphans = [];
            OrphanText = "✖ The check couldn't finish, so nothing will be touched: " + ex.Message;
        }
        finally
        {
            HasOrphans = _orphans.Count > 0;
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task MoveOrphansAsync()
    {
        if (_orphans.Count == 0 || Busy || NeedAdmin(m => OrphanText = m)) { return; }
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Move orphaned installer packages to which folder?" };
        if (picker.ShowDialog(Application.Current.MainWindow) != true) { return; }
        var dest = picker.FolderName;
        if (PathSafety.CheckDestination(dest, PathSafety.Context()) is { } why) { OrphanText = $"✖ Can't move into {dest}: {why}."; return; }
        var bytes = _orphans.Sum(o => o.Size);
        if (ThemedDialog.Show(
            $"Move {_orphans.Count} orphaned package(s), {Format.Bytes(bytes)}, from {UpdateLeftovers.InstallerFolder} to\n{dest}?\n\n" +
            "No installed program or update refers to them any more. Moving (not deleting) keeps them in case an old uninstall ever asks for one; you can also move them back from History.",
            "Move orphaned installer packages", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        Busy = true;
        try
        {
            // Fingerprints first (the cache is admin-only, so this is the real content): a package only ever goes back
            // into the cache if it still matches.
            OrphanText = "Fingerprinting the packages…";
            var orphans = _orphans;
            var prints = await Task.Run(() => orphans.Select(o => new MovedPackage(Path.GetFileName(o.Path), o.Size, InstallerPackageLedger.Hash(o.Path))).ToList()).ConfigureAwait(true);
            var owner = Application.Current.MainWindow is { } w ? new WindowInteropHelper(w).Handle : 0;
            var results = await ShellFileOps.MoveAsync(_orphans.Select(o => o.Path).ToList(), dest, owner).ConfigureAwait(true);
            var moved = results.Where(r => r.Outcome == ItemOutcome.Moved && r.NewPath is not null).ToList();
            InstallerPackageLedger.Record(prints.Where(p => moved.Any(m => string.Equals(Path.GetFileName(m.Path), p.Name, StringComparison.OrdinalIgnoreCase))));
            var movedBytes = _orphans.Where(o => moved.Any(m => string.Equals(m.Path, o.Path, StringComparison.OrdinalIgnoreCase))).Sum(o => o.Size);
            if (moved.Count > 0)
            {
                new FileOpsJournal().Record(new FileOpEntry
                {
                    Kind = FileOpKind.Moved,
                    Count = moved.Count,
                    Bytes = movedBytes,
                    Origin = FileOpEntry.InstallerCacheOrigin,
                    Summary = $"Moved {moved.Count} orphaned installer package(s), {Format.Bytes(movedBytes)}, to {dest}",
                    Moves = moved.Select(r => new MovedItem(r.Path, r.NewPath!)).ToList(),
                });
            }
            OrphanText = $"✔ Moved {moved.Count} package(s) ({Format.Bytes(movedBytes)})." + (moved.Count < _orphans.Count ? $" {_orphans.Count - moved.Count} couldn't be moved." : "");
            main.AddActivity("You", OrphanText);
            main.Cockpit.CoolDown("disk");
            _orphans = [];
            HasOrphans = false;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            OrphanText = "✖ " + ex.Message;
        }
        finally { Busy = false; }
    }

    // ------------------------------------------------------------------ repair tools

    [RelayCommand]
    private void CreateRestorePoint()
    {
        if (NeedAdmin(m => RepairMessage = m)) { return; }
        try
        {
            RepairMessage = RepairTools.CreateRestorePoint("PerfTuner " + DateTime.Now.ToString("g", CultureInfo.CurrentCulture));
            main.AddActivity("You", "Created a restore point");
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            RepairMessage = "✖ " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task RepairWindowsAsync()
    {
        if (Busy || NeedAdmin(m => RepairMessage = m)) { return; }
        if (ThemedDialog.Show(
            "PerfTuner asks Windows to check its own system files (the component store) for damage, using Windows' DISM engine. If damage is found, Windows downloads clean copies from Windows Update and repairs them.\n\n" +
            "• The check takes 5–15 minutes; a repair can take longer.\n• You can keep working.\n• Nothing of yours is changed; only damaged Windows files are replaced.",
            "Repair Windows", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        Busy = true;
        var dispatcher = Application.Current.Dispatcher;
        void Progress(string phase, uint cur, uint total) => dispatcher.BeginInvoke(() => RepairProgress = total > 0 ? $"{phase}: {100.0 * cur / total:F0} %" : phase + "…");
        try
        {
            RepairMessage = "Checking Windows' system files…";
            var health = await Task.Run(() => RepairTools.ScanImage((c, t) => Progress("Checking", c, t))).ConfigureAwait(true);
            if (health == RepairTools.ImageHealth.Healthy)
            {
                RepairMessage = "✔ Windows' system files are healthy. Nothing to repair.";
            }
            else if (health == RepairTools.ImageHealth.NotRepairable)
            {
                RepairMessage = "✖ Windows reports damage that can't be repaired automatically. An in-place upgrade (reinstall keeping your files and apps) is the usual fix.";
            }
            else
            {
                RepairMessage = "Damage found. Repairing from Windows Update…";
                await Task.Run(() => RepairTools.RestoreImage((c, t) => Progress("Repairing", c, t))).ConfigureAwait(true);
                RepairMessage = "✔ Windows' system files were repaired. Restart the PC when convenient.";
            }
            main.AddActivity("You", RepairMessage);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or DllNotFoundException or EntryPointNotFoundException or UnauthorizedAccessException or InvalidOperationException)
        {
            RepairMessage = "✖ The repair couldn't run: " + ex.Message;
        }
        finally
        {
            RepairProgress = "";
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task ResetWindowsUpdateAsync()
    {
        if (Busy || NeedAdmin(m => RepairMessage = m)) { return; }
        if (ThemedDialog.Show(
            "Use this when Windows Update keeps failing or is stuck.\n\nPerfTuner stops the update services, moves the SoftwareDistribution and catroot2 folders aside (kept as \".old\" folders, nothing is deleted) and starts the services again. Windows then rebuilds them; the update history list in Settings may look empty afterwards.",
            "Reset Windows Update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        Busy = true;
        try
        {
            RepairMessage = await Task.Run(RepairTools.ResetWindowsUpdate).ConfigureAwait(true);
            main.AddActivity("You", "Reset Windows Update");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RepairMessage = "✖ " + ex.Message;
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand needs an instance method.")]
    private void OpenPanel(string? id)
    {
        // Fixed list of classic Windows panels; the address never comes from input. Control-panel files are opened by
        // their full System32 path.
        var uri = id switch
        {
            "control" => "shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}",
            "network" => "shell:::{7007ACC7-3202-11D1-AAD2-00805FC1270E}",
            "power" => "shell:::{025A5937-A6BE-4686-A844-36FE4BEC8B6D}",
            "sound" => "ms-settings:sound",
            "system" => "shell:::{BB06C0E4-D293-4f75-8A90-CB05B6477EEE}",
            "devices" => "shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}",
            "programs" => "shell:::{7B81BE6A-CE2B-4676-A29E-EB907A5126C5}",
            "recovery" => "shell:::{9FE63AFD-59CF-4419-9775-ABCC3849F861}",
            "computer" => Path.Combine(Environment.SystemDirectory, "compmgmt.msc"),
            "mouse" => Path.Combine(Environment.SystemDirectory, "main.cpl"),
            "printers" => "shell:::{2227A280-3AEA-1069-A2DE-08002B30309D}",
            "region" => Path.Combine(Environment.SystemDirectory, "intl.cpl"),
            "datetime" => Path.Combine(Environment.SystemDirectory, "timedate.cpl"),
            "firewall" => Path.Combine(Environment.SystemDirectory, "firewall.cpl"),
            "maintenance" => Path.Combine(Environment.SystemDirectory, "wscui.cpl"),
            "sysprops" => Path.Combine(Environment.SystemDirectory, "sysdm.cpl"),
            "restore" => Path.Combine(Environment.SystemDirectory, "rstrui.exe"),
            _ => null,
        };
        if (uri is null) { return; }
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
