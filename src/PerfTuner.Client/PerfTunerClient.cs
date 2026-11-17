using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using PerfTuner.Contracts;
using PerfTuner.Platform.Windows;

namespace PerfTuner.Client;

public sealed record ClientOptions
{
    /// <summary>Connect to the standalone engine (PerfTuner.Service --console, started by the GUI or by hand) instead of the installed service.</summary>
    public bool DevMode { get; init; }

    /// <summary>Allow-listed characters only (A-Z a-z 0-9 space . _ -), ≤ 64 chars.</summary>
    public string ClientName { get; init; } = "PerfTuner Client";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class ServerVerificationException(string message) : Exception(message);

public sealed class RemoteErrorException(ErrorResponse error) : Exception($"{error.Code}: {error.Message}")
{
    public ErrorResponse Error { get; } = error;
}

/// <summary>
/// IPC client. Verifies it is talking to the genuine service before sending anything:
/// the pipe server's PID must equal the PID the Service Control Manager reports for
/// PerfTunerSvc (so a process squatting on the pipe name is detected). Connects with
/// Identification-level impersonation so the service can identify — but never act as —
/// the user.
/// </summary>
public sealed class PerfTunerClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly ClientOptions _options;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Response>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _readBuffer = new byte[ProtocolConstants.MaxResponseFrameBytes];
    private Task? _readLoop;
    private int _nextId;
    private int _disconnected;

    private PerfTunerClient(NamedPipeClientStream pipe, ClientOptions options)
    {
        _pipe = pipe;
        _options = options;
    }

    public HelloAck? Session { get; private set; }

    /// <summary>Raised on the read-loop thread for every metric push. Handlers must be quick.</summary>
    public event Action<MetricsPush>? MetricsReceived;

    /// <summary>Raised once when the connection ends (either side).</summary>
    public event Action<Exception?>? Disconnected;

    public static async Task<PerfTunerClient> ConnectAsync(ClientOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var pipeName = options.DevMode ? PipeNames.Standalone() : ProtocolConstants.PipeName;
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync((int)options.ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
            VerifyServer(pipe, options.DevMode);

            var client = new PerfTunerClient(pipe, options);
            await client.HandshakeAsync(ct).ConfigureAwait(false);
            client._readLoop = Task.Run(client.ReadLoopAsync, CancellationToken.None);
            return client;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void VerifyServer(NamedPipeClientStream pipe, bool devMode)
    {
        var serverPid = ProcessInfo.GetPipeServerProcessId(pipe)
            ?? throw new ServerVerificationException("cannot determine pipe server process");

        if (devMode)
        {
            // Dev instance is not registered with the SCM; require the expected image name.
            // (The dev pipe's DACL already restricts it to the current user.)
            var image = ProcessInfo.TryGetImagePath(serverPid);
            if (image is null || !string.Equals(Path.GetFileName(image), "PerfTuner.Service.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ServerVerificationException($"dev pipe served by unexpected process {serverPid}");
            }
            return;
        }

        var service = ServiceControl.Query();
        if (service.State != ServiceRunState.Running || service.ProcessId != serverPid)
        {
            throw new ServerVerificationException(
                $"pipe server pid {serverPid} is not the registered service (SCM pid {service.ProcessId}, state {service.State})");
        }
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        var id = NextId();
        await WriteAsync(new HelloRequest { Id = id, ProtocolVersion = ProtocolConstants.ProtocolVersion, ClientName = _options.ClientName }, ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);
        var length = await FrameCodec.ReadFrameAsync(_pipe, _readBuffer, ProtocolConstants.MaxResponseFrameBytes, timeout.Token).ConfigureAwait(false);
        if (length < 0)
        {
            throw new IOException("server closed the connection during handshake");
        }
        if (!MessageCodec.TryDecodeResponse(_readBuffer.AsSpan(0, length), out var response, out var error))
        {
            throw new ProtocolViolationException($"invalid handshake response: {error}");
        }
        Session = response switch
        {
            HelloAck ack when ack.Id == id => ack,
            ErrorResponse err => throw new RemoteErrorException(err),
            _ => throw new ProtocolViolationException("unexpected handshake response"),
        };
    }

    /// <summary>Sends a request and awaits its correlated response. Error responses throw <see cref="RemoteErrorException"/>.</summary>
    public async Task<TResponse> RequestAsync<TResponse>(Func<uint, Request> build, CancellationToken ct = default) where TResponse : Response
    {
        ArgumentNullException.ThrowIfNull(build);
        var id = NextId();
        var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteAsync(build(id), ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(_options.RequestTimeout);
            var response = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            return response switch
            {
                TResponse typed => typed,
                ErrorResponse err => throw new RemoteErrorException(err),
                _ => throw new ProtocolViolationException($"unexpected response {response.GetType().Name}"),
            };
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task<StatusResponse> GetStatusAsync(CancellationToken ct = default)
        => RequestAsync<StatusResponse>(id => new GetStatusRequest { Id = id }, ct);

    public Task<HistoryResponse> GetHistoryAsync(int maxPoints, CancellationToken ct = default)
        => RequestAsync<HistoryResponse>(id => new GetHistoryRequest { Id = id, MaxPoints = maxPoints }, ct);

    public Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default)
        => RequestAsync<ConfigResponse>(id => new GetConfigRequest { Id = id }, ct);

    public Task<AckResponse> SubscribeAsync(bool enable, CancellationToken ct = default)
        => RequestAsync<AckResponse>(id => new SubscribeMetricsRequest { Id = id, Enable = enable }, ct);

    /// <summary>v5: stop the standalone engine gracefully (rolls back every change). The installed service refuses.</summary>
    public Task<AckResponse> ShutdownEngineAsync(CancellationToken ct = default)
        => RequestAsync<AckResponse>(id => new ShutdownEngineRequest { Id = id }, ct);

    public Task<PongResponse> PingAsync(CancellationToken ct = default)
        => RequestAsync<PongResponse>(id => new PingRequest { Id = id }, ct);

    // ---- protocol v2: tuning

    public Task<ProcessListResponse> GetProcessesAsync(int maxCount, CancellationToken ct = default)
        => RequestAsync<ProcessListResponse>(id => new GetProcessesRequest { Id = id, MaxCount = maxCount }, ct);

    /// <summary>Builds the request with the given id (use <c>with { Id = id }</c> on a template).</summary>
    public Task<ActionResultResponse> ProcessActionAsync(ProcessActionRequest template, CancellationToken ct = default)
        => RequestAsync<ActionResultResponse>(id => template with { Id = id }, ct);

    public Task<ChangesResponse> GetChangesAsync(CancellationToken ct = default)
        => RequestAsync<ChangesResponse>(id => new GetChangesRequest { Id = id }, ct);

    public Task<ActionResultResponse> RevertChangeAsync(long changeId, CancellationToken ct = default)
        => RequestAsync<ActionResultResponse>(id => new RevertChangeRequest { Id = id, ChangeId = changeId }, ct);

    public Task<ActionResultResponse> RevertAllAsync(CancellationToken ct = default)
        => RequestAsync<ActionResultResponse>(id => new RevertAllRequest { Id = id }, ct);

    public Task<ActionResultResponse> SetTuningOptionsAsync(bool? safeMode = null, TuningMode? mode = null, bool? autoCalm = null, int? threshold = null, CancellationToken ct = default)
        => RequestAsync<ActionResultResponse>(id => new SetTuningOptionsRequest { Id = id, SafeMode = safeMode, Mode = mode, AutoCalm = autoCalm, AutoCalmThresholdPercent = threshold }, ct);

    public Task<ActionResultResponse> ResetBreakerAsync(CancellationToken ct = default)
        => RequestAsync<ActionResultResponse>(id => new ResetBreakerRequest { Id = id }, ct);

    public Task<AckResponse> ReportForegroundAsync(int pid, CancellationToken ct = default)
        => RequestAsync<AckResponse>(id => new ReportForegroundRequest { Id = id, Pid = pid }, ct);

    public Task<RulesResponse> GetRulesAsync(CancellationToken ct = default)
        => RequestAsync<RulesResponse>(id => new GetRulesRequest { Id = id }, ct);

    public Task<ActionResultResponse> SetRulesAsync(AppRule[] rules, CancellationToken ct = default)
        => RequestAsync<ActionResultResponse>(id => new SetRulesRequest { Id = id, Rules = rules }, ct);

    public Task<InsightsResponse> GetInsightsAsync(CancellationToken ct = default)
        => RequestAsync<InsightsResponse>(id => new GetInsightsRequest { Id = id }, ct);

    public Task<ActionResultResponse> StartLatencyScanAsync(int seconds, CancellationToken ct = default)
        => RequestAsync<ActionResultResponse>(id => new StartLatencyScanRequest { Id = id, Seconds = seconds }, ct);

    public Task<BootReportResponse> GetBootReportAsync(CancellationToken ct = default)
        => RequestAsync<BootReportResponse>(id => new GetBootReportRequest { Id = id }, ct);

    public Task<ActionResultResponse> TrimBackgroundMemoryAsync(CancellationToken ct = default)
        => RequestAsync<ActionResultResponse>(id => new TrimBackgroundMemoryRequest { Id = id }, ct);

    // ---- protocol v4: forensics, visibility, autonomy

    public Task<TimelineResponse> GetTimelineAsync(long sinceUnixMs, int maxEvents, CancellationToken ct = default)
        => RequestAsync<TimelineResponse>(id => new GetTimelineRequest { Id = id, SinceUnixMs = sinceUnixMs, MaxEvents = maxEvents }, ct);

    public Task<ExplanationResponse> ExplainStallAsync(long eventId, CancellationToken ct = default)
        => RequestAsync<ExplanationResponse>(id => new ExplainStallRequest { Id = id, EventId = eventId }, ct);

    public Task<IoLatencyResponse> GetIoLatencyAsync(CancellationToken ct = default)
        => RequestAsync<IoLatencyResponse>(id => new GetIoLatencyRequest { Id = id }, ct);

    public Task<NetworkHealthResponse> GetNetworkHealthAsync(CancellationToken ct = default)
        => RequestAsync<NetworkHealthResponse>(id => new GetNetworkHealthRequest { Id = id }, ct);

    public Task<ActionResultResponse> StartWaitAnalysisAsync(int seconds, CancellationToken ct = default)
        => RequestAsync<ActionResultResponse>(id => new StartWaitAnalysisRequest { Id = id, Seconds = seconds }, ct);

    public Task<WaitAnalysisResponse> GetWaitAnalysisAsync(CancellationToken ct = default)
        => RequestAsync<WaitAnalysisResponse>(id => new GetWaitAnalysisRequest { Id = id }, ct);

    public Task<MemoryGovernorResponse> GetMemoryGovernorAsync(CancellationToken ct = default)
        => RequestAsync<MemoryGovernorResponse>(id => new GetMemoryGovernorRequest { Id = id }, ct);

    public Task<ContractsResponse> GetContractsAsync(CancellationToken ct = default)
        => RequestAsync<ContractsResponse>(id => new GetContractsRequest { Id = id }, ct);

    public Task<ActionResultResponse> SetProfileAsync(TuningProfile profile, CancellationToken ct = default)
        => RequestAsync<ActionResultResponse>(id => new SetProfileRequest { Id = id, Profile = profile }, ct);

    public Task<RegressionsResponse> GetRegressionsAsync(CancellationToken ct = default)
        => RequestAsync<RegressionsResponse>(id => new GetRegressionsRequest { Id = id }, ct);

    public Task<ActionResultResponse> StartExperimentAsync(string contractId, int pid, long createTime, CancellationToken ct = default)
        => RequestAsync<ActionResultResponse>(id => new StartExperimentRequest { Id = id, ContractId = contractId, Pid = pid, CreateTime = createTime }, ct);

    public Task<ExperimentsResponse> GetExperimentsAsync(CancellationToken ct = default)
        => RequestAsync<ExperimentsResponse>(id => new GetExperimentsRequest { Id = id }, ct);

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var length = await FrameCodec.ReadFrameAsync(_pipe, _readBuffer, ProtocolConstants.MaxResponseFrameBytes, _cts.Token).ConfigureAwait(false);
                if (length < 0)
                {
                    break;
                }
                if (!MessageCodec.TryDecodeResponse(_readBuffer.AsSpan(0, length), out var response, out var error))
                {
                    throw new ProtocolViolationException($"invalid server message: {error}");
                }

                if (response is MetricsPush push)
                {
                    MetricsReceived?.Invoke(push);
                }
                else if (response!.Id != 0 && _pending.TryGetValue(response.Id, out var tcs))
                {
                    tcs.TrySetResult(response);
                }
                else if (response is ErrorResponse { Id: 0 } unsolicited)
                {
                    // e.g. rate limiting; the server closes after repeated strikes.
                    failure = new RemoteErrorException(unsolicited);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ProtocolViolationException or ObjectDisposedException)
        {
            failure = ex;
        }
        finally
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(failure ?? new IOException("disconnected"));
            }
            if (Interlocked.Exchange(ref _disconnected, 1) == 0)
            {
                Disconnected?.Invoke(failure);
            }
        }
    }

    private async Task WriteAsync(Request request, CancellationToken ct)
    {
        var frame = MessageCodec.EncodeRequestFrame(request);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(frame, ct).ConfigureAwait(false);
            await _pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private uint NextId()
    {
        var id = (uint)Interlocked.Increment(ref _nextId);
        return id == 0 ? (uint)Interlocked.Increment(ref _nextId) : id;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
