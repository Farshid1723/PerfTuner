using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.Controls;
using PerfTuner.Core.Undocumented;
using UVerdict = PerfTuner.Core.Undocumented.Verdict;
using PerfTuner.Platform.Windows.Undocumented;

namespace PerfTuner.App.ViewModels;

public sealed partial class UndocRow(UndocumentedItem item, bool available) : ObservableObject
{
    public UndocumentedItem Item { get; } = item;
    public string Group => Item.Group;
    public string Title => Item.Title;
    public string Description => Item.Description;
    public UVerdict Verdict => Item.Verdict;
    public string VerdictText => UndocumentedCatalog.VerdictText(Item.Verdict);
    public string VerdictNote => Item.VerdictNote;
    public bool HasVerdictNote => Item.VerdictNote.Length > 0;
    public bool Available { get; } = available;
    public bool IsAction => Item.Kind == UndocKind.Action;
    public bool IsToggle => !IsAction;
    public string Meta =>
        (Available ? $"Takes effect: {Item.TakesEffect}" : "Not on this Windows: " + Item.BuildNote)
        + (Item.RequiresAdmin ? " · needs full power" : "")
        + (IsAction ? " · can't be undone" : "");

    [ObservableProperty] private bool _applied;
    [ObservableProperty] private bool _known = true;

    public bool CanChange => Available && Known;
    partial void OnKnownChanged(bool value) => OnPropertyChanged(nameof(CanChange));
}

/// <summary>
/// The Undocumented page: settings Microsoft never documented (and a few documented ones that
/// are widely misused), each with an honest verdict. Opt-in behind a warning; every change
/// asks first, risky ones twice, and everything except the one-off actions can be undone.
/// </summary>
public sealed partial class UndocumentedViewModel(MainViewModel main) : ObservableObject
{
    private bool _loaded;

    public ObservableCollection<UndocRow> Rows { get; } = [];

    [ObservableProperty] private bool _accepted = GuiSettings.Current.UndocumentedAccepted;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _message = "";

    public string BuildText => $"Windows build {UndocumentedTweaks.Build}";

    public void SetActive(bool active)
    {
        if (!active || _loaded || !Accepted) { return; }
        _loaded = true;
        _ = RefreshAsync();
    }

    /// <summary>Shows the list for this session only (nothing saved).</summary>
    internal void Preview()
    {
        Accepted = true;
        _loaded = true;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void Accept()
    {
        Accepted = true;
        GuiSettings.Update(g => g with { UndocumentedAccepted = true });
        _loaded = true;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Busy = true;
        try
        {
            var elevated = main.IsElevatedProcess;
            var states = await Task.Run(() =>
            {
                IReadOnlyDictionary<string, string>? boot = null;
                string? bootError = null;
                if (elevated)
                {
                    try { boot = BootSettings.Read(); }
                    catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { bootError = ex.Message; }
                }
                var list = new List<(UndocumentedItem Item, bool Applied, bool Known)>();
                foreach (var item in UndocumentedCatalog.All)
                {
                    if (item.Kind == UndocKind.Desktop) { list.Add((item, DesktopPreference(item.Id), true)); continue; }
                    if (item.Kind == UndocKind.Boot && boot is null) { list.Add((item, false, false)); continue; }
                    bool applied;
                    try { applied = item.Kind != UndocKind.Action && UndocumentedTweaks.IsApplied(item, boot); }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { applied = false; }
                    list.Add((item, applied, true));
                }
                return (list, bootError);
            }).ConfigureAwait(true);

            Rows.Clear();
            foreach (var (item, applied, known) in states.list)
            {
                Rows.Add(new UndocRow(item, item.AppliesTo(UndocumentedTweaks.Build)) { Applied = applied, Known = known });
            }
            if (!elevated) { Message = "Boot settings and most machine-wide items need full power (administrator)."; }
            else if (states.bootError is not null) { Message = "Boot settings couldn't be read: " + states.bootError; }
        }
        finally { Busy = false; }
    }

    private static bool DesktopPreference(string id) => id switch
    {
        "vd-tray" => GuiSettings.Current.DesktopTray,
        "vd-overlay" => GuiSettings.Current.DesktopOverlay,
        "vd-hotkeys" => GuiSettings.Current.DesktopHotkeys,
        _ => false,
    };

    [RelayCommand]
    private async Task ToggleAsync(UndocRow? row)
    {
        if (row is null) { return; }
        if (Busy) { Reset(row); return; }
        var item = row.Item;
        var on = !row.Applied;
        if (!row.CanChange) { Reset(row); return; }
        if (item.RequiresAdmin && !main.IsElevatedProcess)
        {
            Message = $"\"{item.Title}\" needs full power (Unlock full power).";
            Reset(row);
            return;
        }

        if (item.Kind == UndocKind.Desktop)
        {
            GuiSettings.Update(g => item.Id switch
            {
                "vd-tray" => g with { DesktopTray = on },
                "vd-overlay" => g with { DesktopOverlay = on },
                _ => g with { DesktopHotkeys = on },
            });
            main.ApplyDesktopHelpers();
            row.Applied = on;
            Message = $"✔ {item.Title}: {(on ? "on" : "off")}.";
            return;
        }

        if (on && !Confirm(item)) { Reset(row); return; }

        Busy = true;
        try
        {
            var journal = main.SystemSettings.Journal;
            var machine = main.SystemSettings.MachineSettings;
            if (on)
            {
                await Task.Run(() => UndocumentedTweaks.Apply(item, journal, machine)).ConfigureAwait(true);
                Message = $"✔ {item.Title}: on. Takes effect: {item.TakesEffect}. Switch it off here or in History to undo.";
                main.AddActivity("You", $"Undocumented: {item.Title} on");
            }
            else
            {
                var undone = await Task.Run(() => UndocumentedTweaks.Undo(item, journal, machine)).ConfigureAwait(true);
                Message = undone == 0
                    ? $"{item.Title} was set outside PerfTuner, so there's nothing of PerfTuner's to undo."
                    : $"✔ {item.Title}: previous setting restored.";
                if (undone > 0) { main.AddActivity("You", $"Undocumented: {item.Title} undone"); }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException or TimeoutException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            Message = $"✖ {item.Title}: {ex.Message}";
        }
        finally { Busy = false; }
        await RefreshOneAsync(row).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RunActionAsync(UndocRow? row)
    {
        if (row is not { IsAction: true } || Busy) { return; }
        var item = row.Item;
        if (item.RequiresAdmin && !main.IsElevatedProcess) { Message = $"\"{item.Title}\" needs full power (Unlock full power)."; return; }
        if (!Confirm(item)) { return; }
        Busy = true;
        try
        {
            var result = await Task.Run(() => UndocumentedTweaks.RunAction(item)).ConfigureAwait(true);
            Message = "✔ " + result;
            main.AddActivity("You", $"Undocumented: {item.Title}. {result}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            Message = $"✖ {item.Title}: {ex.Message}";
        }
        finally { Busy = false; }
    }

    /// <summary>Every change asks; risky and security-risk items ask a second time with the reason spelled out.</summary>
    private static bool Confirm(UndocumentedItem item)
    {
        var verdict = UndocumentedCatalog.VerdictText(item.Verdict);
        var text = $"{item.Title}\n\n{item.Description}\n\nVerdict: {verdict}." + (item.VerdictNote.Length > 0 ? " " + item.VerdictNote : "")
            + $"\n\nTakes effect: {item.TakesEffect}.\n\n"
            + (item.Undoable ? "Microsoft doesn't document this. You can switch it off again here or undo it in History." : "This can't be undone.")
            + "\n\nContinue?";
        if (ThemedDialog.Show(text, "Undocumented setting", MessageBoxButton.YesNo, item.Dangerous ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return false;
        }
        if (!item.Dangerous) { return true; }
        var second = item.Verdict == UVerdict.SecurityRisk
            ? $"Last check: \"{item.Title}\" lowers this PC's protection against malicious code.\n\nTurn it on anyway?"
            : $"Last check: \"{item.Title}\" usually makes things worse.\n\n{item.VerdictNote}\n\nDo it anyway?";
        return ThemedDialog.Show(second, "Are you sure?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private static void Reset(UndocRow row)
    {
        // The switch flipped visually before the command ran: put it back.
        var applied = row.Applied;
        row.Applied = !applied;
        row.Applied = applied;
    }

    private async Task RefreshOneAsync(UndocRow row)
    {
        try
        {
            var elevated = main.IsElevatedProcess;
            var applied = await Task.Run(() =>
            {
                var boot = row.Item.Kind == UndocKind.Boot && elevated ? BootSettings.Read() : null;
                return UndocumentedTweaks.IsApplied(row.Item, boot);
            }).ConfigureAwait(true);
            row.Applied = !applied;
            row.Applied = applied;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Security.SecurityException or UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        {
            Reset(row);
        }
    }
}
