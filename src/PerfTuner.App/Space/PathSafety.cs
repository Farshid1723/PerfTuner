using System.IO;
using Microsoft.Win32;
using PerfTuner.Core.Space;

namespace PerfTuner.App.Space;

/// <summary>
/// Checks shared by the Space explorer and History's "move back" (design §4): exact (canonical) paths only, local
/// fixed drives only, no junction or symbolic link anywhere on the path, and the machine's protected locations.
/// </summary>
public static class PathSafety
{
    /// <summary>The protected locations of this PC, including every user profile listed by Windows.</summary>
    public static ProtectionContext Context()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var profile = F(Environment.SpecialFolder.UserProfile);
        var usersRoot = Path.GetDirectoryName(profile) ?? @"C:\Users";
        var others = new List<string>();
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (list?.GetValue("ProfilesDirectory") is string dir) { usersRoot = Environment.ExpandEnvironmentVariables(dir); }
            foreach (var sid in list?.GetSubKeyNames() ?? [])
            {
                using var k = list!.OpenSubKey(sid);
                if (k?.GetValue("ProfileImagePath") is string p)
                {
                    var full = Environment.ExpandEnvironmentVariables(p);
                    if (!string.Equals(full.TrimEnd('\\'), profile.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { others.Add(full); }
                }
            }
            if (list?.GetValue("Public") is string pub) { others.Add(Environment.ExpandEnvironmentVariables(pub)); }
            if (list?.GetValue("Default") is string def) { others.Add(Environment.ExpandEnvironmentVariables(def)); }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall back to the profile's parent folder; other users' folders under it stay locked by the rules.
        }
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');
        return new ProtectionContext(
            F(Environment.SpecialFolder.Windows),
            new[] { F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0).ToList(),
            F(Environment.SpecialFolder.CommonApplicationData),
            usersRoot,
            profile,
            [appDir, Path.Combine(F(Environment.SpecialFolder.LocalApplicationData), "PerfTuner"), Path.Combine(F(Environment.SpecialFolder.CommonApplicationData), "PerfTuner-Undo")],
            others);
    }

    /// <summary>The path is fully qualified and Windows reads it exactly as written (no trailing dots/spaces, no "..").</summary>
    public static bool IsCanonical(string path) =>
        Path.IsPathFullyQualified(path) && path.Length >= 3 && path[1] == ':' && !path.StartsWith(@"\\", StringComparison.Ordinal)
        && string.Equals(Path.GetFullPath(path).TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.Ordinal);

    /// <summary>On a local fixed drive.</summary>
    public static bool IsLocalFixed(string path)
    {
        try { return path.Length >= 3 && new DriveInfo(path[..3]).DriveType == DriveType.Fixed; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>True when the item or any folder above it (below the drive root) is a junction or symbolic link.</summary>
    public static bool HasLinkOnPath(string path)
    {
        var current = path.TrimEnd('\\');
        while (current.Length > 3)
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) { return true; }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Missing parts are judged by the caller (exists checks); keep walking up.
            }
            current = Path.GetDirectoryName(current) ?? "";
        }
        return false;
    }

    /// <summary>All checks for an item about to be deleted or moved. Null = safe; otherwise a plain reason.</summary>
    public static string? CheckSource(string path, bool isFolder, ProtectionContext ctx)
    {
        if (!IsCanonical(path)) { return "its path is unusual (Windows would read it differently)"; }
        if (!IsLocalFixed(path)) { return "it isn't on a local drive"; }
        var p = ProtectionRules.Check(path, isFolder, false, ctx);
        if (p.Level == ProtectionLevel.Locked) { return p.Reason; }
        if (HasLinkOnPath(path)) { return "it is (or is inside) a link to another place"; }
        return null;
    }

    /// <summary>All checks for a destination folder. Null = safe.</summary>
    public static string? CheckDestination(string folder, ProtectionContext ctx)
    {
        if (!IsCanonical(folder)) { return "its path is unusual"; }
        if (!IsLocalFixed(folder)) { return "it isn't on a local drive"; }
        if (!Directory.Exists(folder)) { return "it no longer exists"; }
        if (ProtectionRules.Check(Path.Combine(folder, "x"), false, false, ctx) is { Level: ProtectionLevel.Locked } p) { return p.Reason; }
        if (HasLinkOnPath(folder)) { return "it is (or is inside) a link to another place"; }
        return null;
    }
}
