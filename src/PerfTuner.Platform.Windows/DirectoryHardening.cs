using System.Security.AccessControl;
using System.Security.Principal;

namespace PerfTuner.Platform.Windows;

/// <summary>
/// Applies a protected (non-inherited) DACL to product directories:
/// SYSTEM and Administrators full control, Users read/execute only.
/// <para>
/// Why: %ProgramData% lets ordinary users create files in new subfolders by default.
/// Without this, a standard user could plant a config.json (or pre-create the logs folder
/// as a junction) that the LocalSystem service would then trust.
/// </para>
/// </summary>
public static class DirectoryHardening
{
    public static void CreateHardened(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));

        var info = new DirectoryInfo(path);
        if (info.Exists)
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // A junction/symlink planted in place of our directory: refuse to follow it.
                throw new IOException($"'{path}' is a reparse point; refusing to use it.");
            }
            info.SetAccessControl(security);
        }
        else
        {
            info.Create(security);
        }
    }
}
