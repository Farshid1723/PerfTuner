using PerfTuner.Contracts;
using PerfTuner.Core.Configuration;
using PerfTuner.Platform.Windows;
using PerfTuner.Service;
using PerfTuner.Service.Engine;
using PerfTuner.Service.Ipc;
using Serilog;

// ---------------------------------------------------------------------------
// PerfTuner.Service
//   (no args)   run under the Service Control Manager (production)
//   --console   developer mode: runs in the terminal as the current user, uses the
//               per-user data folder and the separate dev pipe name.
// ---------------------------------------------------------------------------

var devMode = Array.IndexOf(args, "--console") >= 0;
var paths = devMode ? ProductPaths.Development : ProductPaths.Production;

// 1) Data directory: in production, harden the ACL BEFORE reading anything from it.
try
{
    if (devMode)
    {
        Directory.CreateDirectory(paths.DataRoot);
    }
    else
    {
        DirectoryHardening.CreateHardened(paths.DataRoot);
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Cannot prepare data directory: {ex.Message}");
    return 2;
}

// 2) Configuration: bounded, validated, fail-safe to defaults.
var config = ConfigLoader.Load(paths.ConfigFile);

// 3) Logging: structured compact-JSON files, size-capped and count-capped.
Log.Logger = LoggingSetup.Create(config.Config.Logging, paths, devMode);

try
{
    Log.Information("PerfTuner.Service {Version} starting (devMode={DevMode}, safeMode={SafeMode})",
        typeof(Program).Assembly.GetName().Version?.ToString(3), devMode, config.Config.SafeMode);
    foreach (var warning in config.Errors)
    {
        Log.Warning("Configuration: {Warning}", warning);
    }

    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    builder.Services.AddSerilog();
    builder.Services.AddWindowsService(o => o.ServiceName = ProtocolConstants.ServiceName);
    builder.Services.Configure<HostOptions>(o =>
    {
        o.ShutdownTimeout = TimeSpan.FromSeconds(15);
        // A crash in a background loop must stop the process (SCM restarts it, the
        // unclean-shutdown marker trips the breaker) rather than limp along half-dead.
        o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
    });

    builder.Services.AddSingleton(paths);
    builder.Services.AddSingleton(new RuntimeState(config, devMode));
    builder.Services.AddSingleton<SafetyCoordinator>();
    builder.Services.AddSingleton<MetricsHub>();
    builder.Services.AddSingleton<TuningEngine>();
    builder.Services.AddSingleton<InsightsEngine>();
    builder.Services.AddSingleton<ForensicsEngine>();
    builder.Services.AddHostedService<TelemetryEngine>();
    builder.Services.AddHostedService<PipeServer>();

    using var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
