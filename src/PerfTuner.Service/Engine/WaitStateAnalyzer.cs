using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using PerfTuner.Contracts;
using PerfTuner.Platform.Windows.Telemetry;

namespace PerfTuner.Service.Engine;

/// <summary>
/// CPU wait-state lens (F8): "CPU % isn't the whole story." A short kernel trace of
/// <c>ContextSwitch</c> + <c>Dispatcher</c> (ReadyThread) events splits each process's wall time into:
/// <list type="bullet">
/// <item><b>Running</b> — on a CPU (switch-in → switch-out).</item>
/// <item><b>Ready</b> — runnable but waiting for a CPU (readied / preempted → switch-in). High ready
///   time = CPU contention, invisible in CPU % graphs.</item>
/// <item><b>Blocked</b> — waiting (switch-out in Wait state → readied), bucketed by the kernel wait
///   reason: kernel/IO (Executive), paging (PageIn/FreePage/VirtualMemory), synchronisation
///   (UserRequest/LPC/EventPair) and idle sleeps (DelayExecution/Suspended).</item>
/// </list>
/// Bounded: ≤ 200 000 threads and ≤ 4 096 processes tracked; runs at most 30 s; private session.
/// </summary>
internal sealed class WaitStateAnalyzer
{
    private const string SessionName = "PerfTuner-WaitLens";
    private const int ThreadStateWait = 5;   // System.Diagnostics.ThreadState.Wait
    private const int ThreadStateReady = 1;  // System.Diagnostics.ThreadState.Ready

    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private WaitAnalysisResponse _last = Empty(LatencyScanState.Idle, "Not run yet.", 0);
    private int _running;

    public WaitStateAnalyzer(ILogger logger) => _logger = logger;

    public WaitAnalysisResponse Last(uint id)
    {
        lock (_gate) { return _last with { Id = id }; }
    }

    public ActionResultResponse Start(uint id, int seconds, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new ActionResultResponse { Id = id, Success = false, Message = "A wait-state analysis is already running." };
        }
        if (TraceEventSession.IsElevated() != true)
        {
            Interlocked.Exchange(ref _running, 0);
            Set(Empty(LatencyScanState.Failed, "WaitLens needs full power: start PerfTuner as administrator (or install the always-on service).", seconds));
            return new ActionResultResponse { Id = id, Success = false, Message = "WaitLens needs full power (administrator)." };
        }
        Set(Empty(LatencyScanState.Running, $"Recording scheduler activity for {seconds} s… reproduce the slowness now.", seconds));
        _ = Task.Run(() => Run(seconds, ct), CancellationToken.None);
        return new ActionResultResponse { Id = id, Success = true, Message = $"Wait-state analysis started ({seconds} s)." };
    }

    private sealed class ThreadInfo
    {
        public int Pid;
        public double SwitchInMs = -1;
        public double OutMs = -1;
        public int OutState;
        public int OutReason;
        public double ReadyMs = -1;
    }

    private sealed class ProcAgg
    {
        public double Running, Ready, Io, Paging, Sync, Idle;
    }

    private void Run(int seconds, CancellationToken ct)
    {
        var threads = new Dictionary<int, ThreadInfo>(4096);
        var procs = new Dictionary<int, ProcAgg>(512);
        var gate = new Lock();

        ProcAgg Proc(int pid)
        {
            if (!procs.TryGetValue(pid, out var p))
            {
                if (procs.Count >= 4096) { return new ProcAgg(); }
                p = new ProcAgg();
                procs[pid] = p;
            }
            return p;
        }

        ThreadInfo? Thread(int tid, int pid)
        {
            if (!threads.TryGetValue(tid, out var t))
            {
                if (threads.Count >= 200_000) { return null; }
                t = new ThreadInfo { Pid = pid };
                threads[tid] = t;
            }
            if (pid > 0) { t.Pid = pid; }
            return t;
        }

        void AddBlocked(ThreadInfo t, double ms)
        {
            if (ms <= 0 || ms > 60_000) { return; }
            var p = Proc(t.Pid);
            switch (t.OutReason)
            {
                case 1 or 2 or 11 or 12: p.Paging += ms; break;          // FreePage, PageIn, VirtualMemory, PageOut
                case 0 or 3: p.Io += ms; break;                           // Executive, SystemAllocation (kernel/IO/driver)
                case 6 or 7 or 8 or 9 or 10: p.Sync += ms; break;         // UserRequest, EventPair*, Lpc*
                default: p.Idle += ms; break;                             // ExecutionDelay, Suspended, Unknown
            }
        }

        try
        {
            using (var session = new TraceEventSession(SessionName) { StopOnDispose = true, BufferSizeMB = 64 })
            {
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.Dispatcher);
                var kernel = session.Source.Kernel;

                kernel.DispatcherReadyThread += e =>
                {
                    lock (gate)
                    {
                        var now = e.TimeStampRelativeMSec;
                        var t = Thread(e.AwakenedThreadID, e.AwakenedProcessID);
                        if (t is null) { return; }
                        if (t.OutMs >= 0 && t.OutState == ThreadStateWait) { AddBlocked(t, now - t.OutMs); }
                        t.OutMs = -1;
                        t.ReadyMs = now;
                    }
                };

                kernel.ThreadCSwitch += e =>
                {
                    lock (gate)
                    {
                        var now = e.TimeStampRelativeMSec;
                        // Outgoing thread
                        var old = Thread(e.OldThreadID, e.OldProcessID);
                        if (old is not null && e.OldThreadID != 0)
                        {
                            if (old.SwitchInMs >= 0) { Proc(old.Pid).Running += now - old.SwitchInMs; }
                            old.SwitchInMs = -1;
                            old.OutMs = now;
                            old.OutState = (int)e.OldThreadState;
                            old.OutReason = (int)e.OldThreadWaitReason;
                            old.ReadyMs = old.OutState == ThreadStateReady ? now : -1; // preempted: ready immediately
                        }
                        // Incoming thread
                        var neu = Thread(e.NewThreadID, e.NewProcessID);
                        if (neu is not null && e.NewThreadID != 0)
                        {
                            if (neu.ReadyMs >= 0) { Proc(neu.Pid).Ready += now - neu.ReadyMs; }
                            neu.ReadyMs = -1;
                            neu.OutMs = -1;
                            neu.SwitchInMs = now;
                        }
                    }
                };

                var pump = new Thread(() => session.Source.Process()) { IsBackground = true, Name = "PerfTuner wait-lens ETW" };
                pump.Start();
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
                session.Stop();
                pump.Join(TimeSpan.FromSeconds(15));
            }

            var names = new ProcessNameCache();
            List<ProcessWaitProfile> rows;
            double totalRun, totalReady;
            lock (gate)
            {
                totalRun = procs.Where(p => p.Key > 0).Sum(p => p.Value.Running);
                totalReady = procs.Where(p => p.Key > 0).Sum(p => p.Value.Ready);
                rows = procs
                    .Where(p => p.Key > 4)
                    .OrderByDescending(p => p.Value.Running + p.Value.Ready + p.Value.Io + p.Value.Paging + p.Value.Sync)
                    .Take(25)
                    .Select(p => new ProcessWaitProfile
                    {
                        Pid = p.Key, Name = names.GetName(p.Key),
                        RunningMs = Math.Round(p.Value.Running, 1), ReadyMs = Math.Round(p.Value.Ready, 1),
                        BlockedIoMs = Math.Round(p.Value.Io, 1), BlockedPagingMs = Math.Round(p.Value.Paging, 1),
                        BlockedSyncMs = Math.Round(p.Value.Sync, 1), IdleWaitMs = Math.Round(p.Value.Idle, 1),
                        Diagnosis = Diagnose(p.Value),
                    })
                    .ToList();
            }

            var readyPct = totalRun + totalReady > 0 ? 100 * totalReady / (totalRun + totalReady) : 0;
            var summary = readyPct switch
            {
                >= 25 => $"CPU contention: threads spent {readyPct:F0}% of their wanted CPU time waiting for a free core. CPU % alone hides this — lower background priorities or use Focus mode.",
                >= 10 => $"Moderate contention: {readyPct:F0}% of wanted CPU time was spent waiting for a core.",
                _ => $"No CPU contention ({readyPct:F0}% ready time). If something felt slow, look at the blocked columns: IO, paging or lock waits.",
            };
            Set(new WaitAnalysisResponse
            {
                Id = 0, State = LatencyScanState.Completed, Seconds = seconds, SystemReadyPercent = Math.Round(readyPct, 1),
                Message = $"Analysed {seconds} s of scheduler activity across {procs.Count} processes.", Summary = summary, Processes = [.. rows],
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wait-state analysis failed");
            Set(Empty(LatencyScanState.Failed, $"Analysis failed: {ex.GetType().Name}", seconds));
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static string Diagnose(ProcAgg p)
    {
        var wanted = p.Running + p.Ready;
        if (wanted > 50 && p.Ready / wanted >= 0.3)
        {
            return $"CPU-starved: waited for a core {100 * p.Ready / wanted:F0}% of the time it wanted to run";
        }
        var blocked = new[] { ("disk/driver IO", p.Io), ("paging (memory read from disk)", p.Paging), ("locks / other threads", p.Sync) }.MaxBy(x => x.Item2);
        if (blocked.Item2 > p.Running && blocked.Item2 > 100)
        {
            return $"Mostly blocked on {blocked.Item1} ({blocked.Item2:F0} ms)";
        }
        return p.Running > 100 ? "CPU-bound (running most of the time it is active)" : "Mostly idle";
    }

    private void Set(WaitAnalysisResponse r)
    {
        lock (_gate) { _last = r; }
    }

    private static WaitAnalysisResponse Empty(LatencyScanState state, string message, int seconds) => new()
    {
        Id = 0, State = state, Message = message, Seconds = seconds, SystemReadyPercent = 0, Summary = "", Processes = [],
    };
}
