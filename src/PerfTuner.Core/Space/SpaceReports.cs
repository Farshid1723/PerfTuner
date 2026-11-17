namespace PerfTuner.Core.Space;

public sealed record CategoryTotal(FileCategory Category, long Bytes, long Count);

/// <summary>Reports over a scanned tree (design §6.2). Pure queries; the tree is not modified.</summary>
public static class SpaceReports
{
    public const long BigBytes = 100L << 20;
    public static readonly TimeSpan OldAge = TimeSpan.FromDays(365);

    private static IEnumerable<SpaceNode> RealFiles(SpaceNode root) =>
        root.Descendants().Where(n => !n.IsFolder && !n.Flags.HasFlag(SpaceFlags.Aggregate));

    public static IReadOnlyList<SpaceNode> BiggestFiles(SpaceNode root, int count) =>
        RealFiles(root).OrderByDescending(n => n.Size).Take(count).ToList();

    public static IReadOnlyList<SpaceNode> BiggestFolders(SpaceNode root, int count) =>
        root.Descendants().Where(n => n.IsFolder).OrderByDescending(n => n.Size).Take(count).ToList();

    public static IReadOnlyList<SpaceNode> Oldest(SpaceNode root, int count) =>
        RealFiles(root).Where(n => n.LastWriteUtc > DateTime.MinValue.AddYears(1)).OrderBy(n => n.LastWriteUtc).Take(count).ToList();

    /// <summary>Not changed for a year and at least 100 MB, biggest first.</summary>
    public static IReadOnlyList<SpaceNode> OldAndBig(SpaceNode root, DateTime nowUtc, int count) =>
        RealFiles(root).Where(n => n.Size >= BigBytes && n.LastWriteUtc > DateTime.MinValue.AddYears(1) && nowUtc - n.LastWriteUtc >= OldAge)
            .OrderByDescending(n => n.Size).Take(count).ToList();

    public static IReadOnlyList<SpaceNode> OfCategory(SpaceNode root, FileCategory category, int count) =>
        RealFiles(root).Where(n => FileCategories.Of(n.Name) == category).OrderByDescending(n => n.Size).Take(count).ToList();

    public static IReadOnlyList<CategoryTotal> Summary(SpaceNode root) =>
        RealFiles(root).GroupBy(n => FileCategories.Of(n.Name))
            .Select(g => new CategoryTotal(g.Key, g.Sum(n => n.Size), g.LongCount()))
            .OrderByDescending(t => t.Bytes).ToList();
}
