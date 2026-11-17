using System.Text;
using PerfTuner.Contracts;

namespace PerfTuner.Core.Tests;

public class ProtocolTests
{
    private static bool Decode(string json, out Request? req, out string code)
        => MessageCodec.TryDecodeRequest(Encoding.UTF8.GetBytes(json), out req, out code, out _);

    [Fact]
    public void Valid_hello_decodes()
    {
        Assert.True(Decode("""{"type":"hello","id":1,"protocolVersion":1,"clientName":"PerfTuner GUI"}""", out var req, out _));
        var hello = Assert.IsType<HelloRequest>(req);
        Assert.Equal("PerfTuner GUI", hello.ClientName);
    }

    [Theory]
    [InlineData("""{"type":"format_c","id":1}""")]                                         // unknown type
    [InlineData("""{"id":1}""")]                                                           // missing discriminator
    [InlineData("""{"type":"ping","id":1,"extra":true}""")]                                // unknown property
    [InlineData("""{"type":"getHistory","id":1}""")]                                       // missing required
    [InlineData("""{"type":"getHistory","id":1,"maxPoints":"10"}""")]                      // wrong JSON type
    [InlineData("""{"type":"hello","id":1,"protocolVersion":1}""")]                        // missing clientName
    public void Schema_violations_are_rejected(string json)
    {
        Assert.False(Decode(json, out var req, out var code));
        Assert.Null(req);
        Assert.Equal(ErrorCodes.Schema, code);
    }

    [Theory]
    [InlineData("""{"type":"ping","type":"setMonitoringOptions","id":1}""")]               // duplicate discriminator
    [InlineData("""{"type":"ping","id":1,"id":2}""")]                                      // duplicate property
    [InlineData("""[{"type":"ping","id":1}]""")]                                           // non-object root
    [InlineData("""{"type":"ping","id":1,}""")]                                            // trailing comma
    [InlineData("""{"type":"ping",/*x*/"id":1}""")]                                        // comment
    [InlineData("""{"a":{"b":{"c":{"d":{"e":{"f":{"g":1}}}}}}}""")]                        // too deep
    [InlineData("not json")]
    public void Structural_violations_are_rejected(string json)
    {
        Assert.False(Decode(json, out _, out var code));
        Assert.Equal(ErrorCodes.Malformed, code);
    }

    [Theory]
    [InlineData("""{"type":"ping","id":0}""")]                                             // id zero
    [InlineData("""{"type":"getHistory","id":1,"maxPoints":0}""")]
    [InlineData("""{"type":"getHistory","id":1,"maxPoints":100000}""")]
    [InlineData("""{"type":"hello","id":1,"protocolVersion":1,"clientName":"a\nb"}""")]   // log injection
    [InlineData("""{"type":"hello","id":1,"protocolVersion":1,"clientName":""}""")]
    [InlineData("""{"type":"setMonitoringOptions","id":1,"aggregationIntervalMs":10}""")]
    [InlineData("""{"type":"setMonitoringOptions","id":1}""")]                             // no-op
    public void Bound_violations_are_rejected(string json)
    {
        Assert.False(Decode(json, out _, out var code));
        Assert.Equal(ErrorCodes.Schema, code);
    }

    [Fact]
    public void Response_round_trips_through_frame()
    {
        var push = new MetricsPush
        {
            Id = 0,
            Sample = new MetricSample { CpuPercent = 12.5, TimestampUnixMs = 42, Flags = SampleFlags.EtwIo },
            TopIo = [new ProcessIo { Pid = 4, Name = "System", DiskBytesPerSec = 10 }],
        };
        var frame = MessageCodec.EncodeResponseFrame(push);
        Assert.Equal(frame.Length - 4, BitConverter.ToInt32(frame, 0));

        Assert.True(MessageCodec.TryDecodeResponse(frame.AsSpan(4), out var decoded, out _));
        var back = Assert.IsType<MetricsPush>(decoded);
        Assert.Equal(12.5, back.Sample.CpuPercent);
        Assert.Equal("System", back.TopIo[0].Name);
    }

    [Fact]
    public void Reusable_writer_produces_identical_frame()
    {
        var ack = new AckResponse { Id = 7 };
        var writer = new System.Buffers.ArrayBufferWriter<byte>(256);
        MessageCodec.WriteResponseFrame(writer, ack);
        Assert.Equal(MessageCodec.EncodeResponseFrame(ack), writer.WrittenSpan.ToArray());
    }

    [Fact]
    public async Task Oversized_frame_is_a_protocol_violation()
    {
        var header = new byte[4];
        FrameCodec.WriteHeader(header, ProtocolConstants.MaxRequestFrameBytes + 1);
        using var stream = new MemoryStream(header);
        var buffer = new byte[ProtocolConstants.MaxRequestFrameBytes];
        await Assert.ThrowsAsync<ProtocolViolationException>(async () =>
            await FrameCodec.ReadFrameAsync(stream, buffer, ProtocolConstants.MaxRequestFrameBytes, CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_frame_is_a_protocol_violation()
    {
        var bytes = new byte[4 + 3];
        FrameCodec.WriteHeader(bytes, 10); // claims 10, provides 3
        using var stream = new MemoryStream(bytes);
        var buffer = new byte[ProtocolConstants.MaxRequestFrameBytes];
        await Assert.ThrowsAsync<ProtocolViolationException>(async () =>
            await FrameCodec.ReadFrameAsync(stream, buffer, ProtocolConstants.MaxRequestFrameBytes, CancellationToken.None));
    }

    [Fact]
    public async Task Clean_eof_returns_minus_one()
    {
        using var stream = new MemoryStream([]);
        var buffer = new byte[ProtocolConstants.MaxRequestFrameBytes];
        Assert.Equal(-1, await FrameCodec.ReadFrameAsync(stream, buffer, ProtocolConstants.MaxRequestFrameBytes, CancellationToken.None));
    }
}
