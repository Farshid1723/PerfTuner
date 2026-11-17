using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PerfTuner.Core.Hardware;

namespace PerfTuner.App.Controls;

public enum HardwareKind
{
    Cpu,
    Memory,
    Ssd,
    Hdd,
    Network,
}

/// <summary>
/// Animated, theme-coloured hardware icon (design 6.2). Motion follows <see cref="Load"/>, colour follows
/// <see cref="State"/> (calm = accent, busy = warn, strained = bad). <see cref="PlayCoolDown"/> plays a short
/// "cooled down" transition with a check mark after a successful fix.
/// Cost control: one 30 fps clock per icon, stopped while the icon isn't visible or the window is minimized;
/// fully static when Windows animation effects are off.
/// </summary>
public sealed class HardwareIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(HardwareKind), typeof(HardwareIcon),
        new FrameworkPropertyMetadata(HardwareKind.Cpu, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LoadProperty = DependencyProperty.Register(nameof(Load), typeof(double), typeof(HardwareIcon),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(LoadState), typeof(HardwareIcon),
        new FrameworkPropertyMetadata(LoadState.Calm, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((HardwareIcon)d)._previousState = (LoadState)e.OldValue));

    /// <summary>Incremented by view models after a successful fix; any change plays the cool-down.</summary>
    public static readonly DependencyProperty CoolDownTokenProperty = DependencyProperty.Register(nameof(CoolDownToken), typeof(int), typeof(HardwareIcon),
        new FrameworkPropertyMetadata(0, (d, e) => { if ((int)e.NewValue != 0) { ((HardwareIcon)d).PlayCoolDown(); } }));

    private static readonly DependencyProperty TickProperty = DependencyProperty.Register("Tick", typeof(double), typeof(HardwareIcon),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double CoolDownSeconds = 1.6;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastSeconds;
    private double _t;            // animation time, advances faster under load
    private double _shownLoad;    // eased load
    private double _coolStart = -10;
    private LoadState _previousState;
    private bool _running;
    private Window? _window;

    public HardwareKind Kind { get => (HardwareKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>0 … 1.</summary>
    public double Load { get => (double)GetValue(LoadProperty); set => SetValue(LoadProperty, value); }

    public LoadState State { get => (LoadState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    public int CoolDownToken { get => (int)GetValue(CoolDownTokenProperty); set => SetValue(CoolDownTokenProperty, value); }

    public HardwareIcon()
    {
        IsVisibleChanged += (_, _) => UpdateClock();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null) { _window.StateChanged += OnWindowStateChanged; }
            Themes.ThemeManager.Changed += InvalidateVisual;
            UpdateClock();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) { _window.StateChanged -= OnWindowStateChanged; }
            Themes.ThemeManager.Changed -= InvalidateVisual;
            StopClock();
        };
    }

    public void PlayCoolDown()
    {
        _coolStart = _clock.Elapsed.TotalSeconds;
        if (!_running && Animated)
        {
            UpdateClock();
        }
        InvalidateVisual();
    }

    private static bool Animated => SystemParameters.ClientAreaAnimation;

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateClock();

    private void UpdateClock()
    {
        var shouldRun = Animated && IsVisible && _window?.WindowState != WindowState.Minimized && _window?.IsVisible != false;
        if (shouldRun == _running) { return; }
        if (shouldRun)
        {
            var tick = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(1))) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(tick, 30);
            BeginAnimation(TickProperty, tick);
            _lastSeconds = _clock.Elapsed.TotalSeconds;
            _running = true;
        }
        else
        {
            StopClock();
        }
    }

    private void StopClock()
    {
        BeginAnimation(TickProperty, null);
        _running = false;
    }

    // ------------------------------------------------------------------ drawing

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 64 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 64 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastSeconds, 0, 0.2);
        _lastSeconds = now;
        var target = Math.Clamp(Load, 0, 1);
        _shownLoad = _running ? _shownLoad + (target - _shownLoad) * Math.Min(1, dt * 3) : target;
        var load = _shownLoad;

        var cool = Math.Clamp((now - _coolStart) / CoolDownSeconds, 0, 1);
        var cooling = cool < 1;
        // Under a cool-down the motion eases down quickly, so the change is visible.
        var speed = cooling ? 0.25 + load * (0.4 + cool * 0.6) : 0.25 + load;
        _t += dt * speed;

        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) { return; }
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / 100, size / 100));

        var p = new Paint(this, State, _previousState, cool, cooling);
        switch (Kind)
        {
            case HardwareKind.Cpu: DrawCpu(dc, p, load); break;
            case HardwareKind.Memory: DrawMemory(dc, p, load); break;
            case HardwareKind.Ssd: DrawSsd(dc, p, load); break;
            case HardwareKind.Hdd: DrawHdd(dc, p, load); break;
            default: DrawNetwork(dc, p, load); break;
        }
        if (cooling) { DrawCheck(dc, p, cool); }
        dc.Pop();
        dc.Pop();
    }

    /// <summary>Resolved theme colours for one frame.</summary>
    private readonly struct Paint
    {
        public readonly Color State, Accent, Good, Body, BodyEdge, Dim, Text, Sheen;
        public readonly bool Square;

        public Paint(FrameworkElement e, LoadState state, LoadState previous, double cool, bool cooling)
        {
            Color C(string key, string fallback) => e.TryFindResource(key) is SolidColorBrush b ? b.Color : (Color)ColorConverter.ConvertFromString(fallback);
            var accent = C("AccentBrush", "#2BC4B4");
            var good = C("GoodBrush", "#3DD68C");
            Accent = accent;
            Good = good;
            var warn = C("WarnBrush", "#F5B83D");
            var bad = C("BadBrush", "#F0616D");
            Color Of(LoadState s) => s switch { LoadState.Strained => bad, LoadState.Busy => warn, _ => accent };
            // Cool-down: from the previous (hot) colour through "good" to the new state's colour.
            State = !cooling ? Of(state)
                : cool < 0.5 ? Lerp(Of(previous == LoadState.Calm ? state : previous), good, cool * 2)
                : Lerp(good, Of(state), (cool - 0.5) * 2);
            Body = C("SurfaceAltBrush", "#212A34");
            BodyEdge = C("BorderBrush", "#2A3542");
            Dim = Lerp(Body, C("SubtleBrush", "#8D9AA8"), 0.25);
            Text = C("TextBrush", "#E8EEF3");
            Sheen = C("SheenBrush", "#3A4450");
            Square = Themes.ThemeManager.Current.IsMetal;
        }

        public double R(double rounded) => Square ? 0 : rounded;
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Color Alpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    private static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static Pen P(Color c, double w) { var p = new Pen(B(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; p.Freeze(); return p; }

    private static Brush Metal(Paint p)
    {
        var g = new LinearGradientBrush(Lerp(p.Body, p.Sheen, 0.55), Lerp(p.Body, Colors.Black, 0.25), new Point(0, 0), new Point(0, 1));
        g.Freeze();
        return g;
    }

    private static void Glow(DrawingContext dc, Color c, Point center, double radius, double strength)
    {
        var g = new RadialGradientBrush(Alpha(c, strength), Alpha(c, 0));
        g.Freeze();
        dc.DrawEllipse(g, null, center, radius, radius);
    }

    /// <summary>Stable pseudo-random 0…1 for an integer seed.</summary>
    private static double Hash(int n)
    {
        unchecked
        {
            var x = (uint)n * 2654435761u;
            x ^= x >> 13;
            x *= 0x5bd1e995;
            x ^= x >> 15;
            return (x & 0xFFFF) / 65535.0;
        }
    }

    private void DrawCpu(DrawingContext dc, Paint p, double load)
    {
        // Heat glow: pulses faster and stronger under load.
        var pulse = 0.5 + 0.5 * Math.Sin(_t * Math.PI * 2 * (0.6 + 2.4 * load));
        Glow(dc, p.State, new Point(50, 50), 44 + 6 * load, 0.18 + 0.32 * load * (0.6 + 0.4 * pulse));
        // Pins.
        var pin = B(Lerp(p.Dim, p.Sheen, 0.4));
        for (var i = 0; i < 6; i++)
        {
            var o = 30 + i * 8;
            dc.DrawRectangle(pin, null, new Rect(o, 16, 3.2, 9));
            dc.DrawRectangle(pin, null, new Rect(o, 75, 3.2, 9));
            dc.DrawRectangle(pin, null, new Rect(16, o, 9, 3.2));
            dc.DrawRectangle(pin, null, new Rect(75, o, 9, 3.2));
        }
        var body = new Rect(24, 24, 52, 52);
        dc.DrawRoundedRectangle(Metal(p), P(p.BodyEdge, 1.2), body, p.R(6), p.R(6));
        dc.DrawRoundedRectangle(null, P(Alpha(p.Sheen, 0.8), 0.8), new Rect(25.5, 25.5, 49, 49), p.R(5), p.R(5));
        // Core grid: lit cells ∝ load, the lit set reshuffles faster when busy.
        var lit = (int)Math.Round(load * 16);
        var shuffle = (int)(_t * (1.5 + 6 * load));
        var order = Enumerable.Range(0, 16).OrderBy(i => Hash(i * 31 + shuffle * 7)).ToArray();
        var on = new bool[16];
        for (var k = 0; k < lit; k++) { on[order[k]] = true; }
        var unlit = B(Lerp(p.Body, Colors.Black, 0.3));
        var litBrush = B(p.State);
        for (var i = 0; i < 16; i++)
        {
            var x = 32 + i % 4 * 9.5;
            var y = 32 + i / 4 * 9.5;
            dc.DrawRoundedRectangle(on[i] ? litBrush : unlit, null, new Rect(x, y, 7.5, 7.5), p.R(1.5), p.R(1.5));
        }
    }

    private void DrawMemory(DrawingContext dc, Paint p, double load)
    {
        Glow(dc, p.State, new Point(50, 50), 46, 0.10 + 0.25 * load);
        var stick = new Rect(8, 30, 84, 34);
        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            g.BeginFigure(new Point(8, 30), true, true);
            g.LineTo(new Point(92, 30), true, false);
            g.LineTo(new Point(92, 64), true, false);
            g.LineTo(new Point(55, 64), true, false);
            g.LineTo(new Point(55, 60), true, false);
            g.LineTo(new Point(51, 60), true, false);
            g.LineTo(new Point(51, 64), true, false);
            g.LineTo(new Point(8, 64), true, false);
        }
        outline.Freeze();
        dc.DrawGeometry(Metal(p), P(p.BodyEdge, 1.2), outline);
        // Gold contacts.
        var gold = B(Color.FromRgb(0xD6, 0xA8, 0x45));
        for (var x = 11.0; x < 90; x += 3.4)
        {
            if (x > 49 && x < 56) { continue; }
            dc.DrawRectangle(gold, null, new Rect(x, 65, 2, 7));
        }
        // Eight chips fill left to right with the used share.
        var fill = load * 8;
        var empty = B(Lerp(p.Body, Colors.Black, 0.35));
        var full = B(p.State);
        for (var i = 0; i < 8; i++)
        {
            var r = new Rect(12 + i * 10, 35, 8, 20);
            dc.DrawRoundedRectangle(empty, null, r, p.R(1), p.R(1));
            var share = Math.Clamp(fill - i, 0, 1);
            if (share > 0) { dc.DrawRoundedRectangle(full, null, new Rect(r.X, r.Bottom - r.Height * share, r.Width, r.Height * share), p.R(1), p.R(1)); }
        }
        // Shimmer runs along the stick while memory is under pressure.
        if (State != LoadState.Calm || load > 0.6)
        {
            var x = 8 + (_t * 60 % 120) - 20;
            var band = new LinearGradientBrush(Alpha(Colors.White, 0), Alpha(Colors.White, 0.22), new Point(0, 0), new Point(1, 0)) { SpreadMethod = GradientSpreadMethod.Reflect };
            band.Freeze();
            dc.PushClip(outline);
            dc.DrawRectangle(band, null, new Rect(x, 30, 10, 34));
            dc.Pop();
        }
    }

    private void DrawSsd(DrawingContext dc, Paint p, double load)
    {
        Glow(dc, p.State, new Point(50, 50), 46, 0.08 + 0.22 * load);
        var board = new Rect(10, 24, 80, 52);
        dc.DrawRoundedRectangle(Metal(p), P(p.BodyEdge, 1.2), board, p.R(5), p.R(5));
        var chip = B(Lerp(p.Body, Colors.Black, 0.4));
        dc.DrawRoundedRectangle(chip, P(Alpha(p.Sheen, 0.6), 0.6), new Rect(16, 34, 18, 18), p.R(2), p.R(2));
        for (var i = 0; i < 3; i++) { dc.DrawRoundedRectangle(chip, P(Alpha(p.Sheen, 0.6), 0.6), new Rect(40 + i * 15, 32, 12, 30), p.R(2), p.R(2)); }
        // Data streaks flow from the controller to the flash chips.
        var streak = P(Alpha(p.State, 0.85), 1.6);
        var lanes = 1 + (int)(load * 3);
        for (var i = 0; i < lanes; i++)
        {
            var x = 34 + ((_t * 40 + i * 13) % 46);
            dc.DrawLine(streak, new Point(x, 56 + i * 4), new Point(Math.Min(x + 6, 85), 56 + i * 4));
        }
        // Activity LED.
        var blink = load > 0.02 && Math.Sin(_t * Math.PI * 2 * (1 + 12 * load)) > 0.2;
        var led = new Point(82, 31);
        if (blink) { Glow(dc, p.State, led, 8, 0.7); }
        dc.DrawEllipse(B(blink ? p.State : p.Dim), null, led, 2.6, 2.6);
        dc.DrawRectangle(B(Color.FromRgb(0xD6, 0xA8, 0x45)), null, new Rect(90, 36, 4, 28));
    }

    private void DrawHdd(DrawingContext dc, Paint p, double load)
    {
        Glow(dc, p.State, new Point(46, 50), 46, 0.08 + 0.22 * load);
        var body = new Rect(10, 10, 80, 80);
        dc.DrawRoundedRectangle(Metal(p), P(p.BodyEdge, 1.2), body, p.R(7), p.R(7));
        var c = new Point(44, 46);
        var platter = new RadialGradientBrush(Lerp(p.Sheen, Colors.White, 0.25), Lerp(p.Body, Colors.Black, 0.2)) { GradientOrigin = new Point(0.35, 0.3) };
        platter.Freeze();
        dc.DrawEllipse(platter, P(p.BodyEdge, 1), c, 30, 30);
        // Spinning sheen: rotation speed ∝ load.
        var angle = _t * 360 * 1.5;
        var spoke = P(Alpha(Colors.White, 0.28), 1.2);
        dc.PushTransform(new RotateTransform(angle, c.X, c.Y));
        dc.DrawLine(spoke, new Point(c.X, c.Y - 28), new Point(c.X, c.Y - 9));
        dc.DrawLine(P(Alpha(Colors.White, 0.12), 1), new Point(c.X + 24, c.Y + 14), new Point(c.X + 8, c.Y + 5));
        dc.Pop();
        dc.DrawEllipse(B(p.Dim), P(p.BodyEdge, 1), c, 6, 6);
        // Read arm swings with activity.
        var swing = -18 + 14 * load + 6 * Math.Sin(_t * Math.PI * 2 * (0.5 + 3 * load)) * load;
        var pivot = new Point(78, 78);
        dc.PushTransform(new RotateTransform(swing, pivot.X, pivot.Y));
        dc.DrawLine(P(Lerp(p.Sheen, p.Text, 0.3), 3), pivot, new Point(54, 40));
        dc.DrawEllipse(B(p.State), null, new Point(54, 40), 2.4, 2.4);
        dc.Pop();
        dc.DrawEllipse(B(Lerp(p.Dim, p.Sheen, 0.5)), P(p.BodyEdge, 1), pivot, 5, 5);
    }

    private void DrawNetwork(DrawingContext dc, Paint p, double load)
    {
        Glow(dc, p.State, new Point(50, 60), 44, 0.08 + 0.24 * load);
        var c = new Point(50, 64);
        var globe = new RadialGradientBrush(Lerp(p.Body, p.Sheen, 0.6), Lerp(p.Body, Colors.Black, 0.2)) { GradientOrigin = new Point(0.35, 0.3) };
        globe.Freeze();
        dc.DrawEllipse(globe, P(p.BodyEdge, 1.2), c, 22, 22);
        var line = P(Alpha(p.State, 0.75), 1.1);
        dc.DrawEllipse(null, line, c, 9, 22);
        dc.DrawLine(line, new Point(28, 64), new Point(72, 64));
        dc.DrawLine(line, new Point(c.X, 42), new Point(c.X, 86));
        dc.DrawEllipse(null, P(Alpha(p.State, 0.45), 0.9), new Point(50, 56), 19, 4);
        dc.DrawEllipse(null, P(Alpha(p.State, 0.45), 0.9), new Point(50, 72), 19, 4);
        // Signal waves rise from the globe; rate ∝ traffic.
        var phase = _t * (0.6 + 2.2 * load) % 1;
        for (var i = 0; i < 3; i++)
        {
            var k = (i + phase) / 3;
            var radius = 26 + k * 22;
            var fade = 1 - k;
            var arc = new StreamGeometry();
            using (var g = arc.Open())
            {
                var a0 = (210 + 20) * Math.PI / 180;
                var a1 = (330 - 20) * Math.PI / 180;
                g.BeginFigure(new Point(c.X + radius * Math.Cos(a0), c.Y + radius * Math.Sin(a0)), false, false);
                g.ArcTo(new Point(c.X + radius * Math.Cos(a1), c.Y + radius * Math.Sin(a1)), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, P(Alpha(p.State, fade * (0.35 + 0.6 * Math.Max(load, 0.15))), 3), arc);
        }
    }

    private static void DrawCheck(DrawingContext dc, Paint p, double cool)
    {
        // Check mark draws in over the first half, then fades out.
        var draw = Math.Clamp(cool / 0.45, 0, 1);
        var fade = cool < 0.6 ? 1 : 1 - (cool - 0.6) / 0.4;
        dc.DrawEllipse(B(Alpha(p.Good, 0.22 * fade)), P(Alpha(p.Good, 0.9 * fade), 2.5), new Point(50, 50), 22, 22);
        var a = new Point(39, 51);
        var b = new Point(47, 59);
        var c = new Point(62, 42);
        var pen = P(Alpha(p.Good, fade), 5);
        var first = Math.Clamp(draw / 0.4, 0, 1);
        dc.DrawLine(pen, a, new Point(a.X + (b.X - a.X) * first, a.Y + (b.Y - a.Y) * first));
        if (draw > 0.4)
        {
            var second = (draw - 0.4) / 0.6;
            dc.DrawLine(pen, b, new Point(b.X + (c.X - b.X) * second, b.Y + (c.Y - b.Y) * second));
        }
    }
}
