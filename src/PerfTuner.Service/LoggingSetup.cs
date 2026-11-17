using PerfTuner.Core.Configuration;
using PerfTuner.Platform.Windows;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace PerfTuner.Service;

/// <summary>
/// Structured logging policy:
/// <list type="bullet">
/// <item>Compact JSON (CLEF) — every property is a field, never string-concatenated, so a
///   hostile value cannot forge log lines.</item>
/// <item>Bounded retention: file size cap + retained file count (validated config bounds;
///   worst case 30 × 50 MB).</item>
/// <item>No secrets: the service handles none; IPC payloads are never logged, only
///   validated, allow-listed fields (client name, error codes).</item>
/// </list>
/// </summary>
internal static class LoggingSetup
{
    public static Serilog.ILogger Create(LoggingOptions options, ProductPaths paths, bool devMode)
    {
        var level = Enum.Parse<LogEventLevel>(options.MinimumLevel); // validated allow-list

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.WithProperty("App", "PerfTuner.Service")
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(paths.LogDirectory, "service-.clef"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: options.FileSizeLimitMb * 1024L * 1024L,
                retainedFileCountLimit: options.RetainedFileCount,
                buffered: true,
                flushToDiskInterval: TimeSpan.FromSeconds(5));

        if (devMode)
        {
            config = config.WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture);
        }

        return config.CreateLogger();
    }
}
