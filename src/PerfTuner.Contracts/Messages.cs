using System.Text.Json.Serialization;

namespace PerfTuner.Contracts;

// ---------------------------------------------------------------------------
// Requests (client → service). The "type" discriminator selects the concrete
// record; unknown discriminators, unknown properties, missing required
// properties and duplicate properties are all rejected before any handler runs.
// ---------------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloRequest), "hello")]
[JsonDerivedType(typeof(PingRequest), "ping")]
[JsonDerivedType(typeof(GetStatusRequest), "getStatus")]
[JsonDerivedType(typeof(SubscribeMetricsRequest), "subscribeMetrics")]
[JsonDerivedType(typeof(GetHistoryRequest), "getHistory")]
[JsonDerivedType(typeof(GetConfigRequest), "getConfig")]
[JsonDerivedType(typeof(SetMonitoringOptionsRequest), "setMonitoringOptions")]
[JsonDerivedType(typeof(GetProcessesRequest), "getProcesses")]
[JsonDerivedType(typeof(ProcessActionRequest), "processAction")]
[JsonDerivedType(typeof(GetChangesRequest), "getChanges")]
[JsonDerivedType(typeof(RevertChangeRequest), "revertChange")]
[JsonDerivedType(typeof(RevertAllRequest), "revertAll")]
[JsonDerivedType(typeof(SetTuningOptionsRequest), "setTuningOptions")]
[JsonDerivedType(typeof(ResetBreakerRequest), "resetBreaker")]
[JsonDerivedType(typeof(ReportForegroundRequest), "reportForeground")]
[JsonDerivedType(typeof(GetRulesRequest), "getRules")]
[JsonDerivedType(typeof(SetRulesRequest), "setRules")]
[JsonDerivedType(typeof(TrimBackgroundMemoryRequest), "trimBackgroundMemory")]
[JsonDerivedType(typeof(GetInsightsRequest), "getInsights")]
[JsonDerivedType(typeof(StartLatencyScanRequest), "startLatencyScan")]
[JsonDerivedType(typeof(GetBootReportRequest), "getBootReport")]
[JsonDerivedType(typeof(GetTimelineRequest), "getTimeline")]
[JsonDerivedType(typeof(ExplainStallRequest), "explainStall")]
[JsonDerivedType(typeof(GetIoLatencyRequest), "getIoLatency")]
[JsonDerivedType(typeof(GetNetworkHealthRequest), "getNetworkHealth")]
[JsonDerivedType(typeof(StartWaitAnalysisRequest), "startWaitAnalysis")]
[JsonDerivedType(typeof(GetWaitAnalysisRequest), "getWaitAnalysis")]
[JsonDerivedType(typeof(GetMemoryGovernorRequest), "getMemoryGovernor")]
[JsonDerivedType(typeof(GetContractsRequest), "getContracts")]
[JsonDerivedType(typeof(SetProfileRequest), "setProfile")]
[JsonDerivedType(typeof(GetRegressionsRequest), "getRegressions")]
[JsonDerivedType(typeof(StartExperimentRequest), "startExperiment")]
[JsonDerivedType(typeof(GetExperimentsRequest), "getExperiments")]
[JsonDerivedType(typeof(ShutdownEngineRequest), "shutdownEngine")]
public abstract record Request
{
    /// <summary>Client-chosen correlation id, echoed in the response. Must be non-zero.</summary>
    public required uint Id { get; init; }
}

/// <summary>Must be the first message on every connection.</summary>
public sealed record HelloRequest : Request
{
    public required int ProtocolVersion { get; init; }
    public required string ClientName { get; init; }
}

public sealed record PingRequest : Request;

/// <summary>v5: asks a standalone engine (started by the GUI) to stop gracefully — every change is rolled back first. The installed service refuses it.</summary>
public sealed record ShutdownEngineRequest : Request;

public sealed record GetStatusRequest : Request;

/// <summary>Start/stop the server-push metric stream for this connection.</summary>
public sealed record SubscribeMetricsRequest : Request
{
    public required bool Enable { get; init; }
}

public sealed record GetHistoryRequest : Request
{
    public required int MaxPoints { get; init; }
}

public sealed record GetConfigRequest : Request;

/// <summary>
/// Safe monitoring toggles (Milestone 1's only mutating request). Admin role required.
/// Null fields mean "leave unchanged". Changes are runtime-only (not persisted).
/// </summary>
public sealed record SetMonitoringOptionsRequest : Request
{
    public bool? EnablePerProcessIo { get; init; }
    public int? AggregationIntervalMs { get; init; }
}

// ---------------------------------------------------------------------------
// Responses (service → client). Id echoes the request; pushes use Id = 0.
// ---------------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloAck), "helloAck")]
[JsonDerivedType(typeof(PongResponse), "pong")]
[JsonDerivedType(typeof(StatusResponse), "status")]
[JsonDerivedType(typeof(MetricsPush), "metrics")]
[JsonDerivedType(typeof(HistoryResponse), "history")]
[JsonDerivedType(typeof(ConfigResponse), "config")]
[JsonDerivedType(typeof(AckResponse), "ack")]
[JsonDerivedType(typeof(ErrorResponse), "error")]
[JsonDerivedType(typeof(ProcessListResponse), "processes")]
[JsonDerivedType(typeof(ActionResultResponse), "actionResult")]
[JsonDerivedType(typeof(ChangesResponse), "changes")]
[JsonDerivedType(typeof(RulesResponse), "rules")]
[JsonDerivedType(typeof(InsightsResponse), "insights")]
[JsonDerivedType(typeof(BootReportResponse), "bootReport")]
[JsonDerivedType(typeof(TimelineResponse), "timeline")]
[JsonDerivedType(typeof(ExplanationResponse), "explanation")]
[JsonDerivedType(typeof(IoLatencyResponse), "ioLatency")]
[JsonDerivedType(typeof(NetworkHealthResponse), "networkHealth")]
[JsonDerivedType(typeof(WaitAnalysisResponse), "waitAnalysis")]
[JsonDerivedType(typeof(MemoryGovernorResponse), "memoryGovernor")]
[JsonDerivedType(typeof(ContractsResponse), "contracts")]
[JsonDerivedType(typeof(RegressionsResponse), "regressions")]
[JsonDerivedType(typeof(ExperimentsResponse), "experiments")]
public abstract record Response
{
    public required uint Id { get; init; }
}

public enum ClientRole
{
    /// <summary>Read-only: status, metrics, history, config.</summary>
    Viewer = 0,
    /// <summary>Caller token is an elevated member of BUILTIN\Administrators.</summary>
    Admin = 1,
}

public sealed record HelloAck : Response
{
    public required int ProtocolVersion { get; init; }
    public required string ServerVersion { get; init; }
    public required string SessionId { get; init; }
    public required ClientRole Role { get; init; }
    public required int AggregationIntervalMs { get; init; }
    public required int MaxRequestsPerSecond { get; init; }
}

public sealed record PongResponse : Response
{
    public required long ServerTimeUnixMs { get; init; }
}

public enum BreakerState
{
    /// <summary>Normal: tuning actions allowed (if not in Safe Mode).</summary>
    Closed = 0,
    /// <summary>Tripped: tuning disabled, all changes rolled back, cooling down.</summary>
    Open = 1,
    /// <summary>Probation after cooldown; any instability re-trips immediately.</summary>
    HalfOpen = 2,
    /// <summary>Tripped too often; stays disabled until an admin resets it.</summary>
    Latched = 3,
}

public sealed record StatusResponse : Response
{
    public required string ServerVersion { get; init; }
    public required long UptimeSeconds { get; init; }
    public required bool SafeMode { get; init; }
    public required string TelemetrySource { get; init; }
    public required bool PerProcessIoEnabled { get; init; }
    public required bool Degraded { get; init; }
    public required BreakerState Breaker { get; init; }
    public string? BreakerReason { get; init; }
    public required int ActiveChanges { get; init; }
    public required int ConnectedClients { get; init; }
    public required int AggregationIntervalMs { get; init; }
    public required double ServiceCpuPercent { get; init; }
    public required double ServicePrivateMb { get; init; }
    public required double CpuBudgetPercent { get; init; }
    public required double MemoryBudgetMb { get; init; }

    // ---- protocol v2: tuning state
    public required TuningMode Mode { get; init; }
    public required bool AutoCalm { get; init; }
    public required int AutoCalmThresholdPercent { get; init; }
    public required int RulesCount { get; init; }
    public required bool HybridCpu { get; init; }
    public required bool ForegroundKnown { get; init; }
    public required bool TuningAllowed { get; init; }

    // ---- protocol v4: profile + telemetry health (F9)
    public required TuningProfile Profile { get; init; }
    public required double TelemetryEventsPerSec { get; init; }
    public required long TelemetryEventsLost { get; init; }
    public required string TelemetryHealth { get; init; }
}

public sealed record MetricsPush : Response
{
    public required MetricSample Sample { get; init; }
    public ProcessIo[] TopIo { get; init; } = [];
}

public sealed record HistoryResponse : Response
{
    public required MetricSample[] Samples { get; init; }
}

/// <summary>Read-only view of the effective, validated configuration.</summary>
public sealed record ConfigResponse : Response
{
    public required int AggregationIntervalMs { get; init; }
    public required int HistoryCapacity { get; init; }
    public required bool EnableEtw { get; init; }
    public required bool EnablePerProcessIo { get; init; }
    public required int TopProcessCount { get; init; }
    public required bool SafeMode { get; init; }
    public required string[] ConfigWarnings { get; init; }
}

public sealed record AckResponse : Response;

public sealed record ErrorResponse : Response
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>Stable machine-readable error codes.</summary>
public static class ErrorCodes
{
    public const string BadFrame = "bad_frame";
    public const string Malformed = "malformed";
    public const string Schema = "schema";
    public const string HandshakeRequired = "handshake_required";
    public const string UnsupportedVersion = "unsupported_version";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string RateLimited = "rate_limited";
    public const string Busy = "busy";
    public const string Internal = "internal";
}
