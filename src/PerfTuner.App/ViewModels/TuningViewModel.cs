using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.Client;
using PerfTuner.Contracts;

namespace PerfTuner.App.ViewModels;

public sealed record RuleItem(AppRule Rule)
{
    public string Name => Rule.ImageName;
    public string Actions => string.Join(", ", new[]
    {
        Rule.EfficiencyMode == true ? "efficiency mode" : null,
        Rule.Priority is { } p ? $"priority {p}" : null,
        Rule.Cores is { } c ? $"cores: {c}" : null,
        Rule.MemoryPriority is { } m ? $"memory priority {m}" : null,
    }.Where(x => x is not null));
}

public sealed record ContractRow(string Name, string Profile, string Scope, string Bounds, string RateLimit, string Reversibility, string Aborts, string Outcome, string Experiment);

/// <summary>Tuning tab: profile (Safe / Balanced / Advanced), one-click modes, auto-calm, background memory trim, app rules, tuning contracts.</summary>
public sealed partial class TuningViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _editing;

    public TuningViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<RuleItem> Rules { get; } = [];
    public ObservableCollection<ContractRow> Contracts { get; } = [];

    [ObservableProperty] private bool _profileSafe = true;
    [ObservableProperty] private bool _profileBalanced;
    [ObservableProperty] private bool _profileAdvanced;
    [ObservableProperty] private string _profileText = "";
    [ObservableProperty] private bool _modeOff = true;
    [ObservableProperty] private bool _modeFocus;
    [ObservableProperty] private bool _modeQuiet;
    [ObservableProperty] private bool _autoCalm;
    [ObservableProperty] private double _autoCalmThreshold = 25;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _foregroundInfo = "";
    [ObservableProperty] private RuleItem? _selectedRule;

    partial void OnProfileSafeChanged(bool value) => _editing = true;
    partial void OnProfileBalancedChanged(bool value) => _editing = true;
    partial void OnProfileAdvancedChanged(bool value) => _editing = true;

    private TuningProfile SelectedProfile => ProfileAdvanced ? TuningProfile.Advanced : ProfileBalanced ? TuningProfile.Balanced : TuningProfile.Safe;
    partial void OnModeOffChanged(bool value) => _editing = true;
    partial void OnModeFocusChanged(bool value) => _editing = true;
    partial void OnModeQuietChanged(bool value) => _editing = true;
    partial void OnAutoCalmChanged(bool value) => _editing = true;
    partial void OnAutoCalmThresholdChanged(double value) => _editing = true;

    /// <summary>Called with every status poll; does not overwrite unsaved edits.</summary>
    public void ApplyStatus(StatusResponse s)
    {
        ForegroundInfo = s.ForegroundKnown
            ? "Foreground app is known — modes and auto-calm can act."
            : "Foreground app unknown — automatic throttling waits (keep this app open as administrator; minimised is fine).";
        ProfileText = s.Profile switch
        {
            TuningProfile.Safe => "Active: Safe — nothing happens automatically.",
            TuningProfile.Balanced => "Active: Balanced — modes, auto-calm, rules and the memory governor may act, all within their contracts.",
            _ => "Active: Advanced — Balanced plus the regression autopilot (autonomous A/B experiments, kept only when proven).",
        };
        if (_editing)
        {
            return;
        }
        ProfileSafe = s.Profile == TuningProfile.Safe;
        ProfileBalanced = s.Profile == TuningProfile.Balanced;
        ProfileAdvanced = s.Profile == TuningProfile.Advanced;
        ModeOff = s.Mode == TuningMode.Off;
        ModeFocus = s.Mode == TuningMode.Focus;
        ModeQuiet = s.Mode == TuningMode.Quiet;
        AutoCalm = s.AutoCalm;
        AutoCalmThreshold = s.AutoCalmThresholdPercent;
        _editing = false;
    }

    [RelayCommand]
    private async Task ApplyOptionsAsync()
    {
        if (_main.Client is not { } client)
        {
            Message = "Not connected.";
            return;
        }
        var mode = ModeFocus ? TuningMode.Focus : ModeQuiet ? TuningMode.Quiet : TuningMode.Off;
        var profile = SelectedProfile;
        var first = await Call(() => client.SetProfileAsync(profile)).ConfigureAwait(true);
        Message = first.StartsWith('✖')
            ? first
            : await Call(() => client.SetTuningOptionsAsync(profile == TuningProfile.Safe, mode, AutoCalm, (int)Math.Round(AutoCalmThreshold))).ConfigureAwait(true);
        _editing = false;
    }

    [RelayCommand]
    private void DiscardEdits() => _editing = false;

    [RelayCommand]
    private async Task TrimBackgroundAsync()
    {
        if (_main.Client is { } client)
        {
            Message = await Call(() => client.TrimBackgroundMemoryAsync()).ConfigureAwait(true);
        }
    }

    public async Task LoadContractsAsync()
    {
        if (_main.Client is not { } client) { return; }
        try
        {
            var r = await client.GetContractsAsync().ConfigureAwait(true);
            Contracts.Clear();
            foreach (var c in r.Contracts)
            {
                Contracts.Add(new ContractRow(c.Name, c.MinimumProfile.ToString(), c.Scope, c.Bounds, c.RateLimit, c.Reversibility,
                    string.Join(Environment.NewLine, c.AbortConditions), c.ExpectedOutcome, c.Experimentable ? "yes" : "–"));
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or OperationCanceledException)
        {
            Message = "Could not load contracts: " + ex.Message;
        }
    }

    public async Task LoadRulesAsync()
    {
        if (_main.Client is not { } client)
        {
            return;
        }
        try
        {
            var rules = await client.GetRulesAsync().ConfigureAwait(true);
            Rules.Clear();
            foreach (var r in rules.Rules)
            {
                Rules.Add(new RuleItem(r));
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or OperationCanceledException)
        {
            Message = "Could not load rules: " + ex.Message;
        }
    }

    public async Task<string> AddOrReplaceRuleAsync(AppRule rule)
    {
        await LoadRulesAsync().ConfigureAwait(true);
        var list = Rules.Select(r => r.Rule).Where(r => !string.Equals(r.ImageName, rule.ImageName, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(rule);
        if (list.Count > ProtocolConstants.MaxRules)
        {
            return $"At most {ProtocolConstants.MaxRules} rules.";
        }
        var msg = await SaveRulesAsync(list).ConfigureAwait(true);
        return $"Rule saved for {rule.ImageName}. {msg}";
    }

    [RelayCommand]
    private async Task RemoveRuleAsync()
    {
        if (SelectedRule is not { } selected)
        {
            Message = "Select a rule first.";
            return;
        }
        Message = await SaveRulesAsync(Rules.Select(r => r.Rule).Where(r => r != selected.Rule).ToList()).ConfigureAwait(true);
    }

    private async Task<string> SaveRulesAsync(List<AppRule> rules)
    {
        if (_main.Client is not { } client)
        {
            return "Not connected.";
        }
        var msg = await Call(() => client.SetRulesAsync([.. rules])).ConfigureAwait(true);
        await LoadRulesAsync().ConfigureAwait(true);
        return msg;
    }

    private static async Task<string> Call(Func<Task<ActionResultResponse>> call)
    {
        try
        {
            var r = await call().ConfigureAwait(true);
            return (r.Success ? "✔ " : "✖ ") + r.Message;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or OperationCanceledException)
        {
            return "✖ " + ex.Message;
        }
    }
}
