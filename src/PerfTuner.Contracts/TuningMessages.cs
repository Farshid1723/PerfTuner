namespace PerfTuner.Contracts;

// ---------------------------------------------------------------------------
// Tuning vocabulary (protocol v2). Every value the client can send is an enum or
// a bounded number — there is no free-form command, path, or script anywhere.
// ---------------------------------------------------------------------------

public enum ProcessAction
{
    SetPriority,
    EfficiencyMode,
    CpuCores,
    MemoryPriority,
    SoftMemoryCap,
    TrimMemory,
    EndProcess,
}

/// <summary>The only priority classes the engine will ever set (never High / Realtime).</summary>
public enum PriorityLevel
{
    BelowNormal,
    Normal,
    AboveNormal,
}

public enum CpuCorePreset
{
    EfficiencyCores,
    PerformanceCores,
    HalfCores,
}

public enum MemoryPriorityLevel
{
    Low,
    Normal,
}

public enum TuningMode
{
    Off,
    /// <summary>Boost the foreground app one step; efficiency mode for busy background apps.</summary>
    Focus,
    /// <summary>Efficiency mode for all busy background apps; no boosting.</summary>
    Quiet,
}

/// <summary>A persistent per-application rule, applied automatically when the app starts (Safe Mode off).</summary>
public sealed record AppRule
{
    /// <summary>Executable name without extension, e.g. "OneDrive". Allow-listed characters only.</summary>
    public required string ImageName { get; init; }
    public PriorityLevel? Priority { get; init; }
    public bool? EfficiencyMode { get; init; }
    public CpuCorePreset? Cores { get; init; }
    public MemoryPriorityLevel? MemoryPriority { get; init; }
}

// ------------------------------- requests ----------------------------------

public sealed record GetProcessesRequest : Request
{
    public required int MaxCount { get; init; }
}

/// <summary>
/// One action on one process. The target is identified by PID AND creation time, so a
/// request can never hit a different process that recycled the PID.
/// </summary>
public sealed record ProcessActionRequest : Request
{
    public required int Pid { get; init; }
    public required long CreateTime { get; init; }
    public required ProcessAction Action { get; init; }
    public PriorityLevel? Priority { get; init; }
    public bool? Enable { get; init; }
    public CpuCorePreset? Cores { get; init; }
    public MemoryPriorityLevel? MemoryPriority { get; init; }
    public int? CapMb { get; init; }

    /// <summary>Required (true) for irreversible actions (EndProcess).</summary>
    public bool? Confirm { get; init; }
}

public sealed record GetChangesRequest : Request;

public sealed record RevertChangeRequest : Request
{
    public required long ChangeId { get; init; }
}

public sealed record RevertAllRequest : Request;

public sealed record SetTuningOptionsRequest : Request
{
    public bool? SafeMode { get; init; }
    public TuningMode? Mode { get; init; }
    public bool? AutoCalm { get; init; }
    public int? AutoCalmThresholdPercent { get; init; }
}

public sealed record ResetBreakerRequest : Request;

/// <summary>Sent by the (elevated) GUI: the service in session 0 cannot see the user's foreground window.</summary>
public sealed record ReportForegroundRequest : Request
{
    public required int Pid { get; init; }
}

public sealed record GetRulesRequest : Request;

public sealed record SetRulesRequest : Request
{
    public required AppRule[] Rules { get; init; }
}

public sealed record TrimBackgroundMemoryRequest : Request;

// ------------------------------- responses ---------------------------------

public sealed record ProcessEntry
{
    public required int Pid { get; init; }
    public required long CreateTime { get; init; }
    public required string Name { get; init; }
    public required double CpuPercent { get; init; }
    public required double WorkingSetMb { get; init; }
    public required double PrivateMb { get; init; }
    public required string Priority { get; init; }
    public required int SessionId { get; init; }
    public required bool Controllable { get; init; }
    public string? Reason { get; init; }
    /// <summary>Comma-separated list of active changes on this process (empty if none).</summary>
    public required string Applied { get; init; }
}

public sealed record ProcessListResponse : Response
{
    public required ProcessEntry[] Processes { get; init; }
    public int? ForegroundPid { get; init; }
    public required int TotalProcesses { get; init; }
}

public sealed record ActionResultResponse : Response
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public long? ChangeId { get; init; }
}

public sealed record ChangeView
{
    public required long Id { get; init; }
    public required string Kind { get; init; }
    public required string Description { get; init; }
    public required string Owner { get; init; }
    public required long CreatedUnixMs { get; init; }
}

public sealed record HistoryEntry
{
    public required long TimeUnixMs { get; init; }
    public required string Message { get; init; }
}

public sealed record ChangesResponse : Response
{
    public required ChangeView[] Active { get; init; }
    public required HistoryEntry[] History { get; init; }
}

public sealed record RulesResponse : Response
{
    public required AppRule[] Rules { get; init; }
}
