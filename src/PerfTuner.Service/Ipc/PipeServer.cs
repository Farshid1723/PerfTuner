using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using PerfTuner.Contracts;
using PerfTuner.Service.Engine;

namespace PerfTuner.Service.Ipc;

/// <summary>
/// Named-pipe listener.
/// <para><b>Pipe DACL (production)</b>: SYSTEM + Administrators full control; INTERACTIVE
/// users read/write only (no FILE_CREATE_PIPE_INSTANCE, so a user process cannot add a rogue
/// instance of our pipe); NETWORK explicitly denied (no remote access over SMB).</para>
/// <para><b>Anti-squatting</b>: the first instance is created with FirstPipeInstance — if some
/// other process already owns the name, creation fails and the service refuses to serve
/// rather than sharing a name with an impostor. Clients additionally verify the server PID
/// against the SCM (see the client library).</para>
/// </summary>
internal sealed partial class PipeServer : BackgroundService
{
    private readonly RuntimeState _state;
    private readonly SafetyCoordinator _safety;
    private readonly MetricsHub _hub;
    private readonly TuningEngine _tuning;
    private readonly InsightsEngine _insights;
    private readonly ForensicsEngine _forensics;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PipeServer> _logger;
    private readonly ConcurrentDictionary<Task, byte> _sessions = new();
    private int _active;

    public PipeServer(RuntimeState state, SafetyCoordinator safety, MetricsHub hub, TuningEngine tuning, InsightsEngine insights, ForensicsEngine forensics, IHostApplicationLifetime lifetime, ILogger<PipeServer> logger)
    {
        _state = state;
        _safety = safety;
        _hub = hub;
        _tuning = tuning;
        _insights = insights;
        _forensics = forensics;
        _lifetime = lifetime;
        _logger = logger;
    }

    public string PipeName => _state.DevMode ? PerfTuner.Platform.Windows.PipeNames.Standalone() : ProtocolConstants.PipeName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var security = BuildSecurity(_state.DevMode);
        var maxClients = _state.Config.MaxClients;
        var first = true;
        LogListening(PipeName, maxClients);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                pipe = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.InOut,
                    maxClients + 1,          // +1 so an over-limit client gets a "busy" answer
                    PipeTransmissionMode.Byte,
                    options,
                    ProtocolConstants.MaxRequestFrameBytes,
                    64 * 1024,
                    security);
                first = false;
            }
            catch (UnauthorizedAccessException) when (first)
            {
                LogPipeSquatted(PipeName);
                return; // fail closed: never serve alongside an impostor
            }
            catch (IOException)
            {
                // All instances busy; back off briefly.
                await Task.Delay(250, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var session = ServeAsync(pipe, maxClients, stoppingToken);
            _sessions.TryAdd(session, 0);
            _ = session.ContinueWith(t => _sessions.TryRemove(t, out _), TaskScheduler.Default);
        }

        // Give sessions a moment to close cleanly.
        await Task.WhenAny(Task.WhenAll(_sessions.Keys), Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, int maxClients, CancellationToken ct)
    {
        await using var _ = pipe.ConfigureAwait(false);
        if (Interlocked.Increment(ref _active) > maxClients)
        {
            Interlocked.Decrement(ref _active);
            await TrySendAndCloseAsync(pipe, ErrorCodes.Busy, "too many clients", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            if (!CallerIdentity.TryResolve(pipe, _state.DevMode, out var caller, out var reason))
            {
                LogAuthRejected(reason);
                await TrySendAndCloseAsync(pipe, ErrorCodes.Unauthorized, "caller not permitted", ct).ConfigureAwait(false);
                return;
            }

            var session = new ClientSession(pipe, caller!, _state, _safety, _hub, _tuning, _insights, _forensics, _lifetime.StopApplication, () => Volatile.Read(ref _active), _logger);
            await using (session.ConfigureAwait(false))
            {
                await session.RunAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // A bug in one session must never take down the service.
            _logger.LogError(ex, "Unhandled error in IPC session");
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private static async Task TrySendAndCloseAsync(NamedPipeServerStream pipe, string code, string message, CancellationToken ct)
    {
        try
        {
            var frame = MessageCodec.EncodeResponseFrame(new ErrorResponse { Id = 0, Code = code, Message = message });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1000);
            await pipe.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
        }
    }

    internal static PipeSecurity BuildSecurity(bool devMode)
    {
        var security = new PipeSecurity();
        if (devMode)
        {
            // Dev instance runs as the developer: only that user (plus SYSTEM) may connect.
            var me = WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        else
        {
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC listening on named pipe {PipeName} (max {MaxClients} clients)")]
    private partial void LogListening(string pipeName, int maxClients);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Pipe name {PipeName} is already owned by another process (possible squatting). IPC disabled.")]
    private partial void LogPipeSquatted(string pipeName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IPC connection rejected: {Reason}")]
    private partial void LogAuthRejected(string reason);
}
