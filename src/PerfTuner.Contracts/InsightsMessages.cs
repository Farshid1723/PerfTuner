namespace PerfTuner.Contracts;

// ---------------------------------------------------------------------------
// Protocol v3: diagnostics ("Insights"). All read-only except starting a
// latency scan (Admin), which only records a short kernel trace.
// ---------------------------------------------------------------------------

public sealed record GetInsightsRequest : Request;

/// <summary>Starts a DPC/ISR driver-latency capture of the given length (Admin).</summary>
public sealed record StartLatencyScanRequest : Request
{
    public required int Seconds { get; init; }
}

public sealed record GetBootReportRequest : Request;

public sealed record CpuClockInfo
{
    public required double BaseMhz { get; init; }
    public required double EffectiveMhz { get; init; }
    /// <summary>Current performance vs. nominal (can exceed 100 % with turbo).</summary>
    public required double PerformancePercent { get; init; }
    /// <summary>Windows "% Performance Limit": below 100 = power/thermal/firmware limit active.</summary>
    public required double LimitPercent { get; init; }
    /// <summary>Share of the last 10 minutes during which the CPU was limited.</summary>
    public required double LimitedShareRecentPercent { get; init; }
    public required bool Available { get; init; }
}

public sealed record LeakSuspect
{
    public required int Pid { get; init; }
    public required string Name { get; init; }
    public required double PrivateMb { get; init; }
    public required double GrowthMbPerHour { get; init; }
    public required double ObservedMinutes { get; init; }
    /// <summary>Goodness of fit of the linear trend (R², 0..1).</summary>
    public required double Confidence { get; init; }
}

public enum LatencyScanState
{
    Idle,
    Running,
    Completed,
    Failed,
}

public sealed record DriverLatency
{
    public required string Driver { get; init; }
    public required long DpcCount { get; init; }
    public required long IsrCount { get; init; }
    public required double MaxDpcUs { get; init; }
    public required double MaxIsrUs { get; init; }
    public required double TotalMs { get; init; }
}

public sealed record LatencyReport
{
    public required LatencyScanState State { get; init; }
    public required string Message { get; init; }
    public required long StartedUnixMs { get; init; }
    public required int Seconds { get; init; }
    public required double WorstUs { get; init; }
    public required string Verdict { get; init; }
    public required DriverLatency[] Drivers { get; init; }
}

public sealed record InsightsResponse : Response
{
    public required CpuClockInfo Clock { get; init; }
    public required LeakSuspect[] Leaks { get; init; }
    public required double LeakObservationMinutes { get; init; }
    public required int TrackedProcesses { get; init; }
    public required LatencyReport Latency { get; init; }
}

public sealed record BootRecord
{
    public required long TimeUnixMs { get; init; }
    /// <summary>"boot", "shutdown" or "resume".</summary>
    public required string Kind { get; init; }
    public required double TotalSeconds { get; init; }
    public double MainPathSeconds { get; init; }
    public double PostBootSeconds { get; init; }
}

public sealed record BootCulprit
{
    public required string Name { get; init; }
    /// <summary>"app", "driver", "service", "device", "system" …</summary>
    public required string Kind { get; init; }
    public required string Phase { get; init; }
    public required int Occurrences { get; init; }
    public required double AvgDelayMs { get; init; }
    public required double MaxDelayMs { get; init; }
}

public sealed record BootReportResponse : Response
{
    public required bool Available { get; init; }
    public required string Message { get; init; }
    public required BootRecord[] Records { get; init; }
    public required BootCulprit[] Culprits { get; init; }
}
