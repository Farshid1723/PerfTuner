using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.SystemTools;
using PerfTuner.Client;
using PerfTuner.Contracts;

namespace PerfTuner.App.ViewModels;

public sealed record LeakRow(string Name, int Pid, string Memory, string Growth, string Window, string Confidence);
public sealed record LatencyRow(string Driver, string MaxDpc, string MaxIsr, string Count, string Total, double Worst);
public sealed record BootRow(string When, string Kind, string Total, string Detail);
public sealed record CulpritRow(string Name, string What, string Times, string AvgDelay, string MaxDelay);

/// <summary>
/// Insights tab: CPU clock limit, memory-leak suspects, driver latency (DPC/ISR), boot and
/// shutdown analysis, drive health. Everything here is diagnostic (read-only).
/// </summary>
public sealed partial class InsightsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private bool _bootLoaded;

    public InsightsViewModel(MainViewModel main)
    {
        _main = main;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    public ObservableCollection<LeakRow> Leaks { get; } = [];
    public ObservableCollection<LatencyRow> Latency { get; } = [];
    public ObservableCollection<BootRow> Boots { get; } = [];
    public ObservableCollection<CulpritRow> Culprits { get; } = [];
    public ObservableCollection<DriveHealthInfo> Drives { get; } = [];

    [ObservableProperty] private string _clockText = "–";
    [ObservableProperty] private string _clockDetail = "";
    [ObservableProperty] private string _leakSummary = "Collecting… leak detection needs about 15 minutes of observation.";
    [ObservableProperty] private string _latencyState = "Not run yet.";
    [ObservableProperty] private string _latencyVerdict = "";
    [ObservableProperty] private double _scanSeconds = 20;
    [ObservableProperty] private string _bootSummary = "";
    [ObservableProperty] private string _driveSummary = "";
    [ObservableProperty] private bool _scanRunning;

    public void SetActive(bool active)
    {
        if (active)
        {
            _ = RefreshAsync();
            _ = LoadBootAsync();
            _ = LoadDrivesAsync();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_main.Client is not { } client)
        {
            ClockText = "Not connected.";
            return;
        }
        InsightsResponse i;
        try
        {
            i = await client.GetInsightsAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or OperationCanceledException)
        {
            ClockText = "Could not load insights: " + ex.Message;
            return;
        }

        if (!_bootLoaded)
        {
            _bootLoaded = true; // the page may open before the engine is connected
            _ = LoadBootAsync();
        }

        var c = CultureInfo.CurrentCulture;
        if (i.Clock.Available)
        {
            ClockText = $"{(i.Clock.EffectiveMhz / 1000).ToString("F2", c)} GHz effective · {i.Clock.PerformancePercent.ToString("F0", c)} % of base {(i.Clock.BaseMhz / 1000).ToString("F2", c)} GHz";
            ClockDetail = i.Clock.LimitPercent < 99.5
                ? $"⚠ Windows reports the CPU is LIMITED to {i.Clock.LimitPercent.ToString("F0", c)} % right now (power, thermal or firmware limit). Limited {i.Clock.LimitedShareRecentPercent.ToString("F0", c)} % of the last 10 minutes. Check cooling, the power plan's maximum processor state, and battery/energy-saver settings."
                : $"No performance limit active. Limited {i.Clock.LimitedShareRecentPercent.ToString("F0", c)} % of the last 10 minutes.";
        }
        else
        {
            ClockText = "CPU performance counters unavailable on this system.";
        }

        Leaks.Clear();
        foreach (var l in i.Leaks)
        {
            Leaks.Add(new LeakRow(l.Name, l.Pid, $"{l.PrivateMb:F0} MB", $"+{l.GrowthMbPerHour:F0} MB/h", $"{l.ObservedMinutes:F0} min", $"{l.Confidence * 100:F0} %"));
        }
        LeakSummary = i.LeakObservationMinutes < 15
            ? $"Watching {i.TrackedProcesses} apps for {i.LeakObservationMinutes:F0} min — leak verdicts start after 15 min of observation."
            : i.Leaks.Length == 0
                ? $"No leaks: none of the {i.TrackedProcesses} apps watched over {i.LeakObservationMinutes:F0} min shows steady, unbounded memory growth."
                : $"{i.Leaks.Length} app(s) grow memory steadily without levelling off — likely leaks. Restarting them frees the memory; report it to the vendor.";

        var lat = i.Latency;
        ScanRunning = lat.State == LatencyScanState.Running;
        LatencyState = lat.State switch
        {
            LatencyScanState.Running => lat.Message,
            LatencyScanState.Completed => $"Completed {DateTimeOffset.FromUnixTimeMilliseconds(lat.StartedUnixMs).ToLocalTime():t} · {lat.Message}",
            LatencyScanState.Failed => "✖ " + lat.Message,
            _ => "Not run yet. Measures how long each driver blocks the CPU — the cause of audio crackle, stutter and input lag.",
        };
        LatencyVerdict = lat.Verdict;
        if (lat.State == LatencyScanState.Completed && (Latency.Count == 0 || Latency.Count != lat.Drivers.Length))
        {
            Latency.Clear();
            foreach (var d in lat.Drivers)
            {
                Latency.Add(new LatencyRow(d.Driver, $"{d.MaxDpcUs:F0} µs", $"{d.MaxIsrUs:F0} µs", $"{d.DpcCount + d.IsrCount:N0}", $"{d.TotalMs:F1} ms", Math.Max(d.MaxDpcUs, d.MaxIsrUs)));
            }
        }
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (_main.Client is not { } client) { return; }
        try
        {
            var r = await client.StartLatencyScanAsync((int)Math.Round(ScanSeconds)).ConfigureAwait(true);
            LatencyState = (r.Success ? "" : "✖ ") + r.Message;
            Latency.Clear();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or OperationCanceledException)
        {
            LatencyState = "✖ " + ex.Message;
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LoadBootAsync()
    {
        if (_main.Client is not { } client)
        {
            BootSummary = "Connecting to the engine…";
            _bootLoaded = false;
            return;
        }
        BootReportResponse r;
        try
        {
            r = await client.GetBootReportAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or OperationCanceledException)
        {
            BootSummary = "Could not load boot report: " + ex.Message;
            return;
        }
        Boots.Clear();
        Culprits.Clear();
        foreach (var b in r.Records)
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(b.TimeUnixMs).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            var detail = b.Kind == "boot" ? $"to desktop {b.MainPathSeconds:F1} s, then {b.PostBootSeconds:F1} s until idle" : "";
            Boots.Add(new BootRow(when, b.Kind, $"{b.TotalSeconds:F1} s", detail));
        }
        foreach (var c in r.Culprits)
        {
            Culprits.Add(new CulpritRow(c.Name, $"{c.Phase} · {c.Kind}", c.Occurrences.ToString(CultureInfo.CurrentCulture), $"+{c.AvgDelayMs / 1000:F1} s", $"+{c.MaxDelayMs / 1000:F1} s"));
        }
        var boots = r.Records.Where(x => x.Kind == "boot").ToList();
        BootSummary = !r.Available ? r.Message
            : boots.Count == 0 ? r.Message
            : $"Last boot {boots[0].TotalSeconds:F1} s (average of last {boots.Count}: {boots.Average(x => x.TotalSeconds):F1} s). " +
              (r.Culprits.Length > 0 ? $"Biggest recurring slowdown: {r.Culprits[0].Name} (+{r.Culprits[0].AvgDelayMs / 1000:F1} s, {r.Culprits[0].Occurrences}×)." : "No recorded slowdowns.");
    }

    [RelayCommand]
    private async Task LoadDrivesAsync()
    {
        try
        {
            var drives = await Task.Run(DriveHealth.Read).ConfigureAwait(true);
            Drives.Clear();
            foreach (var d in drives) { Drives.Add(d); }
            DriveSummary = drives.Count == 0 ? "No physical disks reported."
                : drives.All(d => d.Verdict.StartsWith("Healthy", StringComparison.Ordinal)) ? $"All {drives.Count} drive(s) healthy."
                : "Attention needed — see below.";
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            DriveSummary = "Drive health unavailable: " + ex.Message;
        }
    }
}
