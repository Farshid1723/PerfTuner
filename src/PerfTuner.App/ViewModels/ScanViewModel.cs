using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.SystemTools;
using PerfTuner.Client;
using PerfTuner.Contracts;
using PerfTuner.Platform.Windows.Settings;

namespace PerfTuner.App.ViewModels;

public enum ScanPhase { Idle, Scanning, Results, Fixing, Done }

public enum ScanState { Pending, Running, Ok, Issue, Info, Fixed, Failed }

/// <summary>What pressing Fix does for an item. Only real, measured findings; only safe, mostly reversible fixes.</summary>
public enum FixKind { None, CleanTemp, RamRefresh, CalmHogs, FastVisuals, QuickSwitches, OpenPage }

public sealed partial class ScanItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    public required string Hint { get; init; }

    [ObservableProperty] private ScanState _state = ScanState.Pending;
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _selected;
    [ObservableProperty] private FixKind _fix;
    [ObservableProperty] private int _openPage = -1;

    public int Weight { get; set; }
    public object? Payload { get; set; }

    public bool CanFix => Fix is not (FixKind.None or FixKind.OpenPage) && State == ScanState.Issue;
    public bool CanOpen => Fix == FixKind.OpenPage && OpenPage >= 0;

    partial void OnStateChanged(ScanState value) { OnPropertyChanged(nameof(CanFix)); OnPropertyChanged(nameof(CanOpen)); }
    partial void OnFixChanged(FixKind value) { OnPropertyChanged(nameof(CanFix)); OnPropertyChanged(nameof(CanOpen)); }
}

/// <summary>
/// PulseScan: the one-button checkup. Every item is a real measurement (no invented "registry
/// errors"); every fix is either reversible through UndoVault or clearly announced as permanent
/// (temp cleanup). Items that need a human decision open the right page instead of "fixing".
/// </summary>
public sealed partial class ScanViewModel : ObservableObject
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    public ScanViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<ScanItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsBusy), nameof(HasResults), nameof(IsDone))]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand), nameof(FixCommand))]
    private ScanPhase _phase = ScanPhase.Idle;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _headline = "Ready to scan";
    [ObservableProperty] private string _subline = "Checks junk, memory, CPU hogs, throttling, leaks, drives, startup and Windows settings.";
    [ObservableProperty] private int _issueCount;
    [ObservableProperty] private string _performanceGrade = "–";
    [ObservableProperty] private string _stabilityGrade = "–";
    [ObservableProperty] private string _hygieneGrade = "–";

    public bool IsIdle => Phase == ScanPhase.Idle;
    public bool IsBusy => Phase is ScanPhase.Scanning or ScanPhase.Fixing;
    public bool HasResults => Phase is ScanPhase.Results;
    public bool IsDone => Phase is ScanPhase.Done;

    private bool CanStart() => Phase is ScanPhase.Idle or ScanPhase.Results or ScanPhase.Done;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartScanAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Phase = ScanPhase.Scanning;
        Items.Clear();
        var checks = new (Feature F, string Name, Func<ScanItem, Task> Run)[]
        {
            (Brand.CleanSweep, "Junk files", CheckJunkAsync),
            (Brand.RamRefresh, "Memory", CheckMemoryAsync),
            (Brand.HogTamer, "Background CPU hogs", CheckHogsAsync),
            (Brand.ClockCheck, "CPU throttling", CheckClockAsync),
            (Brand.LeakHunter, "Memory leaks", CheckLeaksAsync),
            (Brand.StallSense, "Freezes & stalls", CheckStallsAsync),
            (Brand.DriveDoctor, "Drive health", CheckDrivesAsync),
            (Brand.BootTrim, "Startup apps", CheckStartupAsync),
            (Brand.FastVisuals, "Visual effects", CheckVisualsAsync),
            (Brand.QuickSwitches, "Windows 11 settings", CheckSwitchesAsync),
        };
        foreach (var (f, name, _) in checks)
        {
            var item = new ScanItem { Name = name, Glyph = f.Glyph, Hint = $"{f.Name}: {f.Hint}" };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ScanItem.Selected) or nameof(ScanItem.State)) { FixCommand.NotifyCanExecuteChanged(); }
            };
            Items.Add(item);
        }

        for (var i = 0; i < checks.Length && !ct.IsCancellationRequested; i++)
        {
            var item = Items[i];
            item.State = ScanState.Running;
            Headline = $"Scanning: {item.Name.ToLower(C)}";
            var started = Environment.TickCount64;
            try
            {
                await checks[i].Run(item).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or RemoteErrorException
                or InvalidOperationException or System.Management.ManagementException or System.Runtime.InteropServices.COMException or OperationCanceledException)
            {
                item.State = ScanState.Info;
                item.Result = "Couldn't check";
                item.Detail = ex.Message;
            }
            // Keep each step visible long enough to read (the checks themselves are fast).
            var elapsed = Environment.TickCount64 - started;
            if (elapsed < 350) { await Task.Delay((int)(350 - elapsed), CancellationToken.None).ConfigureAwait(true); }
            Progress = 100.0 * (i + 1) / checks.Length;
        }

        if (ct.IsCancellationRequested)
        {
            Phase = ScanPhase.Idle;
            Headline = "Scan stopped";
            Progress = 0;
            return;
        }
        Summarize();
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    private void Summarize()
    {
        IssueCount = Items.Count(i => i.State == ScanState.Issue);
        int Score(params string[] names)
        {
            var hit = Items.Where(i => names.Contains(i.Name) && i.State == ScanState.Issue).Sum(i => Math.Max(1, i.Weight));
            return hit;
        }
        static string Grade(int penalty) => penalty switch { 0 => "GOOD", <= 2 => "FAIR", _ => "POOR" };
        PerformanceGrade = Grade(Score("Memory", "Background CPU hogs", "CPU throttling", "Visual effects"));
        StabilityGrade = Grade(Score("Memory leaks", "Freezes & stalls", "Drive health"));
        HygieneGrade = Grade(Score("Junk files", "Startup apps", "Windows 11 settings"));
        Headline = IssueCount == 0 ? "Your PC is in great shape" : $"{IssueCount} thing{(IssueCount == 1 ? "" : "s")} to improve";
        Subline = IssueCount == 0
            ? "Nothing to fix. The scan only reports real, measured findings."
            : "Selected items are fixed with one click. Everything except junk cleanup can be undone in History.";
        Phase = ScanPhase.Results;
    }

    private bool CanFixNow() => Phase == ScanPhase.Results && Items.Any(i => i.Selected && i.CanFix);

    [RelayCommand(CanExecute = nameof(CanFixNow))]
    private async Task FixAsync()
    {
        var todo = Items.Where(i => i.Selected && i.CanFix).ToList();
        if (todo.Any(i => i.Fix == FixKind.CleanTemp))
        {
            var answer = Controls.ThemedDialog.Show("Junk cleanup permanently deletes temporary files older than 2 days (not via the Recycle Bin). Everything else the scan fixes can be undone in History.\n\nContinue?",
                "Scan & Fix", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) { return; }
        }
        Phase = ScanPhase.Fixing;
        Progress = 0;
        for (var k = 0; k < todo.Count; k++)
        {
            var item = todo[k];
            Headline = $"Fixing: {item.Name.ToLower(C)}";
            item.State = ScanState.Running;
            try
            {
                item.Result = await ApplyAsync(item).ConfigureAwait(true);
                item.State = ScanState.Fixed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or RemoteErrorException or InvalidOperationException)
            {
                item.State = ScanState.Failed;
                item.Result = "Not fixed";
                item.Detail = ex.Message;
            }
            Progress = 100.0 * (k + 1) / todo.Count;
            await Task.Delay(250, CancellationToken.None).ConfigureAwait(true);
        }
        var fixedCount = todo.Count(i => i.State == ScanState.Fixed);
        Headline = $"Fix completed — {fixedCount} of {todo.Count} done";
        Subline = "Undo any change in History. Want more speed? Try Turbo mode on the Speed page.";
        Phase = ScanPhase.Done;
    }

    [RelayCommand]
    private void Open(ScanItem? item)
    {
        if (item is { OpenPage: >= 0 }) { _main.SelectedTab = item.OpenPage; }
    }

    [RelayCommand]
    private void Back()
    {
        Phase = ScanPhase.Idle;
        Progress = 0;
        Headline = "Ready to scan";
    }

    // ------------------------------------------------------------------ checks

    private static void Issue(ScanItem i, string result, string detail, FixKind fix, bool selected, int weight = 1)
    {
        i.State = ScanState.Issue; i.Result = result; i.Detail = detail; i.Fix = fix; i.Selected = selected; i.Weight = weight;
    }

    private static void Ok(ScanItem i, string result, string detail = "") { i.State = ScanState.Ok; i.Result = result; i.Detail = detail; }

    private async Task CheckJunkAsync(ScanItem i)
    {
        var elevated = _main.IsElevatedProcess;
        var scan = await Task.Run(() => SystemSettingsViewModel.ScanTempNow(elevated)).ConfigureAwait(true);
        var mb = scan.Bytes / 1048576.0;
        if (mb >= 50) { Issue(i, $"{mb:F0} MB", $"{scan.Files:N0} temporary files older than 2 days.", FixKind.CleanTemp, true, mb > 1024 ? 2 : 1); }
        else { Ok(i, $"{mb:F0} MB", "Very little junk."); }
    }

    private async Task CheckMemoryAsync(ScanItem i)
    {
        var s = _main.LatestSample;
        if (s is null) { i.State = ScanState.Info; i.Result = "No data yet"; return; }
        var freePct = s.Value.MemoryTotalMb > 0 ? 100.0 * s.Value.MemoryAvailableMb / s.Value.MemoryTotalMb : 100;
        await Task.CompletedTask.ConfigureAwait(true);
        if (freePct < 20) { Issue(i, $"{freePct:F0}% free", "Memory is getting tight; background apps can hand some back.", FixKind.RamRefresh, true, freePct < 10 ? 2 : 1); }
        else { Ok(i, $"{freePct:F0}% free"); }
    }

    private async Task CheckHogsAsync(ScanItem i)
    {
        if (_main.Client is not { } client) { i.State = ScanState.Info; i.Result = "Engine offline"; return; }
        await client.GetProcessesAsync(ProtocolConstants.MaxProcessListCount).ConfigureAwait(true); // primes CPU deltas
        await Task.Delay(1500).ConfigureAwait(true);
        var list = await client.GetProcessesAsync(ProtocolConstants.MaxProcessListCount).ConfigureAwait(true);
        var hogs = list.Processes.Where(p => p.Controllable && p.Pid != list.ForegroundPid && p.CpuPercent >= 10 && p.Applied.Length == 0).Take(3).ToList();
        if (hogs.Count > 0)
        {
            i.Payload = hogs;
            Issue(i, $"{hogs.Count} app(s)", string.Join(", ", hogs.Select(h => $"{h.Name} {h.CpuPercent:F0}%")) + " — can run in efficiency mode.", FixKind.CalmHogs, true, 2);
        }
        else { Ok(i, "None", "No background app is hogging the CPU."); }
    }

    private async Task CheckClockAsync(ScanItem i)
    {
        if (_main.Client is not { } client) { i.State = ScanState.Info; i.Result = "Engine offline"; return; }
        var ins = await client.GetInsightsAsync().ConfigureAwait(true);
        if (!ins.Clock.Available) { i.State = ScanState.Info; i.Result = "Not available"; return; }
        if (ins.Clock.LimitPercent < 90 || ins.Clock.LimitedShareRecentPercent >= 20)
        {
            Issue(i, $"Limited to {ins.Clock.LimitPercent:F0}%", "Windows is holding the CPU back (heat, power plan or battery). Check cooling, or see Troubleshoot → \"PC gets slower after a while\".", FixKind.OpenPage, false, 2);
            i.OpenPage = MainViewModel.PageCleanOptimize;
        }
        else { Ok(i, $"{ins.Clock.EffectiveMhz / 1000:F1} GHz", "Running at full speed."); }
    }

    private async Task CheckLeaksAsync(ScanItem i)
    {
        if (_main.Client is not { } client) { i.State = ScanState.Info; i.Result = "Engine offline"; return; }
        var ins = await client.GetInsightsAsync().ConfigureAwait(true);
        if (ins.Leaks.Length > 0)
        {
            Issue(i, $"{ins.Leaks.Length} app(s)", string.Join(", ", ins.Leaks.Take(3).Select(l => $"{l.Name} +{l.GrowthMbPerHour:F0} MB/h")) + ". Restarting them frees the memory.", FixKind.OpenPage, false);
            i.OpenPage = MainViewModel.PageHealthCheck;
        }
        else if (ins.LeakObservationMinutes < 15) { i.State = ScanState.Info; i.Result = "Still watching"; i.Detail = "The leak check needs 15 minutes of watching."; }
        else { Ok(i, "None"); }
    }

    private async Task CheckStallsAsync(ScanItem i)
    {
        if (_main.Client is not { } client) { i.State = ScanState.Info; i.Result = "Engine offline"; return; }
        var t = await client.GetTimelineAsync(0, 50).ConfigureAwait(true);
        if (t.StallsLastHour > 0)
        {
            Issue(i, $"{t.StallsLastHour} in the last hour", "Open Troubleshoot → \"Why was my PC slow?\" to see what caused them.", FixKind.OpenPage, false, t.StallsLastHour > 3 ? 2 : 1);
            i.OpenPage = MainViewModel.PageDiagnose;
        }
        else { Ok(i, "None"); }
    }

    private async Task CheckDrivesAsync(ScanItem i)
    {
        var drives = await Task.Run(DriveHealth.Read).ConfigureAwait(true);
        var bad = drives.Where(d => !d.Verdict.StartsWith("Healthy", StringComparison.Ordinal)).ToList();
        if (bad.Count > 0)
        {
            Issue(i, $"{bad.Count} need attention", string.Join("; ", bad.Select(d => $"{d.Name}: {d.Verdict}")), FixKind.OpenPage, false, 3);
            i.OpenPage = MainViewModel.PageHealthCheck;
        }
        else { Ok(i, $"{drives.Count} healthy"); }
    }

    private async Task CheckStartupAsync(ScanItem i)
    {
        var apps = await Task.Run(StartupApps.List).ConfigureAwait(true);
        var enabled = apps.Count(a => a.Enabled);
        if (enabled > 6)
        {
            Issue(i, $"{enabled} apps", "Many apps start with Windows and slow down your boot. Review them on the Startup page — you decide which to keep.", FixKind.OpenPage, false);
            i.OpenPage = MainViewModel.PageStartup;
        }
        else { Ok(i, $"{enabled} apps"); }
    }

    private async Task CheckVisualsAsync(ScanItem i)
    {
        var best = await Task.Run(() => VisualEffects.IsBestPerformance(VisualEffects.Capture())).ConfigureAwait(true);
        if (!best) { Issue(i, "Animations on", "Turning animations and shadows off makes menus feel snappier (optional).", FixKind.FastVisuals, false); }
        else { Ok(i, "Optimized"); }
    }

    private async Task CheckSwitchesAsync(ScanItem i)
    {
        var elevated = _main.IsElevatedProcess;
        var missing = await Task.Run(() => WindowsTweaks.Catalog.Count(t => !WindowsTweaks.IsApplied(t) && (!t.RequiresAdmin || elevated))).ConfigureAwait(true);
        if (missing > 0) { Issue(i, $"{missing} available", "Recommended Windows settings that aren't on yet (optional, each undoable on the Clean page).", FixKind.QuickSwitches, false); }
        else { Ok(i, "All applied"); }
    }

    // ------------------------------------------------------------------ fixes

    private async Task<string> ApplyAsync(ScanItem item)
    {
        var client = _main.Client;
        switch (item.Fix)
        {
            case FixKind.CleanTemp:
                var (deleted, freed) = await _main.SystemSettings.CleanTempCoreAsync().ConfigureAwait(true);
                return $"{freed / 1048576.0:F0} MB freed ({deleted:N0} files)";
            case FixKind.RamRefresh when client is not null:
                var r = await client.TrimBackgroundMemoryAsync().ConfigureAwait(true);
                return r.Success ? r.Message : throw new InvalidOperationException(r.Message);
            case FixKind.CalmHogs when client is not null && item.Payload is List<ProcessEntry> hogs:
                var n = 0;
                foreach (var h in hogs)
                {
                    var a = await client.ProcessActionAsync(new ProcessActionRequest { Id = 0, Pid = h.Pid, CreateTime = h.CreateTime, Action = ProcessAction.EfficiencyMode, Enable = true }).ConfigureAwait(true);
                    if (a.Success) { n++; }
                }
                return $"{n} app(s) calmed";
            case FixKind.FastVisuals:
                _main.SystemSettings.ApplyFastVisuals();
                return "Animations off";
            case FixKind.QuickSwitches:
                return _main.SystemSettings.ApplyRecommendedSwitches();
            default:
                throw new InvalidOperationException("The engine is not connected.");
        }
    }
}
