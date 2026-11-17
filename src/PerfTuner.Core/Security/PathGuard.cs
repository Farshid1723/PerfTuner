namespace PerfTuner.Core.Security;

/// <summary>
/// Path-traversal defence. The service never accepts paths over IPC; this guard
/// protects paths derived from configuration or environment so they cannot escape
/// the product's data root (via "..", absolute overrides, or alternate data streams).
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// Combines <paramref name="root"/> and a relative <paramref name="child"/> and returns
    /// the full path only if it stays strictly inside root.
    /// </summary>
    public static bool TryResolveUnderRoot(string root, string child, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(child))
        {
            return false;
        }
        // Rooted children (C:\x, \\server\x, /x) and ADS / drive-relative forms are refused outright.
        if (Path.IsPathRooted(child) || child.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, child));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }
}
