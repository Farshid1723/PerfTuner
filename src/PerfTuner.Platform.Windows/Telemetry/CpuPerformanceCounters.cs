using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows.Telemetry;

public readonly record struct CpuPerformance(double BaseMhz, double PerformancePercent, double LimitPercent)
{
    public double EffectiveMhz => BaseMhz * PerformancePercent / 100.0;
}

/// <summary>
/// Real CPU clock and Windows' own performance-limit signal, from the documented
/// "Processor Information" counters:
/// <list type="bullet">
/// <item><c>Processor Frequency</c>: nominal (base) MHz.</item>
/// <item><c>% Processor Performance</c>: actual performance vs. nominal (turbo &gt; 100 %).</item>
/// <item><c>% Performance Limit</c>: below 100 % when the CPU is held back by power, thermal
///   or firmware limits — the signal <c>PROCESSOR_POWER_INFORMATION</c> no longer provides.</item>
/// </list>
/// One PDH query, three counters, sampled once per aggregation interval (~50 µs).
/// </summary>
public sealed class CpuPerformanceCounters : IDisposable
{
    private readonly nint _query;
    private readonly nint _frequency;
    private readonly nint _performance;
    private readonly nint _limit;
    private bool _disposed;

    public CpuPerformanceCounters()
    {
        Check(Pdh.OpenQuery(0, 0, out _query));
        Check(Pdh.AddEnglishCounter(_query, @"\Processor Information(_Total)\Processor Frequency", 0, out _frequency));
        Check(Pdh.AddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Performance", 0, out _performance));
        Check(Pdh.AddEnglishCounter(_query, @"\Processor Information(_Total)\% Performance Limit", 0, out _limit));
        _ = Pdh.PdhCollectQueryData(_query);
    }

    public CpuPerformance Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Pdh.PdhCollectQueryData(_query) != Pdh.ErrorSuccess)
        {
            return default;
        }
        return new CpuPerformance(Read(_frequency), Read(_performance), Read(_limit));
    }

    private static double Read(nint counter)
    {
        var status = Pdh.PdhGetFormattedCounterValue(counter, Pdh.FmtDouble | Pdh.FmtNoCap100, out _, out var value);
        return status == Pdh.ErrorSuccess && value.CStatus is Pdh.CstatusValidData or Pdh.CstatusNewData ? Math.Max(0, value.DoubleValue) : 0;
    }

    private static void Check(uint status)
    {
        if (status != Pdh.ErrorSuccess)
        {
            throw new InvalidOperationException($"PDH error 0x{status:X8}");
        }
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

/// <summary>
/// Maps kernel code addresses to driver file names using the documented PSAPI
/// <c>EnumDeviceDrivers</c> / <c>GetDeviceDriverBaseName</c> (needs admin / LocalSystem;
/// with KASLR, non-privileged callers get zero addresses). Each address is attributed to
/// the driver with the highest load base at or below it.
/// </summary>
public sealed unsafe class DriverAddressMap
{
    private readonly ulong[] _bases;
    private readonly string[] _names;

    private DriverAddressMap(ulong[] bases, string[] names)
    {
        _bases = bases;
        _names = names;
    }

    public int Count => _bases.Length;

    public static DriverAddressMap Load()
    {
        var addresses = new nint[2048];
        fixed (nint* p = addresses)
        {
            if (!DriverApi.EnumDeviceDrivers(p, (uint)(addresses.Length * sizeof(nint)), out var needed))
            {
                return new DriverAddressMap([], []);
            }
            var count = Math.Min(addresses.Length, (int)(needed / (uint)sizeof(nint)));
            var list = new List<(ulong Base, string Name)>(count);
            var name = stackalloc char[260];
            for (var i = 0; i < count; i++)
            {
                if (addresses[i] == 0) { continue; }
                var len = DriverApi.GetDeviceDriverBaseName(addresses[i], name, 260);
                list.Add(((ulong)addresses[i], len > 0 ? new string(name, 0, (int)len) : "unknown"));
            }
            list.Sort((a, b) => a.Base.CompareTo(b.Base));
            return new DriverAddressMap([.. list.Select(x => x.Base)], [.. list.Select(x => x.Name)]);
        }
    }

    public string Resolve(ulong address)
    {
        if (_bases.Length == 0 || address < _bases[0])
        {
            return "unknown";
        }
        var i = Array.BinarySearch(_bases, address);
        if (i < 0) { i = ~i - 1; }
        return _names[i];
    }
}
