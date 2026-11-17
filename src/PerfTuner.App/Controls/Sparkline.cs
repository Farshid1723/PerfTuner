using System.Windows;
using System.Windows.Media;

namespace PerfTuner.App.Controls;

/// <summary>
/// A tiny, theme-coloured area chart for the last N samples (drawn in OnRender, one visual).
/// <see cref="Max"/> fixes the scale (e.g. 100 for percentages); 0 auto-scales.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(double[]), typeof(Sparkline),
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(nameof(Max), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[] Values { get => (double[])GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var v = Values;
        double w = ActualWidth, h = ActualHeight;
        if (v is null || v.Length < 2 || w < 4 || h < 4) { return; }
        var max = Max > 0 ? Max : Math.Max(1e-9, v.Max() * 1.15);
        var step = w / (v.Length - 1);
        Point P(int i) => new(i * step, h - (Math.Clamp(v[i] / max, 0, 1) * (h - 2)) - 1);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            for (var i = 1; i < v.Length; i++) { ctx.LineTo(P(i), true, true); }
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (var i = 0; i < v.Length; i++) { ctx.LineTo(P(i), true, false); }
            ctx.LineTo(new Point(w, h), true, false);
        }
        area.Freeze();

        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.SteelBlue;
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B), Color.FromArgb(0x00, color.R, color.G, color.B), 90);
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
