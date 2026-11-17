using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PerfTuner.App.Controls;

/// <summary>
/// Circular 270° gauge (drawn in OnRender — one visual, no per-segment elements). The value
/// animates smoothly; colours come from the active theme; the arc follows the value (accent/good → warn → bad) or <see cref="Invert"/>ed
/// for "higher is better" scores. Big text in the middle, caption underneath.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, (d, e) => ((RingGauge)d).AnimateTo((double)e.NewValue)));

    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InvertProperty = DependencyProperty.Register(nameof(Invert), typeof(bool), typeof(RingGauge),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FixedColorProperty = DependencyProperty.Register(nameof(FixedColor), typeof(Color?), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    /// <summary>True when a high value is good (PulseScore): high = green.</summary>
    public bool Invert { get => (bool)GetValue(InvertProperty); set => SetValue(InvertProperty, value); }

    public Color? FixedColor { get => (Color?)GetValue(FixedColorProperty); set => SetValue(FixedColorProperty, value); }

    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public RingGauge()
    {
        Loaded += (_, _) => Themes.ThemeManager.Changed += InvalidateVisual;
        Unloaded += (_, _) => Themes.ThemeManager.Changed -= InvalidateVisual;
    }

    private Brush Res(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private Color ResColor(string key, Color fallback) => (TryFindResource(key) as SolidColorBrush)?.Color ?? fallback;

    private void AnimateTo(double target)
    {
        var anim = new DoubleAnimation(Math.Clamp(target, 0, 100), TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(ShownProperty, anim);
    }

    private Color ColorFor(double v)
    {
        if (FixedColor is { } c) { return c; }
        var bad = Invert ? 100 - v : v;
        return bad < 60
            ? ResColor(Invert ? "GoodBrush" : "AccentBrush", Colors.SeaGreen)
            : bad < 85 ? ResColor("WarnBrush", Colors.Orange) : ResColor("BadBrush", Colors.IndianRed);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20) { return; }
        var t = Math.Min(Thickness, size / 6);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = (size / 2) - t;
        var value = (double)GetValue(ShownProperty);
        var color = ColorFor(Value);

        var trackPen = new Pen(Res("SurfaceAltBrush", Colors.DimGray), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, trackPen, Arc(center, r, 135, 270));

        if (value > 0.5)
        {
            var sweep = 270 * value / 100;
            var pen = new Pen(new SolidColorBrush(color), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Arc(center, r, 135, sweep));
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!string.IsNullOrEmpty(Text))
        {
            var ft = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size * 0.24, Res("TextBrush", Colors.White), dpi);
            dc.DrawText(ft, new Point(center.X - (ft.Width / 2), center.Y - (ft.Height / 2) - (size * 0.03)));
        }
        if (!string.IsNullOrEmpty(Caption))
        {
            var ft = new FormattedText(Caption, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, Math.Max(10, size * 0.085), Res("SubtleBrush", Colors.Gray), dpi)
            {
                MaxTextWidth = r * 1.6,
                TextAlignment = TextAlignment.Center,
                MaxLineCount = 2,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(ft, new Point(center.X - (r * 0.8), center.Y + (r * 0.42)));
        }
    }

    private static StreamGeometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        sweepDeg = Math.Min(sweepDeg, 359.9);
        static Point P(Point c, double r, double deg) => new(c.X + (r * Math.Cos(deg * Math.PI / 180)), c.Y + (r * Math.Sin(deg * Math.PI / 180)));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(c, r, startDeg), false, false);
            ctx.ArcTo(P(c, r, startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
