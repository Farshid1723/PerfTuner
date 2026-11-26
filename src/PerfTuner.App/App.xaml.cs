using System.IO;
using System.Windows;
using PerfTuner.App.ViewModels;
using PerfTuner.Client;

namespace PerfTuner.App;

/// <summary>
/// Two personalities, one executable:
/// <list type="bullet">
/// <item><c>PerfTuner.exe [--tray] [--dev]</c> — the GUI. It starts its own engine unless the always-on service
///   runs; closing the window keeps it in the notification area. <c>--dev</c> connects to an engine started by hand.</item>
/// <item><c>PerfTuner.exe --elevated-op &lt;verb&gt;</c> — the headless helper launched through UAC
///   to install/start/stop/uninstall the service. It shows no window and exits with a result code.</item>
/// </list>
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var helperResult = ElevatedOperations.TryRunFromCommandLine(e.Args, HelperLog.Write);
        if (helperResult is not null)
        {
            HelperLog.Write($"exit: {helperResult}");
            Shutdown((int)helperResult.Value);
            return;
        }

        // Setup and uninstall (v0.9): started by PerfTuner-Setup.exe / PerfTuner-Uninstall.exe, never the normal GUI.
        if (Array.IndexOf(e.Args, "--setup") >= 0 || Array.IndexOf(e.Args, "--uninstall") >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Themes.ThemeManager.Apply(Themes.ThemeManager.Find(GuiSettings.Current.Theme));
            Shutdown(Setup.SetupFlow.TryRun(e.Args) ?? 0);
            return;
        }

        // Last-chance diagnostics: a crash must leave a trace (bounded log, no user data).
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticsLog.Write($"FATAL unhandled: {args.ExceptionObject}");
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticsLog.Write($"UI thread exception: {args.Exception}");
            args.Handled = true; // keep the monitor alive; the error is logged
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticsLog.Write($"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        // One instance per user: a second launch just brings the first one forward.
        _singleInstance = new Mutex(true, @"Local\PerfTuner.GUI", out var first);
        if (!first && Array.IndexOf(e.Args, "--after-restart") < 0)
        {
            try { EventWaitHandle.OpenExisting(@"Local\PerfTuner.GUI.Show").Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(0);
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PerfTuner.GUI.Show");
        var showThread = new Thread(() =>
        {
            while (_showSignal.WaitOne() && !_exiting) { Dispatcher.BeginInvoke(ShowMain); }
        }) { IsBackground = true, Name = "PerfTuner show signal" };
        showThread.Start();

        // --theme <id> previews a theme without saving it (documentation screenshots).
        var themeArg = Array.IndexOf(e.Args, "--theme");
        var themeId = themeArg >= 0 && themeArg + 1 < e.Args.Length ? e.Args[themeArg + 1] : GuiSettings.Current.Theme;
        Themes.ThemeManager.Apply(Themes.ThemeManager.Find(themeId));

        var externalEngine = Array.IndexOf(e.Args, "--dev") >= 0;
        _viewModel = new MainViewModel(externalEngine);
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Closing += OnMainClosing;

        _tray = new TrayIcon(Brand.Product, TrayMenu);
        _tray.OpenRequested += ShowMain;
        _tray.MenuCommand += OnTrayCommand;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.PulseScore))
            {
                _tray.SetTooltip($"{Brand.Product} · health {_viewModel.PulseScore:F0} · {_viewModel.PulseVerdict} · {_viewModel.ModeTitle}");
            }
        };
        _viewModel.Notify += (title, text) => _tray?.ShowBalloon(title, text);
        SessionEnding += (_, _) => ExitApplication();

        // --page N opens a specific page (shortcuts, documentation screenshots).
        var pageArg = Array.IndexOf(e.Args, "--page");
        if (pageArg >= 0 && pageArg + 1 < e.Args.Length && int.TryParse(e.Args[pageArg + 1], System.Globalization.CultureInfo.InvariantCulture, out var page)
            && page is >= MainViewModel.PageHome and <= MainViewModel.LastPage)
        {
            _viewModel.NavigateCommand.Execute(page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var spaceArg = Array.IndexOf(e.Args, "--space");
            if (spaceArg >= 0 && spaceArg + 1 < e.Args.Length && e.Args[spaceArg + 1] is { Length: 1 } letter && char.IsAsciiLetter(letter[0]))
            {
                var modeArg = Array.IndexOf(e.Args, "--space-mode");
                if (modeArg >= 0 && modeArg + 1 < e.Args.Length) { _viewModel.Cockpit.Space.PreviewMode(e.Args[modeArg + 1]); }
                _viewModel.SelectedTab = MainViewModel.PageCockpit;
                _viewModel.Cockpit.OpenSpaceFor(letter.ToUpperInvariant());
            }
            // --app <catalog id> selects an app on Get apps (screenshots); unknown ids are ignored.
            var appArg = Array.IndexOf(e.Args, "--app");
            if (appArg >= 0 && appArg + 1 < e.Args.Length) { _viewModel.AppStore.Preselect(e.Args[appArg + 1]); }
            // --scan starts Scan & Fix right away (with --page 8; shortcuts, screenshots).
            if (page == MainViewModel.PageScan && Array.IndexOf(e.Args, "--scan") >= 0) { _viewModel.Scan.StartScanCommand.Execute(null); }
            // --preview-undocumented shows the Undocumented page's list without saving the warning's acknowledgement (screenshots).
            if (Array.IndexOf(e.Args, "--preview-undocumented") >= 0) { _viewModel.Undocumented.Preview(); }
            var partArg = Array.IndexOf(e.Args, "--part");
            if (partArg >= 0 && partArg + 1 < e.Args.Length) { _viewModel.Cockpit.SelectPartCommand.Execute(e.Args[partArg + 1]); }
        }

        if (Array.IndexOf(e.Args, "--tray") >= 0) { window.Hide(); } else { window.Show(); }
    }

    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private TrayIcon? _tray;
    private MainViewModel? _viewModel;
    private volatile bool _exiting;

    private IReadOnlyList<TrayMenuItem> TrayMenu()
    {
        var vm = _viewModel!;
        return
        [
            new(1, $"Open {Brand.Product}"),
            new(0, "", Separator: true),
            new(2, "Turbo — maximum speed for the app you're using", vm.IsTurbo),
            new(3, "Smart — hands-off everyday care", vm.IsSmart),
            new(4, "Silent — cooler, quieter, longer battery", vm.IsSilent),
            new(5, "All modes off", vm.IsOff),
            new(0, "", Separator: true),
            new(6, "Free up RAM now"),
            new(7, "Scan & Fix"),
            new(0, "", Separator: true),
            new(9, $"Exit {Brand.Product} (undoes live tuning)"),
        ];
    }

    private void OnTrayCommand(int id)
    {
        var vm = _viewModel!;
        switch (id)
        {
            case 1: ShowMain(); break;
            case 2: _ = TrayModeAsync("Turbo"); break;
            case 3: _ = TrayModeAsync("Smart"); break;
            case 4: _ = TrayModeAsync("Silent"); break;
            case 5: _ = TrayModeAsync("Off"); break;
            case 6: _ = TrayRamAsync(); break;
            case 7:
                ShowMain();
                vm.ScanAndFixCommand.Execute(null);
                break;
            case 9: ExitApplication(); break;
        }
    }

    private async Task TrayModeAsync(string mode)
    {
        await _viewModel!.SetModeCommand.ExecuteAsync(mode).ConfigureAwait(true);
        _tray?.ShowBalloon(_viewModel.ModeTitle, _viewModel.ModeMessage);
    }

    private async Task TrayRamAsync()
    {
        await _viewModel!.RamRefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        _tray?.ShowBalloon("Free up RAM", _viewModel.QuickMessage.TrimStart('✔', '✖', ' '));
    }

    private void ShowMain()
    {
        if (MainWindow is not { } w || _exiting) { return; }
        w.Show();
        if (w.WindowState == WindowState.Minimized) { w.WindowState = WindowState.Normal; }
        w.Activate();
    }

    private void OnMainClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) { return; }
        e.Cancel = true;
        if (_viewModel is not { CloseToTray: true })
        {
            ExitApplication();
            return;
        }
        MainWindow!.Hide();
        if (!GuiSettings.Current.TrayHintShown)
        {
            _tray?.ShowBalloon(Brand.Product, "Still running here in the tray. Right-click to switch modes or free RAM; choose Exit to close completely.");
            GuiSettings.Update(p => p with { TrayHintShown = true });
        }
    }

    /// <summary>Full exit: stop the standalone engine gracefully (it undoes live tuning), remove the tray icon, quit.</summary>
    public static void ExitApplication()
    {
        if (Current is App app) { _ = app.ExitAsync(); }
    }

    private async Task ExitAsync()
    {
        if (_exiting) { return; }
        _exiting = true;
        MainWindow?.Hide();
        _tray?.Dispose();
        _tray = null;
        if (_viewModel is not null)
        {
            try { await _viewModel.DisposeAsync().ConfigureAwait(true); }
            catch (Exception ex) { DiagnosticsLog.Write($"exit: {ex.GetType().Name} {ex.Message}"); }
        }
        _showSignal?.Set();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        DiagnosticsLog.Write($"exit code {e.ApplicationExitCode}");
        base.OnExit(e);
    }
}

/// <summary>GUI diagnostics (%LOCALAPPDATA%\PerfTuner\gui.log, ≤ 256 KB).</summary>
internal static class DiagnosticsLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerfTuner", "gui.log");

    public static void Write(string message) => BoundedLog.Append(FilePath, message);
}

/// <summary>Bounded log for the elevated helper (%LOCALAPPDATA%\PerfTuner\helper.log, ≤ 256 KB).</summary>
internal static class HelperLog
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerfTuner", "helper.log");

    public static void Write(string message) => BoundedLog.Append(FilePath, message);
}

internal static class BoundedLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly Lock Gate = new();

    public static void Append(string path, string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    info.Delete();
                }
                // Single-line entries; strip newlines so messages cannot forge extra lines.
                var line = message.Replace('\r', ' ').Replace('\n', ' ');
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
