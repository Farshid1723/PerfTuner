using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PerfTuner.Contracts;
using PerfTuner.Core.Safety;
using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows.Tuning;

/// <summary>
/// Identity-verified process handles. Every operation re-opens the process and checks
/// its creation time, so a PID that has been recycled by a different program is never touched.
/// </summary>
public static class ProcessTarget
{
    public static SafeProcessHandle? Open(int pid, long createTime, uint access, out string error)
    {
        var handle = ProcessApi.OpenProcess(access | ProcessApi.QueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastPInvokeError();
            handle.Dispose();
            error = code == 5 ? "access denied (protected or elevated process)" : "process not found";
            return null;
        }
        if (!ProcessApi.GetProcessTimes(handle, out var created, out _, out _, out _) || created != createTime)
        {
            handle.Dispose();
            error = "process has exited (PID reused)";
            return null;
        }
        error = string.Empty;
        return handle;
    }

    public static bool IsAlive(int pid, long createTime)
    {
        using var h = Open(pid, createTime, 0, out _);
        return h is not null;
    }

    public static uint ToPriorityClass(PriorityLevel level) => level switch
    {
        PriorityLevel.BelowNormal => ProcessApi.BelowNormalPriorityClass,
        PriorityLevel.AboveNormal => ProcessApi.AboveNormalPriorityClass,
        _ => ProcessApi.NormalPriorityClass,
    };

    public static string PriorityName(uint cls) => cls switch
    {
        ProcessApi.IdlePriorityClass => "Idle",
        ProcessApi.BelowNormalPriorityClass => "Below normal",
        ProcessApi.NormalPriorityClass => "Normal",
        ProcessApi.AboveNormalPriorityClass => "Above normal",
        ProcessApi.HighPriorityClass => "High",
        ProcessApi.RealtimePriorityClass => "Realtime",
        _ => "?",
    };

    /// <summary>Base priority from the snapshot → display name.</summary>
    public static string PriorityNameFromBase(int basePriority) => basePriority switch
    {
        <= 4 => "Idle",
        <= 6 => "Below normal",
        <= 8 => "Normal",
        <= 10 => "Above normal",
        <= 13 => "High",
        _ => "Realtime",
    };
}

/// <summary>Base class: identity + name + the generic revert skeleton.</summary>
public abstract class ProcessChange : IReversibleChange
{
    protected ProcessChange(int pid, long createTime, string name)
    {
        Pid = pid;
        CreateTime = createTime;
        Name = name;
    }

    public int Pid { get; }
    public long CreateTime { get; }
    public string Name { get; }
    public int TargetPid => Pid;
    public abstract string Kind { get; }
    public abstract string Description { get; }

    public bool IsTargetAlive() => ProcessTarget.IsAlive(Pid, CreateTime);

    public RevertOutcome Revert()
    {
        using var h = ProcessTarget.Open(Pid, CreateTime, RevertAccess, out _);
        if (h is null)
        {
            return IsTargetAlive() ? RevertOutcome.Failed : RevertOutcome.TargetGone;
        }
        return RestoreOriginal(h) ? RevertOutcome.Reverted : RevertOutcome.Failed;
    }

    protected abstract uint RevertAccess { get; }
    protected abstract bool RestoreOriginal(SafeProcessHandle handle);
    public abstract ChangeRecord ToRecord();

    protected ChangeRecord Record(long a = 0, long b = 0, long c = 0, uint[]? ids = null) => new()
    {
        Kind = Kind, Description = Description, Pid = Pid, CreateTime = CreateTime,
        OriginalA = a, OriginalB = b, OriginalC = c, OriginalIds = ids,
    };

    /// <summary>Rebuilds a change from its persisted record (crash recovery).</summary>
    public static ProcessChange? FromRecord(ChangeRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.Kind switch
        {
            PriorityChange.KindName => new PriorityChange(r.Pid, r.CreateTime, r.Description, (uint)r.OriginalA, 0),
            EfficiencyModeChange.KindName => new EfficiencyModeChange(r.Pid, r.CreateTime, r.Description, (uint)r.OriginalA, (uint)r.OriginalB, (uint)r.OriginalC),
            CpuSetChange.KindName => new CpuSetChange(r.Pid, r.CreateTime, r.Description, r.OriginalIds ?? [], "restored"),
            MemoryPriorityChange.KindName => new MemoryPriorityChange(r.Pid, r.CreateTime, r.Description, (uint)r.OriginalA, 0),
            WorkingSetCapChange.KindName => new WorkingSetCapChange(r.Pid, r.CreateTime, r.Description, (nuint)r.OriginalA, (nuint)r.OriginalB, (uint)r.OriginalC, 0),
            _ => null,
        };
    }
}

public sealed class PriorityChange : ProcessChange
{
    public const string KindName = "priority";
    private readonly uint _original;
    private readonly uint _applied;

    internal PriorityChange(int pid, long create, string name, uint original, uint applied) : base(pid, create, name)
    {
        _original = original;
        _applied = applied;
    }

    public override string Kind => KindName;
    public override string Description => $"{Name} ({Pid}): priority {ProcessTarget.PriorityName(_original)} → {ProcessTarget.PriorityName(_applied)}";
    protected override uint RevertAccess => ProcessApi.SetInformation;
    protected override bool RestoreOriginal(SafeProcessHandle h) => ProcessApi.SetPriorityClass(h, _original);
    public override ChangeRecord ToRecord() => Record(_original);

    public static PriorityChange? TryApply(int pid, long create, string name, PriorityLevel level, out string error)
    {
        using var h = ProcessTarget.Open(pid, create, ProcessApi.SetInformation | ProcessApi.QueryInformation, out error);
        if (h is null) { return null; }
        var original = ProcessApi.GetPriorityClass(h);
        if (original == 0) { error = "cannot read priority"; return null; }
        if (original is ProcessApi.HighPriorityClass or ProcessApi.RealtimePriorityClass)
        {
            error = "process runs at High/Realtime priority; left untouched";
            return null;
        }
        var target = ProcessTarget.ToPriorityClass(level);
        if (!ProcessApi.SetPriorityClass(h, target)) { error = $"SetPriorityClass failed ({Marshal.GetLastPInvokeError()})"; return null; }
        return new PriorityChange(pid, create, name, original, target);
    }
}

/// <summary>
/// Windows 11 "Efficiency mode" (as in Task Manager): EcoQoS power throttling + lower
/// priority (Below normal here, never Idle). Captures both originals.
/// </summary>
public sealed class EfficiencyModeChange : ProcessChange
{
    public const string KindName = "efficiency";
    private readonly uint _originalPriority;
    private readonly uint _originalControl;
    private readonly uint _originalState;

    internal EfficiencyModeChange(int pid, long create, string name, uint originalPriority, uint originalControl, uint originalState) : base(pid, create, name)
    {
        _originalPriority = originalPriority;
        _originalControl = originalControl;
        _originalState = originalState;
    }

    public override string Kind => KindName;
    public override string Description => $"{Name} ({Pid}): efficiency mode (EcoQoS + below-normal priority)";
    protected override uint RevertAccess => ProcessApi.SetInformation;

    protected override unsafe bool RestoreOriginal(SafeProcessHandle h)
    {
        var state = new PowerThrottlingState { Version = 1, ControlMask = _originalControl, StateMask = _originalState };
        var ok1 = ProcessApi.SetProcessInformation(h, ProcessApi.ProcessPowerThrottlingClass, &state, (uint)sizeof(PowerThrottlingState));
        var ok2 = _originalPriority == 0 || ProcessApi.SetPriorityClass(h, _originalPriority);
        return ok1 && ok2;
    }

    public override ChangeRecord ToRecord() => Record(_originalPriority, _originalControl, _originalState);

    public static unsafe EfficiencyModeChange? TryApply(int pid, long create, string name, out string error)
    {
        using var h = ProcessTarget.Open(pid, create, ProcessApi.SetInformation | ProcessApi.QueryInformation, out error);
        if (h is null) { return null; }

        var originalPriority = ProcessApi.GetPriorityClass(h);
        if (originalPriority is ProcessApi.HighPriorityClass or ProcessApi.RealtimePriorityClass)
        {
            error = "process runs at High/Realtime priority; left untouched";
            return null;
        }
        var original = new PowerThrottlingState { Version = 1 };
        if (!ProcessApi.GetProcessInformation(h, ProcessApi.ProcessPowerThrottlingClass, &original, (uint)sizeof(PowerThrottlingState)))
        {
            original = new PowerThrottlingState { Version = 1 }; // unknown → restore to "let Windows decide"
        }

        var eco = new PowerThrottlingState { Version = 1, ControlMask = ProcessApi.PowerThrottlingExecutionSpeed, StateMask = ProcessApi.PowerThrottlingExecutionSpeed };
        if (!ProcessApi.SetProcessInformation(h, ProcessApi.ProcessPowerThrottlingClass, &eco, (uint)sizeof(PowerThrottlingState)))
        {
            error = $"EcoQoS not applied ({Marshal.GetLastPInvokeError()})";
            return null;
        }
        if (originalPriority != ProcessApi.IdlePriorityClass)
        {
            ProcessApi.SetPriorityClass(h, ProcessApi.BelowNormalPriorityClass);
        }
        return new EfficiencyModeChange(pid, create, name, originalPriority, original.ControlMask, original.StateMask);
    }
}

public sealed class CpuSetChange : ProcessChange
{
    public const string KindName = "cpu-cores";
    private readonly uint[] _original;
    private readonly string _label;

    internal CpuSetChange(int pid, long create, string name, uint[] original, string label) : base(pid, create, name)
    {
        _original = original;
        _label = label;
    }

    public override string Kind => KindName;
    public override string Description => $"{Name} ({Pid}): CPU cores → {_label}";
    protected override uint RevertAccess => ProcessApi.SetLimitedInformation;

    protected override unsafe bool RestoreOriginal(SafeProcessHandle h)
    {
        fixed (uint* ids = _original)
        {
            return ProcessApi.SetProcessDefaultCpuSets(h, _original.Length == 0 ? null : ids, (uint)_original.Length);
        }
    }

    public override ChangeRecord ToRecord() => Record(ids: _original);

    public static unsafe CpuSetChange? TryApply(int pid, long create, string name, uint[] cpuSets, string label, out string error)
    {
        if (cpuSets.Length < 2)
        {
            error = "not available on this CPU";
            return null;
        }
        using var h = ProcessTarget.Open(pid, create, ProcessApi.SetLimitedInformation, out error);
        if (h is null) { return null; }

        var original = new uint[256];
        uint count;
        fixed (uint* buf = original)
        {
            if (!ProcessApi.GetProcessDefaultCpuSets(h, buf, (uint)original.Length, out count))
            {
                error = "cannot read current CPU sets";
                return null;
            }
        }
        Array.Resize(ref original, (int)count);

        fixed (uint* ids = cpuSets)
        {
            if (!ProcessApi.SetProcessDefaultCpuSets(h, ids, (uint)cpuSets.Length))
            {
                error = $"SetProcessDefaultCpuSets failed ({Marshal.GetLastPInvokeError()})";
                return null;
            }
        }
        return new CpuSetChange(pid, create, name, original, label);
    }
}

public sealed class MemoryPriorityChange : ProcessChange
{
    public const string KindName = "memory-priority";
    private readonly uint _original;
    private readonly uint _applied;

    internal MemoryPriorityChange(int pid, long create, string name, uint original, uint applied) : base(pid, create, name)
    {
        _original = original;
        _applied = applied;
    }

    public override string Kind => KindName;
    public override string Description => $"{Name} ({Pid}): memory priority {_original} → {_applied} (1 = lowest, 5 = normal)";
    protected override uint RevertAccess => ProcessApi.SetInformation;

    protected override unsafe bool RestoreOriginal(SafeProcessHandle h)
    {
        var value = _original;
        return ProcessApi.SetProcessInformation(h, ProcessApi.ProcessMemoryPriorityClass, &value, sizeof(uint));
    }

    public override ChangeRecord ToRecord() => Record(_original);

    public static unsafe MemoryPriorityChange? TryApply(int pid, long create, string name, MemoryPriorityLevel level, out string error)
    {
        using var h = ProcessTarget.Open(pid, create, ProcessApi.SetInformation | ProcessApi.QueryInformation, out error);
        if (h is null) { return null; }
        uint original = 0;
        if (!ProcessApi.GetProcessInformation(h, ProcessApi.ProcessMemoryPriorityClass, &original, sizeof(uint)))
        {
            error = "cannot read memory priority";
            return null;
        }
        uint target = level == MemoryPriorityLevel.Low ? 2u : 5u; // MEMORY_PRIORITY_LOW / NORMAL
        if (!ProcessApi.SetProcessInformation(h, ProcessApi.ProcessMemoryPriorityClass, &target, sizeof(uint)))
        {
            error = $"memory priority not applied ({Marshal.GetLastPInvokeError()})";
            return null;
        }
        return new MemoryPriorityChange(pid, create, name, original, target);
    }
}

/// <summary>
/// SOFT working-set cap: the memory manager trims the process toward the cap under memory
/// pressure but never hard-limits it (a hard cap can make a process thrash or hang).
/// </summary>
public sealed class WorkingSetCapChange : ProcessChange
{
    public const string KindName = "memory-cap";
    private readonly nuint _originalMin;
    private readonly nuint _originalMax;
    private readonly uint _originalFlags;
    private readonly int _capMb;

    internal WorkingSetCapChange(int pid, long create, string name, nuint min, nuint max, uint flags, int capMb) : base(pid, create, name)
    {
        _originalMin = min;
        _originalMax = max;
        _originalFlags = flags;
        _capMb = capMb;
    }

    public override string Kind => KindName;
    public override string Description => _capMb > 0 ? $"{Name} ({Pid}): soft memory cap {_capMb} MB" : Name;
    protected override uint RevertAccess => ProcessApi.SetQuota | ProcessApi.QueryInformation;
    protected override bool RestoreOriginal(SafeProcessHandle h) => ProcessApi.SetProcessWorkingSetSizeEx(h, _originalMin, _originalMax, _originalFlags);
    public override ChangeRecord ToRecord() => Record((long)_originalMin, (long)_originalMax, _originalFlags);

    public static WorkingSetCapChange? TryApply(int pid, long create, string name, int capMb, out string error)
    {
        using var h = ProcessTarget.Open(pid, create, ProcessApi.SetQuota | ProcessApi.QueryInformation, out error);
        if (h is null) { return null; }
        if (!ProcessApi.GetProcessWorkingSetSizeEx(h, out var min, out var max, out var flags))
        {
            error = "cannot read working-set limits";
            return null;
        }
        var cap = (nuint)capMb * 1024 * 1024;
        var newMin = min < cap ? min : cap / 4;
        const uint Soft = ProcessApi.QuotaLimitsHardwsMinDisable | ProcessApi.QuotaLimitsHardwsMaxDisable;
        if (!ProcessApi.SetProcessWorkingSetSizeEx(h, newMin, cap, Soft))
        {
            error = $"working-set cap not applied ({Marshal.GetLastPInvokeError()})";
            return null;
        }
        return new WorkingSetCapChange(pid, create, name, min, max, flags, capMb);
    }
}

/// <summary>One-shot actions that have no persistent state to revert.</summary>
public static class ProcessOneShots
{
    /// <summary>Asks Windows to page out the process's working set. Harmless: pages fault back in on use.</summary>
    public static bool TrimWorkingSet(int pid, long create, out string error)
    {
        using var h = ProcessTarget.Open(pid, create, ProcessApi.SetQuota | ProcessApi.QueryInformation, out error);
        if (h is null) { return false; }
        if (!ProcessApi.EmptyWorkingSet(h))
        {
            error = $"trim failed ({Marshal.GetLastPInvokeError()})";
            return false;
        }
        return true;
    }

    /// <summary>IRREVERSIBLE. Only reachable with explicit confirmation from an elevated admin.</summary>
    public static bool Terminate(int pid, long create, out string error)
    {
        using var h = ProcessTarget.Open(pid, create, ProcessApi.Terminate, out error);
        if (h is null) { return false; }
        if (!ProcessApi.TerminateProcess(h, 1))
        {
            error = $"terminate failed ({Marshal.GetLastPInvokeError()})";
            return false;
        }
        return true;
    }

    public static int? GetForegroundProcessId()
    {
        var hwnd = ProcessApi.GetForegroundWindow();
        if (hwnd == 0) { return null; }
        ProcessApi.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }
}

/// <summary>Read-only inspection used by diagnostics and tests to verify that a change really took effect.</summary>
public static unsafe class ProcessInspector
{
    public static long? GetCreateTime(int pid)
    {
        using var h = ProcessApi.OpenProcess(ProcessApi.QueryLimitedInformation, false, pid);
        return !h.IsInvalid && ProcessApi.GetProcessTimes(h, out var created, out _, out _, out _) ? created : null;
    }

    public static string Priority(int pid)
    {
        using var h = ProcessApi.OpenProcess(ProcessApi.QueryLimitedInformation, false, pid);
        return h.IsInvalid ? "?" : ProcessTarget.PriorityName(ProcessApi.GetPriorityClass(h));
    }

    public static (uint Control, uint State)? PowerThrottling(int pid)
    {
        using var h = ProcessApi.OpenProcess(ProcessApi.QueryInformation, false, pid);
        var s = new PowerThrottlingState { Version = 1 };
        return !h.IsInvalid && ProcessApi.GetProcessInformation(h, ProcessApi.ProcessPowerThrottlingClass, &s, (uint)sizeof(PowerThrottlingState))
            ? (s.ControlMask, s.StateMask) : null;
    }

    public static uint? MemoryPriority(int pid)
    {
        using var h = ProcessApi.OpenProcess(ProcessApi.QueryInformation, false, pid);
        uint v = 0;
        return !h.IsInvalid && ProcessApi.GetProcessInformation(h, ProcessApi.ProcessMemoryPriorityClass, &v, sizeof(uint)) ? v : null;
    }

    public static int? CpuSetCount(int pid)
    {
        using var h = ProcessApi.OpenProcess(ProcessApi.QueryLimitedInformation, false, pid);
        if (h.IsInvalid) { return null; }
        var ids = stackalloc uint[256];
        return ProcessApi.GetProcessDefaultCpuSets(h, ids, 256, out var count) ? (int)count : null;
    }

    public static (ulong Min, ulong Max, uint Flags)? WorkingSetLimits(int pid)
    {
        using var h = ProcessApi.OpenProcess(ProcessApi.QueryInformation, false, pid);
        return !h.IsInvalid && ProcessApi.GetProcessWorkingSetSizeEx(h, out var min, out var max, out var flags) ? (min, max, flags) : null;
    }
}

/// <summary>Cheap per-process measurements used by experiments (F4).</summary>
public static class ProcessMeter
{
    public static long? CpuTime100ns(int pid, long createTime)
    {
        using var h = ProcessApi.OpenProcess(ProcessApi.QueryLimitedInformation, false, pid);
        // A non-zero exit time means the process has exited (a handle held elsewhere keeps the object alive).
        if (h.IsInvalid || !ProcessApi.GetProcessTimes(h, out var created, out var exited, out var kernel, out var user) || created != createTime || exited != 0)
        {
            return null;
        }
        return kernel + user;
    }

    public static long? WorkingSetBytes(int pid, long createTime)
    {
        const uint VmRead = 0x0010;
        using var h = ProcessApi.OpenProcess(ProcessApi.QueryLimitedInformation | VmRead, false, pid);
        if (h.IsInvalid || !ProcessApi.GetProcessTimes(h, out var created, out var exited, out _, out _) || created != createTime || exited != 0)
        {
            return null;
        }
        var counters = new ProcessMemoryCountersEx { Cb = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<ProcessMemoryCountersEx>() };
        return Kernel32.GetProcessMemoryInfo(h.DangerousGetHandle(), ref counters, counters.Cb) ? (long)counters.WorkingSetSize : null;
    }
}
