using System.Text.Json;

namespace PerfTuner.Core.Apps;

public enum WinGetAction { Install, Upgrade, Uninstall, UpgradeAll }

public sealed record WinGetResult(int ExitCode, string Summary)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>The fixed winget command lines PerfTuner can run, and how it reads winget's output (pure, unit-tested).</summary>
public static class WinGetCommands
{
    /// <summary>The argument list for an action (public for tests and review). Throws for an id outside the catalog.</summary>
    public static IReadOnlyList<string> Arguments(WinGetAction action, string? id)
    {
        if (action != WinGetAction.UpgradeAll && (id is null || !AppCatalog.IsValidId(id) || AppCatalog.Find(id) is null))
        {
            throw new ArgumentException("Only apps from PerfTuner's catalog can be installed, updated or removed.", nameof(id));
        }
        string[] common = ["--source", "winget", "--accept-source-agreements", "--disable-interactivity"];
        return action switch
        {
            WinGetAction.Install => ["install", "--id", id!, "--exact", .. common, "--accept-package-agreements", "--silent"],
            WinGetAction.Upgrade => ["upgrade", "--id", id!, "--exact", .. common, "--accept-package-agreements", "--silent"],
            WinGetAction.Uninstall => ["uninstall", "--id", id!, "--exact", .. common, "--silent"],
            _ => ["upgrade", "--all", .. common, "--accept-package-agreements", "--silent"],
        };
    }

    /// <summary>Drops progress bars, spinners and blank lines from winget's console output.</summary>
    public static string? Clean(string raw)
    {
        var line = raw.Trim();
        if (line.Length < 2) { return null; }
        if (line.All(c => c is '█' or '▒' or '-' or '\\' or '|' or '/' or ' ' or '%' or '.' || char.IsAsciiDigit(c) || c is 'K' or 'M' or 'G' or 'B')) { return null; }
        if (line.Contains('█', StringComparison.Ordinal) || line.Contains('▒', StringComparison.Ordinal)) { return null; }
        return line.Length > 300 ? line[..300] : line;
    }

    /// <summary>"winget show" for one catalog app: its repository details (version, publisher, homepage, licence…).</summary>
    public static IReadOnlyList<string> ShowArguments(string id)
    {
        if (!AppCatalog.IsValidId(id) || AppCatalog.Find(id) is null) { throw new ArgumentException("Only apps from PerfTuner's catalog can be looked up.", nameof(id)); }
        return ["show", "--id", id, "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity"];
    }

    /// <summary>Done, already installed / up to date, or done pending a restart.</summary>
    public static bool CountsAsSuccess(int code) => unchecked((uint)code) is 0 or 0x8A15002B or 0x8A15010D or 0x8A150061 or 0x8A150109;

    /// <summary>Plain words for winget's documented result codes.</summary>
    public static string Describe(int code, string lastLine) => unchecked((uint)code) switch
    {
        0 => "Done",
        0x8A15002B => "Already installed and up to date",
        0x8A15010D or 0x8A150061 => "Already installed",
        0x8A150109 => "Done; restart Windows to finish",
        0x8A150014 => "Not found (not installed, or not in the package repository)",
        0x8A150010 => "No installer for this version of Windows",
        0x8A150101 => "The app is running; close it and try again",
        0x8A150102 => "Another installation is in progress; try again later",
        0x8A150056 => "This app's installer refuses to run with administrator rights; install it from the publisher's website instead",
        0xC0000135 => "Windows Package Manager couldn't start (a component is missing); update \"App Installer\" from the Microsoft Store",
        _ => $"Stopped (code 0x{code:X8}){(lastLine.Length > 0 ? ": " + lastLine : "")}",
    };

    /// <summary>The catalog identifiers found in a winget export document.</summary>
    public static IReadOnlySet<string> ParseExport(string json)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("Sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sources.EnumerateArray())
            {
                if (!source.TryGetProperty("Packages", out var packages) || packages.ValueKind != JsonValueKind.Array) { continue; }
                foreach (var p in packages.EnumerateArray())
                {
                    if (p.TryGetProperty("PackageIdentifier", out var id) && id.GetString() is { } s && AppCatalog.Find(s) is { } app) { ids.Add(app.Id); }
                }
            }
        }
        return ids;
    }

}
