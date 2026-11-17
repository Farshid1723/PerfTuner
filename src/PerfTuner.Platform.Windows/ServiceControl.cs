using System.ComponentModel;
using System.Runtime.InteropServices;
using PerfTuner.Contracts;
using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows;

public enum ServiceRunState
{
    NotInstalled = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
    Unknown = 99,
}

public readonly record struct ServiceQuery(ServiceRunState State, int ProcessId);

/// <summary>Read-only SCM queries. Works for standard users (default service DACL allows query).</summary>
public static class ServiceControl
{
    public static ServiceQuery Query(string serviceName = ProtocolConstants.ServiceName)
    {
        var scm = Advapi32.OpenSCManager(null, null, Advapi32.ScManagerConnect);
        if (scm == 0)
        {
            return new ServiceQuery(ServiceRunState.Unknown, 0);
        }
        try
        {
            var svc = Advapi32.OpenService(scm, serviceName, Advapi32.ServiceQueryStatus);
            if (svc == 0)
            {
                return Marshal.GetLastPInvokeError() == Advapi32.ErrorServiceDoesNotExist
                    ? new ServiceQuery(ServiceRunState.NotInstalled, 0)
                    : new ServiceQuery(ServiceRunState.Unknown, 0);
            }
            try
            {
                unsafe
                {
                    ServiceStatusProcess status;
                    if (!Advapi32.QueryServiceStatusEx(svc, 0, &status, sizeof(ServiceStatusProcess), out _))
                    {
                        return new ServiceQuery(ServiceRunState.Unknown, 0);
                    }
                    var state = status.CurrentState is >= 1 and <= 7 ? (ServiceRunState)status.CurrentState : ServiceRunState.Unknown;
                    return new ServiceQuery(state, (int)status.ProcessId);
                }
            }
            finally
            {
                Advapi32.CloseServiceHandle(svc);
            }
        }
        finally
        {
            Advapi32.CloseServiceHandle(scm);
        }
    }

    /// <summary>
    /// Stops a leftover kernel ETW session (e.g. after a service crash). ETW sessions are
    /// system-wide and survive their creator, so the uninstaller cleans this up explicitly.
    /// Returns true if a session was stopped or none existed.
    /// </summary>
    public static unsafe bool StopOrphanedEtwSession(string sessionName)
    {
        // EVENT_TRACE_PROPERTIES (120 bytes on x64) followed by room for two names.
        const int PropertiesSize = 120;
        const int NameBytes = 1024 * sizeof(char);
        const int Total = PropertiesSize + (2 * NameBytes);
        var buffer = stackalloc byte[Total];
        new Span<byte>(buffer, Total).Clear();
        *(uint*)buffer = Total;                                   // Wnode.BufferSize
        *(uint*)(buffer + 112) = PropertiesSize + NameBytes;      // LogFileNameOffset
        *(uint*)(buffer + 116) = PropertiesSize;                  // LoggerNameOffset
        const uint EventTraceControlStop = 1;
        var rc = Advapi32.ControlTrace(0, sessionName, buffer, EventTraceControlStop);
        return rc is 0 or Advapi32.ErrorWmiInstanceNotFound;
    }

    internal static Win32Exception LastError(string what) => new(Marshal.GetLastPInvokeError(), what);
}
