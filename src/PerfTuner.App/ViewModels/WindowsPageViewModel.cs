using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.Controls;
using PerfTuner.Core.Network;
using PerfTuner.Platform.Windows.Maintenance;
using PerfTuner.Platform.Windows.Network;
using PerfTuner.Platform.Windows.Settings;
using PerfTuner.Platform.Windows.Undocumented;
using PerfTuner.Core.Undocumented;

namespace PerfTuner.App.ViewModels;

public sealed partial class FeatureRow(WindowsFeature feature) : ObservableObject
{
    public WindowsFeature Feature { get; } = feature;
    public string Title => Feature.Title;
    public string Description => Feature.Description + (Feature.Note is { } n ? " " + n : "");
    [ObservableProperty] private FeatureState _state = FeatureState.NotAvailable;
    [ObservableProperty] private bool _known;
    public bool IsOn => State is FeatureState.On;
    public bool CanChange => Known && State != FeatureState.NotAvailable;
    public string StateText => !Known ? "Checking…" : State switch
    {
        FeatureState.On => "On",
        FeatureState.Partly => "Partly on",
        FeatureState.Off => "Off",
        _ => "Not available in this Windows edition",
    };
    partial void OnStateChanged(FeatureState value) { OnPropertyChanged(nameof(IsOn)); OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(CanChange)); }
    partial void OnKnownChanged(bool value) { OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(CanChange)); }
}

/// <summary>The Windows page (v0.10): optional Windows features and DNS. (The settings list on the same page is SystemSettings.)</summary>
public sealed partial class WindowsPageViewModel(MainViewModel main) : ObservableObject
{
    private bool _loaded;

    public ObservableCollection<FeatureRow> Features { get; } = new(RepairTools.Features.Select(f => new FeatureRow(f)));
    public IReadOnlyList<DnsProvider> DnsProviders { get; } = DnsPresets.All;

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _featureMessage = "";
    [ObservableProperty] private string _featureProgress = "";
    [ObservableProperty] private DnsProvider? _selectedDns;
    [ObservableProperty] private string _dnsNow = "";
    [ObservableProperty] private string _dnsMessage = "";
    [ObservableProperty] private bool _canRestoreDns;
    [ObservableProperty] private bool _searchIndexOn;
    [ObservableProperty] private bool _searchKnown;
    [ObservableProperty] private string _searchMessage = "";

    private const string SearchTag = "search-indexing";

    public void SetActive(bool active)
    {
        if (!active || _loaded) { return; }
        _loaded = true;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!main.IsElevatedProcess) { FeatureMessage = "Reading Windows features needs full power (administrator)."; }
        else
        {
            try
            {
                var states = await Task.Run(RepairTools.GetFeatureStates).ConfigureAwait(true);
                foreach (var r in Features) { r.State = states.TryGetValue(r.Feature.Id, out var s) ? s : FeatureState.NotAvailable; r.Known = true; }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or IOException or ArgumentException)
            {
                FeatureMessage = "✖ Windows features couldn't be read: " + ex.Message;
            }
        }
        await RefreshDnsAsync().ConfigureAwait(true);
        await RefreshSearchAsync().ConfigureAwait(true);
    }

    private async Task RefreshSearchAsync()
    {
        var state = await Task.Run(() => ServiceStart.Get(DocumentedServices.Search)).ConfigureAwait(true);
        SearchKnown = state is not null;
        // Flip twice so a switch the user clicked shows the real state again.
        SearchIndexOn = !(state is { StartType: not 4 });
        SearchIndexOn = state is { StartType: not 4 };
        if (state is null) { SearchMessage = "Windows Search isn't installed on this PC."; }
    }

    /// <summary>Windows Search indexing on/off (the WSearch service's start type, journaled so History can undo it).</summary>
    [RelayCommand]
    private async Task ToggleSearchIndexAsync()
    {
        if (Busy || !SearchKnown) { await RefreshSearchAsync().ConfigureAwait(true); return; }
        if (!main.IsElevatedProcess) { SearchMessage = "Changing search indexing needs full power (administrator)."; await RefreshSearchAsync().ConfigureAwait(true); return; }
        var turnOff = SearchIndexOn;
        if (turnOff && ThemedDialog.Show("Turn off search indexing?\n\nThe Windows Search service stops and won't start again. Searching in Start, File Explorer and Outlook still works but is much slower, and searching inside files stops. Saves a little disk and CPU work on slow PCs.\n\nYou can turn it back on here or undo it in History.",
                "Search indexing", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            await RefreshSearchAsync().ConfigureAwait(true);
            return;
        }
        Busy = true;
        try
        {
            var journal = main.SystemSettings.MachineSettings ?? throw new InvalidOperationException("This needs full power (administrator).");
            SearchMessage = await Task.Run(() =>
            {
                var name = DocumentedServices.Search;
                if (turnOff)
                {
                    var st = ServiceStart.Get(name) ?? throw new InvalidOperationException("Windows Search isn't installed.");
                    ServiceStart.Set(name, 4, false);
                    journal.Record(new SettingChange { Kind = SettingKind.ServiceStartType, Tag = SearchTag, ServiceName = name, ServiceStart = st.StartType, ServiceDelayed = st.Delayed, Description = "Search indexing off" });
                    ServiceStart.Stop(name);
                    return "✔ Search indexing is off.";
                }
                var ids = journal.Entries.Where(e => e.Tag == SearchTag).Select(e => e.Id).Reverse().ToList();
                foreach (var id in ids) { journal.Revert(id); }
                if (ids.Count == 0) { ServiceStart.Set(name, 2, true); }
                ServiceStart.Start(name);
                return "✔ Search indexing is on again. Windows rebuilds the index in the background.";
            }).ConfigureAwait(true);
            main.AddActivity("You", SearchMessage);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or IOException)
        {
            SearchMessage = "✖ " + ex.Message;
        }
        finally { Busy = false; }
        await RefreshSearchAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ToggleFeatureAsync(FeatureRow? row)
    {
        if (row is not { CanChange: true } || Busy) { return; }
        var on = !row.IsOn;
        var text = on
            ? $"Turn on {row.Title}?\n\n{row.Description}\n\nWindows installs it with its own servicing engine; this can take several minutes and often needs a restart. You can turn it off again here."
            : $"Turn off {row.Title}?\n\nPrograms that rely on it stop working until it's turned on again. Usually needs a restart.";
        if (ThemedDialog.Show(text, on ? "Turn on a Windows feature" : "Turn off a Windows feature", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        Busy = true;
        FeatureMessage = "";
        try
        {
            var restart = await Task.Run(() => RepairTools.SetFeature(row.Feature, on, (c, t) =>
                Application.Current.Dispatcher.BeginInvoke(() => FeatureProgress = t > 0 ? $"{row.Title}: {100 * c / t}%" : $"{row.Title}…"))).ConfigureAwait(true);
            FeatureMessage = $"✔ {row.Title} turned {(on ? "on" : "off")}." + (restart ? " Restart Windows to finish." : "");
            main.AddActivity("You", FeatureMessage);
            var states = await Task.Run(RepairTools.GetFeatureStates).ConfigureAwait(true);
            foreach (var r in Features) { if (states.TryGetValue(r.Feature.Id, out var s)) { r.State = s; } }
            if (restart && on) { row.State = FeatureState.On; }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException or IOException or ArgumentException)
        {
            FeatureMessage = $"✖ {row.Title} couldn't be changed: {ex.Message}";
        }
        finally
        {
            Busy = false;
            FeatureProgress = "";
        }
    }

    private async Task RefreshDnsAsync()
    {
        try
        {
            var adapters = await Task.Run(DnsSettings.Adapters).ConfigureAwait(true);
            var names = adapters.Select(a =>
            {
                var preset = DnsPresets.Match(a.Servers, a.Automatic);
                return $"{a.Name}: {(preset?.Name ?? string.Join(", ", a.Servers))}";
            });
            DnsNow = adapters.Count == 0 ? "No active network adapter." : string.Join("\n", names);
            SelectedDns ??= adapters.Select(a => DnsPresets.Match(a.Servers, a.Automatic)).FirstOrDefault(p => p is not null) ?? DnsPresets.All[0];
            CanRestoreDns = DnsSettings.HasSaved;
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            DnsNow = "Couldn't read the network adapters: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ApplyDnsAsync()
    {
        if (SelectedDns is not { } p || Busy) { return; }
        if (!main.IsElevatedProcess) { DnsMessage = "Changing DNS needs full power (administrator)."; return; }
        var servers = p.Servers.Count == 0 ? "automatic (from your router)" : string.Join(", ", p.Servers);
        if (ThemedDialog.Show($"Use {p.Name} on every active network connection?\n\nDNS servers: {servers}\n\nPerfTuner saves your current setting first, so \"Restore previous\" puts it back. VPN apps that manage DNS themselves keep doing so.",
                "Change DNS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        Busy = true;
        try
        {
            var n = await Task.Run(() => DnsSettings.Apply(p)).ConfigureAwait(true);
            DnsMessage = $"✔ {p.Name} set on {n} connection(s). Takes effect right away.";
            main.AddActivity("You", DnsMessage);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or IOException)
        {
            DnsMessage = "✖ DNS couldn't be changed: " + ex.Message;
        }
        finally { Busy = false; }
        await RefreshDnsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RestoreDnsAsync()
    {
        if (Busy || !main.IsElevatedProcess) { return; }
        Busy = true;
        try
        {
            var n = await Task.Run(DnsSettings.RestorePrevious).ConfigureAwait(true);
            DnsMessage = $"✔ Previous DNS setting restored on {n} connection(s).";
            main.AddActivity("You", DnsMessage);
            SelectedDns = null;
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or IOException)
        {
            DnsMessage = "✖ " + ex.Message;
        }
        finally { Busy = false; }
        await RefreshDnsAsync().ConfigureAwait(true);
    }
}
