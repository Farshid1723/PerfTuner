namespace PerfTuner.Core.Space;

/// <summary>
/// Duplicate finder, step 1 (pure): files of exactly the same size are candidates; only those get their content
/// compared (hashing happens in the app). Groups sorted by the space the extra copies waste.
/// </summary>
public static class Duplicates
{
    public static IReadOnlyList<IReadOnlyList<SpaceNode>> SizeGroups(SpaceNode root, long minBytes = 1L << 20)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.Descendants()
            .Where(n => !n.IsFolder && n.Size >= minBytes && (n.Flags & (SpaceFlags.Aggregate | SpaceFlags.ReparsePoint)) == 0)
            .GroupBy(n => n.Size)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<SpaceNode>)g.ToList())
            .OrderByDescending(Wasted)
            .ToList();
    }

    /// <summary>Space taken by all copies but one.</summary>
    public static long Wasted(IReadOnlyList<SpaceNode> group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Count < 2 ? 0 : group[0].Size * (group.Count - 1);
    }
}
