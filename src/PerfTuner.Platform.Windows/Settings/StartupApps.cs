using Microsoft.Win32;

namespace PerfTuner.Platform.Windows.Settings;

public enum StartupLocation
{
    UserRun,
    MachineRun,
    MachineRun32,
    UserStartupFolder,
    CommonStartupFolder,
}

public sealed record StartupApp(string Name, string Command, StartupLocation Location, bool Enabled)
{
    public bool RequiresAdmin => Location is StartupLocation.MachineRun or StartupLocation.MachineRun32 or StartupLocation.CommonStartupFolder;

    public string LocationText => Location switch
    {
        StartupLocation.UserRun => "Current user",
        StartupLocation.MachineRun => "All users",
        StartupLocation.MachineRun32 => "All users (32-bit)",
        StartupLocation.UserStartupFolder => "Startup folder",
        _ => "Common startup folder",
    };
}

/// <summary>
/// Startup apps, enabled/disabled exactly the way Task Manager does it: through the
/// <c>Explorer\StartupApproved</c> keys. The program's own Run entry or shortcut is never
/// modified or deleted, so disabling is fully reversible and Task Manager shows the same state.
/// </summary>
public static class StartupApps
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static IReadOnlyList<StartupApp> List()
    {
        var result = new List<StartupApp>();
        ReadRun(Registry.CurrentUser, RunKey, StartupLocation.UserRun, result);
        ReadRun(Registry.LocalMachine, RunKey, StartupLocation.MachineRun, result);
        ReadRun(Registry.LocalMachine, Run32Key, StartupLocation.MachineRun32, result);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupLocation.UserStartupFolder, result);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupLocation.CommonStartupFolder, result);
        return result;
    }

    /// <summary>Sets the approved state; returns the previous raw value (null = absent) for undo.</summary>
    public static byte[]? SetEnabled(StartupApp app, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(app);
        var (hive, path) = ApprovedKey(app.Location);
        using var key = hive.CreateSubKey(path, writable: true);
        var previous = key.GetValue(app.Name) as byte[];
        var value = new byte[12];
        value[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled)
        {
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(value, 4);
        }
        key.SetValue(app.Name, value, RegistryValueKind.Binary);
        return previous;
    }

    /// <summary>Restores a previously captured raw value.</summary>
    public static void Restore(StartupLocation location, string name, byte[]? previous)
    {
        var (hive, path) = ApprovedKey(location);
        using var key = hive.CreateSubKey(path, writable: true);
        if (previous is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, previous, RegistryValueKind.Binary);
        }
    }

    private static (RegistryKey Hive, string Path) ApprovedKey(StartupLocation location) => location switch
    {
        StartupLocation.UserRun => (Registry.CurrentUser, ApprovedRoot + "Run"),
        StartupLocation.MachineRun => (Registry.LocalMachine, ApprovedRoot + "Run"),
        StartupLocation.MachineRun32 => (Registry.LocalMachine, ApprovedRoot + "Run32"),
        StartupLocation.UserStartupFolder => (Registry.CurrentUser, ApprovedRoot + "StartupFolder"),
        _ => (Registry.LocalMachine, ApprovedRoot + "StartupFolder"),
    };

    private static bool IsEnabled(StartupLocation location, string name)
    {
        var (hive, path) = ApprovedKey(location);
        using var key = hive.OpenSubKey(path);
        // Absent = enabled. Even first byte (0x02, 0x06) = enabled; odd (0x03, 0x01) = disabled.
        return key?.GetValue(name) is not byte[] { Length: > 0 } raw || (raw[0] & 1) == 0;
    }

    private static void ReadRun(RegistryKey hive, string path, StartupLocation location, List<StartupApp> output)
    {
        using var key = hive.OpenSubKey(path);
        if (key is null) { return; }
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) { continue; }
            var command = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
            output.Add(new StartupApp(name, Trim(command), location, IsEnabled(location, name)));
        }
    }

    private static void ReadFolder(string folder, StartupLocation location, List<StartupApp> output)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { return; }
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) { continue; }
            output.Add(new StartupApp(name, Trim(file), location, IsEnabled(location, name)));
        }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;
}

public readonly record struct TempScan(int Files, long Bytes);

/// <summary>
/// Deletes old temporary files. IRREVERSIBLE — the GUI previews first and asks for
/// confirmation. Only files older than the age limit; reparse points are never followed;
/// files in use are skipped.
/// </summary>
public static class TempCleaner
{
    public static IReadOnlyList<string> Roots(bool includeWindowsTemp)
    {
        var roots = new List<string> { Path.GetTempPath() };
        if (includeWindowsTemp)
        {
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
        }
        return roots;
    }

    public static TempScan Scan(IEnumerable<string> roots, TimeSpan minAge)
    {
        ArgumentNullException.ThrowIfNull(roots);
        int files = 0;
        long bytes = 0;
        foreach (var file in Candidates(roots, minAge))
        {
            files++;
            bytes += file.Length;
        }
        return new TempScan(files, bytes);
    }

    public static (int Deleted, long Freed, int Skipped) Clean(IEnumerable<string> roots, TimeSpan minAge)
    {
        ArgumentNullException.ThrowIfNull(roots);
        int deleted = 0, skipped = 0;
        long freed = 0;
        foreach (var file in Candidates(roots, minAge).ToList())
        {
            try
            {
                var size = file.Length;
                file.Delete();
                deleted++;
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }
        return (deleted, freed, skipped);
    }

    private static IEnumerable<FileInfo> Candidates(IEnumerable<string> roots, TimeSpan minAge)
    {
        var cutoff = DateTime.UtcNow - minAge;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            MaxRecursionDepth = 16,
        };
        foreach (var root in roots)
        {
            var dir = new DirectoryInfo(root);
            if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) { continue; }
            foreach (var file in dir.EnumerateFiles("*", options))
            {
                if (file.LastWriteTimeUtc < cutoff && file.LastAccessTimeUtc < cutoff)
                {
                    yield return file;
                }
            }
        }
    }
}
