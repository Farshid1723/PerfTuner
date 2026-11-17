namespace PerfTuner.Contracts;

// ---------------------------------------------------------------------------
// Protocol v4: forensic diagnosis (F1/F2), autonomous safe tuning (F3/F4/F5),
// extreme visibility (F6/F7/F8/F9).
// ---------------------------------------------------------------------------

public enum Subsystem { Cpu, Scheduler, Memory, Disk, Network, Power, Driver, Tuning, Telemetry }

public enum Severity { Info, Warning, Critical }

/// <summary>Safe = manual only · Balanced = + automation and memory governor · Advanced = + autonomous experiments.</summary>
public enum TuningProfile { Safe, Balanced, Advanced }

public sealed record TimelineEvent
{
    public required long Id { get; init; }
    public required long TimeUnixMs { get; init; }
    public required Subsystem Subsystem { get; init; }
    public required Severity Severity { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public int? Pid { get; init; }
    public string? ProcessName { get; init; }
    /// <summary>True for events the stall detector classifies as a user-visible stall.</summary>
    public required bool IsStall { get; init; }
}

public sealed record GetTimelineRequest : Request
{
    public required long SinceUnixMs { get; init; }
    public required int MaxEvents { get; init; }
}

public sealed record TimelineResponse : Response
{
    public required TimelineEvent[] Events { get; init; }
    public required int StallsLastHour { get; init; }
}

public sealed record ExplainStallRequest : Request
{
    public required long EventId { get; init; }
}

public sealed record CauseFinding
{
    public required string Title { get; init; }
    public required Subsystem Subsystem { get; init; }
    /// <summary>0..1, from the strength and temporal overlap of the evidence.</summary>
    public required double Confidence { get; init; }
    public required string[] Evidence { get; init; }
    public string? SuggestedContractId { get; init; }
    public int? SuggestedPid { get; init; }
    public long? SuggestedCreateTime { get; init; }
    public string? SuggestedProcessName { get; init; }
}

public sealed record ExplanationResponse : Response
{
    public required bool Found { get; init; }
    public required string Story { get; init; }
    public required long WindowStartUnixMs { get; init; }
    public required long WindowEndUnixMs { get; init; }
    public required CauseFinding[] Causes { get; init; }
    public required TimelineEvent[] Related { get; init; }
}

// ------------------------------------------------------------ F6 / F7 visibility

public sealed record LatencyPercentiles
{
    public required long Count { get; init; }
    public required double P50Ms { get; init; }
    public required double P90Ms { get; init; }
    public required double P99Ms { get; init; }
    public required double MaxMs { get; init; }
}

public sealed record ProcessIoLatency
{
    public required int Pid { get; init; }
    public required string Name { get; init; }
    public required long Reads { get; init; }
    public required long Writes { get; init; }
    public required double MegabytesTransferred { get; init; }
    public required LatencyPercentiles Total { get; init; }
}

public sealed record IoLatencyResponse : Response
{
    public required bool Available { get; init; }
    public required string Message { get; init; }
    public required int WindowSeconds { get; init; }
    public required LatencyPercentiles ReadTotal { get; init; }
    public required LatencyPercentiles WriteTotal { get; init; }
    /// <summary>Time spent waiting in the OS/driver queue (total − device service time).</summary>
    public required LatencyPercentiles Queue { get; init; }
    /// <summary>Time the device itself took.</summary>
    public required LatencyPercentiles Device { get; init; }
    /// <summary>Log2 histogram of total latency: bucket i counts IOs in [2^i, 2^(i+1)) µs.</summary>
    public required long[] Histogram { get; init; }
    public required double DiskQueueLength { get; init; }
    public required ProcessIoLatency[] Processes { get; init; }
}

public sealed record GetIoLatencyRequest : Request;

public sealed record NetworkBurst
{
    public required long TimeUnixMs { get; init; }
    public required string Direction { get; init; }
    public required double MegabitsPerSecond { get; init; }
    public required double BaselineMegabitsPerSecond { get; init; }
    public required int DurationMs { get; init; }
    public int? Pid { get; init; }
    public string? ProcessName { get; init; }
}

public sealed record NetworkHealthResponse : Response
{
    public required bool Available { get; init; }
    public required string Message { get; init; }
    public required double RxMbpsNow { get; init; }
    public required double TxMbpsNow { get; init; }
    public required double PeakRxMbps100ms { get; init; }
    public required double PeakTxMbps100ms { get; init; }
    public required long RetransmitsLastMinute { get; init; }
    public required double RetransmitRatePercent { get; init; }
    public required string Health { get; init; }
    public required NetworkBurst[] Bursts { get; init; }
    /// <summary>Last 60 s at 100 ms resolution (Mbit/s), for the burst chart.</summary>
    public required double[] RxSeries { get; init; }
    public required double[] TxSeries { get; init; }
}

public sealed record GetNetworkHealthRequest : Request;

// ------------------------------------------------------------ F8 wait-state lens

public sealed record StartWaitAnalysisRequest : Request
{
    public required int Seconds { get; init; }
}

public sealed record GetWaitAnalysisRequest : Request;

public sealed record ProcessWaitProfile
{
    public required int Pid { get; init; }
    public required string Name { get; init; }
    public required double RunningMs { get; init; }
    /// <summary>Runnable but waiting for a CPU (CPU contention).</summary>
    public required double ReadyMs { get; init; }
    public required double BlockedIoMs { get; init; }
    public required double BlockedPagingMs { get; init; }
    public required double BlockedSyncMs { get; init; }
    public required double IdleWaitMs { get; init; }
    public required string Diagnosis { get; init; }
}

public sealed record WaitAnalysisResponse : Response
{
    public required LatencyScanState State { get; init; }
    public required string Message { get; init; }
    public required int Seconds { get; init; }
    public required double SystemReadyPercent { get; init; }
    public required string Summary { get; init; }
    public required ProcessWaitProfile[] Processes { get; init; }
}

// ------------------------------------------------------------ F3 memory governor

public enum MemoryBehavior { Stable, AllocationBurst, WorkingSetChurn, Pressure, Thrashing }

public sealed record MemoryGovernorResponse : Response
{
    public required MemoryBehavior Behavior { get; init; }
    public required string Explanation { get; init; }
    public required string Decision { get; init; }
    public required double AvailableMb { get; init; }
    public required double CommitPercent { get; init; }
    public required double HardFaultsPerSec { get; init; }
    public required double SoftFaultsPerSec { get; init; }
    public required double CommitGrowthMbPerMin { get; init; }
    public required bool GovernorActive { get; init; }
    public required string[] History { get; init; }
}

public sealed record GetMemoryGovernorRequest : Request;

// ------------------------------------------------------------ F5 contracts

public sealed record TuningContractView
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Scope { get; init; }
    public required string Bounds { get; init; }
    public required string RateLimit { get; init; }
    public required string Reversibility { get; init; }
    public required string[] AbortConditions { get; init; }
    public required string ExpectedOutcome { get; init; }
    public required TuningProfile MinimumProfile { get; init; }
    public required bool Experimentable { get; init; }
}

public sealed record GetContractsRequest : Request;

public sealed record ContractsResponse : Response
{
    public required TuningProfile ActiveProfile { get; init; }
    public required TuningContractView[] Contracts { get; init; }
}

public sealed record SetProfileRequest : Request
{
    public required TuningProfile Profile { get; init; }
}

// ------------------------------------------------------------ F2 / F4 regressions & experiments

public sealed record Suggestion
{
    public required string ContractId { get; init; }
    public required int Pid { get; init; }
    public required long CreateTime { get; init; }
    public required string ProcessName { get; init; }
    public required string Description { get; init; }
}

public sealed record RegressionItem
{
    public required string Kpi { get; init; }
    public required Subsystem Subsystem { get; init; }
    public required double Baseline { get; init; }
    public required double Current { get; init; }
    public required double ChangePercent { get; init; }
    public required string Unit { get; init; }
    public required string[] SuspectedCauses { get; init; }
    public required Suggestion[] Suggestions { get; init; }
    public required long DetectedUnixMs { get; init; }
}

public sealed record GetRegressionsRequest : Request;

public sealed record RegressionsResponse : Response
{
    public required double BaselineHours { get; init; }
    public required RegressionItem[] Items { get; init; }
    public required string Summary { get; init; }
}

public enum ExperimentState { Baseline, Treatment, Accepted, RolledBack, Aborted }

public sealed record KpiValue
{
    public required string Name { get; init; }
    public required double Before { get; init; }
    public required double After { get; init; }
    public required string Unit { get; init; }
    public required bool LowerIsBetter { get; init; }
}

public sealed record ExperimentRecord
{
    public required long Id { get; init; }
    public required string ContractId { get; init; }
    public required string Target { get; init; }
    public required ExperimentState State { get; init; }
    public required long StartedUnixMs { get; init; }
    public long? FinishedUnixMs { get; init; }
    public required KpiValue[] Kpis { get; init; }
    public required string Decision { get; init; }
    public required bool Autonomous { get; init; }
}

public sealed record StartExperimentRequest : Request
{
    public required string ContractId { get; init; }
    public required int Pid { get; init; }
    public required long CreateTime { get; init; }
}

public sealed record GetExperimentsRequest : Request;

public sealed record ExperimentsResponse : Response
{
    public ExperimentRecord? Current { get; init; }
    public required ExperimentRecord[] History { get; init; }
}
