using System.Text.Json;
using System.Text.Json.Serialization;
using PerfTuner.Contracts;

namespace PerfTuner.Core.Configuration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = false,
    WriteIndented = true,
    MaxDepth = 8)]
[JsonSerializable(typeof(ServiceConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>Result of loading configuration. <see cref="Config"/> is always usable.</summary>
public sealed record ConfigLoadResult(ServiceConfig Config, IReadOnlyList<string> Errors, bool UsedDefaults);

/// <summary>
/// Bounded, validated configuration loading. Policy is fail-safe: if the file is
/// missing, oversized, malformed, contains unknown fields, or violates any bound,
/// the service runs on built-in defaults (monitoring + Safe Mode) and reports why.
/// Partial acceptance is deliberately NOT supported — a half-applied config is
/// harder to reason about than a rejected one.
/// </summary>
public static class ConfigLoader
{
    public const int MaxConfigBytes = 64 * 1024;

    public static ConfigLoadResult Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ConfigLoadResult(new ServiceConfig(), ["config file not found; using defaults"], UsedDefaults: true);
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            if (stream.Length > MaxConfigBytes)
            {
                return Fallback($"config file exceeds {MaxConfigBytes} bytes");
            }
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fallback($"config file unreadable ({ex.GetType().Name})");
        }

        return Parse(bytes);
    }

    public static ConfigLoadResult Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxConfigBytes)
        {
            return Fallback($"config exceeds {MaxConfigBytes} bytes");
        }

        // Skip the UTF-8 BOM that Notepad may add.
        if (utf8Json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            utf8Json = utf8Json[3..];
        }

        ServiceConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(utf8Json, ConfigJsonContext.Default.ServiceConfig);
        }
        catch (JsonException ex)
        {
            return Fallback($"config JSON invalid at line {ex.LineNumber}, position {ex.BytePositionInLine}: unknown field, wrong type, or syntax error");
        }

        if (config is null)
        {
            return Fallback("config is null");
        }

        var errors = Validate(config);
        return errors.Count == 0
            ? new ConfigLoadResult(config, [], UsedDefaults: false)
            : new ConfigLoadResult(new ServiceConfig(), errors, UsedDefaults: true);
    }

    public static string Serialize(ServiceConfig config) => JsonSerializer.Serialize(config, ConfigJsonContext.Default.ServiceConfig);

    public static List<string> Validate(ServiceConfig c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var errors = new List<string>();

        void Range(string name, double value, double min, double max)
        {
            if (double.IsNaN(value) || value < min || value > max)
            {
                errors.Add($"{name}={value} outside {min}..{max}");
            }
        }

        if (c.SchemaVersion != 1)
        {
            errors.Add($"schemaVersion={c.SchemaVersion} unsupported (expected 1)");
        }
        Range("aggregationIntervalMs", c.AggregationIntervalMs, ProtocolConstants.MinAggregationIntervalMs, ProtocolConstants.MaxAggregationIntervalMs);
        Range("historyCapacity", c.HistoryCapacity, 60, 7200);
        Range("topProcessCount", c.TopProcessCount, 0, ProtocolConstants.MaxTopProcesses);
        Range("maxClients", c.MaxClients, 1, 8);
        Range("maxRequestsPerSecond", c.MaxRequestsPerSecond, 1, 100);

        if (c.Logging is null || c.CircuitBreaker is null || c.Budgets is null)
        {
            errors.Add("logging, circuitBreaker and budgets sections must not be null");
            return errors;
        }

        string[] levels = ["Verbose", "Debug", "Information", "Warning", "Error"];
        if (Array.IndexOf(levels, c.Logging.MinimumLevel) < 0)
        {
            errors.Add("logging.minimumLevel must be one of Verbose, Debug, Information, Warning, Error");
        }
        Range("logging.retainedFileCount", c.Logging.RetainedFileCount, 1, 30);
        Range("logging.fileSizeLimitMb", c.Logging.FileSizeLimitMb, 1, 50);

        var cb = c.CircuitBreaker;
        Range("circuitBreaker.cpuSaturationPercent", cb.CpuSaturationPercent, 50, 100);
        Range("circuitBreaker.memoryLoadPercent", cb.MemoryLoadPercent, 50, 99);
        Range("circuitBreaker.commitPercent", cb.CommitPercent, 50, 99);
        Range("circuitBreaker.sustainSeconds", cb.SustainSeconds, 5, 600);
        Range("circuitBreaker.maxCollectorFaultsPerMinute", cb.MaxCollectorFaultsPerMinute, 1, 60);
        Range("circuitBreaker.cooldownSeconds", cb.CooldownSeconds, 30, 3600);
        Range("circuitBreaker.maxTripsPerHour", cb.MaxTripsPerHour, 1, 10);

        Range("budgets.serviceCpuPercent", c.Budgets.ServiceCpuPercent, 0.1, 5);
        Range("budgets.servicePrivateMb", c.Budgets.ServicePrivateMb, 32, 512);

        return errors;
    }

    private static ConfigLoadResult Fallback(string reason) => new(new ServiceConfig(), [reason], UsedDefaults: true);
}
