using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PerfTuner.Contracts;

namespace PerfTuner.Platform.Windows;

/// <summary>Pipe names shared by the standalone engine, the GUI and the CLI.</summary>
public static class PipeNames
{
    /// <summary>The standalone engine's pipe for the current user (one per user; DACL = that user + SYSTEM).</summary>
    public static string Standalone()
        => ProtocolConstants.StandalonePipePrefix + (WindowsIdentity.GetCurrent().User?.Value ?? "unknown");

    public static bool CurrentProcessElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>
/// Runs the backend engine without installing anything: the GUI starts the bundled
/// <c>Service\PerfTuner.Service.exe --console</c> as a hidden child with the GUI's own rights
/// (elevated GUI ⇒ full kernel-ETW features). The child is placed in a kill-on-close job object,
/// so it can never outlive the GUI; a normal exit asks it to stop gracefully first (rollback),
/// and a crash is covered by the engine's persisted journal (replayed on next start).
/// </summary>
public sealed partial class StandaloneEngine : IDisposable
{
    private readonly SafeFileHandle _job;
    private Process? _process;

    private StandaloneEngine(SafeFileHandle job) => _job = job;

    public static string EnginePath => Path.Combine(AppContext.BaseDirectory, "Service", "PerfTuner.Service.exe");

    public static bool IsAvailable => File.Exists(EnginePath);

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Starts the engine (if not already running for this user). Returns null when the engine binary is missing.</summary>
    public static StandaloneEngine? Start()
    {
        if (!IsAvailable)
        {
            return null;
        }
        var job = CreateJobObjectW(0, null);
        if (job.IsInvalid)
        {
            throw new InvalidOperationException($"CreateJobObject failed ({Marshal.GetLastPInvokeError()})");
        }
        var info = new JobExtendedLimitInformation { BasicLimitInformation = new JobBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
        {
            job.Dispose();
            throw new InvalidOperationException($"SetInformationJobObject failed ({Marshal.GetLastPInvokeError()})");
        }

        var engine = new StandaloneEngine(job);
        engine.Launch();
        return engine;
    }

    private void Launch()
    {
        var psi = new ProcessStartInfo(EnginePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(EnginePath)!,
        };
        psi.ArgumentList.Add("--console");
        var p = Process.Start(psi) ?? throw new InvalidOperationException("engine did not start");
        if (!AssignProcessToJobObject(_job, p.SafeHandle))
        {
            // Without the job the engine could outlive us; don't run it unsupervised.
            try { p.Kill(); } catch (InvalidOperationException) { }
            throw new InvalidOperationException($"AssignProcessToJobObject failed ({Marshal.GetLastPInvokeError()})");
        }
        _process = p;
    }

    /// <summary>Restarts the engine if it exited unexpectedly.</summary>
    public void EnsureRunning()
    {
        if (_process is null || _process.HasExited)
        {
            _process?.Dispose();
            Launch();
        }
    }

    /// <summary>Waits for a graceful exit (after a shutdown request) — then the job kills whatever is left.</summary>
    public bool WaitForExit(TimeSpan timeout) => _process is null || _process.WaitForExit(timeout);

    public void Dispose()
    {
        _job.Dispose(); // KILL_ON_JOB_CLOSE: the engine cannot outlive the GUI
        _process?.Dispose();
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
