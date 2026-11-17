using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfTuner.Platform.Windows.Settings;

public enum SettingKind
{
    PowerPlan,
    ProcessorPower,
    VisualEffects,
    Transparency,
    StartupApp,
    RegistryValue,
    MouseParameters,
}

/// <summary>One persistent settings change and the value it replaced.</summary>
public sealed record SettingChange
{
    public long Id { get; set; }
    public SettingKind Kind { get; set; }
    public string Description { get; set; } = "";
    public long CreatedUnixMs { get; set; }

    public Guid? Scheme { get; set; }                 // PowerPlan (previous active), ProcessorPower (scheme)
    public uint[]? ProcessorValues { get; set; }      // ProcessorPower originals (6 values)
    public int[]? VisualValues { get; set; }          // VisualEffects originals
    public int? TransparencyValue { get; set; }       // Transparency original (null = absent)
    public StartupLocation? StartupLocation { get; set; }
    public string? StartupName { get; set; }
    public byte[]? StartupOriginal { get; set; }

    public bool RegMachine { get; set; }              // RegistryValue
    public string? RegPath { get; set; }
    public string? RegName { get; set; }
    public string? RegOriginal { get; set; }          // null = value did not exist
    public string? RegOriginalKind { get; set; }
    public int[]? IntValues { get; set; }             // MouseParameters originals
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<SettingChange>))]
internal sealed partial class SettingsJournalJsonContext : JsonSerializerContext;

/// <summary>
/// Undo log for persistent settings changes made from the GUI (power plan, processor power,
/// visual effects, transparency, startup apps). Stored per user in
/// %LOCALAPPDATA%\PerfTuner\settings-journal.json (bounded: 200 entries, 1 MB).
/// Unlike live process tuning these are deliberate, lasting choices, so they are NOT undone
/// when the service stops — only by Revert / Revert all or by uninstalling.
/// </summary>
public sealed class SettingsJournal
{
    private const int MaxEntries = 200;
    private readonly string _path;
    private readonly Lock _gate = new();
    private List<SettingChange> _entries;

    public SettingsJournal(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerfTuner", "settings-journal.json");
        _entries = Load(_path);
    }

    public IReadOnlyList<SettingChange> Entries
    {
        get { lock (_gate) { return [.. _entries]; } }
    }

    public void Record(SettingChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            change.Id = _entries.Count == 0 ? 1 : _entries.Max(e => e.Id) + 1;
            change.CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _entries.Add(change);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(0); // oldest entry loses its undo; documented bound
            }
            Save();
        }
    }

    /// <summary>Restores the value captured by one entry and removes it.</summary>
    public string Revert(long id)
    {
        SettingChange? entry;
        lock (_gate)
        {
            entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry is null) { return "Change not found."; }
        }
        Undo(entry);
        lock (_gate)
        {
            _entries.Remove(entry);
            Save();
        }
        return $"Reverted: {entry.Description}";
    }

    /// <summary>Reverts everything, newest first. Returns (reverted, failed).</summary>
    public (int Reverted, int Failed) RevertAll()
    {
        int ok = 0, failed = 0;
        foreach (var entry in Entries.Reverse())
        {
            try
            {
                Undo(entry);
                ok++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or ArgumentException)
            {
                failed++;
            }
            lock (_gate)
            {
                _entries.RemoveAll(e => e.Id == entry.Id);
            }
        }
        lock (_gate) { Save(); }
        return (ok, failed);
    }

    private static void Undo(SettingChange e)
    {
        switch (e.Kind)
        {
            case SettingKind.PowerPlan when e.Scheme is { } scheme:
                PowerSettings.SetActive(scheme);
                break;
            case SettingKind.ProcessorPower when e.Scheme is { } scheme && e.ProcessorValues is { Length: 6 } v:
                PowerSettings.WriteProcessor(scheme, new ProcessorPower(v[0], v[1], v[2], v[3], v[4], v[5]));
                break;
            case SettingKind.VisualEffects when e.VisualValues is { } values:
                VisualEffects.Apply(values);
                break;
            case SettingKind.Transparency:
                Transparency.Set(e.TransparencyValue);
                break;
            case SettingKind.StartupApp when e.StartupLocation is { } loc && e.StartupName is { } name:
                StartupApps.Restore(loc, name, e.StartupOriginal);
                break;
            case SettingKind.RegistryValue when e.RegPath is not null && e.RegName is not null:
                // The journal sits in the user profile: only values PerfTuner itself writes may be put back.
                if (!WindowsTweaks.MayRestore(e.RegMachine, e.RegPath, e.RegName) && !Security.SecurityChecks.MayRestore(e.RegMachine, e.RegPath, e.RegName, e.RegOriginal))
                {
                    throw new InvalidOperationException("This undo record doesn't match anything PerfTuner changes, so it was not applied.");
                }
                RegistryWriter.Restore(e);
                break;
            case SettingKind.MouseParameters when e.IntValues is { Length: 3 } mouse:
                WindowsTweaks.SetMouseParams(mouse);
                break;
            default:
                throw new InvalidOperationException("incomplete journal entry");
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, SettingsJournalJsonContext.Default.ListSettingChange));
        File.Move(tmp, _path, overwrite: true);
    }

    private static List<SettingChange> Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1024 * 1024) { return []; }
            return JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJournalJsonContext.Default.ListSettingChange) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
