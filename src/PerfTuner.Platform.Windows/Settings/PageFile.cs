using System.Globalization;
using System.Management;

namespace PerfTuner.Platform.Windows.Settings;

/// <summary>One page-file setting: drive letter and sizes in MB (0/0 = size managed by Windows on that drive).</summary>
public sealed record PageFileEntry(string Letter, long InitialMb, long MaximumMb);

/// <summary>The machine's complete page-file configuration.</summary>
public sealed record PageFileConfig(bool AutomaticManaged, IReadOnlyList<PageFileEntry> Entries)
{
    public bool SameAs(PageFileConfig other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (AutomaticManaged != other.AutomaticManaged) { return false; }
        if (AutomaticManaged) { return true; }
        var a = Entries.OrderBy(e => e.Letter, StringComparer.Ordinal).ToList();
        var b = other.Entries.OrderBy(e => e.Letter, StringComparer.Ordinal).ToList();
        return a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);
    }

    public string Describe() => AutomaticManaged ? "managed by Windows"
        : Entries.Count == 0 ? "no page file"
        : string.Join(", ", Entries.Select(e => e.InitialMb == 0 && e.MaximumMb == 0 ? $"{e.Letter}: managed by Windows" : $"{e.Letter}: {e.InitialMb:N0}–{e.MaximumMb:N0} MB"));
}

/// <summary>
/// Page-file configuration through the documented WMI classes Win32_ComputerSystem.AutomaticManagedPagefile and
/// Win32_PageFileSetting. File names are built only from a validated drive letter ("X:\pagefile.sys").
/// Writing requires administrator rights; changes take effect after a restart. PerfTuner never restarts the PC.
/// </summary>
public static class PageFile
{
    private static ManagementScope Scope()
    {
        var scope = new ManagementScope(@"\\.\root\cimv2", new ConnectionOptions { EnablePrivileges = true });
        scope.Connect();
        return scope;
    }

    public static PageFileConfig Read()
    {
        var scope = Scope();
        bool auto;
        using (var cs = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem")))
        {
            auto = cs.Get().OfType<ManagementObject>().Select(o => { using (o) { return o["AutomaticManagedPagefile"] is true; } }).FirstOrDefault();
        }
        var entries = new List<PageFileEntry>();
        using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting")))
        {
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var name = o["Name"] as string ?? "";
                    if (name.Length < 2 || name[1] != ':' || !char.IsAsciiLetter(name[0])) { continue; }
                    entries.Add(new PageFileEntry(char.ToUpperInvariant(name[0]).ToString(),
                        Convert.ToInt64(o["InitialSize"] ?? 0u, CultureInfo.InvariantCulture),
                        Convert.ToInt64(o["MaximumSize"] ?? 0u, CultureInfo.InvariantCulture)));
                }
            }
        }
        return new PageFileConfig(auto, entries);
    }

    /// <summary>
    /// Writes a complete configuration. With <see cref="PageFileConfig.AutomaticManaged"/> = true the per-drive
    /// entries are left to Windows. Refuses configurations without any page file, or without one on the Windows drive.
    /// </summary>
    public static void Write(PageFileConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var windows = char.ToUpperInvariant(Environment.SystemDirectory[0]).ToString();
        if (!config.AutomaticManaged && !config.Entries.Any(e => e.Letter == windows))
        {
            throw new InvalidOperationException("The Windows drive must keep a page file.");
        }
        foreach (var e in config.Entries)
        {
            Validate(e);
        }

        var scope = Scope();
        if (!config.AutomaticManaged)
        {
            // Per-drive settings only apply once automatic management is off.
            SetAutomatic(scope, false);
            // Create or update first (Windows drive first), delete last: a failure half-way never leaves the
            // Windows drive without a page file.
            var existing = new Dictionary<string, ManagementObject>(StringComparer.Ordinal);
            var stale = new List<ManagementObject>();
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_PageFileSetting")))
            {
                foreach (ManagementObject o in s.Get())
                {
                    var name = o["Name"] as string ?? "";
                    var letter = name.Length > 1 && name[1] == ':' ? char.ToUpperInvariant(name[0]).ToString() : "";
                    if (letter.Length == 1 && config.Entries.Any(e => e.Letter == letter) && !existing.ContainsKey(letter)) { existing[letter] = o; }
                    else { stale.Add(o); }
                }
            }
            try
            {
                using var cls = new ManagementClass(scope, new ManagementPath("Win32_PageFileSetting"), null);
                foreach (var e in config.Entries.OrderBy(e => e.Letter == windows ? 0 : 1))
                {
                    if (existing.TryGetValue(e.Letter, out var o))
                    {
                        o["InitialSize"] = (uint)e.InitialMb;
                        o["MaximumSize"] = (uint)e.MaximumMb;
                        o.Put();
                    }
                    else
                    {
                        using var n = cls.CreateInstance();
                        n["Name"] = e.Letter + @":\pagefile.sys";
                        n["InitialSize"] = (uint)e.InitialMb;
                        n["MaximumSize"] = (uint)e.MaximumMb;
                        n.Put();
                    }
                }
                foreach (var o in stale) { o.Delete(); }
            }
            finally
            {
                foreach (var o in existing.Values) { o.Dispose(); }
                foreach (var o in stale) { o.Dispose(); }
            }
        }
        else
        {
            SetAutomatic(scope, true);
        }
    }

    /// <summary>True when <see cref="Write"/> accepts this configuration (it keeps a page file on the Windows drive).</summary>
    public static bool IsWritable(PageFileConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var windows = char.ToUpperInvariant(Environment.SystemDirectory[0]).ToString();
        return config.AutomaticManaged || config.Entries.Any(e => e.Letter == windows);
    }

    private static void SetAutomatic(ManagementScope scope, bool value)
    {
        using var cs = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_ComputerSystem"));
        foreach (ManagementObject o in cs.Get())
        {
            using (o)
            {
                if (o["AutomaticManagedPagefile"] is bool current && current == value) { continue; }
                o["AutomaticManagedPagefile"] = value;
                o.Put();
            }
        }
    }

    private static void Validate(PageFileEntry e)
    {
        if (e.Letter is not { Length: 1 } || !char.IsAsciiLetterUpper(e.Letter[0]))
        {
            throw new ArgumentException("invalid drive letter");
        }
        if (e.InitialMb < 0 || e.MaximumMb < e.InitialMb || e.MaximumMb > 16L * 1024 * 1024)
        {
            throw new ArgumentException("invalid page-file size");
        }
        if (e.MaximumMb != 0 && e.InitialMb < 16)
        {
            throw new ArgumentException("page file too small");
        }
    }
}
