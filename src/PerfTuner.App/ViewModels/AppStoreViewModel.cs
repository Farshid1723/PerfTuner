using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.Controls;
using PerfTuner.Core.Apps;
using PerfTuner.Platform.Windows.Apps;

namespace PerfTuner.App.ViewModels;

/// <summary>One app tile on the Get apps page.</summary>
public sealed partial class CatalogRow(CatalogApp app) : ObservableObject
{
    public CatalogApp App { get; } = app;
    public string Name => App.Name;
    public string Description => App.Description;
    public string Category => App.Category;
    public bool OpenSource => App.OpenSource;
    public string Initials { get; } = Monogram.Initials(app.Name);
    public Brush BadgeBrush { get; } = Monogram.BrushFor(app.Id);
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isFocused;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private InstallState _state;
    [ObservableProperty] private InstalledEntry? _local;

    public bool IsInstalled => State != InstallState.NotInstalled;
    public bool IsManaged => State == InstallState.Managed;
    public bool IsElsewhere => State == InstallState.InstalledElsewhere;
    public bool HasIcon => Icon is not null;
    public bool NoIcon => Icon is null;
    public string PackageText => $"Package: {App.Id}" + (App.OpenSource ? " · open source" : "");

    public string StateText => State switch
    {
        InstallState.Managed => "Installed",
        InstallState.InstalledElsewhere => "Installed (not via Windows Package Manager)",
        _ => "Not installed",
    };

    public string FoundText => Local is null ? "" : (Local.Source switch
    {
        InstallSource.InstalledPrograms => "Listed in Settings › Apps › Installed apps",
        InstallSource.StorePackage => "Installed as a Microsoft Store / MSIX package",
        _ => "Found as a Start-menu or desktop shortcut",
    }) + (Local.Version is { Length: > 0 } v ? $" · version {v}" : "");

    partial void OnStateChanged(InstallState value)
    {
        OnPropertyChanged(nameof(IsInstalled)); OnPropertyChanged(nameof(IsManaged)); OnPropertyChanged(nameof(IsElsewhere)); OnPropertyChanged(nameof(StateText));
    }

    partial void OnIconChanged(ImageSource? value) { OnPropertyChanged(nameof(HasIcon)); OnPropertyChanged(nameof(NoIcon)); }
    partial void OnLocalChanged(InstalledEntry? value) => OnPropertyChanged(nameof(FoundText));
}

/// <summary>
/// "Get apps" (v0.10): install, update and remove apps from PerfTuner's built-in catalog with Microsoft's Windows
/// Package Manager. Choose apps, review the list, and PerfTuner runs them one after another, showing winget's output.
/// </summary>
public sealed partial class AppStoreViewModel(MainViewModel main) : ObservableObject
{
    private readonly List<CatalogRow> _all = AppCatalog.Apps.Select(a => new CatalogRow(a)).ToList();
    private readonly Dictionary<string, IReadOnlyList<string>> _shown = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _showCancel;
    private bool _checkedOnce;

    public IReadOnlyList<string> Categories { get; } = ["All", .. AppCatalog.Categories];
    public ObservableCollection<CatalogRow> Rows { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public ObservableCollection<string> DetailLines { get; } = [];

    [ObservableProperty] private string _selectedCategory = "All";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _installedOnly;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _progress = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private CatalogRow? _focused;
    public bool HasFocused => Focused is not null;
    public bool NothingFocused => Focused is null;
    [ObservableProperty] private bool _detailBusy;
    [ObservableProperty] private bool _logOpen;

    public void SetActive(bool active)
    {
        if (Rows.Count == 0) { ApplyFilter(); }
        if (active && !_checkedOnce) { _checkedOnce = true; _ = CheckInstalledAsync(); }
    }

    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();
    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnInstalledOnlyChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var category = SelectedCategory == "All" ? null : SelectedCategory;
        var ids = AppCatalog.Filter(Search, category).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        Rows.Clear();
        foreach (var r in _all.Where(r => ids.Contains(r.App.Id) && (!InstalledOnly || r.IsInstalled)))
        {
            Rows.Add(r);
        }
    }

    [RelayCommand]
    private void Toggle(CatalogRow? row)
    {
        if (row is null) { return; }
        if (!Busy)
        {
            row.IsSelected = !row.IsSelected;
            SelectedCount = _all.Count(r => r.IsSelected);
        }
        Focused = row;
    }

    /// <summary>Selects one catalog app (startup argument for screenshots).</summary>
    public void Preselect(string id)
    {
        if (_all.FirstOrDefault(r => r.App.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } row) { Toggle(row); }
    }

    partial void OnFocusedChanged(CatalogRow? oldValue, CatalogRow? newValue)
    {
        OnPropertyChanged(nameof(HasFocused));
        OnPropertyChanged(nameof(NothingFocused));
        if (oldValue is not null) { oldValue.IsFocused = false; }
        if (newValue is null) { return; }
        newValue.IsFocused = true;
        _ = LoadDetailsAsync(newValue);
    }

    /// <summary>The package repository's own description of the focused app (cached per app for this session).</summary>
    private async Task LoadDetailsAsync(CatalogRow row)
    {
        _showCancel?.Cancel();
        DetailLines.Clear();
        if (_shown.TryGetValue(row.App.Id, out var cached)) { foreach (var l in cached) { DetailLines.Add(l); } return; }
        var cancel = _showCancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        DetailBusy = true;
        try
        {
            var lines = await Task.Run(() => WinGet.ShowAsync(row.App.Id, cancel.Token)).ConfigureAwait(true);
            _shown[row.App.Id] = lines;
            if (ReferenceEquals(Focused, row)) { foreach (var l in lines) { DetailLines.Add(l); } }
        }
        catch (OperationCanceledException)
        {
            // another app was clicked, or no answer in time
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (ReferenceEquals(Focused, row)) { DetailLines.Add("Package details aren't available: " + ex.Message); }
        }
        finally
        {
            if (ReferenceEquals(_showCancel, cancel)) { DetailBusy = false; }
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in _all) { r.IsSelected = false; }
        SelectedCount = 0;
    }

    [RelayCommand]
    private async Task CheckInstalledAsync()
    {
        if (Busy) { return; }
        Busy = true;
        Progress = "Checking what's installed…";
        string? wingetProblem = null;
        try
        {
            var localTask = Task.Run(InstalledApps.Scan);
            IReadOnlySet<string> managed = new HashSet<string>();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                managed = await Task.Run(() => WinGet.InstalledCatalogIdsAsync(timeout.Token)).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { wingetProblem = "Windows Package Manager didn't answer within 3 minutes"; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
            {
                wingetProblem = ex.Message;
            }
            var local = await localTask.ConfigureAwait(true);
            foreach (var r in _all)
            {
                var found = InstalledMatching.Find(r.App, local);
                r.Local = found;
                r.State = managed.Contains(r.App.Id) ? InstallState.Managed : found is not null ? InstallState.InstalledElsewhere : InstallState.NotInstalled;
            }
            var installed = _all.Count(r => r.IsInstalled);
            var elsewhere = _all.Count(r => r.IsElsewhere);
            Message = $"{installed} of the {_all.Count} apps are installed" + (elsewhere > 0 ? $" ({elsewhere} outside Windows Package Manager: they usually update themselves)." : ".")
                + (wingetProblem is null ? "" : $" Windows Package Manager: {wingetProblem}.");
            ApplyFilter();
            var withIcons = _all.Where(r => r.Local is not null).ToList();
            var icons = await Task.Run(() => withIcons.Select(r => (r, AppIcons.Load(r.Local))).ToList()).ConfigureAwait(true);
            foreach (var (r, icon) in icons) { r.Icon = icon; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Message = "✖ " + ex.Message;
        }
        finally
        {
            Busy = false;
            Progress = "";
        }
    }

    [RelayCommand] private Task InstallAsync() => RunSelectedAsync(WinGetAction.Install);
    [RelayCommand] private Task UpgradeAsync() => RunSelectedAsync(WinGetAction.Upgrade);
    [RelayCommand] private Task UninstallAsync() => RunSelectedAsync(WinGetAction.Uninstall);

    [RelayCommand]
    private async Task UpgradeAllAsync()
    {
        if (Busy || !Ready()) { return; }
        if (ThemedDialog.Show(
                "Update every app Windows Package Manager can update — including apps that aren't in PerfTuner's list?\n\nInstallers run silently, one after another; some apps may close while they update.",
                "Update all apps", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        await RunAsync([(null, "Updating all apps")], WinGetAction.UpgradeAll).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Stop()
    {
        _stop?.Cancel();
        Progress = "Stopping after the current app…";
    }

    private bool Ready()
    {
        if (!main.IsElevatedProcess) { Message = "Installing apps needs full power (administrator)."; return false; }
        return true;
    }

    private async Task RunSelectedAsync(WinGetAction action)
    {
        if (Busy || !Ready()) { return; }
        var chosen = _all.Where(r => r.IsSelected).ToList();
        if (chosen.Count == 0) { Message = "Select one or more apps first (click a tile)."; return; }
        var skipped = new List<CatalogRow>();
        if (action is WinGetAction.Upgrade or WinGetAction.Uninstall)
        {
            // Windows Package Manager can only update or remove what it can connect to one of its packages.
            skipped = chosen.Where(r => !r.IsManaged).ToList();
            foreach (var r in skipped)
            {
                r.Status = r.IsElsewhere
                    ? (action == WinGetAction.Upgrade ? "Installed outside Windows Package Manager: it updates itself, or use its own Check for updates." : "Installed outside Windows Package Manager: remove it in Settings › Apps › Installed apps.")
                    : "Not installed.";
            }
            chosen = chosen.Where(r => r.IsManaged).ToList();
            if (chosen.Count == 0)
            {
                Message = action == WinGetAction.Upgrade
                    ? "None of the selected apps is managed by Windows Package Manager, so it can't update them. They were installed another way and usually update themselves (see Details)."
                    : "None of the selected apps is managed by Windows Package Manager. Remove them in Settings › Apps › Installed apps (see Details).";
                Focused = skipped[0];
                return;
            }
        }
        var verb = action switch { WinGetAction.Install => "Install", WinGetAction.Upgrade => "Update", _ => "Uninstall" };
        var names = string.Join("\n", chosen.Take(12).Select(r => "• " + r.Name)) + (chosen.Count > 12 ? $"\n• … and {chosen.Count - 12} more" : "");
        var detail = action == WinGetAction.Uninstall
            ? "Each app's own uninstaller runs silently. Their settings and files in your user folder usually stay."
            : "Downloads come from Microsoft's Windows Package Manager repository; each app's licence is its publisher's. Installers run silently, one after another.";
        if (skipped.Count > 0) { detail += $"\n\nSkipped ({skipped.Count}): " + string.Join(", ", skipped.Take(6).Select(r => r.Name)) + " — not installed through Windows Package Manager."; }
        if (action == WinGetAction.Install && chosen.Any(r => r.IsElsewhere)) { detail += "\n\nAlready on this PC (installed another way): " + string.Join(", ", chosen.Where(r => r.IsElsewhere).Take(6).Select(r => r.Name)) + ". Installing again may add a second copy."; }
        if (ThemedDialog.Show($"{verb} {chosen.Count} app(s)?\n\n{names}\n\n{detail}", $"{verb} apps", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        await RunAsync(chosen.Select(r => ((CatalogRow?)r, r.Name)).ToList(), action).ConfigureAwait(true);
    }

    private async Task RunAsync(IReadOnlyList<(CatalogRow? Row, string Name)> jobs, WinGetAction action)
    {
        Busy = true;
        LogOpen = true;
        Log.Clear();
        _stop = new CancellationTokenSource();
        var ok = 0;
        var verb = action switch { WinGetAction.Install => "Installing", WinGetAction.Uninstall => "Removing", _ => "Updating" };
        try
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                if (_stop.IsCancellationRequested) { AddLog("Stopped before the remaining apps."); break; }
                var (row, name) = jobs[i];
                Progress = jobs.Count > 1 ? $"{verb} {i + 1} of {jobs.Count}: {name}…" : $"{verb} {name}…";
                if (row is not null) { row.Status = "Working…"; }
                AddLog($"— {name}");
                WinGetResult result;
                try
                {
                    result = await Task.Run(() => WinGet.RunAsync(action, row?.App.Id, line => Application.Current.Dispatcher.BeginInvoke(() => AddLog("   " + line)))).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or IOException)
                {
                    result = new WinGetResult(-1, ex.Message);
                }
                var good = WinGetCommands.CountsAsSuccess(result.ExitCode);
                if (good) { ok++; }
                if (row is not null)
                {
                    row.Status = (good ? "✔ " : "✖ ") + result.Summary;
                    if (good) { row.State = action == WinGetAction.Uninstall ? InstallState.NotInstalled : InstallState.Managed; row.IsSelected = false; }
                }
                AddLog($"   {(good ? "✔" : "✖")} {result.Summary}");
            }
            SelectedCount = _all.Count(r => r.IsSelected);
            Message = $"{ok} of {jobs.Count} done." + (ok < jobs.Count ? " See the activity log below." : "");
            main.AddActivity("You", $"Apps: {verb.ToLowerInvariant()} — {ok} of {jobs.Count} done");
        }
        finally
        {
            Busy = false;
            Progress = "";
            _stop.Dispose();
            _stop = null;
            ApplyFilter();
        }
    }

    private void AddLog(string line)
    {
        Log.Add(line);
        while (Log.Count > 400) { Log.RemoveAt(0); }
    }
}
