using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerfTuner.App.Desktop;

/// <summary>A second notification-area icon that shows a number (the current virtual desktop).</summary>
public sealed partial class NumberTrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 2;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;
    private const int WmLButtonUp = 0x0202;

    private readonly HwndSource _window;
    private readonly uint _taskbarCreated;
    private nint _icon;
    private string _tip = "";
    private int _number = -1;
    private bool _added;

    public event Action? Clicked;

    public NumberTrayIcon()
    {
        _window = new HwndSource(new HwndSourceParameters("PerfTunerDesktopNumber") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = -3 /* HWND_MESSAGE */ });
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    }

    public void Show(int number, string tip)
    {
        _tip = tip.Length > 120 ? tip[..120] : tip;
        if (number != _number || _icon == 0)
        {
            var old = _icon;
            _icon = Draw(number);
            _number = number;
            if (old != 0) { Update(NifIcon | NifTip); DestroyIcon(old); return; }
        }
        Update(NifTip);
    }

    private void Update(uint flags)
    {
        var d = Data(flags | NifMessage | NifIcon);
        _added = _added ? ShellNotifyIconW(NimModify, ref d) || ShellNotifyIconW(NimAdd, ref d) : ShellNotifyIconW(NimAdd, ref d);
    }

    private unsafe NotifyIconData Data(uint flags)
    {
        var d = new NotifyIconData { CbSize = (uint)sizeof(NotifyIconData), HWnd = _window.Handle, UId = 2, UFlags = flags, UCallbackMessage = CallbackMessage, HIcon = _icon };
        var n = Math.Min(_tip.Length, 127);
        for (var i = 0; i < n; i++) { d.SzTip[i] = _tip[i]; }
        d.SzTip[n] = '\0';
        return d;
    }

    /// <summary>Renders the number in the theme's accent colour into a small icon.</summary>
    private static nint Draw(int number)
    {
        var size = Math.Max(16, GetSystemMetrics(49 /* SM_CXSMICON */));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var accent = Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.SteelBlue;
            dc.DrawRoundedRectangle(accent, null, new Rect(0, 0, size, size), size * 0.22, size * 0.22);
            var text = new FormattedText(number > 99 ? "+" : number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size * (number > 9 ? 0.58 : 0.72), Brushes.White, 1.0);
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var pixels = new byte[size * size * 4];
        bmp.CopyPixels(pixels, size * 4, 0);
        var color = CreateBitmap(size, size, 1, 32, pixels);
        var mask = CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);
        var info = new IconInfo { FIcon = 1, HbmMask = mask, HbmColor = color };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == CallbackMessage && (int)lParam == WmLButtonUp) { Clicked?.Invoke(); handled = true; }
        else if (msg == (int)_taskbarCreated && _icon != 0) { _added = false; Update(NifTip); }
        return 0;
    }

    public void Dispose()
    {
        if (_added) { var d = Data(0); ShellNotifyIconW(NimDelete, ref d); _added = false; }
        if (_icon != 0) { DestroyIcon(_icon); _icon = 0; }
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
        public int UCallbackMessage;
        public nint HIcon;
        public fixed char SzTip[128];
        public uint DwState;
        public uint DwStateMask;
        public fixed char SzInfo[256];
        public uint UTimeoutOrVersion;
        public fixed char SzInfoTitle[64];
        public uint DwInfoFlags;
        public Guid GuidItem;
        public nint HBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int FIcon;
        public uint XHotspot;
        public uint YHotspot;
        public nint HbmMask;
        public nint HbmColor;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIconW(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconIndirect(ref IconInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}
