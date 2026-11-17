namespace PerfTuner.Contracts;

/// <summary>
/// Fixed protocol limits. Every bound here is enforced on both sides of the pipe;
/// changing one is a protocol change and must bump <see cref="ProtocolVersion"/>.
/// </summary>
public static class ProtocolConstants
{
    public const int ProtocolVersion = 5;

    /// <summary>Windows service name registered with the SCM.</summary>
    public const string ServiceName = "PerfTunerSvc";
    public const string ServiceDisplayName = "Win11 Performance Tuner Service";

    /// <summary>Production pipe (service running under the SCM).</summary>
    public const string PipeName = "PerfTuner.v1";

    /// <summary>Developer pipe (service started with --console). Separate name so a dev
    /// instance can never be mistaken for the installed service.</summary>
    /// <summary>Standalone engine (started by the GUI, runs as the user): the pipe name is this prefix + the user's SID, so every user gets their own.</summary>
    public const string StandalonePipePrefix = "PerfTuner.standalone.v1.";

    /// <summary>Client → server frames are tiny control messages.</summary>
    public const int MaxRequestFrameBytes = 16 * 1024;

    /// <summary>Server → client frames (a full history response is the largest).</summary>
    public const int MaxResponseFrameBytes = 256 * 1024;

    public const int FrameHeaderBytes = 4;
    public const int MaxJsonDepth = 6;

    public const int MaxClientNameLength = 64;
    public const int MaxHistoryPoints = 300;
    public const int MaxTopProcesses = 10;
    public const int MaxProcessNameLength = 64;
    public const int MaxErrorMessageLength = 256;

    public const int HandshakeTimeoutMs = 5_000;
    public const int MinAggregationIntervalMs = 500;
    public const int MaxAggregationIntervalMs = 2_000;

    public const int MaxProcessListCount = 300;
    public const int MaxRules = 32;
    public const int MinSoftCapMb = 64;
    public const int MaxSoftCapMb = 65_536;
    public const int MinAutoCalmThresholdPercent = 5;
    public const int MaxAutoCalmThresholdPercent = 100;
    public const int MaxHistoryEntries = 200;
    public const int MinLatencyScanSeconds = 5;
    public const int MaxLatencyScanSeconds = 60;
    public const int MaxTimelineEvents = 200;

    /// <summary>IO latency histogram: bucket i counts IOs in [2^i, 2^(i+1)) µs (1 µs … ~4.5 min).</summary>
    public const int LatencyHistogramBuckets = 28;
    public const int MinWaitAnalysisSeconds = 3;
    public const int MaxWaitAnalysisSeconds = 30;
}
