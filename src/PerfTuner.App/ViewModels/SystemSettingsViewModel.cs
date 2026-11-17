using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.SystemTools;
using PerfTuner.Platform.Windows.Settings;

namespace PerfTuner.App.ViewModels;

public sealed record BoostOption(uint Value, string Name);

public sealed record TweakItem(WindowsTweak Tweak, bool Applied)
{
    public string Title => Tweak.Title;
    public string Description => Tweak.Description;
    public string Category => Tweak.Category;
    public string Meta => $"{Tweak.Category} · takes effect: {Tweak.TakesEffect}" + (Tweak.RequiresAdmin ? " · needs full power" : "");
    public string StateText => Applied ? "On" : "Off";
}

/// <summary>One junk source on the Clean page (or the Recycle Bin), with its measured size.</summary>
public sealed partial class JunkItem : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool NeedsAdmin { get; init; }
    public Platform.Windows.Cleanup.JunkSource? Source { get; init; }

    [ObservableProperty] private bool _selected = true;
    [ObservableProperty] private long _bytes;
    [ObservableProperty] private string _sizeText = "–";
}

public sealed partial class StartupItem : ObservableObject
{
    public required StartupApp App { get; init; }
    public string Name => App.Name;
    public string Location => App.LocationText;
    public string Command => App.Command;
    public string State => App.Enabled ? "Enabled" : "Disabled";
    public string ToggleText => App.Enabled ? "Disable" : "Enable";
}

/// <summary>
/// System tab: persistent Windows settings changed directly by this (elevated) app, each
/// journaled in the per-user settings journal so it can be reverted from the Changes tab.
/// </summary>
public sealed partial class SystemSettingsViewModel : ObservableObject
{
    private static readonly TimeSpan TempMinAge = TimeSpan.FromDays(2);
    private readonly MainViewModel _main;

    public SystemSettingsViewModel(MainViewModel main)
    {
        _main = main;
        Journal = new SettingsJournal();
        BoostOptions = [.. PowerSettings.BoostModes.Select(b => new BoostOption(b.Value, b.Name))];
    }

    public SettingsJournal Journal { get; }
    public bool IsElevated => _main.IsElevatedProcess;
    public ObservableCollection<PowerPlan> PowerPlans { get; } = [];
    public IReadOnlyList<BoostOption> BoostOptions { get; }
    public ObservableCollection<StartupItem> StartupApps { get; } = [];
    public ObservableCollection<VolumeInfo> Volumes { get; } = [];
    public ObservableCollection<TweakItem> Tweaks { get; } = [];
    public ObservableCollection<JunkItem> Junk { get; } = [];

    [ObservableProperty] private string _junkSummary = "Press Scan to see how much space you can get back.";
    [ObservableProperty] private string _junkTotal = "";
    [ObservableProperty] private bool _junkScanned;

    [ObservableProperty] private string _pointerPrecisionState = "";

    [ObservableProperty] private PowerPlan? _selectedPlan;
    [ObservableProperty] private BoostOption? _selectedBoost;
    [ObservableProperty] private double _minProcessorState;
    [ObservableProperty] private double _maxProcessorState = 100;
    [ObservableProperty] private string _visualEffectsState = "";
    [ObservableProperty] private bool _transparencyOn;
    [ObservableProperty] private VolumeInfo? _selectedVolume;
    [ObservableProperty] private string _driveStatus = "";
    [ObservableProperty] private string _tempStatus = "Scan to see how much can be freed.";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _busy;

    public void Refresh()
    {
        try
        {
            PowerPlans.Clear();
            foreach (var p in PowerSettings.List()) { PowerPlans.Add(p); }
            SelectedPlan = PowerPlans.FirstOrDefault(p => p.Active);

            var proc = PowerSettings.ReadProcessor(PowerSettings.GetActive());
            SelectedBoost = BoostOptions.FirstOrDefault(b => b.Value == proc.BoostAc) ?? BoostOptions[2];
            MinProcessorState = proc.MinAc;
            MaxProcessorState = proc.MaxAc;

            var fx = VisualEffects.Capture();
            VisualEffectsState = VisualEffects.IsBestPerformance(fx) ? "Animations and shadows are OFF (best performance)." : "Animations and shadows are ON (Windows default look).";
            TransparencyOn = Transparency.Get() != 0;

            ReloadStartup();

            Tweaks.Clear();
            foreach (var t in WindowsTweaks.Catalog)
            {
                Tweaks.Add(new TweakItem(t, WindowsTweaks.IsApplied(t)));
            }
            PointerPrecisionState = WindowsTweaks.PointerPrecisionEnabled()
                ? "Enhance pointer precision is ON (mouse acceleration)."
                : "Enhance pointer precision is OFF (raw 1:1 mouse movement).";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Message = "Could not read settings: " + ex.Message;
        }
        _ = LoadVolumesAsync();
    }

    private void ReloadStartup()
    {
        StartupApps.Clear();
        foreach (var app in Platform.Windows.Settings.StartupApps.List())
        {
            StartupApps.Add(new StartupItem { App = app });
        }
    }

    private async Task LoadVolumesAsync()
    {
        try
        {
            var volumes = await Task.Run(DriveOptimizer.ListFixedVolumes).ConfigureAwait(true);
            Volumes.Clear();
            foreach (var v in volumes) { Volumes.Add(v); }
            SelectedVolume ??= Volumes.FirstOrDefault();
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            DriveStatus = "Drive list unavailable: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- power

    [RelayCommand]
    private void ApplyPowerPlan()
    {
        if (SelectedPlan is not { } plan) { return; }
        Run(() =>
        {
            var previous = PowerSettings.GetActive();
            if (previous == plan.Id) { return "That plan is already active."; }
            PowerSettings.SetActive(plan.Id);
            Journal.Record(new SettingChange { Kind = SettingKind.PowerPlan, Scheme = previous, Description = $"Power plan → {plan.Name}" });
            return $"Power plan switched to {plan.Name}.";
        });
    }

    [RelayCommand]
    private void ApplyProcessorPower()
    {
        Run(() =>
        {
            var scheme = PowerSettings.GetActive();
            var before = PowerSettings.ReadProcessor(scheme);
            var boost = SelectedBoost?.Value ?? before.BoostAc;
            var min = (uint)Math.Round(MinProcessorState);
            var max = (uint)Math.Round(MaxProcessorState);
            if (min > max) { return "Minimum processor state must not exceed the maximum."; }
            PowerSettings.WriteProcessor(scheme, new ProcessorPower(boost, boost, min, min, max, max));
            Journal.Record(new SettingChange
            {
                Kind = SettingKind.ProcessorPower,
                Scheme = scheme,
                ProcessorValues = [before.BoostAc, before.BoostDc, before.MinAc, before.MinDc, before.MaxAc, before.MaxDc],
                Description = $"Processor: boost {SelectedBoost?.Name ?? boost.ToString(CultureInfo.InvariantCulture)}, min {min}%, max {max}%",
            });
            return "Processor power settings applied to the active plan (plugged in and on battery).";
        });
    }

    // ---------------------------------------------------------------- appearance

    [RelayCommand]
    private void BestPerformanceVisuals()
    {
        Run(() =>
        {
            var before = VisualEffects.Capture();
            if (VisualEffects.IsBestPerformance(before)) { return "Visual effects are already set for best performance."; }
            VisualEffects.ApplyBestPerformance();
            Journal.Record(new SettingChange { Kind = SettingKind.VisualEffects, VisualValues = before, Description = "Visual effects → best performance" });
            return "Animations, fades and shadows turned off. Revert any time on the Changes tab.";
        });
    }

    [RelayCommand]
    private void ToggleTransparency()
    {
        Run(() =>
        {
            var before = Transparency.Get();
            var turnOn = before == 0;
            Transparency.Set(turnOn ? 1 : 0);
            Journal.Record(new SettingChange { Kind = SettingKind.Transparency, TransparencyValue = before, Description = $"Transparency effects → {(turnOn ? "on" : "off")}" });
            return $"Transparency effects {(turnOn ? "on" : "off")}.";
        });
    }

    // ---------------------------------------------------------------- Windows 11 switches

    /// <summary>Turns a Windows tweak on (journaled) or off (restores exactly what was there before).</summary>
    [RelayCommand]
    private void ToggleTweak(TweakItem? item)
    {
        if (item is null) { return; }
        if (item.Tweak.RequiresAdmin && !IsElevated)
        {
            Message = $"\"{item.Title}\" needs full power (Unlock full power).";
            return;
        }
        if (!item.Applied)
        {
            if (item.Tweak.Id == "fast-startup" && !WindowsTweaks.HibernationAvailable())
            {
                Message = "Fast Startup needs hibernation, which is turned off on this PC.";
                return;
            }
            Run(() =>
            {
                WindowsTweaks.Apply(item.Tweak, Journal);
                return $"✔ {item.Title} is on. Takes effect: {item.Tweak.TakesEffect}.";
            });
            return;
        }
        Run(() =>
        {
            var ids = Journal.Entries
                .Where(e => e.Kind == SettingKind.RegistryValue && item.Tweak.Values.Any(v => v.KeyPath == e.RegPath && v.ValueName == e.RegName))
                .Select(e => e.Id).Reverse().ToList();
            if (ids.Count == 0) { return $"{item.Title} was set outside PerfTuner, so there is nothing to undo here."; }
            foreach (var id in ids) { Journal.Revert(id); }
            return $"✔ {item.Title} is off again (previous value restored).";
        });
    }

    [RelayCommand]
    private void DisablePointerPrecision()
    {
        Run(() =>
        {
            if (!WindowsTweaks.PointerPrecisionEnabled()) { return "Enhance pointer precision is already off."; }
            WindowsTweaks.DisablePointerPrecision(Journal);
            return "Enhance pointer precision turned off — mouse movement is now 1:1.";
        });
    }

    // ---------------------------------------------------------------- startup apps

    [RelayCommand]
    private void ToggleStartup(StartupItem? item)
    {
        if (item is null) { return; }
        if (item.App.RequiresAdmin && !IsElevated)
        {
            Message = $"\"{item.Name}\" starts for all users; changing it needs administrator rights (Restart as administrator).";
            return;
        }
        Run(() =>
        {
            var enable = !item.App.Enabled;
            var previous = Platform.Windows.Settings.StartupApps.SetEnabled(item.App, enable);
            Journal.Record(new SettingChange
            {
                Kind = SettingKind.StartupApp, StartupLocation = item.App.Location, StartupName = item.App.Name, StartupOriginal = previous,
                Description = $"Startup app \"{item.Name}\" → {(enable ? "enabled" : "disabled")}",
            });
            return $"\"{item.Name}\" will {(enable ? "" : "no longer ")}start with Windows.";
        });
    }

    // ---------------------------------------------------------------- maintenance

    [RelayCommand]
    private async Task OptimizeDriveAsync()
    {
        if (SelectedVolume is not { } volume) { return; }
        if (!IsElevated)
        {
            DriveStatus = "Optimizing drives needs administrator rights (Restart as administrator).";
            return;
        }
        Busy = true;
        DriveStatus = $"Optimizing {volume.Letter}: … (SSD: seconds; HDD: can take many minutes)";
        try
        {
            DriveStatus = await Task.Run(() => DriveOptimizer.Optimize(volume.Letter)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            DriveStatus = "Optimization failed: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    // ---------------------------------------------------------------- junk

    private void EnsureJunkItems()
    {
        if (Junk.Count > 0) { return; }
        foreach (var src in Platform.Windows.Cleanup.JunkSources.All())
        {
            Junk.Add(new JunkItem { Id = src.Id, Name = src.Name, Description = src.Description, NeedsAdmin = src.NeedsAdmin, Source = src, Selected = !src.NeedsAdmin || IsElevated });
        }
        Junk.Add(new JunkItem { Id = "recycle-bin", Name = "Recycle Bin", Description = "Files you already deleted. Emptying it is permanent.", Selected = false });
    }

    /// <summary>Measures every junk source (read-only).</summary>
    [RelayCommand]
    public async Task ScanJunkAsync()
    {
        EnsureJunkItems();
        Busy = true;
        JunkSummary = "Measuring…";
        try
        {
            long total = 0;
            foreach (var j in Junk)
            {
                j.SizeText = "…";
                if (j.NeedsAdmin && !IsElevated) { j.SizeText = "needs full power"; j.Selected = false; continue; }
                var bytes = await Task.Run(() => j.Source is { } src
                    ? Platform.Windows.Cleanup.JunkSources.Scan(src).Bytes
                    : Platform.Windows.Cleanup.RecycleBin.Query().Bytes).ConfigureAwait(true);
                j.Bytes = bytes;
                j.SizeText = Format.Bytes(bytes);
                total += bytes;
            }
            JunkTotal = Format.Bytes(total);
            JunkSummary = total < 1_048_576 ? "Already clean — nothing worth removing." : $"{Format.Bytes(total)} can be freed. Choose what to remove and press Clean.";
            JunkScanned = true;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task CleanJunkAsync()
    {
        var chosen = Junk.Where(j => j.Selected && j.Bytes > 0).ToList();
        if (chosen.Count == 0) { JunkSummary = "Nothing selected (or nothing to remove)."; return; }
        var answer = Controls.ThemedDialog.Show(
            $"Remove {string.Join(", ", chosen.Select(c => c.Name))} ({Format.Bytes(chosen.Sum(c => c.Bytes))})?\n\nThis can't be undone. Files that are in use are skipped.",
            "Clean junk", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) { return; }
        await CleanJunkCoreAsync(chosen).ConfigureAwait(true);
    }

    internal async Task<long> CleanJunkCoreAsync(IReadOnlyList<JunkItem> chosen)
    {
        Busy = true;
        JunkSummary = "Cleaning…";
        long freed = 0;
        try
        {
            foreach (var j in chosen)
            {
                if (j.Source is { } src)
                {
                    var (_, f, _) = await Task.Run(() => Platform.Windows.Cleanup.JunkSources.Clean(src)).ConfigureAwait(true);
                    freed += f;
                }
                else if (await Task.Run(Platform.Windows.Cleanup.RecycleBin.Empty).ConfigureAwait(true))
                {
                    freed += j.Bytes;
                }
            }
            JunkSummary = $"✔ Freed {Format.Bytes(freed)}.";
            _main.Changes.AddLocalHistory($"Clean: freed {Format.Bytes(freed)} ({string.Join(", ", chosen.Select(c => c.Name))})");
        }
        finally
        {
            Busy = false;
        }
        await ScanJunkAsync().ConfigureAwait(true);
        JunkSummary = $"✔ Freed {Format.Bytes(freed)}. " + JunkSummary;
        return freed;
    }

    internal static TempScan ScanTempNow(bool elevated) => TempCleaner.Scan(TempCleaner.Roots(elevated), TempMinAge);

    [RelayCommand]
    private async Task ScanTempAsync()
    {
        var roots = TempCleaner.Roots(IsElevated);
        TempStatus = "Scanning…";
        var scan = await Task.Run(() => TempCleaner.Scan(roots, TempMinAge)).ConfigureAwait(true);
        TempStatus = $"{scan.Files:N0} temporary files older than 2 days, {scan.Bytes / 1048576.0:F0} MB" +
            (IsElevated ? " (your Temp folder + Windows\\Temp)." : " (your Temp folder; run as administrator to include Windows\\Temp).");
    }

    [RelayCommand]
    private async Task CleanTempAsync()
    {
        var answer = Controls.ThemedDialog.Show(
            "Delete temporary files older than 2 days?\n\nDeleted files do NOT go to the Recycle Bin and cannot be restored. Files in use are skipped.",
            "Clean temporary files", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) { return; }
        await CleanTempCoreAsync().ConfigureAwait(true);
    }

    /// <summary>Temp cleanup without its own prompt (PulseScan asks once for everything it fixes).</summary>
    internal async Task<(int Deleted, long Freed)> CleanTempCoreAsync()
    {
        var roots = TempCleaner.Roots(IsElevated);
        Busy = true;
        TempStatus = "Cleaning…";
        try
        {
            var (deleted, freed, skipped) = await Task.Run(() => TempCleaner.Clean(roots, TempMinAge)).ConfigureAwait(true);
            TempStatus = $"Deleted {deleted:N0} files, freed {freed / 1048576.0:F0} MB ({skipped:N0} in use or protected, skipped).";
            _main.Changes.AddLocalHistory("Temp cleanup: " + TempStatus);
            return (deleted, freed);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>For PulseScan: apply without prompts; returns the result line.</summary>
    internal string ApplyFastVisuals()
    {
        BestPerformanceVisuals();
        return Message;
    }

    internal string ApplyRecommendedSwitches()
    {
        var applied = 0;
        foreach (var t in WindowsTweaks.Catalog)
        {
            if (WindowsTweaks.IsApplied(t) || (t.RequiresAdmin && !IsElevated) || (t.Id == "fast-startup" && !WindowsTweaks.HibernationAvailable())) { continue; }
            try
            {
                WindowsTweaks.Apply(t, Journal);
                applied++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
            {
                Message = "✖ " + ex.Message;
            }
        }
        Refresh();
        _main.Changes.AddLocalHistory($"QuickSwitches: {applied} applied");
        return $"{applied} Windows switch(es) applied.";
    }

    private void Run(Func<string> action)
    {
        try
        {
            Message = action();
            _main.Changes.AddLocalHistory(Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            Message = "✖ " + ex.Message + (IsElevated ? "" : " — this may need administrator rights (Restart as administrator).");
        }
        Refresh();
    }
}
