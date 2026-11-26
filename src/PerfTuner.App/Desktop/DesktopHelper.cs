using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PerfTuner.Core.Undocumented;

namespace PerfTuner.App.Desktop;

/// <summary>
/// The optional keyboard and virtual-desktop helpers: mode shortcuts (Settings page) and, on the
/// Undocumented page, the desktop number in the tray, a name overlay when switching and hotkeys
/// to jump between desktops. Desktops are read from Explorer's own registry list (watched with
/// RegNotifyChangeKeyValue) and switched with Windows' Win+Ctrl+←/→, so nothing depends on
/// Explorer's internal interfaces.
/// </summary>
public sealed partial class DesktopHelper : IDisposable
{
    private const string DesktopsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    private const int ModeKeyFirst = 100, ModeKeyLast = 103, DesktopKeyFirst = 200, DesktopKeyLast = 209;

    private readonly Dispatcher _dispatcher;
    private readonly HotkeyHost _keys = new();
    private readonly DesktopHistory _history = new();
    private readonly string _sessionKey = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{SessionId()}\VirtualDesktops";
    private NumberTrayIcon? _tray;
    private Thread? _watcher;
    private ManualResetEvent? _stop;
    private bool _tracking;
    private bool _overlay;
    private int _lastIndex = -1;
    private IReadOnlyList<Guid> _ids = [];
    private int _index = -1;
    private Window? _overlayWindow;
    private DispatcherTimer? _overlayTimer;

    private static int SessionId()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return p.SessionId;
    }

    public DesktopHelper()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Shown when a shortcut is taken by another program.</summary>
    public event Action<string>? Problem;

    // ------------------------------------------------------------------ mode shortcuts

    /// <summary>Ctrl+Alt+Shift+F1…F4 = Off, Turbo, Smart, Silent. Returns the shortcuts that couldn't be registered.</summary>
    public IReadOnlyList<string> SetModeHotkeys(bool on, Action<string> setMode)
    {
        ArgumentNullException.ThrowIfNull(setMode);
        _keys.UnregisterRange(ModeKeyFirst, ModeKeyLast);
        if (!on) { return []; }
        var failed = new List<string>();
        string[] modes = ["Off", "Turbo", "Smart", "Silent"];
        for (var i = 0; i < modes.Length; i++)
        {
            var mode = modes[i];
            if (!_keys.Register(ModeKeyFirst + i, HotkeyHost.Control | HotkeyHost.Alt | HotkeyHost.Shift, (uint)(0x70 + i) /* F1… */, () => setMode(mode)))
            {
                failed.Add($"Ctrl+Alt+Shift+F{i + 1}");
            }
        }
        return failed;
    }

    // ------------------------------------------------------------------ virtual desktops

    /// <summary>Switches the three desktop helpers on or off (from the saved preferences).</summary>
    public void Configure(bool tray, bool overlay, bool hotkeys)
    {
        _overlay = overlay;

        if (tray && _tray is null)
        {
            _tray = new NumberTrayIcon();
            _tray.Clicked += () => _ = Step(1);
        }
        else if (!tray && _tray is not null)
        {
            _tray.Dispose();
            _tray = null;
        }

        _keys.UnregisterRange(DesktopKeyFirst, DesktopKeyLast);
        if (hotkeys)
        {
            var failed = new List<string>();
            for (var n = 1; n <= 9; n++)
            {
                var target = n - 1;
                if (!_keys.Register(DesktopKeyFirst + n, HotkeyHost.Control | HotkeyHost.Win | HotkeyHost.Alt, (uint)(0x30 + n), () => _ = JumpTo(target)))
                {
                    failed.Add($"Ctrl+Win+Alt+{n}");
                }
            }
            if (!_keys.Register(DesktopKeyFirst, HotkeyHost.Control | HotkeyHost.Win | HotkeyHost.Alt, 0x30, () => _ = Back())) { failed.Add("Ctrl+Win+Alt+0"); }
            if (failed.Count > 0) { Problem?.Invoke("Already used by another program: " + string.Join(", ", failed)); }
        }

        var track = tray || overlay || hotkeys;
        if (track && !_tracking) { StartWatching(); }
        else if (!track && _tracking) { StopWatching(); }
        if (track) { Refresh(showOverlay: false); }
    }

    private void StartWatching()
    {
        _tracking = true;
        _stop = new ManualResetEvent(false);
        var stop = _stop;
        _watcher = new Thread(() => Watch(stop)) { IsBackground = true, Name = "PerfTuner desktop watcher" };
        _watcher.Start();
    }

    private void StopWatching()
    {
        _tracking = false;
        _stop?.Set();
        _watcher?.Join(TimeSpan.FromSeconds(2));
        _stop?.Dispose();
        _stop = null;
        _watcher = null;
        _lastIndex = -1;
    }

    /// <summary>Waits for changes under Explorer's desktop keys (the notification belongs to this thread, so it stays alive).</summary>
    private void Watch(ManualResetEvent stop)
    {
        using var changed = new AutoResetEvent(false);
        while (!stop.WaitOne(0))
        {
            using var main = Registry.CurrentUser.OpenSubKey(DesktopsKey);
            using var session = Registry.CurrentUser.OpenSubKey(_sessionKey);
            var ok = main is not null && RegNotifyChangeKeyValue(main.Handle.DangerousGetHandle(), true, NotifyFlags, changed.SafeWaitHandle.DangerousGetHandle(), true) == 0;
            if (session is not null) { RegNotifyChangeKeyValue(session.Handle.DangerousGetHandle(), true, NotifyFlags, changed.SafeWaitHandle.DangerousGetHandle(), true); }
            if (!ok) { stop.WaitOne(TimeSpan.FromSeconds(5)); continue; }
            if (WaitHandle.WaitAny([stop, changed]) == 0) { return; }
            _dispatcher.BeginInvoke(() => Refresh(showOverlay: true));
        }
    }

    private const uint NotifyFlags = 0x1 /* NAME */ | 0x4 /* LAST_SET */;

    private void Refresh(bool showOverlay)
    {
        if (!_tracking) { return; }
        var (ids, current) = Read();
        _ids = ids;
        _history.Prune(ids);
        _history.Visit(current);
        _index = VirtualDesktopState.IndexOf(ids, current);
        if (_index < 0) { return; }
        var name = VirtualDesktopState.DisplayName(_index, ReadName(ids[_index]));
        _tray?.Show(_index + 1, $"{name} ({_index + 1} of {ids.Count}). Click for the next desktop.");
        if (showOverlay && _overlay && _index != _lastIndex && _lastIndex >= 0) { ShowOverlay(_index + 1, name); }
        _lastIndex = _index;
    }

    private (IReadOnlyList<Guid> Ids, Guid? Current) Read()
    {
        try
        {
            using var main = Registry.CurrentUser.OpenSubKey(DesktopsKey);
            using var session = Registry.CurrentUser.OpenSubKey(_sessionKey);
            var ids = VirtualDesktopState.ParseIds(main?.GetValue("VirtualDesktopIDs") as byte[]);
            var current = VirtualDesktopState.ParseId(session?.GetValue("CurrentVirtualDesktop") as byte[])
                          ?? VirtualDesktopState.ParseId(main?.GetValue("CurrentVirtualDesktop") as byte[]);
            return (ids, current);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return ([], null);
        }
    }

    private static string? ReadName(Guid id)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{DesktopsKey}\Desktops\{id:B}");
            return key?.GetValue("Name") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    private Task Step(int steps) => DesktopKeys.StepAsync(steps);

    private Task JumpTo(int target)
    {
        Refresh(showOverlay: false);
        return DesktopKeys.StepAsync(VirtualDesktopState.Steps(_index, target, _ids.Count));
    }

    private Task Back()
    {
        Refresh(showOverlay: false);
        if (_history.Previous is not { } previous) { return Task.CompletedTask; }
        var target = VirtualDesktopState.IndexOf(_ids, previous);
        return DesktopKeys.StepAsync(VirtualDesktopState.Steps(_index, target, _ids.Count));
    }

    /// <summary>A brief, click-through overlay with the desktop's number and name (a new window each time, so it opens on the new desktop).</summary>
    private void ShowOverlay(int number, string name)
    {
        _overlayTimer?.Stop();
        _overlayWindow?.Close();
        var accent = Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.SteelBlue;
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 64, FontWeight = FontWeights.Bold, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = name, FontSize = 22, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x20, 0x24)),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(40, 20, 40, 24),
                MinWidth = 220,
                Child = panel,
            },
        };
        window.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(window).Handle;
            SetWindowLongPtrW(h, -20 /* GWL_EXSTYLE */, GetWindowLongPtrW(h, -20) | 0x08000000 /* NOACTIVATE */ | 0x20 /* TRANSPARENT */ | 0x80 /* TOOLWINDOW */);
        };
        _overlayWindow = window;
        window.Show();
        _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _overlayTimer.Tick += (_, _) =>
        {
            _overlayTimer?.Stop();
            window.Close();
            if (ReferenceEquals(_overlayWindow, window)) { _overlayWindow = null; }
        };
        _overlayTimer.Start();
    }

    public void Dispose()
    {
        if (_tracking) { StopWatching(); }
        _tray?.Dispose();
        _tray = null;
        _overlayTimer?.Stop();
        _overlayWindow?.Close();
        _keys.Dispose();
    }

    [LibraryImport("advapi32.dll")]
    private static partial int RegNotifyChangeKeyValue(nint key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, uint filter, nint evt, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);
}
