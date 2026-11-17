namespace PerfTuner.Core.Space;

[Flags]
public enum SpaceFlags
{
    None = 0,
    Hidden = 1,
    System = 2,

    /// <summary>A junction or symbolic link: shown, never followed (its target is counted where it really lives).</summary>
    ReparsePoint = 4,

    /// <summary>The folder couldn't be read (no access); its size is unknown.</summary>
    NoAccess = 8,

    /// <summary>"N small files" summary node (very large drives).</summary>
    Aggregate = 16,
}

/// <summary>
/// One file or folder of a scanned drive. Folders hold their children sorted by size (biggest first) after
/// <see cref="Recalculate"/>. The root's <see cref="Name"/> is the full root path (e.g. "C:\"); every other
/// node's name is a single path segment, so <see cref="FullPath"/> is built only from enumerated names.
/// </summary>
public sealed class SpaceNode
{
    private SpaceNode(string name, bool isFolder, long size, DateTime lastWriteUtc)
    {
        Name = name;
        IsFolder = isFolder;
        Size = size;
        LastWriteUtc = lastWriteUtc;
        Files = isFolder ? 0 : 1;
    }

    public string Name { get; }
    public bool IsFolder { get; }
    public long Size { get; private set; }
    public long Files { get; private set; }
    public long Folders { get; private set; }
    public DateTime LastWriteUtc { get; set; }
    public SpaceFlags Flags { get; set; }
    public SpaceNode? Parent { get; private set; }
    public List<SpaceNode>? Children { get; private set; }

    public static SpaceNode NewFile(string name, long size, DateTime lastWriteUtc) => new(name, false, size, lastWriteUtc);

    public static SpaceNode NewFolder(string name, DateTime lastWriteUtc = default) => new(name, true, 0, lastWriteUtc) { Children = [] };

    /// <summary>"N small files" stand-in for many tiny files of one folder.</summary>
    public static SpaceNode NewAggregate(long count, long size) =>
        new($"{count:N0} small files", false, size, default) { Flags = SpaceFlags.Aggregate, Files = count };

    public string FullPath
    {
        get
        {
            if (Parent is null) { return Name; }
            var parts = new Stack<string>();
            for (var n = this; n.Parent is not null; n = n.Parent) { parts.Push(n.Name); }
            var root = this;
            while (root.Parent is not null) { root = root.Parent; }
            return root.Name.TrimEnd('\\') + "\\" + string.Join('\\', parts);
        }
    }

    public int Depth
    {
        get
        {
            var d = 0;
            for (var n = Parent; n is not null; n = n.Parent) { d++; }
            return d;
        }
    }

    /// <summary>Adds a child (no totals update; call <see cref="Recalculate"/> on the subtree when done building).</summary>
    public void Add(SpaceNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (Children is null) { throw new InvalidOperationException("files have no children"); }
        child.Parent = this;
        Children.Add(child);
    }

    /// <summary>Adds a child whose subtree is already recalculated, updating every ancestor's totals and order.</summary>
    public void Attach(SpaceNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        Add(child);
        for (var a = this; a is not null; a = a.Parent)
        {
            a.Size += child.Size;
            a.Files += child.Files;
            a.Folders += child.IsFolder ? 1 + child.Folders : 0;
            a.Children!.Sort(static (x, y) => y.Size.CompareTo(x.Size));
        }
    }

    /// <summary>Recomputes sizes and counts of this subtree bottom-up and sorts children biggest first.</summary>
    public void Recalculate()
    {
        if (Children is null) { return; }
        long size = 0, files = 0, folders = 0;
        foreach (var c in Children)
        {
            c.Recalculate();
            size += c.Size;
            files += c.Files;
            folders += c.IsFolder ? 1 + c.Folders : 0;
        }
        Size = size;
        Files = files;
        Folders = folders;
        Children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
    }

    /// <summary>Detaches this node and subtracts it from every ancestor (after a delete or move).</summary>
    public void Remove()
    {
        if (Parent is not { } p) { return; }
        p.Children!.Remove(this);
        for (var a = p; a is not null; a = a.Parent)
        {
            a.Size -= Size;
            a.Files -= Files;
            a.Folders -= IsFolder ? 1 + Folders : 0;
        }
        Parent = null;
    }

    /// <summary>Replaces a child with a freshly scanned version (already recalculated) and fixes ancestor totals.</summary>
    public void ReplaceChild(SpaceNode old, SpaceNode fresh)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(fresh);
        var i = Children?.IndexOf(old) ?? -1;
        if (i < 0) { throw new ArgumentException("not a child", nameof(old)); }
        Children![i] = fresh;
        fresh.Parent = this;
        old.Parent = null;
        for (var a = this; a is not null; a = a.Parent)
        {
            a.Size += fresh.Size - old.Size;
            a.Files += fresh.Files - old.Files;
            a.Folders += (fresh.IsFolder ? 1 + fresh.Folders : 0) - (old.IsFolder ? 1 + old.Folders : 0);
            a.Children!.Sort(static (x, y) => y.Size.CompareTo(x.Size));
        }
    }

    /// <summary>All nodes below this one (depth-first), not including this node.</summary>
    public IEnumerable<SpaceNode> Descendants()
    {
        if (Children is null) { yield break; }
        var stack = new Stack<SpaceNode>(Children);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            if (n.Children is { } cs) { foreach (var c in cs) { stack.Push(c); } }
        }
    }

    public override string ToString() => $"{Name} ({Size:N0} B)";
}
