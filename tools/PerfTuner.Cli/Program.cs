using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using PerfTuner.Client;
using PerfTuner.Contracts;

// perftuner-cli [--dev] [--samples N] [--selftest] [--tuning-selftest]
//   default     : handshake, status, config, history size, then N live samples
//   --selftest  : sends hostile/malformed traffic and checks the service rejects it

Console.OutputEncoding = Encoding.UTF8;
var dev = args.Contains("--dev");
var selfTest = args.Contains("--selftest");
var tuningTest = args.Contains("--tuning-selftest");
var insights = args.Contains("--insights");
var forensics = args.Contains("--forensics");
var experimentTest = args.Contains("--experiment-selftest");
var stopEngine = args.Contains("--stop-engine");
var samples = 5;
var idx = Array.IndexOf(args, "--samples");
if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], CultureInfo.InvariantCulture, out var n))
{
    samples = Math.Clamp(n, 1, 3600);
}

try
{
    return stopEngine ? await StopEngine()
        : experimentTest ? await ExperimentSelfTest.RunAsync(dev)
        : forensics ? await ShowForensics(dev)
        : insights ? await ShowInsights(dev)
        : tuningTest ? await TuningSelfTest.RunAsync(dev)
        : selfTest ? await SelfTest.RunAsync(dev)
        : await Monitor(dev, samples);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

static async Task<int> Monitor(bool dev, int samples)
{
    await using var client = await PerfTunerClient.ConnectAsync(new ClientOptions { DevMode = dev, ClientName = "perftuner-cli" });
    var ack = client.Session!;
    Console.WriteLine($"connected: server {ack.ServerVersion}, protocol {ack.ProtocolVersion}, role {ack.Role}, interval {ack.AggregationIntervalMs} ms");

    var status = await client.GetStatusAsync();
    Console.WriteLine($"status   : source='{status.TelemetrySource}', safeMode={status.SafeMode}, breaker={status.Breaker}, perProcessIo={status.PerProcessIoEnabled}, clients={status.ConnectedClients}");
    Console.WriteLine($"overhead : cpu {status.ServiceCpuPercent:F3}% (budget {status.CpuBudgetPercent}%), private {status.ServicePrivateMb:F1} MB (budget {status.MemoryBudgetMb} MB)");

    var config = await client.GetConfigAsync();
    Console.WriteLine($"config   : history {config.HistoryCapacity}, etw {config.EnableEtw}, warnings: {(config.ConfigWarnings.Length == 0 ? "none" : string.Join("; ", config.ConfigWarnings))}");

    var history = await client.GetHistoryAsync(ProtocolConstants.MaxHistoryPoints);
    Console.WriteLine($"history  : {history.Samples.Length} samples");

    var received = 0;
    var done = new TaskCompletionSource();
    client.MetricsReceived += push =>
    {
        var s = push.Sample;
        var top = push.TopIo.Length == 0 ? "" : "  top: " + string.Join(", ", push.TopIo.Select(p => $"{p.Name}({p.Pid}) {Fmt(p.DiskBytesPerSec + p.NetBytesPerSec)}"));
        Console.WriteLine($"[{DateTimeOffset.FromUnixTimeMilliseconds(s.TimestampUnixMs).ToLocalTime():HH:mm:ss}] cpu {s.CpuPercent,5:F1}%  mem {s.MemoryLoadPercent,4:F0}%  disk R {Fmt(s.DiskReadBytesPerSec)} W {Fmt(s.DiskWriteBytesPerSec)}  net ↓{Fmt(s.NetRxBytesPerSec)} ↑{Fmt(s.NetTxBytesPerSec)}  svc {s.ServiceCpuPercent:F3}%/{s.ServicePrivateMb:F0}MB [{s.Flags}]{top}");
        if (Interlocked.Increment(ref received) >= samples)
        {
            done.TrySetResult();
        }
    };
    client.Disconnected += ex => done.TrySetException(ex ?? new IOException("disconnected"));
    await client.SubscribeAsync(true);
    await done.Task.WaitAsync(TimeSpan.FromSeconds(samples * 3 + 5));
    return 0;
}

static async Task<int> ShowInsights(bool dev)
{
    await using var client = await PerfTunerClient.ConnectAsync(new ClientOptions { DevMode = dev, ClientName = "perftuner-cli" });
    await Task.Delay(2500); // let a few samples accumulate
    var i = await client.GetInsightsAsync();
    Console.WriteLine($"cpu clock : available={i.Clock.Available} base {i.Clock.BaseMhz} MHz, effective {i.Clock.EffectiveMhz} MHz ({i.Clock.PerformancePercent}%), limit {i.Clock.LimitPercent}%, limited {i.Clock.LimitedShareRecentPercent}% of recent time");
    Console.WriteLine($"leaks     : tracking {i.TrackedProcesses} processes over {i.LeakObservationMinutes} min, {i.Leaks.Length} suspect(s)");
    foreach (var l in i.Leaks) { Console.WriteLine($"            {l.Name} ({l.Pid}) {l.PrivateMb} MB, +{l.GrowthMbPerHour} MB/h, R²={l.Confidence}"); }
    Console.WriteLine($"latency   : {i.Latency.State} — {i.Latency.Message}");
    var scan = await client.StartLatencyScanAsync(5);
    Console.WriteLine($"scan start: {scan.Success} — {scan.Message}");
    var boot = await client.GetBootReportAsync();
    Console.WriteLine($"boot      : available={boot.Available} — {boot.Message}; {boot.Records.Length} records, {boot.Culprits.Length} culprits");
    foreach (var r in boot.Records.Take(5)) { Console.WriteLine($"            {DateTimeOffset.FromUnixTimeMilliseconds(r.TimeUnixMs).ToLocalTime():g} {r.Kind} {r.TotalSeconds:F1}s"); }
    foreach (var c in boot.Culprits.Take(5)) { Console.WriteLine($"            {c.Phase}/{c.Kind}: {c.Name} ×{c.Occurrences}, avg +{c.AvgDelayMs} ms"); }
    return 0;
}

static async Task<int> StopEngine()
{
    await using var client = await PerfTunerClient.ConnectAsync(new ClientOptions { DevMode = true, ClientName = "perftuner-cli" });
    await client.ShutdownEngineAsync();
    Console.WriteLine("standalone engine: graceful shutdown requested (live tuning is rolled back)");
    return 0;
}

static async Task<int> ShowForensics(bool dev)
{
    await using var client = await PerfTunerClient.ConnectAsync(new ClientOptions { DevMode = dev, ClientName = "perftuner-cli" });
    await Task.Delay(3000);
    var st = await client.GetStatusAsync();
    Console.WriteLine($"telemetry : {st.TelemetryHealth}; {st.TelemetryEventsPerSec:F0} events/s, {st.TelemetryEventsLost} lost; profile {st.Profile}");
    var tl = await client.GetTimelineAsync(0, 20);
    Console.WriteLine($"timeline  : {tl.Events.Length} event(s), {tl.StallsLastHour} stall(s) in the last hour");
    foreach (var e in tl.Events.Take(8)) { Console.WriteLine($"            #{e.Id} {DateTimeOffset.FromUnixTimeMilliseconds(e.TimeUnixMs).ToLocalTime():HH:mm:ss} [{e.Subsystem}/{e.Severity}] {e.Title} — {e.Detail}"); }
    if (tl.Events.FirstOrDefault(e => e.IsStall) is { } stall)
    {
        var x = await client.ExplainStallAsync(stall.Id);
        Console.WriteLine($"explain   : {x.Story}");
    }
    var io = await client.GetIoLatencyAsync();
    Console.WriteLine($"io latency: available={io.Available} — {io.Message}");
    Console.WriteLine($"            read p50/p90/p99 {io.ReadTotal.P50Ms}/{io.ReadTotal.P90Ms}/{io.ReadTotal.P99Ms} ms (n={io.ReadTotal.Count}); write p99 {io.WriteTotal.P99Ms} ms; queue p99 {io.Queue.P99Ms} ms; device p99 {io.Device.P99Ms} ms; queue length {io.DiskQueueLength}");
    var net = await client.GetNetworkHealthAsync();
    Console.WriteLine($"network   : {net.Health}; ↓{net.RxMbpsNow} ↑{net.TxMbpsNow} Mbit/s; 100 ms peaks ↓{net.PeakRxMbps100ms} ↑{net.PeakTxMbps100ms}; {net.RetransmitsLastMinute} retransmits/min; {net.Bursts.Length} burst(s)");
    var mem = await client.GetMemoryGovernorAsync();
    Console.WriteLine($"memory    : {mem.Behavior} — {mem.Explanation} Decision: {mem.Decision}");
    var reg = await client.GetRegressionsAsync();
    Console.WriteLine($"regression: {reg.Summary}");
    var contracts = await client.GetContractsAsync();
    Console.WriteLine($"contracts : {contracts.Contracts.Length} (active profile {contracts.ActiveProfile}; experimentable: {string.Join(", ", contracts.Contracts.Where(c => c.Experimentable).Select(c => c.Id))})");
    var exp = await client.GetExperimentsAsync();
    Console.WriteLine($"experiment: current={(exp.Current is null ? "none" : exp.Current.State.ToString())}, {exp.History.Length} in audit trail");
    var wait = await client.StartWaitAnalysisAsync(3);
    Console.WriteLine($"wait lens : {wait.Success} — {wait.Message}");
    if (wait.Success)
    {
        await Task.Delay(6000);
        var w = await client.GetWaitAnalysisAsync();
        Console.WriteLine($"            {w.State}: {w.Summary}");
        foreach (var p in w.Processes.Take(5)) { Console.WriteLine($"            {p.Name} ({p.Pid}) run {p.RunningMs} ready {p.ReadyMs} io {p.BlockedIoMs} paging {p.BlockedPagingMs} sync {p.BlockedSyncMs} — {p.Diagnosis}"); }
    }
    return 0;
}

static string Fmt(double bytesPerSec) => bytesPerSec switch
{
    >= 1024 * 1024 => $"{bytesPerSec / (1024 * 1024),6:F1} MB/s",
    >= 1024 => $"{bytesPerSec / 1024,6:F1} KB/s",
    _ => $"{bytesPerSec,6:F0}  B/s",
};

internal static class SelfTest
{
    public static async Task<int> RunAsync(bool dev)
    {
        var pipeName = dev ? PerfTuner.Platform.Windows.PipeNames.Standalone() : ProtocolConstants.PipeName;
        var failures = 0;

        async Task Check(string name, Func<Task<bool>> test)
        {
            bool ok;
            try { ok = await test(); }
            catch (Exception ex) { Console.WriteLine($"  ! {name}: {ex.GetType().Name} {ex.Message}"); ok = false; }
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
            if (!ok) { failures++; }
        }

        async Task<NamedPipeClientStream> Open()
        {
            var p = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await p.ConnectAsync(3000);
            return p;
        }

        static async Task Send(Stream s, string json)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var header = new byte[4];
            FrameCodec.WriteHeader(header, payload.Length);
            await s.WriteAsync(header);
            await s.WriteAsync(payload);
            await s.FlushAsync();
        }

        static async Task<Response?> Receive(Stream s)
        {
            var buffer = new byte[ProtocolConstants.MaxResponseFrameBytes];
            using var cts = new CancellationTokenSource(3000);
            try
            {
                var len = await FrameCodec.ReadFrameAsync(s, buffer, buffer.Length, cts.Token);
                if (len < 0) { return null; }
                return MessageCodec.TryDecodeResponse(buffer.AsSpan(0, len), out var r, out _) ? r : null;
            }
            catch (IOException) { return null; }
        }

        static async Task<bool> ClosedByServer(Stream s)
        {
            var one = new byte[1];
            using var cts = new CancellationTokenSource(6000);
            try { return await s.ReadAsync(one, cts.Token) == 0; }
            catch (IOException) { return true; }
        }

        const string Hello = """{"type":"hello","id":1,"protocolVersion":5,"clientName":"selftest"}""";

        await Check("request before hello is refused", async () =>
        {
            await using var p = await Open();
            await Send(p, """{"type":"getStatus","id":5}""");
            return await Receive(p) is ErrorResponse { Code: ErrorCodes.HandshakeRequired } && await ClosedByServer(p);
        });

        await Check("wrong protocol version is refused", async () =>
        {
            await using var p = await Open();
            await Send(p, """{"type":"hello","id":1,"protocolVersion":99,"clientName":"selftest"}""");
            return await Receive(p) is ErrorResponse { Code: ErrorCodes.UnsupportedVersion };
        });

        await Check("unknown message type is a schema error", async () =>
        {
            await using var p = await Open();
            await Send(p, Hello);
            _ = await Receive(p);
            await Send(p, """{"type":"runShell","id":2,"cmd":"calc.exe"}""");
            return await Receive(p) is ErrorResponse { Code: ErrorCodes.Schema };
        });

        await Check("duplicate-key smuggling is rejected", async () =>
        {
            await using var p = await Open();
            await Send(p, Hello);
            _ = await Receive(p);
            await Send(p, """{"type":"ping","type":"setMonitoringOptions","id":2,"enablePerProcessIo":false}""");
            return await Receive(p) is ErrorResponse { Code: ErrorCodes.Malformed };
        });

        await Check("unknown field is rejected", async () =>
        {
            await using var p = await Open();
            await Send(p, Hello);
            _ = await Receive(p);
            await Send(p, """{"type":"getHistory","id":2,"maxPoints":10,"path":"..\\..\\Windows"}""");
            return await Receive(p) is ErrorResponse { Code: ErrorCodes.Schema };
        });

        await Check("out-of-bounds value is rejected", async () =>
        {
            await using var p = await Open();
            await Send(p, Hello);
            _ = await Receive(p);
            await Send(p, """{"type":"getHistory","id":2,"maxPoints":1000000}""");
            return await Receive(p) is ErrorResponse { Code: ErrorCodes.Schema };
        });

        await Check("admin-only request needs elevation (or succeeds when elevated)", async () =>
        {
            await using var p = await Open();
            await Send(p, Hello);
            var ack = await Receive(p) as HelloAck;
            await Send(p, """{"type":"setMonitoringOptions","id":2,"enablePerProcessIo":true}""");
            var r = await Receive(p);
            return ack?.Role == ClientRole.Admin ? r is AckResponse : r is ErrorResponse { Code: ErrorCodes.Forbidden };
        });

        await Check("oversized frame closes the connection", async () =>
        {
            await using var p = await Open();
            var header = new byte[4];
            FrameCodec.WriteHeader(header, ProtocolConstants.MaxRequestFrameBytes + 1);
            await p.WriteAsync(header);
            await p.FlushAsync();
            return await ClosedByServer(p);
        });

        await Check("three malformed messages close the connection", async () =>
        {
            await using var p = await Open();
            await Send(p, Hello);
            _ = await Receive(p);
            for (var i = 0; i < 3; i++)
            {
                await Send(p, "{not json");
                _ = await Receive(p);
            }
            return await ClosedByServer(p);
        });

        await Check("request flood is rate limited", async () =>
        {
            await using var p = await Open();
            await Send(p, Hello);
            _ = await Receive(p);
            var limited = false;
            for (var i = 0; i < 200 && !limited; i++)
            {
                await Send(p, $$"""{"type":"ping","id":{{i + 10}}}""");
                limited = await Receive(p) is ErrorResponse { Code: ErrorCodes.RateLimited };
            }
            return limited;
        });

        await Check("silent client is dropped after handshake timeout", async () =>
        {
            await using var p = await Open();
            return await ClosedByServer(p);
        });

        Console.WriteLine(failures == 0 ? "self-test: all checks passed" : $"self-test: {failures} check(s) FAILED");
        return failures == 0 ? 0 : 3;
    }
}
