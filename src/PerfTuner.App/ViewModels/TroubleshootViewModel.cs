using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.SystemTools;
using PerfTuner.Client;
using PerfTuner.Contracts;

namespace PerfTuner.App.ViewModels;

public enum Verdict { None, Good, Warning, Problem }

/// <summary>
/// One everyday question ("Why was my PC slow just now?") with a Check button, a plain-language
/// answer and — when there is something to do — one action button.
/// </summary>
public sealed partial class TroubleQuestion : ObservableObject
{
    public required string Id { get; init; }
    public required string Glyph { get; init; }
    public required string Question { get; init; }
    public required string Explainer { get; init; }
    public required Func<TroubleQuestion, Task> Check { get; init; }

    [ObservableProperty] private bool _running;
    [ObservableProperty] private Verdict _verdict;
    [ObservableProperty] private string _answer = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string? _actionText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAction))]
    private Func<Task>? _action;

    public bool HasAction => Action is not null;
    public bool HasAnswer => Verdict != Verdict.None;

    partial void OnVerdictChanged(Verdict value) => OnPropertyChanged(nameof(HasAnswer));

    public void Set(Verdict verdict, string answer, string details = "", string? actionText = null, Func<Task>? action = null)
    {
        Verdict = verdict;
        Answer = answer;
        Details = details;
        ActionText = actionText;
        Action = action;
    }
}

/// <summary>
/// Troubleshoot page: replaces the old expert "Diagnose" views with questions people actually ask.
/// Each check uses the engine's measurements (StallSense, LagScan, BurstRadar, DiskLens, ClockCheck,
/// LeakHunter, MemGuard, DriveDoctor) and answers in one or two plain sentences.
/// </summary>
public sealed partial class TroubleshootViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    public TroubleshootViewModel(MainViewModel main)
    {
        _main = main;
        Questions =
        [
            new() { Id = "slow", Glyph = "", Question = "Why was my PC slow just now?",
                    Explainer = "PerfTuner records every freeze or slowdown while it runs. This explains the most recent one.", Check = CheckSlowAsync },
            new() { Id = "stutter", Glyph = "", Question = "Games stutter or audio crackles",
                    Explainer = "Measures for 10 seconds how long each driver blocks the CPU (the usual cause).", Check = CheckStutterAsync },
            new() { Id = "net", Glyph = "", Question = "Internet feels laggy",
                    Explainer = "Looks for traffic spikes and lost packets in the last minute.", Check = CheckNetworkAsync },
            new() { Id = "disk", Glyph = "", Question = "Opening files and apps is slow",
                    Explainer = "Measures how long your drive takes to answer, and which app keeps it busy.", Check = CheckDiskAsync },
            new() { Id = "heat", Glyph = "", Question = "PC gets slower after a while (heat?)",
                    Explainer = "Checks whether Windows is holding the CPU back because of heat, battery or power settings.", Check = CheckClockAsync },
            new() { Id = "memory", Glyph = "", Question = "Memory keeps filling up",
                    Explainer = "Checks memory pressure and finds apps whose memory use never stops growing.", Check = CheckMemoryAsync },
            new() { Id = "drives", Glyph = "", Question = "Is my drive healthy?",
                    Explainer = "Reads the health data your drives report (wear, temperature, errors).", Check = CheckDrivesAsync },
        ];
    }

    public ObservableCollection<TroubleQuestion> Questions { get; }

    [ObservableProperty] private bool _checkingAll;

    [RelayCommand]
    private async Task CheckAsync(TroubleQuestion? q)
    {
        if (q is null || q.Running) { return; }
        q.Running = true;
        q.Set(Verdict.None, "Checking…");
        try
        {
            if (_main.Client is null && q.Id != "drives")
            {
                q.Set(Verdict.Warning, "The engine isn't connected yet — try again in a few seconds.");
                return;
            }
            await q.Check(q).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or InvalidOperationException)
        {
            q.Set(Verdict.Warning, "Couldn't check: " + ex.Message);
        }
        finally
        {
            q.Running = false;
        }
    }

    [RelayCommand]
    private async Task CheckAllAsync()
    {
        CheckingAll = true;
        try
        {
            foreach (var q in Questions.Where(q => q.Id != "stutter")) { await CheckAsync(q).ConfigureAwait(true); }
        }
        finally
        {
            CheckingAll = false;
        }
    }

    [RelayCommand]
    private static async Task RunActionAsync(TroubleQuestion? q)
    {
        if (q?.Action is { } a) { await a().ConfigureAwait(true); }
    }

    private PerfTunerClient Client => _main.Client ?? throw new InvalidOperationException("engine not connected");

    // ---------------------------------------------------------------- checks

    private async Task CheckSlowAsync(TroubleQuestion q)
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds();
        var t = await Client.GetTimelineAsync(since, ProtocolConstants.MaxTimelineEvents).ConfigureAwait(true);
        var stall = t.Events.Where(e => e.IsStall).OrderByDescending(e => e.TimeUnixMs).FirstOrDefault();
        if (stall is null)
        {
            q.Set(Verdict.Good, "No slowdown was recorded in the last 30 minutes.",
                "If it happens again, come back here right after — PerfTuner keeps watching in the background. (Freezes shorter than ~3 seconds may not be recorded.)");
            return;
        }
        var x = await Client.ExplainStallAsync(stall.Id).ConfigureAwait(true);
        var when = DateTimeOffset.FromUnixTimeMilliseconds(stall.TimeUnixMs).ToLocalTime().ToString("HH:mm", C);
        var top = x.Causes.FirstOrDefault();
        var details = string.Join(Environment.NewLine, x.Causes.Take(3).Select(c => $"• {c.Title} — {string.Join("; ", c.Evidence.Take(2))}"));
        if (top is { SuggestedPid: { } pid, SuggestedCreateTime: { } create, SuggestedProcessName: { } name })
        {
            q.Set(Verdict.Problem, $"At {when}: {x.Story}", details, $"Calm {name} down", () => CalmAsync(q, pid, create, name));
        }
        else
        {
            q.Set(x.Found ? Verdict.Warning : Verdict.Good, $"At {when}: {x.Story}", details);
        }
    }

    private async Task CalmAsync(TroubleQuestion q, int pid, long create, string name)
    {
        var r = await Client.ProcessActionAsync(new ProcessActionRequest
        {
            Id = 0, Pid = pid, CreateTime = create, Action = ProcessAction.EfficiencyMode, Enable = true,
        }).ConfigureAwait(true);
        q.Set(r.Success ? Verdict.Good : Verdict.Warning,
            r.Success ? $"✔ {name} now runs in efficiency mode, so it can't slow down the app you're using. Undo any time in History." : "✖ " + r.Message);
    }

    private async Task CheckStutterAsync(TroubleQuestion q)
    {
        var start = await Client.StartLatencyScanAsync(10).ConfigureAwait(true);
        if (!start.Success)
        {
            q.Set(Verdict.Warning, start.Message, "Driver timing can only be measured with full power (administrator).",
                _main.ShowUnlock ? "Unlock full power" : null, _main.ShowUnlock ? () => { _main.RestartAsAdministratorCommand.Execute(null); return Task.CompletedTask; } : null);
            return;
        }
        q.Answer = "Measuring for 10 seconds — keep doing what stutters (play the game, the audio…)";
        LatencyReport report;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            await Task.Delay(1500).ConfigureAwait(true);
            report = (await Client.GetInsightsAsync().ConfigureAwait(true)).Latency;
        }
        while (report.State == LatencyScanState.Running && DateTime.UtcNow < deadline);

        var worst = report.Drivers.OrderByDescending(d => Math.Max(d.MaxDpcUs, d.MaxIsrUs)).FirstOrDefault();
        var details = string.Join(Environment.NewLine, report.Drivers.OrderByDescending(d => Math.Max(d.MaxDpcUs, d.MaxIsrUs)).Take(3)
            .Select(d => $"• {d.Driver}: up to {Math.Max(d.MaxDpcUs, d.MaxIsrUs).ToString("N0", C)} µs"));
        var verdict = report.WorstUs switch { < 500 => Verdict.Good, < 2000 => Verdict.Warning, _ => Verdict.Problem };
        var answer = verdict switch
        {
            Verdict.Good => "Drivers are behaving well — stutter is probably not caused by a driver. Try Turbo mode while gaming.",
            _ when worst is not null => $"{worst.Driver} blocked the CPU for up to {(Math.Max(worst.MaxDpcUs, worst.MaxIsrUs) / 1000).ToString("F1", C)} ms. " +
                "Update this driver (Windows Update or the maker's website); for Wi-Fi/Bluetooth drivers, try a cable connection to confirm.",
            _ => report.Verdict,
        };
        q.Set(verdict, answer, details, verdict == Verdict.Good ? "Turn on Turbo" : null,
            verdict == Verdict.Good ? () => _main.SetModeCommand.ExecuteAsync(nameof(PerfMode.Turbo)) : null);
    }

    private async Task CheckNetworkAsync(TroubleQuestion q)
    {
        var n = await Client.GetNetworkHealthAsync().ConfigureAwait(true);
        if (!n.Available)
        {
            q.Set(Verdict.Warning, "Network checks need full power (administrator).", n.Message,
                _main.ShowUnlock ? "Unlock full power" : null, _main.ShowUnlock ? () => { _main.RestartAsAdministratorCommand.Execute(null); return Task.CompletedTask; } : null);
            return;
        }
        var recent = n.Bursts.Where(b => b.TimeUnixMs > DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds()).ToList();
        var hog = recent.Where(b => b.ProcessName is not null).GroupBy(b => b.ProcessName!).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        if (n.RetransmitRatePercent >= 2)
        {
            q.Set(Verdict.Problem, $"{n.RetransmitRatePercent.ToString("F1", C)} % of packets had to be sent again — the connection itself is losing data.",
                "Usually Wi-Fi interference or a weak signal: move closer to the router, use 5 GHz or a cable, or restart the router.");
        }
        else if (recent.Count > 0)
        {
            q.Set(Verdict.Warning, $"{recent.Count} traffic spike(s) in the last 2 minutes" + (hog is null ? "." : $", mostly from {hog}."),
                "Spikes from downloads, cloud sync or updates fill your connection for a moment and make games and calls lag. Pause the app causing them while you play or call.");
        }
        else
        {
            q.Set(Verdict.Good, "Your connection looks healthy: no spikes and no lost packets in the last minute.",
                $"Now: ↓ {n.RxMbpsNow.ToString("F1", C)} Mbit/s, ↑ {n.TxMbpsNow.ToString("F1", C)} Mbit/s. If lag continues, the problem is likely outside this PC (router or provider).");
        }
    }

    private async Task CheckDiskAsync(TroubleQuestion q)
    {
        var io = await Client.GetIoLatencyAsync().ConfigureAwait(true);
        if (!io.Available)
        {
            q.Set(Verdict.Warning, "Disk timing needs full power (administrator).", io.Message,
                _main.ShowUnlock ? "Unlock full power" : null, _main.ShowUnlock ? () => { _main.RestartAsAdministratorCommand.Execute(null); return Task.CompletedTask; } : null);
            return;
        }
        var p99 = Math.Max(io.ReadTotal.P99Ms, io.WriteTotal.P99Ms);
        var busy = io.Processes.Where(p => p.Pid > 0).OrderByDescending(p => p.MegabytesTransferred).FirstOrDefault();
        var details = $"Typical answer: {io.ReadTotal.P50Ms.ToString("F1", C)} ms · slowest 1 %: {p99.ToString("F0", C)} ms" +
            (busy is null ? "" : $" · busiest app: {busy.Name} ({busy.MegabytesTransferred.ToString("F0", C)} MB in the last minute)");
        if (io.ReadTotal.Count + io.WriteTotal.Count == 0)
        {
            q.Set(Verdict.Good, "The drive was idle in the last minute — try again while the slowness happens.");
        }
        else if (p99 < 30)
        {
            q.Set(Verdict.Good, "Your drive answers quickly. Slowness is probably not caused by the disk.", details);
        }
        else if (p99 < 150)
        {
            q.Set(Verdict.Warning, "Your drive is sometimes slow to answer" + (busy is null ? "." : $" — {busy.Name} keeps it busy."), details,
                busy is null ? null : $"Slow {busy.Name} down", busy is null ? null : () => LowerIoAppAsync(q, busy.Pid, busy.Name));
        }
        else
        {
            q.Set(Verdict.Problem, "Your drive is very slow to answer." + (busy is null ? " If it's an old hard disk (HDD), an SSD is the biggest upgrade you can make." : $" {busy.Name} is hammering it."),
                details, busy is null ? null : $"Slow {busy.Name} down", busy is null ? null : () => LowerIoAppAsync(q, busy.Pid, busy.Name));
        }
    }

    private async Task LowerIoAppAsync(TroubleQuestion q, int pid, string name)
    {
        var procs = await Client.GetProcessesAsync(ProtocolConstants.MaxProcessListCount).ConfigureAwait(true);
        var p = procs.Processes.FirstOrDefault(x => x.Pid == pid);
        if (p is null) { q.Set(Verdict.Good, $"{name} has already exited."); return; }
        var r = await Client.ProcessActionAsync(new ProcessActionRequest
        {
            Id = 0, Pid = pid, CreateTime = p.CreateTime, Action = ProcessAction.SetPriority, Priority = PriorityLevel.BelowNormal,
        }).ConfigureAwait(true);
        q.Set(r.Success ? Verdict.Good : Verdict.Warning, r.Success ? $"✔ {name} now has low priority, so your apps get the disk first. Undo in History." : "✖ " + r.Message);
    }

    private async Task CheckClockAsync(TroubleQuestion q)
    {
        var i = await Client.GetInsightsAsync().ConfigureAwait(true);
        var clock = i.Clock;
        if (!clock.Available)
        {
            q.Set(Verdict.Warning, "This PC doesn't report its CPU speed limits.");
            return;
        }
        var details = $"Now {(clock.EffectiveMhz / 1000).ToString("F2", C)} GHz ({clock.PerformancePercent.ToString("F0", C)} % of the {(clock.BaseMhz / 1000).ToString("F2", C)} GHz base speed). " +
            $"Held back {clock.LimitedShareRecentPercent.ToString("F0", C)} % of the last 10 minutes.";
        if (clock.LimitedShareRecentPercent >= 20 || clock.LimitPercent is > 0 and < 85)
        {
            q.Set(Verdict.Problem, "Yes — Windows is holding your CPU back, usually because it's too hot or running on battery / power saving.",
                details + Environment.NewLine + "Plug in the charger, keep the vents free, and clean dust from the fans. Silent mode lowers heat if fans are loud.",
                "Turn on Silent", () => _main.SetModeCommand.ExecuteAsync(nameof(PerfMode.Silent)));
        }
        else
        {
            q.Set(Verdict.Good, "No — your CPU runs at full speed and isn't being held back.", details);
        }
    }

    private async Task CheckMemoryAsync(TroubleQuestion q)
    {
        var mem = await Client.GetMemoryGovernorAsync().ConfigureAwait(true);
        var ins = await Client.GetInsightsAsync().ConfigureAwait(true);
        var leak = ins.Leaks.OrderByDescending(l => l.GrowthMbPerHour).FirstOrDefault();
        var details = $"{(mem.AvailableMb / 1024).ToString("F1", C)} GB free. " +
            (ins.LeakObservationMinutes < 15 ? $"Leak check needs 15 minutes of watching ({ins.LeakObservationMinutes.ToString("F0", C)} so far)." : "");
        if (leak is not null)
        {
            q.Set(Verdict.Problem, $"{leak.Name} keeps using more memory (+{leak.GrowthMbPerHour.ToString("F0", C)} MB per hour, now {leak.PrivateMb.ToString("F0", C)} MB). Restarting it gives the memory back.",
                details, "Free RAM now", () => _main.RamRefreshCommand.ExecuteAsync(null));
        }
        else if (mem.Behavior is MemoryBehavior.Pressure or MemoryBehavior.Thrashing)
        {
            q.Set(Verdict.Problem, "Memory is running out, so Windows is swapping to disk (that's what feels slow).",
                details + Environment.NewLine + "Smart mode frees memory automatically when this happens.", "Turn on Smart", () => _main.SetModeCommand.ExecuteAsync(nameof(PerfMode.Smart)));
        }
        else
        {
            q.Set(Verdict.Good, "Memory is fine: enough free and no app with a runaway memory use.", details);
        }
    }

    private async Task CheckDrivesAsync(TroubleQuestion q)
    {
        var drives = await Task.Run(DriveHealth.Read).ConfigureAwait(true);
        var bad = drives.Where(d => !d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase)).ToList();
        var details = string.Join(Environment.NewLine, drives.Select(d => $"• {d.Name}: {d.Verdict}"));
        q.Set(bad.Count == 0 ? Verdict.Good : Verdict.Problem,
            bad.Count == 0 ? $"All {drives.Count} drive(s) report healthy." : $"{string.Join(", ", bad.Select(b => b.Name))} needs attention — back up your files soon.",
            details);
    }
}
