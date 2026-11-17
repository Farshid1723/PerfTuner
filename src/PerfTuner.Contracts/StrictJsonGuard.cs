using System.Text.Json;

namespace PerfTuner.Contracts;

/// <summary>
/// Structural pre-check run on every inbound payload BEFORE deserialization.
/// System.Text.Json (in .NET 9) silently accepts duplicate property names with
/// "last wins" semantics, which enables schema-confusion attacks
/// (e.g. <c>{"type":"ping","type":"setMonitoringOptions"}</c>). This guard rejects:
/// <list type="bullet">
///   <item>invalid UTF-8 / invalid JSON, comments and trailing commas</item>
///   <item>nesting deeper than <see cref="ProtocolConstants.MaxJsonDepth"/></item>
///   <item>duplicate property names within the same object</item>
///   <item>a root that is not an object</item>
/// </list>
/// Payloads are ≤ 4 KiB and rate-limited, so the small per-call allocations are irrelevant.
/// </summary>
public static class StrictJsonGuard
{
    public static bool IsAcceptable(ReadOnlySpan<byte> utf8Json, out string error)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = ProtocolConstants.MaxJsonDepth,
        });

        // One name-set per open object; arrays push null.
        var scopes = new Stack<HashSet<string>?>();
        try
        {
            var first = true;
            while (reader.Read())
            {
                if (first)
                {
                    first = false;
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        error = "root must be a JSON object";
                        return false;
                    }
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        scopes.Push(null);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var names = scopes.Peek();
                        if (names is not null && !names.Add(reader.GetString()!))
                        {
                            error = "duplicate property name";
                            return false;
                        }
                        break;
                }
            }

            if (first)
            {
                error = "empty payload";
                return false;
            }
        }
        catch (JsonException)
        {
            error = "invalid JSON";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
