using System.Runtime.InteropServices;
using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows.Tuning;

public struct ProcSample
{
    public int Pid;
    public int SessionId;
    public long CreateTime;
    public double CpuPercent;
    public long WorkingSetBytes;
    public long PrivateBytes;
    public int BasePriority;
    public string Name;
}

/// <summary>
/// All processes in one syscall (NtQuerySystemInformation / SystemProcessInformation).
/// The native buffer is reused and only grown; names are cached per (pid, creation time),
/// so steady-state sampling allocates only the result list. Called at most every 2 s and
/// only while something needs it (process list open, or automation active).
/// </summary>
public sealed unsafe class ProcessSnapshotter : IDisposable
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // x64 SYSTEM_PROCESS_INFORMATION field offsets.
    private const int OffNext = 0;
    private const int OffCreateTime = 32;
    private const int OffUserTime = 40;
    private const int OffKernelTime = 48;
    private const int OffNameLength = 56;
    private const int OffNameBuffer = 64;
    private const int OffBasePriority = 72;
    private const int OffPid = 80;
    private const int OffSessionId = 100;
    private const int OffWorkingSet = 144;
    private const int OffPrivatePageCount = 200;

    private readonly Dictionary<int, (long Create, string Name)> _names = [];
    private Dictionary<int, (long Create, long Cpu)> _previous = [];
    private Dictionary<int, (long Create, long Cpu)> _current = [];
    private readonly int _processors = Environment.ProcessorCount;
    private byte* _buffer;
    private uint _size;
    private long _lastTimestamp;

    public ProcessSnapshotter()
    {
        if (IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException("x64 only");
        }
    }

    /// <summary>Fills <paramref name="output"/> (cleared first). CPU % is relative to the whole machine.</summary>
    public void Snapshot(List<ProcSample> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        Query();

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed100ns = _lastTimestamp == 0 ? 0 : (now - _lastTimestamp) * 10_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        _lastTimestamp = now;
        _current.Clear();

        var offset = 0;
        while (true)
        {
            var entry = _buffer + offset;
            var pid = (int)*(nint*)(entry + OffPid);
            var create = *(long*)(entry + OffCreateTime);
            var cpu = *(long*)(entry + OffUserTime) + *(long*)(entry + OffKernelTime);

            if (pid > 0)
            {
                double cpuPercent = 0;
                if (elapsed100ns > 0 && _previous.TryGetValue(pid, out var prev) && prev.Create == create)
                {
                    cpuPercent = Math.Clamp(100.0 * (cpu - prev.Cpu) / (elapsed100ns * _processors), 0, 100);
                }
                _current[pid] = (create, cpu);

                output.Add(new ProcSample
                {
                    Pid = pid,
                    SessionId = *(int*)(entry + OffSessionId),
                    CreateTime = create,
                    CpuPercent = cpuPercent,
                    WorkingSetBytes = (long)*(nuint*)(entry + OffWorkingSet),
                    PrivateBytes = (long)*(nuint*)(entry + OffPrivatePageCount),
                    BasePriority = *(int*)(entry + OffBasePriority),
                    Name = NameOf(entry, pid, create),
                });
            }

            var next = *(uint*)(entry + OffNext);
            if (next == 0)
            {
                break;
            }
            offset += (int)next;
        }

        (_previous, _current) = (_current, _previous);

        // Drop cached names of processes that are gone (bounded memory).
        if (_names.Count > output.Count * 2)
        {
            var alive = new HashSet<int>(output.Count);
            foreach (var p in output) { alive.Add(p.Pid); }
            foreach (var pid in _names.Keys.Where(pid => !alive.Contains(pid)).ToList()) { _names.Remove(pid); }
        }
    }

    private string NameOf(byte* entry, int pid, long create)
    {
        if (_names.TryGetValue(pid, out var cached) && cached.Create == create)
        {
            return cached.Name;
        }
        var length = *(ushort*)(entry + OffNameLength) / sizeof(char);
        var buffer = *(char**)(entry + OffNameBuffer);
        var name = buffer == null || length == 0 ? "?" : new string(buffer, 0, Math.Min(length, 260));
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        name = Sanitize(name);
        _names[pid] = (create, name);
        return name;
    }

    private static string Sanitize(string name)
    {
        if (name.Length > Contracts.ProtocolConstants.MaxProcessNameLength)
        {
            name = name[..Contracts.ProtocolConstants.MaxProcessNameLength];
        }
        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                return new string(name.Select(ch => char.IsControl(ch) ? '?' : ch).ToArray());
            }
        }
        return name;
    }

    private void Query()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (_buffer != null)
            {
                var status = ProcessApi.NtQuerySystemInformation(SystemProcessInformation, _buffer, _size, out var needed);
                if (status >= 0)
                {
                    return;
                }
                if (status != StatusInfoLengthMismatch)
                {
                    throw new InvalidOperationException($"NtQuerySystemInformation failed (0x{status:X8})");
                }
                Grow(needed + (64 * 1024));
            }
            else
            {
                Grow(512 * 1024);
            }
        }
        throw new InvalidOperationException("process snapshot buffer kept growing");
    }

    private void Grow(uint size)
    {
        // Hard ceiling: 16 MB is ~50k processes; anything beyond indicates a problem.
        if (size > 16 * 1024 * 1024)
        {
            throw new InvalidOperationException("process snapshot too large");
        }
        if (_buffer != null)
        {
            NativeMemory.Free(_buffer);
        }
        _buffer = (byte*)NativeMemory.Alloc(size);
        _size = size;
    }

    public void Dispose()
    {
        if (_buffer != null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
        }
    }
}
