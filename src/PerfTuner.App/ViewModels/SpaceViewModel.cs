using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.Controls;
using PerfTuner.App.Space;
using PerfTuner.Core.Space;

namespace PerfTuner.App.ViewModels;

/// <summary>One visible row of the tree list (the tree flattened, only expanded folders' children).</summary>
public sealed partial class SpaceRow(SpaceNode node, int depth, double share, ProtectionResult protection) : ObservableObject
{
    public SpaceNode Node { get; } = node;
    public int Depth { get; } = depth;
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    public double Share { get; } = share;
    public string ShareText => Share.ToString("P0", CultureInfo.CurrentCulture);
    public GridLength BarFilled => new(Math.Max(0.0001, Share), GridUnitType.Star);
    public GridLength BarEmpty => new(Math.Max(0.0001, 1 - Share), GridUnitType.Star);
    public string Name => Node.Name;
    public string SizeText => Node.Flags.HasFlag(SpaceFlags.NoAccess) ? "no access" : Format.Bytes(Node.Size);
    public string FilesText => Node.IsFolder ? Node.Files.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string ModifiedText => Node.LastWriteUtc > DateTime.MinValue.AddYears(1) ? Node.LastWriteUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) : "";
    public bool Expandable => Node.IsFolder && Node.Children is { Count: > 0 };
    public string Glyph => Node.Flags.HasFlag(SpaceFlags.ReparsePoint) ? "\uE71B" : Node.IsFolder ? "\uE8B7" : "\uE7C3";
    public bool IsLocked => protection.Level == ProtectionLevel.Locked;
    public bool IsWarn => protection.Level == ProtectionLevel.Warn;
    public string LockReason => protection.Reason;

    [ObservableProperty] private bool _isExpanded;

    public string ExpanderGlyph => !Expandable ? "" : IsExpanded ? "\uE70D" : "\uE76C";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpanderGlyph));
}

/// <summary>One report line (a file or folder, or a file-type total).</summary>
public sealed record ReportRow(SpaceNode? Node, string Name, string Folder, string SizeText, string Date, string Type, bool IsLocked, string LockReason);

/// <summary>
/// The Space view (design 2026-11-16): scan a fixed drive, show it as tree / treemap / sunburst, reports, and safe
/// Recycle Bin / permanent delete / move of selected items. Only scanned nodes reach file operations; each one is
/// checked again (protection rules, still exists, same type, not a link, still under the scanned drive) right before.
/// </summary>
public sealed partial class SpaceViewModel : ObservableObject
{
    private const int ReportSize = 200;
    private const int MaxRowsPerFolder = 2000;
    private readonly MainViewModel _main;
    private readonly HashSet<SpaceNode> _expanded = [];
    private readonly HashSet<SpaceNode> _selected = [];
    private CancellationTokenSource? _cts;
    private ProtectionContext _ctx;

    public SpaceViewModel(MainViewModel main)
    {
        _main = main;
        _ctx = BuildContext();
        _mode = GuiSettings.Current.SpaceMode is "treemap" or "sunburst" ? GuiSettings.Current.SpaceMode : "tree";
    }

    private static ProtectionContext BuildContext() => PathSafety.Context();

    /// <summary>Guards the tree: background reports read it, the UI thread changes it after operations and rescans.</summary>
    private readonly Lock _treeLock = new();

    // ------------------------------------------------------------------ state

    [ObservableProperty] private string _driveLetter = "C";
    [ObservableProperty] private SpaceNode? _root;
    [ObservableProperty] private SpaceNode? _currentFolder;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private int _version;
    [ObservableProperty] private string _mode;
    [ObservableProperty] private string _selectionText = "Nothing selected";
    [ObservableProperty] private string _selectionDetail = "Click items in the tree, the treemap, the sunburst or a report. Ctrl or Shift selects several.";
    [ObservableProperty] private bool _canAct;
    [ObservableProperty] private bool _selectionWarn;
    public ObservableCollection<SpaceRow> Rows { get; } = [];
    public ObservableCollection<SpaceNode> Breadcrumbs { get; } = [];
    public IReadOnlyCollection<SpaceNode> Selected => _selected;
    private long _driveUsed;

    public bool IsTree => Mode == "tree";
    public bool IsTreemap => Mode == "treemap";
    public bool IsSunburst => Mode == "sunburst";
    public string Title => $"Space on {DriveLetter}:";

    partial void OnModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsTree));
        OnPropertyChanged(nameof(IsTreemap));
        OnPropertyChanged(nameof(IsSunburst));
        GuiSettings.Update(s => s with { SpaceMode = value });
    }

    partial void OnDriveLetterChanged(string value) => OnPropertyChanged(nameof(Title));

    /// <summary>Shows a mode without remembering it (documentation screenshots).</summary>
    public void PreviewMode(string mode)
    {
        if (mode is not ("tree" or "treemap" or "sunburst")) { return; }
#pragma warning disable MVVMTK0034 // set the field on purpose: don't persist
        _mode = mode;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsTree));
        OnPropertyChanged(nameof(IsTreemap));
        OnPropertyChanged(nameof(IsSunburst));
    }

    [RelayCommand]
    private void SetMode(string? mode)
    {
        if (mode is "tree" or "treemap" or "sunburst") { Mode = mode; }
    }

    // ------------------------------------------------------------------ scanning

    /// <summary>Starts scanning a fixed drive from the Cockpit volume list (letter validated there and here).</summary>
    public void Start(string letter, long usedBytes)
    {
        if (letter is not { Length: 1 } || !char.IsAsciiLetterUpper(letter[0])) { return; }
        if (IsScanning && letter == DriveLetter) { return; }
        DriveLetter = letter;
        _driveUsed = usedBytes;
        _ = ScanAsync();
    }

    [RelayCommand]
    private Task ScanAgainAsync() => ScanAsync();

    private async Task ScanAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ctx = BuildContext();
        _expanded.Clear();
        _selected.Clear();
        var root = SpaceNode.NewFolder(DriveLetter + @":\");
        Root = root;
        CurrentFolder = root;
        UpdateBreadcrumbs();
        Rows.Clear();
        ReportRows.Clear();
        ReportNote = "Reports appear when the scan is finished.";
        UpdateSelection();
        IsScanning = true;
        Message = "";
        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressFraction = _driveUsed > 0 ? Math.Clamp((double)p.Bytes / _driveUsed, 0, 1) : 0;
            ProgressText = $"{(IsScanning ? "Scanning… " : "")}{p.Files:N0} files · {Format.Bytes(p.Bytes)} counted · {p.Elapsed:mm\\:ss}" +
                (p.NoAccess > 0 ? $" · {p.NoAccess:N0} folders without access" : "");
        });
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            await SpaceScanner.ScanDriveAsync(root.Name, batch => dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(Root, root)) { return; }
                foreach (var n in batch) { root.Attach(n); }
                Refresh();
            }), progress, ct).ConfigureAwait(true);
            await dispatcher.InvokeAsync(() => { }); // let the last batches attach
            ProgressFraction = 1;
            ProgressText = $"Done: {root.Files:N0} files · {Format.Bytes(root.Size)} in {root.Folders:N0} folders." +
                (root.Descendants().Any(n => n.Flags.HasFlag(SpaceFlags.NoAccess)) ? (_main.IsElevatedProcess ? " Some folders can't be read even with full power." : " Some folders need full power (administrator) to be counted.") : "");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(Root, root)) { ProgressText = "Scan stopped. What was counted so far is shown; folders that weren't finished are missing."; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProgressText = "The drive couldn't be scanned: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(Root, root)) { IsScanning = false; }
        }
        if (ReferenceEquals(Root, root)) { await RunReportAsync().ConfigureAwait(true); }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Called when the user leaves the Space view.</summary>
    public void Stop() => _cts?.Cancel();

    private void Refresh()
    {
        Version++;
        RebuildRows();
    }

    // ------------------------------------------------------------------ navigation and tree rows

    [RelayCommand]
    private void Open(SpaceNode? node)
    {
        if (node is not { IsFolder: true }) { return; }
        CurrentFolder = node;
        UpdateBreadcrumbs();
        Refresh();
    }

    [RelayCommand]
    private void Up()
    {
        if (CurrentFolder?.Parent is { } p) { Open(p); }
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();
        var chain = new List<SpaceNode>();
        for (var n = CurrentFolder; n is not null; n = n.Parent) { chain.Insert(0, n); }
        foreach (var n in chain) { Breadcrumbs.Add(n); }
    }

    [RelayCommand]
    private void Toggle(SpaceRow? row)
    {
        if (row is not { Expandable: true }) { return; }
        if (!_expanded.Add(row.Node)) { _expanded.Remove(row.Node); }
        RebuildRows();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        if (CurrentFolder is not { } folder) { return; }
        void AddChildren(SpaceNode parent, int depth)
        {
            if (parent.Children is null) { return; }
            var count = 0;
            foreach (var c in parent.Children)
            {
                if (++count > MaxRowsPerFolder) { break; }
                var row = new SpaceRow(c, depth, parent.Size > 0 ? (double)c.Size / parent.Size : 0, Protection(c)) { IsExpanded = _expanded.Contains(c) };
                Rows.Add(row);
                if (row.IsExpanded) { AddChildren(c, depth + 1); }
            }
        }
        AddChildren(folder, 0);
    }

    private ProtectionResult Protection(SpaceNode n)
    {
        if (n.Flags.HasFlag(SpaceFlags.Aggregate)) { return new(ProtectionLevel.Locked, "This is a summary of many small files; open the folder in Explorer to pick them."); }
        if (n.Flags.HasFlag(SpaceFlags.ReparsePoint)) { return new(ProtectionLevel.Locked, "This is a link to another place; its content is counted where it really lives."); }
        if (n.Parent is null) { return new(ProtectionLevel.Locked, "This is the whole drive."); }
        return ProtectionRules.Check(n.FullPath, n.IsFolder, n.Flags.HasFlag(SpaceFlags.System), _ctx);
    }

    // ------------------------------------------------------------------ selection (tree list, views, reports)

    /// <summary>Selection from the tree list or a report list (code-behind passes the selected nodes).</summary>
    public void SetSelection(IEnumerable<SpaceNode> nodes)
    {
        _selected.Clear();
        foreach (var n in nodes) { _selected.Add(n); }
        PermanentConfirmed = false;
        UpdateSelection();
    }

    /// <summary>Click in the treemap or the sunburst.</summary>
    public void Click(SpaceNode node, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (!_selected.Add(node)) { _selected.Remove(node); }
        }
        else
        {
            _selected.Clear();
            _selected.Add(node);
        }
        PermanentConfirmed = false;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        // A folder and something inside it: the inner item is already covered.
        var top = _selected.Where(n => !_selected.Any(o => o != n && IsAncestor(o, n))).ToList();
        var bytes = top.Sum(n => n.Size);
        var locked = top.Select(n => (n, p: Protection(n))).Where(x => x.p.Level == ProtectionLevel.Locked).ToList();
        SelectionWarn = top.Any(n => Protection(n).Level == ProtectionLevel.Warn);
        SelectionText = top.Count == 0 ? "Nothing selected" : top.Count == 1 ? $"{top[0].Name} · {Format.Bytes(bytes)}" : $"{top.Count} items · {Format.Bytes(bytes)}";
        SelectionDetail = top.Count == 0
            ? "Click items in the tree, the treemap, the sunburst or a report. Ctrl or Shift selects several."
            : locked.Count > 0 ? $"🔒 {locked[0].n.Name}: {locked[0].p.Reason}"
            : SelectionWarn ? "⚠ " + top.Select(Protection).First(p => p.Level == ProtectionLevel.Warn).Reason
            : top.Count == 1 ? top[0].FullPath : "Ready to move or delete.";
        CanAct = top.Count > 0 && locked.Count == 0 && !IsBusy;
        Version++;
        OnPropertyChanged(nameof(Selected));
    }

    private static bool IsAncestor(SpaceNode a, SpaceNode b)
    {
        for (var n = b.Parent; n is not null; n = n.Parent) { if (n == a) { return true; } }
        return false;
    }

    // ------------------------------------------------------------------ file operations

    [ObservableProperty] private bool _isBusy;

    private nint OwnerHandle => Application.Current.MainWindow is { } w ? new WindowInteropHelper(w).Handle : 0;

    /// <summary>
    /// The selection, checked again right before acting. Returns null (and a message) if anything is off:
    /// locked, unusual path, not on a local drive, a link on the path, changed or gone since the scan, or (for folders)
    /// something inside that PerfTuner wouldn't touch on its own (locked, unreadable, a link, a system file).
    /// </summary>
    private List<SpaceNode>? Validated()
    {
        var top = _selected.Where(n => !_selected.Any(o => o != n && IsAncestor(o, n))).ToList();
        if (top.Count == 0) { return null; }
        foreach (var n in top)
        {
            if (!IsAttached(n)) { Message = $"✖ {n.Name} is no longer part of this scan; scan again."; return null; }
            var p = Protection(n);
            if (p.Level == ProtectionLevel.Locked) { Message = $"🔒 {n.Name}: {p.Reason}"; return null; }
            var path = n.FullPath;
            if (!ProtectionRules.IsInside(path, DriveLetter + @":\")) { Message = $"✖ {n.Name} is not on drive {DriveLetter}:."; return null; }
            if (PathSafety.CheckSource(path, n.IsFolder, _ctx) is { } why) { Message = $"✖ {n.Name}: {why}."; return null; }
            try
            {
                var attr = File.GetAttributes(path);
                if (attr.HasFlag(FileAttributes.Directory) != n.IsFolder) { Message = $"✖ {n.Name} has changed since the scan; scan again."; return null; }
                if (attr.HasFlag(FileAttributes.System)) { Message = $"🔒 {n.Name}: Windows marked this as a system file."; return null; }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                Message = $"✖ {n.Name} no longer exists; scan again.";
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Message = $"✖ {n.Name} can't be read: {ex.Message}";
                return null;
            }
            if (n.IsFolder)
            {
                foreach (var d in n.Descendants())
                {
                    if (d.Flags.HasFlag(SpaceFlags.NoAccess)) { Message = $"✖ {n.Name} contains folders PerfTuner couldn't read (e.g. {d.Name}), so it can't show you everything that would be affected."; return null; }
                    if (d.Flags.HasFlag(SpaceFlags.ReparsePoint)) { Message = $"✖ {n.Name} contains a link to another place ({d.Name}); handle it in Explorer."; return null; }
                    if (d.Flags.HasFlag(SpaceFlags.System)) { Message = $"🔒 {n.Name} contains a system file ({d.Name})."; return null; }
                    if (Protection(d) is { Level: ProtectionLevel.Locked } dp) { Message = $"🔒 {n.Name} contains {d.Name}: {dp.Reason}"; return null; }
                }
            }
        }
        return top;
    }

    /// <summary>The node still hangs under the current scan's root.</summary>
    private bool IsAttached(SpaceNode n)
    {
        var r = n;
        while (r.Parent is not null) { r = r.Parent; }
        return ReferenceEquals(r, Root);
    }

    private static string Listing(List<SpaceNode> items) =>
        string.Join("\n", items.Take(6).Select(n => $"• {n.Name} ({Format.Bytes(n.Size)})")) + (items.Count > 6 ? $"\n• … and {items.Count - 6} more" : "");

    [RelayCommand]
    private async Task RecycleAsync()
    {
        if (Validated() is not { } items) { return; }
        var bytes = items.Sum(n => n.Size);
        var warn = SelectionWarn ? "\n\n⚠ Apps keep data in some of these. Removing it can reset an app or sign you out." : "";
        if (ThemedDialog.Show($"Send {items.Count} item(s), {Format.Bytes(bytes)}, to the Recycle Bin?\n\n{Listing(items)}{warn}\n\nYou can restore them from the Recycle Bin. Space is only freed once the Recycle Bin is emptied.",
            "Delete (Recycle Bin)", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        await RunAsync(items, () => ShellFileOps.RecycleAsync(items.Select(n => n.FullPath).ToList(), OwnerHandle)).ConfigureAwait(true);
    }

    [ObservableProperty] private bool _permanentConfirmed;

    [RelayCommand]
    private async Task DeletePermanentlyAsync()
    {
        if (Validated() is not { } items) { return; }
        if (!PermanentConfirmed)
        {
            Message = "Tick \"I understand this can't be undone\" first.";
            return;
        }
        var bytes = items.Sum(n => n.Size);
        if (ThemedDialog.Show($"Delete {items.Count} item(s), {Format.Bytes(bytes)}, permanently?\n\n{Listing(items)}\n\nThey do NOT go to the Recycle Bin and can't be brought back.",
            "Delete permanently", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        PermanentConfirmed = false;
        await RunAsync(items, () => ShellFileOps.DeletePermanentlyAsync(items.Select(n => n.FullPath).ToList(), OwnerHandle)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task MoveAsync()
    {
        if (Validated() is not { } items) { return; }
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Move to which folder?", Multiselect = false };
        if (picker.ShowDialog(Application.Current.MainWindow) != true) { return; }
        var dest = picker.FolderName;
        // Destination rules: exact path on a local fixed drive, not protected, no link on the path, not inside what is moved.
        if (PathSafety.CheckDestination(dest, _ctx) is { } why)
        {
            Message = $"✖ Can't move into {dest}: {why}.";
            return;
        }
        if (items.Any(n => ProtectionRules.IsInside(dest, n.FullPath)))
        {
            Message = "✖ A folder can't be moved into itself.";
            return;
        }
        if (items.Any(n => string.Equals(Path.GetDirectoryName(n.FullPath.TrimEnd('\\')), dest.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            Message = "✖ That is where the items already are.";
            return;
        }
        var bytes = items.Sum(n => n.Size);
        if (ThemedDialog.Show($"Move {items.Count} item(s), {Format.Bytes(bytes)}, to\n{dest}?\n\n{Listing(items)}\n\nYou can move them back from History.",
            "Move", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        // Items whose name already exists at the destination get merged by Windows; such moves can't be undone automatically.
        var merged = items.Where(n => File.Exists(Path.Combine(dest, n.Name)) || Directory.Exists(Path.Combine(dest, n.Name))).Select(n => n.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await RunAsync(items, () => ShellFileOps.MoveAsync(items.Select(n => n.FullPath).ToList(), dest, OwnerHandle), dest, merged).ConfigureAwait(true);
    }

    private async Task RunAsync(List<SpaceNode> items, Func<Task<IReadOnlyList<ItemResult>>> operation, string? destination = null, HashSet<string>? merged = null)
    {
        IsBusy = true;
        CanAct = false;
        Message = "Working…";
        try
        {
            var byPath = items.ToDictionary(n => n.FullPath, StringComparer.OrdinalIgnoreCase);
            var results = await operation().ConfigureAwait(true);
            var journal = new FileOpsJournal();
            var parts = new List<string>();
            foreach (var group in results.GroupBy(r => r.Outcome))
            {
                var nodes = group.Select(r => byPath.TryGetValue(r.Path, out var n) ? n : null).OfType<SpaceNode>().ToList();
                var bytes = nodes.Sum(n => n.Size);
                switch (group.Key)
                {
                    case ItemOutcome.Recycled:
                        parts.Add($"{nodes.Count} sent to the Recycle Bin ({Format.Bytes(bytes)})");
                        journal.Record(new FileOpEntry { Kind = FileOpKind.Recycled, Count = nodes.Count, Bytes = bytes, Summary = $"Sent {nodes.Count} item(s), {Format.Bytes(bytes)}, to the Recycle Bin" });
                        break;
                    case ItemOutcome.DeletedPermanently:
                        parts.Add($"{nodes.Count} deleted permanently ({Format.Bytes(bytes)})");
                        journal.Record(new FileOpEntry { Kind = FileOpKind.DeletedPermanently, Count = nodes.Count, Bytes = bytes, Summary = $"Permanently deleted {nodes.Count} item(s), {Format.Bytes(bytes)}" });
                        break;
                    case ItemOutcome.Moved:
                        parts.Add($"{nodes.Count} moved ({Format.Bytes(bytes)})");
                        journal.Record(new FileOpEntry
                        {
                            Kind = FileOpKind.Moved,
                            Count = nodes.Count,
                            Bytes = bytes,
                            Summary = $"Moved {nodes.Count} item(s), {Format.Bytes(bytes)}, from {DriveLetter}: to {destination}",
                            Moves = group.Where(r => r.NewPath is not null).Select(r => new MovedItem(r.Path, r.NewPath!, merged?.Contains(r.Path) == true)).ToList(),
                        });
                        break;
                    case ItemOutcome.Failed:
                        parts.Add($"{group.Count()} not done (cancelled, in use or no permission)");
                        break;
                    case ItemOutcome.Skipped:
                        parts.Add($"{group.Count()} skipped (no longer there)");
                        break;
                }
                if (group.Key is ItemOutcome.Recycled or ItemOutcome.DeletedPermanently or ItemOutcome.Moved or ItemOutcome.Skipped)
                {
                    lock (_treeLock)
                    {
                        foreach (var n in nodes) { _selected.Remove(n); n.Remove(); }
                    }
                }
            }
            if (CurrentFolder is { } cf && !IsAttached(cf))
            {
                CurrentFolder = Root;
                UpdateBreadcrumbs();
            }
            Message = parts.Count == 0 ? "Nothing was changed." : "✔ " + string.Join(" · ", parts) + ".";
            _main.AddActivity("You", Message); // counts and sizes only, no file names
            _main.Cockpit.CoolDown("disk");
            Refresh();
            UpdateSelection();
            _ = RunReportAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Message = "✖ The operation failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            UpdateSelection();
        }
    }

    [RelayCommand]
    private void ShowInExplorer()
    {
        if (_selected.FirstOrDefault() is { } n) { ShellFileOps.ShowInExplorer(n.FullPath); }
    }

    [RelayCommand]
    private async Task RescanFolderAsync()
    {
        if (CurrentFolder is not { Parent: { } parent } folder || IsScanning || IsBusy || !IsAttached(folder)) { return; }
        var rootAtStart = Root;
        var path = folder.FullPath;
        if (!PathSafety.IsCanonical(path) || PathSafety.HasLinkOnPath(path)) { Message = "✖ This folder can't be scanned again safely; scan the whole drive."; return; }
        IsBusy = true;
        try
        {
            var fresh = await SpaceScanner.ScanFolderAsync(path, folder.Name, _cts?.Token ?? CancellationToken.None).ConfigureAwait(true);
            if (!ReferenceEquals(Root, rootAtStart) || folder.Parent != parent) { return; }
            lock (_treeLock) { parent.ReplaceChild(folder, fresh); }
            _selected.Clear();
            CurrentFolder = fresh;
            UpdateBreadcrumbs();
            Refresh();
            _ = RunReportAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message = "✖ " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            UpdateSelection();
        }
    }

    // ------------------------------------------------------------------ reports

    public static IReadOnlyList<string> ReportKinds { get; } = ["Biggest files", "Biggest folders", "Old and big", "Oldest files", "By file type", "Space per file type", "Duplicate files"];
    public static IReadOnlyList<FileCategory> Categories { get; } = Enum.GetValues<FileCategory>();
    public ObservableCollection<ReportRow> ReportRows { get; } = [];
    [ObservableProperty] private string _reportKind = "Biggest files";
    [ObservableProperty] private FileCategory _reportCategory = FileCategory.Video;
    [ObservableProperty] private string _reportNote = "";

    public bool ShowCategoryPicker => ReportKind == "By file type";

    partial void OnReportKindChanged(string value)
    {
        OnPropertyChanged(nameof(ShowCategoryPicker));
        _ = RunReportAsync();
    }

    partial void OnReportCategoryChanged(FileCategory value) => _ = RunReportAsync();

    private int _reportVersion;

    private async Task RunReportAsync()
    {
        if (Root is not { } root || IsScanning) { return; }
        var version = ++_reportVersion;
        var kind = ReportKind;
        if (kind == "Duplicate files") { await RunDuplicateReportAsync(root, version).ConfigureAwait(true); return; }
        var category = ReportCategory;
        var now = DateTime.UtcNow;
        var rows = await Task.Run(() =>
        {
            lock (_treeLock)
            {
            ReportRow Row(SpaceNode n)
            {
                var p = Protection(n);
                return new ReportRow(n, n.Name, Path.GetDirectoryName(n.FullPath) ?? "", Format.Bytes(n.Size),
                    n.LastWriteUtc > DateTime.MinValue.AddYears(1) ? n.LastWriteUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) : "",
                    n.IsFolder ? "Folder" : FileCategories.Title(FileCategories.Of(n.Name)), p.Level == ProtectionLevel.Locked, p.Reason);
            }
            return kind switch
            {
                "Biggest folders" => SpaceReports.BiggestFolders(root, ReportSize).Select(Row).ToList(),
                "Old and big" => SpaceReports.OldAndBig(root, now, ReportSize).Select(Row).ToList(),
                "Oldest files" => SpaceReports.Oldest(root, ReportSize).Select(Row).ToList(),
                "By file type" => SpaceReports.OfCategory(root, category, ReportSize).Select(Row).ToList(),
                "Space per file type" => SpaceReports.Summary(root).Select(t => new ReportRow(null, FileCategories.Title(t.Category), $"{t.Count:N0} files", Format.Bytes(t.Bytes), "", "", true, "")).ToList(),
                _ => SpaceReports.BiggestFiles(root, ReportSize).Select(Row).ToList(),
            };
            }
        }).ConfigureAwait(true);
        if (!ReferenceEquals(root, Root) || version != _reportVersion) { return; }
        ReportRows.Clear();
        foreach (var r in rows) { ReportRows.Add(r); }
        ReportNote = kind switch
        {
            "Old and big" => "Files of 100 MB or more that haven't changed for a year.",
            "Oldest files" => "Files that haven't changed for the longest time.",
            "Space per file type" => "How much space each kind of file uses on this drive.",
            _ => $"Top {ReportSize}.",
        };
    }

    // ------------------------------------------------------------------ duplicates (v0.9)

    private const int DuplicateGroupLimit = 300;

    /// <summary>
    /// Files with identical content. Same size first (from the scan, under the tree lock), then the first 64 KB, then
    /// a full SHA-256 of what is left — read-only, shared access, outside the lock. Each copy set gets a number; keep one
    /// copy and send the others to the Recycle Bin with the normal (protected) delete.
    /// </summary>
    private async Task RunDuplicateReportAsync(SpaceNode root, int version)
    {
        ReportRows.Clear();
        ReportNote = "Comparing files of the same size…";
        List<List<(SpaceNode Node, string Path, long Size)>> candidates;
        lock (_treeLock)
        {
            candidates = Duplicates.SizeGroups(root).Take(DuplicateGroupLimit)
                .Select(g => g.Select(n => (n, n.FullPath, n.Size)).ToList()).ToList();
        }
        var progress = new Progress<int>(i => { if (version == _reportVersion) { ReportNote = $"Comparing contents… {i} of {candidates.Count} size groups"; } });
        var sets = await Task.Run(() => FindIdentical(candidates, progress, () => version != _reportVersion)).ConfigureAwait(true);
        if (!ReferenceEquals(root, Root) || version != _reportVersion) { return; }
        var rows = new List<ReportRow>();
        var set = 0;
        long wasted = 0;
        foreach (var copies in sets)
        {
            set++;
            wasted += copies[0].Size * (copies.Count - 1);
            foreach (var c in copies)
            {
                if (rows.Count >= ReportSize * 2) { break; }
                var p = Protection(c.Node);
                rows.Add(new ReportRow(c.Node, c.Node.Name, System.IO.Path.GetDirectoryName(c.Path) ?? "", Format.Bytes(c.Size),
                    c.Node.LastWriteUtc > DateTime.MinValue.AddYears(1) ? c.Node.LastWriteUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) : "",
                    $"Copy set {set} · {copies.Count} copies", p.Level == ProtectionLevel.Locked, p.Reason));
            }
        }
        ReportRows.Clear();
        foreach (var r in rows) { ReportRows.Add(r); }
        ReportNote = sets.Count == 0
            ? "No identical files of 1 MB or more found."
            : $"{sets.Count} sets of identical files (1 MB or more); the extra copies take {Format.Bytes(wasted)}. Keep one of each set.";
    }

    private static List<List<(SpaceNode Node, string Path, long Size)>> FindIdentical(
        List<List<(SpaceNode Node, string Path, long Size)>> groups, IProgress<int> progress, Func<bool> cancelled)
    {
        var result = new List<List<(SpaceNode Node, string Path, long Size)>>();
        var done = 0;
        foreach (var g in groups)
        {
            if (cancelled()) { break; }
            progress.Report(++done);
            var byHead = g.GroupBy(f => Hash(f.Path, 64 * 1024, cancelled) ?? Guid.NewGuid().ToString()).Where(x => x.Count() > 1);
            foreach (var head in byHead)
            {
                var byFull = head.GroupBy(f => (f.Size <= 64 * 1024 ? head.Key : Hash(f.Path, long.MaxValue, cancelled)) ?? Guid.NewGuid().ToString()).Where(x => x.Count() > 1);
                result.AddRange(byFull.Select(x => x.ToList()));
            }
        }
        return result;
    }

    private static string? Hash(string path, long maxBytes, Func<bool> cancelled)
    {
        try
        {
            using var fs = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.SequentialScan, BufferSize = 0 });
            using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long total = 0;
            int read;
            while (total < maxBytes && (read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, maxBytes - total))) > 0)
            {
                if (cancelled()) { return null; } // a newer report (or another drive) took over: stop reading at once
                sha.AppendData(buffer, 0, read);
                total += read;
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
