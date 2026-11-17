using System.Text.Json;
using System.Text.Json.Serialization;
using PerfTuner.Contracts;
using PerfTuner.Core.Autonomy;
using PerfTuner.Core.Forensics;
using PerfTuner.Core.Safety;
using PerfTuner.Core.Tuning;
using PerfTuner.Core.Visibility;
using PerfTuner.Platform.Windows;
using PerfTuner.Platform.Windows.Telemetry;
using PerfTuner.Platform.Windows.Tuning;
using PerfTuner.Service.Telemetry;

namespace PerfTuner.Service.Engine;

internal sealed record ForensicsState
{
    public double[] Baseline { get; set; } = [];
    public int[] BaselineMinutes { get; set; } = [];
    public List<ExperimentRecord> Experiments { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ForensicsState))]
internal sealed partial class ForensicsJsonContext : JsonSerializerContext;

/// <summary>
/// Forensics and autonomy hub (F1/F2/F3/F6/F7/F9), fed by the ETW consumer (per event, allocation
/// free) and by the telemetry engine (per interval):
/// <code>
///  ETW DiskIO ───▶ IoLatencyTracker (histograms: total / queue / device, per process)
///  ETW TcpIp  ───▶ BurstDetector (100 ms bins)          ETW HardFault / Retransmit ─▶ counters
///  tick ─▶ StallSignals ─▶ StallDetector ─▶ Timeline (+ process context captured at the stall)
///       ─▶ MemoryBehaviorClassifier ─▶ memory governor (Balanced+, contract "memory-governor")
///       ─▶ minute KPIs ─▶ RegressionDetector ─▶ suggestions ─▶ ExperimentRunner (Advanced: autopilot)
///       ─▶ telemetry health (events/s, lost events, freshness)
/// </code>
/// Every structure is bounded; the only periodic cost beyond the counters is one process
/// snapshot every 5 s (one syscall).
/// </summary>
internal sealed partial class ForensicsEngine : IForensicsSink, IDisposable
{
    private const long ContextIntervalMs = 5_000;
    private const int SecondsKept = 900;
    private const int MaxStallRecords = 64;
    private const long PersistIntervalMs = 10 * 60_000;
    private const long GovernorActionIntervalMs = 60_000;
    private const long GovernorTrimIntervalMs = 3 * 60_000;
    private const long GovernorMinPrivateBytes = 200L * 1024 * 1024;
    private const int GovernorMaxTargets = 3;

    private readonly RuntimeState _state;
    private readonly TuningEngine _tuning;
    private readonly InsightsEngine _insights;
    private readonly ILogger<ForensicsEngine> _logger;
    private readonly string _stateFile;
    private readonly int _selfPid = Environment.ProcessId;

    private readonly Lock _gate = new();
    private readonly Timeline _timeline = new();
    private readonly StallDetector _detector = new();
    private readonly BurstDetector _bursts = new();
    private readonly IoLatencyTracker _io = new();
    private readonly RegressionDetector _regressions = new();
    private readonly HardFaultTable _faultTable = new();
    private readonly ProcessSnapshotter _snapshotter = new();
    private readonly List<ProcSample> _procs = new(512);
    private readonly SecondStat[] _seconds = new SecondStat[SecondsKept];
    private readonly Dictionary<long, StallRecord> _stalls = [];
    private readonly Queue<long> _stallOrder = new();
    private readonly Queue<string> _governorHistory = new();
    private readonly HashSet<int> _governed = [];
    private readonly Dictionary<string, long> _regressionSince = [];
    private readonly MetricSample[] _sampleScratch = new MetricSample[20];

    private SystemPressureCounters? _pressure;
    private long _pressureRetryMs;
    private long _hardFaults;
    private long _retransmits;
    private long _packets;
    private long _lastHardFaults;
    private long _lastRetransmits;
    private long _lastPackets;
    private long _lastEvents;
    private long _lastLost;
    private int _secondHead;
    private int _secondCount;
    private long _lastContextMs = -1_000_000_000_000;
    private long _lastPersistMs;
    private long _lastLossEventMs = -1_000_000_000_000;
    private long _lastTickWallMs;
    private double _lastQueueLength;
    private StallContext? _latestContext;
    private MemoryStats _memory;
    private MemoryVerdict _verdict = new(MemoryBehavior.Stable, "Collecting data…", MemoryActionKind.None, "No action needed.");
    private int _pressureTicks;
    private int _stableTicks;
    private long _lastGovernorActionMs = -1_000_000_000_000;
    private long _lastGovernorTrimMs = -1_000_000_000_000;
    private bool _etwActive;

    // Minute accumulators for the regression detector.
    private long _minuteStartMs;
    private double _minP99Sum;
    private int _minP99N;
    private double _minHfSum;
    private double _minRetxSum;
    private int _minLimited;
    private int _minTicks;

    // Telemetry health (F9).
    private double _eventsPerSec;
    private long _lostLastMinute;
    private readonly long[] _lostRing = new long[60];
    private int _lostHead;

    private struct SecondStat
    {
        public long WallMs;
        public double DiskP99;
        public long IoCount;
        public double HardFaults;
        public double Retransmits;
        public double Packets;
        public double UsedMb;
        public bool Stall;
    }

    private sealed record StallRecord(StallContext? Context, MetricSample[] Samples);

    public ForensicsEngine(RuntimeState state, TuningEngine tuning, InsightsEngine insights, SafetyCoordinator safety, ProductPaths paths, ILogger<ForensicsEngine> logger)
    {
        _state = state;
        _tuning = tuning;
        _insights = insights;
        _logger = logger;
        _stateFile = Path.Combine(paths.StateDirectory, "forensics.json");
        Wait = new WaitStateAnalyzer(logger);
        Experiments = new ExperimentRunner(tuning, safety, WindowStats, Persist, logger);
        Load();
        // Never hide our own actions: every tuning change or decision is a timeline event.
        tuning.HistoryAdded += message =>
        {
            var colon = message.IndexOf(": ", StringComparison.Ordinal);
            var title = colon is > 0 and < 32 ? $"Tuning · {message[..colon]}" : "Tuning";
            _timeline.Add(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Subsystem.Tuning, Severity.Info, title, colon is > 0 and < 32 ? message[(colon + 2)..] : message);
        };
    }

    public WaitStateAnalyzer Wait { get; }

    public ExperimentRunner Experiments { get; }

    // ============================================================ ETW sink (consumer thread)

    public void DiskIo(int pid, bool write, int bytes, double elapsedMs, double deviceMs) => _io.Record(pid, write, bytes, elapsedMs, deviceMs);

    public void Network(long unixMs, int bytes, bool receive, int pid)
    {
        Interlocked.Increment(ref _packets);
        _bursts.Add(unixMs, bytes, receive, pid);
    }

    public void HardFault(int pid)
    {
        Interlocked.Increment(ref _hardFaults);
        _faultTable.Add(pid);
    }

    public void Retransmit() => Interlocked.Increment(ref _retransmits);

    // ============================================================ per interval (engine thread)

    public void OnTick(in MetricSample sample, long eventsProcessed, int eventsLost, bool etwActive, long tickMs, double intervalMs)
    {
        var wall = sample.TimestampUnixMs;
        var seconds = Math.Max(0.001, intervalMs / 1000.0);
        _etwActive = etwActive;

        var pressure = SamplePressure(tickMs);
        var hfTotal = Interlocked.Read(ref _hardFaults);
        var retxTotal = Interlocked.Read(ref _retransmits);
        var pktTotal = Interlocked.Read(ref _packets);
        var hardFaults = etwActive ? (hfTotal - _lastHardFaults) / seconds : pressure.HardFaultPagesPerSec;
        var retx = (retxTotal - _lastRetransmits) / seconds;
        var packets = (pktTotal - _lastPackets) / seconds;
        (_lastHardFaults, _lastRetransmits, _lastPackets) = (hfTotal, retxTotal, pktTotal);

        var (p99, ioCount) = _io.HarvestSecond(tickMs);
        var availablePct = sample.MemoryTotalMb > 0 ? 100.0 * sample.MemoryAvailableMb / sample.MemoryTotalMb : 100;

        lock (_gate)
        {
            _lastTickWallMs = wall;
            _lastQueueLength = pressure.DiskQueueLength;
            UpdateTelemetryHealth(eventsProcessed, eventsLost, seconds, wall);

            // ---- F7: bursts closed in this interval become timeline events
            foreach (var b in _bursts.Evaluate(wall))
            {
                var name = b.Pid > 0 ? NameOf(b.Pid) : null;
                _timeline.Add(b.TimeUnixMs, Subsystem.Network, b.Mbps >= 200 ? Severity.Warning : Severity.Info,
                    $"Network {(b.Receive ? "download" : "upload")} microburst",
                    $"{b.Mbps:F0} Mbit/s for {b.DurationMs} ms (baseline {b.BaselineMbps:F1} Mbit/s){(name is null ? "" : $" — {name}")}.",
                    pid: b.Pid > 0 ? b.Pid : null, processName: name);
            }

            // ---- F1: stall detection
            var signals = new StallSignals(wall, sample.CpuPercent, p99, pressure.DiskQueueLength, hardFaults, availablePct, sample.CpuLimitPercent, retx);
            var stalled = false;
            foreach (var issue in _detector.Evaluate(signals))
            {
                var e = _timeline.Add(wall, issue.Subsystem, issue.Severity, issue.Title, issue.Detail, issue.IsStall);
                if (issue.IsStall)
                {
                    stalled = true;
                    RememberStall(e.Id, CaptureContext(tickMs));
                }
            }

            // ---- per-second ring
            ref var slot = ref _seconds[_secondHead];
            slot = new SecondStat
            {
                WallMs = wall, DiskP99 = p99, IoCount = ioCount, HardFaults = hardFaults, Retransmits = retx, Packets = packets,
                UsedMb = sample.MemoryTotalMb - sample.MemoryAvailableMb, Stall = stalled,
            };
            _secondHead = (_secondHead + 1) % SecondsKept;
            _secondCount = Math.Min(SecondsKept, _secondCount + 1);

            // ---- periodic: process context + memory governor (F3)
            if (tickMs - _lastContextMs >= ContextIntervalMs)
            {
                _latestContext = CaptureContext(tickMs);
                _memory = new MemoryStats(sample.MemoryAvailableMb, sample.MemoryTotalMb, sample.CommitPercent, UsedGrowthPerMinute(),
                    hardFaults, Math.Max(0, pressure.PageFaultsPerSec - pressure.HardFaultPagesPerSec));
                _verdict = MemoryBehaviorClassifier.Classify(_memory);
                RunGovernor(tickMs, wall);
            }

            // ---- F2: minute KPIs
            AccumulateMinute(p99, ioCount, hardFaults, retx, sample.CpuLimitPercent, tickMs, wall);
        }

        Experiments.OnTick(tickMs, wall);
        MaybeStartAutopilot(tickMs, wall);

        if (tickMs - _lastPersistMs >= PersistIntervalMs)
        {
            _lastPersistMs = tickMs;
            Persist();
        }
    }

    private PressureSample SamplePressure(long tickMs)
    {
        if (_pressure is null && tickMs >= _pressureRetryMs)
        {
            try
            {
                _pressure = new SystemPressureCounters();
            }
            catch (InvalidOperationException ex)
            {
                _pressureRetryMs = tickMs + 300_000;
                LogPressureUnavailable(ex.Message);
            }
        }
        try
        {
            return _pressure?.Sample() ?? default;
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }

    private void UpdateTelemetryHealth(long eventsProcessed, int eventsLost, double seconds, long wall)
    {
        var events = eventsProcessed - _lastEvents;
        _lastEvents = eventsProcessed;
        _eventsPerSec = Math.Max(0, events / seconds);

        var lost = Math.Max(0, eventsLost - _lastLost);
        _lastLost = eventsLost;
        _lostRing[_lostHead] = lost;
        _lostHead = (_lostHead + 1) % _lostRing.Length;
        _lostLastMinute = _lostRing.Sum();

        if (lost > 0 && wall - _lastLossEventMs > 60_000)
        {
            _lastLossEventMs = wall;
            _timeline.Add(wall, Subsystem.Telemetry, Severity.Warning, "Telemetry events dropped",
                $"The kernel dropped {lost} ETW events (consumer fell behind). Latency and burst figures for this moment are incomplete.");
        }
    }

    // ------------------------------------------------------------ stall context

    private StallContext? CaptureContext(long tickMs)
    {
        try
        {
            _snapshotter.Snapshot(_procs);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        var elapsedS = Math.Max(1, (tickMs - _lastContextMs) / 1000.0);
        _lastContextMs = tickMs;
        var fg = _tuning.ForegroundPid(tickMs);

        var byPid = new Dictionary<int, ProcSample>(_procs.Count);
        foreach (var p in _procs) { byPid[p.Pid] = p; }

        Culprit C(in ProcSample s, double value)
            => new(s.Pid, s.CreateTime, s.Name, Math.Round(value, 1),
                s.Pid != fg && ProcessPolicy.CanControl(s.Pid, s.Name, s.SessionId, _selfPid, out _));

        var topCpu = _procs.Where(p => p.Pid > 4).OrderByDescending(p => p.CpuPercent).Take(3).Select(p => C(p, p.CpuPercent)).ToList();
        var topPrivate = _procs.Where(p => p.Pid > 4).OrderByDescending(p => p.PrivateBytes).Take(3).Select(p => C(p, p.PrivateBytes / 1048576.0)).ToList();
        var topDisk = _io.TopByThroughput(3, tickMs).Where(x => byPid.ContainsKey(x.Pid)).Select(x => C(byPid[x.Pid], x.MegabytesPerSec)).ToList();
        var topFaults = _faultTable.DrainTop(3).Where(x => byPid.ContainsKey(x.Pid)).Select(x => C(byPid[x.Pid], x.Count / elapsedS)).ToList();
        return new StallContext(topCpu, topDisk, topFaults, topPrivate);
    }

    private void RememberStall(long eventId, StallContext? context)
    {
        var n = _state.History.CopyLatest(_sampleScratch);
        _stalls[eventId] = new StallRecord(context, _sampleScratch.AsSpan(0, n).ToArray());
        _stallOrder.Enqueue(eventId);
        while (_stallOrder.Count > MaxStallRecords) { _stalls.Remove(_stallOrder.Dequeue()); }
    }

    private string? NameOf(int pid)
    {
        foreach (var p in _procs)
        {
            if (p.Pid == pid) { return p.Name; }
        }
        return null;
    }

    // ------------------------------------------------------------ F3 memory governor

    private void RunGovernor(long tickMs, long wall)
    {
        var profile = _tuning.Profile;
        if (profile < TuningProfile.Balanced)
        {
            if (_governed.Count > 0)
            {
                _tuning.RevertOwner(ChangeOwners.Governor);
                _governed.Clear();
                GovernorNote(wall, "Profile is Safe — governor changes reverted.");
            }
            _pressureTicks = _stableTicks = 0;
            return;
        }

        // Low free RAM counts as pressure too (the classifier alone waits for < 12 % free).
        var lowRam = _memory.TotalMb > 0 && _memory.AvailableMb / _memory.TotalMb < 0.20;
        _pressureTicks = _verdict.Action != MemoryActionKind.None || lowRam ? _pressureTicks + 1 : 0;
        _stableTicks = _verdict.Behavior == MemoryBehavior.Stable && !lowRam ? _stableTicks + 1 : 0;

        // Release once memory has been stable for a minute.
        if (_stableTicks >= 12 && _governed.Count > 0)
        {
            var n = _tuning.RevertOwner(ChangeOwners.Governor);
            _governed.Clear();
            GovernorNote(wall, $"Memory stable for 60 s — released {n} app(s).");
            _timeline.Add(wall, Subsystem.Memory, Severity.Info, "Memory governor released", "Memory is stable again; memory priorities restored.");
            return;
        }

        // Act only on sustained (≥ 10 s) pressure, at most once a minute.
        if (_pressureTicks < 2 || tickMs - _lastGovernorActionMs < GovernorActionIntervalMs || !_tuning.TuningAllowed)
        {
            return;
        }
        _lastGovernorActionMs = tickMs;
        _governed.RemoveWhere(pid => !_procs.Any(p => p.Pid == pid));

        var fg = _tuning.ForegroundPid(tickMs);
        var candidates = _procs
            .Where(p => p.Pid > 4 && p.Pid != fg && p.PrivateBytes >= GovernorMinPrivateBytes && !_governed.Contains(p.Pid)
                && ProcessPolicy.CanControl(p.Pid, p.Name, p.SessionId, _selfPid, out _))
            .OrderByDescending(p => p.PrivateBytes)
            .Take(Math.Max(0, GovernorMaxTargets - _governed.Count))
            .ToList();

        var acted = new List<string>();
        foreach (var c in candidates)
        {
            var r = _tuning.ApplyAction(new ProcessActionRequest
            {
                Id = 0, Pid = c.Pid, CreateTime = c.CreateTime, Action = ProcessAction.MemoryPriority, MemoryPriority = MemoryPriorityLevel.Low,
            }, tickMs, ChangeOwners.Governor);
            if (r.Success)
            {
                _governed.Add(c.Pid);
                acted.Add($"{c.Name} ({c.PrivateBytes / 1048576} MB)");
            }
        }

        // Free RAM like the RAM Refresh button does (at most every 3 minutes), and say how much.
        var freedText = "";
        if (tickMs - _lastGovernorTrimMs >= GovernorTrimIntervalMs)
        {
            _lastGovernorTrimMs = tickMs;
            _tuning.TrimBackground(0, tickMs, "MemGuard", out var freed);
            if (freed > 0) { freedText = $"freed {freed / 1048576} MB"; }
        }

        if (acted.Count > 0 || freedText.Length > 0)
        {
            var msg = string.Join("; ", new[] { freedText, acted.Count > 0 ? $"low memory priority for {string.Join(", ", acted)}" : "" }.Where(x => x.Length > 0)) + ".";
            GovernorNote(wall, msg);
            if (acted.Count > 0) { _tuning.Note("MemGuard: " + msg); }
        }
    }

    private void GovernorNote(long wall, string message)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(wall).ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        _governorHistory.Enqueue($"{t} {message}");
        while (_governorHistory.Count > 20) { _governorHistory.Dequeue(); }
    }

    private double UsedGrowthPerMinute()
    {
        if (_secondCount < 2) { return 0; }
        var newest = _seconds[(_secondHead - 1 + SecondsKept) % SecondsKept];
        for (var k = 2; k <= _secondCount; k++)
        {
            var s = _seconds[(_secondHead - k + SecondsKept) % SecondsKept];
            if (newest.WallMs - s.WallMs >= 60_000 || k == _secondCount)
            {
                var minutes = Math.Max(1.0 / 60, (newest.WallMs - s.WallMs) / 60_000.0);
                return (newest.UsedMb - s.UsedMb) / minutes;
            }
        }
        return 0;
    }

    // ------------------------------------------------------------ F2 regressions

    private void AccumulateMinute(double p99, long ioCount, double hardFaults, double retx, double limit, long tickMs, long wall)
    {
        if (_minuteStartMs == 0) { _minuteStartMs = tickMs; }
        if (ioCount > 0) { _minP99Sum += p99; _minP99N++; }
        _minHfSum += hardFaults;
        _minRetxSum += retx;
        if (limit is > 0 and < 90) { _minLimited++; }
        _minTicks++;

        if (tickMs - _minuteStartMs < 60_000) { return; }
        var ticks = Math.Max(1, _minTicks);
        Span<double> values =
        [
            _minP99N > 0 ? _minP99Sum / _minP99N : 0,
            _timeline.CountStalls(wall - 3_600_000),
            _minHfSum / ticks,
            _minRetxSum / ticks,
            100.0 * _minLimited / ticks,
        ];
        _regressions.ObserveMinute(values);
        (_minuteStartMs, _minP99Sum, _minP99N, _minHfSum, _minRetxSum, _minLimited, _minTicks) = (tickMs, 0, 0, 0, 0, 0, 0);

        var current = _regressions.Regressions();
        var names = current.Select(r => r.Kpi.Name).ToHashSet();
        foreach (var gone in _regressionSince.Keys.Where(k => !names.Contains(k)).ToList()) { _regressionSince.Remove(gone); }
        foreach (var (kpi, baseline, now) in current)
        {
            if (_regressionSince.TryAdd(kpi.Name, wall))
            {
                _timeline.Add(wall, kpi.Subsystem, Severity.Warning, $"Regression: {kpi.Name}",
                    $"{kpi.Name} is {now:F1} {kpi.Unit} over the last 10 minutes vs a baseline of {baseline:F1} {kpi.Unit}.");
            }
        }
    }

    private List<Suggestion> SuggestionsFor(Subsystem subsystem)
    {
        var ctx = _latestContext;
        if (ctx is null) { return []; }
        var (list, contract, verb) = subsystem switch
        {
            Subsystem.Disk => (ctx.TopDiskMbps, "priority-below", "Lower the priority of the heaviest background disk user"),
            Subsystem.Memory => (ctx.TopPrivateMb, "memory-priority-low", "Give the largest background app low memory priority"),
            Subsystem.Cpu or Subsystem.Scheduler => (ctx.TopCpu, "efficiency-mode", "Put the top background CPU user in efficiency mode"),
            _ => ((IReadOnlyList<Culprit>)[], "", ""),
        };
        return [.. list.Where(c => c.Background && c.Pid > 4).Take(2).Select(c => new Suggestion
        {
            ContractId = contract, Pid = c.Pid, CreateTime = c.CreateTime, ProcessName = c.Name,
            Description = $"{verb}: {c.Name} (PID {c.Pid}, {c.Value:F1}). Runs as an A/B experiment — kept only if it measurably helps.",
        })];
    }

    private void MaybeStartAutopilot(long tickMs, long wall)
    {
        if (_tuning.Profile != TuningProfile.Advanced || !Experiments.AutonomousDue(tickMs))
        {
            return;
        }
        Suggestion? pick = null;
        lock (_gate)
        {
            foreach (var (kpi, _, _) in _regressions.Regressions())
            {
                pick = SuggestionsFor(kpi.Subsystem).FirstOrDefault(s => !Experiments.RecentlyTried(s.ContractId, s.ProcessName, tickMs));
                if (pick is not null) { break; }
            }
        }
        if (pick is null) { return; }
        _ = Experiments.Start(0, pick.ContractId, pick.Pid, pick.CreateTime, autonomous: true, tickMs, wall); // logged via the tuning history → timeline
    }

    // ------------------------------------------------------------ window stats (experiments)

    private WindowKpis WindowStats(long fromWall, long toWall)
    {
        lock (_gate)
        {
            int stalls = 0, p99N = 0, n = 0;
            double p99Sum = 0, hfSum = 0;
            for (var k = 1; k <= _secondCount; k++)
            {
                var s = _seconds[(_secondHead - k + SecondsKept) % SecondsKept];
                if (s.WallMs < fromWall) { break; }
                if (s.WallMs > toWall) { continue; }
                n++;
                hfSum += s.HardFaults;
                if (s.Stall) { stalls++; }
                if (s.IoCount > 0) { p99Sum += s.DiskP99; p99N++; }
            }
            return new WindowKpis(stalls, p99N > 0 ? p99Sum / p99N : 0, n > 0 ? hfSum / n : 0);
        }
    }

    // ============================================================ IPC queries

    public (double EventsPerSec, long EventsLost, string Health) TelemetryHealth()
    {
        lock (_gate)
        {
            var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastTickWallMs;
            var snapshot = _state.Snapshot;
            string health;
            if (_lastTickWallMs == 0) { health = "Starting"; }
            else if (ageMs > 5_000) { health = $"Stale: no sample for {ageMs / 1000} s"; }
            else if (!_etwActive) { health = "Fallback (PDH counters) — no per-event latency or bursts"; }
            else if (snapshot.Degraded) { health = "Degraded — reduced detail to stay within the overhead budget"; }
            else if (_lostLastMinute > 0) { health = $"Lossy — {_lostLastMinute} events dropped in the last minute"; }
            else { health = "Healthy"; }
            return (Math.Round(_eventsPerSec), _lastLost, health);
        }
    }

    public TimelineResponse GetTimeline(uint id, long sinceUnixMs, int max)
    {
        var events = _timeline.Since(sinceUnixMs, Math.Clamp(max, 1, ProtocolConstants.MaxTimelineEvents));
        return new TimelineResponse
        {
            Id = id, Events = events,
            StallsLastHour = _timeline.CountStalls(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 3_600_000),
        };
    }

    public ExplanationResponse ExplainStall(uint id, long eventId)
    {
        var stall = _timeline.Find(eventId);
        if (stall is null)
        {
            return new ExplanationResponse
            {
                Id = id, Found = false, Story = "That event is no longer in the timeline (only the most recent 1,000 events are kept).",
                WindowStartUnixMs = 0, WindowEndUnixMs = 0, Causes = [], Related = [],
            };
        }

        StallRecord? record;
        lock (_gate) { _stalls.TryGetValue(eventId, out record); }
        var from = stall.TimeUnixMs - StallExplainer.BeforeMs;
        var to = stall.TimeUnixMs + StallExplainer.AfterMs;

        var history = new MetricSample[ProtocolConstants.MaxHistoryPoints];
        var n = _state.History.CopyLatest(history);
        var samples = history.AsSpan(0, n).ToArray()
            .Concat(record?.Samples ?? [])
            .Where(s => s.TimestampUnixMs >= from && s.TimestampUnixMs <= to)
            .GroupBy(s => s.TimestampUnixMs).Select(g => g.First())
            .OrderBy(s => s.TimestampUnixMs)
            .ToList();

        return StallExplainer.Explain(id, stall, _timeline.Window(from, to), samples, record?.Context,
            _insights.LastLatency, _tuning.RecentChanges(stall.TimeUnixMs - 120_000));
    }

    public IoLatencyResponse GetIoLatency(uint id)
    {
        var (read, write, queue, device, histogram, hasDevice, rows) = _io.Snapshot();
        double queueLength;
        lock (_gate) { queueLength = _lastQueueLength; }
        var available = _etwActive;
        var processes = rows.Take(20).Select(r => new ProcessIoLatency
        {
            Pid = r.Pid, Name = r.Pid == 0 ? "(other / unattributed)" : NameOrPid(r.Pid),
            Reads = r.Reads, Writes = r.Writes, MegabytesTransferred = Math.Round(r.Bytes / 1048576.0, 1), Total = r.Latency,
        }).ToArray();
        return new IoLatencyResponse
        {
            Id = id, Available = available,
            Message = !available
                ? "DiskLens needs full power: start PerfTuner as administrator (or install the always-on service)."
                : read.Count + write.Count == 0 ? "No disk IO completed in the last minute." :
                  hasDevice ? $"Last {IoLatencyTracker.WindowSeconds} s of disk IO. Queue = time waiting in Windows/driver queues; device = time the drive itself took."
                            : $"Last {IoLatencyTracker.WindowSeconds} s of disk IO (this storage driver does not report device service time, so queue/device split is unavailable).",
            WindowSeconds = IoLatencyTracker.WindowSeconds, ReadTotal = read, WriteTotal = write, Queue = queue, Device = device,
            Histogram = histogram, DiskQueueLength = Math.Round(queueLength, 2), Processes = processes,
        };
    }

    public NetworkHealthResponse GetNetworkHealth(uint id)
    {
        var (rx, tx, peakRx, peakTx) = _bursts.Series();
        var bursts = _bursts.RecentBursts().Take(30).Select(b => new NetworkBurst
        {
            TimeUnixMs = b.TimeUnixMs, Direction = b.Receive ? "Download" : "Upload", MegabitsPerSecond = b.Mbps,
            BaselineMegabitsPerSecond = b.BaselineMbps, DurationMs = b.DurationMs,
            Pid = b.Pid > 0 ? b.Pid : null, ProcessName = b.Pid > 0 ? NameOrPid(b.Pid) : null,
        }).ToArray();

        double retxMinute = 0, packetsMinute = 0;
        lock (_gate)
        {
            for (var k = 1; k <= Math.Min(60, _secondCount); k++)
            {
                var s = _seconds[(_secondHead - k + SecondsKept) % SecondsKept];
                retxMinute += s.Retransmits;
                packetsMinute += s.Packets;
            }
        }
        var ratePct = packetsMinute > 0 ? 100 * retxMinute / packetsMinute : 0;
        _state.History.TryGetLatest(out var latest);
        var health = !_etwActive ? "Unknown (needs kernel ETW)"
            : ratePct >= 2 ? "Lossy — many retransmissions (packet loss or congestion)"
            : bursts.Any(b => b.TimeUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000) ? "Bursty — microbursts in the last minute"
            : "Good";

        return new NetworkHealthResponse
        {
            Id = id, Available = _etwActive,
            Message = _etwActive ? "Throughput binned at 100 ms from kernel TCP/UDP events; a microburst is ≥ 10 Mbit/s and ≥ 5× the 5 s median."
                                 : "BurstRadar needs full power: start PerfTuner as administrator (or install the always-on service).",
            RxMbpsNow = Math.Round(latest.NetRxBytesPerSec * 8 / 1_000_000, 2), TxMbpsNow = Math.Round(latest.NetTxBytesPerSec * 8 / 1_000_000, 2),
            PeakRxMbps100ms = peakRx, PeakTxMbps100ms = peakTx,
            RetransmitsLastMinute = (long)Math.Round(retxMinute), RetransmitRatePercent = Math.Round(ratePct, 2),
            Health = health, Bursts = bursts, RxSeries = rx, TxSeries = tx,
        };
    }

    public MemoryGovernorResponse GetMemoryGovernor(uint id)
    {
        lock (_gate)
        {
            var active = _tuning.Profile >= TuningProfile.Balanced;
            return new MemoryGovernorResponse
            {
                Id = id, Behavior = _verdict.Behavior, Explanation = _verdict.Explanation,
                Decision = active ? _verdict.Decision : _verdict.Decision + " (Governor is off in the Safe profile — switch to Balanced to let it act.)",
                AvailableMb = _memory.AvailableMb, CommitPercent = Math.Round(_memory.CommitPercent, 1),
                HardFaultsPerSec = Math.Round(_memory.HardFaultsPerSec, 1), SoftFaultsPerSec = Math.Round(_memory.SoftFaultsPerSec),
                CommitGrowthMbPerMin = Math.Round(_memory.CommitGrowthMbPerMin), GovernorActive = active,
                History = [.. _governorHistory.Reverse()],
            };
        }
    }

    public RegressionsResponse GetRegressions(uint id)
    {
        lock (_gate)
        {
            var changes = _tuning.RecentChanges(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 2 * 3_600_000);
            var items = _regressions.Regressions().Select(r =>
            {
                var suggestions = SuggestionsFor(r.Kpi.Subsystem);
                var causes = new List<string>();
                var ctx = _latestContext;
                if (ctx is not null)
                {
                    var culprit = r.Kpi.Subsystem switch
                    {
                        Subsystem.Disk => ctx.TopDiskMbps.FirstOrDefault(),
                        Subsystem.Memory => ctx.TopHardFaults.FirstOrDefault(),
                        Subsystem.Cpu => ctx.TopCpu.FirstOrDefault(),
                        _ => default,
                    };
                    if (culprit.Pid > 0) { causes.Add($"Top contributor now: {culprit.Name} (PID {culprit.Pid}, {culprit.Value:F1})."); }
                }
                causes.AddRange(changes.Take(3).Select(c => $"Tuning change at {DateTimeOffset.FromUnixTimeMilliseconds(c.TimeUnixMs).ToLocalTime():HH:mm}: {c.Description}"));
                if (r.Kpi.Subsystem == Subsystem.Power) { causes.Add("Check cooling, power plan and battery saver — the CPU is being limited by Windows or firmware."); }
                if (r.Kpi.Subsystem == Subsystem.Network) { causes.Add("Retransmissions usually mean Wi-Fi interference, a congested link or a failing cable."); }
                return new RegressionItem
                {
                    Kpi = r.Kpi.Name, Subsystem = r.Kpi.Subsystem, Baseline = r.Baseline, Current = r.Current,
                    ChangePercent = r.Baseline > 0 ? Math.Round(100 * (r.Current - r.Baseline) / r.Baseline) : 100,
                    Unit = r.Kpi.Unit, SuspectedCauses = [.. causes], Suggestions = [.. suggestions],
                    DetectedUnixMs = _regressionSince.TryGetValue(r.Kpi.Name, out var t) ? t : 0,
                };
            }).ToArray();

            var hours = _regressions.BaselineHours;
            var summary = hours < 1
                ? $"Learning this PC's normal behaviour: {hours * 60:F0} of 60 minutes of baseline collected. Regressions are reported after that."
                : items.Length == 0
                    ? $"No regressions: every KPI is within its normal range ({hours:F1} h of baseline)."
                    : $"{items.Length} KPI(s) worse than this PC's normal: {string.Join(", ", items.Select(i => $"{i.Kpi} +{i.ChangePercent:F0}%"))}.";
            return new RegressionsResponse { Id = id, BaselineHours = Math.Round(hours, 1), Items = items, Summary = summary };
        }
    }

    private string NameOrPid(int pid)
    {
        lock (_gate) { return NameOf(pid) ?? $"PID {pid}"; }
    }

    // ============================================================ persistence

    private void Load()
    {
        try
        {
            if (!File.Exists(_stateFile)) { return; }
            var info = new FileInfo(_stateFile);
            if (info.Length > 1_048_576) { return; } // bounded: ignore oversized state
            var state = JsonSerializer.Deserialize(File.ReadAllText(_stateFile), ForensicsJsonContext.Default.ForensicsState);
            if (state is null) { return; }
            _regressions.Import(state.Baseline ?? [], state.BaselineMinutes ?? []);
            Experiments.ImportHistory(state.Experiments);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogStateUnreadable(ex.GetType().Name);
        }
    }

    private void Persist()
    {
        try
        {
            ForensicsState state;
            lock (_gate)
            {
                var (baseline, minutes) = _regressions.Export();
                state = new ForensicsState { Baseline = baseline, BaselineMinutes = minutes, Experiments = Experiments.ExportHistory() };
            }
            var tmp = _stateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, ForensicsJsonContext.Default.ForensicsState));
            File.Move(tmp, _stateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStateUnwritable(ex.GetType().Name);
        }
    }

    public void Shutdown()
    {
        Experiments.Shutdown(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Persist();
    }

    public void Dispose()
    {
        _pressure?.Dispose();
        _snapshotter.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Paging/disk-queue counters unavailable: {Reason}")]
    private partial void LogPressureUnavailable(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Forensics state unreadable ({ErrorType}); starting fresh")]
    private partial void LogStateUnreadable(string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save forensics state ({ErrorType})")]
    private partial void LogStateUnwritable(string errorType);

    /// <summary>Per-PID hard-fault counts between context captures: 64 fixed slots, overflow ignored.</summary>
    private sealed class HardFaultTable
    {
        private const int Slots = 64;
        private readonly Lock _gate = new();
        private readonly int[] _pids = new int[Slots];
        private readonly long[] _counts = new long[Slots];

        public void Add(int pid)
        {
            if (pid <= 0) { return; }
            var start = (int)((uint)pid * 2654435769u >> 26);
            lock (_gate)
            {
                for (var i = 0; i < 8; i++)
                {
                    var s = (start + i) & (Slots - 1);
                    if (_pids[s] == pid) { _counts[s]++; return; }
                    if (_pids[s] == 0) { _pids[s] = pid; _counts[s] = 1; return; }
                }
            }
        }

        public List<(int Pid, long Count)> DrainTop(int n)
        {
            lock (_gate)
            {
                var list = new List<(int, long)>();
                for (var s = 0; s < Slots; s++)
                {
                    if (_pids[s] > 0) { list.Add((_pids[s], _counts[s])); }
                }
                Array.Clear(_pids);
                Array.Clear(_counts);
                return [.. list.OrderByDescending(x => x.Item2).Take(n)];
            }
        }
    }
}
