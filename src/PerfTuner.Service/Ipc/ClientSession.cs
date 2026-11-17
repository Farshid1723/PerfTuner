using System.Buffers;
using System.IO.Pipes;
using PerfTuner.Contracts;
using PerfTuner.Core.Security;
using PerfTuner.Service.Engine;

namespace PerfTuner.Service.Ipc;

/// <summary>
/// One authenticated client connection. Life cycle:
/// <list type="number">
/// <item>Handshake: the first frame must be a valid <see cref="HelloRequest"/> within 5 s.</item>
/// <item>Request loop: for every frame → size check → strict decode → rate limit →
///   re-authenticate caller token → role authorization → dispatch → respond.</item>
/// <item>Optional push loop: metric frames from <see cref="MetricsHub"/>.</item>
/// </list>
/// Violations of framing close the connection immediately; schema/rate violations get an
/// error response and close the connection after <see cref="MaxStrikes"/> strikes.
/// </summary>
internal sealed partial class ClientSession : IAsyncDisposable
{
    private const int MaxStrikes = 3;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly NamedPipeServerStream _pipe;
    private readonly CallerIdentity _caller;
    private readonly RuntimeState _state;
    private readonly SafetyCoordinator _safety;
    private readonly MetricsHub _hub;
    private readonly TuningEngine _tuning;
    private readonly InsightsEngine _insights;
    private readonly ForensicsEngine _forensics;
    private readonly Action _stopEngine;
    private readonly Func<int> _connectedClients;
    private CancellationToken _serviceToken;
    private readonly ILogger _logger;

    private readonly byte[] _readBuffer = new byte[ProtocolConstants.MaxRequestFrameBytes];
    private readonly ArrayBufferWriter<byte> _responseWriter = new(1024);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TokenBucket _rateLimiter;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    private CancellationTokenSource? _sessionCts;
    private long _subscriptionId = -1;
    private Task? _pushTask;
    private CancellationTokenSource? _pushCts;
    private int _strikes;

    public ClientSession(
        NamedPipeServerStream pipe,
        CallerIdentity caller,
        RuntimeState state,
        SafetyCoordinator safety,
        MetricsHub hub,
        TuningEngine tuning,
        InsightsEngine insights,
        ForensicsEngine forensics,
        Action stopEngine,
        Func<int> connectedClients,
        ILogger logger)
    {
        _pipe = pipe;
        _caller = caller;
        _state = state;
        _safety = safety;
        _hub = hub;
        _tuning = tuning;
        _insights = insights;
        _forensics = forensics;
        _stopEngine = stopEngine;
        _connectedClients = connectedClients;
        _logger = logger;
        var rate = state.Config.MaxRequestsPerSecond;
        _rateLimiter = new TokenBucket(rate, rate * 2, Environment.TickCount64);
    }

    public async Task RunAsync(CancellationToken serviceToken)
    {
        _serviceToken = serviceToken;
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        var ct = _sessionCts.Token;

        try
        {
            if (!await HandshakeAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                var length = await FrameCodec.ReadFrameAsync(_pipe, _readBuffer, ProtocolConstants.MaxRequestFrameBytes, ct).ConfigureAwait(false);
                if (length < 0)
                {
                    break; // client closed
                }
                if (!await HandleFrameAsync(length, ct).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (ProtocolViolationException ex)
        {
            LogProtocolViolation(_caller.ProcessId, ex.Message);
        }
        catch (OperationCanceledException)
        {
            // service stopping, handshake timeout or write timeout
        }
        catch (IOException)
        {
            // pipe broken: client went away
        }
    }

    private async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProtocolConstants.HandshakeTimeoutMs);

        int length;
        try
        {
            length = await FrameCodec.ReadFrameAsync(_pipe, _readBuffer, ProtocolConstants.MaxRequestFrameBytes, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogHandshakeRejected(_caller.ProcessId, "timeout");
            return false;
        }
        if (length < 0)
        {
            return false;
        }

        if (!MessageCodec.TryDecodeRequest(_readBuffer.AsSpan(0, length), out var request, out var code, out var error))
        {
            await SendAsync(Error(0, code, error), ct).ConfigureAwait(false);
            LogHandshakeRejected(_caller.ProcessId, error);
            return false;
        }
        if (request is not HelloRequest hello)
        {
            await SendAsync(Error(request!.Id, ErrorCodes.HandshakeRequired, "first message must be hello"), ct).ConfigureAwait(false);
            LogHandshakeRejected(_caller.ProcessId, "no hello");
            return false;
        }
        if (hello.ProtocolVersion != ProtocolConstants.ProtocolVersion)
        {
            await SendAsync(Error(hello.Id, ErrorCodes.UnsupportedVersion, $"server speaks protocol {ProtocolConstants.ProtocolVersion}"), ct).ConfigureAwait(false);
            LogHandshakeRejected(_caller.ProcessId, "version mismatch");
            return false;
        }

        await SendAsync(new HelloAck
        {
            Id = hello.Id,
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            ServerVersion = _state.Version,
            SessionId = _sessionId,
            Role = _caller.Role,
            AggregationIntervalMs = _state.AggregationIntervalMs,
            MaxRequestsPerSecond = _state.Config.MaxRequestsPerSecond,
        }, ct).ConfigureAwait(false);

        LogSessionStarted(_sessionId, hello.ClientName, _caller.ProcessId, _caller.ImageName, _caller.Role);
        return true;
    }

    /// <returns>false to close the connection.</returns>
    private async Task<bool> HandleFrameAsync(int length, CancellationToken ct)
    {
        if (!_rateLimiter.TryTake(Environment.TickCount64))
        {
            await SendAsync(Error(0, ErrorCodes.RateLimited, "too many requests"), ct).ConfigureAwait(false);
            return ++_strikes < MaxStrikes;
        }

        if (!MessageCodec.TryDecodeRequest(_readBuffer.AsSpan(0, length), out var request, out var code, out var error))
        {
            await SendAsync(Error(0, code, error), ct).ConfigureAwait(false);
            LogRejectedRequest(_sessionId, code);
            return ++_strikes < MaxStrikes;
        }

        // Authenticate on every request: the token behind the pipe must still be the
        // principal that completed the handshake.
        if (!_caller.Revalidate(_pipe))
        {
            await SendAsync(Error(request!.Id, ErrorCodes.Unauthorized, "caller identity changed"), ct).ConfigureAwait(false);
            LogAuthFailure(_sessionId);
            return false;
        }

        if (RequiredRole(request!) > _caller.Role)
        {
            await SendAsync(Error(request!.Id, ErrorCodes.Forbidden, "requires an elevated administrator"), ct).ConfigureAwait(false);
            LogForbidden(_sessionId, request!.GetType().Name);
            return ++_strikes < MaxStrikes;
        }

        var response = Dispatch(request!);
        await SendAsync(response, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Authorization table. New request types default to Admin (fail closed).</summary>
    private static ClientRole RequiredRole(Request request) => request switch
    {
        PingRequest or GetStatusRequest or SubscribeMetricsRequest or GetHistoryRequest or GetConfigRequest
            or GetProcessesRequest or GetChangesRequest or GetRulesRequest
            or GetInsightsRequest or GetBootReportRequest
            or GetTimelineRequest or ExplainStallRequest or GetIoLatencyRequest or GetNetworkHealthRequest
            or GetWaitAnalysisRequest or GetMemoryGovernorRequest or GetContractsRequest or GetRegressionsRequest
            or GetExperimentsRequest => ClientRole.Viewer,
        // StartWaitAnalysis (kernel trace), StartExperiment (changes a process) and SetProfile are Admin.
        _ => ClientRole.Admin,
    };

    private Response Dispatch(Request request)
    {
        switch (request)
        {
            case PingRequest:
                return new PongResponse { Id = request.Id, ServerTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

            case GetStatusRequest:
                return BuildStatus(request.Id);

            case SubscribeMetricsRequest subscribe:
                if (subscribe.Enable) { StartPush(); } else { StopPush(); }
                return new AckResponse { Id = request.Id };

            case GetHistoryRequest history:
            {
                var buffer = new MetricSample[Math.Min(history.MaxPoints, _state.History.Capacity)];
                var n = _state.History.CopyLatest(buffer);
                if (n < buffer.Length)
                {
                    Array.Resize(ref buffer, n);
                }
                return new HistoryResponse { Id = request.Id, Samples = buffer };
            }

            case GetConfigRequest:
                return new ConfigResponse
                {
                    Id = request.Id,
                    AggregationIntervalMs = _state.AggregationIntervalMs,
                    HistoryCapacity = _state.Config.HistoryCapacity,
                    EnableEtw = _state.Config.EnableEtw,
                    EnablePerProcessIo = _state.PerProcessIoRequested,
                    TopProcessCount = _state.Config.TopProcessCount,
                    SafeMode = _tuning.CurrentState.SafeMode,
                    ConfigWarnings = _state.ConfigWarnings,
                };

            case SetMonitoringOptionsRequest options:
                if (options.EnablePerProcessIo is { } perProcess)
                {
                    _state.PerProcessIoRequested = perProcess;
                }
                if (options.AggregationIntervalMs is { } interval)
                {
                    _state.AggregationIntervalMs = interval;
                }
                LogOptionsChanged(_sessionId, options.EnablePerProcessIo, options.AggregationIntervalMs);
                return new AckResponse { Id = request.Id };

            case GetProcessesRequest list:
                return _tuning.GetProcesses(request.Id, list.MaxCount, Environment.TickCount64);

            case ProcessActionRequest action:
                LogAction(_sessionId, action.Action, action.Pid);
                return _tuning.ApplyAction(action, Environment.TickCount64);

            case GetChangesRequest:
                return _tuning.GetChanges(request.Id);

            case RevertChangeRequest revert:
                return _tuning.Revert(request.Id, revert.ChangeId);

            case RevertAllRequest:
                return _tuning.RevertAll(request.Id);

            case SetTuningOptionsRequest tuningOptions:
                return _tuning.SetOptions(tuningOptions);

            case ResetBreakerRequest:
                return _tuning.ResetBreaker(request.Id);

            case ReportForegroundRequest foreground:
                _tuning.ReportForeground(foreground.Pid, Environment.TickCount64);
                return new AckResponse { Id = request.Id };

            case GetRulesRequest:
                return new RulesResponse { Id = request.Id, Rules = [.. _tuning.CurrentState.Rules] };

            case SetRulesRequest rules:
                return _tuning.SetRules(request.Id, rules.Rules);

            case TrimBackgroundMemoryRequest:
                return _tuning.TrimBackground(request.Id, Environment.TickCount64);

            case GetInsightsRequest:
                return _insights.GetInsights(request.Id);

            case StartLatencyScanRequest scan:
                return _insights.StartLatencyScan(request.Id, scan.Seconds, _serviceToken);

            case GetBootReportRequest:
                return _insights.GetBootReport(request.Id);

            // ---- v4: forensics, visibility, autonomy
            case GetTimelineRequest timeline:
                return _forensics.GetTimeline(request.Id, timeline.SinceUnixMs, timeline.MaxEvents);

            case ExplainStallRequest explain:
                return _forensics.ExplainStall(request.Id, explain.EventId);

            case GetIoLatencyRequest:
                return _forensics.GetIoLatency(request.Id);

            case GetNetworkHealthRequest:
                return _forensics.GetNetworkHealth(request.Id);

            case StartWaitAnalysisRequest wait:
                return _forensics.Wait.Start(request.Id, wait.Seconds, _serviceToken);

            case GetWaitAnalysisRequest:
                return _forensics.Wait.Last(request.Id);

            case GetMemoryGovernorRequest:
                return _forensics.GetMemoryGovernor(request.Id);

            case GetContractsRequest:
                return _tuning.GetContracts(request.Id);

            case SetProfileRequest profile:
                return _tuning.SetProfile(request.Id, profile.Profile);

            case GetRegressionsRequest:
                return _forensics.GetRegressions(request.Id);

            case StartExperimentRequest experiment:
                return _forensics.Experiments.Start(request.Id, experiment.ContractId, experiment.Pid, experiment.CreateTime,
                    autonomous: false, Environment.TickCount64, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            case GetExperimentsRequest:
                return _forensics.Experiments.Get(request.Id);

            case ShutdownEngineRequest:
                if (!_state.DevMode)
                {
                    return Error(request.Id, ErrorCodes.Forbidden, "the installed service is stopped through Windows, not over IPC");
                }
                // Reply first, then stop: the host rolls every change back before exiting.
                _ = Task.Delay(300, CancellationToken.None).ContinueWith(_ => _stopEngine(), TaskScheduler.Default);
                return new AckResponse { Id = request.Id };

            default:
                return Error(request.Id, ErrorCodes.Schema, "unsupported request");
        }
    }

    private StatusResponse BuildStatus(uint id)
    {
        var snap = _state.Snapshot;
        var tuning = _tuning.CurrentState;
        var (eventsPerSec, eventsLost, health) = _forensics.TelemetryHealth();
        return new StatusResponse
        {
            Id = id,
            ServerVersion = _state.Version,
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - _state.StartedAt).TotalSeconds,
            SafeMode = tuning.SafeMode,
            TelemetrySource = snap.TelemetrySource,
            PerProcessIoEnabled = snap.PerProcessIoActive,
            Degraded = snap.Degraded,
            Breaker = _safety.State,
            BreakerReason = _safety.Breaker.LastReason,
            ActiveChanges = _safety.Journal.Count,
            ConnectedClients = _connectedClients(),
            AggregationIntervalMs = _state.AggregationIntervalMs,
            ServiceCpuPercent = snap.ServiceCpuPercent,
            ServicePrivateMb = snap.ServicePrivateMb,
            CpuBudgetPercent = _state.Config.Budgets.ServiceCpuPercent,
            MemoryBudgetMb = _state.Config.Budgets.ServicePrivateMb,
            Mode = tuning.Mode,
            AutoCalm = tuning.AutoCalm,
            AutoCalmThresholdPercent = tuning.AutoCalmThresholdPercent,
            RulesCount = tuning.Rules.Count,
            HybridCpu = _tuning.HybridCpu,
            ForegroundKnown = _tuning.ForegroundKnown(Environment.TickCount64),
            TuningAllowed = _safety.AllowsTuning,
            Profile = tuning.Profile,
            TelemetryEventsPerSec = eventsPerSec,
            TelemetryEventsLost = eventsLost,
            TelemetryHealth = health,
        };
    }

    private void StartPush()
    {
        if (_subscriptionId >= 0)
        {
            return;
        }
        var (id, reader) = _hub.Subscribe();
        _subscriptionId = id;
        _pushCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts!.Token);
        var token = _pushCts.Token;
        _pushTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    await WriteRawAsync(frame, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                _sessionCts?.Cancel(); // broken pipe: end the whole session
            }
        }, token);
    }

    private void StopPush()
    {
        if (_subscriptionId < 0)
        {
            return;
        }
        _hub.Unsubscribe(_subscriptionId);
        _subscriptionId = -1;
        _pushCts?.Cancel();
    }

    private async Task SendAsync(Response response, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            MessageCodec.WriteResponseFrame(_responseWriter, response);
            await WriteWithTimeoutAsync(_responseWriter.WrittenMemory, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteRawAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteWithTimeoutAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>A client that stops reading for 5 s is disconnected; it can never back-pressure the service.</summary>
    private async Task WriteWithTimeoutAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WriteTimeout);
        try
        {
            await _pipe.WriteAsync(data, timeout.Token).ConfigureAwait(false);
            await _pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _sessionCts?.Cancel();
            throw;
        }
    }

    private static ErrorResponse Error(uint id, string code, string message) => new()
    {
        Id = id,
        Code = code,
        Message = message.Length > ProtocolConstants.MaxErrorMessageLength ? message[..ProtocolConstants.MaxErrorMessageLength] : message,
    };

    public async ValueTask DisposeAsync()
    {
        StopPush();
        _sessionCts?.Cancel();
        if (_pushTask is not null)
        {
            try { await _pushTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _pushCts?.Dispose();
        _sessionCts?.Dispose();
        _writeLock.Dispose();
        LogSessionEnded(_sessionId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC session {SessionId} started: client {ClientName}, pid {Pid} ({Image}), role {Role}")]
    private partial void LogSessionStarted(string sessionId, string clientName, int pid, string image, ClientRole role);

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC session {SessionId} ended")]
    private partial void LogSessionEnded(string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handshake rejected for pid {Pid}: {Reason}")]
    private partial void LogHandshakeRejected(int pid, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Protocol violation from pid {Pid}: {Reason}; connection closed")]
    private partial void LogProtocolViolation(int pid, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId} rejected request ({Code})")]
    private partial void LogRejectedRequest(string sessionId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId} failed per-request authentication; closed")]
    private partial void LogAuthFailure(string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId} forbidden request {RequestType}")]
    private partial void LogForbidden(string sessionId, string requestType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} requested {Action} on pid {Pid}")]
    private partial void LogAction(string sessionId, ProcessAction action, int pid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} changed monitoring options: perProcessIo={PerProcess}, intervalMs={IntervalMs}")]
    private partial void LogOptionsChanged(string sessionId, bool? perProcess, int? intervalMs);
}
