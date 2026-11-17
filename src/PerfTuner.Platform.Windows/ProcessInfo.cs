using System.IO.Pipes;
using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows;

/// <summary>Small, allocation-conscious process queries.</summary>
public static class ProcessInfo
{
    /// <summary>
    /// Full image path of a process, or null if it cannot be opened (exited, protected,
    /// or access denied). Uses PROCESS_QUERY_LIMITED_INFORMATION — the least access
    /// right that works across integrity levels.
    /// </summary>
    public static unsafe string? TryGetImagePath(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }
        using var handle = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
        {
            return null;
        }
        const int Capacity = 1024;
        var buffer = stackalloc char[Capacity];
        var size = Capacity;
        return Kernel32.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    public static int? GetPipeClientProcessId(PipeStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return Kernel32.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? (int)pid : null;
    }

    public static int? GetPipeServerProcessId(PipeStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return Kernel32.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) ? (int)pid : null;
    }
}
