using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PerfTuner.Core.Space;

namespace PerfTuner.App.Controls;

/// <summary>Theme-derived colours for file categories and folders (shared by the treemap and the sunburst).</summary>
internal static class SpaceColors
{
    public static Color Get(FrameworkElement e, string key, string fallback) =>
        e.TryFindResource(key) is SolidColorBrush b ? b.Color : (Color)ColorConverter.ConvertFromString(fallback);

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Color[] Palette(FrameworkElement e)
    {
        var accent = Get(e, "AccentBrush", "#2BC4B4");
        var alt = Get(e, "AccentAltBrush", "#3D8BFD");
        var good = Get(e, "GoodBrush", "#3DD68C");
        var warn = Get(e, "WarnBrush", "#F5B83D");
        var bad = Get(e, "BadBrush", "#F0616D");
        var subtle = Get(e, "SubtleBrush", "#8D9AA8");
        return [accent, alt, good, warn, bad, Mix(accent, good, 0.5), Mix(alt, bad, 0.5), Mix(warn, bad, 0.5), Mix(accent, warn, 0.5), subtle];
    }

    /// <summary>Category → colour (index into <see cref="Palette"/>).</summary>
    public static Color ForCategory(Color[] palette, FileCategory c) => c switch
    {
        FileCategory.Video => palette[0],
        FileCategory.Audio => palette[1],
        FileCategory.Images => palette[2],
        FileCategory.Archives => palette[3],
        FileCategory.DiskImages => palette[4],
        FileCategory.Documents => palette[5],
        FileCategory.CodeAndBuild => palette[6],
        FileCategory.Installers => palette[7],
        FileCategory.DataAndLogs => palette[8],
        _ => palette[9],
    };

    public static Brush Frozen(Brush b) { b.Freeze(); return b; }

    public static string Size(long bytes) => Format.Bytes(bytes);
}

/// <summary>Common plumbing: current folder, selection, click / double-click / hover events.</summary>
public abstract class SpaceViewBase : FrameworkElement
{
    public static readonly DependencyProperty FolderProperty = DependencyProperty.Register(nameof(Folder), typeof(SpaceNode), typeof(SpaceViewBase),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(IReadOnlyCollection<SpaceNode>), typeof(SpaceViewBase),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bumped by the view model whenever the tree or the selection changes (forces a redraw).</summary>
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(int), typeof(SpaceViewBase),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public SpaceNode? Folder { get => (SpaceNode?)GetValue(FolderProperty); set => SetValue(FolderProperty, value); }
    public IReadOnlyCollection<SpaceNode>? Selected { get => (IReadOnlyCollection<SpaceNode>?)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    /// <summary>Click on an item (with the modifier keys held).</summary>
    public event Action<SpaceNode, ModifierKeys>? NodeClicked;

    /// <summary>Double-click on a folder: make it the current folder.</summary>
    public event Action<SpaceNode>? NodeOpened;

    protected SpaceViewBase()
    {
        ToolTipService.SetInitialShowDelay(this, 250);
        Cursor = Cursors.Hand;
        Focusable = true;
        Themes.ThemeManager.Changed += InvalidateVisual;
        Unloaded += (_, _) => Themes.ThemeManager.Changed -= InvalidateVisual;
        Loaded += (_, _) => { Themes.ThemeManager.Changed -= InvalidateVisual; Themes.ThemeManager.Changed += InvalidateVisual; };
    }

    protected abstract (SpaceNode? Node, string Text) HitTestAt(Point p);

    protected bool IsSelected(SpaceNode n) => Selected?.Contains(n) == true;

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);
        Focus();
        var (node, _) = HitTestAt(e.GetPosition(this));
        if (node is null) { return; }
        if (e.ClickCount >= 2 && node.IsFolder) { NodeOpened?.Invoke(node); }
        else { NodeClicked?.Invoke(node, Keyboard.Modifiers); }
        e.Handled = true;
    }

    private string? _lastTip;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        var (_, text) = HitTestAt(e.GetPosition(this));
        if (text != _lastTip)
        {
            _lastTip = text;
            ToolTip = string.IsNullOrEmpty(text) ? null : text;
        }
    }

    protected static FormattedText Label(string text, double size, Brush brush, double dpi, FontWeight? weight = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, weight ?? FontWeights.SemiBold, FontStretches.Normal), size, brush, dpi);

    protected static string Tip(SpaceNode n, SpaceNode? parent) =>
        $"{n.Name}\n{SpaceColors.Size(n.Size)}" + (parent is { Size: > 0 } ? $" · {100.0 * n.Size / parent.Size:F1} % of {parent.Name}" : "") +
        (n.IsFolder ? $"\n{n.Files:N0} files" + (n.IsFolder ? " · double-click to open" : "") : "");
}

/// <summary>Squarified treemap of the current folder, nested up to three levels, cushion-shaded, coloured by file type.</summary>
public sealed class TreemapView : SpaceViewBase
{
    private readonly List<(Rect Rect, SpaceNode Node, SpaceNode Parent)> _hits = [];
    private const int MaxItemsPerFolder = 400;

    protected override (SpaceNode? Node, string Text) HitTestAt(Point p)
    {
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Rect.Contains(p)) { return (_hits[i].Node, Tip(_hits[i].Node, _hits[i].Parent)); }
        }
        return (null, "");
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        _hits.Clear();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        if (Folder is not { Children: not null } folder || folder.Size <= 0 || bounds.Width < 10 || bounds.Height < 10) { return; }
        var palette = SpaceColors.Palette(this);
        var text = SpaceColors.Get(this, "TextBrush", "#E8EEF3");
        var surface = SpaceColors.Get(this, "SurfaceAltBrush", "#212A34");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var brushes = new Dictionary<Color, Brush>();
        Brush Cushion(Color c)
        {
            if (!brushes.TryGetValue(c, out var b))
            {
                var g = new LinearGradientBrush(SpaceColors.Mix(c, Colors.White, 0.28), SpaceColors.Mix(c, Colors.Black, 0.28), new Point(0, 0), new Point(1, 1));
                b = SpaceColors.Frozen(g);
                brushes[c] = b;
            }
            return b;
        }
        var edge = new Pen(SpaceColors.Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0))), 1);
        edge.Freeze();
        var selectedPen = new Pen(SpaceColors.Frozen(new SolidColorBrush(text)), 2.5);
        selectedPen.Freeze();
        var folderBrush = SpaceColors.Frozen(new SolidColorBrush(SpaceColors.Mix(surface, Colors.Black, 0.25)));
        var labelBrush = SpaceColors.Frozen(new SolidColorBrush(Colors.White));
        var headerBrush = SpaceColors.Frozen(new SolidColorBrush(text));

        void Lay(SpaceNode parent, Rect r, int depth, Color? folderHue)
        {
            var items = parent.Children!.Where(c => c.Size > 0).Take(MaxItemsPerFolder).ToList();
            if (items.Count == 0) { return; }
            var rects = TreemapLayout.Squarify(items.Select(c => (double)c.Size).ToList(), new Core.Space.RectD(r.X, r.Y, r.Width, r.Height));
            for (var i = 0; i < items.Count; i++)
            {
                var n = items[i];
                var rr = new Rect(rects[i].X, rects[i].Y, Math.Max(0, rects[i].W), Math.Max(0, rects[i].H));
                if (rr.Width < 1 || rr.Height < 1) { continue; }
                var hue = folderHue ?? palette[i % 5];
                if (n.IsFolder && n.Children is { Count: > 0 } && depth < 2 && rr.Width > 46 && rr.Height > 34)
                {
                    dc.DrawRectangle(folderBrush, edge, rr);
                    var header = Label(n.Name + "  " + SpaceColors.Size(n.Size), 11, headerBrush, dpi);
                    header.MaxTextWidth = Math.Max(1, rr.Width - 6);
                    header.MaxLineCount = 1;
                    header.Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(header, new Point(rr.X + 4, rr.Y + 1));
                    _hits.Add((rr, n, parent));
                    Lay(n, new Rect(rr.X + 2, rr.Y + 16, Math.Max(0, rr.Width - 4), Math.Max(0, rr.Height - 18)), depth + 1, hue);
                }
                else
                {
                    var c = n.IsFolder ? SpaceColors.Mix(hue, surface, 0.35) : SpaceColors.ForCategory(palette, FileCategories.Of(n.Name));
                    dc.DrawRectangle(Cushion(c), edge, rr);
                    if (rr.Width > 56 && rr.Height > 18)
                    {
                        var ft = Label(n.Name, 11, labelBrush, dpi);
                        ft.MaxTextWidth = rr.Width - 6;
                        ft.MaxLineCount = rr.Height > 34 ? 2 : 1;
                        ft.Trimming = TextTrimming.CharacterEllipsis;
                        dc.DrawText(ft, new Point(rr.X + 3, rr.Y + 2));
                        if (rr.Height > 40)
                        {
                            var sz = Label(SpaceColors.Size(n.Size), 10.5, labelBrush, dpi, FontWeights.Normal);
                            sz.MaxTextWidth = rr.Width - 6;
                            sz.MaxLineCount = 1;
                            dc.DrawText(sz, new Point(rr.X + 3, rr.Bottom - 16));
                        }
                    }
                    _hits.Add((rr, n, parent));
                }
                if (IsSelected(n)) { dc.DrawRectangle(null, selectedPen, new Rect(rr.X + 1, rr.Y + 1, Math.Max(0, rr.Width - 2), Math.Max(0, rr.Height - 2))); }
            }
        }

        Lay(folder, bounds, 0, null);
    }
}

/// <summary>Sunburst: rings around the current folder (up to four levels), angle ∝ size; tiny items merged.</summary>
public sealed class SunburstView : SpaceViewBase
{
    private const int Rings = 4;
    private readonly List<(int Ring, double Start, double Sweep, SpaceNode? Node, SpaceNode Parent, long RestBytes)> _segments = [];
    private Point _center;
    private double _r0;
    private double _ringWidth;

    protected override (SpaceNode? Node, string Text) HitTestAt(Point p)
    {
        var dx = p.X - _center.X;
        var dy = p.Y - _center.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < _r0 || _ringWidth <= 0) { return (null, Folder is { } f ? $"{f.Name}\n{SpaceColors.Size(f.Size)}" : ""); }
        var ring = (int)((dist - _r0) / _ringWidth);
        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        foreach (var s in _segments)
        {
            if (s.Ring != ring) { continue; }
            var rel = ((angle - s.Start) % 360 + 360) % 360;
            if (rel <= s.Sweep)
            {
                return s.Node is { } n ? (n, Tip(n, s.Parent)) : (null, $"Smaller items\n{SpaceColors.Size(s.RestBytes)}");
            }
        }
        return (null, "");
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        _segments.Clear();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Folder is not { Children: not null } folder || folder.Size <= 0) { return; }
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 8;
        if (radius < 40) { return; }
        _center = new Point(ActualWidth / 2, ActualHeight / 2);
        _r0 = radius * 0.24;
        _ringWidth = (radius - _r0) / Rings;
        var palette = SpaceColors.Palette(this);
        var text = SpaceColors.Get(this, "TextBrush", "#E8EEF3");
        var surface = SpaceColors.Get(this, "SurfaceAltBrush", "#212A34");
        var subtle = SpaceColors.Get(this, "SubtleBrush", "#8D9AA8");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var edge = new Pen(SpaceColors.Frozen(new SolidColorBrush(SpaceColors.Get(this, "BgBrush", "#0F1419"))), 1.2);
        edge.Freeze();
        var selectedPen = new Pen(SpaceColors.Frozen(new SolidColorBrush(text)), 2.5);
        selectedPen.Freeze();

        void Ring(SpaceNode parent, int ring, double start, double sweep, Color? hue)
        {
            var items = parent.Children!.Where(c => c.Size > 0).ToList();
            if (items.Count == 0) { return; }
            var arcs = SunburstLayout.Arcs(items.Select(c => (double)c.Size).ToList(), 0.6, start, sweep);
            var inner = _r0 + ring * _ringWidth;
            var outer = inner + _ringWidth - 1;
            var k = 0;
            foreach (var a in arcs)
            {
                if (a.Index < 0)
                {
                    var rest = items.Skip(arcs.Count(x => x.Index >= 0)).Sum(x => x.Size);
                    DrawSector(dc, inner, outer, a.Start, a.Sweep, SpaceColors.Frozen(new SolidColorBrush(SpaceColors.Mix(subtle, surface, 0.5))), edge);
                    _segments.Add((ring, a.Start, a.Sweep, null, parent, rest));
                    continue;
                }
                var n = items[a.Index];
                var baseHue = hue ?? palette[k++ % 5];
                var color = n.IsFolder
                    ? SpaceColors.Mix(baseHue, Colors.White, 0.12 * ring)
                    : SpaceColors.ForCategory(palette, FileCategories.Of(n.Name));
                var fill = new RadialGradientBrush(SpaceColors.Mix(color, Colors.White, 0.15), SpaceColors.Mix(color, Colors.Black, 0.2))
                {
                    MappingMode = BrushMappingMode.Absolute,
                    Center = _center,
                    GradientOrigin = _center,
                    RadiusX = outer,
                    RadiusY = outer,
                };
                DrawSector(dc, inner, outer, a.Start, a.Sweep, SpaceColors.Frozen(fill), IsSelected(n) ? selectedPen : edge);
                _segments.Add((ring, a.Start, a.Sweep, n, parent, 0));
                if (ring == 0 && a.Sweep > 14)
                {
                    var mid = (a.Start + a.Sweep / 2) * Math.PI / 180;
                    var lr = inner + _ringWidth / 2;
                    var ft = Label(n.Name, 10.5, SpaceColors.Frozen(new SolidColorBrush(Colors.White)), dpi);
                    ft.MaxTextWidth = Math.Max(10, _ringWidth * 1.4);
                    ft.MaxLineCount = 1;
                    ft.Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(ft, new Point(_center.X + lr * Math.Cos(mid) - ft.Width / 2, _center.Y + lr * Math.Sin(mid) - ft.Height / 2));
                }
                if (n.IsFolder && n.Children is { Count: > 0 } && ring + 1 < Rings) { Ring(n, ring + 1, a.Start, a.Sweep, baseHue); }
            }
        }

        Ring(folder, 0, -90, 360, null);

        // Centre: the current folder.
        dc.DrawEllipse(SpaceColors.Frozen(new SolidColorBrush(surface)), edge, _center, _r0 - 2, _r0 - 2);
        var name = Label(folder.Name, 13, SpaceColors.Frozen(new SolidColorBrush(text)), dpi);
        name.MaxTextWidth = _r0 * 1.7;
        name.MaxLineCount = 1;
        name.Trimming = TextTrimming.CharacterEllipsis;
        name.TextAlignment = TextAlignment.Center;
        dc.DrawText(name, new Point(_center.X - name.MaxTextWidth / 2, _center.Y - 18));
        var size = Label(SpaceColors.Size(folder.Size), 15, SpaceColors.Frozen(new SolidColorBrush(text)), dpi, FontWeights.Bold);
        size.TextAlignment = TextAlignment.Center;
        size.MaxTextWidth = _r0 * 1.7;
        dc.DrawText(size, new Point(_center.X - size.MaxTextWidth / 2, _center.Y));
    }

    private void DrawSector(DrawingContext dc, double inner, double outer, double start, double sweep, Brush fill, Pen pen)
    {
        sweep = Math.Min(sweep, 359.99);
        double Rad(double d) => d * Math.PI / 180;
        Point P(double r, double deg) => new(_center.X + r * Math.Cos(Rad(deg)), _center.Y + r * Math.Sin(Rad(deg)));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            var large = sweep > 180;
            c.BeginFigure(P(inner, start), true, true);
            c.LineTo(P(outer, start), true, false);
            c.ArcTo(P(outer, start + sweep), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            c.LineTo(P(inner, start + sweep), true, false);
            c.ArcTo(P(inner, start), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(fill, pen, g);
    }
}
