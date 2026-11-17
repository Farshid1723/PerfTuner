using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.Client;
using PerfTuner.Contracts;
using PerfTuner.Platform.Windows.Settings;

namespace PerfTuner.App.ViewModels;

/// <summary>One startup app with its measured cost. Numeric properties drive column sorting.</summary>
public sealed partial class StartupRow : ObservableObject
{
    public required StartupApp App { get; init; }
    public string Name => App.Name;
    public string Source => App.LocationText;
    public string Command => App.Command;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _impact = "–";
    [ObservableProperty] private int _impactRank;              // High 3, Medium 2, Low 1, not running 0 (for sorting)
    [ObservableProperty] private double _startedAfter = double.MaxValue;
    [ObservableProperty] private string _startedText = "–";
    [ObservableProperty] private double _cpuSeconds;
    [ObservableProperty] private string _cpuText = "–";
    [ObservableProperty] private double _diskMb;
    [ObservableProperty] private string _diskText = "–";
    [ObservableProperty] private double _bootDelayMs;
    [ObservableProperty] private string _bootDelayText = "–";

    public string StateText => Enabled ? "On" : "Off";

    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(StateText));
}

public sealed record BootPoint(string When, double Seconds, string Text, double BarWidth);

public sealed record Recommendation(string Glyph, string Title, string Detail, string? ActionText, string? ActionId);

/// <summary>Per-boot cache of startup measurements, so numbers taken right after boot survive app restarts.</summary>
public sealed record StartupCache
{
    public long BootUnixMs { get; init; }
    public Dictionary<string, StartupMeasurement> Measurements { get; init; } = [];
}

[JsonSerializable(typeof(StartupCache))]
internal sealed partial class StartupCacheJsonContext : JsonSerializerContext;

/// <summary>
/// Startup page: how long Windows takes to boot (Windows' own boot diagnostics), what slowed it,
/// a sortable table of startup apps with their measured cost, and concrete recommendations —
/// each with a one-click, undoable action.
/// </summary>
public sealed partial class StartupViewModel : ObservableObject
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerfTuner", "startup-impact.json");

    private readonly MainViewModel _main;
    private BootCulprit[] _culprits = [];

    public StartupViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<StartupRow> Apps { get; } = [];
    public ObservableCollection<BootPoint> Boots { get; } = [];
    public ObservableCollection<Recommendation> Recommendations { get; } = [];

    [ObservableProperty] private string _lastBootValue = "–";
    [ObservableProperty] private string _lastBootDetail = "Loading…";
    [ObservableProperty] private string _bootVerdict = "";
    [ObservableProperty] private string _bootAverage = "–";
    [ObservableProperty] private string _appsSummary = "";
    [ObservableProperty] private string _measureNote = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private StartupRow? _selected;
    [ObservableProperty] private bool _busy;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (Busy) { return; }
        Busy = true;
        try
        {
            await LoadBootAsync().ConfigureAwait(true);
            await LoadAppsAsync().ConfigureAwait(true);
            BuildRecommendations();
        }
        finally
        {
            Busy = false;
        }
    }

    // ---------------------------------------------------------------- boot time

    private async Task LoadBootAsync()
    {
        var bootLocal = StartupImpact.BootTimeUtc.ToLocalTime();
        var c = CultureInfo.CurrentCulture;
        LastBootDetail = $"PC started {bootLocal.ToString("g", c)} · up {Format.Uptime(Environment.TickCount64 / 1000)}";
        if (_main.Client is not { } client)
        {
            BootVerdict = "Connecting to the engine…";
            return;
        }
        try
        {
            var r = await client.GetBootReportAsync().ConfigureAwait(true);
            _culprits = r.Culprits;
            var boots = r.Records.Where(b => b.Kind == "boot" && b.TotalSeconds > 0).OrderByDescending(b => b.TimeUnixMs).Take(10).ToList();
            Boots.Clear();
            if (!r.Available || boots.Count == 0)
            {
                LastBootValue = "–";
                BootVerdict = r.Available ? "Windows hasn't recorded a boot yet." : "Boot timing needs full power (administrator) — Windows keeps it in a protected log.";
                return;
            }
            var last = boots[0];
            LastBootValue = $"{last.TotalSeconds.ToString("F0", c)} s";
            var avg = boots.Average(b => b.TotalSeconds);
            BootAverage = $"{avg.ToString("F0", c)} s average over the last {boots.Count} boots";
            BootVerdict = last.TotalSeconds switch
            {
                < 30 => "Fast — nothing to worry about.",
                < 60 => "Average. A few changes below can make it faster.",
                < 120 => "Slow. The recommendations below should help.",
                _ => "Very slow. Follow the recommendations below.",
            };
            if (last.MainPathSeconds > 0)
            {
                LastBootDetail += $" · desktop after {last.MainPathSeconds.ToString("F0", c)} s, ready after {last.TotalSeconds.ToString("F0", c)} s";
            }
            var max = Math.Max(1, boots.Max(b => b.TotalSeconds));
            foreach (var b in boots.AsEnumerable().Reverse())
            {
                Boots.Add(new BootPoint(DateTimeOffset.FromUnixTimeMilliseconds(b.TimeUnixMs).ToLocalTime().ToString("dd MMM", c),
                    b.TotalSeconds, $"{b.TotalSeconds.ToString("F0", c)} s", 160 * b.TotalSeconds / max));
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException)
        {
            BootVerdict = "Could not read boot history: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- startup apps

    private async Task LoadAppsAsync()
    {
        var bootMs = new DateTimeOffset(StartupImpact.BootTimeUtc).ToUnixTimeMilliseconds();
        var cache = LoadCache();
        if (Math.Abs(cache.BootUnixMs - bootMs) > 60_000) { cache = new StartupCache { BootUnixMs = bootMs }; }

        var list = await Task.Run(() =>
        {
            var apps = StartupApps.List();
            var result = new List<(StartupApp App, StartupMeasurement M)>();
            foreach (var a in apps)
            {
                // Keep the first measurement taken after this boot (closest to the real startup cost).
                var key = a.Location + "|" + a.Name;
                if (!cache.Measurements.TryGetValue(key, out var m) || !m.Running)
                {
                    m = StartupImpact.Measure(a.Command);
                    if (m.Running) { cache.Measurements[key] = m; }
                }
                result.Add((a, m));
            }
            return result;
        }).ConfigureAwait(true);
        SaveCache(cache);

        var c = CultureInfo.CurrentCulture;
        Apps.Clear();
        foreach (var (app, m) in list)
        {
            var impact = app.Enabled || m.Running ? StartupImpact.Rate(m) : "Disabled";
            var row = new StartupRow
            {
                App = app,
                Enabled = app.Enabled,
                Impact = impact,
                ImpactRank = impact switch { "High" => 3, "Medium" => 2, "Low" => 1, _ => 0 },
                StartedAfter = m.StartedAfterBootSeconds ?? double.MaxValue,
                StartedText = m.StartedAfterBootSeconds is { } s ? (s > 3600 ? "later" : $"+{s.ToString("F0", c)} s") : "–",
                CpuSeconds = m.CpuSeconds ?? 0,
                CpuText = m.CpuSeconds is { } cpu ? $"{cpu.ToString("F1", c)} s" : "–",
                DiskMb = m.DiskReadMb ?? 0,
                DiskText = m.DiskReadMb is { } d ? $"{d.ToString("F0", c)} MB" : "–",
            };
            var culprit = _culprits.FirstOrDefault(x => x.Kind == "app" && StartupImpact.ExeName(app.Command) is { } exe
                && x.Name.Contains(exe, StringComparison.OrdinalIgnoreCase));
            if (culprit is not null)
            {
                row.BootDelayMs = culprit.AvgDelayMs;
                row.BootDelayText = $"{(culprit.AvgDelayMs / 1000).ToString("F1", c)} s";
            }
            Apps.Add(row);
        }
        var enabled = Apps.Count(a => a.Enabled);
        var high = Apps.Count(a => a.Enabled && a.ImpactRank == 3);
        AppsSummary = $"{enabled} of {Apps.Count} apps start with Windows · {high} with high impact";
        MeasureNote = TimeSpan.FromMilliseconds(Environment.TickCount64) < TimeSpan.FromMinutes(15)
            ? "Measured right after this boot — close to the real startup cost."
            : "Measured some time after boot, so CPU and disk include later use. For exact numbers, open this page within 15 minutes of starting the PC.";
    }

    [RelayCommand]
    private void Toggle(StartupRow? row)
    {
        if (row is null) { return; }
        if (row.App.RequiresAdmin && !_main.IsElevatedProcess)
        {
            Message = $"\"{row.Name}\" starts for all users; changing it needs full power (administrator).";
            return;
        }
        try
        {
            var enable = !row.Enabled;
            var previous = StartupApps.SetEnabled(row.App, enable);
            _main.SystemSettings.Journal.Record(new SettingChange
            {
                Kind = SettingKind.StartupApp, StartupLocation = row.App.Location, StartupName = row.App.Name, StartupOriginal = previous,
                Description = $"Startup app \"{row.Name}\" → {(enable ? "enabled" : "disabled")}",
            });
            row.Enabled = enable;
            Message = enable ? $"✔ {row.Name} will start with Windows again." : $"✔ {row.Name} won't start with Windows any more (undo in History).";
            _main.Changes.AddLocalHistory(Message);
            BuildRecommendations();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            Message = "✖ " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- recommendations

    private void BuildRecommendations()
    {
        Recommendations.Clear();
        var c = CultureInfo.CurrentCulture;
        var heavy = Apps.Where(a => a.Enabled && a.ImpactRank == 3).ToList();
        if (heavy.Count > 0)
        {
            Recommendations.Add(new Recommendation("", $"{heavy.Count} startup app(s) have a high impact",
                string.Join(", ", heavy.Take(4).Select(h => h.Name)) + (heavy.Count > 4 ? " …" : "") +
                ". Turn off the ones you don't need right after you sign in — you can still open them any time.", "Show them", "sort-impact"));
        }
        var enabled = Apps.Count(a => a.Enabled);
        if (enabled > 10)
        {
            Recommendations.Add(new Recommendation("", $"{enabled} apps start with Windows",
                "Every app that starts with Windows competes for the disk while you wait for the desktop. Most PCs need fewer than 8.", null, null));
        }
        var fast = WindowsTweaks.Find("fast-startup");
        if (fast is not null && !WindowsTweaks.IsApplied(fast) && WindowsTweaks.HibernationAvailable())
        {
            Recommendations.Add(new Recommendation("", "Turn on Fast Startup",
                "Windows resumes its core after a shutdown instead of starting from scratch — usually 30–50 % faster boots. Undoable in History.", "Turn on", "fast-startup"));
        }
        foreach (var x in _culprits.Where(x => x.Kind is "driver" or "service").OrderByDescending(x => x.AvgDelayMs).Take(2))
        {
            Recommendations.Add(new Recommendation("", $"{x.Name} slows your boots",
                $"Windows measured an average delay of {(x.AvgDelayMs / 1000).ToString("F1", c)} s ({x.Occurrences}×). Update this {x.Kind} from the device maker's website or Windows Update.", null, null));
        }
        if (Recommendations.Count == 0)
        {
            Recommendations.Add(new Recommendation("", "Your startup is in good shape", "No heavy startup apps and nothing slowing the boot.", null, null));
        }
    }

    /// <summary>Raised to ask the view to sort the table by impact (DataGrid sorting lives in the view).</summary>
    public event Action? SortByImpactRequested;

    [RelayCommand]
    private void RunRecommendation(Recommendation? r)
    {
        switch (r?.ActionId)
        {
            case "sort-impact":
                SortByImpactRequested?.Invoke();
                break;
            case "fast-startup" when WindowsTweaks.Find("fast-startup") is { } t:
                if (!_main.IsElevatedProcess) { Message = "Fast Startup needs full power (administrator)."; return; }
                try
                {
                    WindowsTweaks.Apply(t, _main.SystemSettings.Journal);
                    Message = "✔ Fast Startup is on. It takes effect at your next shutdown (undo in History).";
                    _main.Changes.AddLocalHistory(Message);
                    BuildRecommendations();
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
                {
                    Message = "✖ " + ex.Message;
                }
                break;
        }
    }

    private static StartupCache LoadCache()
    {
        try
        {
            if (File.Exists(CachePath) && new FileInfo(CachePath).Length < 512_000)
            {
                return JsonSerializer.Deserialize(File.ReadAllText(CachePath), StartupCacheJsonContext.Default.StartupCache) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new();
    }

    private static void SaveCache(StartupCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache, StartupCacheJsonContext.Default.StartupCache));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
