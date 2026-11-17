using System.Globalization;

namespace PerfTuner.Core.Hardware;

/// <summary>WCAG 2.x contrast ratio between two "#RRGGBB" colours (1 … 21).</summary>
public static class ColorContrast
{
    public static double Ratio(string hexA, string hexB)
    {
        var a = Luminance(hexA);
        var b = Luminance(hexB);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    public static double Luminance(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var h = hex.TrimStart('#');
        if (h.Length != 6) { throw new FormatException($"expected #RRGGBB, got '{hex}'"); }
        static double Channel(string s)
        {
            var c = int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(h[..2]) + 0.7152 * Channel(h[2..4]) + 0.0722 * Channel(h[4..6]);
    }
}
