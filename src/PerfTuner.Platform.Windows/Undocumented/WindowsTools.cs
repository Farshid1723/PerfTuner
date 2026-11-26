using System.Diagnostics;

namespace PerfTuner.Platform.Windows.Undocumented;

/// <summary>
/// The only Windows command-line tools PerfTuner starts besides winget: bcdedit (boot settings),
/// netsh (Winsock and TCP/IP reset) and WSReset (Microsoft Store cache). Always from System32,
/// never through a shell, and only with the fixed argument lists defined next to their callers.
/// </summary>
public static class WindowsTools
{
    public const string BcdEdit = "bcdedit.exe";
    public const string Netsh = "netsh.exe";
    public const string StoreReset = "WSReset.exe";

    private static readonly string[] Allowed = [BcdEdit, Netsh, StoreReset];

    public sealed record Result(int ExitCode, string Output);

    public static Result Run(string tool, IReadOnlyList<string> arguments, TimeSpan timeout, bool wait = true)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Allowed.Contains(tool, StringComparer.Ordinal)) { throw new InvalidOperationException("not an allowed Windows tool"); }
        var exe = Path.Combine(Environment.SystemDirectory, tool);
        if (!File.Exists(exe)) { throw new InvalidOperationException($"{tool} isn't on this PC."); }
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = wait,
            RedirectStandardError = wait,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var a in arguments) { start.ArgumentList.Add(a); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} didn't start.");
        if (!wait) { return new Result(0, ""); }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{tool} didn't finish in time.");
        }
        return new Result(process.ExitCode, (stdout.Result + stderr.Result).Trim());
    }
}

/// <summary>Boot settings of the running Windows through bcdedit ({current} entry only, catalog elements only).</summary>
public static class BootSettings
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // How bcdedit shows "No" in the languages Windows ships in; anything else present counts as set.
    private static readonly string[] NoWords = ["no", "nein", "non", "nee", "nej", "nei", "não", "nao", "nie", "ne", "ei", "nem", "hayır", "нет", "ні", "όχι", "לא", "لا", "否", "いいえ", "아니요", "ไม่"];

    /// <summary>The elements of the running Windows' boot entry, name → value as bcdedit shows it (needs administrator rights).</summary>
    public static IReadOnlyDictionary<string, string> Read()
    {
        var r = WindowsTools.Run(WindowsTools.BcdEdit, ["/enum", "{current}"], Timeout);
        if (r.ExitCode != 0) { throw new InvalidOperationException("Boot settings couldn't be read: " + FirstLine(r.Output)); }
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in r.Output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) { continue; }
            var gap = line.IndexOf(' ', StringComparison.Ordinal);
            if (gap <= 0) { continue; }
            map.TryAdd(line[..gap], line[gap..].Trim());
        }
        return map;
    }

    /// <summary>Whether what bcdedit shows matches the value PerfTuner sets ("yes" in any language).</summary>
    public static bool IsSetTo(string? shown, string value)
    {
        if (string.IsNullOrWhiteSpace(shown)) { return false; }
        var s = shown.Trim();
        return value == "yes"
            ? !NoWords.Contains(s.ToLowerInvariant(), StringComparer.Ordinal)
            : string.Equals(s, value, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(string element, string value)
    {
        if (!Core.Undocumented.UndocumentedCatalog.MayRestoreBoot(element, value)) { throw new InvalidOperationException("not a catalog boot setting"); }
        Check(WindowsTools.Run(WindowsTools.BcdEdit, ["/set", "{current}", element, value], Timeout));
    }

    /// <summary>Puts a boot element back: a word, or removed when it wasn't set before.</summary>
    public static void Restore(string element, string? original)
    {
        if (!Core.Undocumented.UndocumentedCatalog.MayRestoreBoot(element, original)) { throw new InvalidOperationException("not a catalog boot setting"); }
        Check(original is null
            ? WindowsTools.Run(WindowsTools.BcdEdit, ["/deletevalue", "{current}", element], Timeout)
            : WindowsTools.Run(WindowsTools.BcdEdit, ["/set", "{current}", element, original], Timeout));
    }

    private static void Check(WindowsTools.Result r)
    {
        if (r.ExitCode != 0) { throw new InvalidOperationException("Boot settings couldn't be changed: " + FirstLine(r.Output)); }
    }

    private static string FirstLine(string text) => text.Split('\n').FirstOrDefault()?.Trim() ?? "";
}
