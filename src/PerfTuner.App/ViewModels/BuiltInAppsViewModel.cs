using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.Controls;
using PerfTuner.Platform.Windows.Apps;

namespace PerfTuner.App.ViewModels;

public sealed partial class BuiltInRow(BuiltInApp app, InstalledPackage package) : ObservableObject
{
    public BuiltInApp App { get; } = app;
    public InstalledPackage Package { get; } = package;
    public string Title => App.Title;
    public string Note => App.Note;
    public bool HasNote => App.Note.Length > 0;
    [ObservableProperty] private bool _selected;
}

/// <summary>Get apps page: remove built-in Windows apps (current user) and repair Microsoft Store.</summary>
public sealed partial class BuiltInAppsViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<BuiltInRow> Apps { get; } = [];
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _progress = "";
    [ObservableProperty] private bool _open;

    partial void OnOpenChanged(bool value)
    {
        if (value && !Loaded && !Busy) { _ = LoadAsync(); }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        Busy = true;
        try
        {
            var list = await Task.Run(AppPackages.InstalledBuiltIns).ConfigureAwait(true);
            Apps.Clear();
            foreach (var (app, package) in list) { Apps.Add(new BuiltInRow(app, package)); }
            Loaded = true;
            Message = Apps.Count == 0 ? "None of the removable built-in apps are installed for you." : "";
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var chosen = Apps.Where(a => a.Selected).ToList();
        if (Busy || chosen.Count == 0) { Message = chosen.Count == 0 ? "Tick the apps you want to remove first." : Message; return; }
        var names = string.Join("\n", chosen.Select(c => "• " + c.Title + (c.HasNote ? $" ({c.Note})" : "")));
        if (ThemedDialog.Show($"Remove these built-in apps for your account?\n\n{names}\n\nThey can be installed again from Microsoft Store. PerfTuner can't undo this itself. Other accounts on this PC keep them.",
                "Remove built-in apps", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        Busy = true;
        var failed = new List<string>();
        var removed = 0;
        try
        {
            for (var i = 0; i < chosen.Count; i++)
            {
                var row = chosen[i];
                Progress = $"Removing {row.Title} ({i + 1} of {chosen.Count})…";
                try
                {
                    await Task.Run(() => AppPackages.Remove(row.Package)).ConfigureAwait(true);
                    removed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
                {
                    failed.Add($"{row.Title}: {ex.Message}");
                }
            }
            Message = $"✔ Removed {removed} app(s)." + (failed.Count > 0 ? " Not removed: " + string.Join("; ", failed) : "");
            main.AddActivity("You", Message);
        }
        finally
        {
            Busy = false;
            Progress = "";
        }
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ResetStoreCache()
    {
        if (ThemedDialog.Show("Clear Microsoft Store's cache?\n\nWindows' own WSReset runs; a blank window appears for a few seconds and then the Store opens. Your apps and purchases aren't affected.",
                "Repair Microsoft Store", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        try
        {
            AppPackages.ResetStoreCache();
            Message = "✔ WSReset started; the Store opens when it's done.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Message = "✖ " + ex.Message;
        }
    }

    [RelayCommand]
    private Task ReregisterStoreAsync() => RunAsync(
        "Re-register Microsoft Store?\n\nWindows registers the Store again from its installed files. Fixes a Store that doesn't open or closes right away. The Store closes if it's open.",
        "Re-register Microsoft Store", () => { AppPackages.ReregisterStore(); return "✔ Microsoft Store re-registered."; });

    [RelayCommand]
    private Task ReregisterAllAsync() => RunAsync(
        "Re-register all your apps?\n\nWindows registers every Store and built-in app of your account again from its installed files. Fixes Start-menu tiles and built-in apps that don't open. Takes a few minutes; open apps are closed.",
        "Re-register all apps", () =>
        {
            var d = Application.Current.Dispatcher;
            var (ok, failed) = AppPackages.ReregisterAll((n, total) => d.BeginInvoke(() => Progress = $"Re-registering {n} of {total}…"));
            return $"✔ Re-registered {ok} app(s)" + (failed > 0 ? $"; {failed} couldn't be (usually system parts that don't need it)." : ".");
        });

    private async Task RunAsync(string question, string title, Func<string> action)
    {
        if (Busy) { return; }
        if (ThemedDialog.Show(question, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        Busy = true;
        try
        {
            Message = title + "…";
            Message = await Task.Run(action).ConfigureAwait(true);
            main.AddActivity("You", Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.Runtime.InteropServices.COMException)
        {
            Message = $"✖ {title}: {ex.Message}";
        }
        finally
        {
            Busy = false;
            Progress = "";
        }
    }
}
