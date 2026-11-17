namespace PerfTuner.Service.Telemetry;

/// <summary>Receives per-event forensic data from the ETW consumer thread. Implementations must be allocation-free and fast.</summary>
internal interface IForensicsSink
{
    void DiskIo(int pid, bool write, int bytes, double elapsedMs, double deviceMs);

    void Network(long unixMs, int bytes, bool receive, int pid);

    void HardFault(int pid);

    void Retransmit();
}
