using System.Globalization;
using System.Management;

namespace PerfTuner.App.SystemTools;

public sealed record DriveHealthInfo(
    string Name, string Media, string Bus, double SizeGb, string Health,
    int? TemperatureC, int? TemperatureMaxC, int? WearPercent, long? PowerOnHours,
    long? ReadErrorsUncorrected, long? WriteErrorsUncorrected, long? ReadLatencyMaxMs, string Verdict, string DeviceId = "")
{
    public string TemperatureText => TemperatureC is > 0 ? $"{TemperatureC} °C" + (TemperatureMaxC is > 0 ? $" (max {TemperatureMaxC} °C)" : "") : "n/a";
    public string WearText => WearPercent is { } w ? $"{w} % used" : "n/a";
    public string HoursText => PowerOnHours is { } h ? $"{h:N0} h ({h / 24.0 / 365:F1} y)" : "n/a";
    public string ErrorsText => ReadErrorsUncorrected is null && WriteErrorsUncorrected is null
        ? "n/a"
        : $"{(ReadErrorsUncorrected ?? 0) + (WriteErrorsUncorrected ?? 0)} uncorrected";
    public string Summary => $"{Media} · {Bus} · {SizeGb:F0} GB · Windows health: {Health}";
}

/// <summary>
/// Drive health from the Windows Storage Management API: MSFT_PhysicalDisk (health status,
/// media type) joined to MSFT_StorageReliabilityCounter (temperature, wear, power-on hours,
/// uncorrected errors, worst latency) on DeviceId. Read-only; reliability counters need
/// administrator rights.
/// </summary>
public static class DriveHealth
{
    private const string Scope = @"root\Microsoft\Windows\Storage";

    public static IReadOnlyList<DriveHealthInfo> Read()
    {
        var counters = new Dictionary<string, ManagementBaseObject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var rc = new ManagementObjectSearcher(Scope, "SELECT * FROM MSFT_StorageReliabilityCounter");
            foreach (var o in rc.Get())
            {
                if (o["DeviceId"] is string id) { counters[id] = o; }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Not elevated or not supported: health status still works.
        }

        var result = new List<DriveHealthInfo>();
        using var disks = new ManagementObjectSearcher(Scope, "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk");
        foreach (var d in disks.Get())
        {
            using (d)
            {
                var id = d["DeviceId"] as string ?? "";
                counters.TryGetValue(id, out var c);
                var media = Convert.ToInt32(d["MediaType"] ?? 0, CultureInfo.InvariantCulture) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Disk" };
                var bus = Convert.ToInt32(d["BusType"] ?? 0, CultureInfo.InvariantCulture) switch { 7 => "USB", 11 => "SATA", 17 => "NVMe", 8 => "RAID", 10 => "SAS", _ => "other" };
                var health = Convert.ToInt32(d["HealthStatus"] ?? 5, CultureInfo.InvariantCulture) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
                int? temp = Int(c, "Temperature"), tempMax = Int(c, "TemperatureMax"), wear = Int(c, "Wear");
                long? hours = Long(c, "PowerOnHours"), re = Long(c, "ReadErrorsUncorrected"), we = Long(c, "WriteErrorsUncorrected"), lat = Long(c, "ReadLatencyMax");

                var verdict = health != "Healthy" ? $"⚠ Windows reports this drive as {health}. Back up now."
                    : (re ?? 0) + (we ?? 0) > 0 ? "⚠ Uncorrected read/write errors recorded — back up and check the drive."
                    : wear is >= 90 ? "⚠ Drive is near the end of its rated write endurance."
                    : wear is >= 70 ? "Wear is getting high; plan a replacement."
                    : temp is > 0 && tempMax is > 0 && temp >= tempMax - 5 ? "Running close to its maximum temperature — improve airflow."
                    : c is null ? "Healthy (detailed counters need administrator rights or aren't reported by this drive)."
                    : "Healthy.";

                result.Add(new DriveHealthInfo(
                    d["FriendlyName"] as string ?? id, media, bus,
                    Convert.ToDouble(d["Size"] ?? 0UL, CultureInfo.InvariantCulture) / 1e9, health,
                    temp, tempMax, wear, hours, re, we, lat, verdict, id));
            }
        }
        foreach (var c in counters.Values) { (c as IDisposable)?.Dispose(); }
        return result;
    }

    private static int? Int(ManagementBaseObject? o, string p) => o?[p] is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;
    private static long? Long(ManagementBaseObject? o, string p) => o?[p] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
}
