using PerfTuner.Contracts;

namespace PerfTuner.Core.Forensics;

/// <summary>A process as captured at the moment an issue was detected.</summary>
public readonly record struct Culprit(int Pid, long CreateTime, string Name, double Value, bool Background);

/// <summary>Process context captured when a stall fires (top consumers per resource).</summary>
public sealed record StallContext(
    IReadOnlyList<Culprit> TopCpu,
    IReadOnlyList<Culprit> TopDiskMbps,
    IReadOnlyList<Culprit> TopHardFaults,
    IReadOnlyList<Culprit> TopPrivateMb);

/// <summary>
/// "Explain this stall" (F1). Evidence-based, rule-driven causal analysis over the correlated
/// window [t − 15 s, t + 3 s]:
/// <list type="number">
/// <item>Collect metric samples and timeline events in the window.</item>
/// <item>Each rule scores one hypothesis (CPU contention, storage saturation, memory thrash,
///   power limit, network, driver latency, recent tuning change) from 0..1: strength of the
///   signal × temporal overlap with the stall.</item>
/// <item>Hypotheses ≥ 0.25 become causes, sorted by confidence, each with concrete evidence
///   (values and timestamps) and, when a single process dominates, a suggested tuning contract.</item>
/// </list>
/// No machine learning, no guessing: every sentence in the story cites a measured value.
/// </summary>
public static class StallExplainer
{
    public const long BeforeMs = 15_000;
    public const long AfterMs = 3_000;

    public static ExplanationResponse Explain(
        uint id,
        TimelineEvent stall,
        IReadOnlyList<TimelineEvent> window,
        IReadOnlyList<MetricSample> samples,
        StallContext? context,
        LatencyReport? lastLatencyScan,
        IReadOnlyList<(long TimeUnixMs, string Description)> recentChanges)
    {
        ArgumentNullException.ThrowIfNull(stall);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(recentChanges);

        var from = stall.TimeUnixMs - BeforeMs;
        var to = stall.TimeUnixMs + AfterMs;
        var inWindow = samples.Where(s => s.TimestampUnixMs >= from && s.TimestampUnixMs <= to).ToList();
        var causes = new List<CauseFinding>();
        string T(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        if (inWindow.Count > 0)
        {
            // ---- CPU contention
            var cpuAvg = inWindow.Average(s => s.CpuPercent);
            var cpuPeak = inWindow.Max(s => s.CpuPercent);
            var cpuHighShare = inWindow.Count(s => s.CpuPercent >= 90) / (double)inWindow.Count;
            var cpuScore = Math.Clamp((cpuAvg - 70) / 25, 0, 1) * (0.5 + (0.5 * cpuHighShare));
            if (cpuScore >= 0.25)
            {
                var top = context?.TopCpu.FirstOrDefault();
                var evidence = new List<string> { $"CPU averaged {cpuAvg:F0}% (peak {cpuPeak:F0}%) between {T(from)} and {T(to)}; ≥ 90% for {cpuHighShare:P0} of that time." };
                if (top is { } t && t.Pid > 0) { evidence.Add($"Largest consumer at {T(stall.TimeUnixMs)}: {t.Name} (PID {t.Pid}) using {t.Value:F0}% of the machine."); }
                causes.Add(new CauseFinding
                {
                    Title = top is { Pid: > 0 } tp ? $"CPU contention, driven by {tp.Name}" : "CPU contention",
                    Subsystem = Subsystem.Cpu, Confidence = Math.Round(cpuScore, 2), Evidence = [.. evidence],
                    SuggestedContractId = top is { Pid: > 0, Background: true } ? "efficiency-mode" : null,
                    SuggestedPid = top is { Pid: > 0, Background: true } b1 ? b1.Pid : null,
                    SuggestedCreateTime = top is { Pid: > 0, Background: true } c1 ? c1.CreateTime : null,
                    SuggestedProcessName = top is { Pid: > 0, Background: true } b2 ? b2.Name : null,
                });
            }

            // ---- Power / thermal limit
            var limited = inWindow.Where(s => s.CpuLimitPercent is > 0 and < 95).ToList();
            if (limited.Count > 0)
            {
                var minLimit = limited.Min(s => s.CpuLimitPercent);
                var score = Math.Clamp((100 - minLimit) / 40, 0, 1) * (limited.Count / (double)inWindow.Count);
                if (score >= 0.25)
                {
                    causes.Add(new CauseFinding
                    {
                        Title = "CPU held back by power/thermal limit", Subsystem = Subsystem.Power, Confidence = Math.Round(score, 2),
                        Evidence = [$"Windows reported '% Performance Limit' down to {minLimit:F0}% in {limited.Count} of {inWindow.Count} intervals.",
                                    "Check cooling, the power plan's maximum processor state, and battery/energy-saver settings."],
                    });
                }
            }
        }

        // ---- Storage saturation (from timeline + context)
        var diskEvents = window.Where(e => e.Subsystem == Subsystem.Disk).ToList();
        if (diskEvents.Count > 0)
        {
            var topIo = context?.TopDiskMbps.FirstOrDefault();
            var score = Math.Min(1, 0.55 + (0.15 * diskEvents.Count) + (diskEvents.Any(e => e.Severity == Severity.Critical) ? 0.2 : 0));
            var evidence = diskEvents.Select(e => $"{T(e.TimeUnixMs)} {e.Title}: {e.Detail}").ToList();
            if (topIo is { Pid: > 0 } t) { evidence.Add($"Heaviest disk user at that moment: {t.Name} (PID {t.Pid}) at {t.Value:F1} MB/s."); }
            causes.Add(new CauseFinding
            {
                Title = topIo is { Pid: > 0 } tp ? $"Storage saturation, driven by {tp.Name}" : "Storage saturation",
                Subsystem = Subsystem.Disk, Confidence = Math.Round(score, 2), Evidence = [.. evidence],
                SuggestedContractId = topIo is { Pid: > 0, Background: true } ? "priority-below" : null,
                SuggestedPid = topIo is { Pid: > 0, Background: true } b ? b.Pid : null,
                SuggestedCreateTime = topIo is { Pid: > 0, Background: true } bc ? bc.CreateTime : null,
                SuggestedProcessName = topIo is { Pid: > 0, Background: true } bn ? bn.Name : null,
            });
        }

        // ---- Memory thrash / paging
        var memEvents = window.Where(e => e.Subsystem == Subsystem.Memory).ToList();
        if (memEvents.Count > 0)
        {
            var top = context?.TopPrivateMb.FirstOrDefault(c => c.Background);
            var score = memEvents.Any(e => e.Severity == Severity.Critical) ? 0.9 : 0.5;
            var evidence = memEvents.Select(e => $"{T(e.TimeUnixMs)} {e.Title}: {e.Detail}").ToList();
            if (context?.TopHardFaults.FirstOrDefault() is { Pid: > 0 } hf) { evidence.Add($"Most hard faults: {hf.Name} (PID {hf.Pid}), {hf.Value:F0}/s."); }
            if (top is { Pid: > 0 } t) { evidence.Add($"Largest background memory user: {t.Name} (PID {t.Pid}), {t.Value:F0} MB private."); }
            causes.Add(new CauseFinding
            {
                Title = "Memory pressure — data paged back from disk", Subsystem = Subsystem.Memory, Confidence = score, Evidence = [.. evidence],
                SuggestedContractId = top is { Pid: > 0 } ? "memory-priority-low" : null,
                SuggestedPid = top is { Pid: > 0 } b ? b.Pid : null,
                SuggestedCreateTime = top is { Pid: > 0 } bc ? bc.CreateTime : null,
                SuggestedProcessName = top is { Pid: > 0 } bn ? bn.Name : null,
            });
        }

        // ---- Network
        var netEvents = window.Where(e => e.Subsystem == Subsystem.Network).ToList();
        if (netEvents.Count > 0)
        {
            causes.Add(new CauseFinding
            {
                Title = "Network burst / congestion", Subsystem = Subsystem.Network,
                Confidence = Math.Round(Math.Min(0.8, 0.35 + (0.15 * netEvents.Count)), 2),
                Evidence = [.. netEvents.Select(e => $"{T(e.TimeUnixMs)} {e.Title}: {e.Detail}")],
            });
        }

        // ---- Driver latency (from the most recent scan, if recent)
        if (lastLatencyScan is { State: LatencyScanState.Completed, WorstUs: >= 1000 } scan
            && Math.Abs(stall.TimeUnixMs - scan.StartedUnixMs) < 30 * 60_000 && scan.Drivers.Length > 0)
        {
            causes.Add(new CauseFinding
            {
                Title = $"Driver latency ({scan.Drivers[0].Driver})", Subsystem = Subsystem.Driver, Confidence = 0.3,
                Evidence = [$"The latency scan at {T(scan.StartedUnixMs)} measured DPC/ISR bursts up to {scan.WorstUs:F0} µs in {scan.Drivers[0].Driver}. Such bursts freeze the CPU core they run on."],
            });
        }

        // ---- Recent tuning changes (never hide our own actions)
        foreach (var (time, description) in recentChanges.Where(c => c.TimeUnixMs >= stall.TimeUnixMs - 60_000 && c.TimeUnixMs <= stall.TimeUnixMs))
        {
            causes.Add(new CauseFinding
            {
                Title = "Recent tuning change", Subsystem = Subsystem.Tuning, Confidence = 0.25,
                Evidence = [$"{T(time)}: {description} ({(stall.TimeUnixMs - time) / 1000} s before the stall). Revert it on the Changes tab if the stall repeats."],
            });
        }

        var ranked = causes.Where(c => c.Confidence >= 0.25).OrderByDescending(c => c.Confidence).ToArray();
        var story = ranked.Length == 0
            ? $"{T(stall.TimeUnixMs)} {stall.Title}: no single subsystem shows enough evidence in the ±15 s window. It may have been caused inside one application (e.g. a lock or UI-thread wait) — run the CPU wait-state lens while it happens."
            : $"{T(stall.TimeUnixMs)} {stall.Title}: most likely {Lower(ranked[0].Title)} ({ranked[0].Confidence:P0} confidence). " +
              ranked[0].Evidence[0] +
              (ranked.Length > 1 ? $" Contributing: {string.Join("; ", ranked.Skip(1).Take(2).Select(c => $"{Lower(c.Title)} ({c.Confidence:P0})"))}." : "");

        return new ExplanationResponse
        {
            Id = id, Found = ranked.Length > 0, Story = story, WindowStartUnixMs = from, WindowEndUnixMs = to,
            Causes = ranked, Related = [.. window],
        };
    }

    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
