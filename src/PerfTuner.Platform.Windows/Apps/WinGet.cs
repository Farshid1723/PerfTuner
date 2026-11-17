using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PerfTuner.Core.Apps;

namespace PerfTuner.Platform.Windows.Apps;

/// <summary>
/// Installs, updates and removes apps from the built-in <see cref="AppCatalog"/> with Microsoft's Windows Package
/// Manager (winget.exe, part of Windows' App Installer). This is the one place PerfTuner starts another program:
/// <list type="bullet">
/// <item>the program is winget.exe inside the App Installer package folder under %ProgramFiles%\WindowsApps (found
///   through the documented package API, never the per-user alias in a user-writable folder);</item>
/// <item>no shell: the argument list is built here from fixed words plus a package identifier that must be in the
///   catalog and pass <see cref="AppCatalog.IsValidId"/>; nothing typed by the user reaches it.</item>
/// </list>
/// Packages come from Microsoft's community repository ("winget" source); each app's licence is its publisher's.
/// </summary>
public static partial class WinGet
{
    private const string Family = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";

    /// <summary>Full path of winget.exe in the installed App Installer package, or null if App Installer is missing.</summary>
    public static unsafe string? Locate()
    {
        uint count = 0, length = 0;
        var rc = GetPackagesByPackageFamily(Family, ref count, null, ref length, null);
        if (rc != ErrorInsufficientBuffer || count == 0) { return null; }
        var names = new nint[count];
        var buffer = new char[length];
        fixed (nint* pNames = names)
        fixed (char* pBuffer = buffer)
        {
            if (GetPackagesByPackageFamily(Family, ref count, pNames, ref length, pBuffer) != 0) { return null; }
            var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\";
            string? best = null;
            Version? bestVersion = null;
            for (var i = 0; i < count; i++)
            {
                var fullName = new string((char*)names[i]);
                var path = PackagePath(fullName);
                if (path is null || !path.StartsWith(windowsApps, StringComparison.OrdinalIgnoreCase)) { continue; }
                var exe = Path.Combine(path, "winget.exe");
                var parts = fullName.Split('_');
                if (!File.Exists(exe) || parts.Length < 2 || !Version.TryParse(parts[1], out var v)) { continue; }
                if (bestVersion is null || v > bestVersion) { (best, bestVersion) = (exe, v); }
            }
            return best;
        }
    }

    private static unsafe string? PackagePath(string fullName)
    {
        uint length = 0;
        if (GetPackagePathByFullName(fullName, ref length, null) != ErrorInsufficientBuffer) { return null; }
        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            return GetPackagePathByFullName(fullName, ref length, p) == 0 ? new string(p) : null;
        }
    }

    /// <summary>Runs one action and reports meaningful output lines as they come (progress bars and spinners are dropped).</summary>
    public static async Task<WinGetResult> RunAsync(WinGetAction action, string? id, Action<string> output, CancellationToken cancel = default)
    {
        var exe = Locate() ?? throw new InvalidOperationException("Windows Package Manager isn't installed. Get \"App Installer\" from the Microsoft Store, then try again.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var a in WinGetCommands.Arguments(action, id)) { start.ArgumentList.Add(a); }
        UseAdminOnlyTemp(start);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows Package Manager didn't start.");
        var last = "";
        var gate = new object();
        void Line(string? raw)
        {
            if (raw is null) { return; }
            foreach (var part in raw.Split('\r'))
            {
                var line = WinGetCommands.Clean(part);
                if (line is null) { continue; }
                lock (gate) { last = line; }
                output(line);
            }
        }
        var outTask = Task.Run(async () => { string? l; while ((l = await process.StandardOutput.ReadLineAsync(cancel).ConfigureAwait(false)) is not null) { Line(l); } }, cancel);
        var errTask = Task.Run(async () => { string? l; while ((l = await process.StandardError.ReadLineAsync(cancel).ConfigureAwait(false)) is not null) { Line(l); } }, cancel);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        return new WinGetResult(process.ExitCode, WinGetCommands.Describe(process.ExitCode, last));
    }

    /// <summary>The repository details of one catalog app ("winget show"), as cleaned output lines.</summary>
    public static async Task<IReadOnlyList<string>> ShowAsync(string id, CancellationToken cancel = default)
    {
        var exe = Locate() ?? throw new InvalidOperationException("Windows Package Manager isn't installed. Get \"App Installer\" from the Microsoft Store, then try again.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var a in WinGetCommands.ShowArguments(id)) { start.ArgumentList.Add(a); }
        // "show" only reads the repository: no installer is downloaded, so no TEMP redirection is needed.
        
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows Package Manager didn't start.");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await process.WaitForExitAsync(cancel).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var text = await output.ConfigureAwait(false) + "\n" + await errors.ConfigureAwait(false);
        return text.Split('\n').SelectMany(l => l.Split('\r')).Select(WinGetCommands.Clean).OfType<string>().Take(60).ToList();
    }

    /// <summary>
    /// Catalog apps that are installed, from "winget export" (JSON written to an administrators-only folder).
    /// </summary>
    public static async Task<IReadOnlySet<string>> InstalledCatalogIdsAsync(CancellationToken cancel = default)
    {
        var exe = Locate() ?? throw new InvalidOperationException("Windows Package Manager isn't installed. Get \"App Installer\" from the Microsoft Store, then try again.");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PerfTuner-Undo");
        DirectoryHardening.CreateHardened(dir);
        var file = Path.Combine(dir, $"winget-export-{Guid.NewGuid():N}.json");
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Environment.SystemDirectory };
            foreach (var a in new[] { "export", "--output", file, "--source", "winget", "--accept-source-agreements", "--disable-interactivity" }) { start.ArgumentList.Add(a); }
            UseAdminOnlyTemp(start);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows Package Manager didn't start.");
            var drainOut = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var drainErr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try { await process.WaitForExitAsync(cancel).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
            await Task.WhenAll(drainOut, drainErr).ConfigureAwait(false);
            if (!File.Exists(file)) { throw new InvalidOperationException(WinGetCommands.Describe(process.ExitCode, "")); }
            return WinGetCommands.ParseExport(await File.ReadAllTextAsync(file, cancel).ConfigureAwait(false));
        }
        finally
        {
            if (File.Exists(file)) { File.Delete(file); }
        }
    }

    /// <summary>
    /// winget downloads installers into %TEMP% and then runs them with our administrator rights. The user's own %TEMP%
    /// can be written by any program the user runs, so a file could be swapped between winget's hash check and the
    /// start: point TEMP/TMP at a folder only administrators can write instead.
    /// </summary>
    private static void UseAdminOnlyTemp(ProcessStartInfo start)
    {
        var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PerfTuner-Undo", "winget-temp");
        DirectoryHardening.CreateHardened(Path.GetDirectoryName(temp)!);
        DirectoryHardening.CreateHardened(temp);
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
    }

    private const int ErrorInsufficientBuffer = 122;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetPackagesByPackageFamily(string family, ref uint count, nint* fullNames, ref uint bufferLength, char* buffer);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetPackagePathByFullName(string fullName, ref uint length, char* path);
}
