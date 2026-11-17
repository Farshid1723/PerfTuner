using System.Diagnostics;
using PerfTuner.Contracts;
using PerfTuner.Core.Safety;
using PerfTuner.Core.Telemetry;
using PerfTuner.Platform.Windows.Telemetry;
using PerfTuner.Service.Ipc;
using PerfTuner.Service.Telemetry;

namespace PerfTuner.Service.Engine;

/// <summary>
/// The monitoring pipeline. One async loop on a <see cref="PeriodicTimer"/> (no thread is
/// blocked between ticks); each tick:
/// <code>
///  ETW callbacks ──▶ IoAccumulator (continuous, allocation-free)
///                       │
///  tick ── GetSystemTimes / GlobalMemoryStatusEx / self-sample
///       ── SnapshotAndReset(accumulator) → rates
///       ── MetricSample → RingBuffer (history)
///       ── OverheadGovernor (self budget) → maybe degrade
///       ── SafetyCoordinator (circuit breaker)
///       ── if anyone subscribed: encode once → MetricsHub fan-out
/// </code>
/// </summary>
internal sealed partial class TelemetryEngine : BackgroundService
{
    private readonly RuntimeState _state;
    private readonly SafetyCoordinator _safety;
    private readonly MetricsHub _hub;
    private readonly TuningEngine _tuning;
    private readonly InsightsEngine _insights;
    private readonly ForensicsEngine _forensics;
    private readonly ILogger<TelemetryEngine> _logger;

    private readonly IoAccumulator _accumulator = new(1024);
    private readonly SystemSampler _system = new();
    private readonly SelfSampler _self = new();
    private readonly ProcessNameCache _names = new();
    private readonly OverheadGovernor _governor;
    private readonly ProcessIoCounters[] _top = new ProcessIoCounters[ProtocolConstants.MaxTopProcesses];

    private IIoSource? _source;
    private long _lastFaultLogMs = -1_000_000_000_000; // not long.MinValue: (now - last) must not overflow
    private long _lastBudgetLogMs = -1_000_000_000_000;

    public TelemetryEngine(RuntimeState state, SafetyCoordinator safety, MetricsHub hub, TuningEngine tuning, InsightsEngine insights, ForensicsEngine forensics, ILogger<TelemetryEngine> logger)
    {
        _forensics = forensics;
        _state = state;
        _safety = safety;
        _hub = hub;
        _tuning = tuning;
        _insights = insights;
        _logger = logger;
        _governor = new OverheadGovernor(state.Config.Budgets.ServiceCpuPercent);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never block host start-up

        _safety.OnServiceStarting();
        _source = CreateIoSource(preferEtw: _state.Config.EnableEtw);
        ApplyPerProcessSetting();

        // Prime delta-based samplers so the first published sample is meaningful.
        _system.Sample();
        _self.Sample();
        _accumulator.SnapshotAndReset([], out _);

        var intervalMs = _state.AggregationIntervalMs;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        var lastTick = Stopwatch.GetTimestamp();

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var now = Stopwatch.GetTimestamp();
                var measuredMs = Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds;
                lastTick = now;

                Tick(measuredMs);

                var requested = _state.AggregationIntervalMs;
                if (requested != intervalMs)
                {
                    intervalMs = requested;
                    timer.Period = TimeSpan.FromMilliseconds(intervalMs);
                    LogIntervalChanged(intervalMs);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            _source?.Dispose();   // always stop the ETW session: never leak a kernel logger
            _source = null;
            _forensics.Shutdown(); // records an interrupted experiment before the journal rolls back
            _safety.OnServiceStopping();
        }
    }

    private void Tick(double intervalMs)
    {
        var nowMs = Environment.TickCount64;
        var fault = false;
        ApplyPerProcessSetting(); // picks up admin toggles from IPC on the engine thread

        CpuMemorySample sys = default;
        try
        {
            sys = _system.Sample();
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            fault = true;
            LogFaultRateLimited(ex, nowMs);
        }

        var self = _self.Sample();

        if (_source is { Faulted: true })
        {
            fault = true;
            _source.Dispose();
            _source = CreateIoSource(preferEtw: false);
            ApplyPerProcessSetting();
        }

        try
        {
            _source?.Poll(intervalMs);
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            fault = true;
            LogFaultRateLimited(ex, nowMs);
        }

        PerfTuner.Platform.Windows.Telemetry.CpuPerformance cpuPerf = default;
        try
        {
            cpuPerf = _insights.OnTick(nowMs);
        }
        catch (InvalidOperationException ex)
        {
            LogFaultRateLimited(ex, nowMs);
        }

        var topCapacity = Math.Min(_state.Config.TopProcessCount, _top.Length);
        var totals = _accumulator.SnapshotAndReset(_top.AsSpan(0, topCapacity), out var topCount);
        var perSecond = intervalMs > 0 ? 1000.0 / intervalMs : 0;

        var flags = _source?.Flags ?? SampleFlags.None;
        if (_governor.Degraded) { flags |= SampleFlags.Degraded; }
        if (totals.TableOverflowed) { flags |= SampleFlags.ProcessTableOverflow; }

        var sample = new MetricSample
        {
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IntervalMs = (int)Math.Round(intervalMs),
            CpuPercent = Round1(sys.CpuPercent),
            MemoryLoadPercent = sys.MemoryLoadPercent,
            CommitPercent = Round1(sys.CommitPercent),
            MemoryAvailableMb = sys.AvailableMb,
            MemoryTotalMb = sys.TotalMb,
            DiskReadBytesPerSec = Math.Round(totals.DiskReadBytes * perSecond),
            DiskWriteBytesPerSec = Math.Round(totals.DiskWriteBytes * perSecond),
            NetRxBytesPerSec = Math.Round(totals.NetRxBytes * perSecond),
            NetTxBytesPerSec = Math.Round(totals.NetTxBytes * perSecond),
            ServiceCpuPercent = Math.Round(self.CpuPercent, 3),
            ServicePrivateMb = Round1(self.PrivateMb),
            Flags = flags,
            CpuEffectiveMhz = Math.Round(cpuPerf.EffectiveMhz),
            CpuLimitPercent = Math.Round(cpuPerf.LimitPercent, 1),
        };
        _state.History.Add(sample);

        if (_governor.Observe(self.CpuPercent))
        {
            ApplyPerProcessSetting();
            LogDegradationChanged(_governor.Degraded, _governor.RollingMeanPercent, _state.Config.Budgets.ServiceCpuPercent);
        }
        if (self.PrivateMb > _state.Config.Budgets.ServicePrivateMb && nowMs - _lastBudgetLogMs > 300_000)
        {
            _lastBudgetLogMs = nowMs;
            LogMemoryBudget(self.PrivateMb, _state.Config.Budgets.ServicePrivateMb);
        }

        _safety.Evaluate(new HealthSignals(sys.CpuPercent, sys.MemoryLoadPercent, sys.CommitPercent, fault), nowMs);

        _state.Snapshot = new EngineSnapshot(
            _source?.Name ?? "none",
            _accumulator.TrackPerProcess,
            _governor.Degraded,
            sample.ServiceCpuPercent,
            sample.ServicePrivateMb);

        if (_hub.Count > 0)
        {
            Publish(sample, topCount, perSecond);
        }

        try
        {
            var etw = _source as EtwKernelIoSource;
            _forensics.OnTick(sample, etw?.EventsProcessed ?? 0, etw?.EventsLost ?? 0, etw is not null, nowMs, intervalMs);
        }
        catch (InvalidOperationException ex)
        {
            LogFaultRateLimited(ex, nowMs);
        }

        try
        {
            _tuning.OnTick(nowMs);
        }
        catch (InvalidOperationException ex)
        {
            LogFaultRateLimited(ex, nowMs);
        }
    }

    private void Publish(in MetricSample sample, int topCount, double perSecond)
    {
        var top = topCount == 0 ? [] : new ProcessIo[topCount];
        for (var i = 0; i < topCount; i++)
        {
            ref readonly var p = ref _top[i];
            top[i] = new ProcessIo
            {
                Pid = p.Pid,
                Name = _names.GetName(p.Pid),
                DiskBytesPerSec = Math.Round(p.DiskBytes * perSecond),
                NetBytesPerSec = Math.Round(p.NetBytes * perSecond),
            };
        }
        var frame = MessageCodec.EncodeResponseFrame(new MetricsPush { Id = 0, Sample = sample, TopIo = top });
        _hub.Broadcast(frame);
    }

    private IIoSource? CreateIoSource(bool preferEtw)
    {
        if (preferEtw)
        {
            var etw = new EtwKernelIoSource(_accumulator, _forensics, _logger);
            try
            {
                etw.Start();
                LogIoSource(etw.Name);
                return etw;
            }
            catch (Exception ex)
            {
                etw.Dispose();
                LogEtwUnavailable(ex.GetType().Name, ex.Message);
            }
        }

        var pdh = new PdhIoSource(_accumulator);
        try
        {
            pdh.Start();
            LogIoSource(pdh.Name);
            return pdh;
        }
        catch (InvalidOperationException ex)
        {
            pdh.Dispose();
            _logger.LogError(ex, "PDH fallback unavailable; disk/network metrics disabled");
            return null;
        }
    }

    private void ApplyPerProcessSetting()
    {
        _accumulator.TrackPerProcess = _state.PerProcessIoRequested
            && !_governor.Degraded
            && _source is { SupportsPerProcess: true };
    }

    private void LogFaultRateLimited(Exception ex, long nowMs)
    {
        if (nowMs - _lastFaultLogMs > 60_000)
        {
            _lastFaultLogMs = nowMs;
            _logger.LogWarning(ex, "Collector fault (further faults suppressed for 60 s)");
        }
    }

    private static double Round1(double v) => Math.Round(v, 1);

    [LoggerMessage(Level = LogLevel.Information, Message = "IO telemetry source: {Source}")]
    private partial void LogIoSource(string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kernel ETW unavailable ({ErrorType}: {Reason}); using fallback")]
    private partial void LogEtwUnavailable(string errorType, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Aggregation interval changed to {IntervalMs} ms")]
    private partial void LogIntervalChanged(int intervalMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Overhead governor degraded={Degraded} (rolling CPU {MeanPercent:F3}% vs budget {BudgetPercent}%)")]
    private partial void LogDegradationChanged(bool degraded, double meanPercent, double budgetPercent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Service private memory {PrivateMb:F1} MB exceeds budget {BudgetMb} MB")]
    private partial void LogMemoryBudget(double privateMb, double budgetMb);
}
