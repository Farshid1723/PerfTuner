using PerfTuner.Contracts;
using PerfTuner.Core.Telemetry;
using PerfTuner.Platform.Windows.Telemetry;

namespace PerfTuner.Service.Telemetry;

/// <summary>
/// Polled fallback: PDH rates are converted back to interval byte counts and fed into the
/// same accumulator, so the engine's aggregation code has exactly one path.
/// </summary>
internal sealed class PdhIoSource(IoAccumulator accumulator) : IIoSource
{
    private PdhIoFallback? _pdh;

    public string Name => "PDH fallback (system totals)";
    public SampleFlags Flags => SampleFlags.FallbackIo;
    public bool SupportsPerProcess => false;
    public bool Faulted => false;

    public void Start() => _pdh = new PdhIoFallback();

    public void Poll(double intervalMs)
    {
        if (_pdh is null)
        {
            return;
        }
        var r = _pdh.Sample();
        var seconds = intervalMs / 1000.0;
        accumulator.AddDiskRead(-1, (long)(r.DiskReadBps * seconds));
        accumulator.AddDiskWrite(-1, (long)(r.DiskWriteBps * seconds));
        accumulator.AddNetRx(-1, (long)(r.NetRxBps * seconds));
        accumulator.AddNetTx(-1, (long)(r.NetTxBps * seconds));
    }

    public void Dispose() => _pdh?.Dispose();
}
