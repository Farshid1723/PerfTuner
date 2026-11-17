using Microsoft.Win32;
using PerfTuner.Core.Apps;

namespace PerfTuner.Platform.Windows.Apps;

/// <summary>
/// What's installed, read the way Windows' own "Installed apps" list does: the uninstall entries (machine 64/32-bit and
/// this account), this account's Store/MSIX packages, and Start-menu/desktop shortcuts (for apps that only unpack into
/// a folder). Read-only.
/// </summary>
public static class InstalledApps
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledEntry> Scan()
    {
        var list = new List<InstalledEntry>();
        ReadUninstall(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), list);
        ReadUninstall(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), list);
        ReadUninstall(RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default), list);
        ReadPackages(list);
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            ReadShortcuts(Environment.GetFolderPath(folder), list);
        }
        return list;
    }

    private static void ReadUninstall(RegistryKey hive, List<InstalledEntry> list)
    {
        using (hive)
        using (var root = hive.OpenSubKey(UninstallPath))
        {
            if (root is null) { return; }
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") is not string display || display.Trim().Length == 0) { continue; }
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) { continue; }
                    list.Add(new InstalledEntry(InstallSource.InstalledPrograms, display.Trim(), k.GetValue("DisplayVersion") as string, k.GetValue("DisplayIcon") as string));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // unreadable entry: skip
                }
            }
        }
    }

    /// <summary>This account's packaged apps, from the package repository keys ("Name_Version_Arch_..._PublisherId").</summary>
    private static void ReadPackages(List<InstalledEntry> list)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (root is null) { return; }
            foreach (var full in root.GetSubKeyNames())
            {
                var parts = full.Split('_');
                if (parts.Length >= 2) { list.Add(new InstalledEntry(InstallSource.StorePackage, parts[0], parts[1], null)); }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // not readable: no packaged apps listed
        }
    }

    private static void ReadShortcuts(string folder, List<InstalledEntry> list)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { return; }
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var lnk in Directory.EnumerateFiles(folder, "*.lnk", options).Take(3000))
            {
                list.Add(new InstalledEntry(InstallSource.Shortcut, Path.GetFileNameWithoutExtension(lnk), null, lnk));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // skip this folder
        }
    }
}
