using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using PerfTuner.Contracts;
using PerfTuner.Platform.Windows;

namespace PerfTuner.Service.Ipc;

/// <summary>
/// Who is on the other end of a pipe connection, established from the kernel-authenticated
/// pipe handle — never from anything the client says about itself.
/// <para>
/// The client connects with <c>TokenImpersonationLevel.Identification</c>: the service can
/// read the caller's token (user SID, groups, elevation) but cannot act as the caller.
/// </para>
/// </summary>
internal sealed record CallerIdentity(SecurityIdentifier User, bool IsElevatedAdmin, int ProcessId, string ImageName)
{
    public ClientRole Role => IsElevatedAdmin ? ClientRole.Admin : ClientRole.Viewer;

    /// <summary>Dev-mode owner of the console instance (same user as the service process).</summary>
    public bool DevOwner { get; init; }

    private readonly record struct TokenFacts(
        SecurityIdentifier? User, bool IsAdmin, bool IsNetwork, bool IsAnonymousOrGuest, bool IsInteractive, bool IsSystem);

    private static readonly bool EngineElevated = PerfTuner.Platform.Windows.PipeNames.CurrentProcessElevated();

    public static bool TryResolve(NamedPipeServerStream pipe, bool devMode, out CallerIdentity? identity, out string reason)
    {
        identity = null;
        if (!TryReadToken(pipe, out var facts))
        {
            reason = "could not read caller token";
            return false;
        }
        if (facts.User is null || facts.IsAnonymousOrGuest)
        {
            reason = "anonymous or guest caller";
            return false;
        }
        if (facts.IsNetwork)
        {
            // The pipe DACL already denies NETWORK; this is defence in depth.
            reason = "network logon";
            return false;
        }
        if (!devMode && !(facts.IsInteractive || facts.IsSystem || facts.IsAdmin))
        {
            reason = "caller is not an interactive user";
            return false;
        }

        var pid = ProcessInfo.GetPipeClientProcessId(pipe);
        if (pid is null)
        {
            reason = "unknown client process";
            return false;
        }

        // Dev mode only: the developer who started the console instance owns it. The dev service
        // runs with exactly the caller's rights, so granting Admin crosses no privilege boundary.
        // If the engine itself is elevated (started by an elevated GUI), a non-elevated process of the
        // same user must not borrow that elevation: it gets Viewer only.
        var devOwner = devMode && WindowsIdentity.GetCurrent().User is { } owner && facts.User.Equals(owner)
            && (facts.IsAdmin || !EngineElevated);
        var image = ProcessInfo.TryGetImagePath(pid.Value);
        identity = new CallerIdentity(facts.User, facts.IsAdmin || devOwner, pid.Value, image is null ? "?" : Path.GetFileName(image)) { DevOwner = devOwner };
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Per-request re-authentication: re-reads the caller's token through the pipe and checks
    /// it is still the same principal with (at least) the same elevation.
    /// </summary>
    public bool Revalidate(NamedPipeServerStream pipe)
    {
        if (!TryReadToken(pipe, out var facts) || facts.User is null)
        {
            return false;
        }
        return facts.User.Equals(User) && (!IsElevatedAdmin || facts.IsAdmin || DevOwner);
    }

    private static bool TryReadToken(NamedPipeServerStream pipe, out TokenFacts facts)
    {
        TokenFacts result = default;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                bool network = false, interactive = false;
                if (identity.Groups is { } groups)
                {
                    foreach (var group in groups)
                    {
                        if (group is SecurityIdentifier sid)
                        {
                            network |= sid.IsWellKnown(WellKnownSidType.NetworkSid);
                            interactive |= sid.IsWellKnown(WellKnownSidType.InteractiveSid);
                        }
                    }
                }
                // IsInRole uses CheckTokenMembership: under UAC the Administrators group is
                // deny-only in a non-elevated token, so this is true only for elevated callers.
                var admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                result = new TokenFacts(
                    identity.User,
                    admin,
                    network,
                    identity.IsAnonymous || identity.IsGuest,
                    interactive,
                    identity.IsSystem);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            facts = default;
            return false;
        }
        facts = result;
        return true;
    }
}
