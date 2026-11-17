using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Text;
using System.Reflection;
using System.Security.Cryptography;
using PerfTuner.Core.Security;
using PerfTuner.Core.Setup;

// Never resolve a native library from the exe's own folder (usually Downloads): System32 only.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace PerfTuner.Bootstrap;

/// <summary>
/// PerfTuner-Portable.exe / PerfTuner-Setup.exe / PerfTuner-Uninstall.exe — one launcher, three roles, decided by
/// what is appended to the exe (never by the command line):
/// <list type="bullet">
/// <item>Portable: unpacks the app once per version into %ProgramFiles%\PerfTuner-Portable\&lt;version&gt; (only
///   administrators can create or change anything there), checks every file against its SHA-256 on every start, then
///   starts PerfTuner.</item>
/// <item>Setup: unpacks into %ProgramFiles%\PerfTuner-Setup\&lt;version&gt;, checks every file, and runs the app's own
///   Setup wizard from there (<c>PerfTuner.exe --setup</c>), which installs to %ProgramFiles%\PerfTuner. <c>/S</c> = silent.</item>
/// <item>Uninstall: copies itself out of the install folder, lets the installed app undo its changes and unregister
///   (<c>PerfTuner.exe --uninstall</c>), then deletes the program files listed in install.manifest. <c>/S</c> = silent.</item>
/// </list>
/// </summary>
internal static class Program
{
    private const string AppExe = "PerfTuner.exe";
    private const string UninstallerName = "PerfTuner-Uninstall.exe";

    private static string Version => Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    [STAThread]
    private static int Main(string[] args)
    {
        Native.RestrictDllSearchToSystem32();
        var silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("/silent", StringComparison.OrdinalIgnoreCase));
        try
        {
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown program path.");
            var payload = Payload.Open(self);
            return payload.Mode switch
            {
                Payload.Portable => RunPortable(payload, args),
                Payload.Setup => RunSetup(payload, silent),
                _ => RunUninstall(self, args, silent),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!silent) { Native.Message("PerfTuner", "PerfTuner couldn't start:\n\n" + ex.Message, error: true); }
            return 2;
        }
    }

    // ------------------------------------------------------------------ portable

    private static int RunPortable(Payload payload, string[] args)
    {
        var root = Path.Combine(ProgramFiles, "PerfTuner-Portable");
        var dir = Path.Combine(root, Version);
        Folders.EnsureAdminOnly(root);
        var manifest = payload.ReadManifest();
        using var gate = Folders.Lock(root);
        if (!Folders.IsAdminOwned(dir) || !Unpacker.Verify(dir, manifest))
        {
            Progress.Run("PerfTuner", "Getting PerfTuner ready…", "First start of this version: unpacking and checking every file. This takes a few seconds.",
                () => Unpacker.Unpack(payload, manifest, root, dir));
        }
        var start = new ProcessStartInfo(Path.Combine(dir, AppExe)) { UseShellExecute = false, WorkingDirectory = dir };
        foreach (var a in args) { start.ArgumentList.Add(a); }
        using var _ = Process.Start(start);
        Folders.TryDeleteOtherVersions(root, Version);
        return 0;
    }

    // ------------------------------------------------------------------ setup

    private static int RunSetup(Payload payload, bool silent)
    {
        var root = Path.Combine(ProgramFiles, "PerfTuner-Setup");
        var dir = Path.Combine(root, Version);
        Folders.EnsureAdminOnly(root);
        var manifest = payload.ReadManifest();
        void Prepare()
        {
            Unpacker.Unpack(payload, manifest, root, dir);
            payload.WriteStub(Path.Combine(dir, UninstallerName));
        }
        if (silent) { Prepare(); } else { Progress.Run("PerfTuner Setup", "Preparing PerfTuner Setup…", "Unpacking and checking every file.", Prepare); }

        var start = new ProcessStartInfo(Path.Combine(dir, AppExe)) { UseShellExecute = false, WorkingDirectory = dir };
        start.ArgumentList.Add("--setup");
        if (silent) { start.ArgumentList.Add("--silent"); }
        int code;
        using (var p = Process.Start(start) ?? throw new InvalidOperationException("Setup didn't start."))
        {
            p.WaitForExit();
            code = p.ExitCode;
        }
        Folders.TryDeleteTree(dir);
        return code;
    }

    // ------------------------------------------------------------------ uninstall

    private static int RunUninstall(string self, string[] args, bool silent)
    {
        var installRoot = Path.Combine(ProgramFiles, "PerfTuner");
        var workRoot = Path.Combine(ProgramFiles, "PerfTuner-Setup");
        var workDir = Path.Combine(workRoot, "Uninstall");
        var runIndex = Array.IndexOf(args, "--run");
        if (runIndex < 0)
        {
            // Stage 1 (from the install folder): a program can't delete itself, so continue from an admin-only copy.
            Folders.EnsureAdminOnly(workRoot);
            Folders.TryDeleteTree(workDir);
            Directory.CreateDirectory(workDir);
            var copy = Path.Combine(workDir, UninstallerName);
            File.Copy(self, copy, overwrite: true);
            var start = new ProcessStartInfo(copy) { UseShellExecute = false, WorkingDirectory = workDir };
            start.ArgumentList.Add("--run");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (silent) { start.ArgumentList.Add("/S"); }
            using var _ = Process.Start(start);
            return 0;
        }

        // Stage 2: wait for stage 1 to exit (it lives in the install folder), then remove.
        if (runIndex + 1 < args.Length && int.TryParse(args[runIndex + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parent))
        {
            try { using var p = Process.GetProcessById(parent); p.WaitForExit(15_000); }
            catch (ArgumentException) { /* already gone */ }
        }
        var app = Path.Combine(installRoot, AppExe);
        if (!File.Exists(app))
        {
            // Without the app nothing can undo PerfTuner's changes or remove its service: don't half-uninstall.
            if (!silent) { Native.Message("Uninstall PerfTuner", $"{app} is missing, so PerfTuner can't undo its changes. Run PerfTuner-Setup.exe to repair the installation, then uninstall again.", error: true); }
            Native.ScheduleDeleteOnRestart(Environment.ProcessPath!);
            return 2;
        }
        {
            var start = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = installRoot };
            start.ArgumentList.Add("--uninstall");
            if (silent) { start.ArgumentList.Add("--silent"); }
            using var p = Process.Start(start) ?? throw new InvalidOperationException("The uninstall step didn't start.");
            p.WaitForExit();
            if (p.ExitCode != 0) { Native.ScheduleDeleteOnRestart(Environment.ProcessPath!); return p.ExitCode; }
        }
        var leftovers = Unpacker.RemoveInstalled(installRoot);
        Native.ScheduleDeleteOnRestart(Environment.ProcessPath!);
        Native.ScheduleDeleteOnRestart(workDir);
        Native.ScheduleDeleteOnRestart(workRoot);
        if (!silent)
        {
            Native.Message("Uninstall PerfTuner", leftovers == 0
                ? "PerfTuner was removed from this PC."
                : $"PerfTuner was removed. {leftovers} file(s) it didn't install were left in {installRoot}.", error: false);
        }
        return 0;
    }
}

/// <summary>
/// The payload appended to the exe: [Brotli(manifest length, manifest, distinct file contents)][length: 8 bytes][mode: 1 byte]["PTPAYLD2"]; the uninstaller has mode U and
/// length 0. The exe is opened once, without delete/write sharing (so it can't be renamed or replaced while we read),
/// and only after checking that the opened file is the very file Windows started (the image UAC approved). Everything
/// (file list, hashes, content) is read through that one handle.
/// </summary>
internal sealed class Payload
{
    public const byte Portable = (byte)'P';
    public const byte Setup = (byte)'S';
    public const byte Uninstaller = (byte)'U';
    private static readonly byte[] Magic = "PTPAYLD2"u8.ToArray();
    private const int FooterSize = 17;

    private readonly FileStream _file;
    public long Offset { get; }
    public long Length { get; }
    public byte Mode { get; }

    private Payload(FileStream file, long offset, long length, byte mode) => (_file, Offset, Length, Mode) = (file, offset, length, mode);

    public static Payload Open(string exe)
    {
        var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Native.IsRunningImage(fs.SafeFileHandle))
        {
            fs.Dispose();
            throw new InvalidDataException("The program file was replaced after it started. Download PerfTuner again.");
        }
        var damaged = new InvalidDataException("The package is damaged or incomplete. Download it again.");
        if (fs.Length < FooterSize) { throw damaged; }
        fs.Seek(-FooterSize, SeekOrigin.End);
        Span<byte> footer = stackalloc byte[FooterSize];
        fs.ReadExactly(footer);
        if (!footer[9..].SequenceEqual(Magic)) { throw damaged; }
        var length = BitConverter.ToInt64(footer[..8]);
        var offset = fs.Length - FooterSize - length;
        var mode = footer[8];
        var valid = mode == Uninstaller ? length == 0 : mode is Portable or Setup && length > 0 && offset > 0;
        if (!valid) { throw damaged; }
        return new Payload(fs, offset, length, mode);
    }

    /// <summary>The decompressed payload, positioned after the manifest (whose bytes must match <see cref="ReadManifest"/>).</summary>
    public Stream OpenContent()
    {
        var stream = new BrotliStream(new Window(_file, Offset, Length), CompressionMode.Decompress);
        _ = ReadManifestFrom(stream);
        return stream;
    }

    public IReadOnlyList<PayloadEntry> ReadManifest()
    {
        using var stream = new BrotliStream(new Window(_file, Offset, Length), CompressionMode.Decompress);
        return PayloadManifest.Parse(ReadManifestFrom(stream));
    }

    private static string ReadManifestFrom(Stream stream)
    {
        Span<byte> len = stackalloc byte[4];
        stream.ReadExactly(len);
        var length = BitConverter.ToInt32(len);
        if (length is <= 0 or > 4_000_000) { throw new InvalidDataException("The package's file list is damaged. Download it again."); }
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>The launcher without its payload, marked as the uninstaller.</summary>
    public void WriteStub(string target)
    {
        using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[1 << 20];
        _file.Position = 0;
        var left = Offset;
        while (left > 0)
        {
            var n = _file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0) { throw new EndOfStreamException(); }
            dst.Write(buffer, 0, n);
            left -= n;
        }
        dst.Write(BitConverter.GetBytes(0L));
        dst.WriteByte(Uninstaller);
        dst.Write(Magic);
    }

    /// <summary>A read-only window onto part of the (shared, never disposed here) exe stream.</summary>
    private sealed class Window(FileStream inner, long start, long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, length); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            if (n <= 0) { return 0; }
            inner.Position = start + _position;
            n = inner.Read(buffer, offset, n);
            _position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal static class Unpacker
{
    /// <summary>Every listed file exists with the right size and SHA-256.</summary>
    public static bool Verify(string dir, IReadOnlyList<PayloadEntry> manifest)
    {
        if (!Directory.Exists(dir)) { return false; }
        var ok = true;
        Parallel.ForEach(manifest, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) }, (e, state) =>
        {
            if (!Check(dir, e)) { ok = false; state.Stop(); }
        });
        return ok;
    }

    private static bool Check(string dir, PayloadEntry e)
    {
        if (!PathGuard.TryResolveUnderRoot(dir, e.Path, out var path)) { return false; }
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != e.Size || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { return false; }
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexStringLower(SHA256.HashData(fs)).Equals(e.Sha256, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Unpacks into a fresh folder next to <paramref name="dir"/>, checks every file, then swaps it in. Files are taken
    /// from the payload stream in manifest order; a file whose content already came earlier is copied from that file.
    /// </summary>
    public static void Unpack(Payload payload, IReadOnlyList<PayloadEntry> manifest, string root, string dir)
    {
        var temp = dir + ".new-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Folders.TryDeleteTree(temp);
        Directory.CreateDirectory(temp); // inherits the admin-only rules of root
        using (var input = payload.OpenContent())
        {
            var written = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in manifest)
            {
                if (!PathGuard.TryResolveUnderRoot(temp, e.Path, out var target)) { throw new InvalidDataException($"The package lists an unsafe path ({e.Path})."); }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (written.TryGetValue(e.Sha256, out var same)) { File.Copy(same, target); continue; }
                WriteFile(input, target, e);
                written[e.Sha256] = target;
            }
            if (input.ReadByte() != -1) { throw new InvalidDataException("The package has unexpected extra data. Download it again."); }
        }
        File.WriteAllText(Path.Combine(temp, PayloadManifest.FileName), PayloadManifest.Format(manifest));
        if (!Verify(temp, manifest)) { throw new InvalidDataException("Unpacked files didn't pass the checksum test."); }
        Folders.TryDeleteTree(dir);
        if (Directory.Exists(dir)) { throw new IOException($"The old copy in {dir} is in use. Close PerfTuner and try again."); }
        Directory.Move(temp, dir);
    }

    private static void WriteFile(Stream input, string target, PayloadEntry expected)
    {
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        var left = expected.Size;
        while (left > 0)
        {
            var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0) { throw new InvalidDataException("The package is incomplete. Download it again."); }
            hash.AppendData(buffer, 0, n);
            output.Write(buffer, 0, n);
            left -= n;
        }
        if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(expected.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{expected.Path} failed the checksum test. Download the package again.");
        }
    }

    /// <summary>Deletes the files Setup installed (install.manifest), then empty folders. Returns how many unknown files stayed.</summary>
    public static int RemoveInstalled(string root)
    {
        if (!Directory.Exists(root) || new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint)) { return 0; }
        var manifestPath = Path.Combine(root, "install.manifest");
        if (File.Exists(manifestPath))
        {
            IReadOnlyList<PayloadEntry> entries;
            try { entries = PayloadManifest.Parse(File.ReadAllText(manifestPath)); }
            catch (FormatException) { entries = []; }
            foreach (var e in entries)
            {
                if (PathGuard.TryResolveUnderRoot(root, e.Path, out var path) && File.Exists(path) && !Folders.HasLinkOnPath(root, path))
                {
                    try { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Native.ScheduleDeleteOnRestart(path); }
                }
            }
            File.Delete(manifestPath);
        }
        foreach (var d in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(d).Any()) { Directory.Delete(d); }
        }
        if (!Directory.EnumerateFileSystemEntries(root).Any()) { Directory.Delete(root); return 0; }
        return Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Count();
    }
}

internal static class Folders
{
    /// <summary>
    /// The unpack roots live under %ProgramFiles%, where only administrators can create folders, so nobody else can
    /// prepare (or swap in) a folder or link there. Still refuse a link or a folder not owned by Administrators/SYSTEM.
    /// </summary>
    public static void EnsureAdminOnly(string path)
    {
        Directory.CreateDirectory(path);
        if (!IsAdminOwned(path)) { throw new IOException($"{path} isn't owned by Administrators; refusing to use it."); }
    }

    /// <summary>One unpack at a time: an exclusive lock file inside the admin-only root (waits up to 3 minutes).</summary>
    public static FileStream Lock(string root)
    {
        var deadline = Environment.TickCount64 + 180_000;
        while (true)
        {
            try { return new FileStream(Path.Combine(root, ".unpack.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(250); }
        }
    }

    public static bool IsAdminOwned(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { return false; }
        var owner = info.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
        return owner is not null && (owner.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid));
    }

    /// <summary>Deletes a folder tree without ever following a link (a link is removed, not its target).</summary>
    public static void TryDeleteTree(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) { return; }
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { info.Delete(); return; }
            foreach (var f in info.EnumerateFiles())
            {
                f.Attributes = FileAttributes.Normal;
                f.Delete();
            }
            foreach (var d in info.EnumerateDirectories()) { TryDeleteTree(d.FullName); }
            info.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // in use: left for next time
        }
    }

    private static bool InUse(string exe)
    {
        if (!File.Exists(exe)) { return false; }
        try
        {
            using var _ = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static bool HasLinkOnPath(string root, string path)
    {
        for (var d = Path.GetDirectoryName(path); d is not null && d.Length > root.Length; d = Path.GetDirectoryName(d))
        {
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) { return true; }
        }
        return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>Older unpacked versions are removed when no longer running.</summary>
    public static void TryDeleteOtherVersions(string root, string keep)
    {
        foreach (var d in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(d);
            if (name.Equals(keep, StringComparison.OrdinalIgnoreCase)) { continue; }
            if (name.Contains(".new-", StringComparison.Ordinal) && DateTime.UtcNow - Directory.GetCreationTimeUtc(d) < TimeSpan.FromHours(1)) { continue; } // another launch may be unpacking
            if (InUse(Path.Combine(d, "PerfTuner.exe")) || InUse(Path.Combine(d, "Service", "PerfTuner.Service.exe"))) { continue; }
            TryDeleteTree(d);
        }
    }
}
