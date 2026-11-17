using System.Windows;
using System.Windows.Media;
using PerfTuner.Core.Hardware;

namespace PerfTuner.App.Themes;

/// <summary>A palette as the UI uses it: the hex values from <see cref="ThemeColors"/> plus preview brushes.</summary>
public sealed class ThemePalette(ThemeColors colors)
{
    public ThemeColors Colors { get; } = colors;
    public string Id => Colors.Id;
    public string Name => Colors.Name;
    public bool Dark => Colors.Dark;
    public bool IsMetal => Colors.Family == ThemeFamily.Metal;
    public string Background => Colors.Background;
    public string Sidebar => Colors.Sidebar;
    public string Surface => Colors.Surface;
    public string SurfaceAlt => Colors.SurfaceAlt;
    public string Border => Colors.Border;
    public string Text => Colors.Text;
    public string Subtle => Colors.Subtle;
    public string Accent => Colors.Accent;
    public string AccentAlt => Colors.AccentAlt;
    public string OnAccent => Colors.OnAccent;
    public string Good => Colors.Good;
    public string Warn => Colors.Warn;
    public string Bad => Colors.Bad;

    public Brush Swatch => ThemeManager.Gradient(Accent, AccentAlt);
    public Brush SwatchBackground => IsMetal ? ThemeManager.Brushed(Background, Dark) : new SolidColorBrush(ThemeManager.Parse(Background));
    public Brush SwatchPlate => IsMetal ? ThemeManager.Plate(Surface, Dark) : new SolidColorBrush(ThemeManager.Parse(Surface));
    public Brush SwatchText => new SolidColorBrush(ThemeManager.Parse(Text));
    public CornerRadius SwatchCorner => IsMetal ? new CornerRadius(0) : new CornerRadius(11);
    public CornerRadius SwatchInnerCorner => IsMetal ? new CornerRadius(0) : new CornerRadius(5);
}

/// <summary>
/// 27 predefined palettes (<see cref="ThemeCatalog"/>): 6 Classic (soft, rounded) and 21 Metal (square, machined,
/// brushed). Applying one replaces the brush and shape resources in <see cref="Application.Resources"/> — the whole UI
/// uses DynamicResource — and switches the Fluent base theme (Light/Dark) so standard controls match.
/// </summary>
public static class ThemeManager
{
    public static IReadOnlyList<ThemePalette> All { get; } = ThemeCatalog.All.Select(c => new ThemePalette(c)).ToList();

    public static IReadOnlyList<ThemePalette> Classic { get; } = All.Where(t => !t.IsMetal).ToList();

    public static IReadOnlyList<ThemePalette> Metal { get; } = All.Where(t => t.IsMetal).ToList();

    public static ThemePalette Current { get; private set; } = All[0];

    public static event Action? Changed;

    public static ThemePalette Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    public static void Apply(ThemePalette t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var r = Application.Current.Resources;
        void Set(string key, string hex) => r[key] = Frozen(new SolidColorBrush(Parse(hex)));
        void SetColor(string key, string hex) => r[key] = Parse(hex);

        Set("BgBrush", t.Background);
        Set("SidebarBrush", t.Sidebar);
        Set("SurfaceBrush", t.Surface);
        Set("SurfaceAltBrush", t.SurfaceAlt);
        Set("BorderBrush", t.Border);
        Set("TextBrush", t.Text);
        Set("SubtleBrush", t.Subtle);
        Set("AccentBrush", t.Accent);
        Set("AccentAltBrush", t.AccentAlt);
        Set("OnAccentBrush", t.OnAccent);
        Set("GoodBrush", t.Good);
        Set("WarnBrush", t.Warn);
        Set("BadBrush", t.Bad);
        r["AccentSoftBrush"] = Frozen(new SolidColorBrush(WithAlpha(Parse(t.Accent), (byte)(t.Dark ? 0x2E : 0x1F))));
        r["HoverBrush"] = Frozen(new SolidColorBrush(WithAlpha(Parse(t.Accent), (byte)(t.Dark ? 0x1A : 0x12))));
        r["GoodSoftBrush"] = Frozen(new SolidColorBrush(WithAlpha(Parse(t.Good), 0x26)));
        r["WarnSoftBrush"] = Frozen(new SolidColorBrush(WithAlpha(Parse(t.Warn), 0x26)));
        r["BadSoftBrush"] = Frozen(new SolidColorBrush(WithAlpha(Parse(t.Bad), 0x26)));
        r["AccentGradient"] = Frozen(Gradient(t.Accent, t.AccentAlt));
        r["HeroGradient"] = Frozen(new LinearGradientBrush(
            WithAlpha(Parse(t.Accent), (byte)(t.Dark ? 0x40 : 0x26)), WithAlpha(Parse(t.AccentAlt), (byte)(t.Dark ? 0x14 : 0x0C)), 20));
        // Fluent base controls (CheckBox, Slider, DataGrid selection…) follow the theme accent too.
        Set("AccentFillColorDefaultBrush", t.Accent);
        Set("AccentFillColorSecondaryBrush", t.Accent);
        Set("AccentFillColorTertiaryBrush", t.AccentAlt);
        Set("TextOnAccentFillColorPrimaryBrush", t.OnAccent);
        SetColor("AccentColor", t.Accent);
        SetColor("ShadowColor", t.Dark ? "#000000" : "#5A6B80");

        ApplyShape(r, t);

#pragma warning disable WPF0001 // Fluent theme mode is experimental in .NET 9
        Application.Current.ThemeMode = t.Dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        Current = t;
        Changed?.Invoke();
    }

    /// <summary>
    /// Shape tokens. Classic: rounded corners, flat plates. Metal: square corners, plates with a light top edge and a
    /// dark bottom edge (bevel), a faint brushed texture on the window and a brushed-metal title bar.
    /// </summary>
    private static void ApplyShape(ResourceDictionary r, ThemePalette t)
    {
        var metal = t.IsMetal;
        r["CardRadius"] = new CornerRadius(metal ? 0 : 14);
        r["ControlRadius"] = new CornerRadius(metal ? 0 : 9);
        r["PopupRadius"] = new CornerRadius(metal ? 0 : 14);
        r["PillRadius"] = new CornerRadius(metal ? 2 : 999);
        r["IconRadius"] = new CornerRadius(metal ? 3 : 999);
        r["BarRadius"] = new CornerRadius(metal ? 0 : 4);

        var surface = Parse(t.Surface);
        var sheen = Mix(surface, Colors.White, t.Dark ? 0.16 : 0.9);
        var shadow = Mix(surface, Colors.Black, t.Dark ? 0.55 : 0.22);
        r["SheenBrush"] = Frozen(new SolidColorBrush(sheen));
        r["ShadeBrush"] = Frozen(new SolidColorBrush(shadow));
        r["AccentGlowBrush"] = Frozen(new SolidColorBrush(WithAlpha(Parse(t.Accent), 0x66)));

        if (metal)
        {
            r["BgBrush"] = Frozen(Brushed(t.Background, t.Dark));
            r["PlateBrush"] = Frozen(Plate(t.Surface, t.Dark));
            var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            edge.GradientStops.Add(new GradientStop(sheen, 0));
            edge.GradientStops.Add(new GradientStop(Parse(t.Border), 0.35));
            edge.GradientStops.Add(new GradientStop(Parse(t.Border), 0.7));
            edge.GradientStops.Add(new GradientStop(shadow, 1));
            r["PlateEdgeBrush"] = Frozen(edge);
            var side = Parse(t.Sidebar);
            var title = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            title.GradientStops.Add(new GradientStop(Mix(side, Colors.White, t.Dark ? 0.10 : 0.55), 0));
            title.GradientStops.Add(new GradientStop(side, 0.55));
            title.GradientStops.Add(new GradientStop(Mix(side, Colors.Black, t.Dark ? 0.25 : 0.06), 1));
            r["TitleBarBrush"] = Frozen(title);
            var rail = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            rail.GradientStops.Add(new GradientStop(Mix(side, Colors.White, t.Dark ? 0.04 : 0.3), 0));
            rail.GradientStops.Add(new GradientStop(side, 0.85));
            rail.GradientStops.Add(new GradientStop(Mix(side, Colors.Black, t.Dark ? 0.3 : 0.08), 1));
            r["SidebarBrush"] = Frozen(rail);
        }
        else
        {
            r["PlateBrush"] = Frozen(new SolidColorBrush(surface));
            r["PlateEdgeBrush"] = Frozen(new SolidColorBrush(Parse(t.Border)));
            r["TitleBarBrush"] = Frozen(new SolidColorBrush(Parse(t.Sidebar)));
        }
    }

    /// <summary>Brushed metal: the base colour with faint horizontal hairlines of varying brightness, tiled.</summary>
    public static Brush Brushed(string baseHex, bool dark)
    {
        var baseColor = Parse(baseHex);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(baseColor), null, new RectangleGeometry(new Rect(0, 0, 256, 24))));
        // Deterministic "random" hairlines so every launch looks the same.
        var seed = 7u;
        for (var y = 0; y < 24; y++)
        {
            seed = seed * 1103515245u + 12345u;
            var light = (seed >> 16) % 2 == 0;
            var alpha = (byte)(2 + (seed >> 20) % (dark ? 5u : 7u));
            var c = light ? Color.FromArgb(alpha, 255, 255, 255) : Color.FromArgb(alpha, 0, 0, 0);
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(c), null, new RectangleGeometry(new Rect(0, y, 256, 1))));
        }
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 256, 24),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        return brush;
    }

    /// <summary>A metal plate: slightly lighter at the top than at the bottom.</summary>
    public static Brush Plate(string surfaceHex, bool dark)
    {
        var s = Parse(surfaceHex);
        var plate = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        plate.GradientStops.Add(new GradientStop(Mix(s, Colors.White, dark ? 0.045 : 0.5), 0));
        plate.GradientStops.Add(new GradientStop(Mix(s, Colors.Black, dark ? 0.12 : 0.03), 1));
        return plate;
    }

    public static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount),
        (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));

    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static LinearGradientBrush Gradient(string a, string b) => new(Parse(a), Parse(b), 35);

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
