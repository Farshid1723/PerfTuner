using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.Controls;
using PerfTuner.Core.Network;
using PerfTuner.Platform.Windows.Maintenance;
using PerfTuner.Platform.Windows.Network;

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
