using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.Controls;
using PerfTuner.App.SystemTools;
using PerfTuner.Client;
using PerfTuner.Contracts;
using PerfTuner.Core.Hardware;
using PerfTuner.Platform.Windows.Settings;

namespace PerfTuner.App.ViewModels;

/// <summary>One Cockpit tile: an animated part with its value, status and detail line.</summary>
public sealed partial class PartTile : ObservableObject
{
    public PartTile(string id, string title, HardwareKind kind)
    {
        Id = id;
        Title = title;
        _kind = kind;
    }

    public string Id { get; }
    public string Title { get; }

    [ObservableProperty] private HardwareKind _kind;
    [ObservableProperty] private double _load;
    [ObservableProperty] private LoadState _state;
    [ObservableProperty] private string _value = "–";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private int _coolDownToken;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _problems;

    public string StateText => State switch { LoadState.Strained => "strained", LoadState.Busy => "busy", _ => "calm" };

    partial void OnStateChanged(LoadState value) => OnPropertyChanged(nameof(StateText));

    public void CoolDown() => CoolDownToken++;
}

/// <summary>A disk problem in plain words, with an optional fix.</summary>
public sealed record DiskProblem(string Text, string Severity, string? ActionText = null, string? ActionKey = null, string? Letter = null);

/// <summary>A volume row with its own action state.</summary>
public sealed partial class VolumeView(VolumeDetail detail) : ObservableObject
{
    public VolumeDetail Detail { get; } = detail;
    [ObservableProperty] private string _fragmentation = "not analysed";
    [ObservableProperty] private string _scanResult = "not scanned";
    [ObservableProperty] private bool _offerOfflineFix;
}

/// <summary>A network adapter line.</summary>
public sealed record AdapterInfo(string Name, string Kind, string Speed, string Status);

/// <summary>
/// The Cockpit page (design 6.2–6.4): four animated tiles fed by the live metrics stream, plus CPU / Memory / Disk /
/// Network detail views. Latency and network health are polled from the engine only while the page is visible.
/// </summary>
public sealed partial class CockpitViewModel : ObservableObject
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;
    private const long GB = 1L << 30;

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _poll;
    private int _pollTick;
    private bool _active;
    private readonly Queue<double> _diskRates = new();
    private readonly Queue<double> _netRates = new();
    private LoadState _diskLatencyState = LoadState.Calm;
    private bool _diskLatencyKnown;
    private LoadState _netLossState = LoadState.Calm;
    private bool _netLossKnown;
    private bool _primaryIsHdd;

    public CockpitViewModel(MainViewModel main)
    {
        _main = main;
        Parts = [Cpu, Memory, Disk, Network];
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _poll.Tick += async (_, _) => await PollAsync().ConfigureAwait(true);
        Space = new SpaceViewModel(main);
        SelectPart("cpu");
    }

    // ------------------------------------------------------------------ Space explorer (sub-view)

    public SpaceViewModel Space { get; }
    [ObservableProperty] private bool _showSpace;
    public bool ShowParts => !ShowSpace;

    partial void OnShowSpaceChanged(bool value) => OnPropertyChanged(nameof(ShowParts));

    /// <summary>"Find what's using space" on a drive.</summary>
    [RelayCommand]
    private void OpenSpace(VolumeView? v)
    {
        if (v is null) { return; }
        ShowSpace = true;
        Space.Start(v.Detail.Letter, v.Detail.SizeBytes - v.Detail.FreeBytes);
    }

    /// <summary>Opens the Space view for a drive letter (Clean page link, "almost full" problem).</summary>
    public void OpenSpaceFor(string letter)
    {
        var v = Volumes.FirstOrDefault(x => x.Detail.Letter == letter);
        ShowSpace = true;
        Space.Start(letter, v is null ? 0 : v.Detail.SizeBytes - v.Detail.FreeBytes);
    }

    [RelayCommand]
    private void BackToCockpit() => ShowSpace = false;

    public PartTile Cpu { get; } = new("cpu", "Processor", HardwareKind.Cpu);
    public PartTile Memory { get; } = new("memory", "Memory", HardwareKind.Memory);
    public PartTile Disk { get; } = new("disk", "Disk", HardwareKind.Ssd);
    public PartTile Network { get; } = new("network", "Network", HardwareKind.Network);
    public IReadOnlyList<PartTile> Parts { get; }

    [ObservableProperty] private string _selectedPart = "cpu";

    public bool ShowCpu => SelectedPart == "cpu";
    public bool ShowMemory => SelectedPart == "memory";
    public bool ShowDisk => SelectedPart == "disk";
    public bool ShowNetwork => SelectedPart == "network";

    partial void OnSelectedPartChanged(string value)
    {
        foreach (var p in Parts) { p.IsSelected = p.Id == value; }
        OnPropertyChanged(nameof(ShowCpu));
        OnPropertyChanged(nameof(ShowMemory));
        OnPropertyChanged(nameof(ShowDisk));
        OnPropertyChanged(nameof(ShowNetwork));
        if (_active) { _ = LoadSelectedAsync(); }
    }

    [RelayCommand]
    private void SelectPart(string? id)
    {
        if (id is "cpu" or "memory" or "disk" or "network") { SelectedPart = id; }
    }

    /// <summary>Called by the main view model when the Cockpit page is shown or hidden.</summary>
    public void SetActive(bool active)
    {
        if (_active == active) { return; }
        _active = active;
        if (active)
        {
            _pollTick = 0;
            _poll.Start();
            _ = PollAsync();
            _ = LoadSelectedAsync();
            _ = LoadAdaptersAsync();
        }
        else
        {
            _poll.Stop();
        }
    }

    // ------------------------------------------------------------------ live stream

    public void OnSample(in MetricSample s)
    {
        var throttled = s.CpuLimitPercent is > 0 and < 90;
        Cpu.Load = s.CpuPercent / 100;
        Cpu.State = ResourceStatus.Cpu(s.CpuPercent, throttled);
        Cpu.Value = s.CpuPercent.ToString("F0", C) + " %";
        Cpu.Detail = s.CpuEffectiveMhz > 0 ? $"{(s.CpuEffectiveMhz / 1000).ToString("F2", C)} GHz" : "";
        Cpu.Note = throttled ? $"Held back to {s.CpuLimitPercent.ToString("F0", C)} %" : "";
        CpuSpeedText = Cpu.Detail.Length > 0 ? Cpu.Detail + (throttled ? $" (limited to {s.CpuLimitPercent.ToString("F0", C)} %)" : "") : "–";
        CpuThrottleText = throttled
            ? "Windows is holding the processor back right now (heat, battery or power settings). Turbo mode or a better power plan can help; if it is heat, clean the vents."
            : "The processor runs at full speed — nothing is holding it back.";

        var availablePct = s.MemoryLoadPercent > 0 ? 100 - s.MemoryLoadPercent : 100;
        Memory.Load = s.MemoryLoadPercent / 100;
        Memory.State = ResourceStatus.Memory(availablePct, _commitPercent);
        Memory.Value = s.MemoryLoadPercent.ToString("F0", C) + " %";
        Memory.Detail = $"{(s.MemoryAvailableMb / 1024.0).ToString("F1", C)} GB free";

        var disk = s.DiskReadBytesPerSec + s.DiskWriteBytesPerSec;
        Disk.Load = Math.Clamp(disk / (_primaryIsHdd ? 150e6 : 800e6), 0, 1);
        var diskThroughput = ResourceStatus.Throughput(disk, Median(_diskRates, disk), 20e6);
        Disk.State = _diskLatencyKnown ? ResourceStatus.Worst(_diskLatencyState, diskThroughput) : diskThroughput;
        if (Disk.Problems > 0) { Disk.State = ResourceStatus.Worst(Disk.State, LoadState.Busy); }
        Disk.Value = Format.Rate(disk);
        Disk.Detail = $"R {Format.Rate(s.DiskReadBytesPerSec)} · W {Format.Rate(s.DiskWriteBytesPerSec)}";

        var net = s.NetRxBytesPerSec + s.NetTxBytesPerSec;
        Network.Load = Math.Clamp(net / 12.5e6, 0, 1); // 100 Mbit/s = full animation
        var netThroughput = ResourceStatus.Throughput(net, Median(_netRates, net), 2e6);
        Network.State = _netLossKnown ? ResourceStatus.Worst(_netLossState, netThroughput) : netThroughput;
        Network.Value = Format.Rate(net);
        Network.Detail = $"↓ {Format.Rate(s.NetRxBytesPerSec)} · ↑ {Format.Rate(s.NetTxBytesPerSec)}";
        NetDownText = Format.Rate(s.NetRxBytesPerSec);
        NetUpText = Format.Rate(s.NetTxBytesPerSec);
    }

    /// <summary>Rolling median over the last 10 minutes (one sample per second).</summary>
    private static double Median(Queue<double> window, double value)
    {
        window.Enqueue(value);
        while (window.Count > 600) { window.Dequeue(); }
        var sorted = window.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>Plays the "cooled down" animation on a part after a successful fix.</summary>
    public void CoolDown(string part)
    {
        var tile = Parts.FirstOrDefault(p => p.Id == part);
        tile?.CoolDown();
        if (part == "memory") { HomeMemoryCoolDown++; }
        if (part == "cpu") { HomeCpuCoolDown++; }
        if (part == "disk") { HomeDiskCoolDown++; }
    }

    [ObservableProperty] private int _homeCpuCoolDown;
    [ObservableProperty] private int _homeMemoryCoolDown;
    [ObservableProperty] private int _homeDiskCoolDown;

    // ------------------------------------------------------------------ polling (only while visible)

    private async Task PollAsync()
    {
        if (_main.Client is not { } client) { return; }
        _pollTick++;
        try
        {
            var io = await client.GetIoLatencyAsync().ConfigureAwait(true);
            _diskLatencyKnown = io.Available;
            if (io.Available)
            {
                var p99 = Math.Max(io.ReadTotal.P99Ms, io.WriteTotal.P99Ms);
                _diskLatencyState = io.ReadTotal.Count + io.WriteTotal.Count == 0 || p99 < 30 ? LoadState.Calm : p99 < 150 ? LoadState.Busy : LoadState.Strained;
                Disk.Note = io.ReadTotal.Count + io.WriteTotal.Count == 0 ? "Idle" : $"Answers in {io.ReadTotal.P50Ms.ToString("F1", C)} ms · slowest {p99.ToString("F0", C)} ms";
                DiskLatencyText = Disk.Note;
            }
            else
            {
                Disk.Note = "Latency needs full power";
                DiskLatencyText = "Disk response times need full power (administrator).";
            }
            if (_pollTick % 3 == 1)
            {
                var n = await client.GetNetworkHealthAsync().ConfigureAwait(true);
                _netLossKnown = n.Available;
                if (n.Available)
                {
                    _netLossState = n.RetransmitRatePercent >= 2 ? LoadState.Strained : n.RetransmitRatePercent >= 0.5 ? LoadState.Busy : LoadState.Calm;
                    Network.Note = n.RetransmitRatePercent >= 0.5 ? $"{n.RetransmitRatePercent.ToString("F1", C)} % packets resent" : "No lost packets";
                    NetHealthText = n.RetransmitRatePercent >= 2
                        ? $"{n.RetransmitRatePercent.ToString("F1", C)} % of packets had to be sent again — the connection is losing data (often weak Wi-Fi)."
                        : n.RetransmitRatePercent >= 0.5 ? $"{n.RetransmitRatePercent.ToString("F1", C)} % of packets resent — mostly fine, occasionally lossy."
                        : "Healthy: no lost packets in the last minute.";
                }
                else
                {
                    Network.Note = "Loss check needs full power";
                    NetHealthText = "Packet-loss checks need full power (administrator).";
                }
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or InvalidOperationException)
        {
            // Engine busy or reconnecting: keep the last values.
        }
        if (_pollTick % 3 == 0 && ShowMemory) { await LoadMemoryAsync().ConfigureAwait(true); }
    }

    private Task LoadSelectedAsync() => SelectedPart switch
    {
        "memory" => LoadMemoryAsync(),
        "disk" => LoadDiskAsync(),
        "network" => LoadAdaptersAsync(),
        _ => Task.CompletedTask,
    };

    // ------------------------------------------------------------------ CPU detail

    [ObservableProperty] private string _cpuSpeedText = "–";
    [ObservableProperty] private string _cpuThrottleText = "";
    public string CpuName { get; } = ReadCpuName();
    public string CpuCores { get; } = $"{Environment.ProcessorCount} logical processors";

    private static string ReadCpuName()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Processor";
    }

    // ------------------------------------------------------------------ Network detail

    [ObservableProperty] private string _netDownText = "–";
    [ObservableProperty] private string _netUpText = "–";
    [ObservableProperty] private string _netHealthText = "Checking…";
    public ObservableCollection<AdapterInfo> Adapters { get; } = [];

    /// <summary>Windows' internal helper interfaces (filters, schedulers, miniports) that people don't think of as adapters.</summary>
    private static readonly string[] NoiseWords = ["Filter", "Scheduler", "Miniport", "Kernel Debug", "Local Area Connection*"];

    private static bool IsVirtual(NetworkInterface n) =>
        n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
        || n.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase) || n.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase);

    private async Task LoadAdaptersAsync()
    {
        var list = await Task.Run(() => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) && n.OperationalStatus == OperationalStatus.Up
                && !NoiseWords.Any(w => n.Description.Contains(w, StringComparison.OrdinalIgnoreCase) || n.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => IsVirtual(n) ? 1 : 0)
            .Select(n => new AdapterInfo(
                n.Name,
                IsVirtual(n) ? "Virtual" : n.NetworkInterfaceType switch { NetworkInterfaceType.Wireless80211 => "Wi-Fi", NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "Cable", _ => n.NetworkInterfaceType.ToString() },
                n.Speed > 0 ? (n.Speed >= 1_000_000_000 ? $"{(n.Speed / 1e9).ToString("0.#", C)} Gbit/s" : $"{(n.Speed / 1e6).ToString("0", C)} Mbit/s") : "",
                n.Description))
            .ToList()).ConfigureAwait(true);
        Adapters.Clear();
        foreach (var a in list) { Adapters.Add(a); }
    }

    // ------------------------------------------------------------------ Disk detail

    public ObservableCollection<VolumeView> Volumes { get; } = [];
    public ObservableCollection<DriveHealthInfo> PhysicalDisks { get; } = [];
    public ObservableCollection<DiskProblem> DiskProblems { get; } = [];
    [ObservableProperty] private string _diskMessage = "";
    [ObservableProperty] private string _diskLatencyText = "";
    [ObservableProperty] private bool _diskBusy;
    private DateTime _diskLoaded = DateTime.MinValue;

    [RelayCommand]
    private Task RefreshDiskAsync()
    {
        _diskLoaded = DateTime.MinValue;
        return LoadDiskAsync();
    }

    private async Task LoadDiskAsync()
    {
        if (DateTime.UtcNow - _diskLoaded < TimeSpan.FromSeconds(30)) { return; }
        _diskLoaded = DateTime.UtcNow;
        try
        {
            var (disks, volumes) = await Task.Run(() =>
            {
                var d = DriveHealth.Read();
                var pf = SafePageFileLetters();
                return (d, DiskInspector.ReadVolumes(pf, d));
            }).ConfigureAwait(true);
            PhysicalDisks.Clear();
            foreach (var d in disks) { PhysicalDisks.Add(d); }
            var previous = Volumes.ToDictionary(v => v.Detail.Letter, StringComparer.Ordinal);
            Volumes.Clear();
            foreach (var v in volumes)
            {
                var view = new VolumeView(v);
                if (previous.TryGetValue(v.Letter, out var old)) { view.Fragmentation = old.Fragmentation; view.ScanResult = old.ScanResult; view.OfferOfflineFix = old.OfferOfflineFix; }
                view.OfferOfflineFix |= v.Dirty == true;
                Volumes.Add(view);
            }
            var windowsDisk = volumes.FirstOrDefault(v => v.IsWindows)?.Media;
            _primaryIsHdd = windowsDisk == "HDD";
            Disk.Kind = _primaryIsHdd ? HardwareKind.Hdd : HardwareKind.Ssd;
            BuildDiskProblems(volumes, disks);
            if (!_main.IsElevatedProcess) { DiskMessage = "Some details (file-system version, error flag, temperature, wear) and all disk actions need full power (administrator)."; }
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            DiskMessage = "Disk information is unavailable: " + ex.Message;
        }
    }

    private static List<string> SafePageFileLetters()
    {
        try
        {
            var cfg = PageFile.Read();
            var letters = cfg.Entries.Select(e => e.Letter).ToList();
            if (cfg.AutomaticManaged || letters.Count == 0) { letters.Add(char.ToUpperInvariant(Environment.SystemDirectory[0]).ToString()); }
            return letters;
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    private void BuildDiskProblems(IReadOnlyList<VolumeDetail> volumes, IReadOnlyList<DriveHealthInfo> disks)
    {
        DiskProblems.Clear();
        foreach (var v in volumes)
        {
            var low = v.IsWindows ? v.FreeBytes < 15 * GB || v.FreePercent < 10 : v.FreePercent < 10;
            if (low)
            {
                DiskProblems.Add(new DiskProblem(
                    v.IsWindows
                        ? $"Drive {v.Letter}: is almost full ({v.SpaceText}). Windows slows down and updates can fail when the system drive is nearly full."
                        : $"Drive {v.Letter}: is almost full ({v.SpaceText}).",
                    "warn", "Find what's using space", "space", v.Letter));
            }
            if (v.Dirty == true)
            {
                DiskProblems.Add(new DiskProblem($"Windows has marked drive {v.Letter}: for a check — its file system may have errors.", "bad", "Scan for errors", "scan", v.Letter));
            }
            if (v.Health is "Warning" or "Unhealthy")
            {
                DiskProblems.Add(new DiskProblem($"Windows reports drive {v.Letter}: as {v.Health}.", "bad", "Scan for errors", "scan", v.Letter));
            }
        }
        foreach (var d in disks)
        {
            if (d.Health != "Healthy" && d.Health != "Unknown")
            {
                DiskProblems.Add(new DiskProblem($"{d.Name}: Windows reports the drive as {d.Health}. Back up your files now.", "bad"));
            }
            else if ((d.ReadErrorsUncorrected ?? 0) + (d.WriteErrorsUncorrected ?? 0) > 0)
            {
                DiskProblems.Add(new DiskProblem($"{d.Name}: the drive recorded read/write errors it couldn't correct. Back up your files and keep an eye on it.", "bad"));
            }
            if (d.WearPercent is >= 80)
            {
                DiskProblems.Add(new DiskProblem($"{d.Name}: {d.WearPercent} % of its rated write endurance is used. Plan a replacement.", "warn"));
            }
            if (d.TemperatureC is > 0 && d.TemperatureMaxC is > 0 && d.TemperatureC >= d.TemperatureMaxC)
            {
                DiskProblems.Add(new DiskProblem($"{d.Name}: running at {d.TemperatureC} °C, at or above its maximum. Improve airflow.", "bad"));
            }
        }
        foreach (var v in Volumes.Where(v => v.Fragmentation.EndsWith("fragmented", StringComparison.Ordinal) && v.Detail.Media == "HDD"))
        {
            if (int.TryParse(v.Fragmentation.Split(' ')[0], out var pct) && pct >= 10)
            {
                DiskProblems.Add(new DiskProblem($"Drive {v.Detail.Letter}: is {pct} % fragmented — files take longer to read.", "warn", "Optimize", "optimize", v.Detail.Letter));
            }
        }
        Disk.Problems = DiskProblems.Count;
        HasNoDiskProblems = DiskProblems.Count == 0;
    }

    [ObservableProperty] private bool _hasNoDiskProblems = true;

    [RelayCommand]
    private async Task DiskProblemActionAsync(DiskProblem? problem)
    {
        if (problem?.ActionKey is null) { return; }
        if (problem.ActionKey == "space" && problem.Letter is { } letter) { OpenSpaceFor(letter); return; }
        var view = Volumes.FirstOrDefault(v => v.Detail.Letter == problem.Letter);
        if (view is null) { return; }
        if (problem.ActionKey == "scan") { await ScanAsync(view).ConfigureAwait(true); }
        if (problem.ActionKey == "optimize") { await OptimizeAsync(view).ConfigureAwait(true); }
    }

    private bool NeedAdmin()
    {
        if (_main.IsElevatedProcess) { return false; }
        DiskMessage = "This needs full power (administrator). Use \"Unlock full power\" at the bottom left.";
        return true;
    }

    [RelayCommand]
    private async Task AnalyzeAsync(VolumeView? v)
    {
        if (v is null || DiskBusy || NeedAdmin()) { return; }
        DiskBusy = true;
        DiskMessage = $"Analysing drive {v.Detail.Letter}: …";
        try
        {
            var pct = await Task.Run(() => DiskInspector.AnalyzeFragmentation(v.Detail.Letter)).ConfigureAwait(true);
            v.Fragmentation = pct is { } p ? $"{p} % fragmented" : "analysis not available";
            DiskMessage = pct is { } q
                ? v.Detail.Media == "SSD" ? $"Drive {v.Detail.Letter}: {q} % fragmented. This is an SSD — fragmentation doesn't slow it down; Optimize sends TRIM instead."
                  : q >= 10 ? $"Drive {v.Detail.Letter}: {q} % fragmented — Optimize will make files faster to read." : $"Drive {v.Detail.Letter}: {q} % fragmented — nothing to do."
                : "Windows couldn't analyse this drive.";
            BuildDiskProblems(Volumes.Select(x => x.Detail).ToList(), PhysicalDisks.ToList());
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            DiskMessage = "✖ " + ex.Message;
        }
        finally { DiskBusy = false; }
    }

    [RelayCommand]
    private async Task OptimizeAsync(VolumeView? v)
    {
        if (v is null || DiskBusy || NeedAdmin()) { return; }
        DiskBusy = true;
        DiskMessage = v.Detail.Media == "HDD" ? $"Defragmenting drive {v.Detail.Letter}: … this can take a while; you can keep working." : $"Optimizing drive {v.Detail.Letter}: (TRIM) …";
        try
        {
            DiskMessage = await Task.Run(() => DriveOptimizer.Optimize(v.Detail.Letter)).ConfigureAwait(true);
            if (DiskMessage.EndsWith("optimized.", StringComparison.Ordinal))
            {
                v.Fragmentation = "optimized just now";
                CoolDown("disk");
                _main.AddActivity("You", DiskMessage);
            }
            BuildDiskProblems(Volumes.Select(x => x.Detail).ToList(), PhysicalDisks.ToList());
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            DiskMessage = "✖ " + ex.Message;
        }
        finally { DiskBusy = false; }
    }

    [RelayCommand]
    private async Task ScanAsync(VolumeView? v)
    {
        if (v is null || DiskBusy || NeedAdmin()) { return; }
        DiskBusy = true;
        DiskMessage = $"Scanning drive {v.Detail.Letter}: for errors … the drive stays usable.";
        try
        {
            var r = await Task.Run(() => DiskInspector.Repair(v.Detail.Letter, offline: false)).ConfigureAwait(true);
            (v.ScanResult, DiskMessage) = r switch
            {
                DiskInspector.RepairResult.NoErrorsFound => ("No errors", $"✔ Drive {v.Detail.Letter}: no file-system errors found."),
                DiskInspector.RepairResult.ErrorsFixed => ("Errors fixed", $"✔ Drive {v.Detail.Letter}: errors were found and fixed while you work."),
                DiskInspector.RepairResult.ErrorsFoundNeedsOfflineFix => ("Needs fix at restart", $"Drive {v.Detail.Letter}: errors found that can only be fixed during a restart. Use \"Fix at next restart\"."),
                _ => ("Scan failed", $"✖ Windows couldn't scan drive {v.Detail.Letter}:."),
            };
            v.OfferOfflineFix = r == DiskInspector.RepairResult.ErrorsFoundNeedsOfflineFix || v.Detail.Dirty == true;
            if (r is DiskInspector.RepairResult.NoErrorsFound or DiskInspector.RepairResult.ErrorsFixed) { CoolDown("disk"); }
            _main.AddActivity("You", DiskMessage);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            DiskMessage = "✖ " + ex.Message;
        }
        finally { DiskBusy = false; }
    }

    [RelayCommand]
    private async Task FixAtRestartAsync(VolumeView? v)
    {
        if (v is null || DiskBusy || NeedAdmin()) { return; }
        var answer = ThemedDialog.Show(
            $"Windows will check and repair drive {v.Detail.Letter}: the next time you restart the PC.\n\n" +
            "• The repair runs before Windows starts and can take from a few minutes to over an hour on large hard disks.\n" +
            "• Don't turn the PC off while it runs.\n• This is a repair, so it can't be undone — it doesn't delete your files.\n\n" +
            "PerfTuner won't restart the PC; it happens whenever you restart.",
            "Fix errors at next restart", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) { return; }
        DiskBusy = true;
        try
        {
            var r = await Task.Run(() => DiskInspector.Repair(v.Detail.Letter, offline: true)).ConfigureAwait(true);
            DiskMessage = r == DiskInspector.RepairResult.Scheduled
                ? $"✔ Repair of drive {v.Detail.Letter}: scheduled for the next restart."
                : $"✖ Windows couldn't schedule the repair of drive {v.Detail.Letter}:.";
            if (r == DiskInspector.RepairResult.Scheduled) { v.ScanResult = "Repair at next restart"; v.OfferOfflineFix = false; }
            _main.AddActivity("You", DiskMessage);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            DiskMessage = "✖ " + ex.Message;
        }
        finally { DiskBusy = false; }
    }

    // ------------------------------------------------------------------ Memory detail

    private double _commitPercent;
    [ObservableProperty] private MemorySnapshot? _memorySnapshot;
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private string _commitText = "";
    [ObservableProperty] private string _pageFileConfigText = "";
    [ObservableProperty] private string _adviceTitle = "";
    [ObservableProperty] private string _adviceText = "";
    [ObservableProperty] private string _adviceAction = "";
    [ObservableProperty] private bool _hasAdviceAction;
    [ObservableProperty] private bool _adviceIsGood = true;
    [ObservableProperty] private string _memoryMessage = "";
    [ObservableProperty] private bool _restartPending;
    [ObservableProperty] private GridLength _ramUsedWidth = new(0, GridUnitType.Star);
    [ObservableProperty] private GridLength _ramCacheWidth = new(0, GridUnitType.Star);
    [ObservableProperty] private GridLength _ramFreeWidth = new(1, GridUnitType.Star);
    [ObservableProperty] private GridLength _commitUsedWidth = new(0, GridUnitType.Star);
    [ObservableProperty] private GridLength _commitFreeWidth = new(1, GridUnitType.Star);
    [ObservableProperty] private double _commitPeakPosition;
    public ObservableCollection<PageFileUsageInfo> PageFiles { get; } = [];
    public ObservableCollection<string> PageFileDrives { get; } = [];
    [ObservableProperty] private string? _customDrive;
    [ObservableProperty] private bool _customAvailable;
    [ObservableProperty] private double _customMinGb;
    [ObservableProperty] private double _customMaxGb = 1;
    [ObservableProperty] private double _customGb;
    [ObservableProperty] private string _customHint = "";
    private PageFileAdvice? _advice;
    private string? _ssdLetter;
    private Dictionary<string, (long Free, long Size, string Media)> _drives = [];

    public string CustomGbText => CustomGb.ToString("F1", C) + " GB";

    partial void OnCustomGbChanged(double value) => OnPropertyChanged(nameof(CustomGbText));

    [RelayCommand]
    private Task RefreshMemoryAsync() => LoadMemoryAsync();

    private DateTime _drivesLoaded = DateTime.MinValue;

    private async Task LoadMemoryAsync()
    {
        MemorySnapshot snap;
        try
        {
            snap = await Task.Run(MemoryInspector.Read).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            MemoryMessage = "Memory details are unavailable: " + ex.Message;
            return;
        }
        // Drive media/free space only matter for page-file advice; read them at most every 5 minutes
        // (reliability counters can wake sleeping hard disks).
        if (DateTime.UtcNow - _drivesLoaded > TimeSpan.FromMinutes(5))
        {
            try
            {
                var volumes = await Task.Run(() => DiskInspector.ReadVolumes([], DriveHealth.Read())).ConfigureAwait(true);
                // Only internal NTFS drives can hold a page file reliably (no USB disks).
                _drives = volumes.Where(v => v.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) && !v.External)
                    .ToDictionary(v => v.Letter, v => (v.FreeBytes, v.SizeBytes, v.Media), StringComparer.Ordinal);
                _drivesLoaded = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is System.Management.ManagementException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                MemoryMessage = "Drive details are unavailable, so the page-file advice is limited: " + ex.Message;
            }
        }
        ApplyMemory(snap);
    }

    private static string WindowsLetter => char.ToUpperInvariant(Environment.SystemDirectory[0]).ToString();

    /// <summary>Advice for the current configuration; free space is that of <paramref name="drive"/> (custom-size bounds).</summary>
    private PageFileAdvice Advise(MemorySnapshot m, string drive)
    {
        var windows = WindowsLetter;
        List<string> pfLetters = m.Config.AutomaticManaged ? [windows] : m.Config.Entries.Select(e => e.Letter).ToList();
        if (pfLetters.Count == 0 && m.Usage.Count > 0) { pfLetters = m.Usage.Select(u => u.Letter).ToList(); }
        var onlyHdd = pfLetters.Count > 0 && pfLetters.All(l => _drives.TryGetValue(l, out var d) && d.Media == "HDD");
        _ssdLetter = _drives.Where(d => d.Value.Media == "SSD" && !pfLetters.Contains(d.Key)).OrderByDescending(d => d.Value.Free).Select(d => d.Key).FirstOrDefault();
        var ssdWithRoom = _ssdLetter is not null && _drives[_ssdLetter].Free >= 0.2 * _drives[_ssdLetter].Size;
        // Crash information needs the page file on the Windows drive: that entry decides "too small".
        // Managed by Windows (automatic, or 0/0 on that drive) can grow, so it never counts as too small.
        var windowsEntry = m.Config.Entries.FirstOrDefault(e => e.Letter == windows);
        var windowsMax = m.Config.AutomaticManaged || windowsEntry is { MaximumMb: 0 } ? long.MaxValue : (windowsEntry?.MaximumMb ?? 0) * (1L << 20);
        var free = _drives.TryGetValue(drive, out var td) ? td.Free : 0;
        return PageFileAdvisor.Advise(new PageFileInputs(m.PhysicalTotal, m.CommitPeak, m.CommitLimit, m.Config.AutomaticManaged, windowsMax,
            m.Config.AutomaticManaged || m.Config.Entries.Count > 0 || m.Usage.Count > 0, onlyHdd, ssdWithRoom, free, m.CrashDumpType));
    }

    private void ApplyMemory(MemorySnapshot m)
    {
        MemorySnapshot = m;
        _commitPercent = m.CommitFraction * 100;
        RamText = $"{Format.Bytes(m.InUse)} in use · {Format.Bytes(m.SystemCache)} cached · {Format.Bytes(m.PhysicalAvailable)} available · {Format.Bytes(m.PhysicalTotal)} total";
        CommitText = $"{Format.Bytes(m.CommitTotal)} committed of {Format.Bytes(m.CommitLimit)} limit (RAM + page files) · peak since start {Format.Bytes(m.CommitPeak)}";
        RamUsedWidth = new GridLength(m.InUseFraction, GridUnitType.Star);
        RamCacheWidth = new GridLength(m.CacheFraction, GridUnitType.Star);
        RamFreeWidth = new GridLength(Math.Max(0, 1 - m.InUseFraction - m.CacheFraction), GridUnitType.Star);
        CommitUsedWidth = new GridLength(m.CommitFraction, GridUnitType.Star);
        CommitFreeWidth = new GridLength(1 - m.CommitFraction, GridUnitType.Star);
        CommitPeakPosition = m.CommitPeakFraction;
        PageFileConfigText = "Setting: " + m.Config.Describe();
        PageFiles.Clear();
        foreach (var u in m.Usage) { PageFiles.Add(u); }

        _advice = Advise(m, WindowsLetter);
        (AdviceTitle, AdviceText, AdviceAction, AdviceIsGood) = _advice.Verdict switch
        {
            PageFileVerdict.NoPageFile => ("No page file", "This PC has no page file. Some apps refuse to run without one, Windows can't save crash information, and when memory runs out apps close suddenly. Let Windows manage it.", "Let Windows manage it", false),
            PageFileVerdict.SwitchToSystemManaged => ("Page file too small for how you use this PC",
                $"Your custom page file is smaller than this PC needs (memory use peaked at {Format.Bytes(m.CommitPeak)} of {Format.Bytes(m.CommitLimit)}, or the one on the Windows drive is below the {Format.Bytes(_advice.CrashDumpMinimumBytes)} Windows needs for crash information). Letting Windows manage it grows it when needed.",
                "Let Windows manage it", false),
            PageFileVerdict.AddOnSsd => ("Page file only on a hard disk",
                $"Your page file is on a hard disk, but drive {_ssdLetter}: is a much faster SSD. Adding a page file there makes memory-heavy moments smoother; the one on the Windows drive stays for crash information.",
                $"Add a page file on {_ssdLetter}:", false),
            _ => ("Best setting: keep it as it is",
                m.Config.AutomaticManaged
                    ? $"Windows manages the page file and there is plenty of headroom (peak {Format.Bytes(m.CommitPeak)} of {Format.Bytes(m.CommitLimit)}). This is the best setting for almost every PC."
                    : $"Your custom page file has enough headroom (peak {Format.Bytes(m.CommitPeak)} of {Format.Bytes(m.CommitLimit)}).",
                "", true),
        };
        HasAdviceAction = AdviceAction.Length > 0;

        // Rebuild the drive list only when it changed, so the user's choice in the ComboBox survives refreshes.
        var letters = _drives.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (!letters.SequenceEqual(PageFileDrives))
        {
            var keep = CustomDrive;
            _updatingDrives = true;
            PageFileDrives.Clear();
            foreach (var d in letters) { PageFileDrives.Add(d); }
            _updatingDrives = false;
            CustomDrive = keep is not null && letters.Contains(keep) ? keep : letters.Contains(WindowsLetter) ? WindowsLetter : letters.FirstOrDefault();
        }
        UpdateCustomBounds();
    }

    private bool _updatingDrives;

    partial void OnCustomDriveChanged(string? value)
    {
        if (_updatingDrives || value is null) { return; }
        UpdateCustomBounds();
    }

    /// <summary>Bounds of the "fixed size" option for the chosen drive (never touches the drive list).</summary>
    private void UpdateCustomBounds()
    {
        if (MemorySnapshot is not { } m || CustomDrive is null)
        {
            CustomAvailable = false;
            CustomHint = "No suitable drive found.";
            return;
        }
        var advice = Advise(m, CustomDrive);
        if (advice.CustomMinBytes is { } min && advice.CustomMaxBytes is { } max && advice.CustomSuggestedBytes is { } sug)
        {
            // Round inwards so the slider never leaves the safe range.
            CustomMinGb = Math.Ceiling(min / (double)GB * 10) / 10;
            CustomMaxGb = Math.Floor(max / (double)GB * 10) / 10;
            CustomAvailable = CustomMaxGb >= CustomMinGb;
            if (CustomGb < CustomMinGb || CustomGb > CustomMaxGb) { CustomGb = Math.Clamp(Math.Round(sug / (double)GB, 1), CustomMinGb, CustomMaxGb); }
            CustomHint = $"Between {CustomMinGb.ToString("F1", C)} GB (enough for crash information) and {CustomMaxGb.ToString("F1", C)} GB on drive {CustomDrive}:. Suggested for you: {(sug / (double)GB).ToString("F1", C)} GB.";
        }
        else
        {
            CustomAvailable = false;
            CustomHint = $"Not enough free space on drive {CustomDrive}: for a fixed page file. Pick another drive.";
        }
    }

    [RelayCommand]
    private Task ApplyAdviceAsync()
    {
        if (_advice is null || MemorySnapshot is not { } m) { return Task.CompletedTask; }
        var windows = WindowsLetter;
        return _advice.Verdict switch
        {
            PageFileVerdict.NoPageFile or PageFileVerdict.SwitchToSystemManaged =>
                ApplyPageFileAsync(new PageFileConfig(true, []), "Page file: let Windows manage it",
                    "Windows will manage the page file and grow it when needed. Per-drive custom settings no longer apply" +
                    (m.Config.Entries.Count > 0 ? $" ({m.Config.Describe()})." : ".")),
            PageFileVerdict.AddOnSsd when _ssdLetter is not null =>
                ApplyPageFileAsync(new PageFileConfig(false, WithWindowsKept(m.Config, windows, m.CrashDumpType, m.PhysicalTotal).Append(new PageFileEntry(_ssdLetter, 0, 0)).ToList()),
                    $"Page file: add one on {_ssdLetter}:",
                    $"A page file managed by Windows is added on drive {_ssdLetter}: (SSD). The page file on the Windows drive {windows}: stays, so crash information can still be saved."),
            _ => Task.CompletedTask,
        };
    }

    [RelayCommand]
    private Task ApplyCustomAsync()
    {
        if (MemorySnapshot is not { } m || CustomDrive is not { Length: 1 } drive || !_drives.ContainsKey(drive)) { return Task.CompletedTask; }
        // Re-check the bounds for exactly this drive right before writing.
        var advice = Advise(m, drive);
        if (advice.CustomMinBytes is not { } min || advice.CustomMaxBytes is not { } max) { CustomAvailable = false; return Task.CompletedTask; }
        var mb = Math.Clamp((long)Math.Round(CustomGb * 1024), (min + (1L << 20) - 1) >> 20, max >> 20);
        var gbText = (mb / 1024.0).ToString("F1", C);
        var windows = WindowsLetter;
        var entries = WithWindowsKept(m.Config, windows, m.CrashDumpType, m.PhysicalTotal).Where(e => e.Letter != drive).ToList();
        entries.Add(new PageFileEntry(drive, mb, mb));
        return ApplyPageFileAsync(new PageFileConfig(false, entries), $"Page file: {drive}: fixed {gbText} GB",
            $"Drive {drive}: gets a fixed page file of {gbText} GB." + (drive != windows ? $" The page file on the Windows drive {windows}: stays." : ""));
    }

    /// <summary>
    /// The current per-drive entries, making sure the Windows drive keeps a page file: managed by Windows if it had none,
    /// and raised to the crash-dump minimum if it is a fixed size below it.
    /// </summary>
    private static List<PageFileEntry> WithWindowsKept(PageFileConfig current, string windows, int crashDumpType, long ram)
    {
        List<PageFileEntry> list = current.AutomaticManaged ? [] : current.Entries.ToList();
        var minMb = (PageFileAdvisor.CrashDumpMinimum(ram, crashDumpType) + (1L << 20) - 1) >> 20;
        var w = list.FirstOrDefault(e => e.Letter == windows);
        if (w is null) { list.Add(new PageFileEntry(windows, 0, 0)); }
        else if (w.MaximumMb != 0 && w.MaximumMb < minMb)
        {
            list.Remove(w);
            list.Add(new PageFileEntry(windows, Math.Max(w.InitialMb, minMb), minMb));
        }
        return list;
    }

    private async Task ApplyPageFileAsync(PageFileConfig target, string description, string explanation)
    {
        if (!_main.IsElevatedProcess) { MemoryMessage = "Changing the page file needs full power (administrator). Use \"Unlock full power\" at the bottom left."; return; }
        PageFileConfig before;
        try { before = await Task.Run(PageFile.Read).ConfigureAwait(true); }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            MemoryMessage = "✖ The current page-file setting could not be read: " + ex.Message;
            return;
        }
        // Some starting points can't be restored as they were (e.g. no page file at all, which PerfTuner never sets).
        var restorable = PageFile.IsWritable(before);
        var undoText = restorable
            ? "You can undo it any time in History."
            : $"Undo in History will let Windows manage the page file, not go back to \"{before.Describe()}\" (PerfTuner never leaves the Windows drive without a page file).";
        var answer = ThemedDialog.Show(explanation + "\n\nThe change takes effect after you restart the PC (PerfTuner won't restart it). " + undoText,
            "Change the page file", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) { return; }
        try
        {
            await Task.Run(() =>
            {
                // Record first, so even a failed or partial write can be undone.
                var journal = new MachineJournal();
                var id = journal.Record(new MachineChange
                {
                    Description = description,
                    PageFileBefore = restorable ? before : new PageFileConfig(true, []),
                    UndoNote = restorable ? null : $"was: {before.Describe()}",
                });
                PageFile.Write(target);
                journal.SetApplied(id, PageFile.Read());
            }).ConfigureAwait(true);
            MemoryMessage = $"✔ {description}. Takes effect after a restart.";
            RestartPending = true;
            CoolDown("memory");
            _main.AddActivity("You", MemoryMessage);
            await LoadMemoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or IOException or System.Runtime.InteropServices.COMException)
        {
            MemoryMessage = "✖ The page file could not be changed completely: " + ex.Message + " — the previous setting is recorded in History, so you can undo it there.";
        }
    }

    [RelayCommand]
    private Task FreeRamAsync() => _main.RamRefreshCommand.ExecuteAsync(null);
}
