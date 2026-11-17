namespace PerfTuner.Core.Security;

/// <summary>
/// Allocation-free token-bucket rate limiter. Time is passed in explicitly
/// (milliseconds from any monotonic clock) so behaviour is deterministic in tests.
/// Not thread-safe: each IPC connection owns one and uses it from its read loop.
/// </summary>
public sealed class TokenBucket
{
    private readonly double _capacity;
    private readonly double _refillPerMs;
    private double _tokens;
    private long _lastMs;

    public TokenBucket(int ratePerSecond, int burst, long nowMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ratePerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        _capacity = burst;
        _refillPerMs = ratePerSecond / 1000.0;
        _tokens = burst;
        _lastMs = nowMs;
    }

    public bool TryTake(long nowMs)
    {
        var elapsed = nowMs - _lastMs;
        if (elapsed > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + (elapsed * _refillPerMs));
            _lastMs = nowMs;
        }
        if (_tokens >= 1.0)
        {
            _tokens -= 1.0;
            return true;
        }
        return false;
    }
}
