using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.Client;
using PerfTuner.Contracts;
using PerfTuner.Platform.Windows.Settings;

namespace PerfTuner.App.ViewModels;

public sealed record LiveChangeItem(ChangeView View)
{
    public string Description => View.Description;
    public string Source => Brand.OwnerName(View.Owner);
    public string Time => DateTimeOffset.FromUnixTimeMilliseconds(View.CreatedUnixMs).ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>History line for a file operation.</summary>
public sealed record FileOpItem(Space.FileOpEntry Entry)
{
    public string Time => DateTimeOffset.FromUnixTimeMilliseconds(Entry.CreatedUnixMs).ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    public string Description => Entry.Summary;
    public bool CanUndo => Entry.Kind == Space.FileOpKind.Moved && Entry.Moves.Count > 0;
    public string Note => Entry.Kind switch
    {
        Space.FileOpKind.Recycled => "Restore from the Recycle Bin",
        Space.FileOpKind.DeletedPermanently => "Can't be undone",
        _ => "",
    };
}

public sealed record SettingItem(SettingChange? Change, MachineChange? Machine = null, bool AdminJournal = false)
{
    public string Description => Machine is { } m
        ? m.Description + " (whole PC · after restart)" + (m.UndoNote is { } note ? $" · {note}; undo lets Windows manage the page file" : "")
        : Change?.Description ?? "";
    public string Time => DateTimeOffset.FromUnixTimeMilliseconds(Machine?.CreatedUnixMs ?? Change?.CreatedUnixMs ?? 0).ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>
/// Changes tab: everything currently in effect, each with its own Revert button;
/// "Revert everything"; circuit-breaker status and reset; activity history.
/// </summary>
public sealed partial class ChangesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private readonly List<HistoryEntry> _localHistory = [];

    public ChangesViewModel(MainViewModel main)
    {
        _main = main;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    public ObservableCollection<LiveChangeItem> LiveChanges { get; } = [];
    public ObservableCollection<SettingItem> SettingChanges { get; } = [];

    /// <summary>Files the user moved or deleted in the Space explorer (own journal; never part of Undo everything).</summary>
    public ObservableCollection<FileOpItem> FileOps { get; } = [];
    public ObservableCollection<string> History { get; } = [];

    [ObservableProperty] private string _breakerText = "–";
    [ObservableProperty] private bool _breakerTripped;
    [ObservableProperty] private string _message = "";

    public void SetActive(bool active)
    {
        if (active)
        {
            _ = RefreshAsync();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public void ApplyStatus(StatusResponse s)
    {
        BreakerTripped = s.Breaker != BreakerState.Closed;
        BreakerText = s.Breaker switch
        {
            BreakerState.Closed => "Closed — healthy, tuning allowed.",
            BreakerState.HalfOpen => "Half-open — probation after a trip; tuning resumes when the system stays healthy.",
            BreakerState.Open => $"OPEN — tuning paused and rolled back: {s.BreakerReason}",
            _ => $"LATCHED — tripped repeatedly: {s.BreakerReason}. Reset once you have checked the system.",
        };
    }

    public void AddLocalHistory(string message)
    {
        _localHistory.Add(new HistoryEntry { TimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Message = message });
        if (_localHistory.Count > 100) { _localHistory.RemoveAt(0); }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        SettingChanges.Clear();
        FileOps.Clear();
        foreach (var f in new Space.FileOpsJournal().Entries.Reverse()) { FileOps.Add(new FileOpItem(f)); }
        foreach (var s in _main.SystemSettings.Journal.Entries.Reverse())
        {
            SettingChanges.Add(new SettingItem(s));
        }
        foreach (var m in new MachineJournal().Entries.Reverse())
        {
            SettingChanges.Add(new SettingItem(null, m));
        }
        foreach (var s in _main.SystemSettings.MachineSettings?.Entries.Reverse() ?? [])
        {
            SettingChanges.Add(new SettingItem(s, null, AdminJournal: true));
        }

        HistoryEntry[] serviceHistory = [];
        if (_main.Client is { } client)
        {
            try
            {
                var changes = await client.GetChangesAsync().ConfigureAwait(true);
                LiveChanges.Clear();
                foreach (var c in changes.Active.Reverse())
                {
                    LiveChanges.Add(new LiveChangeItem(c));
                }
                serviceHistory = changes.History;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or OperationCanceledException)
            {
                Message = "Could not load changes: " + ex.Message;
            }
        }

        History.Clear();
        foreach (var h in serviceHistory.Concat(_localHistory).OrderByDescending(h => h.TimeUnixMs).Take(200))
        {
            History.Add($"{DateTimeOffset.FromUnixTimeMilliseconds(h.TimeUnixMs).ToLocalTime():HH:mm:ss}  {h.Message}");
        }
    }

    [RelayCommand]
    private async Task RevertLiveAsync(LiveChangeItem? item)
    {
        if (item is null || _main.Client is not { } client) { return; }
        Message = await Call(() => client.RevertChangeAsync(item.View.Id)).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RevertSettingAsync(SettingItem? item)
    {
        if (item is null) { return; }
        try
        {
            if (item.Machine is { } machine)
            {
                if (!_main.IsElevatedProcess)
                {
                    Message = "Undoing a page-file change needs full power (administrator).";
                    return;
                }
                if (MachineJournal.HasDrifted(machine))
                {
                    var answer = Controls.ThemedDialog.Show(
                        $"The page file was changed again after PerfTuner changed it.\n\nNow: {PageFile.Read().Describe()}\nBefore PerfTuner: {machine.PageFileBefore?.Describe()}\n\nRestore the setting from before PerfTuner anyway?",
                        "Undo page-file change", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                    if (answer != MessageBoxResult.Yes) { return; }
                }
                Message = new MachineJournal().Revert(machine.Id);
            }
            else if (item.Change is { } change && item.AdminJournal)
            {
                if (_main.SystemSettings.MachineSettings is not { } admin)
                {
                    Message = "Undoing this change needs full power (administrator).";
                    return;
                }
                Message = admin.Revert(change.Id);
            }
            else if (item.Change is { } change2)
            {
                Message = _main.SystemSettings.Journal.Revert(change2.Id);
            }
            AddLocalHistory(Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException or System.Management.ManagementException)
        {
            Message = "✖ Revert failed: " + ex.Message;
        }
        _main.SystemSettings.Refresh();
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RevertEverythingAsync()
    {
        var answer = Controls.ThemedDialog.Show(
            "Undo every change made by Performance Tuner?\n\n• live process tuning (priorities, efficiency mode, cores, memory)\n• power plan and processor settings\n• Windows settings, visual effects, transparency and startup apps\n• Undocumented-page settings, boot settings and services (needs full power)\n• page-file changes (take effect after a restart)\n\n(Ended processes and deleted temp files cannot be restored. Files you moved or deleted in the Space explorer are not touched.)",
            "Revert everything", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) { return; }

        var parts = new List<string>();
        if (_main.Client is { } client)
        {
            parts.Add(await Call(() => client.RevertAllAsync()).ConfigureAwait(true));
        }
        try
        {
            var (ok, failed) = _main.SystemSettings.Journal.RevertAll();
            if (_main.SystemSettings.MachineSettings is { } admin)
            {
                var (aok, afailed) = admin.RevertAll();
                ok += aok;
                failed += afailed;
            }
            parts.Add($"Settings: {ok} reverted, {failed} failed.");
            if (_main.IsElevatedProcess && new MachineJournal().Entries.Count > 0)
            {
                var (mok, mfailed, mskipped) = new MachineJournal().RevertAll();
                parts.Add($"Page file: {mok} reverted, {mfailed} failed" + (mskipped > 0 ? $", {mskipped} kept because it was changed again since" : "") + " (after restart).");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
        {
            parts.Add("Settings revert failed: " + ex.Message);
        }
        Message = string.Join("  ", parts);
        AddLocalHistory("Revert everything: " + Message);
        _main.SystemSettings.Refresh();
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ResetBreakerAsync()
    {
        if (_main.Client is { } client)
        {
            Message = await Call(() => client.ResetBreakerAsync()).ConfigureAwait(true);
        }
    }

    private static async Task<string> Call(Func<Task<ActionResultResponse>> call)
    {
        try
        {
            var r = await call().ConfigureAwait(true);
            return (r.Success ? "✔ " : "✖ ") + r.Message;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or OperationCanceledException)
        {
            return "✖ " + ex.Message;
        }
    }

    /// <summary>Moves the items of one move operation back to where they came from (one operation at a time).</summary>
    [RelayCommand]
    private async Task UndoFileMoveAsync(FileOpItem? item)
    {
        if (item is not { CanUndo: true }) { return; }
        var ready = new List<Space.MovedItem>();
        var problems = new List<string>();
        // The journal is an ordinary per-user file: every path in it is checked like a fresh selection before use.
        var ctx = Space.PathSafety.Context();
        if (item.Entry.Origin == Space.FileOpEntry.InstallerCacheOrigin)
        {
            await UndoInstallerMoveAsync(item).ConfigureAwait(true);
            return;
        }
        foreach (var m in item.Entry.Moves)
        {
            var back = Path.GetDirectoryName(m.From);
            var isFolder = Directory.Exists(m.To);
            if (m.Merged) { problems.Add($"{Path.GetFileName(m.To)} was merged into an existing folder; move it back in Explorer"); }
            else if (Space.PathSafety.CheckSource(m.To, isFolder, ctx) is { } whyFrom) { problems.Add($"{Path.GetFileName(m.To)}: {whyFrom}"); }
            else if (back is null) { problems.Add($"{Path.GetFileName(m.From)}: unknown original folder"); }
            else if (Space.PathSafety.CheckDestination(back, ctx) is { } whyTo) { problems.Add($"{Path.GetFileName(m.From)}: the original folder can't be used ({whyTo})"); }
            else if (!Space.PathSafety.IsCanonical(m.From)) { problems.Add($"{Path.GetFileName(m.From)}: unusual original path"); }
            else if (!File.Exists(m.To) && !Directory.Exists(m.To)) { problems.Add($"{Path.GetFileName(m.To)} is no longer at {Path.GetDirectoryName(m.To)}"); }
            else if (File.Exists(m.From) || Directory.Exists(m.From)) { problems.Add($"{Path.GetFileName(m.From)}: something with that name is back in the original folder"); }
            else if (back is null || !Directory.Exists(back)) { problems.Add($"{Path.GetFileName(m.From)}: the original folder no longer exists"); }
            else if (!string.Equals(Path.GetFileName(m.From), Path.GetFileName(m.To), StringComparison.OrdinalIgnoreCase)) { problems.Add($"{Path.GetFileName(m.To)} was renamed while moving; move it back in Explorer"); }
            else { ready.Add(m); }
        }
        if (ready.Count == 0)
        {
            Message = "Nothing can be moved back: " + string.Join("; ", problems.Take(3));
            return;
        }
        var list = string.Join("\n", ready.Take(6).Select(m => $"• {Path.GetFileName(m.To)} → {Path.GetDirectoryName(m.From)}")) + (ready.Count > 6 ? $"\n• … and {ready.Count - 6} more" : "");
        var extra = problems.Count > 0 ? $"\n\n{problems.Count} item(s) can't go back: " + string.Join("; ", problems.Take(2)) : "";
        if (Controls.ThemedDialog.Show($"Move {ready.Count} item(s) back?\n\n{list}{extra}", "Move back", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        var owner = Application.Current.MainWindow is { } w ? new System.Windows.Interop.WindowInteropHelper(w).Handle : 0;
        var movedBack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var group in ready.GroupBy(m => Path.GetDirectoryName(m.From)!, StringComparer.OrdinalIgnoreCase))
            {
                var results = await Space.ShellFileOps.MoveAsync(group.Select(m => m.To).ToList(), group.Key, owner).ConfigureAwait(true);
                foreach (var r in results.Where(r => r.Outcome == Space.ItemOutcome.Moved)) { movedBack.Add(r.Path); }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            Message = "✖ Moving back failed: " + ex.Message;
        }
        var journal = new Space.FileOpsJournal();
        var remaining = item.Entry.Moves.Where(m => !movedBack.Contains(m.To)).ToList();
        if (remaining.Count == 0) { journal.Remove(item.Entry.Id); }
        else { journal.Update(item.Entry with { Moves = remaining, Count = remaining.Count, Summary = item.Entry.Summary + $" ({movedBack.Count} moved back)" }); }
        Message = $"✔ Moved {movedBack.Count} item(s) back." + (remaining.Count > 0 ? $" {remaining.Count} still in the new place." : "");
        _main.AddActivity("You", Message);
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Orphaned installer packages go back only into %WINDIR%\Installer, under their original name, and only if their
    /// content still matches the fingerprint taken when they were moved (stored where only administrators can write).
    /// </summary>
    private async Task UndoInstallerMoveAsync(FileOpItem item)
    {
        var cache = Platform.Windows.Maintenance.UpdateLeftovers.InstallerFolder;
        var candidates = item.Entry.Moves.Where(m => string.Equals(Path.GetDirectoryName(m.From), cache, StringComparison.OrdinalIgnoreCase) && File.Exists(m.To)).ToList();
        if (candidates.Count == 0) { Message = "Nothing can be moved back: the packages are no longer where they were moved to."; return; }
        if (Controls.ThemedDialog.Show($"Put {candidates.Count} installer package(s) back into {cache}?\n\nEach one is checked against the fingerprint taken when it was moved; a changed file is not put back.",
                "Move back", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        var problems = new List<string>();
        var done = await Task.Run(() =>
        {
            var back = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in candidates)
            {
                try
                {
                    if (Platform.Windows.Maintenance.InstallerPackageLedger.Restore(m.To, Path.GetFileName(m.From)) is { } why) { problems.Add($"{Path.GetFileName(m.From)}: {why}"); }
                    else { back.Add(m.To); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{Path.GetFileName(m.From)}: {ex.Message}");
                }
            }
            return back;
        }).ConfigureAwait(true);
        var journal = new Space.FileOpsJournal();
        var remaining = item.Entry.Moves.Where(m => !done.Contains(m.To)).ToList();
        if (remaining.Count == 0) { journal.Remove(item.Entry.Id); }
        else { journal.Update(item.Entry with { Moves = remaining, Count = remaining.Count }); }
        Message = $"✔ Put {done.Count} package(s) back." + (problems.Count > 0 ? " Not put back: " + string.Join("; ", problems.Take(3)) : "");
        _main.AddActivity("You", Message);
        await RefreshAsync().ConfigureAwait(true);
    }
}
