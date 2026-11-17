using System.Management;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using PerfTuner.Core.Network;

namespace PerfTuner.Platform.Windows.Network;

/// <summary>A network adapter with IPv4 on, as Windows reports it.</summary>
public sealed record DnsAdapter(string SettingId, string Name, IReadOnlyList<string> Servers, bool Automatic);

public sealed record SavedDns(string SettingId, string Name, List<string> Servers);

[JsonSerializable(typeof(List<SavedDns>))]
internal sealed partial class SavedDnsJsonContext : JsonSerializerContext;

/// <summary>
/// Sets the IPv4 DNS servers of every active network adapter (documented WMI method
/// Win32_NetworkAdapterConfiguration.SetDNSServerSearchOrder). Before the first change, each adapter's previous
/// setting is saved where only administrators can write (%ProgramData%\PerfTuner-Undo\dns.json), so "Restore" puts
/// back exactly what was there, including "automatic".
/// </summary>
public static class DnsSettings
{
    private static string SavePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PerfTuner-Undo", "dns.json");

    public static IReadOnlyList<DnsAdapter> Adapters()
    {
        var list = new List<DnsAdapter>();
        using var searcher = new ManagementObjectSearcher("SELECT SettingID, Description, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                if (mo["SettingID"] is not string id) { continue; }
                var servers = (mo["DNSServerSearchOrder"] as string[] ?? []).ToList();
                list.Add(new DnsAdapter(id, mo["Description"] as string ?? id, servers, !HasStaticDns(id)));
            }
        }
        return list;
    }

    /// <summary>Windows stores manually set servers in the interface's NameServer value; empty means automatic.</summary>
    private static bool HasStaticDns(string settingId)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{settingId}");
        return key?.GetValue("NameServer") is string s && s.Trim().Length > 0;
    }

    public static bool HasSaved => Load().Count > 0;

    /// <summary>A saved file that exists but can't be trusted or read: changing DNS now would lose the original setting.</summary>
    private static bool SaveUnreadable() => File.Exists(SavePath) && Load().Count == 0;

    /// <summary>Applies a preset to every active adapter. Returns how many adapters were changed.</summary>
    public static int Apply(DnsProvider provider)
    {
        if (SaveUnreadable()) { throw new IOException("PerfTuner's saved DNS setting couldn't be read, so DNS was not changed (the original would be lost). Use \"Restore previous\" or fix the network settings in Windows first."); }
        var adapters = Adapters();
        var saved = Load();
        foreach (var a in adapters.Where(a => saved.All(s => s.SettingId != a.SettingId)))
        {
            saved.Add(new SavedDns(a.SettingId, a.Name, a.Automatic ? [] : [.. a.Servers]));
        }
        Save(saved);
        var changed = 0;
        foreach (var a in adapters)
        {
            if (Set(a.SettingId, provider.Servers)) { changed++; }
        }
        return changed;
    }

    /// <summary>Puts back each adapter's saved setting, then forgets it. Returns how many were restored.</summary>
    public static int RestorePrevious()
    {
        var saved = Load();
        var present = Adapters().Select(a => a.SettingId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restored = 0;
        var keep = new List<SavedDns>();
        foreach (var s in saved)
        {
            if (!DnsPresets.AreAddresses(s.Servers)) { continue; } // never trusted, never kept
            // An adapter that isn't connected right now keeps its saved setting for the next restore.
            if (present.Contains(s.SettingId) && Set(s.SettingId, s.Servers)) { restored++; } else { keep.Add(s); }
        }
        Save(keep);
        return restored;
    }

    private static bool Set(string settingId, IReadOnlyList<string> servers)
    {
        if (!Guid.TryParseExact(settingId, "B", out _)) { return false; }
        using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE AND SettingID = '{settingId}'");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                using var input = mo.GetMethodParameters("SetDNSServerSearchOrder");
                input["DNSServerSearchOrder"] = servers.Count == 0 ? null : servers.ToArray();
                using var output = mo.InvokeMethod("SetDNSServerSearchOrder", input, null);
                return output?["ReturnValue"] is uint rc && rc is 0 or 1;
            }
        }
        return false;
    }

    private static List<SavedDns> Load()
    {
        try
        {
            var path = SavePath;
            if (!File.Exists(path) || new FileInfo(path).Length > 1_000_000) { return []; }
            var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) { return []; }
            var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || !(owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid))) { return []; }
            return JsonSerializer.Deserialize(File.ReadAllText(path), SavedDnsJsonContext.Default.ListSavedDns) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Save(List<SavedDns> list)
    {
        DirectoryHardening.CreateHardened(Path.GetDirectoryName(SavePath)!);
        if (list.Count == 0) { if (File.Exists(SavePath)) { File.Delete(SavePath); } return; }
        var tmp = SavePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, SavedDnsJsonContext.Default.ListSavedDns));
        File.Move(tmp, SavePath, overwrite: true);
    }
}
