using System.Globalization;
using System.Text;

namespace PerfTuner.Core.Setup;

/// <summary>One file of a release payload: its path relative to the app folder, its size and SHA-256 (hex).</summary>
public sealed record PayloadEntry(string Path, long Size, string Sha256);

/// <summary>
/// The list of files a release consists of ("payload.manifest" inside the portable/setup package, and
/// "install.manifest" in an installed copy). One line per file: <c>sha256 size relative\path</c>.
/// Paths are strictly validated: relative, backslash-separated, no "." or ".." segments, no drive, no stream (":"),
/// no characters Windows forbids, no trailing dot/space. So a tampered package can never write outside its folder.
/// </summary>
public static class PayloadManifest
{
    public const string FileName = "payload.manifest";
    private static readonly char[] Forbidden = ['<', '>', ':', '"', '/', '|', '?', '*'];

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path[0] == '\\') { return false; }
        foreach (var segment in path.Split('\\'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(Forbidden) >= 0
                || segment.Any(c => c < 32) || segment.EndsWith('.') || segment.EndsWith(' ') || segment.StartsWith(' '))
            {
                return false;
            }
        }
        return true;
    }

    public static string Format(IEnumerable<PayloadEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sb = new StringBuilder();
        foreach (var e in entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsSafeRelativePath(e.Path)) { throw new ArgumentException($"Unsafe path in manifest: {e.Path}", nameof(entries)); }
            sb.Append(e.Sha256.ToLowerInvariant()).Append(' ').Append(e.Size.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(e.Path).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Parses a manifest. Throws <see cref="FormatException"/> on any malformed line, unsafe path or duplicate.</summary>
    public static IReadOnlyList<PayloadEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var list = new List<PayloadEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { continue; }
            var first = line.IndexOf(' ', StringComparison.Ordinal);
            var second = first < 0 ? -1 : line.IndexOf(' ', first + 1);
            if (first != 64 || second < 0) { throw new FormatException("Malformed manifest line."); }
            var hash = line[..first];
            if (!hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) { throw new FormatException("Malformed hash."); }
            if (!long.TryParse(line.AsSpan(first + 1, second - first - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var size)) { throw new FormatException("Malformed size."); }
            var path = line[(second + 1)..];
            if (!IsSafeRelativePath(path)) { throw new FormatException($"Unsafe path: {path}"); }
            if (!seen.Add(path)) { throw new FormatException($"Duplicate path: {path}"); }
            list.Add(new PayloadEntry(path, size, hash));
        }
        if (list.Count == 0) { throw new FormatException("Empty manifest."); }
        return list;
    }

    /// <summary>Files listed in <paramref name="previous"/> that the new release no longer has (removed on upgrade).</summary>
    public static IReadOnlyList<string> Stale(IEnumerable<PayloadEntry> previous, IEnumerable<PayloadEntry> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var now = current.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return previous.Select(e => e.Path).Where(p => !now.Contains(p)).ToList();
    }
}
