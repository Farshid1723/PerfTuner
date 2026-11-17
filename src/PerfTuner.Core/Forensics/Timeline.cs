using PerfTuner.Contracts;

namespace PerfTuner.Core.Forensics;

/// <summary>Bounded, thread-safe ring of correlated events (F1). 1,000 events ≈ 200 KB worst case.</summary>
public sealed class Timeline(int capacity = 1000)
{
    private readonly Lock _gate = new();
    private readonly Queue<TimelineEvent> _events = new(capacity);
    private long _nextId;

    public TimelineEvent Add(long timeUnixMs, Subsystem subsystem, Severity severity, string title, string detail,
        bool isStall = false, int? pid = null, string? processName = null)
    {
        lock (_gate)
        {
            var e = new TimelineEvent
            {
                Id = ++_nextId,
                TimeUnixMs = timeUnixMs,
                Subsystem = subsystem,
                Severity = severity,
                Title = Clip(title, 120),
                Detail = Clip(detail, 400),
                IsStall = isStall,
                Pid = pid,
                ProcessName = processName is null ? null : Clip(processName, 64),
            };
            _events.Enqueue(e);
            while (_events.Count > capacity) { _events.Dequeue(); }
            return e;
        }
    }

    public TimelineEvent[] Since(long sinceUnixMs, int max)
    {
        lock (_gate)
        {
            return [.. _events.Where(e => e.TimeUnixMs >= sinceUnixMs).OrderByDescending(e => e.TimeUnixMs).Take(max)];
        }
    }

    public TimelineEvent[] Window(long fromUnixMs, long toUnixMs)
    {
        lock (_gate)
        {
            return [.. _events.Where(e => e.TimeUnixMs >= fromUnixMs && e.TimeUnixMs <= toUnixMs).OrderBy(e => e.TimeUnixMs)];
        }
    }

    public TimelineEvent? Find(long id)
    {
        lock (_gate) { return _events.FirstOrDefault(e => e.Id == id); }
    }

    public int CountStalls(long sinceUnixMs)
    {
        lock (_gate) { return _events.Count(e => e.IsStall && e.TimeUnixMs >= sinceUnixMs); }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Per-interval signals the stall detector evaluates.</summary>
public readonly record struct StallSignals(
    long TimeUnixMs,
    double CpuPercent,
    double DiskP99Ms,
    double DiskQueueLength,
    double HardFaultsPerSec,
    double AvailableMemoryPercent,
    double CpuLimitPercent,
    double RetransmitsPerSec);

public readonly record struct DetectedIssue(Subsystem Subsystem, Severity Severity, string Title, string Detail, bool IsStall);

/// <summary>
/// Turns raw signals into timeline events with hysteresis: a condition must persist
/// (CPU: 3 consecutive intervals) and each rule has a 30 s cooldown, so one incident yields
/// one event, not one per second.
/// </summary>
public sealed class StallDetector
{
    private const long CooldownMs = 30_000;
    private readonly Dictionary<string, long> _lastFired = [];
    private int _cpuHighTicks;
    private bool _limited;

    public List<DetectedIssue> Evaluate(in StallSignals s)
    {
        var issues = new List<DetectedIssue>();
        _cpuHighTicks = s.CpuPercent >= 95 ? _cpuHighTicks + 1 : 0;

        if (_cpuHighTicks >= 3)
        {
            Fire("cpu", s.TimeUnixMs, issues, new(Subsystem.Cpu, Severity.Warning, "CPU saturated",
                $"CPU at {s.CpuPercent:F0}% for {_cpuHighTicks} consecutive intervals — foreground apps compete for CPU time.", true));
        }
        if (s.DiskP99Ms >= 200 || s.DiskQueueLength >= 8)
        {
            Fire("disk", s.TimeUnixMs, issues, new(Subsystem.Disk, s.DiskP99Ms >= 1000 ? Severity.Critical : Severity.Warning, "Storage stall",
                $"Disk p99 latency {s.DiskP99Ms:F0} ms, queue length {s.DiskQueueLength:F1} — apps waiting on disk will freeze.", true));
        }
        if (s.HardFaultsPerSec >= 1000 && s.AvailableMemoryPercent < 10)
        {
            Fire("thrash", s.TimeUnixMs, issues, new(Subsystem.Memory, Severity.Critical, "Memory thrashing",
                $"{s.HardFaultsPerSec:F0} hard page faults/s with only {s.AvailableMemoryPercent:F0}% RAM available — the system is paging to disk.", true));
        }
        else if (s.HardFaultsPerSec >= 300 && s.AvailableMemoryPercent < 20)
        {
            Fire("paging", s.TimeUnixMs, issues, new(Subsystem.Memory, Severity.Warning, "Heavy paging",
                $"{s.HardFaultsPerSec:F0} hard page faults/s (data read back from disk).", false));
        }

        var limitedNow = s.CpuLimitPercent is > 0 and < 90;
        if (limitedNow && !_limited)
        {
            Fire("limit", s.TimeUnixMs, issues, new(Subsystem.Power, Severity.Warning, "CPU performance limited",
                $"Windows limits the CPU to {s.CpuLimitPercent:F0}% (thermal, power or firmware limit).", false));
        }
        _limited = limitedNow;

        if (s.RetransmitsPerSec >= 20)
        {
            Fire("retx", s.TimeUnixMs, issues, new(Subsystem.Network, Severity.Warning, "TCP retransmissions",
                $"{s.RetransmitsPerSec:F0} TCP segments/s retransmitted — packet loss or congestion.", false));
        }
        return issues;
    }

    private void Fire(string rule, long now, List<DetectedIssue> into, DetectedIssue issue)
    {
        if (_lastFired.TryGetValue(rule, out var last) && now - last < CooldownMs)
        {
            return;
        }
        _lastFired[rule] = now;
        into.Add(issue);
    }
}
