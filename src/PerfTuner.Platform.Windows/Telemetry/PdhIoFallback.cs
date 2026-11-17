using System.Runtime.InteropServices;
using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows.Telemetry;

public readonly record struct IoRates(double DiskReadBps, double DiskWriteBps, double NetRxBps, double NetTxBps);

/// <summary>
/// Fallback IO telemetry when a kernel ETW session cannot be started (non-elevated dev
/// run, policy, or session limit). System totals only — no per-process attribution.
/// One PDH query, four counters; the counter-array buffer is native, allocated once and
/// only grown when the adapter list grows, so polling does not allocate managed memory.
/// </summary>
public sealed class PdhIoFallback : IDisposable
{
    private readonly nint _query;
    private readonly nint _diskRead;
    private readonly nint _diskWrite;
    private readonly nint _netRx;
    private readonly nint _netTx;
    private nint _arrayBuffer;
    private uint _arrayBufferSize;
    private bool _disposed;

    public PdhIoFallback()
    {
        Check(Pdh.OpenQuery(0, 0, out _query), "PdhOpenQuery");
        Check(Pdh.AddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", 0, out _diskRead), "disk read counter");
        Check(Pdh.AddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", 0, out _diskWrite), "disk write counter");
        Check(Pdh.AddEnglishCounter(_query, @"\Network Interface(*)\Bytes Received/sec", 0, out _netRx), "net rx counter");
        Check(Pdh.AddEnglishCounter(_query, @"\Network Interface(*)\Bytes Sent/sec", 0, out _netTx), "net tx counter");
        _ = Pdh.PdhCollectQueryData(_query); // prime: rate counters need two samples
    }

    public IoRates Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Pdh.PdhCollectQueryData(_query) != Pdh.ErrorSuccess)
        {
            throw new InvalidOperationException("PdhCollectQueryData failed");
        }
        return new IoRates(ReadSingle(_diskRead), ReadSingle(_diskWrite), SumArray(_netRx), SumArray(_netTx));
    }

    private static double ReadSingle(nint counter)
    {
        var status = Pdh.PdhGetFormattedCounterValue(counter, Pdh.FmtDouble | Pdh.FmtNoCap100, out _, out var value);
        return status == Pdh.ErrorSuccess && value.CStatus is Pdh.CstatusValidData or Pdh.CstatusNewData
            ? Math.Max(0, value.DoubleValue)
            : 0;
    }

    private unsafe double SumArray(nint counter)
    {
        var size = _arrayBufferSize;
        var status = Pdh.GetFormattedCounterArray(counter, Pdh.FmtDouble | Pdh.FmtNoCap100, ref size, out var count, _arrayBuffer);
        if (status == Pdh.MoreData)
        {
            // Grow (rare: adapters added). Bounded to 1 MiB so a bad counter cannot exhaust memory.
            if (size > 1024 * 1024)
            {
                return 0;
            }
            if (_arrayBuffer != 0)
            {
                NativeMemory.Free((void*)_arrayBuffer);
            }
            _arrayBuffer = (nint)NativeMemory.Alloc(size);
            _arrayBufferSize = size;
            status = Pdh.GetFormattedCounterArray(counter, Pdh.FmtDouble | Pdh.FmtNoCap100, ref size, out count, _arrayBuffer);
        }
        if (status != Pdh.ErrorSuccess)
        {
            return 0;
        }

        double sum = 0;
        var items = new ReadOnlySpan<PdhFmtCounterValueItem>((void*)_arrayBuffer, (int)count);
        foreach (ref readonly var item in items)
        {
            if (item.Value.CStatus is Pdh.CstatusValidData or Pdh.CstatusNewData)
            {
                sum += Math.Max(0, item.Value.DoubleValue);
            }
        }
        return sum;
    }

    private static void Check(uint status, string what)
    {
        if (status != Pdh.ErrorSuccess)
        {
            throw new InvalidOperationException($"{what} failed (0x{status:X8})");
        }
    }

    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _ = Pdh.PdhCloseQuery(_query);
        if (_arrayBuffer != 0)
        {
            NativeMemory.Free((void*)_arrayBuffer);
            _arrayBuffer = 0;
        }
    }
}
