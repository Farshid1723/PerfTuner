using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PerfTuner.App.SystemTools;

/// <summary>One fixed volume with a drive letter, as shown on the Cockpit disk view.</summary>
public sealed record VolumeDetail(
    string Letter,
    string Label,
    string FileSystem,
    string? FileSystemVersion,
    long ClusterBytes,
    long SizeBytes,
    long FreeBytes,
    string Health,
    bool? Dirty,
    bool IsWindows,
    bool HasPageFile,
    string? DiskId,
    string Media,
    bool External = false)
{
    public double UsedFraction => SizeBytes > 0 ? 1 - (double)FreeBytes / SizeBytes : 0;
    public double FreePercent => SizeBytes > 0 ? 100.0 * FreeBytes / SizeBytes : 0;
    public string Title => $"{Letter}:  {(Label.Length > 0 ? Label : (IsWindows ? "Windows" : "Local disk"))}";
    public string SpaceText => $"{Format.Bytes(FreeBytes)} free of {Format.Bytes(SizeBytes)}";
    public string FileSystemText => FileSystemVersion is null ? FileSystem : $"{FileSystem} {FileSystemVersion}";
    public string ClusterText => ClusterBytes > 0 ? Format.Bytes(ClusterBytes) + " clusters" : "";
    public string RoleText => string.Join(" · ", new[] { IsWindows ? "Windows" : null, HasPageFile ? "page file" : null, Media }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// Read-only disk information from documented interfaces: MSFT_Volume / MSFT_Partition (Storage Management WMI)
/// and the FSCTL_GET_NTFS_VOLUME_DATA / FSCTL_GET_REFS_VOLUME_DATA / FSCTL_IS_VOLUME_DIRTY volume controls.
/// Volume handles are opened only from a validated single drive letter (never from free text) and only with
/// FILE_READ_ATTRIBUTES; the version and dirty flag need administrator rights and are null otherwise.
/// </summary>
public static partial class DiskInspector
{
    private const string Scope = @"root\Microsoft\Windows\Storage";

    public static IReadOnlyList<VolumeDetail> ReadVolumes(IReadOnlyCollection<string> pageFileLetters, IReadOnlyList<DriveHealthInfo> disks)
    {
        ArgumentNullException.ThrowIfNull(pageFileLetters);
        ArgumentNullException.ThrowIfNull(disks);
        var windows = char.ToUpperInvariant(Environment.SystemDirectory[0]).ToString();
        var diskOfLetter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var parts = new ManagementObjectSearcher(Scope, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition");
            foreach (var o in parts.Get())
            {
                using (o)
                {
                    if (o["DriveLetter"] is char c && c != '\0')
                    {
                        diskOfLetter[c.ToString()] = Convert.ToString(o["DiskNumber"], CultureInfo.InvariantCulture) ?? "";
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }

        var result = new List<VolumeDetail>();
        using var searcher = new ManagementObjectSearcher(Scope,
            "SELECT DriveLetter, FileSystemLabel, FileSystem, Size, SizeRemaining, AllocationUnitSize, HealthStatus FROM MSFT_Volume WHERE DriveType = 3");
        foreach (var o in searcher.Get())
        {
            using (o)
            {
                if (o["DriveLetter"] is not char c || !char.IsAsciiLetter(c)) { continue; }
                var letter = char.ToUpperInvariant(c).ToString();
                var fs = o["FileSystem"] as string ?? "";
                if (fs.Length == 0 || fs.Equals("RAW", StringComparison.OrdinalIgnoreCase)) { continue; } // unformatted, damaged or locked (BitLocker)
                diskOfLetter.TryGetValue(letter, out var diskId);
                var disk = disks.FirstOrDefault(d => d.DeviceId == diskId);
                var media = disk?.Media ?? "";
                result.Add(new VolumeDetail(
                    letter,
                    o["FileSystemLabel"] as string ?? "",
                    fs,
                    FileSystemVersion(letter, fs),
                    Convert.ToInt64(o["AllocationUnitSize"] ?? 0L, CultureInfo.InvariantCulture),
                    Convert.ToInt64(o["Size"] ?? 0UL, CultureInfo.InvariantCulture),
                    Convert.ToInt64(o["SizeRemaining"] ?? 0UL, CultureInfo.InvariantCulture),
                    Convert.ToInt32(o["HealthStatus"] ?? 5, CultureInfo.InvariantCulture) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                    IsDirty(letter),
                    letter == windows,
                    pageFileLetters.Contains(letter, StringComparer.OrdinalIgnoreCase),
                    diskId,
                    media,
                    disk?.Bus == "USB"));
            }
        }
        return result.OrderBy(v => v.Letter, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Percent of the volume's files that are fragmented, from Win32_Volume.DefragAnalysis (root\cimv2; MSFT_Volume has
    /// no analysis method). Admin; can take a while on HDDs.
    /// </summary>
    public static int? AnalyzeFragmentation(string letter)
    {
        Validate(letter);
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", $"SELECT * FROM Win32_Volume WHERE DriveLetter = '{letter}:'");
        using var volume = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
        if (volume is null) { return null; }
        using var outParams = volume.InvokeMethod("DefragAnalysis", null, null);
        if (Convert.ToUInt32(outParams["ReturnValue"], CultureInfo.InvariantCulture) != 0) { return null; }
        if (outParams["DefragAnalysis"] is ManagementBaseObject analysis)
        {
            using (analysis)
            {
                return Convert.ToInt32(analysis["FilePercentFragmentation"] ?? 0, CultureInfo.InvariantCulture);
            }
        }
        return null;
    }

    /// <summary>Result of an online error scan or a scheduled repair.</summary>
    public enum RepairResult
    {
        NoErrorsFound,
        ErrorsFixed,
        ErrorsFoundNeedsOfflineFix,
        Scheduled,
        Failed,
    }

    /// <summary>
    /// MSFT_Volume.Repair. <paramref name="offline"/> = false: online scan (the drive stays usable).
    /// true: OfflineScanAndFix — Windows fixes the volume during the next restart.
    /// </summary>
    public static RepairResult Repair(string letter, bool offline)
    {
        using var volume = FindVolume(letter);
        if (volume is null) { return RepairResult.Failed; }
        using var inParams = volume.GetMethodParameters("Repair");
        if (offline) { inParams["OfflineScanAndFix"] = true; } else { inParams["Scan"] = true; }
        using var outParams = volume.InvokeMethod("Repair", inParams, null);
        var rc = Convert.ToUInt32(outParams["ReturnValue"], CultureInfo.InvariantCulture);
        if (rc != 0) { return RepairResult.Failed; }
        var output = outParams["Output"] is { } v ? Convert.ToUInt32(v, CultureInfo.InvariantCulture) : 0u;
        // Output: 0 no errors, 1 errors fixed, 2 errors found (needs offline fix / spot fix), 3 … scheduled.
        return offline ? RepairResult.Scheduled : output switch
        {
            0 => RepairResult.NoErrorsFound,
            1 => RepairResult.ErrorsFixed,
            _ => RepairResult.ErrorsFoundNeedsOfflineFix,
        };
    }

    private static ManagementObject? FindVolume(string letter)
    {
        Validate(letter);
        using var searcher = new ManagementObjectSearcher(Scope, $"SELECT * FROM MSFT_Volume WHERE DriveLetter = '{letter}'");
        return searcher.Get().OfType<ManagementObject>().FirstOrDefault();
    }

    private static void Validate(string letter)
    {
        if (letter is not { Length: 1 } || !char.IsAsciiLetterUpper(letter[0]))
        {
            throw new ArgumentException("invalid drive letter", nameof(letter));
        }
    }

    // ------------------------------------------------------------------ volume controls

    private const uint FileReadAttributes = 0x80;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint FsctlGetNtfsVolumeData = 0x00090064;
    private const uint FsctlGetRefsVolumeData = 0x000902D8;
    private const uint FsctlIsVolumeDirty = 0x00090078;

    private static string? FileSystemVersion(string letter, string fs)
    {
        if (!fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase) && !fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase)) { return null; }
        var ntfs = fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        var buffer = new byte[256];
        if (!Control(letter, ntfs ? FsctlGetNtfsVolumeData : FsctlGetRefsVolumeData, buffer, out var returned)) { return null; }
        if (ntfs)
        {
            // NTFS_VOLUME_DATA_BUFFER (96 bytes) is followed by NTFS_EXTENDED_VOLUME_DATA: ByteCount, MajorVersion, MinorVersion.
            if (returned < 104) { return null; }
            return $"{BitConverter.ToUInt16(buffer, 100)}.{BitConverter.ToUInt16(buffer, 102)}";
        }
        // REFS_VOLUME_DATA_BUFFER: ByteCount, MajorVersion, MinorVersion (DWORDs).
        return returned >= 12 ? $"{BitConverter.ToUInt32(buffer, 4)}.{BitConverter.ToUInt32(buffer, 8)}" : null;
    }

    private static bool? IsDirty(string letter)
    {
        var buffer = new byte[4];
        return Control(letter, FsctlIsVolumeDirty, buffer, out var returned) && returned >= 4 ? (BitConverter.ToUInt32(buffer, 0) & 1) != 0 : null;
    }

    private static bool Control(string letter, uint code, byte[] output, out uint returned)
    {
        returned = 0;
        Validate(letter);
        using var handle = CreateFileW(@"\\.\" + letter + ":", FileReadAttributes, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid) { return false; }
        return DeviceIoControl(handle, code, 0, 0, output, (uint)output.Length, out returned, 0);
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint inBuffer, uint inSize, [Out] byte[] outBuffer, uint outSize, out uint returned, nint overlapped);
}
