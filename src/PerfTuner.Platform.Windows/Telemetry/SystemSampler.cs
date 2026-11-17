using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows.Telemetry;

public readonly record struct CpuMemorySample(
    double CpuPercent,
    double MemoryLoadPercent,
    double CommitPercent,
    long AvailableMb,
    long TotalMb);

public readonly record struct SelfSample(double CpuPercent, double PrivateMb);

/// <summary>
/// System CPU and memory via GetSystemTimes / GlobalMemoryStatusEx.
/// <para>
/// Why not ETW here: ETW exposes CPU as context-switch or sampled-profile events, which
/// at 1 kHz cost far more than one syscall per interval. These two calls are the same
/// data Task Manager uses, cost ~1 µs, and allocate nothing.
/// </para>
/// </summary>
public sealed class SystemSampler
{
    private long _lastIdle;
    private long _lastKernel;
    private long _lastUser;
    private bool _primed;

    public CpuMemorySample Sample()
    {
        double cpu = 0;
        if (Kernel32.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (_primed)
            {
                var idleDelta = idle - _lastIdle;
                var totalDelta = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle
                if (totalDelta > 0)
                {
                    cpu = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
                }
            }
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            _primed = true;
        }
        else
        {
            throw new InvalidOperationException("GetSystemTimes failed");
        }

        var mem = new MemoryStatusEx { Length = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<MemoryStatusEx>() };
        if (!Kernel32.GlobalMemoryStatusEx(ref mem))
        {
            throw new InvalidOperationException("GlobalMemoryStatusEx failed");
        }

        var commitPercent = mem.TotalPageFile == 0 ? 0 : 100.0 * (mem.TotalPageFile - mem.AvailPageFile) / mem.TotalPageFile;
        return new CpuMemorySample(
            cpu,
            mem.MemoryLoad,
            commitPercent,
            (long)(mem.AvailPhys / (1024 * 1024)),
            (long)(mem.TotalPhys / (1024 * 1024)));
    }
}

/// <summary>Measures this process's own overhead so budgets are verified continuously in production.</summary>
public sealed class SelfSampler
{
    private readonly nint _process = Kernel32.GetCurrentProcess(); // pseudo-handle, never closed
    private readonly int _processors = Environment.ProcessorCount;
    private long _lastCpu100ns;
    private long _lastWallTicks;
    private bool _primed;

    public SelfSample Sample()
    {
        double cpu = 0;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Kernel32.GetProcessTimes(_process, out _, out _, out var kernel, out var user))
        {
            var total = kernel + user;
            if (_primed)
            {
                var wall100ns = (now - _lastWallTicks) * 10_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (wall100ns > 0)
                {
                    cpu = Math.Clamp(100.0 * (total - _lastCpu100ns) / (wall100ns * _processors), 0, 100);
                }
            }
            _lastCpu100ns = total;
            _lastWallTicks = now;
            _primed = true;
        }

        var counters = new ProcessMemoryCountersEx { Cb = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<ProcessMemoryCountersEx>() };
        var privateMb = Kernel32.GetProcessMemoryInfo(_process, ref counters, counters.Cb)
            ? counters.PrivateUsage / (1024.0 * 1024.0)
            : 0;
        return new SelfSample(cpu, privateMb);
    }
}
