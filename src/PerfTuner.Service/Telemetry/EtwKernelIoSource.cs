using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using PerfTuner.Contracts;
using PerfTuner.Core.Telemetry;

namespace PerfTuner.Service.Telemetry;

/// <summary>
/// Disk and network IO from a private kernel ETW session.
/// <list type="bullet">
/// <item><b>Event filtering:</b> only the DiskIO and NetworkTCPIP kernel keywords are
///   enabled — no process/thread/image rundown, no stacks, no file-name events. That is
///   the cheapest kernel configuration that still yields per-process network attribution.</item>
/// <item><b>Private session name</b> (Windows 8+ "system logger" mode) instead of the
///   global "NT Kernel Logger", so we never hijack or stop another tool's kernel trace.</item>
/// <item><b>Small buffers:</b> 4 MB; if the consumer falls behind the kernel drops events
///   (counted in EventsLost) rather than the service growing memory.</item>
/// <item><b>Hot path:</b> each callback does one locked add into a preallocated table —
///   TraceEvent reuses its event objects, so there is no per-event allocation.</item>
/// </list>
/// Requires admin / LocalSystem; the engine falls back to PDH if <see cref="Start"/> throws.
/// </summary>
internal sealed class EtwKernelIoSource : IIoSource
{
    public const string SessionName = "PerfTuner-KernelIO";

    private readonly IoAccumulator _accumulator;
    private readonly IForensicsSink _sink;
    private readonly ILogger _logger;
    private long _events;
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile bool _faulted;
    private volatile bool _stopping;

    public EtwKernelIoSource(IoAccumulator accumulator, IForensicsSink sink, ILogger logger)
    {
        _accumulator = accumulator;
        _sink = sink;
        _logger = logger;
    }

    public string Name => "ETW (kernel DiskIO + TcpIp)";
    public SampleFlags Flags => SampleFlags.EtwIo;
    public bool SupportsPerProcess => true;
    public bool Faulted => _faulted;

    public int EventsLost => _session?.EventsLost ?? 0;

    /// <summary>Total events processed (for the telemetry-health rate).</summary>
    public long EventsProcessed => Interlocked.Read(ref _events);

    public void Start()
    {
        if (TraceEventSession.IsElevated() != true)
        {
            throw new UnauthorizedAccessException("kernel ETW requires elevation");
        }

        // If a previous instance crashed, a session with our name may still exist:
        // TraceEventSession (default options) stops and replaces it, so there is no leak.
        var session = new TraceEventSession(SessionName)
        {
            StopOnDispose = true,
            BufferSizeMB = 4,
        };

        try
        {
            // DiskIO completions carry total elapsed and device service time (F6); TCP/UDP
            // payload sizes with event timestamps feed the 100 ms microburst bins (F7);
            // hard faults attribute paging (F3/F1). Still no stacks, rundowns or file names.
            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.DiskIO
                | KernelTraceEventParser.Keywords.NetworkTCPIP
                | KernelTraceEventParser.Keywords.MemoryHardFaults);

            var kernel = session.Source.Kernel;
            var acc = _accumulator;
            var sink = _sink;
            kernel.DiskIORead += e =>
            {
                Interlocked.Increment(ref _events);
                acc.AddDiskRead(e.ProcessID, e.TransferSize);
                sink.DiskIo(e.ProcessID, false, e.TransferSize, e.ElapsedTimeMSec, e.DiskServiceTimeMSec);
            };
            kernel.DiskIOWrite += e =>
            {
                Interlocked.Increment(ref _events);
                acc.AddDiskWrite(e.ProcessID, e.TransferSize);
                sink.DiskIo(e.ProcessID, true, e.TransferSize, e.ElapsedTimeMSec, e.DiskServiceTimeMSec);
            };
            kernel.TcpIpRecv += e => Net(e.ProcessID, e.size, true, e.TimeStamp);
            kernel.TcpIpSend += e => Net(e.ProcessID, e.size, false, e.TimeStamp);
            kernel.TcpIpRecvIPV6 += e => Net(e.ProcessID, e.size, true, e.TimeStamp);
            kernel.TcpIpSendIPV6 += e => Net(e.ProcessID, e.size, false, e.TimeStamp);
            kernel.UdpIpRecv += e => Net(e.ProcessID, e.size, true, e.TimeStamp);
            kernel.UdpIpSend += e => Net(e.ProcessID, e.size, false, e.TimeStamp);
            kernel.UdpIpRecvIPV6 += e => Net(e.ProcessID, e.size, true, e.TimeStamp);
            kernel.UdpIpSendIPV6 += e => Net(e.ProcessID, e.size, false, e.TimeStamp);
            kernel.TcpIpRetransmit += e => { Interlocked.Increment(ref _events); sink.Retransmit(); };
            kernel.TcpIpRetransmitIPV6 += e => { Interlocked.Increment(ref _events); sink.Retransmit(); };
            kernel.MemoryHardFault += e => { Interlocked.Increment(ref _events); sink.HardFault(e.ProcessID); };
        }
        catch
        {
            session.Dispose();
            throw;
        }

        _session = session;
        // ProcessTrace blocks for the session's lifetime: give it one dedicated background
        // thread rather than tying up a thread-pool thread forever.
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "PerfTuner ETW consumer",
            Priority = ThreadPriority.AboveNormal, // drain buffers promptly to avoid lost events
        };
        _thread.Start();
    }

    private void Net(int pid, int size, bool rx, DateTime timestamp)
    {
        Interlocked.Increment(ref _events);
        if (rx) { _accumulator.AddNetRx(pid, size); } else { _accumulator.AddNetTx(pid, size); }
        _sink.Network(new DateTimeOffset(timestamp).ToUnixTimeMilliseconds(), size, rx, pid);
    }

    private void Pump()
    {
        try
        {
            _session!.Source.Process();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ETW processing thread failed");
        }
        finally
        {
            if (!_stopping)
            {
                // The session ended without us asking (killed via logman/xperf, or resource limit).
                _faulted = true;
                _logger.LogWarning("ETW session ended unexpectedly; engine will switch to fallback");
            }
        }
    }

    public void Poll(double intervalMs)
    {
        // Event-driven: nothing to do.
    }

    public void Dispose()
    {
        _stopping = true;
        _session?.Dispose();   // StopOnDispose → ControlTrace(STOP); Process() returns
        _thread?.Join(TimeSpan.FromSeconds(5));
        _session = null;
    }
}
