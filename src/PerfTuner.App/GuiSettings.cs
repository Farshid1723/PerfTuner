using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfTuner.App;

public sealed record GuiPreferences
{
    public bool CloseToTray { get; init; } = true;
    public bool TrayHintShown { get; init; }

    /// <summary>Colour profile id (see Themes.ThemeManager).</summary>
    public string Theme { get; init; } = "lagoon";

    /// <summary>Tray pop-ups when a mode or automatic fix changes something.</summary>
    public bool Notifications { get; init; } = true;

    /// <summary>Settings-journal ids of the power changes made by the active Turbo/Silent mode.</summary>
    public long[] ModePowerIds { get; init; } = [];

    /// <summary>The mode the user picked (restored after restarts; the engine keeps its own copy).</summary>
    public string Mode { get; init; } = "Off";

    /// <summary>Space explorer view: tree, treemap or sunburst.</summary>
    public string SpaceMode { get; init; } = "tree";
}

[JsonSerializable(typeof(GuiPreferences))]
internal sealed partial class GuiPreferencesJsonContext : JsonSerializerContext;

/// <summary>Per-user GUI preferences (%LOCALAPPDATA%\PerfTuner\gui-settings.json, tiny, validated by type).</summary>
public static class GuiSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerfTuner", "gui-settings.json");

    private static GuiPreferences? _current;

    public static GuiPreferences Current => _current ??= Load();

    private static GuiPreferences Load()
    {
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length < 16_384)
            {
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), GuiPreferencesJsonContext.Default.GuiPreferences) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new();
    }

    public static void Update(Func<GuiPreferences, GuiPreferences> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        _current = change(Current);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_current, GuiPreferencesJsonContext.Default.GuiPreferences));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
