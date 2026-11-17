using System.Buffers;
using System.Text.Json;

namespace PerfTuner.Contracts;

/// <summary>
/// Single entry point for turning bytes into validated messages and back.
/// Decode pipeline for requests: structural guard → strict source-generated
/// deserialization → semantic validation. Any failure yields an error string and
/// no message object.
/// </summary>
public static class MessageCodec
{
    public static bool TryDecodeRequest(ReadOnlySpan<byte> payload, out Request? request, out string errorCode, out string error)
    {
        request = null;
        if (!StrictJsonGuard.IsAcceptable(payload, out error))
        {
            errorCode = ErrorCodes.Malformed;
            return false;
        }

        try
        {
            request = JsonSerializer.Deserialize(payload, ProtocolJsonContext.Default.Request);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Unknown "type", unknown property, missing required property, wrong JSON type.
            // The exception text is not echoed to the peer (it may include internals).
            errorCode = ErrorCodes.Schema;
            error = "message does not match schema";
            return false;
        }

        if (request is null)
        {
            errorCode = ErrorCodes.Schema;
            error = "null message";
            return false;
        }

        if (!RequestValidator.TryValidate(request, out error))
        {
            request = null;
            errorCode = ErrorCodes.Schema;
            return false;
        }

        errorCode = string.Empty;
        return true;
    }

    public static bool TryDecodeResponse(ReadOnlySpan<byte> payload, out Response? response, out string error)
    {
        response = null;
        if (!StrictJsonGuard.IsAcceptable(payload, out error))
        {
            return false;
        }
        try
        {
            response = JsonSerializer.Deserialize(payload, ProtocolJsonContext.Default.Response);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            error = "message does not match schema";
            return false;
        }
        if (response is null)
        {
            error = "null message";
            return false;
        }
        error = string.Empty;
        return true;
    }

    /// <summary>Serializes a response as a complete frame (header + payload) into a new array.
    /// Used for broadcast pushes: encoded once, shared read-only by all subscribers.</summary>
    public static byte[] EncodeResponseFrame(Response response) => EncodeFrame(response, ProtocolJsonContext.Default.Response, ProtocolConstants.MaxResponseFrameBytes);

    public static byte[] EncodeRequestFrame(Request request) => EncodeFrame(request, ProtocolJsonContext.Default.Request, ProtocolConstants.MaxRequestFrameBytes);

    /// <summary>Serializes a response frame into a reusable writer (per-connection, no per-call arrays).</summary>
    public static void WriteResponseFrame(ArrayBufferWriter<byte> writer, Response response)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.ResetWrittenCount();
        var header = writer.GetSpan(ProtocolConstants.FrameHeaderBytes);
        header[..ProtocolConstants.FrameHeaderBytes].Clear();
        writer.Advance(ProtocolConstants.FrameHeaderBytes);

        using (var json = new Utf8JsonWriter(writer))
        {
            JsonSerializer.Serialize(json, response, ProtocolJsonContext.Default.Response);
        }

        var payloadLength = writer.WrittenCount - ProtocolConstants.FrameHeaderBytes;
        EnsureWithinLimit(payloadLength, ProtocolConstants.MaxResponseFrameBytes);
        // ArrayBufferWriter exposes its written memory; patch the header in place.
        var written = System.Runtime.InteropServices.MemoryMarshal.AsMemory(writer.WrittenMemory);
        FrameCodec.WriteHeader(written.Span, payloadLength);
    }

    private static byte[] EncodeFrame<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, int limit)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        EnsureWithinLimit(payload.Length, limit);
        var frame = new byte[ProtocolConstants.FrameHeaderBytes + payload.Length];
        FrameCodec.WriteHeader(frame, payload.Length);
        payload.CopyTo(frame, ProtocolConstants.FrameHeaderBytes);
        return frame;
    }

    private static void EnsureWithinLimit(int payloadLength, int limit)
    {
        if (payloadLength > limit)
        {
            throw new ProtocolViolationException($"outbound frame {payloadLength} bytes exceeds limit {limit}");
        }
    }
}
