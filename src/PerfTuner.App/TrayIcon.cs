using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PerfTuner.App;

public sealed record TrayMenuItem(int Id, string Text, bool Checked = false, bool Separator = false);

/// <summary>
/// Notification-area icon with a native context menu (Shell_NotifyIcon + TrackPopupMenuEx) —
/// no third-party dependency. Re-registers itself when Explorer restarts (TaskbarCreated).
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    private const int WmLButtonUp = 0x0202, WmLButtonDblClk = 0x0203, WmRButtonUp = 0x0205;

    private readonly HwndSource _window;
    private readonly nint _icon;
    private readonly uint _taskbarCreated;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private string _tip;
    private bool _added;

    public event Action? OpenRequested;
    public event Action<int>? MenuCommand;

    public TrayIcon(string tip, Func<IReadOnlyList<TrayMenuItem>> menu)
    {
        _tip = tip;
        _menu = menu;
        _window = new HwndSource(new HwndSourceParameters("PerfTunerTray") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = -3 /* HWND_MESSAGE */ });
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _icon = LoadImageW(GetModuleHandleW(null), 32512, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0);
        if (_icon == 0) { _icon = LoadIconW(0, 32512); }
        Add();
    }

    private unsafe NotifyIconData Data(uint flags)
    {
        var d = new NotifyIconData { CbSize = (uint)sizeof(NotifyIconData), HWnd = _window.Handle, UId = 1, UFlags = flags, UCallbackMessage = CallbackMessage, HIcon = _icon };
        Copy(_tip, d.SzTip, 128);
        return d;
    }

    private static unsafe void Copy(string text, char* dest, int capacity)
    {
        var n = Math.Min(text.Length, capacity - 1);
        for (var i = 0; i < n; i++) { dest[i] = text[i]; }
        dest[n] = '\0';
    }

    private void Add()
    {
        var d = Data(NifMessage | NifIcon | NifTip);
        _added = ShellNotifyIconW(NimAdd, ref d);
    }

    public void SetTooltip(string tip)
    {
        _tip = tip.Length > 120 ? tip[..120] : tip;
        if (!_added) { return; }
        var d = Data(NifTip);
        ShellNotifyIconW(NimModify, ref d);
    }

    public unsafe void ShowBalloon(string title, string text)
    {
        if (!_added) { return; }
        var d = Data(NifInfo);
        Copy(text, d.SzInfo, 256);
        Copy(title, d.SzInfoTitle, 64);
        d.DwInfoFlags = 1; // NIIF_INFO
        ShellNotifyIconW(NimModify, ref d);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam & 0xFFFF))
            {
                case WmLButtonUp:
                case WmLButtonDblClk:
                    OpenRequested?.Invoke();
                    handled = true;
                    break;
                case WmRButtonUp:
                    ShowMenu();
                    handled = true;
                    break;
            }
        }
        else if (msg == (int)_taskbarCreated)
        {
            Add(); // Explorer restarted
        }
        return 0;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            foreach (var item in _menu())
            {
                if (item.Separator) { AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, 0, null); }
                else { AppendMenuW(menu, item.Checked ? 0x8u /* MF_CHECKED */ : 0u, (nuint)item.Id, item.Text); }
            }
            GetCursorPos(out var pt);
            SetForegroundWindow(_window.Handle); // required so the menu closes when clicking elsewhere
            var id = TrackPopupMenuEx(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0002 /* TPM_RIGHTBUTTON */, pt.X, pt.Y, _window.Handle, 0);
            if (id > 0) { MenuCommand?.Invoke(id); }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = Data(0);
            ShellNotifyIconW(NimDelete, ref d);
            _added = false;
        }
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NotifyIconData
    {
        public uint CbSize;
        public nint HWnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public nint HIcon;
        public fixed char SzTip[128];
        public uint DwState;
        public uint DwStateMask;
        public fixed char SzInfo[256];
        public uint UVersion;
        public fixed char SzInfoTitle[64];
        public uint DwInfoFlags;
        public Guid GuidItem;
        public nint HBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIconW(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll")]
    private static partial nint LoadImageW(nint instance, nint name, uint type, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint LoadIconW(nint instance, nint name);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
