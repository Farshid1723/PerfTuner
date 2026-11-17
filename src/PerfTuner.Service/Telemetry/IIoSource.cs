using PerfTuner.Contracts;

namespace PerfTuner.Service.Telemetry;

/// <summary>
/// A producer of disk/network IO data. Event-driven sources (ETW) push into the shared
/// <see cref="Core.Telemetry.IoAccumulator"/> continuously; polled sources do their work
/// in <see cref="Poll"/>, which the engine calls once per interval.
/// </summary>
internal interface IIoSource : IDisposable
{
    string Name { get; }

    SampleFlags Flags { get; }

    bool SupportsPerProcess { get; }

    /// <summary>True if the source died (e.g. ETW session stopped externally).</summary>
    bool Faulted { get; }

    void Start();

    /// <summary>Called once per interval with the measured interval length.</summary>
    void Poll(double intervalMs);
}
