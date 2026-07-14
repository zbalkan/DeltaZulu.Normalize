using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>
/// Helpers for parsing a single JSON object or array out of a larger text buffer,
/// mirroring how the C library uses json_tokener_parse_ex (which reports
/// how many characters it consumed and tolerates trailing data).
/// </summary>
internal static class JsonText
{
    /// <summary>
    /// Writer options producing output byte-identical to
    /// <see cref="ToCompactString"/> (same escaping, no added whitespace),
    /// for serializing straight from a <see cref="FieldCollector"/>.
    /// </summary>
    public static readonly JsonWriterOptions CompactWriterOptions = new() {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true,
    };

    public static readonly JsonSerializerOptions SerializerOptions = new() {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    /// <summary>
    /// Read an integer the way json-c's json_object_get_int64 does: numbers
    /// convert directly, but a JSON *string* is also accepted and parsed as
    /// a number (rulebases sometimes quote numeric parameters, e.g.
    /// "priority":"1000"). Unparseable or missing values yield 0, matching
    /// json-c's lenient "return 0 on failure" behavior rather than throwing.
    /// </summary>
    public static long GetLenientInt64(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return 0;
        }

        if (v.TryGetValue(out long l))
        {
            return l;
        }

        if (v.TryGetValue(out double d))
        {
            return (long)d;
        }

        if (v.TryGetValue(out string? s) && long.TryParse(s, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    /// <summary>
    /// Read a string the way json-c's json_object_get_string does: a JSON
    /// string passes through unchanged, but any other scalar (number, bool)
    /// is coerced to its JSON text form instead of throwing. Returns null
    /// only for a non-scalar node (object/array) or a missing value.
    /// </summary>
    public static string? GetLenientString(JsonNode? node)
        => node is JsonValue v ? (v.TryGetValue(out string? s) ? s : v.ToJsonString()) : null;

    /// <summary>Compact serialization (no added whitespace), like json-c's PLAIN mode.</summary>
    public static string ToCompactString(JsonNode? node)
        => node?.ToJsonString(SerializerOptions) ?? "null";

    /// <summary>
    /// Try to parse one JSON object or array starting at <paramref name="offs"/>.
    /// On success returns the parsed node and the number of chars consumed,
    /// including any trailing whitespace (json-c treats whitespace after the
    /// value as part of it).
    /// </summary>
    public static bool TryParseValue(string text, int offs, out JsonNode? node, out int charsConsumed)
    {
        node = null;
        charsConsumed = 0;
        if (offs >= text.Length)
        {
            return false;
        }

        if (!TryFindObjectOrArrayEnd(text, offs, out var endExclusive))
        {
            return false;
        }

        /* parse the char span directly (the serializer transcodes via pooled
         * buffers), instead of allocating a UTF-8 copy of the slice */
        try
        {
            node = JsonSerializer.Deserialize<JsonNode?>(text.AsSpan(offs, endExclusive - offs));
        }
        catch (JsonException)
        {
            if (!TryNormalizeLenientJson(text.AsSpan(offs, endExclusive - offs), out var normalized))
            {
                return false;
            }

            try
            {
                node = JsonSerializer.Deserialize<JsonNode?>(normalized);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        charsConsumed = endExclusive - offs;

        /* json-c consumes whitespace following the value; emulate that */
        while (offs + charsConsumed < text.Length && TextRules.IsSpace(text[offs + charsConsumed]))
        {
            ++charsConsumed;
        }

        return true;
    }


    /// <summary>
    /// Apply the small json-c leniencies seen in upstream rulebases but not
    /// accepted by System.Text.Json: Python-style True/False literals and a
    /// trailing comma before an object/array close. The transform is lexical
    /// and deliberately leaves string contents untouched.
    /// </summary>
    private static bool TryNormalizeLenientJson(ReadOnlySpan<char> input, out string normalized)
    {
        var sb = new System.Text.StringBuilder(input.Length);
        var inString = false;
        var escaped = false;
        var changed = false;

        for (var i = 0; i < input.Length; ++i)
        {
            var c = input[i];

            if (inString)
            {
                sb.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
            }
            else if (c == ',' && IsTrailingComma(input, i + 1))
            {
                changed = true;
            }
            else if (input[i..].StartsWith("True".AsSpan()) && IsJsonTokenBoundary(input, i - 1) && IsJsonTokenBoundary(input, i + 4))
            {
                sb.Append("true");
                i += 3;
                changed = true;
            }
            else if (input[i..].StartsWith("False".AsSpan()) && IsJsonTokenBoundary(input, i - 1) && IsJsonTokenBoundary(input, i + 5))
            {
                sb.Append("false");
                i += 4;
                changed = true;
            }
            else
            {
                sb.Append(c);
            }
        }

        normalized = changed ? sb.ToString() : string.Empty;
        return changed;
    }

    private static bool IsTrailingComma(ReadOnlySpan<char> input, int index)
    {
        while (index < input.Length && TextRules.IsSpace(input[index]))
        {
            ++index;
        }

        return index < input.Length && (input[index] == '}' || input[index] == ']');
    }

    private static bool IsJsonTokenBoundary(ReadOnlySpan<char> input, int index)
        => index < 0 || index >= input.Length || TextRules.IsSpace(input[index])
           || input[index] is ':' or ',' or '[' or ']' or '{' or '}';

    /// <summary>
    /// Locate the syntactic end of the leading JSON object/array so callers
    /// can parse just that value while allowing arbitrary non-JSON bytes after
    /// it. This mirrors json_tokener_parse_ex, which reports the first value's
    /// char offset instead of requiring the buffer to contain only JSON.
    /// </summary>
    private static bool TryFindObjectOrArrayEnd(string text, int offs, out int endExclusive)
    {
        endExclusive = 0;
        if (offs >= text.Length || (text[offs] != '{' && text[offs] != '['))
        {
            return false;
        }

        /* depth counting only; a mismatched closer ("{]") yields a candidate
         * extent that the strict JSON parse then rejects, same net result as
         * tracking the exact closer kinds — without allocating a stack */
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = offs; i < text.Length; ++i)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;

                case '{':
                case '[':
                    depth++;
                    break;

                case '}':
                case ']':
                    if (--depth <= 0)
                    {
                        endExclusive = i + 1;
                        return depth == 0;
                    }
                    break;
            }
        }

        return false;
    }
}
