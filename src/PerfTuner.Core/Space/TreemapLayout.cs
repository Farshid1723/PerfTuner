namespace PerfTuner.Core.Space;

/// <summary>A rectangle (no UI types, so layouts are unit-testable).</summary>
public readonly record struct RectD(double X, double Y, double W, double H);

/// <summary>Squarified treemap (Bruls, Huizing, van Wijk): areas proportional to sizes, rectangles close to square.</summary>
public static class TreemapLayout
{
    /// <summary>Lays out <paramref name="sizes"/> (any order; ≥ 0) inside <paramref name="bounds"/>. Result index = input index.</summary>
    public static RectD[] Squarify(IReadOnlyList<double> sizes, RectD bounds)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        var result = new RectD[sizes.Count];
        var total = sizes.Sum(s => Math.Max(0, s));
        if (sizes.Count == 0 || total <= 0 || bounds.W <= 0 || bounds.H <= 0) { return result; }
        var scale = bounds.W * bounds.H / total;
        var order = Enumerable.Range(0, sizes.Count).Where(i => sizes[i] > 0).OrderByDescending(i => sizes[i]).ToList();
        var rect = bounds;
        var row = new List<int>();
        var k = 0;
        while (k < order.Count)
        {
            var side = Math.Min(rect.W, rect.H);
            var candidate = order[k];
            if (row.Count == 0 || Worst(row, sizes, scale, side) >= Worst([.. row, candidate], sizes, scale, side))
            {
                row.Add(candidate);
                k++;
            }
            else
            {
                rect = LayRow(row, sizes, scale, rect, result);
                row.Clear();
            }
        }
        if (row.Count > 0) { LayRow(row, sizes, scale, rect, result); }
        return result;
    }

    private static double Worst(List<int> row, IReadOnlyList<double> sizes, double scale, double side)
    {
        double sum = 0, max = 0, min = double.MaxValue;
        foreach (var i in row)
        {
            var a = sizes[i] * scale;
            sum += a;
            max = Math.Max(max, a);
            min = Math.Min(min, a);
        }
        var s2 = side * side;
        return Math.Max(s2 * max / (sum * sum), sum * sum / (s2 * min));
    }

    private static RectD LayRow(List<int> row, IReadOnlyList<double> sizes, double scale, RectD rect, RectD[] result)
    {
        var area = row.Sum(i => sizes[i] * scale);
        if (rect.W >= rect.H)
        {
            // Column on the left, full height.
            var w = area / rect.H;
            var y = rect.Y;
            foreach (var i in row)
            {
                var h = sizes[i] * scale / w;
                result[i] = new RectD(rect.X, y, w, h);
                y += h;
            }
            return new RectD(rect.X + w, rect.Y, Math.Max(0, rect.W - w), rect.H);
        }
        else
        {
            // Row on top, full width.
            var h = area / rect.W;
            var x = rect.X;
            foreach (var i in row)
            {
                var w = sizes[i] * scale / h;
                result[i] = new RectD(x, rect.Y, w, h);
                x += w;
            }
            return new RectD(rect.X, rect.Y + h, rect.W, Math.Max(0, rect.H - h));
        }
    }
}

/// <summary>One sunburst segment: <see cref="Index"/> into the input, or -1 for the merged "smaller items".</summary>
public readonly record struct Arc(int Index, double Start, double Sweep);

/// <summary>Angles for one sunburst ring: proportional to size, items under <c>minDegrees</c> merged into one segment.</summary>
public static class SunburstLayout
{
    public static Arc[] Arcs(IReadOnlyList<double> sizes, double minDegrees = 0.5, double start = 0, double sweep = 360)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        var total = sizes.Sum(s => Math.Max(0, s));
        if (total <= 0) { return []; }
        var arcs = new List<Arc>();
        double a = start, rest = 0;
        foreach (var i in Enumerable.Range(0, sizes.Count).OrderByDescending(i => sizes[i]))
        {
            var deg = Math.Max(0, sizes[i]) / total * sweep;
            if (deg < minDegrees) { rest += deg; continue; }
            arcs.Add(new Arc(i, a, deg));
            a += deg;
        }
        if (rest > 0) { arcs.Add(new Arc(-1, a, rest)); }
        return [.. arcs];
    }
}
