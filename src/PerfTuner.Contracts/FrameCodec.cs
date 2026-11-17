using System.Buffers.Binary;

namespace PerfTuner.Contracts;

/// <summary>Raised when the peer violates framing rules. The connection must be dropped.</summary>
public sealed class ProtocolViolationException(string message) : Exception(message);

/// <summary>
/// Length-prefixed framing: <c>[uint32 little-endian payload length][UTF-8 JSON payload]</c>.
/// Reads go into a caller-owned, preallocated buffer so the steady-state read path
/// allocates nothing.
/// </summary>
public static class FrameCodec
{
    /// <summary>
    /// Reads one frame into <paramref name="buffer"/>. Returns the payload length, or
    /// -1 on a clean end-of-stream before any header byte. Throws
    /// <see cref="ProtocolViolationException"/> on oversize, zero-length or truncated frames.
    /// </summary>
    public static async ValueTask<int> ReadFrameAsync(Stream stream, byte[] buffer, int maxPayloadBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length < Math.Max(ProtocolConstants.FrameHeaderBytes, maxPayloadBytes))
        {
            throw new ArgumentException("buffer smaller than max frame", nameof(buffer));
        }

        var headerRead = await ReadExactlyOrEofAsync(stream, buffer.AsMemory(0, ProtocolConstants.FrameHeaderBytes), ct).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return -1;
        }
        if (headerRead < ProtocolConstants.FrameHeaderBytes)
        {
            throw new ProtocolViolationException("truncated frame header");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (length == 0 || length > (uint)maxPayloadBytes)
        {
            throw new ProtocolViolationException($"frame length {length} outside 1..{maxPayloadBytes}");
        }

        var payloadRead = await ReadExactlyOrEofAsync(stream, buffer.AsMemory(0, (int)length), ct).ConfigureAwait(false);
        if (payloadRead != (int)length)
        {
            throw new ProtocolViolationException("truncated frame payload");
        }
        return (int)length;
    }

    /// <summary>Writes the 4-byte header for a payload of <paramref name="payloadLength"/> bytes.</summary>
    public static void WriteHeader(Span<byte> destination, int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)payloadLength);
    }

    private static async ValueTask<int> ReadExactlyOrEofAsync(Stream stream, Memory<byte> destination, CancellationToken ct)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var n = await stream.ReadAsync(destination[total..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }
}
