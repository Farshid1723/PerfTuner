namespace PerfTuner.Core.Configuration;

/// <summary>
/// Service configuration (config.json). Every field has a safe default, so a missing or
/// rejected file yields a fully working, monitoring-only service.
/// </summary>
public sealed record ServiceConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Telemetry aggregation (and GUI push) interval. Bounds: 500..2000 ms.</summary>
    public int AggregationIntervalMs { get; set; } = 1000;

    /// <summary>Ring-buffer length in samples. Bounds: 60..7200 (≈ 2 h at 1 s).</summary>
    public int HistoryCapacity { get; set; } = 3600;

    /// <summary>Use a kernel ETW session for disk/network IO (falls back to PDH if unavailable).</summary>
    public bool EnableEtw { get; set; } = true;

    /// <summary>Attribute IO to processes (costs a little more CPU at high event rates).</summary>
    public bool EnablePerProcessIo { get; set; } = true;

    /// <summary>How many top IO processes to send per push. Bounds: 0..10.</summary>
    public int TopProcessCount { get; set; } = 5;

    /// <summary>Safe Mode: monitoring + minimal reversible actions only. Default ON.</summary>
    public bool SafeMode { get; set; } = true;

    /// <summary>Concurrent IPC clients. Bounds: 1..8.</summary>
    public int MaxClients { get; set; } = 4;

    /// <summary>Per-connection request rate. Bounds: 1..100 req/s (burst = 2×).</summary>
    public int MaxRequestsPerSecond { get; set; } = 20;

    public LoggingOptions Logging { get; set; } = new();
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();
    public BudgetOptions Budgets { get; set; } = new();
}

public sealed record LoggingOptions
{
    /// <summary>One of Verbose, Debug, Information, Warning, Error.</summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>Rolled log files kept. Bounds: 1..30.</summary>
    public int RetainedFileCount { get; set; } = 7;

    /// <summary>Size at which a log file rolls. Bounds: 1..50 MB.</summary>
    public int FileSizeLimitMb { get; set; } = 10;
}

public sealed record CircuitBreakerOptions
{
    /// <summary>Sustained system CPU at/above this trips the breaker. Bounds: 50..100.</summary>
    public double CpuSaturationPercent { get; set; } = 97;

    /// <summary>Sustained memory load at/above this trips the breaker. Bounds: 50..99.</summary>
    public double MemoryLoadPercent { get; set; } = 95;

    /// <summary>Sustained commit charge at/above this trips the breaker. Bounds: 50..99.</summary>
    public double CommitPercent { get; set; } = 95;

    /// <summary>How long a condition must persist before tripping. Bounds: 5..600 s.</summary>
    public int SustainSeconds { get; set; } = 30;

    /// <summary>Collector faults per rolling minute that trip the breaker. Bounds: 1..60.</summary>
    public int MaxCollectorFaultsPerMinute { get; set; } = 5;

    /// <summary>Open → HalfOpen delay. Bounds: 30..3600 s.</summary>
    public int CooldownSeconds { get; set; } = 300;

    /// <summary>Trips within one hour that latch the breaker until an admin reset. Bounds: 1..10.</summary>
    public int MaxTripsPerHour { get; set; } = 3;
}

public sealed record BudgetOptions
{
    /// <summary>Target service CPU (% of whole machine). Bounds: 0.1..5.</summary>
    public double ServiceCpuPercent { get; set; } = 0.5;

    /// <summary>Target service private memory. Bounds: 32..512 MB.</summary>
    public double ServicePrivateMb { get; set; } = 100;
}
