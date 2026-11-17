using System.Text.Json.Serialization;

namespace PerfTuner.Contracts;

/// <summary>
/// Source-generated serializer metadata: no runtime reflection, trim/AOT safe and
/// faster than reflection-based serialization. <c>UnmappedMemberHandling.Disallow</c>
/// makes unknown properties a hard error (strict schema).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    MaxDepth = ProtocolConstants.MaxJsonDepth)]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
