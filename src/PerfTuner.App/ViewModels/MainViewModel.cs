using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.Client;
using PerfTuner.Contracts;
using PerfTuner.App.SystemTools;
using PerfTuner.Platform.Windows;

namespace PerfTuner.App.ViewModels;

public sealed record ProcessRow(string Name, int Pid, string Disk, string Net);

public enum EngineKind { None, Standalone, Service }

/// <summary>One entry of the sidebar.</summary>
public sealed record NavItem(int Page, string Glyph, string Title, string Subtitle);

/// <summary>One line in "What PerfTuner did" (automatic actions, shown on Home and History and as tray pop-ups).</summary>
public sealed record ActivityItem(string Time, string Source, string Text)
{
    public string Who => Brand.SourceName(Source);
}

/// <summary>
/// Presentation state, navigation, modes, notifications and the engine/connection life cycle.
/// <para>
/// Engine: if the always-on Windows service is installed and running, the GUI uses it.
/// Otherwise it starts the bundled engine itself (<see cref="StandaloneEngine"/>) with the GUI's
/// own rights — nothing to install. Metric pushes are coalesced onto the UI thread.
/// </para>
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    public const int ChartPoints = 60;

    // Pages: 0–7 are in the sidebar; Scan opens from Home.
    public const int PageHome = 0;
    public const int PageSpeed = 1;
    public const int PageClean = 2;
    public const int PageStartup = 3;
    public const int PageApps = 4;
    public const int PageTroubleshoot = 5;
    public const int PageHistory = 6;
    public const int PageSettings = 7;
    public const int PageScan = 8;
    public const int PageHelp = 9;
    public const int PageAbout = 10;
    public const int PageCockpit = 11;
    public const int PageSecurity = 12;
    public const int PageGetApps = 13;
    public const int PageWindows = 14;
    public const int PageUndocumented = 15;
    public const int LastPage = PageUndocumented;

    // Old names kept for the scan's "open" links.
    public const int PagePulse = PageHome;
    public const int PageCleanOptimize = PageClean;
    public const int PageHealthCheck = PageTroubleshoot;
    public const int PageDiagnose = PageTroubleshoot;

    public static IReadOnlyList<NavItem> NavItems { get; } =
    [
        new(PageHome, "\uE80F", "Home", "Health and quick actions"),
        new(PageCockpit, "\uEC4A", "Cockpit", "CPU · memory · disk · network"),
        new(PageSecurity, "\uEA18", "Security", "Find and close security gaps"),
        new(PageSpeed, "\uE945", "Speed", "Modes and free RAM"),
        new(PageClean, "\uE74D", "Clean", "Junk files and update leftovers"),
        new(PageWindows, "\uE770", "Windows", "Settings, features, updates, DNS"),
        new(PageUndocumented, "\uE7BA", "Undocumented", "Hidden settings · risky"),
        new(PageStartup, "\uE7E8", "Startup", "Boot time and startup apps"),
        new(PageApps, "\uE71D", "Apps", "Control any running app"),
        new(PageGetApps, "\uE719", "Get apps", "Install, update, remove apps"),
        new(PageTroubleshoot, "\uE9D9", "Troubleshoot", "Find out what's wrong"),
        new(PageHistory, "\uE81C", "History", "What changed · undo"),
        new(PageSettings, "\uE713", "Settings", "Themes, tray, engine"),
        new(PageHelp, "\uE897", "Help", "How everything works"),
        new(PageAbout, "\uE946", "About", "Creator, repo, strengths"),
    ];

    private readonly bool _externalEngine;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _freshnessTimer;
    private PerfTunerClient? _client;
    private StandaloneEngine? _engine;
    private MetricsPush? _latest;
    private int _uiUpdatePending;
    private long _lastSampleTick;
    private string _telemetryHealthRaw = "";
    private double _eventsPerSec;
    private long _eventsLost;
    private TuningProfile _profile;
    private int _autoCalmThreshold = 25;
    private readonly double[] _cpuRing = new double[ChartPoints];
    private readonly double[] _memRing = new double[ChartPoints];
    private readonly double[] _diskRing = new double[ChartPoints];
    private readonly double[] _netRing = new double[ChartPoints];

    /// <param name="externalEngine">--dev: connect to a standalone engine started by hand (never launch one).</param>
    public MainViewModel(bool externalEngine)
    {
        _externalEngine = externalEngine;
        _dispatcher = Dispatcher.CurrentDispatcher;
        IsElevatedProcess = ServiceInstaller.IsElevated();
        Processes = new ProcessesViewModel(this);
        Tuning = new TuningViewModel(this);
        SystemSettings = new SystemSettingsViewModel(this);
        Changes = new ChangesViewModel(this);
        Insights = new InsightsViewModel(this);
        Scan = new ScanViewModel(this);
        Startup = new StartupViewModel(this);
        Troubleshoot = new TroubleshootViewModel(this);
        Cockpit = new CockpitViewModel(this);
        Security = new SecurityViewModel(this);
        AppStore = new AppStoreViewModel(this);
        WindowsPage = new WindowsPageViewModel(this);
        Maintenance = new MaintenanceViewModel(this);
        Undocumented = new UndocumentedViewModel(this);
        BuiltIns = new BuiltInAppsViewModel(this);
        Desktops = new Desktop.DesktopHelper();
        Desktops.Problem += text => Undocumented.Message = text;
        _modeHotkeys = GuiSettings.Current.ModeHotkeys;
        ApplyDesktopHelpers();
        ApplyModeHotkeys();
        _selectedTheme = Themes.ThemeManager.Current;
        _freshnessTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _freshnessTimer.Tick += (_, _) => UpdateFreshness();
        _freshnessTimer.Start();
    }

    public bool IsElevatedProcess { get; }
    public bool ShowUnlock => !IsElevatedProcess;
    public ProcessesViewModel Processes { get; }
    public TuningViewModel Tuning { get; }
    public SystemSettingsViewModel SystemSettings { get; }
    public ChangesViewModel Changes { get; }
    public InsightsViewModel Insights { get; }
    public ScanViewModel Scan { get; }
    public StartupViewModel Startup { get; }
    public CockpitViewModel Cockpit { get; }
    public SecurityViewModel Security { get; }
    public AppStoreViewModel AppStore { get; }
    public WindowsPageViewModel WindowsPage { get; }
    public MaintenanceViewModel Maintenance { get; }
    public UndocumentedViewModel Undocumented { get; }
    public BuiltInAppsViewModel BuiltIns { get; }
    public Desktop.DesktopHelper Desktops { get; }

    /// <summary>Settings page: Ctrl+Alt+Shift+F1…F4 switch modes from anywhere.</summary>
    [ObservableProperty] private bool _modeHotkeys;
    [ObservableProperty] private string _hotkeyMessage = "";

    partial void OnModeHotkeysChanged(bool value)
    {
        GuiSettings.Update(g => g with { ModeHotkeys = value });
        ApplyModeHotkeys();
    }

    private void ApplyModeHotkeys()
    {
        var failed = Desktops.SetModeHotkeys(ModeHotkeys, mode => _ = SetModeAsync(mode));
        HotkeyMessage = !ModeHotkeys ? ""
            : failed.Count == 0 ? "✔ Ctrl+Alt+Shift+F1 Off · F2 Turbo · F3 Smart · F4 Silent"
            : "Already used by another program: " + string.Join(", ", failed);
    }

    /// <summary>Applies the saved virtual-desktop helper switches (Undocumented page).</summary>
    internal void ApplyDesktopHelpers()
    {
        var g = GuiSettings.Current;
        var supported = Platform.Windows.Undocumented.UndocumentedTweaks.Build >= 22000;
        Desktops.Configure(supported && g.DesktopTray, supported && g.DesktopOverlay, supported && g.DesktopHotkeys);
    }
    public TroubleshootViewModel Troubleshoot { get; }

    public PerfTunerClient? Client => Volatile.Read(ref _client);

    public MetricSample? LatestSample => Volatile.Read(ref _latest)?.Sample;

    public string Title => Brand.Product + (IsElevatedProcess ? "" : " (limited)");
    public string VersionText => "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0");

    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private EngineKind _engineMode = EngineKind.None;
    [ObservableProperty] private string _engineText = "Starting engine…";
    [ObservableProperty] private string _connectionText = "Starting…";

    // ------------------------------------------------------------------ navigation

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarIndex))]
    private int _selectedTab;

    /// <summary>Sidebar selection (−1 while a page that isn't in the sidebar is open).</summary>
    public int SidebarIndex
    {
        get
        {
            for (var i = 0; i < NavItems.Count; i++) { if (NavItems[i].Page == SelectedTab) { return i; } }
            return -1;
        }
        set { if (value >= 0 && value < NavItems.Count) { SelectedTab = NavItems[value].Page; } }
    }

    partial void OnSelectedTabChanged(int value)
    {
        Processes.SetActive(value == PageApps);
        Insights.SetActive(value == PageTroubleshoot);
        Changes.SetActive(value == PageHistory);
        Cockpit.SetActive(value == PageCockpit);
        Security.SetActive(value == PageSecurity);
        AppStore.SetActive(value == PageGetApps);
        WindowsPage.SetActive(value == PageWindows);
        Maintenance.SetActive(value == PageClean);
        Undocumented.SetActive(value == PageUndocumented);
        if (value == PageApps) { _ = Tuning.LoadRulesAsync(); }
        if (value is PageClean or PageWindows) { SystemSettings.Refresh(); }
        if (value == PageSpeed) { SystemSettings.Refresh(); }
        if (value == PageStartup) { _ = Startup.LoadAsync(); }
    }

    [RelayCommand]
    private void Navigate(string? page)
    {
        if (int.TryParse(page, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= LastPage)
        {
            SelectedTab = p;
        }
    }

    // ------------------------------------------------------------------ help & about

    /// <summary>Fixed project address (never built from input).</summary>
    public const string RepositoryUrl = "https://github.com/Farshid1723/PerfTuner";

    public static IReadOnlyList<HelpTopic> HelpTopicList => HelpTopics.All;

    [ObservableProperty] private HelpTopic _selectedHelpTopic = HelpTopics.All[0];

    /// <summary>"More help" on a "?" hint: open the Help page at that topic.</summary>
    [RelayCommand]
    private void OpenHelp(string? topic)
    {
        SelectedHelpTopic = HelpTopics.Find(topic);
        SelectedTab = PageHelp;
    }

    /// <summary>About → repository link. The URL is a compile-time constant; the shell opens it in the default browser.</summary>
    [RelayCommand]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand needs an instance method.")]
    private void OpenRepository()
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            System.Windows.Clipboard.SetText(RepositoryUrl);
            Controls.ThemedDialog.Show("No web browser could be opened, so the address was copied to the clipboard:\n\n" + RepositoryUrl, "Repository");
        }
    }

    /// <summary>Clean page → Space explorer on the Windows drive.</summary>
    [RelayCommand]
    private void OpenSpaceExplorer()
    {
        SelectedTab = PageCockpit;
        Cockpit.OpenSpaceFor(char.ToUpperInvariant(Environment.SystemDirectory[0]).ToString());
    }

    /// <summary>Home chart card → Cockpit with that part selected.</summary>
    [RelayCommand]
    private void OpenCockpit(string? part)
    {
        Cockpit.SelectPartCommand.Execute(part);
        SelectedTab = PageCockpit;
    }

    /// <summary>Home → "Scan &amp; Fix": open the scan page and start right away.</summary>
    [RelayCommand]
    private void ScanAndFix()
    {
        SelectedTab = PageScan;
        if (Scan.StartScanCommand.CanExecute(null)) { Scan.StartScanCommand.Execute(null); }
    }

    /// <summary>Home → "Clean junk": open Clean and measure what can be freed.</summary>
    [RelayCommand]
    private void CleanJunk()
    {
        SelectedTab = PageClean;
        _ = SystemSettings.ScanJunkAsync();
    }

    /// <summary>Relaunch elevated (UAC) — unlocks kernel-level features in the standalone engine.</summary>
    [RelayCommand]
    private void RestartAsAdministrator()
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        psi.ArgumentList.Add("--after-restart");
        try
        {
            System.Diagnostics.Process.Start(psi);
            App.ExitApplication(); // stops our engine gracefully; the elevated instance starts its own
        }
        catch (System.ComponentModel.Win32Exception)
        {
            OperationMessage = "Restart as administrator was cancelled.";
        }
    }

    private volatile bool _engineWanted = true;
    private volatile bool _exiting;

    public ObservableCollection<ProcessRow> TopProcesses { get; } = [];
    public ObservableCollection<ActivityItem> Activity { get; } = [];

    // ---- live values (Home)
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private string _cpuValue = "–";
    [ObservableProperty] private string _cpuDetail = "";
    [ObservableProperty] private string _memoryValue = "–";
    [ObservableProperty] private string _memoryDetail = "";
    [ObservableProperty] private string _diskValue = "–";
    [ObservableProperty] private string _diskDetail = "";
    [ObservableProperty] private string _networkValue = "–";
    [ObservableProperty] private string _networkDetail = "";
    [ObservableProperty] private double _pulseScore;
    [ObservableProperty] private string _pulseVerdict = "Measuring…";
    [ObservableProperty] private string _pulseReason = "";
    [ObservableProperty] private double[] _cpuHistory = [];
    [ObservableProperty] private double[] _memoryHistory = [];
    [ObservableProperty] private double[] _diskHistory = [];
    [ObservableProperty] private double[] _networkHistory = [];
    [ObservableProperty] private string _quickMessage = "";

    // ---- status / settings page
    [ObservableProperty] private string _serviceStateText = "Unknown";
    [ObservableProperty] private string _serverVersion = "–";
    [ObservableProperty] private string _uptimeText = "–";
    [ObservableProperty] private string _telemetrySource = "–";
    [ObservableProperty] private string _breakerText = "–";
    [ObservableProperty] private string _overheadCpuText = "–";
    [ObservableProperty] private string _overheadMemoryText = "–";
    [ObservableProperty] private string _roleText = "–";
    [ObservableProperty] private string _configWarnings = "none";
    [ObservableProperty] private string _operationMessage = "";
    [ObservableProperty] private string _telemetryHealthText = "Waiting for data…";
    [ObservableProperty] private string _telemetryHealthTip = "";
    [ObservableProperty] private int _telemetryLevel = 2;
    [ObservableProperty] private bool _closeToTray = GuiSettings.Current.CloseToTray;
    [ObservableProperty] private bool _notificationsOn = GuiSettings.Current.Notifications;

    partial void OnIsAdminChanged(bool value) => Processes.NotifyRoleChanged();

    partial void OnCloseToTrayChanged(bool value) => GuiSettings.Update(s => s with { CloseToTray = value });

    partial void OnNotificationsOnChanged(bool value) => GuiSettings.Update(s => s with { Notifications = value });

    // ---- themes
    public IReadOnlyList<Themes.ThemePalette> ThemeChoices => Themes.ThemeManager.All;

    public IReadOnlyList<Themes.ThemePalette> ClassicThemes => Themes.ThemeManager.Classic;
    public IReadOnlyList<Themes.ThemePalette> MetalThemes => Themes.ThemeManager.Metal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedClassicTheme), nameof(SelectedMetalTheme))]
    private Themes.ThemePalette _selectedTheme;

    /// <summary>Selection of the Classic picker (null while a Metal theme is active). Setting null is ignored.</summary>
    public Themes.ThemePalette? SelectedClassicTheme
    {
        get => SelectedTheme.IsMetal ? null : SelectedTheme;
        set { if (value is not null) { SelectedTheme = value; } }
    }

    /// <summary>Selection of the Metal picker (null while a Classic theme is active). Setting null is ignored.</summary>
    public Themes.ThemePalette? SelectedMetalTheme
    {
        get => SelectedTheme.IsMetal ? SelectedTheme : null;
        set { if (value is not null) { SelectedTheme = value; } }
    }

    partial void OnSelectedThemeChanged(Themes.ThemePalette value)
    {
        if (value is null) { return; }
        Themes.ThemeManager.Apply(value);
        GuiSettings.Update(s => s with { Theme = value.Id });
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(StartCommand), nameof(StopCommand), nameof(UninstallCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(UninstallCommand))]
    private ServiceRunState _serviceState = ServiceRunState.Unknown;

    public void Start() => _ = Task.Run(() => ConnectionLoopAsync(_cts.Token));

    // ------------------------------------------------------------------ engine + connection

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        var backoffMs = 500;
        while (!ct.IsCancellationRequested)
        {
            var scm = ServiceControl.Query();
            Post(() => ApplyScmState(scm));
            var useService = scm.State == ServiceRunState.Running && !_externalEngine;

            try
            {
                if (useService)
                {
                    await ShutdownEngineAsync().ConfigureAwait(false); // never run two engines (they share kernel trace names)
                    Post(() => { EngineMode = EngineKind.Service; EngineText = "Always-on service"; });
                    await RunSessionAsync(devMode: false, ct).ConfigureAwait(false);
                }
                else
                {
                    if (!_externalEngine && _engineWanted && !_exiting && !await EngineReachableAsync(ct).ConfigureAwait(false))
                    {
                        EnsureEngineStarted();
                    }
                    Post(() =>
                    {
                        EngineMode = EngineKind.Standalone;
                        EngineText = IsElevatedProcess ? "Standalone engine · full power" : "Standalone engine · limited (not administrator)";
                    });
                    await RunSessionAsync(devMode: true, ct).ConfigureAwait(false);
                }
                backoffMs = 500;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var message = ex switch
                {
                    TimeoutException => "Engine starting…",
                    ServerVerificationException => "Refused: the pipe is not served by PerfTuner",
                    RemoteErrorException r => $"Rejected by the engine ({r.Error.Code})",
                    InvalidOperationException => "Engine could not start: " + ex.Message,
                    _ => $"Disconnected ({ex.GetType().Name})",
                };
                Post(() => { IsConnected = false; ConnectionText = message; });
            }

            try
            {
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoffMs = Math.Min(backoffMs * 2, 4000);
        }
    }

    private static async Task<bool> EngineReachableAsync(CancellationToken ct)
    {
        try
        {
            await using var probe = await PerfTunerClient.ConnectAsync(
                new ClientOptions { DevMode = true, ClientName = "PerfTuner GUI", ConnectTimeout = TimeSpan.FromMilliseconds(400) }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ServerVerificationException or RemoteErrorException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void EnsureEngineStarted()
    {
        if (_engine is null)
        {
            _engine = StandaloneEngine.Start() ?? throw new InvalidOperationException("engine files are missing next to PerfTuner.exe");
        }
        else
        {
            _engine.EnsureRunning();
        }
    }

    /// <summary>Stops the standalone engine gracefully (it rolls every change back) — used on exit and before switching to the service.</summary>
    public async Task ShutdownEngineAsync()
    {
        _engineWanted = false;
        var engine = Interlocked.Exchange(ref _engine, null);
        if (engine is null)
        {
            return;
        }
        try
        {
            if (Client is { } c && EngineMode == EngineKind.Standalone)
            {
                await c.ShutdownEngineAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            await Task.Run(() => engine.WaitForExit(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException or OperationCanceledException or ObjectDisposedException)
        {
            // the job object still guarantees the engine exits with us
        }
        finally
        {
            engine.Dispose();
            if (!_exiting) { _engineWanted = true; }
        }
    }

    private async Task RunSessionAsync(bool devMode, CancellationToken ct)
    {
        var client = await PerfTunerClient.ConnectAsync(
            new ClientOptions { DevMode = devMode, ClientName = "PerfTuner GUI", ConnectTimeout = TimeSpan.FromSeconds(devMode ? 2 : 3) }, ct).ConfigureAwait(false);
        _client = client;
        try
        {
            var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += ex => disconnected.TrySetResult(ex);
            client.MetricsReceived += OnMetrics;

            var ack = client.Session!;
            var admin = ack.Role == ClientRole.Admin;
            Post(() =>
            {
                IsAdmin = admin;
                IsConnected = true;
                ConnectionText = $"Connected · engine v{ack.ServerVersion}";
                RoleText = admin ? "Full control" : "View only";
            });

            var history = await client.GetHistoryAsync(ChartPoints, ct).ConfigureAwait(false);
            Post(() => LoadHistory(history.Samples));
            _activitySinceMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // only notify about what happens from now on

            var config = await client.GetConfigAsync(ct).ConfigureAwait(false);
            Post(() => ConfigWarnings = config.ConfigWarnings.Length == 0 ? "none" : string.Join(Environment.NewLine, config.ConfigWarnings));

            await client.SubscribeAsync(true, ct).ConfigureAwait(false);

            while (!disconnected.Task.IsCompleted && !ct.IsCancellationRequested)
            {
                if (admin && PerfTuner.Platform.Windows.Tuning.ProcessOneShots.GetForegroundProcessId() is { } fg)
                {
                    await client.ReportForegroundAsync(fg, ct).ConfigureAwait(false);
                }
                var status = await client.GetStatusAsync(ct).ConfigureAwait(false);
                var scm = ServiceControl.Query();
                Post(() => { ApplyStatus(status); ApplyScmState(scm); });
                await PollActivityAsync(client, ct).ConfigureAwait(false);

                // Service got installed/started while we run standalone → hand over.
                if (devMode && !_externalEngine && scm.State == ServiceRunState.Running)
                {
                    try { await client.ShutdownEngineAsync(ct).ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or TimeoutException or RemoteErrorException) { }
                    break;
                }
                await Task.WhenAny(disconnected.Task, Task.Delay(2000, ct)).ConfigureAwait(false);
            }

            Post(() => { IsConnected = false; ConnectionText = "Reconnecting…"; });
        }
        finally
        {
            _client = null;
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnMetrics(MetricsPush push)
    {
        Volatile.Write(ref _latest, push);
        if (Interlocked.Exchange(ref _uiUpdatePending, 1) == 0)
        {
            _dispatcher.InvokeAsync(ApplyLatestMetrics, DispatcherPriority.Background);
        }
    }

    private void ApplyLatestMetrics()
    {
        Interlocked.Exchange(ref _uiUpdatePending, 0);
        var push = Volatile.Read(ref _latest);
        if (push is null)
        {
            return;
        }
        var s = push.Sample;
        var c = CultureInfo.CurrentCulture;
        _lastSampleTick = Environment.TickCount64;

        CpuPercent = s.CpuPercent;
        MemoryPercent = s.MemoryLoadPercent;
        CpuValue = s.CpuPercent.ToString("F0", c) + "%";
        CpuDetail = s.CpuEffectiveMhz > 0
            ? $"{(s.CpuEffectiveMhz / 1000).ToString("F2", c)} GHz" + (s.CpuLimitPercent is > 0 and < 99.5 ? $" · limited {s.CpuLimitPercent.ToString("F0", c)}%" : "")
            : "";
        MemoryValue = s.MemoryLoadPercent.ToString("F0", c) + "%";
        MemoryDetail = $"{(s.MemoryAvailableMb / 1024.0).ToString("F1", c)} GB free";
        DiskValue = Format.Rate(s.DiskReadBytesPerSec + s.DiskWriteBytesPerSec);
        DiskDetail = $"R {Format.Rate(s.DiskReadBytesPerSec)} · W {Format.Rate(s.DiskWriteBytesPerSec)}";
        NetworkValue = Format.Rate(s.NetRxBytesPerSec + s.NetTxBytesPerSec);
        NetworkDetail = $"↓ {Format.Rate(s.NetRxBytesPerSec)} · ↑ {Format.Rate(s.NetTxBytesPerSec)}";
        UpdatePulseScore(s);
        Cockpit.OnSample(s);
        CpuHistory = Shift(_cpuRing, s.CpuPercent);
        MemoryHistory = Shift(_memRing, s.MemoryLoadPercent);
        DiskHistory = Shift(_diskRing, s.DiskReadBytesPerSec + s.DiskWriteBytesPerSec);
        NetworkHistory = Shift(_netRing, s.NetRxBytesPerSec + s.NetTxBytesPerSec);

        TopProcesses.Clear();
        foreach (var p in push.TopIo)
        {
            TopProcesses.Add(new ProcessRow(p.Name, p.Pid, Format.Rate(p.DiskBytesPerSec), Format.Rate(p.NetBytesPerSec)));
        }

    }

    /// <summary>Appends to a fixed ring and returns a fresh copy (the sparkline redraws on a new array).</summary>
    private static double[] Shift(double[] ring, double value)
    {
        Array.Copy(ring, 1, ring, 0, ring.Length - 1);
        ring[^1] = value;
        return (double[])ring.Clone();
    }

    // Smoothed inputs so the score doesn't jump with every spike.
    private double _cpuEwma = -1;
    private double _memEwma = -1;

    private void UpdatePulseScore(in MetricSample s)
    {
        _cpuEwma = _cpuEwma < 0 ? s.CpuPercent : _cpuEwma + (0.15 * (s.CpuPercent - _cpuEwma));
        _memEwma = _memEwma < 0 ? s.MemoryLoadPercent : _memEwma + (0.15 * (s.MemoryLoadPercent - _memEwma));
        var score = 100.0;
        var reasons = new List<string>();
        if (_cpuEwma > 60) { var p = Math.Min(30, (_cpuEwma - 60) * 0.75); score -= p; if (p >= 5) { reasons.Add("CPU busy"); } }
        if (_memEwma > 75) { var p = Math.Min(30, (_memEwma - 75) * 1.2); score -= p; if (p >= 5) { reasons.Add("memory tight"); } }
        if (s.CpuLimitPercent is > 0 and < 90) { score -= 15; reasons.Add("CPU throttled"); }
        if (_breakerOpen) { score -= 10; reasons.Add("automatic tuning paused for safety — see History"); }
        PulseScore = Math.Round(Math.Clamp(score, 0, 100));
        PulseVerdict = PulseScore switch { >= 85 => "Running smoothly", >= 65 => "Doing OK", >= 40 => "Under pressure", _ => "Struggling" };
        PulseReason = reasons.Count == 0 ? "No pressure on CPU, memory or power." : char.ToUpperInvariant(string.Join(" · ", reasons)[0]) + string.Join(" · ", reasons)[1..];
    }

    private bool _breakerOpen;

    private void UpdateFreshness()
    {
        if (!IsConnected || _lastSampleTick == 0)
        {
            TelemetryHealthText = IsConnected ? "Waiting for the first sample…" : "Engine not connected";
            TelemetryLevel = 2;
            return;
        }
        var age = (Environment.TickCount64 - _lastSampleTick) / 1000.0;
        var c = CultureInfo.CurrentCulture;
        var stale = age > 5;
        TelemetryLevel = stale ? 2 : _telemetryHealthRaw.StartsWith("Healthy", StringComparison.Ordinal) ? 0 : 1;
        TelemetryHealthText = stale
            ? $"Data is stale ({age.ToString("F0", c)} s)"
            : "Live";
        TelemetryHealthTip = $"{_telemetryHealthRaw}\n{_eventsPerSec.ToString("N0", c)} kernel events/s · {_eventsLost.ToString("N0", c)} dropped";
    }

    private void LoadHistory(MetricSample[] samples)
    {
        foreach (var x in samples.TakeLast(ChartPoints))
        {
            Shift(_cpuRing, x.CpuPercent);
            Shift(_memRing, x.MemoryLoadPercent);
            Shift(_diskRing, x.DiskReadBytesPerSec + x.DiskWriteBytesPerSec);
            Shift(_netRing, x.NetRxBytesPerSec + x.NetTxBytesPerSec);
        }
        CpuHistory = (double[])_cpuRing.Clone();
        MemoryHistory = (double[])_memRing.Clone();
        DiskHistory = (double[])_diskRing.Clone();
        NetworkHistory = (double[])_netRing.Clone();
    }

    private void ApplyStatus(StatusResponse s)
    {
        var c = CultureInfo.CurrentCulture;
        ServerVersion = s.ServerVersion;
        UptimeText = Format.Uptime(s.UptimeSeconds);
        TelemetrySource = s.TelemetrySource + (s.Degraded ? " · reduced detail (overhead budget)" : "");
        _breakerOpen = s.Breaker != BreakerState.Closed;
        BreakerText = s.Breaker switch
        {
            BreakerState.Closed => "Armed (healthy)",
            BreakerState.HalfOpen => "Recovering",
            BreakerState.Open => $"Tripped — tuning paused: {s.BreakerReason}",
            BreakerState.Latched => $"Locked — reset needed: {s.BreakerReason}",
            _ => s.Breaker.ToString(),
        };
        OverheadCpuText = $"{s.ServiceCpuPercent.ToString("F2", c)}% CPU";
        OverheadMemoryText = $"{s.ServicePrivateMb.ToString("F0", c)} MB";
        _telemetryHealthRaw = s.TelemetryHealth;
        _eventsPerSec = s.TelemetryEventsPerSec;
        _eventsLost = s.TelemetryEventsLost;

        _autoCalmThreshold = s.AutoCalmThresholdPercent;
        _profile = s.Profile;
        if (!_modeChanging)
        {
            var mode = s.Profile == TuningProfile.Safe ? PerfMode.Off
                : s.Mode == TuningMode.Focus ? PerfMode.Turbo
                : s.Mode == TuningMode.Quiet ? PerfMode.Silent
                : s.AutoCalm ? PerfMode.Smart : PerfMode.Off;
            if (mode != CurrentMode) { CurrentMode = mode; }
            _syncingAutoFix = true;
            AutoFixOn = s.Profile == TuningProfile.Advanced;
            _syncingAutoFix = false;
        }

        UpdateFreshness();
        Tuning.ApplyStatus(s);
        Changes.ApplyStatus(s);
    }

    private void ApplyScmState(ServiceQuery q)
    {
        ServiceState = q.State;
        ServiceStateText = q.State switch
        {
            ServiceRunState.NotInstalled => "Not installed (optional)",
            ServiceRunState.Running => $"Running (pid {q.ProcessId})",
            ServiceRunState.Unknown => "Unknown",
            _ => q.State.ToString(),
        };
    }

    // ------------------------------------------------------------------ modes (exactly one, or Off)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeTitle), nameof(ModeDescription), nameof(IsOff), nameof(IsTurbo), nameof(IsSmart), nameof(IsSilent))]
    private PerfMode _currentMode = PerfMode.Off;

    [ObservableProperty] private string _modeMessage = "";
    [ObservableProperty] private bool _autoFixOn;

    private bool _modeChanging;
    private bool _syncingAutoFix;

    // Bound two-way to the mode radio buttons: checking one switches the mode (unchecking is ignored).
    public bool IsOff { get => CurrentMode == PerfMode.Off; set => Pick(value, PerfMode.Off); }
    public bool IsTurbo { get => CurrentMode == PerfMode.Turbo; set => Pick(value, PerfMode.Turbo); }
    public bool IsSmart { get => CurrentMode == PerfMode.Smart; set => Pick(value, PerfMode.Smart); }
    public bool IsSilent { get => CurrentMode == PerfMode.Silent; set => Pick(value, PerfMode.Silent); }

    private void Pick(bool chosen, PerfMode mode)
    {
        if (chosen && mode != CurrentMode && !_modeChanging) { _ = SetModeAsync(mode.ToString()); }
    }

    public string ModeTitle => CurrentMode switch
    {
        PerfMode.Turbo => "Turbo is on",
        PerfMode.Smart => "Smart is on",
        PerfMode.Silent => "Silent is on",
        _ => "No mode active",
    };

    public string ModeDescription => CurrentMode switch
    {
        PerfMode.Turbo => "The app you're using gets top priority, busy background apps are held back and the power plan runs at full speed.",
        PerfMode.Smart => "Runaway background apps are calmed down and RAM is freed automatically when it runs low.",
        PerfMode.Silent => "Turbo boost is off and the CPU is capped at 80 %: cooler, quieter, longer battery. Background apps run in efficiency mode.",
        _ => "PerfTuner only acts when you click something.",
    };

    /// <summary>Switches to one mode (or Off). The previous mode is fully undone first — modes never overlap.</summary>
    [RelayCommand]
    private async Task SetModeAsync(string? name)
    {
        if (!Enum.TryParse<PerfMode>(name, out var mode)) { return; }
        if (Client is not { } client) { ModeMessage = "Engine not connected yet."; return; }
        _modeChanging = true;
        try
        {
            var profile = AutoFixOn ? TuningProfile.Advanced : mode == PerfMode.Off ? TuningProfile.Safe : TuningProfile.Balanced;
            var p = await client.SetProfileAsync(profile).ConfigureAwait(true);
            if (!p.Success) { ModeMessage = "✖ " + p.Message; return; }
            var tuningMode = mode switch { PerfMode.Turbo => TuningMode.Focus, PerfMode.Silent => TuningMode.Quiet, _ => TuningMode.Off };
            var r = await client.SetTuningOptionsAsync(profile == TuningProfile.Safe, tuningMode, mode == PerfMode.Smart, _autoCalmThreshold).ConfigureAwait(true);
            if (!r.Success) { ModeMessage = "✖ " + r.Message; return; }
            var power = ModePower.Apply(mode, SystemSettings.Journal, IsElevatedProcess);
            CurrentMode = mode;
            GuiSettings.Update(g => g with { Mode = mode.ToString() });
            ModeMessage = (mode == PerfMode.Off ? "All modes off — everything they changed was undone." : $"{mode} on.")
                + (power.Length > 0 ? $" ({power})" : "");
            AddActivity("You", ModeMessage);
            if (mode != PerfMode.Off) { Cockpit.CoolDown("cpu"); }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException)
        {
            ModeMessage = "✖ " + ex.Message;
        }
        finally
        {
            _modeChanging = false;
            OnPropertyChanged(nameof(IsOff)); OnPropertyChanged(nameof(IsTurbo)); OnPropertyChanged(nameof(IsSmart)); OnPropertyChanged(nameof(IsSilent));
        }
    }

    partial void OnAutoFixOnChanged(bool value)
    {
        if (!_syncingAutoFix) { _ = SetAutoFixAsync(value); }
    }

    /// <summary>"Automatic fixes": DriftWatch + ProofRun may test and keep fixes on their own (engine profile Advanced).</summary>
    private async Task SetAutoFixAsync(bool on)
    {
        if (Client is not { } client) { return; }
        try
        {
            var profile = on ? TuningProfile.Advanced : CurrentMode == PerfMode.Off ? TuningProfile.Safe : TuningProfile.Balanced;
            var r = await client.SetProfileAsync(profile).ConfigureAwait(true);
            OperationMessage = r.Success
                ? on ? "Automatic fixes on: PerfTuner tests fixes for slowdowns it detects and keeps only those that measurably help. You'll get a notification each time."
                     : "Automatic fixes off."
                : "✖ " + r.Message;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException)
        {
            OperationMessage = "✖ " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ quick actions

    [RelayCommand]
    private async Task RamRefreshAsync()
    {
        if (Client is not { } client) { QuickMessage = "Engine not connected yet."; return; }
        QuickMessage = "Freeing RAM…";
        try
        {
            var r = await client.TrimBackgroundMemoryAsync().ConfigureAwait(true);
            QuickMessage = (r.Success ? "✔ " : "✖ ") + r.Message;
            if (r.Success)
            {
                AddActivity("You", r.Message);
                if (r.Message.StartsWith("Freed", StringComparison.Ordinal)) { Cockpit.CoolDown("memory"); }
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException)
        {
            QuickMessage = "✖ " + ex.Message;
        }
    }

    [RelayCommand]
    private Task UndoEverythingAsync() => Changes.RevertEverythingCommand.ExecuteAsync(null);

    // ------------------------------------------------------------------ "What PerfTuner did" + tray notifications

    private long _activitySinceMs;
    private readonly Dictionary<string, long> _lastNotifiedTick = [];

    /// <summary>Raised on the UI thread for a tray pop-up (title, text).</summary>
    public event Action<string, string>? Notify;

    private static readonly HashSet<string> AutomaticSources = new(StringComparer.Ordinal)
    {
        "Turbo", "Smart", "Silent", "MemGuard", "ProofRun", "Autopilot", "Experiment", "Rule",
    };

    /// <summary>Picks up what the engine did on its own since the last poll (timeline "Tuning · Source" events).</summary>
    private async Task PollActivityAsync(PerfTunerClient client, CancellationToken ct)
    {
        TimelineResponse t;
        try
        {
            t = await client.GetTimelineAsync(_activitySinceMs + 1, 50, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RemoteErrorException)
        {
            return;
        }
        var fresh = t.Events.Where(e => e.TimeUnixMs > _activitySinceMs).OrderBy(e => e.TimeUnixMs).ToList();
        if (fresh.Count == 0) { return; }
        _activitySinceMs = fresh[^1].TimeUnixMs;

        var items = new List<ActivityItem>();
        foreach (var e in fresh)
        {
            if (e.Subsystem == Subsystem.Tuning && e.Title.StartsWith("Tuning · ", StringComparison.Ordinal))
            {
                var source = e.Title["Tuning · ".Length..];
                var first = source.Split(' ')[0];
                if (!AutomaticSources.Contains(first) || e.Detail.StartsWith("reverted", StringComparison.OrdinalIgnoreCase)) { continue; }
                items.Add(new ActivityItem(TimeOf(e.TimeUnixMs), first, Friendly(first, e.Detail)));
                continue;
            }
            else if (e.IsStall || e.Severity == Severity.Critical)
            {
                items.Add(new ActivityItem(TimeOf(e.TimeUnixMs), "Watch", $"{e.Title}: {e.Detail}"));
            }
        }
        if (items.Count == 0) { return; }
        Post(() =>
        {
            foreach (var i in items) { InsertActivity(i); }
            if (items.Any(i => i.Source == "MemGuard")) { Cockpit.CoolDown("memory"); }
            if (!NotificationsOn) { return; }
            foreach (var group in items.GroupBy(i => i.Source))
            {
                var now = Environment.TickCount64;
                if (_lastNotifiedTick.TryGetValue(group.Key, out var last) && now - last < 45_000) { continue; } // no pop-up storms
                _lastNotifiedTick[group.Key] = now;
                var list = group.ToList();
                var text = list.Count == 1 ? list[0].Text : $"{list[0].Text}  (+{list.Count - 1} more — see History)";
                Notify?.Invoke(Brand.SourceName(group.Key), text);
            }
        });
    }

    private static string TimeOf(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    /// <summary>Turns engine wording ("chrome (123): priority Normal → Above normal") into a short sentence.</summary>
    private static string Friendly(string source, string detail)
    {
        var app = detail.Split(" (", 2)[0];
        if (detail.Contains("efficiency", StringComparison.OrdinalIgnoreCase)) { return $"Put {app} in efficiency mode (background app)."; }
        if (detail.Contains("priority", StringComparison.OrdinalIgnoreCase) && detail.Contains("Above", StringComparison.Ordinal)) { return $"Gave {app} priority (you're using it)."; }
        if (detail.Contains("priority", StringComparison.OrdinalIgnoreCase) && detail.Contains("Below", StringComparison.Ordinal)) { return $"Lowered the priority of {app}."; }
        return detail.Length > 160 ? detail[..160] + "…" : detail;
    }

    internal void AddActivity(string source, string text) => InsertActivity(new ActivityItem(TimeOf(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), source, text));

    private void InsertActivity(ActivityItem item)
    {
        Activity.Insert(0, item);
        while (Activity.Count > 60) { Activity.RemoveAt(Activity.Count - 1); }
    }

    // ------------------------------------------------------------------ always-on service (optional)

    private bool CanInstall() => !IsBusy;
    private bool CanStart() => !IsBusy && ServiceState is ServiceRunState.Stopped;
    private bool CanStop() => !IsBusy && ServiceState is ServiceRunState.Running or ServiceRunState.StartPending;
    private bool CanUninstall() => !IsBusy && ServiceState is not (ServiceRunState.NotInstalled or ServiceRunState.Unknown);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => RunElevatedAsync(ElevatedVerb.Install, "Installing the always-on service…");

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunElevatedAsync(ElevatedVerb.Start, "Starting the service…");

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunElevatedAsync(ElevatedVerb.Stop, "Stopping the service (all tuning is undone)…");

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private Task UninstallAsync() => RunElevatedAsync(ElevatedVerb.Uninstall, "Uninstalling the service…");

    private async Task RunElevatedAsync(ElevatedVerb verb, string progress)
    {
        IsBusy = true;
        OperationMessage = progress;
        try
        {
            var result = await ElevatedOperations.RunElevatedAsync(verb).ConfigureAwait(true);
            OperationMessage = $"{verb}: {ElevatedOperations.Describe(result)}" +
                (result is ServiceOperationResult.Success or ServiceOperationResult.Cancelled ? "" : $" Log: {HelperLog.FilePath}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            OperationMessage = $"{verb} failed: {ex.Message}";
        }
        finally
        {
            ApplyScmState(ServiceControl.Query());
            IsBusy = false;
        }
    }

    internal void Post(Action action) => _dispatcher.InvokeAsync(action, DispatcherPriority.Background);

    public async ValueTask DisposeAsync()
    {
        _exiting = true;
        Desktops.Dispose();
        _engineWanted = false;
        _freshnessTimer.Stop();
        await ShutdownEngineAsync().ConfigureAwait(false);
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_client is { } client)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        _cts.Dispose();
    }
}
