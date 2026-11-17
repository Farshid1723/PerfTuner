using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using PerfTuner.Contracts;
using PerfTuner.Core.Buffers;
using PerfTuner.Core.Telemetry;

BenchmarkSwitcher.FromAssembly(typeof(HotPathBenchmarks).Assembly).Run(args);

/// <summary>
/// Budget evidence for the per-event and per-interval paths. Acceptance criteria
/// (docs/PERFORMANCE.md): IoAccumulator.Add ≤ 50 ns and 0 B allocated; ring Add 0 B;
/// push encode ≤ 20 µs; request decode ≤ 20 µs.
/// </summary>
[MemoryDiagnoser]
public class HotPathBenchmarks
{
    private readonly IoAccumulator _accumulator = new(1024);
    private readonly RingBuffer<MetricSample> _ring = new(3600);
    private readonly ProcessIoCounters[] _top = new ProcessIoCounters[5];
    private readonly ArrayBufferWriter<byte> _writer = new(4096);
    private readonly byte[] _request = Encoding.UTF8.GetBytes("""{"type":"getHistory","id":42,"maxPoints":120}""");
    private MetricsPush _push = null!;
    private int _pid;

    [GlobalSetup]
    public void Setup()
    {
        _push = new MetricsPush
        {
            Id = 0,
            Sample = new MetricSample { CpuPercent = 12.3, MemoryLoadPercent = 45, DiskReadBytesPerSec = 1_234_567, Flags = SampleFlags.EtwIo },
            TopIo = [new ProcessIo { Pid = 1234, Name = "chrome", DiskBytesPerSec = 1000, NetBytesPerSec = 20000 }],
        };
    }

    /// <summary>The ETW callback path: executed for every disk/network event.</summary>
    [Benchmark(OperationsPerInvoke = 1000)]
    public void EtwEvent_IntoAccumulator()
    {
        for (var i = 0; i < 1000; i++)
        {
            _pid = (_pid + 4) & 1023;
            _accumulator.AddDiskRead(_pid + 4, 4096);
        }
    }

    [Benchmark]
    public IoTotals Interval_SnapshotAndTopN() => _accumulator.SnapshotAndReset(_top, out _);

    [Benchmark]
    public void Interval_RingAdd() => _ring.Add(_push.Sample);

    [Benchmark]
    public void Interval_EncodePushFrame() => MessageCodec.WriteResponseFrame(_writer, _push);

    [Benchmark]
    public bool Request_DecodeAndValidate() => MessageCodec.TryDecodeRequest(_request, out _, out _, out _);
}
