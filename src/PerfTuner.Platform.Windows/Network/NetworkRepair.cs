using System.Management;
using PerfTuner.Platform.Windows.Undocumented;

namespace PerfTuner.Platform.Windows.Network;

/// <summary>
/// Network repairs for the Troubleshoot page. DNS cache and IP lease use Windows' documented WMI
/// classes; Winsock and TCP/IP reset have no API, so they run Windows' own netsh with fixed arguments.
/// </summary>
public static class NetworkRepair
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Empties the DNS client cache (same as "ipconfig /flushdns").</summary>
    public static void FlushDns()
    {
        using var cls = new ManagementClass(@"root\StandardCimv2", "MSFT_DNSClientCache", null);
        var result = cls.InvokeMethod("Clear", null);
        if (result is uint code && code != 0) { throw new InvalidOperationException($"Windows reported error {code}."); }
    }

    /// <summary>Releases and renews the DHCP address of every adapter (same as "ipconfig /release" + "/renew"). Returns false if a restart is needed.</summary>
    public static bool RenewAddresses()
    {
        using var cls = new ManagementClass("Win32_NetworkAdapterConfiguration");
        var release = Code(cls.InvokeMethod("ReleaseDHCPLeaseAll", null));
        var renew = Code(cls.InvokeMethod("RenewDHCPLeaseAll", null));
        if (renew is not (0 or 1)) { throw new InvalidOperationException($"Renewing failed (Windows code {renew}); release code {release}."); }
        return renew == 0;
    }

    private static uint Code(object? result) => result is uint u ? u : 0;

    /// <summary>"netsh winsock reset": restores the Winsock catalog to its default. Needs a restart.</summary>
    public static string ResetWinsock() => Netsh(["winsock", "reset"]);

    /// <summary>"netsh int ip reset": rewrites TCP/IP's registry settings to their defaults. Needs a restart; static IP settings are lost.</summary>
    public static string ResetTcpIp() => Netsh(["int", "ip", "reset"]);

    private static string Netsh(string[] arguments)
    {
        var r = WindowsTools.Run(WindowsTools.Netsh, arguments, Timeout);
        if (r.ExitCode != 0) { throw new InvalidOperationException(r.Output.Split('\n').FirstOrDefault()?.Trim() ?? $"netsh failed ({r.ExitCode})."); }
        return r.Output;
    }
}
