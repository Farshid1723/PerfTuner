using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PerfTuner.App.Controls;

/// <summary>
/// Documented DWM window attributes: dark/light frame (DWMWA_USE_IMMERSIVE_DARK_MODE = 20) and the Windows 11
/// corner preference (DWMWA_WINDOW_CORNER_PREFERENCE = 33). Metal themes ask for square corners, Classic for rounded.
/// Silently does nothing on Windows versions without these attributes.
/// </summary>
public static partial class WindowFrame
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int DwmwcpDoNotRound = 1;

    public static void Apply(Window window, bool dark, bool square)
    {
        ArgumentNullException.ThrowIfNull(window);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) { return; }
        var darkValue = dark ? 1 : 0;
        var corner = square ? DwmwcpDoNotRound : DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkValue, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    /// <summary>Applies the current theme now and whenever the theme changes, for the lifetime of the window.</summary>
    public static void Follow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        void Update() => Apply(window, Themes.ThemeManager.Current.Dark, Themes.ThemeManager.Current.IsMetal);
        window.SourceInitialized += (_, _) => Update();
        Themes.ThemeManager.Changed += Update;
        window.Closed += (_, _) => Themes.ThemeManager.Changed -= Update;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
