namespace PerfTuner.Core.Hardware;

/// <summary>How hard a hardware part is working, as shown by the Cockpit tiles and animated icons.</summary>
public enum LoadState
{
    Calm,
    Busy,
    Strained,
}

/// <summary>Plain threshold rules for the Cockpit status of each part (design 6.2).</summary>
public static class ResourceStatus
{
    /// <summary>CPU: Busy from 60 %, Strained from 90 % or whenever Windows is holding the CPU back.</summary>
    public static LoadState Cpu(double percent, bool throttled) =>
        throttled || percent >= 90 ? LoadState.Strained : percent >= 60 ? LoadState.Busy : LoadState.Calm;

    /// <summary>Memory: Busy below 30 % available, Strained below 15 % available or commit above 90 % of the limit.</summary>
    public static LoadState Memory(double availablePercent, double commitPercent) =>
        availablePercent < 15 || commitPercent > 90 ? LoadState.Strained
        : availablePercent < 30 ? LoadState.Busy
        : LoadState.Calm;

    /// <summary>
    /// Throughput-only rule (used when latency/loss data is unavailable): Busy when the current rate is more than
    /// 4× this PC's usual (median) rate and above an absolute floor, so idle PCs don't flap on tiny bursts.
    /// </summary>
    public static LoadState Throughput(double bytesPerSecond, double medianBytesPerSecond, double floorBytesPerSecond) =>
        bytesPerSecond > floorBytesPerSecond && bytesPerSecond > 4 * medianBytesPerSecond ? LoadState.Busy : LoadState.Calm;

    /// <summary>The worse of two states.</summary>
    public static LoadState Worst(LoadState a, LoadState b) => a > b ? a : b;
}
