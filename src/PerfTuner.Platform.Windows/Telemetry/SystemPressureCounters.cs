using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows.Telemetry;

public readonly record struct PressureSample(double HardFaultPagesPerSec, double PageFaultsPerSec, double DiskQueueLength);

/// <summary>
/// Paging and disk-queue counters for the memory governor (F3) and stall detector (F1):
/// Memory\Pages Input/sec (pages read from disk to resolve hard faults), Memory\Page Faults/sec
/// (hard + soft) and PhysicalDisk(_Total)\Current Disk Queue Length. PDH is used here because
/// these are system-wide rates maintained by the kernel anyway; one query, ~50 µs per interval.
/// </summary>
public sealed class SystemPressureCounters : IDisposable
{
    private readonly nint _query;
    private readonly nint _pagesInput;
    private readonly nint _pageFaults;
    private readonly nint _queue;
    private bool _disposed;

    public SystemPressureCounters()
    {
        Check(Pdh.OpenQuery(0, 0, out _query));
        Check(Pdh.AddEnglishCounter(_query, @"\Memory\Pages Input/sec", 0, out _pagesInput));
        Check(Pdh.AddEnglishCounter(_query, @"\Memory\Page Faults/sec", 0, out _pageFaults));
        Check(Pdh.AddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Current Disk Queue Length", 0, out _queue));
        _ = Pdh.PdhCollectQueryData(_query);
    }

    public PressureSample Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Pdh.PdhCollectQueryData(_query) != Pdh.ErrorSuccess
            ? default
            : new PressureSample(Read(_pagesInput), Read(_pageFaults), Read(_queue));
    }

    private static double Read(nint counter)
    {
        var status = Pdh.PdhGetFormattedCounterValue(counter, Pdh.FmtDouble | Pdh.FmtNoCap100, out _, out var value);
        return status == Pdh.ErrorSuccess && value.CStatus is Pdh.CstatusValidData or Pdh.CstatusNewData ? Math.Max(0, value.DoubleValue) : 0;
    }

    private static void Check(uint status)
    {
        if (status != Pdh.ErrorSuccess) { throw new InvalidOperationException($"PDH error 0x{status:X8}"); }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _ = Pdh.PdhCloseQuery(_query);
        }
    }
}
