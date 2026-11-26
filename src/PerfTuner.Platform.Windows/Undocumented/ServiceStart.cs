using System.ServiceProcess;
using Microsoft.Win32;
using PerfTuner.Platform.Windows.Native;

namespace PerfTuner.Platform.Windows.Undocumented;

/// <summary>Start type of a Windows service through the Service Control Manager (2 automatic, 3 manual, 4 disabled).</summary>
public static class ServiceStart
{
    public sealed record State(int StartType, bool Delayed, bool Running);

    public static State? Get(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            var start = sc.StartType switch
            {
                ServiceStartMode.Automatic => 2,
                ServiceStartMode.Manual => 3,
                ServiceStartMode.Disabled => 4,
                _ => 0,
            };
            if (start == 0) { return null; }
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            var delayed = key?.GetValue("DelayedAutostart") is int d && d != 0;
            return new State(start, delayed, sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending);
        }
        catch (InvalidOperationException)
        {
            return null; // not installed on this PC
        }
    }

    public static unsafe void Set(string name, int startType, bool delayed)
    {
        if (startType is < 2 or > 4) { throw new ArgumentOutOfRangeException(nameof(startType)); }
        var scm = Advapi32.OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
        if (scm == 0) { throw ServiceControl.LastError("OpenSCManager"); }
        try
        {
            var svc = Advapi32.OpenService(scm, name, Advapi32.ServiceChangeConfig | 0x0001 /* SERVICE_QUERY_CONFIG */);
            if (svc == 0) { throw ServiceControl.LastError("OpenService"); }
            try
            {
                const uint NoChange = 0xFFFFFFFF;
                if (!Advapi32.ChangeServiceStartType(svc, NoChange, (uint)startType, NoChange, null, null, 0, null, null, null, null))
                {
                    throw ServiceControl.LastError("ChangeServiceConfig");
                }
                if (startType == 2)
                {
                    var flag = delayed ? 1 : 0;
                    Advapi32.ChangeServiceConfig2(svc, Advapi32.ConfigDelayedAutoStart, &flag);
                }
            }
            finally { Advapi32.CloseServiceHandle(svc); }
        }
        finally { Advapi32.CloseServiceHandle(scm); }
    }

    public static void Stop(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) { return; }
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            // it stops at the next restart at the latest (it's disabled now)
        }
    }

    public static void Start(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) { return; }
            sc.Start();
        }
        catch (InvalidOperationException)
        {
            // starts with Windows at the latest
        }
    }
}
