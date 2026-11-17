using PerfTuner.Contracts;
using PerfTuner.Core.Buffers;
using PerfTuner.Core.Configuration;

namespace PerfTuner.Service.Engine;

/// <summary>Immutable snapshot of engine health, replaced atomically once per interval.</summary>
internal sealed record EngineSnapshot(
    string TelemetrySource,
    bool PerProcessIoActive,
    bool Degraded,
    double ServiceCpuPercent,
    double ServicePrivateMb)
{
    public static EngineSnapshot Starting { get; } = new("starting", false, false, 0, 0);
}

/// <summary>
/// Shared state between the telemetry engine (single writer) and IPC sessions (readers).
/// Mutable runtime options are only the "safe monitoring toggles" and never persist.
/// </summary>
internal sealed class RuntimeState
{
    private volatile EngineSnapshot _snapshot = EngineSnapshot.Starting;
    private int _aggregationIntervalMs;
    private volatile bool _perProcessIoRequested;

    public RuntimeState(ConfigLoadResult config, bool devMode)
    {
        Config = config.Config;
        ConfigWarnings = [.. config.Errors];
        DevMode = devMode;
        _aggregationIntervalMs = Config.AggregationIntervalMs;
        _perProcessIoRequested = Config.EnablePerProcessIo;
        History = new RingBuffer<MetricSample>(Config.HistoryCapacity);
    }

    public ServiceConfig Config { get; }
    public string[] ConfigWarnings { get; }
    public bool DevMode { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public string Version { get; } = typeof(RuntimeState).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Telemetry history (preallocated ring).</summary>
    public RingBuffer<MetricSample> History { get; }

    public EngineSnapshot Snapshot
    {
        get => _snapshot;
        set => _snapshot = value;
    }

    public int AggregationIntervalMs
    {
        get => Volatile.Read(ref _aggregationIntervalMs);
        set => Volatile.Write(ref _aggregationIntervalMs,
            Math.Clamp(value, ProtocolConstants.MinAggregationIntervalMs, ProtocolConstants.MaxAggregationIntervalMs));
    }

    public bool PerProcessIoRequested
    {
        get => _perProcessIoRequested;
        set => _perProcessIoRequested = value;
    }
}
