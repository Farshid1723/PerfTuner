using System.Management;

namespace PerfTuner.App.SystemTools;

public sealed record VolumeInfo(string Letter, string Label, double SizeGb, double FreeGb)
{
    public string Display => $"{Letter}:  {(Label.Length > 0 ? Label + "  " : "")}({FreeGb:F0} GB free of {SizeGb:F0} GB)";
}

/// <summary>
/// "Optimize drives" through the documented Windows Storage Management API (MSFT_Volume.Optimize):
/// Windows picks the right operation per media type — TRIM for SSDs, defragmentation for HDDs —
/// exactly like the built-in Optimize Drives tool. Requires administrator rights.
/// </summary>
public static class DriveOptimizer
{
    private const string Scope = @"root\Microsoft\Windows\Storage";

    public static IReadOnlyList<VolumeInfo> ListFixedVolumes()
    {
        var result = new List<VolumeInfo>();
        using var searcher = new ManagementObjectSearcher(Scope,
            "SELECT DriveLetter, FileSystemLabel, Size, SizeRemaining FROM MSFT_Volume WHERE DriveType = 3");
        foreach (var obj in searcher.Get())
        {
            using (obj)
            {
                if (obj["DriveLetter"] is char letter && letter != '\0')
                {
                    var size = Convert.ToDouble(obj["Size"] ?? 0UL, System.Globalization.CultureInfo.InvariantCulture) / 1e9;
                    var free = Convert.ToDouble(obj["SizeRemaining"] ?? 0UL, System.Globalization.CultureInfo.InvariantCulture) / 1e9;
                    result.Add(new VolumeInfo(letter.ToString(), obj["FileSystemLabel"] as string ?? "", size, free));
                }
            }
        }
        return result.OrderBy(v => v.Letter, StringComparer.Ordinal).ToList();
    }

    /// <summary>Runs the default optimization for the volume's media type. Can take minutes on HDDs.</summary>
    public static string Optimize(string letter)
    {
        if (letter.Length != 1 || !char.IsAsciiLetterUpper(letter[0]))
        {
            throw new ArgumentException("invalid drive letter", nameof(letter));
        }
        using var searcher = new ManagementObjectSearcher(Scope, $"SELECT * FROM MSFT_Volume WHERE DriveLetter = '{letter}'");
        foreach (ManagementObject volume in searcher.Get())
        {
            using (volume)
            {
                using var inParams = volume.GetMethodParameters("Optimize");
                using var outParams = volume.InvokeMethod("Optimize", inParams, null);
                var rc = Convert.ToUInt32(outParams["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
                return rc == 0 ? $"Drive {letter}: optimized." : $"Drive {letter}: optimization returned code {rc}.";
            }
        }
        return $"Drive {letter}: not found.";
    }
}
