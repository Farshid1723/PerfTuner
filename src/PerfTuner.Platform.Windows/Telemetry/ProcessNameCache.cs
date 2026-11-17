namespace PerfTuner.Platform.Windows.Telemetry;

/// <summary>
/// Bounded PID → display-name cache for the top-N IO list. Only called for ≤ 10 PIDs per
/// interval and only while a GUI is subscribed. Fully flushed every minute (and when
/// full) so a recycled PID cannot keep a stale name for long.
/// </summary>
public sealed class ProcessNameCache
{
    private const int Capacity = 256;
    private const int MaxNameLength = Contracts.ProtocolConstants.MaxProcessNameLength;
    private static readonly long FlushTicks = TimeSpan.FromMinutes(1).Ticks;

    private readonly Dictionary<int, string> _names = new(Capacity);
    private long _lastFlush = Environment.TickCount64;

    public string GetName(int pid)
    {
        if (pid == 0) { return "Idle"; }
        if (pid == 4) { return "System"; }

        var now = Environment.TickCount64;
        if (_names.Count >= Capacity || (now - _lastFlush) * TimeSpan.TicksPerMillisecond > FlushTicks)
        {
            _names.Clear();
            _lastFlush = now;
        }

        if (_names.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        var path = ProcessInfo.TryGetImagePath(pid);
        var name = path is null ? $"pid {pid}" : Sanitize(Path.GetFileNameWithoutExtension(path));
        _names[pid] = name;
        return name;
    }

    /// <summary>Image names are attacker-influenced strings; clamp length and strip control chars.</summary>
    private static string Sanitize(string name)
    {
        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength];
        }
        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                return string.Create(name.Length, name, static (span, src) =>
                {
                    for (var i = 0; i < src.Length; i++)
                    {
                        span[i] = char.IsControl(src[i]) ? '?' : src[i];
                    }
                });
            }
        }
        return name;
    }
}
