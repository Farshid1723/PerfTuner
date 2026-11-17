using PerfTuner.Contracts;
using PerfTuner.Core.Configuration;

namespace PerfTuner.Core.Safety;

/// <summary>Health inputs evaluated once per aggregation interval.</summary>
public readonly record struct HealthSignals(
    double CpuPercent,
    double MemoryLoadPercent,
    double CommitPercent,
    bool CollectorFault);

/// <summary>
/// Stability circuit breaker guarding all tuning actions.
/// <code>
///            trip condition                    cooldown elapsed
///  Closed ───────────────────▶ Open ───────────────────────────▶ HalfOpen
///    ▲                          ▲                                   │
///    │  healthy for Sustain     │  any trip condition               │
///    └──────────────────────────┼───────────────────────────────────┘
///                               │
///         ≥ MaxTripsPerHour ───▶ Latched (until admin Reset)
/// </code>
/// Every transition into Open/Latched invokes the trip callback, which rolls back all
/// active changes. The breaker is deterministic: time is supplied by the caller.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly CircuitBreakerOptions _options;
    private readonly Action<string> _onTrip;
    private readonly Queue<long> _faultTimesMs = new();
    private readonly Queue<long> _tripTimesMs = new();

    private long _cpuHighSinceMs = -1;
    private long _memHighSinceMs = -1;
    private long _commitHighSinceMs = -1;
    private long _openedAtMs = -1;
    private long _healthySinceMs = -1;

    public CircuitBreaker(CircuitBreakerOptions options, Action<string> onTrip)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _onTrip = onTrip ?? throw new ArgumentNullException(nameof(onTrip));
    }

    public BreakerState State { get; private set; } = BreakerState.Closed;
    public string? LastReason { get; private set; }

    /// <summary>True only when tuning actions may be applied.</summary>
    public bool AllowsTuning => State == BreakerState.Closed;

    public void Evaluate(in HealthSignals s, long nowMs)
    {
        var sustainMs = _options.SustainSeconds * 1000L;

        var cpuTrip = Sustained(ref _cpuHighSinceMs, s.CpuPercent >= _options.CpuSaturationPercent, nowMs, sustainMs);
        var memTrip = Sustained(ref _memHighSinceMs, s.MemoryLoadPercent >= _options.MemoryLoadPercent, nowMs, sustainMs);
        var commitTrip = Sustained(ref _commitHighSinceMs, s.CommitPercent >= _options.CommitPercent, nowMs, sustainMs);

        if (s.CollectorFault)
        {
            _faultTimesMs.Enqueue(nowMs);
        }
        while (_faultTimesMs.Count > 0 && nowMs - _faultTimesMs.Peek() > 60_000)
        {
            _faultTimesMs.Dequeue();
        }
        var faultTrip = _faultTimesMs.Count >= _options.MaxCollectorFaultsPerMinute;

        string? reason = cpuTrip ? $"CPU ≥ {_options.CpuSaturationPercent}% for {_options.SustainSeconds}s"
            : memTrip ? $"memory load ≥ {_options.MemoryLoadPercent}% for {_options.SustainSeconds}s"
            : commitTrip ? $"commit charge ≥ {_options.CommitPercent}% for {_options.SustainSeconds}s"
            : faultTrip ? $"{_faultTimesMs.Count} collector faults in the last minute"
            : null;

        // "Healthy" means no stress condition is currently present (not merely un-sustained).
        var stressedNow = s.CpuPercent >= _options.CpuSaturationPercent
            || s.MemoryLoadPercent >= _options.MemoryLoadPercent
            || s.CommitPercent >= _options.CommitPercent
            || s.CollectorFault;

        switch (State)
        {
            case BreakerState.Closed:
                if (reason is not null)
                {
                    Trip(reason, nowMs);
                }
                break;

            case BreakerState.Open:
                if (nowMs - _openedAtMs >= _options.CooldownSeconds * 1000L)
                {
                    State = BreakerState.HalfOpen;
                    _healthySinceMs = stressedNow ? -1 : nowMs;
                }
                break;

            case BreakerState.HalfOpen:
                if (stressedNow)
                {
                    // Probation failed: no sustain window, re-trip immediately.
                    Trip(reason ?? "instability during probation", nowMs);
                }
                else
                {
                    if (_healthySinceMs < 0)
                    {
                        _healthySinceMs = nowMs;
                    }
                    if (nowMs - _healthySinceMs >= sustainMs)
                    {
                        State = BreakerState.Closed;
                        LastReason = null;
                    }
                }
                break;

            case BreakerState.Latched:
                break;
        }
    }

    /// <summary>Manual trip (e.g. unclean shutdown detected, admin request).</summary>
    public void ForceTrip(string reason, long nowMs)
    {
        if (State is BreakerState.Open or BreakerState.Latched)
        {
            return;
        }
        Trip(reason, nowMs);
    }

    /// <summary>Admin reset out of any state back to Closed.</summary>
    public void Reset()
    {
        State = BreakerState.Closed;
        LastReason = null;
        _tripTimesMs.Clear();
        _faultTimesMs.Clear();
        _cpuHighSinceMs = _memHighSinceMs = _commitHighSinceMs = -1;
    }

    private void Trip(string reason, long nowMs)
    {
        _tripTimesMs.Enqueue(nowMs);
        while (_tripTimesMs.Count > 0 && nowMs - _tripTimesMs.Peek() > 3_600_000)
        {
            _tripTimesMs.Dequeue();
        }

        State = _tripTimesMs.Count >= _options.MaxTripsPerHour ? BreakerState.Latched : BreakerState.Open;
        LastReason = State == BreakerState.Latched ? $"latched after {_tripTimesMs.Count} trips/hour; last: {reason}" : reason;
        _openedAtMs = nowMs;
        _healthySinceMs = -1;

        // Rollback happens synchronously inside the trip, before any further tuning can run.
        _onTrip(LastReason);
    }

    private static bool Sustained(ref long sinceMs, bool conditionNow, long nowMs, long sustainMs)
    {
        if (!conditionNow)
        {
            sinceMs = -1;
            return false;
        }
        if (sinceMs < 0)
        {
            sinceMs = nowMs;
        }
        return nowMs - sinceMs >= sustainMs;
    }
}
