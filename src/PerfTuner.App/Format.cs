using System.Globalization;

namespace PerfTuner.App;

internal static class Format
{
    public static string Rate(double bytesPerSec) => bytesPerSec switch
    {
        >= 1024d * 1024 * 1024 => (bytesPerSec / (1024d * 1024 * 1024)).ToString("F1", CultureInfo.CurrentCulture) + " GB/s",
        >= 1024d * 1024 => (bytesPerSec / (1024d * 1024)).ToString("F1", CultureInfo.CurrentCulture) + " MB/s",
        >= 1024 => (bytesPerSec / 1024).ToString("F0", CultureInfo.CurrentCulture) + " KB/s",
        _ => bytesPerSec.ToString("F0", CultureInfo.CurrentCulture) + " B/s",
    };

    public static string AxisRate(double bytesPerSec) => bytesPerSec switch
    {
        >= 1024d * 1024 * 1024 => (bytesPerSec / (1024d * 1024 * 1024)).ToString("0.#", CultureInfo.CurrentCulture) + " GB/s",
        >= 1024d * 1024 => (bytesPerSec / (1024d * 1024)).ToString("0.#", CultureInfo.CurrentCulture) + " MB/s",
        >= 1024 => (bytesPerSec / 1024).ToString("0", CultureInfo.CurrentCulture) + " KB/s",
        _ => bytesPerSec.ToString("0", CultureInfo.CurrentCulture),
    };

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("F1", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("F0", CultureInfo.CurrentCulture) + " MB",
        >= 1L << 10 => (bytes / 1024.0).ToString("F0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString(CultureInfo.CurrentCulture) + " B",
    };

    public static string Uptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : $"{t.Hours}h {t.Minutes}m {t.Seconds}s";
    }
}
