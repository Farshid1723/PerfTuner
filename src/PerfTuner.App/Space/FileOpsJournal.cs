using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfTuner.App.Space;

public enum FileOpKind
{
    Moved,
    Recycled,
    DeletedPermanently,
}

/// <summary>One moved item: where it came from and where it went.</summary>
/// <param name="Merged">Windows merged it into an existing folder of the same name: not undoable automatically.</param>
public sealed record MovedItem(string From, string To, bool Merged = false);

/// <summary>One file operation as shown in History.</summary>
public sealed record FileOpEntry
{
    public long Id { get; set; }
    public long CreatedUnixMs { get; set; }
    public FileOpKind Kind { get; set; }
    public int Count { get; set; }
    public long Bytes { get; set; }
    public string Summary { get; set; } = "";
    public List<MovedItem> Moves { get; set; } = [];

    /// <summary>Where the items came from when it isn't the Space explorer (e.g. the Windows Installer cache).</summary>
    public string? Origin { get; set; }

    public const string InstallerCacheOrigin = "installer-cache";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<FileOpEntry>))]
internal sealed partial class FileOpsJsonContext : JsonSerializerContext;

/// <summary>
/// The user's own record of files they moved or deleted with PerfTuner (design §4.11): per-user, in
/// %LOCALAPPDATA%\PerfTuner\file-operations.json, 200 entries. Kept separate from tuning changes: "Undo everything"
/// and uninstall never move files back. Moves are undone one entry at a time.
/// </summary>
public sealed class FileOpsJournal
{
    private const int MaxEntries = 200;
    private static readonly Lock Gate = new();
    private readonly string _path;

    public FileOpsJournal(string? path = null) =>
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerfTuner", "file-operations.json");

    public IReadOnlyList<FileOpEntry> Entries
    {
        get { lock (Gate) { return Load(); } }
    }

    public void Record(FileOpEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (Gate)
        {
            var list = Load();
            entry.Id = list.Count == 0 ? 1 : list.Max(e => e.Id) + 1;
            entry.CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            list.Add(entry);
            while (list.Count > MaxEntries) { list.RemoveAt(0); }
            Save(list);
        }
    }

    public void Remove(long id)
    {
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(e => e.Id == id);
            Save(list);
        }
    }

    /// <summary>Replaces an entry's remaining moves (after a partial undo).</summary>
    public void Update(FileOpEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (Gate)
        {
            var list = Load();
            var i = list.FindIndex(e => e.Id == entry.Id);
            if (i >= 0) { list[i] = entry; Save(list); }
        }
    }

    private List<FileOpEntry> Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 20_000_000) { return []; }
            return JsonSerializer.Deserialize(File.ReadAllText(_path), FileOpsJsonContext.Default.ListFileOpEntry) ?? [];
        }
        catch (JsonException)
        {
            // Keep a damaged journal aside instead of overwriting it on the next write.
            try { File.Move(_path, _path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(List<FileOpEntry> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, FileOpsJsonContext.Default.ListFileOpEntry));
        File.Move(tmp, _path, overwrite: true);
    }
}
