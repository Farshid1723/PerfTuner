namespace PerfTuner.Core.Undocumented;

/// <summary>
/// Explorer keeps its virtual desktops in the registry: an ordered list of 16-byte ids and the
/// id of the current one. This reads that layout and works out how many Win+Ctrl+←/→ steps
/// reach a desktop. Pure, so it's unit-tested on any OS.
/// </summary>
public static class VirtualDesktopState
{
    public const int MaxDesktops = 64;

    /// <summary>The ordered desktop ids from Explorer's VirtualDesktopIDs value (empty if malformed).</summary>
    public static IReadOnlyList<Guid> ParseIds(byte[]? blob)
    {
        if (blob is null || blob.Length == 0 || blob.Length % 16 != 0 || blob.Length / 16 > MaxDesktops) { return []; }
        var list = new Guid[blob.Length / 16];
        for (var i = 0; i < list.Length; i++) { list[i] = new Guid(blob.AsSpan(i * 16, 16)); }
        return list;
    }

    public static Guid? ParseId(byte[]? blob) => blob is { Length: 16 } ? new Guid(blob) : null;

    /// <summary>Zero-based position of the current desktop, or −1.</summary>
    public static int IndexOf(IReadOnlyList<Guid> ids, Guid? current)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (current is not { } c) { return ids.Count == 1 ? 0 : -1; }
        for (var i = 0; i < ids.Count; i++) { if (ids[i] == c) { return i; } }
        return -1;
    }

    /// <summary>Signed number of steps (positive = right) from one desktop to another; 0 if either is unknown.</summary>
    public static int Steps(int from, int to, int count)
    {
        if (count <= 0 || from < 0 || from >= count || to < 0 || to >= count) { return 0; }
        return to - from;
    }

    /// <summary>"Desktop 2" when the user never named it.</summary>
    public static string DisplayName(int index, string? name) =>
        string.IsNullOrWhiteSpace(name) ? $"Desktop {index + 1}" : name.Trim();
}

/// <summary>Remembers the desktop before the current one, by id, so "back" survives reordering and removal.</summary>
public sealed class DesktopHistory
{
    private Guid? _current;

    public Guid? Previous { get; private set; }

    /// <summary>Call whenever the current desktop is read; ignores repeats.</summary>
    public void Visit(Guid? id)
    {
        if (id is null || id == _current) { return; }
        if (_current is not null) { Previous = _current; }
        _current = id;
    }

    /// <summary>Forget a previous desktop that no longer exists.</summary>
    public void Prune(IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (Previous is { } p && !ids.Contains(p)) { Previous = null; }
    }
}
