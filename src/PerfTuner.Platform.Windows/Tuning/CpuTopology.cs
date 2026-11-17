using PerfTuner.Contracts;
using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows.Tuning;

/// <summary>
/// CPU Set ids grouped by core type (GetSystemCpuSetInformation). On hybrid CPUs
/// (Intel 12th gen+), P-cores report a higher EfficiencyClass than E-cores.
/// CPU Sets are the modern, soft form of affinity: Windows treats them as the
/// process's default placement and still honours thread-level overrides.
/// </summary>
public sealed class CpuTopology
{
    private CpuTopology(uint[] all, uint[] efficiency, uint[] performance, bool hybrid)
    {
        All = all;
        Efficiency = efficiency;
        Performance = performance;
        IsHybrid = hybrid;
    }

    public uint[] All { get; }
    public uint[] Efficiency { get; }
    public uint[] Performance { get; }
    public bool IsHybrid { get; }

    public static unsafe CpuTopology Detect()
    {
        ProcessApi.GetSystemCpuSetInformation(null, 0, out var needed, 0, 0);
        if (needed == 0 || needed > 1024 * 1024)
        {
            return new CpuTopology([], [], [], false);
        }
        var buffer = new byte[needed];
        var entries = new List<(uint Id, byte Class, byte Logical)>();
        fixed (byte* p = buffer)
        {
            if (!ProcessApi.GetSystemCpuSetInformation(p, needed, out var returned, 0, 0))
            {
                return new CpuTopology([], [], [], false);
            }
            var offset = 0u;
            while (offset + 8 <= returned)
            {
                var size = *(uint*)(p + offset);
                var type = *(uint*)(p + offset + 4);
                if (size == 0) { break; }
                if (type == 0 && size >= 32) // CpuSetInformation
                {
                    var id = *(uint*)(p + offset + 8);
                    var logical = *(p + offset + 14);
                    var efficiencyClass = *(p + offset + 18);
                    entries.Add((id, efficiencyClass, logical));
                }
                offset += size;
            }
        }

        var all = entries.Select(e => e.Id).ToArray();
        if (entries.Count == 0)
        {
            return new CpuTopology([], [], [], false);
        }
        var min = entries.Min(e => e.Class);
        var max = entries.Max(e => e.Class);
        var eff = entries.Where(e => e.Class == min).Select(e => e.Id).ToArray();
        var perf = entries.Where(e => e.Class == max).Select(e => e.Id).ToArray();
        return new CpuTopology(all, eff, perf, min != max);
    }

    /// <summary>CPU set ids for a preset, or an empty array if the preset is not applicable.</summary>
    public uint[] Resolve(CpuCorePreset preset)
    {
        uint[] ids = preset switch
        {
            CpuCorePreset.EfficiencyCores when IsHybrid => Efficiency,
            CpuCorePreset.PerformanceCores when IsHybrid => Performance,
            // Second half of the logical processors: keeps CPU 0 (interrupt-heavy) free.
            CpuCorePreset.HalfCores => All[(All.Length / 2)..],
            _ => [],
        };
        // Safety bound: never confine a process to fewer than 2 logical processors.
        return ids.Length >= 2 ? ids : [];
    }
}
