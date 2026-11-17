namespace PerfTuner.Contracts;

/// <summary>Bit flags describing where a sample's data came from.</summary>
[Flags]
public enum SampleFlags
{
    None = 0,
    /// <summary>Disk/network numbers come from a kernel ETW session.</summary>
    EtwIo = 1,
    /// <summary>Disk/network numbers come from the PDH / IP-statistics fallback.</summary>
    FallbackIo = 2,
    /// <summary>Per-process attribution was switched off to protect the overhead budget.</summary>
    Degraded = 4,
    /// <summary>Per-process IO table overflowed this interval; totals are still exact.</summary>
    ProcessTableOverflow = 8,
}

/// <summary>
/// One aggregated interval of system telemetry. A plain unmanaged struct so it can
/// live in a preallocated ring buffer without GC pressure.
/// </summary>
public struct MetricSample
{
    public long TimestampUnixMs { get; set; }
    public int IntervalMs { get; set; }

    public double CpuPercent { get; set; }
    public double MemoryLoadPercent { get; set; }
    public double CommitPercent { get; set; }
    public long MemoryAvailableMb { get; set; }
    public long MemoryTotalMb { get; set; }

    public double DiskReadBytesPerSec { get; set; }
    public double DiskWriteBytesPerSec { get; set; }
    public double NetRxBytesPerSec { get; set; }
    public double NetTxBytesPerSec { get; set; }

    /// <summary>The service's own CPU use (percent of total machine capacity).</summary>
    public double ServiceCpuPercent { get; set; }
    public double ServicePrivateMb { get; set; }

    public SampleFlags Flags { get; set; }

    /// <summary>Effective CPU clock (base × % processor performance); 0 if unavailable.</summary>
    public double CpuEffectiveMhz { get; set; }

    /// <summary>Windows "% Performance Limit" (100 = not limited).</summary>
    public double CpuLimitPercent { get; set; }
}

/// <summary>Per-process IO in one interval (bytes per second).</summary>
public sealed record ProcessIo
{
    public required int Pid { get; init; }
    public required string Name { get; init; }
    public double DiskBytesPerSec { get; init; }
    public double NetBytesPerSec { get; init; }
}
