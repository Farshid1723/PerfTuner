using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows.Settings;

/// <summary>How much a startup app cost: when it started after boot and the CPU / disk it used.</summary>
public sealed record StartupMeasurement(
    string ExeName,
    bool Running,
    double? StartedAfterBootSeconds,
    double? CpuSeconds,
    double? DiskReadMb,
    bool MeasuredNearBoot);

/// <summary>
/// Measures startup apps the way Task Manager's "startup impact" does — CPU time and disk
/// reads of the app's process — from documented process APIs (no tracing, read-only).
/// Only processes whose image name matches the startup command are considered; script and
/// generic hosts (rundll32, cmd, powershell …) are reported as not measurable.
/// </summary>
public static partial class StartupImpact
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32", "cmd", "powershell", "pwsh", "wscript", "cscript", "explorer", "conhost", "msiexec", "mshta", "regsvr32", "svchost",
    };

    /// <summary>Boot time, from the system uptime.</summary>
    public static DateTime BootTimeUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>The executable file name a startup command runs (without extension), or null.</summary>
    public static string? ExeName(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) { return null; }
        var c = command.Trim();
        string path;
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            path = end > 1 ? c[1..end] : c.Trim('"');
        }
        else
        {
            var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe > 0 ? c[..(exe + 4)] : c.Split(' ', 2)[0];
        }
        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public static bool IsHost(string? exeName) => exeName is null || Hosts.Contains(exeName);

    public static StartupMeasurement Measure(string command)
    {
        var exe = ExeName(command);
        if (IsHost(exe))
        {
            return new StartupMeasurement(exe ?? "?", false, null, null, null, false);
        }
        var boot = BootTimeUtc;
        Process[] procs;
        try { procs = Process.GetProcessesByName(exe); }
        catch (InvalidOperationException) { procs = []; }

        double? started = null, cpu = null, disk = null;
        try
        {
            foreach (var p in procs)
            {
                using var h = ProcessApi.OpenProcess(ProcessApi.QueryLimitedInformation, false, p.Id);
                if (h.IsInvalid || !ProcessApi.GetProcessTimes(h, out var created, out var exited, out var kernel, out var user) || exited != 0)
                {
                    continue;
                }
                var startUtc = DateTime.FromFileTimeUtc(created);
                var after = Math.Max(0, (startUtc - boot).TotalSeconds);
                // Earliest instance = the one that ran at startup; add the CPU / IO of all instances.
                started = started is null ? after : Math.Min(started.Value, after);
                cpu = (cpu ?? 0) + ((kernel + user) / 1e7);
                if (GetProcessIoCounters(h, out var io)) { disk = (disk ?? 0) + (io.ReadTransferCount / 1048576.0); }
            }
        }
        finally
        {
            foreach (var p in procs) { p.Dispose(); }
        }
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return new StartupMeasurement(exe!, started is not null, started, cpu, disk, uptime < TimeSpan.FromMinutes(15));
    }

    /// <summary>Task-Manager-like rating from CPU seconds and disk MB used while starting.</summary>
    public static string Rate(StartupMeasurement m) => m switch
    {
        { Running: false } => "Not running",
        { CpuSeconds: >= 2 } or { DiskReadMb: >= 30 } => "High",
        { CpuSeconds: >= 0.5 } or { DiskReadMb: >= 5 } => "Medium",
        _ => "Low",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
}
