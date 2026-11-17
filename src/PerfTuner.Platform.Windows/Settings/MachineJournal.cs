using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfTuner.Platform.Windows.Settings;

/// <summary>One machine-wide change: the configuration before and the configuration PerfTuner applied.</summary>
public sealed record MachineChange
{
    public long Id { get; set; }
    public long CreatedUnixMs { get; set; }
    public string Description { get; set; } = "";
    public PageFileConfig? PageFileBefore { get; set; }
    public PageFileConfig? PageFileApplied { get; set; }

    /// <summary>Set when "before" could not be restored as such (e.g. no page file at all): undo then lets Windows manage it.</summary>
    public string? UndoNote { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<MachineChange>))]
internal sealed partial class MachineJournalJsonContext : JsonSerializerContext;

/// <summary>
/// Undo log for machine-wide settings (the page file). Stored in %ProgramData%\PerfTuner-Undo\machine-journal.json
/// (outside the product data folder, so uninstall can't delete it before every change is undone). The folder is
/// re-hardened on every write (Administrators/SYSTEM only, no reparse points) and a file not owned by Administrators
/// or SYSTEM is ignored, so a standard user can't plant an undo target. Bounded to 50 entries.
/// </summary>
public sealed class MachineJournal
{
    private const int MaxEntries = 50;
    private readonly string _path;
    private readonly Lock _gate = new();
    private List<MachineChange> _entries;

    public MachineJournal(string? path = null)
    {
        _path = path ?? DefaultPath;
        _entries = Load(_path);
    }

    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PerfTuner-Undo", "machine-journal.json");

    public IReadOnlyList<MachineChange> Entries
    {
        get { lock (_gate) { return [.. _entries]; } }
    }

    /// <summary>Records a change before it is made (so a failed or partial write can still be undone). Returns its id.</summary>
    public long Record(MachineChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            change.Id = _entries.Count == 0 ? 1 : _entries.Max(e => e.Id) + 1;
            change.CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _entries.Add(change);
            if (_entries.Count > MaxEntries) { _entries.RemoveAt(0); }
            Save();
            return change.Id;
        }
    }

    /// <summary>Stores what was actually applied (for the drift check).</summary>
    public void SetApplied(long id, PageFileConfig applied)
    {
        lock (_gate)
        {
            if (_entries.FirstOrDefault(e => e.Id == id) is { } e)
            {
                e.PageFileApplied = applied;
                Save();
            }
        }
    }

    /// <summary>True when the current configuration is no longer what PerfTuner applied (someone changed it since).</summary>
    public static bool HasDrifted(MachineChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.PageFileApplied is { } applied && !PageFile.Read().SameAs(applied);
    }

    /// <summary>Restores the "before" configuration of one entry and removes it.</summary>
    public string Revert(long id)
    {
        MachineChange? entry;
        lock (_gate) { entry = _entries.FirstOrDefault(e => e.Id == id); }
        if (entry is null) { return "Change not found."; }
        if (entry.PageFileBefore is { } before) { PageFile.Write(before); }
        lock (_gate)
        {
            _entries.Remove(entry);
            Save();
        }
        return $"Reverted: {entry.Description} (takes effect after a restart)";
    }

    /// <summary>
    /// Undoes everything, newest first. Entries whose setting was changed again by someone else since are skipped
    /// (and kept), so a later manual choice is never silently overwritten.
    /// </summary>
    public (int Reverted, int Failed, int Skipped) RevertAll()
    {
        int ok = 0, failed = 0, skipped = 0;
        foreach (var e in Entries.Reverse())
        {
            try
            {
                if (HasDrifted(e)) { skipped++; continue; }
                Revert(e.Id);
                ok++;
            }
            catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or IOException or System.Runtime.InteropServices.COMException)
            {
                failed++;
            }
        }
        return (ok, failed, skipped);
    }

    /// <summary>Removes the journal folder once nothing is left to undo (used by uninstall).</summary>
    public void DeleteIfEmpty()
    {
        lock (_gate)
        {
            if (_entries.Count > 0) { return; }
        }
        var dir = Path.GetDirectoryName(_path)!;
        try
        {
            if (Directory.Exists(dir) && !new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) { Directory.Delete(dir, recursive: true); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; harmless (admin-only, empty journal).
        }
    }

    private void Save()
    {
        DirectoryHardening.CreateHardened(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, MachineJournalJsonContext.Default.ListMachineChange));
        File.Move(tmp, _path, overwrite: true);
    }

    private static List<MachineChange> Load(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1_000_000 || !TrustedOwner(path)) { return []; }
            return JsonSerializer.Deserialize(File.ReadAllText(path), MachineJournalJsonContext.Default.ListMachineChange) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Only trust a journal (and its folder) owned by Administrators or SYSTEM and not reached through a junction.</summary>
    private static bool TrustedOwner(string path)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) { return false; }
        var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
    }
}
