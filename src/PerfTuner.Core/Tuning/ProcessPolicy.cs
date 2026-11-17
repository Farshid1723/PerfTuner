namespace PerfTuner.Core.Tuning;

/// <summary>
/// Which processes may be touched at all. Deliberately conservative:
/// <list type="bullet">
/// <item>Only processes in an interactive user session (session ≠ 0). Everything in
///   session 0 is a system service; changing it can break Windows in subtle ways.</item>
/// <item>A hard-coded deny list of shell and security-critical processes, even in the
///   user session. It cannot be changed by configuration or IPC.</item>
/// </list>
/// </summary>
public static class ProcessPolicy
{
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "Secure System", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm", "fontdrvhost", "audiodg", "sihost",
        "ctfmon", "explorer", "taskmgr", "conhost", "dllhost", "RuntimeBroker", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "TextInputHost", "LockApp", "LogonUI", "userinit",
        "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray", "smartscreen", "consent",
        "WUDFHost", "spoolsv", "SgrmBroker", "PerfTuner", "PerfTuner.Service", "perftuner-cli",
    };

    public static bool CanControl(int pid, string name, int sessionId, int selfPid, out string reason)
    {
        if (pid <= 4)
        {
            reason = "kernel / system process";
            return false;
        }
        if (pid == selfPid)
        {
            reason = "the tuning service itself";
            return false;
        }
        if (sessionId == 0)
        {
            reason = "system service (session 0)";
            return false;
        }
        if (Critical.Contains(name))
        {
            reason = "protected Windows process";
            return false;
        }
        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// Per-process and global rate limits for tuning actions. Manual actions: at most one per
/// process every 2 s. Automatic actions: one per process every 30 s and 20 per minute overall.
/// Allocation-bounded: the per-process table is cleared of stale entries when it grows.
/// </summary>
public sealed class ActionThrottle
{
    private const long ManualPerProcessMs = 2_000;
    private const long AutoPerProcessMs = 30_000;
    private const int AutoPerMinute = 20;

    private readonly Dictionary<int, long> _lastByPid = [];
    private readonly Queue<long> _autoTimes = new();

    public bool TryAcquire(int pid, bool automatic, long nowMs)
    {
        if (_lastByPid.Count > 1024)
        {
            var stale = new List<int>();
            foreach (var kv in _lastByPid)
            {
                if (nowMs - kv.Value > AutoPerProcessMs) { stale.Add(kv.Key); }
            }
            foreach (var p in stale) { _lastByPid.Remove(p); }
        }

        var gap = automatic ? AutoPerProcessMs : ManualPerProcessMs;
        if (_lastByPid.TryGetValue(pid, out var last) && nowMs - last < gap)
        {
            return false;
        }

        if (automatic)
        {
            while (_autoTimes.Count > 0 && nowMs - _autoTimes.Peek() > 60_000)
            {
                _autoTimes.Dequeue();
            }
            if (_autoTimes.Count >= AutoPerMinute)
            {
                return false;
            }
            _autoTimes.Enqueue(nowMs);
        }
        _lastByPid[pid] = nowMs;
        return true;
    }
}
