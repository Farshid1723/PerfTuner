namespace PerfTuner.Core.Telemetry;

/// <summary>
/// Keeps the service inside its own CPU budget. It tracks a rolling mean of the
/// service's CPU use and, if the mean exceeds the budget, asks the engine to degrade
/// (switch off per-process attribution — the most event-rate-sensitive work).
/// Hysteresis: it restores full detail only after the mean falls below half the budget.
/// Fixed-size window, no allocations after construction.
/// </summary>
public sealed class OverheadGovernor
{
    private readonly double[] _window;
    private readonly double _budgetPercent;
    private int _next;
    private int _filled;
    private double _sum;

    public OverheadGovernor(double budgetPercent, int windowSamples = 30)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budgetPercent, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSamples, 2);
        _budgetPercent = budgetPercent;
        _window = new double[windowSamples];
    }

    public bool Degraded { get; private set; }

    public double RollingMeanPercent => _filled == 0 ? 0 : _sum / _filled;

    /// <summary>Feeds one sample; returns true when <see cref="Degraded"/> changed.</summary>
    public bool Observe(double serviceCpuPercent)
    {
        if (double.IsNaN(serviceCpuPercent) || serviceCpuPercent < 0)
        {
            return false;
        }

        _sum -= _window[_next];
        _window[_next] = serviceCpuPercent;
        _sum += serviceCpuPercent;
        _next = (_next + 1) % _window.Length;
        if (_filled < _window.Length)
        {
            _filled++;
        }

        // Only judge on a full window so start-up JIT cost does not trigger degradation.
        if (_filled < _window.Length)
        {
            return false;
        }

        var mean = RollingMeanPercent;
        if (!Degraded && mean > _budgetPercent)
        {
            Degraded = true;
            return true;
        }
        if (Degraded && mean < _budgetPercent / 2)
        {
            Degraded = false;
            return true;
        }
        return false;
    }
}
