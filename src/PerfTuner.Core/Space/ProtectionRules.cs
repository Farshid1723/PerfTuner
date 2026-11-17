using System.Text.RegularExpressions;

namespace PerfTuner.Core.Space;

public enum ProtectionLevel
{
    Allowed,

    /// <summary>Allowed, but the confirmation shows an extra warning (e.g. app data).</summary>
    Warn,

    /// <summary>Never deleted or moved by PerfTuner.</summary>
    Locked,
}

public sealed record ProtectionResult(ProtectionLevel Level, string Reason)
{
    public static readonly ProtectionResult Ok = new(ProtectionLevel.Allowed, "");
}

/// <summary>The machine's well-known locations (from Environment.GetFolderPath at runtime; fixed values in tests).</summary>
public sealed record ProtectionContext(
    string WindowsDir,
    IReadOnlyList<string> ProgramFiles,
    string ProgramData,
    string UsersRoot,
    string UserProfile,
    IReadOnlyList<string> ExtraLocked,
    IReadOnlyList<string>? OtherProfiles = null);

/// <summary>
/// Which scanned items may be deleted or moved (design §4.3). Pure and unit-tested; case-insensitive like Windows.
/// Anything that looks like path tricks (".." segments, 8.3 short names) is locked rather than interpreted.
/// </summary>
public static partial class ProtectionRules
{
    private static readonly string[] RootFolders = ["System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "$WinREAgent", "Config.Msi", "Documents and Settings",
        "Program Files", "Program Files (x86)", "WindowsApps", "WpSystem", "ProgramData", "Windows"];
    private static readonly string[] ReservedNames = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$"];
    private static readonly string[] AppDataLocked = [@"Roaming\Microsoft", @"Local\Microsoft", @"Local\Packages", @"LocalLow\Microsoft"];
    private static readonly string[] RootFiles = ["pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT"];
    private static readonly string[] ProfileShellFolders = ["Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "OneDrive", "Favorites", "Contacts", "Links", "Saved Games", "Searches", "3D Objects", "AppData"];
    private static readonly string[] AppDataRoots = ["Local", "LocalLow", "Roaming"];

    [GeneratedRegex(@"^[^\\]{1,6}~\d+(\.[^\\]{0,3})?$")]
    private static partial Regex ShortName();

    public static ProtectionResult Check(string path, bool isFolder, bool isSystem, ProtectionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(ctx);
        // Anything Windows would silently rewrite (\\?\ or UNC prefixes, names ending in a dot or space, device names,
        // "..", 8.3 short names) is locked instead of interpreted: the text checked must be exactly what gets acted on.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains('/', StringComparison.Ordinal) || path.Length < 3 || path[1] != ':')
        {
            return Locked("This path can't be checked safely.");
        }
        var p = Normalize(path);
        var segments = p.Split('\\');
        if (segments.Any(s => s is "." or "..")
            || segments.Skip(1).Any(s => s.Length == 0 || ShortName().IsMatch(s) || s.EndsWith('.') || s.EndsWith(' ')
                || ReservedNames.Any(r => s.Equals(r, StringComparison.OrdinalIgnoreCase) || s.StartsWith(r + ".", StringComparison.OrdinalIgnoreCase))))
        {
            return Locked("This name is unusual (Windows would read it differently), so PerfTuner leaves it alone.");
        }
        if (segments.Length < 2 || p.Length <= 3) { return Locked("This is the whole drive."); }
        if (isSystem) { return Locked("Windows marked this as a system file."); }

        if (IsInside(p, ctx.WindowsDir)) { return Locked("Windows itself lives here."); }
        if (ctx.ProgramFiles.Any(pf => IsInside(p, pf))) { return Locked("Installed programs live here; uninstall them in Windows Settings instead."); }
        if (IsInside(p, ctx.ProgramData)) { return Locked("Programs keep shared data here."); }
        if (ctx.ExtraLocked.Any(x => IsInside(p, x))) { return Locked("PerfTuner's own files."); }

        if (RootFolders.Any(r => r.Equals(segments[1], StringComparison.OrdinalIgnoreCase))) { return Locked("Windows needs this folder."); }
        if (segments.Length == 2)
        {
            var name = segments[1];
            if (!isFolder && (RootFiles.Any(r => r.Equals(name, StringComparison.OrdinalIgnoreCase)) || name.StartsWith("DumpStack.log", StringComparison.OrdinalIgnoreCase)))
            {
                return Locked("Windows manages this file (page file, hibernation or boot).");
            }
        }

        if (ctx.OtherProfiles?.Any(o => IsInside(p, o)) == true) { return Locked("This belongs to another user account."); }
        var users = Normalize(ctx.UsersRoot);
        // A "Users" folder at the root of a drive that isn't this PC's profile location (another Windows installation).
        if (segments.Length >= 2 && segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase) && !IsInside(p, users))
        {
            return Locked("User profiles live here.");
        }
        if (IsInside(p, users))
        {
            if (Same(p, users)) { return Locked("This folder holds every user's files."); }
            var profile = Normalize(ctx.UserProfile);
            if (!IsInside(p, profile)) { return Locked("This belongs to another user account (or to all users)."); }
            if (Same(p, profile)) { return Locked("This is your whole user folder."); }
            var rest = p[(profile.Length + 1)..].Split('\\');
            if (rest.Length == 1 && isFolder && ProfileShellFolders.Any(f => f.Equals(rest[0], StringComparison.OrdinalIgnoreCase)))
            {
                return Locked("This is one of your main folders; pick what's inside it instead.");
            }
            if (rest[0].Equals("AppData", StringComparison.OrdinalIgnoreCase))
            {
                if (rest.Length == 2 && AppDataRoots.Any(r => r.Equals(rest[1], StringComparison.OrdinalIgnoreCase)))
                {
                    return Locked("Apps keep all their settings here.");
                }
                var underAppData = string.Join('\\', rest.Skip(1));
                if (AppDataLocked.Any(l => underAppData.Equals(l, StringComparison.OrdinalIgnoreCase) || underAppData.StartsWith(l + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    return Locked("Windows keeps keys, credentials and Store apps here.");
                }
                return new ProtectionResult(ProtectionLevel.Warn, "Apps keep data here. Removing it can reset an app or sign you out; caches are usually safe.");
            }
        }
        return ProtectionResult.Ok;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it (case-insensitive, whole segments).</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        return Same(p, f) || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase) || (f.EndsWith(':') && p.StartsWith(f, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');

    private static ProtectionResult Locked(string reason) => new(ProtectionLevel.Locked, reason);
}
