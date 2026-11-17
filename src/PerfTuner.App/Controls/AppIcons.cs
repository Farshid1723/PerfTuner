using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PerfTuner.Core.Apps;

namespace PerfTuner.App.Controls;

/// <summary>
/// The real icon of an installed app, read from its own files on this PC (the icon Windows shows in "Installed apps",
/// or its shortcut's icon). Nothing is downloaded and no logos ship with PerfTuner. Local drive paths only (no network
/// shares). Returns a frozen bitmap, so it can be made on any thread.
/// </summary>
internal static partial class AppIcons
{
    public static ImageSource? Load(InstalledEntry? entry)
    {
        if (entry?.IconPath is not { Length: > 0 } raw) { return null; }
        try
        {
            nint icon;
            if (entry.IconFromShortcut)
            {
                if (!IsLocal(raw) || !File.Exists(raw)) { return null; }
                var info = new ShFileInfo();
                if (SHGetFileInfoW(raw, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), 0x100 /* SHGFI_ICON | SHGFI_LARGEICON */) == 0) { return null; }
                icon = info.hIcon;
            }
            else
            {
                var m = IconSpec().Match(Environment.ExpandEnvironmentVariables(raw.Trim()));
                if (!m.Success) { return null; }
                var path = m.Groups["path"].Value.Trim().Trim('"');
                var index = m.Groups["index"].Success ? int.Parse(m.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                if (!IsLocal(path) || !File.Exists(path)) { return null; }
                if (ExtractIconExW(path, index, out icon, out var small, 1) == 0 || icon == 0) { return null; }
                if (small != 0) { _ = DestroyIcon(small); }
            }
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally
            {
                _ = DestroyIcon(icon);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static bool IsLocal(string path) => path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    [GeneratedRegex(@"^""?(?<path>[^""]+?)""?(?:,(?<index>-?\d+))?$")]
    private static partial Regex IconSpec();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

#pragma warning disable SYSLIB1054 // the struct has by-value strings; classic marshalling is the simple, correct choice here
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);
#pragma warning restore SYSLIB1054

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconExW(string file, int index, out nint large, out nint small, uint count);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}

/// <summary>A coloured initial badge for apps that aren't installed (no third-party logos ship with PerfTuner).</summary>
internal static class Monogram
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x2B, 0x7A, 0xC9), Color.FromRgb(0xC2, 0x41, 0x3B), Color.FromRgb(0x2E, 0x9E, 0x6A), Color.FromRgb(0x8E, 0x5B, 0xC9),
        Color.FromRgb(0xD9, 0x8A, 0x1E), Color.FromRgb(0x1F, 0x9A, 0xA8), Color.FromRgb(0xC2, 0x4A, 0x8A), Color.FromRgb(0x5C, 0x6B, 0x7A),
        Color.FromRgb(0x6A, 0x8F, 0x2B), Color.FromRgb(0x3F, 0x51, 0xB5),
    ];

    private static readonly Dictionary<int, SolidColorBrush> Brushes = [];

    public static string Initials(string name)
    {
        var words = name.Split([' ', '-', '.'], StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToList();
        if (words.Count == 0) { return "?"; }
        return words.Count == 1 ? words[0][..Math.Min(2, words[0].Length)] : $"{words[0][0]}{words[1][0]}";
    }

    public static Brush BrushFor(string id)
    {
        var h = 17;
        foreach (var c in id) { h = unchecked(h * 31 + char.ToUpperInvariant(c)); }
        var i = (int)((uint)h % (uint)Palette.Length);
        if (!Brushes.TryGetValue(i, out var b)) { b = new SolidColorBrush(Palette[i]); b.Freeze(); Brushes[i] = b; }
        return b;
    }
}
