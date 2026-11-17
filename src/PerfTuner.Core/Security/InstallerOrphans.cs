namespace PerfTuner.Core.Security;

/// <summary>A file in the Windows Installer cache.</summary>
public sealed record InstallerFile(string Path, long Size, DateTime LastWriteUtc);

/// <summary>
/// Orphaned installer packages: .msi/.msp files directly in %WINDIR%\Installer that no
/// installed product or patch references any more. Windows Installer itself lists what it still needs; anything else
/// is left over. Safety: if Windows lists nothing at all, nobody can tell what is orphaned, so nothing is.
/// </summary>
public static class InstallerOrphans
{
    public static readonly TimeSpan MinimumAge = TimeSpan.FromDays(30);

    public static IReadOnlyList<InstallerFile>? Find(IEnumerable<InstallerFile> files, IEnumerable<string> referenced, DateTime nowUtc, string installerFolder = @"C:\Windows\Installer")
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(referenced);
        var refs = referenced.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (refs.Count == 0) { return null; }
        var folder = Normalize(installerFolder);
        return files.Where(f =>
        {
            var p = Normalize(f.Path);
            var slash = p.LastIndexOf('\\');
            var dir = slash < 0 ? "" : p[..slash];
            var name = p[(slash + 1)..];
            var dot = name.LastIndexOf('.');
            var ext = dot < 0 ? "" : name[dot..];
            return string.Equals(dir, folder, StringComparison.OrdinalIgnoreCase)
                && (ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msp", StringComparison.OrdinalIgnoreCase))
                && !refs.Contains(p)
                && nowUtc - f.LastWriteUtc >= MinimumAge;
        }).OrderByDescending(f => f.Size).ToList();
    }

    private static string Normalize(string p) => p.Replace('/', '\\').TrimEnd('\\');
}
