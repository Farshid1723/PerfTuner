using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfTuner.Platform.Windows.Maintenance;

public sealed record MovedPackage(string Name, long Size, string Sha256);

[JsonSerializable(typeof(List<MovedPackage>))]
internal sealed partial class MovedPackageJsonContext : JsonSerializerContext;

/// <summary>
/// The fingerprints of installer packages PerfTuner moved out of %WINDIR%\Installer, kept where only administrators
/// can write (%ProgramData%\PerfTuner-Undo\installer-packages.json). A package only goes back if its content still has
/// exactly that SHA-256, and it goes back as a fresh copy written by PerfTuner, so a file swapped or still open for
/// writing in the (usually user-writable) destination folder can never end up in the Windows Installer cache.
/// </summary>
public static class InstallerPackageLedger
{
    private static readonly Lock Gate = new();
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PerfTuner-Undo", "installer-packages.json");

    public static string Hash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    public static void Record(IEnumerable<MovedPackage> packages)
    {
        lock (Gate)
        {
            var list = Load();
            foreach (var p in packages)
            {
                list.RemoveAll(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                list.Add(p);
            }
            DirectoryHardening.CreateHardened(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, MovedPackageJsonContext.Default.ListMovedPackage));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    /// <summary>
    /// Copies <paramref name="movedPath"/> back into the installer cache under its original name if its content matches
    /// the recorded fingerprint, then deletes the moved copy. Returns null on success, else the reason.
    /// </summary>
    public static string? Restore(string movedPath, string originalName)
    {
        var name = Path.GetFileName(originalName);
        var ext = Path.GetExtension(name);
        if (name != originalName || !(ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msp", StringComparison.OrdinalIgnoreCase))) { return "not an installer package"; }
        MovedPackage? known;
        lock (Gate) { known = Load().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }
        if (known is null) { return "PerfTuner has no fingerprint for it"; }
        var target = Path.Combine(UpdateLeftovers.InstallerFolder, name);
        if (File.Exists(target)) { return "it is already back"; }
        var temp = Path.Combine(UpdateLeftovers.InstallerFolder, $"PerfTuner-restore-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var src = new FileStream(movedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 20];
                long total = 0;
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += n;
                    if (total > known.Size) { break; }
                    hash.AppendData(buffer, 0, n);
                    dst.Write(buffer, 0, n);
                }
                if (total != known.Size || !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(known.Sha256, StringComparison.Ordinal))
                {
                    return "its content changed since it was moved";
                }
            }
            File.Move(temp, target);
            File.Delete(movedPath);
            return null;
        }
        finally
        {
            if (File.Exists(temp)) { File.Delete(temp); }
        }
    }

    private static List<MovedPackage> Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path) || new FileInfo(path).Length > 4_000_000) { return []; }
            var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) { return []; }
            var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || !(owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid))) { return []; }
            return JsonSerializer.Deserialize(File.ReadAllText(path), MovedPackageJsonContext.Default.ListMovedPackage) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
