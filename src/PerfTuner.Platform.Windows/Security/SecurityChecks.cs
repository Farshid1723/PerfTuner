using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using PerfTuner.Core.Security;
using PerfTuner.Platform.Windows.Settings;

namespace PerfTuner.Platform.Windows.Security;

/// <summary>A reviewed, reversible fix: only registry values built here (never from input). Undo restores each previous value.</summary>
public sealed record SecurityFix(string Id, string Title, string WhatChanges, string TakesEffect, string? Warning, IReadOnlyList<RegistryValueSpec> Values);

/// <summary>One check on the Security page.</summary>
public sealed record SecurityFinding(
    string Id,
    string Group,
    string Title,
    SecurityStatus Status,
    string Found,
    string Why,
    SecurityFix? Fix = null,
    string? OpenUri = null,
    string? OpenLabel = null);

/// <summary>
/// Security check-up (v0.9). Read-only checks through documented interfaces: Windows Security Center
/// (<c>WscGetSecurityProviderHealth</c>), Defender WMI, Device Guard WMI, BitLocker WMI, TBS (<c>Tbsi_GetDeviceInfo</c>),
/// <c>GetExtendedTcpTable</c>, the Windows Update Agent API, file ACLs and well-known registry values. Each check is
/// independent: one that can't run reports Unknown instead of failing the scan. The "attacker's-eye" group lists the
/// misconfigurations a penetration test looks for first.
/// </summary>
public static partial class SecurityChecks
{
    public const string GroupProtection = "Protection";
    public const string GroupDevice = "Device security";
    public const string GroupNetwork = "Network exposure";
    public const string GroupAttacker = "Attacker's-eye view";

    private const string PolicySystem = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public static IReadOnlyList<SecurityFinding> RunAll()
    {
        var checks = new (Func<IEnumerable<SecurityFinding>> Run, string Title, string Group)[]
        {
            (Antivirus, "Antivirus", GroupProtection), (Firewall, "Firewall", GroupProtection), (SmartScreen, "SmartScreen", GroupProtection),
            (Uac, "User Account Control", GroupProtection), (Updates, "Windows Update", GroupProtection),
            (SecureBoot, "Secure Boot", GroupDevice), (Tpm, "TPM security chip", GroupDevice), (BitLocker, "BitLocker drive encryption", GroupDevice),
            (MemoryIntegrity, "Memory integrity", GroupDevice), (LsaProtection, "LSA protection", GroupDevice),
            (Smb1, "SMB1 file sharing", GroupNetwork), (RemoteDesktop, "Remote Desktop", GroupNetwork), (Llmnr, "Name broadcasting (LLMNR)", GroupNetwork),
            (ListeningPrograms, "Programs listening on the network", GroupNetwork),
            (UnquotedServicePaths, "Unquoted service paths", GroupAttacker), (AlwaysInstallElevated, "Always install elevated", GroupAttacker),
            (WritablePathFolders, "Writable folders on the system PATH", GroupAttacker), (AutoLogon, "Automatic sign-in", GroupAttacker),
            (GuestAccount, "Guest account", GroupAttacker), (AutoRun, "AutoRun", GroupAttacker), (PowerShell2, "PowerShell 2.0", GroupAttacker),
        };
        var results = new List<SecurityFinding>();
        foreach (var (check, title, group) in checks)
        {
            try { results.AddRange(check()); }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or System.Security.SecurityException
                or IOException or InvalidOperationException or ArgumentException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                results.Add(new SecurityFinding(check.Method.Name, group, title, SecurityStatus.Unknown, "This check couldn't run: " + ex.Message,
                    ex is UnauthorizedAccessException or System.Security.SecurityException ? "Windows only shares this with administrators." : ""));
            }
        }
        return results;
    }

    // ------------------------------------------------------------------ protection

    private static IEnumerable<SecurityFinding> Antivirus()
    {
        var health = WscHealth(WscAntivirus);
        var status = health switch { 0 => SecurityStatus.Good, 1 => SecurityStatus.Unknown, null => SecurityStatus.Unknown, _ => SecurityStatus.Bad };
        var found = health switch
        {
            0 => "An antivirus is on and up to date.",
            2 => "Windows Security reports your antivirus is off or out of date.",
            3 => "Your antivirus is snoozed.",
            _ => "Windows Security Center doesn't report an antivirus.",
        };
        yield return new SecurityFinding("antivirus", GroupProtection, "Antivirus", status, found,
            "Real-time antivirus is the first line of defence against malware from downloads, e-mail and USB sticks.",
            OpenUri: "windowsdefender://threat", OpenLabel: "Open Virus & threat protection");

        foreach (var f in DefenderDetails()) { yield return f; }
    }

    /// <summary>Microsoft Defender details, when Defender is the active antivirus (namespace absent with other antivirus products).</summary>
    private static List<SecurityFinding> DefenderDetails()
    {
        try { return DefenderDetailsCore().ToList(); }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<SecurityFinding> DefenderDetailsCore()
    {
        using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender",
            "SELECT AMRunningMode, RealTimeProtectionEnabled, AntivirusSignatureAge, IsTamperProtected FROM MSFT_MpComputerStatus");
        foreach (ManagementObject o in s.Get())
        {
            using (o)
            {
                if (o["AMRunningMode"] is string mode && !mode.Equals("Normal", StringComparison.OrdinalIgnoreCase)) { yield break; }
                if (o["AntivirusSignatureAge"] is { } ageObj)
                {
                    var age = Convert.ToUInt32(ageObj, CultureInfo.InvariantCulture);
                    yield return new SecurityFinding("defender-signatures", GroupProtection, "Virus definitions", SecurityRules.SignatureAge(age),
                        age == 0 ? "Updated today." : $"Last updated {age} day(s) ago.",
                        "New malware appears every day; old definitions miss it.", OpenUri: "windowsdefender://threat", OpenLabel: "Check for updates");
                }
                if (o["RealTimeProtectionEnabled"] is bool rt && !rt)
                {
                    yield return new SecurityFinding("defender-realtime", GroupProtection, "Real-time protection", SecurityStatus.Bad,
                        "Microsoft Defender's real-time protection is off.", "Without it, files are only checked when you scan by hand.",
                        OpenUri: "windowsdefender://threatsettings", OpenLabel: "Turn it on in Windows Security");
                }
                if (o["IsTamperProtected"] is bool tp)
                {
                    yield return new SecurityFinding("tamper", GroupProtection, "Tamper protection", tp ? SecurityStatus.Good : SecurityStatus.Warn,
                        tp ? "On: malware can't switch Defender off behind your back." : "Off: malware with admin rights can switch Defender off.",
                        "Stops malicious programs from disabling your antivirus.", OpenUri: "windowsdefender://threatsettings", OpenLabel: "Open Windows Security");
                }
            }
        }
    }

    private static IEnumerable<SecurityFinding> Firewall()
    {
        var health = WscHealth(WscFirewall);
        yield return new SecurityFinding("firewall", GroupProtection, "Firewall", health switch { 0 => SecurityStatus.Good, 2 or 3 => SecurityStatus.Bad, _ => SecurityStatus.Unknown },
            health == 0 ? "The firewall is on." : health is 2 or 3 ? "The firewall is off for at least one network type." : "No firewall is reported.",
            "The firewall decides which programs other computers can reach. Off, every listening program is exposed.",
            OpenUri: "windowsdefender://network", OpenLabel: "Open Firewall & network protection");
    }

    private static IEnumerable<SecurityFinding> SmartScreen()
    {
        using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System");
        using var explorer = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer");
        var off = policy?.GetValue("EnableSmartScreen") is int p && p == 0
            || explorer?.GetValue("SmartScreenEnabled") is string e && e.Equals("Off", StringComparison.OrdinalIgnoreCase);
        yield return new SecurityFinding("smartscreen", GroupProtection, "SmartScreen for apps and files", off ? SecurityStatus.Bad : SecurityStatus.Good,
            off ? "SmartScreen is turned off." : "SmartScreen checks unknown apps and downloads.",
            "Warns before you run a program that's unknown or reported as malicious.", OpenUri: "windowsdefender://appbrowser", OpenLabel: "Open App & browser control");
    }

    private static IEnumerable<SecurityFinding> Uac()
    {
        using var k = Registry.LocalMachine.OpenSubKey(PolicySystem);
        var lua = k?.GetValue("EnableLUA") as int? ?? 1;
        var consent = k?.GetValue("ConsentPromptBehaviorAdmin") as int? ?? 5;
        var secure = k?.GetValue("PromptOnSecureDesktop") as int? ?? 1;
        if (lua == 0)
        {
            yield return new SecurityFinding("uac", GroupProtection, "User Account Control", SecurityStatus.Bad, "UAC is turned off: every program runs with full admin rights.",
                "UAC makes programs ask before they change the system, so malware can't silently take over.",
                new SecurityFix("uac-on", "Turn User Account Control back on", "Sets EnableLUA = 1 (Windows' default).", "After restart", null,
                    [new(true, PolicySystem, "EnableLUA", RegistryValueKind.DWord, 1)]));
            yield break;
        }
        if (consent == 0)
        {
            yield return new SecurityFinding("uac", GroupProtection, "User Account Control", SecurityStatus.Bad, "Admin programs are elevated without asking.",
                "Without the prompt, any program you start can silently get full control.",
                new SecurityFix("uac-prompt", "Ask before elevating", "Sets ConsentPromptBehaviorAdmin = 5 (Windows' default: ask for consent for non-Windows programs).", "Immediately", null,
                    [new(true, PolicySystem, "ConsentPromptBehaviorAdmin", RegistryValueKind.DWord, 5)]));
            yield break;
        }
        if (secure == 0)
        {
            yield return new SecurityFinding("uac", GroupProtection, "User Account Control", SecurityStatus.Warn, "UAC prompts appear on the normal desktop.",
                "On the dimmed secure desktop, other programs can't click \"Yes\" for you.",
                new SecurityFix("uac-secure", "Show UAC prompts on the secure desktop", "Sets PromptOnSecureDesktop = 1 (Windows' default).", "Immediately", null,
                    [new(true, PolicySystem, "PromptOnSecureDesktop", RegistryValueKind.DWord, 1)]));
            yield break;
        }
        yield return new SecurityFinding("uac", GroupProtection, "User Account Control", SecurityStatus.Good, "On, with Windows' recommended prompt.",
            "Programs must ask before they change the system.");
    }

    private static IEnumerable<SecurityFinding> Updates()
    {
        DateTime? last = null;
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
        if (type is not null)
        {
            dynamic session = Activator.CreateInstance(type)!;
            try
            {
                dynamic searcher = session.CreateUpdateSearcher();
                int count = searcher.GetTotalHistoryCount();
                if (count > 0)
                {
                    dynamic history = searcher.QueryHistory(0, Math.Min(count, 100));
                    int n = history.Count;
                    for (var i = 0; i < n; i++)
                    {
                        dynamic entry = history.Item(i);
                        // Operation 1 = installation, ResultCode 2 = succeeded.
                        if ((int)entry.Operation == 1 && (int)entry.ResultCode == 2)
                        {
                            DateTime d = entry.Date;
                            if (last is null || d > last) { last = d; }
                        }
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(session);
            }
        }
        var auto = WscHealth(WscAutoUpdate);
        var status = last is { } l ? SecurityRules.UpdateAge(DateTime.UtcNow - l.ToUniversalTime()) : SecurityStatus.Unknown;
        if (auto is 2 or 3) { status = SecurityStatus.Bad; }
        using var reboot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        yield return new SecurityFinding("updates", GroupProtection, "Windows updates", status,
            (last is { } d2 ? $"Last update installed {(int)(DateTime.UtcNow - d2.ToUniversalTime()).TotalDays} day(s) ago." : "No installed update found in the history.")
                + (auto is 2 or 3 ? " Automatic updates are turned off." : "")
                + (reboot is not null ? " A restart is waiting to finish installing updates." : ""),
            "Most attacks use holes that a monthly update already closed.", OpenUri: "ms-settings:windowsupdate", OpenLabel: "Open Windows Update");
    }

    // ------------------------------------------------------------------ device

    private static IEnumerable<SecurityFinding> SecureBoot()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        var on = k?.GetValue("UEFISecureBootEnabled") is int v && v == 1;
        yield return new SecurityFinding("secureboot", GroupDevice, "Secure Boot", k is null ? SecurityStatus.Unknown : on ? SecurityStatus.Good : SecurityStatus.Warn,
            k is null ? "This PC doesn't report Secure Boot (legacy BIOS mode?)." : on ? "On." : "Off.",
            "Only trusted, signed boot software can start, which blocks bootkits that hide below Windows. It's switched in the PC's firmware (UEFI) settings.",
            OpenUri: "windowsdefender://devicesecurity", OpenLabel: "Open Device security");
    }

    private static IEnumerable<SecurityFinding> Tpm()
    {
        var info = new TpmDeviceInfo { StructVersion = 1 };
        var rc = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), ref info);
        yield return new SecurityFinding("tpm", GroupDevice, "Security chip (TPM)", rc == 0 ? (info.TpmVersion == 2 ? SecurityStatus.Good : SecurityStatus.Warn) : SecurityStatus.Bad,
            rc == 0 ? $"TPM {(info.TpmVersion == 2 ? "2.0" : "1.2")} is present." : "No TPM was found (or it is turned off in the firmware).",
            "Keeps encryption keys and sign-in secrets in hardware, so they can't simply be copied off the disk.",
            OpenUri: "windowsdefender://devicesecurity", OpenLabel: "Open Device security");
    }

    private static IEnumerable<SecurityFinding> BitLocker()
    {
        var sys = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        int? protection = null;
        using (var s = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftVolumeEncryption", "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume"))
        {
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    if (string.Equals(o["DriveLetter"] as string, sys, StringComparison.OrdinalIgnoreCase)) { protection = Convert.ToInt32(o["ProtectionStatus"], CultureInfo.InvariantCulture); }
                }
            }
        }
        yield return new SecurityFinding("bitlocker", GroupDevice, "Drive encryption (BitLocker)", protection switch { 1 => SecurityStatus.Good, 0 => SecurityStatus.Warn, _ => SecurityStatus.Unknown },
            protection switch { 1 => $"Drive {sys} is encrypted and protected.", 0 => $"Drive {sys} is not protected by encryption.", _ => "Couldn't read the encryption state." },
            "If the PC or its drive is stolen, encryption keeps your files unreadable. Especially important for laptops.",
            OpenUri: "ms-settings:deviceencryption", OpenLabel: "Open Device encryption");
    }

    private static IEnumerable<SecurityFinding> MemoryIntegrity()
    {
        bool? running = null;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            foreach (ManagementObject o in s.Get())
            {
                using (o) { running = o["SecurityServicesRunning"] is uint[] r && r.Contains(2u); }
            }
        }
        catch (ManagementException)
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            running = k?.GetValue("Enabled") is int v && v == 1;
        }
        yield return new SecurityFinding("hvci", GroupDevice, "Memory integrity (Core isolation)", running switch { true => SecurityStatus.Good, false => SecurityStatus.Warn, _ => SecurityStatus.Unknown },
            running == true ? "On." : "Off.",
            "Uses virtualization to stop malicious or vulnerable drivers from taking over the Windows kernel. Some old drivers are incompatible, so Windows Security checks before switching it on.",
            OpenUri: "windowsdefender://coreisolation", OpenLabel: "Open Core isolation");
    }

    private static IEnumerable<SecurityFinding> LsaProtection()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Lsa");
        var v = k?.GetValue("RunAsPPL") as int? ?? 0;
        yield return new SecurityFinding("lsa", GroupDevice, "Credential protection (LSA protection)", v is 1 or 2 ? SecurityStatus.Good : SecurityStatus.Warn,
            v is 1 or 2 ? "On: password hashes in memory are protected." : "Off.",
            "Stops tools like credential dumpers from reading passwords and hashes out of memory.",
            v is 1 or 2 || Environment.OSVersion.Version.Build < 22621 ? null
                // 2 = on without the UEFI lock (Windows 11 22H2+), so undo really turns it off again; 1 would lock it in firmware.
                : new SecurityFix("lsa-on", "Turn on LSA protection", "Sets RunAsPPL = 2 under HKLM\\SYSTEM\\CurrentControlSet\\Control\\Lsa (Microsoft's documented switch: on, without a UEFI lock, so it can be undone).", "After restart",
                "Rarely, an old smart-card, VPN or password-manager plug-in that hooks into sign-in stops working. If that happens, undo it in History and restart.",
                [new(true, @"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL", RegistryValueKind.DWord, 2)]),
            OpenUri: "windowsdefender://coreisolation", OpenLabel: "Open Core isolation");
    }

    // ------------------------------------------------------------------ network exposure

    private static IEnumerable<SecurityFinding> Smb1()
    {
        var installed = OptionalFeatureState("SMB1Protocol");
        using var server = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters");
        var serverOff = server?.GetValue("SMB1") is int s && s == 0;
        var status = installed == true && !serverOff ? SecurityStatus.Bad : installed == true ? SecurityStatus.Warn : installed == false ? SecurityStatus.Good : SecurityStatus.Unknown;
        yield return new SecurityFinding("smb1", GroupNetwork, "Old file sharing protocol (SMBv1)", status,
            installed == true ? (serverOff ? "SMBv1 is installed, but this PC no longer serves it." : "SMBv1 is installed and active.") : installed == false ? "Not installed." : "Couldn't check.",
            "The 30-year-old SMBv1 protocol was used by WannaCry and NotPetya to spread between PCs. Nothing modern needs it.",
            installed == true && !serverOff
                ? new SecurityFix("smb1-server-off", "Stop serving SMBv1", "Sets SMB1 = 0 for the file-sharing server (Microsoft's documented switch). To remove SMBv1 completely, uninstall \"SMB 1.0/CIFS File Sharing Support\" in Optional features.",
                    "After restart", "Very old devices (some NAS boxes or printers from before 2010) may no longer reach shared folders on this PC.",
                    [new(true, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1", RegistryValueKind.DWord, 0)])
                : null,
            OpenUri: "ms-settings:optionalfeatures", OpenLabel: "Open Optional features");
    }

    private static IEnumerable<SecurityFinding> RemoteDesktop()
    {
        using var ts = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
        var enabled = ts?.GetValue("fDenyTSConnections") is int d && d == 0;
        using var tcp = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
        var nla = tcp?.GetValue("UserAuthentication") is not int n || n == 1;
        if (!enabled)
        {
            yield return new SecurityFinding("rdp", GroupNetwork, "Remote Desktop", SecurityStatus.Good, "Off.", "Nobody can sign in to this PC over the network.");
            yield break;
        }
        yield return new SecurityFinding("rdp", GroupNetwork, "Remote Desktop", nla ? SecurityStatus.Warn : SecurityStatus.Bad,
            nla ? "On, with Network Level Authentication." : "On, WITHOUT Network Level Authentication.",
            "Remote Desktop is a favourite target of password-guessing attacks. If you don't use it, turn it off; if you do, keep NLA on and use a strong password.",
            nla && GetSystemMetrics(SmRemoteSession) != 0 ? null // never cut off the session you're using
            : nla
                ? new SecurityFix("rdp-off", "Turn off Remote Desktop", "Sets fDenyTSConnections = 1: nobody can connect to this PC with Remote Desktop.", "Immediately",
                    "You won't be able to connect to this PC remotely until you turn it back on (undo in History, or Settings › System › Remote Desktop).",
                    [new(true, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", RegistryValueKind.DWord, 1)])
                : new SecurityFix("rdp-nla", "Require Network Level Authentication", "Sets UserAuthentication = 1: people must sign in before a remote session is even created.", "New connections",
                    "Very old Remote Desktop clients (before Windows 7) can't connect any more.",
                    [new(true, @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication", RegistryValueKind.DWord, 1)]),
            OpenUri: "ms-settings:remotedesktop", OpenLabel: "Open Remote Desktop settings");
    }

    private static IEnumerable<SecurityFinding> Llmnr()
    {
        const string key = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";
        using var k = Registry.LocalMachine.OpenSubKey(key);
        var off = k?.GetValue("EnableMulticast") is int v && v == 0;
        yield return new SecurityFinding("llmnr", GroupNetwork, "Name guessing on the local network (LLMNR)", off ? SecurityStatus.Good : SecurityStatus.Warn,
            off ? "Off." : "On (Windows' default).",
            "When a name can't be found, Windows asks every device on the network. An attacker on the same Wi-Fi can answer and capture your sign-in hash. Normal DNS keeps working when it's off.",
            off ? null : new SecurityFix("llmnr-off", "Turn off LLMNR", "Sets the documented policy EnableMulticast = 0 for the DNS client.", "Immediately",
                "On a home network without a router-provided DNS name for other PCs, reaching them by name may need \"name.local\" or their IP address.",
                [new(true, key, "EnableMulticast", RegistryValueKind.DWord, 0)]));
    }

    private static IEnumerable<SecurityFinding> ListeningPrograms()
    {
        var listeners = ListeningPorts().Where(l => !l.Loopback).ToList();
        var byProgram = listeners.GroupBy(l => l.Process).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(x => x.Port).Distinct().OrderBy(p => p).Take(6))})").ToList();
        yield return new SecurityFinding("listening", GroupNetwork, "Programs reachable from the network", SecurityStatus.Info,
            listeners.Count == 0 ? "No program accepts connections from other computers." :
                $"{byProgram.Count} program(s) accept connections from other computers: " + string.Join("; ", byProgram.Take(12)) + (byProgram.Count > 12 ? "; …" : "") + ".",
            "Each listening program is a door. The firewall decides who may knock; programs you don't recognise or no longer use are worth removing or blocking.",
            OpenUri: "windowsdefender://network", OpenLabel: "Open Firewall settings");
    }

    // ------------------------------------------------------------------ attacker's-eye view

    private static IEnumerable<SecurityFinding> UnquotedServicePaths()
    {
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        var hits = new List<(string Name, string Path)>();
        foreach (var name in services?.GetSubKeyNames() ?? [])
        {
            using var s = services!.OpenSubKey(name);
            if (s?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string path
                && s.GetValue("Type") is int type && (type & 0x30) != 0 // own or shared process (not drivers)
                && SecurityRules.IsUnquotedServicePath(path))
            {
                hits.Add((name, path));
            }
        }
        if (hits.Count == 0)
        {
            yield return new SecurityFinding("unquoted", GroupAttacker, "Unquoted service paths", SecurityStatus.Good, "None found.",
                "A service path with spaces and no quotes lets anyone who can write \"C:\\Program.exe\" run code as SYSTEM.");
            yield break;
        }
        foreach (var (name, path) in hits)
        {
            yield return new SecurityFinding("unquoted:" + name, GroupAttacker, $"Unquoted service path: {name}", SecurityStatus.Bad, path,
                "Windows tries every space-separated part of this path as a program, so a file planted at one of those places would run as the service (usually SYSTEM).",
                new SecurityFix("unquoted:" + name, $"Put quotes around the path of \"{name}\"",
                    $"ImagePath becomes: {SecurityRules.QuoteServicePath(path)}", "Next start of the service", null,
                    [new(true, $@"SYSTEM\CurrentControlSet\Services\{name}", "ImagePath", RegistryValueKind.ExpandString, SecurityRules.QuoteServicePath(path))]));
        }
    }

    private static IEnumerable<SecurityFinding> AlwaysInstallElevated()
    {
        const string key = @"SOFTWARE\Policies\Microsoft\Windows\Installer";
        using var m = Registry.LocalMachine.OpenSubKey(key);
        using var u = Registry.CurrentUser.OpenSubKey(key);
        var on = m?.GetValue("AlwaysInstallElevated") is int a && a == 1 && u?.GetValue("AlwaysInstallElevated") is int b && b == 1;
        yield return new SecurityFinding("aie", GroupAttacker, "\"Always install elevated\"", on ? SecurityStatus.Bad : SecurityStatus.Good,
            on ? "On: any installer package runs with full system rights." : "Off.",
            "With this policy, any user (or malware) can get SYSTEM rights by running a crafted .msi file.",
            on ? new SecurityFix("aie-off", "Turn off \"Always install elevated\"", "Sets AlwaysInstallElevated = 0 for the machine and for you.", "Immediately", null,
                [new(true, key, "AlwaysInstallElevated", RegistryValueKind.DWord, 0), new(false, key, "AlwaysInstallElevated", RegistryValueKind.DWord, 0)]) : null);
    }

    private static IEnumerable<SecurityFinding> WritablePathFolders()
    {
        var path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
        var writable = new List<string>();
        foreach (var raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = Environment.ExpandEnvironmentVariables(raw);
            if (!Directory.Exists(dir)) { continue; }
            try
            {
                var rules = new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule r in rules)
                {
                    if (r.AccessControlType != AccessControlType.Allow || r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) { continue; }
                    if (r.IdentityReference is not SecurityIdentifier sid || !IsEveryday(sid)) { continue; }
                    if ((r.FileSystemRights & (FileSystemRights.CreateFiles | FileSystemRights.WriteData | FileSystemRights.AppendData)) != 0)
                    {
                        writable.Add(dir);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                // Skip folders we can't inspect.
            }
        }
        yield return new SecurityFinding("path-writable", GroupAttacker, "Writable folders in the system PATH", writable.Count == 0 ? SecurityStatus.Good : SecurityStatus.Bad,
            writable.Count == 0 ? "None: only administrators can add programs to the system search path." : "Everyday accounts can write to: " + string.Join("; ", writable) + ".",
            "Programs started by Windows and services search these folders. If ordinary users can drop files there, a planted DLL or program can run with higher rights. Fix by removing the folder from the system PATH or making it admin-only (the program that created it may need reinstalling to a protected place such as Program Files).");
    }

    private static bool IsEveryday(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid)
        || sid.IsWellKnown(WellKnownSidType.WorldSid) || sid.IsWellKnown(WellKnownSidType.InteractiveSid);

    private static IEnumerable<SecurityFinding> AutoLogon()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        using var k = Registry.LocalMachine.OpenSubKey(key);
        var auto = k?.GetValue("AutoAdminLogon") is string a && a == "1";
        // Only the presence of a stored password is checked; its value is never read.
        var storedPassword = k?.GetValueNames().Contains("DefaultPassword", StringComparer.OrdinalIgnoreCase) == true;
        var status = auto && storedPassword ? SecurityStatus.Bad : auto ? SecurityStatus.Warn : SecurityStatus.Good;
        yield return new SecurityFinding("autologon", GroupAttacker, "Automatic sign-in", status,
            !auto ? "Off: Windows asks for your sign-in." : storedPassword ? "On, with the password stored readably in the registry." : "On.",
            "Anyone who powers on the PC is signed in as you; a stored password can be read by any program with admin rights.",
            auto ? new SecurityFix("autologon-off", "Turn off automatic sign-in", "Sets AutoAdminLogon = 0. The stored password stays until you clear it in \"netplwiz\" (PerfTuner never reads or stores it).", "Next start", null,
                [new(true, key, "AutoAdminLogon", RegistryValueKind.String, "0")]) : null);
    }

    private static IEnumerable<SecurityFinding> GuestAccount()
    {
        bool? enabled = null;
        using (var s = new ManagementObjectSearcher("SELECT SID, Disabled FROM Win32_UserAccount WHERE LocalAccount = TRUE"))
        {
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    if (o["SID"] is string sid && sid.EndsWith("-501", StringComparison.Ordinal)) { enabled = o["Disabled"] is bool d && !d; }
                }
            }
        }
        yield return new SecurityFinding("guest", GroupAttacker, "Guest account", enabled switch { true => SecurityStatus.Bad, false => SecurityStatus.Good, _ => SecurityStatus.Unknown },
            enabled == true ? "The built-in Guest account is enabled." : enabled == false ? "Disabled." : "Couldn't check.",
            "The Guest account has no password; enabled, anyone at the keyboard or on the network may get in.",
            OpenUri: "ms-settings:otherusers", OpenLabel: "Open Other users");
    }

    private static IEnumerable<SecurityFinding> AutoRun()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        using var k = Registry.LocalMachine.OpenSubKey(key);
        var off = k?.GetValue("NoDriveTypeAutoRun") is int v && (v & 0xFF) == 0xFF;
        yield return new SecurityFinding("autorun", GroupAttacker, "AutoRun from USB sticks and discs", off ? SecurityStatus.Good : SecurityStatus.Warn,
            off ? "Off for all drive types." : "Not switched off by policy.",
            "AutoRun used to start programs from inserted media automatically, a classic way for USB malware to spread.",
            off ? null : new SecurityFix("autorun-off", "Turn off AutoRun for all drives", "Sets the documented policy NoDriveTypeAutoRun = 255 (all drive types).", "Immediately",
                "Discs and USB sticks won't open anything automatically; open them in Explorer instead.",
                [new(true, key, "NoDriveTypeAutoRun", RegistryValueKind.DWord, 0xFF)]));
    }

    private static IEnumerable<SecurityFinding> PowerShell2()
    {
        var state = OptionalFeatureState("MicrosoftWindowsPowerShellV2Root");
        yield return new SecurityFinding("ps2", GroupAttacker, "PowerShell 2.0 engine", state == true ? SecurityStatus.Warn : SecurityStatus.Good,
            state == true ? "The old PowerShell 2.0 engine is installed." : "Not installed.",
            "Attackers start PowerShell 2.0 to bypass the logging and anti-malware scanning built into newer versions.",
            OpenUri: "ms-settings:optionalfeatures", OpenLabel: "Open Optional features");
    }

    // ------------------------------------------------------------------ fixes

    /// <summary>Applies a fix from a finding (values built in this class only) and journals every previous value.</summary>
    public static void Apply(SecurityFix fix, SettingsJournal journal)
    {
        ArgumentNullException.ThrowIfNull(fix);
        ArgumentNullException.ThrowIfNull(journal);
        foreach (var v in fix.Values)
        {
            if (!v.Machine && !v.KeyPath.StartsWith(@"SOFTWARE\Policies\", StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("unexpected value"); }
            journal.Record(RegistryWriter.Write(v, "Security: " + fix.Title));
        }
    }

    // ------------------------------------------------------------------ undo allow-list

    /// <summary>Every fixed value a security fix can write (machine?, key, value name).</summary>
    private static readonly (bool Machine, string Key, string Name)[] FixedFixValues =
    [
        (true, PolicySystem, "EnableLUA"), (true, PolicySystem, "ConsentPromptBehaviorAdmin"), (true, PolicySystem, "PromptOnSecureDesktop"),
        (true, @"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL"),
        (true, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1"),
        (true, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections"),
        (true, @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication"),
        (true, @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast"),
        (true, @"SOFTWARE\Policies\Microsoft\Windows\Installer", "AlwaysInstallElevated"),
        (false, @"SOFTWARE\Policies\Microsoft\Windows\Installer", "AlwaysInstallElevated"),
        (true, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "AutoAdminLogon"),
        (true, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun"),
    ];

    /// <summary>
    /// Whether an undo record may be restored. The journal lives in the user's profile, so it's not trusted: a record
    /// can only put back a value that a security fix (or a Windows setting in the catalog) actually writes. A service
    /// path can only go back to the unquoted original of the quoted value PerfTuner wrote, never to anything else.
    /// </summary>
    public static bool MayRestore(bool machine, string path, string name, string? original)
    {
        if (FixedFixValues.Any(v => v.Machine == machine && v.Key.Equals(path, StringComparison.OrdinalIgnoreCase) && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { return true; }
        if (machine && name.Equals("ImagePath", StringComparison.OrdinalIgnoreCase) && original is not null
            && System.Text.RegularExpressions.Regex.IsMatch(path, @"^SYSTEM\\CurrentControlSet\\Services\\[^\\]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && SecurityRules.IsUnquotedServicePath(original))
        {
            using var k = Registry.LocalMachine.OpenSubKey(path);
            return k?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string current
                && current == SecurityRules.QuoteServicePath(original);
        }
        return false;
    }

    // ------------------------------------------------------------------ helpers

    private static bool? OptionalFeatureState(string name)
    {
        using var s = new ManagementObjectSearcher($"SELECT InstallState FROM Win32_OptionalFeature WHERE Name = '{name}'");
        foreach (ManagementObject o in s.Get())
        {
            using (o) { return Convert.ToUInt32(o["InstallState"], CultureInfo.InvariantCulture) == 1; }
        }
        return false;
    }

    public sealed record Listener(int Port, string Process, bool Loopback);

    /// <summary>TCP ports in the LISTEN state with their owning program (GetExtendedTcpTable, IPv4 and IPv6).</summary>
    public static IReadOnlyList<Listener> ListeningPorts()
    {
        var result = new List<Listener>();
        var names = new Dictionary<int, string>();
        string Name(int pid)
        {
            if (names.TryGetValue(pid, out var n)) { return n; }
            try { n = pid == 4 ? "Windows (System)" : System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
            catch (ArgumentException) { n = $"process {pid}"; }
            names[pid] = n;
            return n;
        }
        foreach (var af in new[] { 2, 23 })
        {
            var size = 0;
            _ = GetExtendedTcpTable(0, ref size, false, af, TcpTableOwnerPidListener, 0);
            if (size <= 0) { continue; }
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, af, TcpTableOwnerPidListener, 0) != 0) { continue; }
                var count = Marshal.ReadInt32(buf);
                var rowSize = af == 2 ? 24 : 56;
                for (var i = 0; i < count; i++)
                {
                    var row = buf + 4 + i * rowSize;
                    int port, pid;
                    bool loopback;
                    if (af == 2)
                    {
                        var addr = (uint)Marshal.ReadInt32(row, 4);
                        port = SwapPort(Marshal.ReadInt32(row, 8));
                        pid = Marshal.ReadInt32(row, 20);
                        loopback = (addr & 0xFF) == 127;
                    }
                    else
                    {
                        var bytes = new byte[16];
                        Marshal.Copy(row, bytes, 0, 16);
                        port = SwapPort(Marshal.ReadInt32(row, 20));
                        pid = Marshal.ReadInt32(row, 52);
                        loopback = bytes.Take(15).All(b => b == 0) && bytes[15] == 1;
                    }
                    result.Add(new Listener(port, Name(pid), loopback));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return result;
    }

    private static int SwapPort(int p) => ((p & 0xFF) << 8) | ((p >> 8) & 0xFF);

    private const int TcpTableOwnerPidListener = 3;
    private const int WscFirewall = 0x1;
    private const int WscAutoUpdate = 0x2;
    private const int WscAntivirus = 0x4;

    private static int? WscHealth(int provider) => WscGetSecurityProviderHealth(provider, out var h) == 0 ? h : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    private const int SmRemoteSession = 0x1000;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("wscapi.dll")]
    private static partial int WscGetSecurityProviderHealth(int providers, out int health);

    [LibraryImport("tbs.dll")]
    private static partial uint Tbsi_GetDeviceInfo(uint size, ref TpmDeviceInfo info);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
}
