using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using PerfTuner.Contracts;
using PerfTuner.Core.Insights;
using PerfTuner.Platform.Windows.Telemetry;
using PerfTuner.Platform.Windows.Tuning;

namespace PerfTuner.Service.Engine;

/// <summary>
/// Diagnostics that go beyond live charts:
/// <list type="bullet">
/// <item><b>CPU limit</b>: effective clock and Windows' "% Performance Limit" every interval.</item>
/// <item><b>Leak detection</b>: every 30 s a process snapshot feeds <see cref="LeakDetector"/>.</item>
/// <item><b>Driver latency</b>: on-demand kernel trace of DPC/ISR execution times per driver.</item>
/// <item><b>Boot/shutdown analysis</b>: Windows' own Diagnostics-Performance log (readable by
///   LocalSystem, so it works even when the GUI is not elevated).</item>
/// </list>
/// Everything here is read-only with respect to the system.
/// </summary>
internal sealed partial class InsightsEngine : IDisposable
{
    private const long LeakSampleIntervalMs = 30_000;
    private const string LatencySessionName = "PerfTuner-Latency";

    private readonly ILogger<InsightsEngine> _logger;
    private readonly Lock _gate = new();
    private readonly LeakDetector _leaks = new();
    private readonly LimitTracker _limit = new(600);
    private readonly ProcessSnapshotter _snapshotter = new();
    private readonly List<ProcSample> _samples = new(512);
    private readonly long _startMs = Environment.TickCount64;
    private readonly CpuPerformanceCounters? _cpu;

    private CpuPerformance _lastCpu;
    private long _lastLeakSampleMs = NeverMs; // sentinel: far past without risking overflow in (now - last)
    private const long NeverMs = -1_000_000_000_000;
    private LatencyReport _latency = Idle("No scan yet.");
    private BootReportResponse? _bootCache;
    private long _bootCacheMs;
    private int _scanRunning;

    public InsightsEngine(ILogger<InsightsEngine> logger)
    {
        _logger = logger;
        try
        {
            _cpu = new CpuPerformanceCounters();
        }
        catch (InvalidOperationException ex)
        {
            LogCpuCountersUnavailable(ex.Message);
        }
    }

    // --------------------------------------------------------------- per tick

    /// <summary>Called by the telemetry engine each interval; returns the CPU sample for the metric stream.</summary>
    public CpuPerformance OnTick(long nowMs)
    {
        var cpu = _cpu?.Sample() ?? default;
        lock (_gate)
        {
            _lastCpu = cpu;
            if (cpu.LimitPercent > 0) { _limit.Observe(cpu.LimitPercent); }

            if (nowMs - _lastLeakSampleMs >= LeakSampleIntervalMs)
            {
                _lastLeakSampleMs = nowMs;
                _snapshotter.Snapshot(_samples);
                var batch = new List<(int, long, string, double)>(_samples.Count);
                foreach (var s in _samples)
                {
                    if (s.Pid > 4) { batch.Add((s.Pid, s.CreateTime, s.Name, s.PrivateBytes / 1048576.0)); }
                }
                _leaks.Observe((nowMs - _startMs) / 60000.0, batch);
            }
        }
        return cpu;
    }

    public InsightsResponse GetInsights(uint id)
    {
        lock (_gate)
        {
            var suspects = _leaks.Suspects().Take(10).Select(f => new LeakSuspect
            {
                Pid = f.Pid, Name = f.Name, PrivateMb = f.PrivateMb, GrowthMbPerHour = f.GrowthMbPerHour,
                ObservedMinutes = f.ObservedMinutes, Confidence = f.RSquared,
            }).ToArray();
            return new InsightsResponse
            {
                Id = id,
                Clock = new CpuClockInfo
                {
                    Available = _cpu is not null && _lastCpu.BaseMhz > 0,
                    BaseMhz = Math.Round(_lastCpu.BaseMhz),
                    EffectiveMhz = Math.Round(_lastCpu.EffectiveMhz),
                    PerformancePercent = Math.Round(_lastCpu.PerformancePercent, 1),
                    LimitPercent = Math.Round(_lastCpu.LimitPercent, 1),
                    LimitedShareRecentPercent = Math.Round(_limit.LimitedSharePercent, 1),
                },
                Leaks = suspects,
                LeakObservationMinutes = Math.Round(_leaks.ObservationMinutes, 1),
                TrackedProcesses = _leaks.TrackedCount,
                Latency = _latency,
            };
        }
    }

    // --------------------------------------------------------------- latency

    /// <summary>
    /// Starts a DPC/ISR capture in the background. Kernel keywords: DeferedProcedureCalls +
    /// Interrupt only, private session, 16 MB buffers, auto-stopped after <paramref name="seconds"/>.
    /// Aggregation is per routine address (bounded table), resolved to drivers afterwards.
    /// </summary>
    public ActionResultResponse StartLatencyScan(uint id, int seconds, CancellationToken serviceToken)
    {
        if (Interlocked.CompareExchange(ref _scanRunning, 1, 0) != 0)
        {
            return new ActionResultResponse { Id = id, Success = false, Message = "A latency scan is already running." };
        }
        if (TraceEventSession.IsElevated() != true)
        {
            Interlocked.Exchange(ref _scanRunning, 0);
            SetLatency(Failed("LagScan needs full power: start PerfTuner as administrator (or install the always-on service).", seconds));
            return new ActionResultResponse { Id = id, Success = false, Message = "LagScan needs full power (administrator)." };
        }

        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SetLatency(new LatencyReport
        {
            State = LatencyScanState.Running, Message = $"Recording driver activity for {seconds} s… keep using the PC normally (play audio/video to catch glitches).",
            StartedUnixMs = started, Seconds = seconds, WorstUs = 0, Verdict = "", Drivers = [],
        });
        _ = Task.Run(() => RunScan(seconds, started, serviceToken), CancellationToken.None);
        return new ActionResultResponse { Id = id, Success = true, Message = $"Latency scan started ({seconds} s)." };
    }

    private void RunScan(int seconds, long started, CancellationToken ct)
    {
        var stats = new Dictionary<ulong, RoutineStats>(1024);
        var statsGate = new Lock();
        const int MaxRoutines = 8192;

        void Record(ulong routine, double elapsedMs, bool isr)
        {
            lock (statsGate)
            {
                if (!stats.TryGetValue(routine, out var s))
                {
                    if (stats.Count >= MaxRoutines) { return; }
                    s = new RoutineStats();
                    stats[routine] = s;
                }
                s.Add(elapsedMs, isr);
            }
        }

        try
        {
            using (var session = new TraceEventSession(LatencySessionName) { StopOnDispose = true, BufferSizeMB = 16 })
            {
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.Interrupt);
                var kernel = session.Source.Kernel;
                kernel.PerfInfoDPC += e => Record(e.Routine, e.ElapsedTimeMSec, false);
                kernel.PerfInfoThreadedDPC += e => Record(e.Routine, e.ElapsedTimeMSec, false);
                kernel.PerfInfoTimerDPC += e => Record(e.Routine, e.ElapsedTimeMSec, false);
                kernel.PerfInfoISR += e => Record(e.Routine, e.ElapsedTimeMSec, true);

                var pump = new Thread(() => session.Source.Process()) { IsBackground = true, Name = "PerfTuner latency ETW" };
                pump.Start();
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
                session.Stop();
                pump.Join(TimeSpan.FromSeconds(10));
            }

            var map = DriverAddressMap.Load();
            var byDriver = new Dictionary<string, (long Dpc, long Isr, double MaxDpc, double MaxIsr, double Total)>(StringComparer.OrdinalIgnoreCase);
            lock (statsGate)
            {
                foreach (var (routine, s) in stats)
                {
                    var driver = map.Resolve(routine);
                    byDriver.TryGetValue(driver, out var d);
                    byDriver[driver] = (d.Dpc + s.DpcCount, d.Isr + s.IsrCount, Math.Max(d.MaxDpc, s.MaxDpcMs), Math.Max(d.MaxIsr, s.MaxIsrMs), d.Total + s.TotalMs);
                }
            }

            var drivers = byDriver
                .Select(kv => new DriverLatency
                {
                    Driver = kv.Key.Length > 64 ? kv.Key[..64] : kv.Key,
                    DpcCount = kv.Value.Dpc,
                    IsrCount = kv.Value.Isr,
                    MaxDpcUs = Math.Round(kv.Value.MaxDpc * 1000, 1),
                    MaxIsrUs = Math.Round(kv.Value.MaxIsr * 1000, 1),
                    TotalMs = Math.Round(kv.Value.Total, 2),
                })
                .OrderByDescending(d => Math.Max(d.MaxDpcUs, d.MaxIsrUs))
                .Take(25)
                .ToArray();
            var worst = drivers.Length == 0 ? 0 : drivers.Max(d => Math.Max(d.MaxDpcUs, d.MaxIsrUs));
            var culprit = drivers.FirstOrDefault()?.Driver ?? "-";
            var verdict = worst switch
            {
                0 => "No DPC/ISR activity captured.",
                < 500 => $"Excellent — worst {worst:F0} µs. Suitable for real-time audio and competitive gaming.",
                < 1000 => $"Good — worst {worst:F0} µs ({culprit}). Fine for most real-time work.",
                < 2000 => $"Borderline — worst {worst:F0} µs caused by {culprit}. Audio dropouts or micro-stutter are possible; update or reconfigure that driver.",
                _ => $"Problem — worst {worst:F0} µs caused by {culprit}. Expect audio crackle / input lag; update, roll back or disable that driver/device (Wi-Fi, storage, GPU and ACPI drivers are common causes).",
            };
            SetLatency(new LatencyReport
            {
                State = LatencyScanState.Completed,
                Message = $"Captured {drivers.Sum(d => d.DpcCount + d.IsrCount):N0} DPC/ISR executions from {byDriver.Count} drivers in {seconds} s.",
                StartedUnixMs = started, Seconds = seconds, WorstUs = worst, Verdict = verdict, Drivers = drivers,
            });
            LogLatencyScan(seconds, worst, culprit);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Latency scan failed");
            SetLatency(Failed($"Scan failed: {ex.GetType().Name}", seconds));
        }
        finally
        {
            Interlocked.Exchange(ref _scanRunning, 0);
        }
    }

    private sealed class RoutineStats
    {
        public long DpcCount;
        public long IsrCount;
        public double MaxDpcMs;
        public double MaxIsrMs;
        public double TotalMs;

        public void Add(double ms, bool isr)
        {
            if (ms < 0 || ms > 10_000) { return; }
            TotalMs += ms;
            if (isr) { IsrCount++; MaxIsrMs = Math.Max(MaxIsrMs, ms); }
            else { DpcCount++; MaxDpcMs = Math.Max(MaxDpcMs, ms); }
        }
    }

    /// <summary>Most recent DPC/ISR scan result (for "Explain this stall").</summary>
    public LatencyReport LastLatency
    {
        get { lock (_gate) { return _latency; } }
    }

    private void SetLatency(LatencyReport report)
    {
        lock (_gate) { _latency = report; }
    }

    private static LatencyReport Idle(string message) => new()
    {
        State = LatencyScanState.Idle, Message = message, StartedUnixMs = 0, Seconds = 0, WorstUs = 0, Verdict = "", Drivers = [],
    };

    private static LatencyReport Failed(string message, int seconds) => new()
    {
        State = LatencyScanState.Failed, Message = message, StartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Seconds = seconds, WorstUs = 0, Verdict = "", Drivers = [],
    };

    // --------------------------------------------------------------- boot analysis

    /// <summary>
    /// Reads Microsoft-Windows-Diagnostics-Performance/Operational: 100 (boot), 101–110 (boot
    /// degradation causes), 200–203 (shutdown), 300+ (resume). Cached for 5 minutes; bounded to
    /// the newest 400 events.
    /// </summary>
    public BootReportResponse GetBootReport(uint id)
    {
        lock (_gate)
        {
            if (_bootCache is not null && Environment.TickCount64 - _bootCacheMs < 300_000)
            {
                return _bootCache with { Id = id };
            }
        }

        BootReportResponse report;
        try
        {
            report = ReadBootLog(id);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogException)
        {
            report = new BootReportResponse
            {
                Id = id, Available = false, Records = [], Culprits = [],
                Message = "BootStory reads Windows' boot diagnostics log, which needs full power: start PerfTuner as administrator.",
            };
        }

        lock (_gate)
        {
            _bootCache = report;
            _bootCacheMs = Environment.TickCount64;
        }
        return report;
    }

    private static BootReportResponse ReadBootLog(uint id)
    {
        var query = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
            "*[System[(EventID>=100 and EventID<=110) or (EventID>=200 and EventID<=203) or EventID=300]]")
        {
            ReverseDirection = true,
        };
        using var reader = new EventLogReader(query);

        var records = new List<BootRecord>();
        var culprits = new Dictionary<(string Name, string Phase), (string Kind, int Count, double Sum, double Max)>();
        var read = 0;
        for (var e = reader.ReadEvent(); e is not null && read < 400; e = reader.ReadEvent(), read++)
        {
            using (e)
            {
                var data = ParseData(e.ToXml());
                var time = e.TimeCreated is { } t ? new DateTimeOffset(t).ToUnixTimeMilliseconds() : 0;
                switch (e.Id)
                {
                    case 100 when records.Count(r => r.Kind == "boot") < 15:
                        records.Add(new BootRecord
                        {
                            TimeUnixMs = time, Kind = "boot",
                            TotalSeconds = Ms(data, "BootTime") / 1000,
                            MainPathSeconds = Ms(data, "MainPathBootTime") / 1000,
                            PostBootSeconds = Ms(data, "BootPostBootTime") / 1000,
                        });
                        break;
                    case 200 when records.Count(r => r.Kind == "shutdown") < 10:
                        records.Add(new BootRecord { TimeUnixMs = time, Kind = "shutdown", TotalSeconds = Ms(data, "ShutdownTime") / 1000 });
                        break;
                    case 300 when records.Count(r => r.Kind == "resume") < 10:
                        records.Add(new BootRecord { TimeUnixMs = time, Kind = "resume", TotalSeconds = Ms(data, "TotalTime") / 1000 });
                        break;
                    case >= 101 and <= 110 or >= 201 and <= 203:
                        var (kind, phase) = e.Id switch
                        {
                            101 => ("app", "boot"), 102 => ("driver", "boot"), 103 => ("service", "boot"),
                            104 => ("system", "boot"), 105 or 106 => ("prefetch", "boot"), 107 or 108 => ("policy", "boot"),
                            109 => ("device", "boot"), 110 => ("session", "boot"),
                            201 => ("app", "shutdown"), 202 => ("device", "shutdown"), _ => ("service", "shutdown"),
                        };
                        var name = First(data, "FriendlyName", "Name", "FileName") ?? $"event {e.Id}";
                        if (name.Length > 80) { name = name[..80]; }
                        var delay = Ms(data, "DegradationTime");
                        var key = (name, phase);
                        culprits.TryGetValue(key, out var c);
                        culprits[key] = (kind, c.Count + 1, c.Sum + delay, Math.Max(c.Max, delay));
                        break;
                }
            }
        }

        var top = culprits
            .Select(kv => new BootCulprit
            {
                Name = kv.Key.Name, Phase = kv.Key.Phase, Kind = kv.Value.Kind, Occurrences = kv.Value.Count,
                AvgDelayMs = Math.Round(kv.Value.Sum / kv.Value.Count), MaxDelayMs = Math.Round(kv.Value.Max),
            })
            .OrderByDescending(c => c.AvgDelayMs * c.Occurrences)
            .Take(20)
            .ToArray();

        return new BootReportResponse
        {
            Id = id,
            Available = true,
            Message = records.Count == 0 ? "No boot records yet (Windows writes one after each full boot)." : $"Analysed {read} diagnostic events.",
            Records = [.. records.OrderByDescending(r => r.TimeUnixMs)],
            Culprits = top,
        };
    }

    private static Dictionary<string, string> ParseData(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var doc = XDocument.Parse(xml);
        foreach (var d in doc.Descendants().Where(x => x.Name.LocalName == "Data"))
        {
            var name = d.Attribute("Name")?.Value;
            if (!string.IsNullOrEmpty(name)) { result[name] = d.Value; }
        }
        return result;
    }

    private static double Ms(Dictionary<string, string> data, string key)
        => data.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static string? First(Dictionary<string, string> data, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (data.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) { return v.Trim(); }
        }
        return null;
    }

    public void Dispose()
    {
        _cpu?.Dispose();
        _snapshotter.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "CPU performance counters unavailable: {Reason}")]
    private partial void LogCpuCountersUnavailable(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Latency scan ({Seconds} s): worst {WorstUs:F0} µs, top driver {Driver}")]
    private partial void LogLatencyScan(int seconds, double worstUs, string driver);
}
