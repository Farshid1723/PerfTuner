using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.Client;
using PerfTuner.Contracts;

namespace PerfTuner.App.ViewModels;

public sealed class ProcessItem
{
    public required ProcessEntry Entry { get; init; }
    public string Name => Entry.Name;
    public int Pid => Entry.Pid;
    public string Cpu => Entry.CpuPercent.ToString("F1", CultureInfo.CurrentCulture) + " %";
    public double CpuValue => Entry.CpuPercent;
    public string Memory => Entry.PrivateMb.ToString("F0", CultureInfo.CurrentCulture) + " MB";
    public double MemoryValue => Entry.PrivateMb;
    public string Priority => Entry.Priority;
    public string Applied => Entry.Applied;
    public string Status => Entry.Controllable ? (Entry.Applied.Length > 0 ? "Tuned" : "") : "Protected: " + Entry.Reason;
    public bool Controllable => Entry.Controllable;
}

/// <summary>
/// Processes tab: live list (refreshed every 2 s while the tab is visible) and the
/// per-process action buttons. Every action goes to the service, which re-checks policy,
/// identity (PID + creation time), rate limits and the circuit breaker.
/// </summary>
public sealed partial class ProcessesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private ProcessEntry[] _all = [];

    public ProcessesViewModel(MainViewModel main)
    {
        _main = main;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    public ObservableCollection<ProcessItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionTitle))]
    [NotifyCanExecuteChangedFor(nameof(SetPriorityCommand), nameof(EfficiencyCommand), nameof(CoresCommand), nameof(MemoryPriorityCommand),
        nameof(SoftCapCommand), nameof(TrimCommand), nameof(EndCommand), nameof(SaveRuleCommand), nameof(GpuPreferenceCommand))]
    private ProcessItem? _selected;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _message = "Select a process to see the available actions.";
    [ObservableProperty] private string _summary = "";

    public bool HasSelection => Selected is not null;

    public string SelectionTitle => Selected is null
        ? "No process selected"
        : $"{Selected.Name} (PID {Selected.Pid}) — {Selected.Priority}" + (Selected.Applied.Length > 0 ? $" · active: {Selected.Applied}" : "");

    partial void OnFilterChanged(string value) => ApplyFilter();

    public void SetActive(bool active)
    {
        if (active)
        {
            _ = RefreshAsync();
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
            Summary = "Not connected to the service.";
            return;
        }
        try
        {
            var list = await client.GetProcessesAsync(ProtocolConstants.MaxProcessListCount).ConfigureAwait(true);
            _all = list.Processes;
            Summary = $"{list.TotalProcesses} processes · showing the {list.Processes.Length} busiest" +
                (list.ForegroundPid is { } fg ? $" · foreground PID {fg}" : "");
            ApplyFilter();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or OperationCanceledException)
        {
            Summary = "Could not load processes: " + ex.Message;
        }
    }

    private void ApplyFilter()
    {
        var keep = Selected?.Pid;
        Items.Clear();
        foreach (var e in _all)
        {
            if (Filter.Length == 0 || e.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) || e.Pid.ToString(CultureInfo.InvariantCulture) == Filter)
            {
                var item = new ProcessItem { Entry = e };
                Items.Add(item);
                if (e.Pid == keep)
                {
                    Selected = item;
                }
            }
        }
        if (keep is not null && Selected?.Pid != keep)
        {
            Selected = null;
        }
        OnPropertyChanged(nameof(SelectionTitle));
    }

    private bool CanAct() => Selected is { Controllable: true } && _main.IsAdmin;
    private bool CanSelect() => Selected is not null && _main.IsAdmin;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task SetPriorityAsync(string level) =>
        SendAsync(ProcessAction.SetPriority, t => t with { Priority = Enum.Parse<PriorityLevel>(level) });

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task EfficiencyAsync(string enable) =>
        SendAsync(ProcessAction.EfficiencyMode, t => t with { Enable = bool.Parse(enable) });

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task CoresAsync(string preset) =>
        SendAsync(ProcessAction.CpuCores, t => t with { Cores = Enum.Parse<CpuCorePreset>(preset) });

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task MemoryPriorityAsync(string level) =>
        SendAsync(ProcessAction.MemoryPriority, t => t with { MemoryPriority = Enum.Parse<MemoryPriorityLevel>(level) });

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task SoftCapAsync(string megabytes) =>
        SendAsync(ProcessAction.SoftMemoryCap, t => t with { CapMb = int.Parse(megabytes, CultureInfo.InvariantCulture) });

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task TrimAsync() => SendAsync(ProcessAction.TrimMemory, t => t);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task EndAsync()
    {
        var target = Selected!;
        var answer = Controls.ThemedDialog.Show(
            $"End \"{target.Name}\" (PID {target.Pid})?\n\nThis cannot be undone. Unsaved work in that app will be lost.",
            "End process", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            await SendAsync(ProcessAction.EndProcess, t => t with { Confirm = true }).ConfigureAwait(true);
        }
    }

    /// <summary>Adds a permanent rule for the selected app (applies automatically when Safe Mode is off).</summary>
    [RelayCommand(CanExecute = nameof(CanSelect))]
    private async Task SaveRuleAsync(string kind)
    {
        var name = Selected!.Name;
        if (!RequestValidator.IsSafeName(name, ProtocolConstants.MaxProcessNameLength))
        {
            Message = $"\"{name}\" contains characters that rules cannot match.";
            return;
        }
        var rule = kind switch
        {
            "efficiency" => new AppRule { ImageName = name, EfficiencyMode = true },
            "below" => new AppRule { ImageName = name, Priority = PriorityLevel.BelowNormal },
            "above" => new AppRule { ImageName = name, Priority = PriorityLevel.AboveNormal },
            "ecores" => new AppRule { ImageName = name, Cores = CpuCorePreset.EfficiencyCores },
            _ => new AppRule { ImageName = name, MemoryPriority = MemoryPriorityLevel.Low },
        };
        Message = await _main.Tuning.AddOrReplaceRuleAsync(rule).ConfigureAwait(true);
    }

    /// <summary>Per-app graphics preference (Settings → Display → Graphics). Applies next time the app starts.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectAny))]
    private void GpuPreference(string preference)
    {
        var target = Selected!;
        var path = PerfTuner.Platform.Windows.ProcessInfo.TryGetImagePath(target.Pid);
        if (path is null)
        {
            Message = $"✖ Cannot read the program path of {target.Name} (protected or elevated app).";
            return;
        }
        try
        {
            var pref = preference == "high" ? 2 : 1;
            PerfTuner.Platform.Windows.Settings.WindowsTweaks.SetGpuPreference(path, pref, _main.SystemSettings.Journal);
            Message = $"✔ {target.Name} will use the {(pref == 2 ? "high-performance" : "power-saving")} GPU next time it starts.";
            _main.Changes.AddLocalHistory(Message);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Message = "✖ " + ex.Message;
        }
    }

    private bool CanSelectAny() => Selected is not null;

    private async Task SendAsync(ProcessAction action, Func<ProcessActionRequest, ProcessActionRequest> configure)
    {
        if (_main.Client is not { } client || Selected is not { } target)
        {
            return;
        }
        var request = configure(new ProcessActionRequest { Id = 0, Pid = target.Pid, CreateTime = target.Entry.CreateTime, Action = action });
        try
        {
            var result = await client.ProcessActionAsync(request).ConfigureAwait(true);
            Message = (result.Success ? "✔ " : "✖ ") + result.Message;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or OperationCanceledException)
        {
            Message = "✖ " + ex.Message;
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Re-evaluates button states after the connection role changes.</summary>
    public void NotifyRoleChanged()
    {
        SetPriorityCommand.NotifyCanExecuteChanged();
        EfficiencyCommand.NotifyCanExecuteChanged();
        CoresCommand.NotifyCanExecuteChanged();
        MemoryPriorityCommand.NotifyCanExecuteChanged();
        SoftCapCommand.NotifyCanExecuteChanged();
        TrimCommand.NotifyCanExecuteChanged();
        EndCommand.NotifyCanExecuteChanged();
        SaveRuleCommand.NotifyCanExecuteChanged();
    }
}
