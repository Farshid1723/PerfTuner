using System.Runtime.InteropServices;
using Microsoft.Win32;
using PerfTuner.Core.Undocumented;
using PerfTuner.Platform.Windows.Settings;

namespace PerfTuner.Platform.Windows.Undocumented;

/// <summary>
/// Applies and checks the items of <see cref="UndocumentedCatalog"/>. Only catalog items can be
/// applied; each change is journaled with the item's id so switching it off (or History) puts
/// back exactly what was there.
/// </summary>
public static partial class UndocumentedTweaks
{
    public static int Build { get; } = Environment.OSVersion.Version.Build;

    private static RegistryKey Hive(bool machine) => machine ? Registry.LocalMachine : Registry.CurrentUser;

    private static RegistryValueKind Kind(UndocValueType t) => t switch
    {
        UndocValueType.DWord => RegistryValueKind.DWord,
        UndocValueType.ExpandString => RegistryValueKind.ExpandString,
        UndocValueType.Binary => RegistryValueKind.Binary,
        _ => RegistryValueKind.String,
    };

    public static string AllSettingsFolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), UndocumentedCatalog.AllSettingsFolderName);

    // ------------------------------------------------------------------ state

    /// <summary>Whether the item's change is in place. <paramref name="boot"/> is the current boot settings (null if unknown).</summary>
    public static bool IsApplied(UndocumentedItem item, IReadOnlyDictionary<string, string>? boot)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.AppliesTo(Build)) { return false; }
        return item.Kind switch
        {
            UndocKind.Registry => item.Values.All(Matches),
            UndocKind.PerAdapterRegistry => Adapters() is { Count: > 0 } a && a.All(path => item.Values.All(v => Matches(v with { Path = path }))),
            UndocKind.RemoveKey => item.Keys.All(k => !KeyExists(k.Machine, k.Path)),
            UndocKind.Boot => boot is not null && item.Boot.All(b => boot.TryGetValue(b.Element, out var shown) && BootSettings.IsSetTo(shown, b.Value)),
            UndocKind.Services => item.Services.All(s => ServiceStart.Get(s) is { } st && st.StartType == 4),
            UndocKind.Folder => Directory.Exists(AllSettingsFolderPath),
            _ => false,
        };
    }

    private static bool Matches(UndocValue v)
    {
        object? current;
        try
        {
            using var key = Hive(v.Machine).OpenSubKey(v.Path);
            current = key?.GetValue(v.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false; // protected key: unreadable without full power
        }
        return (current, v.Data) switch
        {
            (null, _) => false,
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            (int a, int b) => a == b,
            (string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool KeyExists(bool machine, string path)
    {
        try
        {
            using var key = Hive(machine).OpenSubKey(path);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return true; // exists, but protected
        }
    }

    /// <summary>The TCP/IP keys of this PC's network adapters.</summary>
    public static IReadOnlyList<string> Adapters()
    {
        using var root = Registry.LocalMachine.OpenSubKey(UndocumentedCatalog.AdapterPathPrefix.TrimEnd('\\'));
        if (root is null) { return []; }
        return [.. root.GetSubKeyNames().Select(n => UndocumentedCatalog.AdapterPathPrefix + n).Where(UndocumentedCatalog.IsAdapterPath)];
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Applies one catalog item; every change is journaled under the item's id. Per-user changes go to the user's
    /// journal; machine-wide ones, boot settings and services only to the administrator-only <paramref name="machine"/> journal.
    /// </summary>
    public static void Apply(UndocumentedItem item, SettingsJournal user, SettingsJournal? machine)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(user);
        SettingsJournal For(bool machineWide) => !machineWide ? user
            : machine is { IsMachine: true } m ? m : throw new InvalidOperationException("This needs full power (administrator).");
        var needsMachine = item.Kind is UndocKind.Boot or UndocKind.Services || item.Values.Any(v => v.Machine) || item.Keys.Any(k => k.Machine);
        if (needsMachine) { _ = For(true); }
        if (!ReferenceEquals(UndocumentedCatalog.Find(item.Id), item)) { throw new InvalidOperationException("unknown item"); }
        if (!item.AppliesTo(Build)) { throw new InvalidOperationException("This doesn't apply to this Windows version. " + item.BuildNote); }
        switch (item.Kind)
        {
            case UndocKind.Registry:
                foreach (var v in item.Values) { For(v.Machine).Record(Write(v, item)); }
                break;
            case UndocKind.PerAdapterRegistry:
                var adapters = Adapters();
                if (adapters.Count == 0) { throw new InvalidOperationException("No network adapters found."); }
                foreach (var path in adapters)
                {
                    foreach (var v in item.Values) { For(v.Machine).Record(Write(v with { Path = path }, item)); }
                }
                break;
            case UndocKind.RemoveKey:
                foreach (var k in item.Keys.Where(k => KeyExists(k.Machine, k.Path)))
                {
                    Hive(k.Machine).DeleteSubKeyTree(k.Path, throwOnMissingSubKey: false);
                    For(k.Machine).Record(new SettingChange { Kind = SettingKind.RegistryKeyRemoved, Tag = item.Id, RegMachine = k.Machine, RegPath = k.Path, Description = item.Title });
                }
                break;
            case UndocKind.Boot:
                var before = BootSettings.Read();
                foreach (var b in item.Boot)
                {
                    var previous = before.TryGetValue(b.Element, out var shown) ? shown : null;
                    if (previous is not null && BootSettings.IsSetTo(previous, b.Value)) { continue; }
                    var original = previous is null ? null : UndocumentedCatalog.BootWord(previous)
                        ?? throw new InvalidOperationException($"\"{b.Element}\" is already set to a value PerfTuner can't put back, so it was left alone.");
                    BootSettings.Set(b.Element, b.Value);
                    For(true).Record(new SettingChange { Kind = SettingKind.BootSetting, Tag = item.Id, BootElement = b.Element, BootOriginal = original, Description = $"{item.Title} ({b.Element} = {b.Value})" });
                }
                break;
            case UndocKind.Services:
                foreach (var name in item.Services)
                {
                    if (ServiceStart.Get(name) is not { } st || st.StartType == 4) { continue; }
                    ServiceStart.Set(name, 4, false);
                    For(true).Record(new SettingChange { Kind = SettingKind.ServiceStartType, Tag = item.Id, ServiceName = name, ServiceStart = st.StartType, ServiceDelayed = st.Delayed, Description = $"{item.Title} ({name} disabled)" });
                    ServiceStart.Stop(name);
                }
                break;
            case UndocKind.Folder:
                var folder = AllSettingsFolderPath;
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    user.Record(new SettingChange { Kind = SettingKind.FolderCreated, Tag = item.Id, FolderPath = folder, Description = item.Title });
                }
                break;
            default:
                throw new InvalidOperationException("This item isn't a setting.");
        }
    }

    private static SettingChange Write(UndocValue v, UndocumentedItem item)
    {
        var root = FirstMissingKey(v.Machine, v.Path);
        var change = RegistryWriter.Write(new RegistryValueSpec(v.Machine, v.Path, v.Name, Kind(v.Type), v.Data), item.Title);
        change.Tag = item.Id;
        change.RegCreatedRoot = root;
        return change;
    }

    /// <summary>The topmost key of <paramref name="path"/> that doesn't exist yet (null if the whole path exists).</summary>
    private static string? FirstMissingKey(bool machine, string path)
    {
        if (KeyExists(machine, path)) { return null; }
        var parts = path.Split('\\');
        for (var n = 1; n <= parts.Length; n++)
        {
            var prefix = string.Join('\\', parts, 0, n);
            if (!KeyExists(machine, prefix)) { return prefix; }
        }
        return path;
    }

    /// <summary>Undoes every journaled change of one item in both journals, newest first. Returns how many were undone.</summary>
    public static int Undo(UndocumentedItem item, SettingsJournal user, SettingsJournal? machine)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(user);
        var count = 0;
        foreach (var journal in new[] { machine, user })
        {
            if (journal is null) { continue; }
            var ids = journal.Entries.Where(e => e.Tag == item.Id).Select(e => e.Id).Reverse().ToList();
            foreach (var id in ids) { journal.Revert(id); }
            count += ids.Count;
        }
        return count;
    }

    // ------------------------------------------------------------------ undo helpers (called by the journal)

    /// <summary>Removes the keys PerfTuner created (from <paramref name="path"/> up to <paramref name="root"/>), each only while it is empty.</summary>
    public static void RemoveCreatedKeys(bool machine, string path, string root)
    {
        var current = path;
        while (current.Length >= root.Length)
        {
            using (var key = Hive(machine).OpenSubKey(current))
            {
                if (key is not null && (key.ValueCount > 0 || key.SubKeyCount > 0)) { return; }
            }
            Hive(machine).DeleteSubKey(current, throwOnMissingSubKey: false);
            var cut = current.LastIndexOf('\\');
            if (cut <= 0) { return; }
            current = current[..cut];
        }
    }

    public static void RecreateKey(bool machine, string path)
    {
        using var key = Hive(machine).CreateSubKey(path, writable: true);
    }

    /// <summary>Removes the "All settings" folder, only from the desktop and only while it is empty.</summary>
    public static void RemoveAllSettingsFolder(string path)
    {
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(AllSettingsFolderPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This undo record doesn't match anything PerfTuner changes, so it was not applied.");
        }
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) { Directory.Delete(path); }
    }

    // ------------------------------------------------------------------ one-off actions

    /// <summary>Runs a one-off action from the catalog; returns a result line.</summary>
    public static string RunAction(UndocumentedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ReferenceEquals(UndocumentedCatalog.Find(item.Id), item) || item.Kind != UndocKind.Action) { throw new InvalidOperationException("unknown action"); }
        return item.Id switch
        {
            "clear-prefetch" => ClearPrefetch(),
            "clear-event-logs" => ClearEventLogs(),
            _ => throw new InvalidOperationException("unknown action"),
        };
    }

    private static string ClearPrefetch()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        int deleted = 0, skipped = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.pf", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try { File.Delete(f); deleted++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return $"Deleted {deleted} Prefetch files" + (skipped > 0 ? $" ({skipped} in use were kept)." : ".");
    }

    private static string ClearEventLogs()
    {
        int cleared = 0, skipped = 0;
        var en = EvtOpenChannelEnum(0, 0);
        if (en == 0) { throw new InvalidOperationException("The list of event logs couldn't be opened (" + Marshal.GetLastPInvokeError() + ")."); }
        try
        {
            var buffer = new char[512];
            while (true)
            {
                int used;
                bool ok;
                unsafe
                {
                    fixed (char* p = buffer) { ok = EvtNextChannelPath(en, buffer.Length, p, out used); }
                }
                if (!ok)
                {
                    var err = Marshal.GetLastPInvokeError();
                    if (err == 259) { break; } // ERROR_NO_MORE_ITEMS
                    if (err == 122 && used > buffer.Length && used < 65536) { buffer = new char[used]; continue; } // buffer too small
                    break;
                }
                var name = new string(buffer, 0, Math.Clamp(used - 1, 0, buffer.Length));
                if (EvtClearLog(0, name, null, 0)) { cleared++; } else { skipped++; }
            }
        }
        finally { EvtClose(en); }
        return $"Cleared {cleared} event logs" + (skipped > 0 ? $" ({skipped} can't be cleared, e.g. analytic logs)." : ".");
    }

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    private static partial nint EvtOpenChannelEnum(nint session, int flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EvtNextChannelPath(nint channelEnum, int bufferSize, char* buffer, out int used);

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClearLog(nint session, string channelPath, string? targetFilePath, int flags);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(nint handle);
}
