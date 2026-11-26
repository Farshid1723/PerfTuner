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

/// <summary>One line in a scan result's details. Lines with a tick box are individual fixes the user can pick.</summary>
public sealed partial class ScanChoice : ObservableObject
{
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public bool Selectable { get; init; }
    public object? Payload { get; init; }
    [ObservableProperty] private bool _selected;
    public bool HasDescription => Description.Length > 0;
}

public sealed partial class ScanItem : ObservableObject
{
    private bool _syncing;

    public ScanItem()
    {
        Choices.CollectionChanged += (_, e) =>
        {
            foreach (ScanChoice c in e.NewItems ?? Array.Empty<ScanChoice>()) { c.PropertyChanged += OnChoiceChanged; }
            OnPropertyChanged(nameof(HasChoices));
            OnPropertyChanged(nameof(DetailsLabel));
        };
    }

    public required string Name { get; init; }
    public required string Glyph { get; init; }
    public required string Hint { get; init; }

    [ObservableProperty] private ScanState _state = ScanState.Pending;
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _selected;
    [ObservableProperty] private FixKind _fix;
    [ObservableProperty] private int _openPage = -1;

    [ObservableProperty] private bool _expanded;

    public int Weight { get; set; }
    public object? Payload { get; set; }

    /// <summary>What the result is made of: the settings, apps or drives behind the one-line summary.</summary>
    public ObservableCollection<ScanChoice> Choices { get; } = [];
    public bool HasChoices => Choices.Count > 0;
    public bool HasSelectableChoices => Choices.Any(c => c.Selectable);
    public IEnumerable<ScanChoice> SelectedChoices => Choices.Where(c => c.Selectable && c.Selected);
    public string DetailsLabel => Choices.Count == 1 ? "Show details (1)" : $"Show details ({Choices.Count})";

    [RelayCommand]
    private void ToggleDetails() => Expanded = !Expanded;

    // The row's "Fix" box and the per-line boxes stay in step: ticking Fix picks every line,
    // unticking every line clears Fix, and picking any line ticks Fix.
    partial void OnSelectedChanged(bool value)
    {
        if (_syncing || !HasSelectableChoices) { return; }
        _syncing = true;
        if (value && !SelectedChoices.Any()) { foreach (var c in Choices.Where(c => c.Selectable)) { c.Selected = true; } }
        else if (!value) { foreach (var c in Choices) { c.Selected = false; } }
        _syncing = false;
    }

    private void OnChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncing || e.PropertyName != nameof(ScanChoice.Selected)) { return; }
        _syncing = true;
        Selected = SelectedChoices.Any();
        _syncing = false;
    }

    public bool CanFix => Fix is not (FixKind.None or FixKind.OpenPage) && State == ScanState.Issue;
    public bool CanOpen => Fix == FixKind.OpenPage && OpenPage >= 0;

    public bool HasDetail => Detail.Length > 0;

    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(HasDetail));
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

    private bool CanFixNow() => Phase == ScanPhase.Results && Items.Any(i => i.Selected && i.CanFix && (!i.HasSelectableChoices || i.SelectedChoices.Any()));

    [RelayCommand(CanExecute = nameof(CanFixNow))]
    private async Task FixAsync()
    {
        var todo = Items.Where(i => i.Selected && i.CanFix && (!i.HasSelectableChoices || i.SelectedChoices.Any())).ToList();
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
            foreach (var h in hogs) { i.Choices.Add(new ScanChoice { Title = $"{h.Name} — {h.CpuPercent:F0}% CPU", Description = "Runs in efficiency mode: lower priority and power-saving cores. Undo in History." }); }
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
            foreach (var l in ins.Leaks) { i.Choices.Add(new ScanChoice { Title = $"{l.Name} (process {l.Pid}) — {l.PrivateMb:N0} MB, growing {l.GrowthMbPerHour:N0} MB per hour", Description = "Close and reopen it to give the memory back." }); }
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
        foreach (var d in drives) { i.Choices.Add(new ScanChoice { Title = $"{d.Name}: {d.Verdict}" }); }
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
        foreach (var a in apps.Where(a => a.Enabled)) { i.Choices.Add(new ScanChoice { Title = a.Name }); }
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
        var missing = await Task.Run(() => WindowsTweaks.ScanSuggestions(elevated)).ConfigureAwait(true);
        foreach (var t in missing)
        {
            i.Choices.Add(new ScanChoice { Title = t.Title, Description = $"{t.Description} Takes effect: {t.TakesEffect}.", Selectable = true, Payload = t });
        }
        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Take(3).Select(t => t.Title)) + (missing.Count > 3 ? $" and {missing.Count - 3} more" : "");
            Issue(i, $"{missing.Count} available", $"{names}. Optional; tick the ones you want, each can be undone in History or on the Windows page.", FixKind.QuickSwitches, false);
            i.Expanded = true;
        }
        else { Ok(i, "All applied", "Every speed and convenience setting is on. More settings are on the Windows page."); }
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
                return _main.SystemSettings.ApplySwitches(item.SelectedChoices.Select(c => c.Payload).OfType<WindowsTweak>().ToList());
            default:
                throw new InvalidOperationException("The engine is not connected.");
        }
    }
}
