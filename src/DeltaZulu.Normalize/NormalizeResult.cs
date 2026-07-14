using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>
/// <para>
/// The flat result of a normalization: an ordered list of extracted fields
/// whose string values are zero-copy slices of the input message. JSON
/// representations are produced on demand — <see cref="WriteTo"/> and
/// <see cref="ToJsonString"/> serialize the slices directly without
/// allocating per-field strings, and <see cref="ToJsonObject"/> materializes
/// a <see cref="JsonObject"/> once and caches it.
/// </para>
/// <para>
/// A live instance keeps the input message string reachable (field values
/// reference slices of it). Lazy materialization is not thread-safe; use one
/// instance from one thread at a time, like any <see cref="JsonNode"/>.
/// </para>
/// </summary>
public sealed class NormalizeResult
{
    private readonly FieldCollector _fields;
    private JsonObject? _materialized;

    internal NormalizeResult(int status, FieldCollector fields)
    {
        Status = status;
        _fields = fields;
    }

    /// <summary>The normalization status code: 0 on a match, non-zero otherwise (C-compatible).</summary>
    public int Status { get; }

    /// <summary>Whether the message matched a rule. On failure the fields
    /// hold "originalmsg" and "unparsed-data", exactly like the C library.</summary>
    public bool Matched => Status == 0;

    /// <summary>Number of extracted fields.</summary>
    public int Count => _fields.Count;

    /// <summary>Name of the field at <paramref name="index"/> (in commit order).</summary>
    public string GetName(int index) => _fields.NameAt(index);

    public bool Contains(string name) => _fields.Contains(name);

    /// <summary>
    /// Get a field's raw text without allocating, when the value is a plain
    /// slice of the input message. Returns false for absent fields and for
    /// structured/converted values (numbers, sub-objects, ...).
    /// </summary>
    public bool TryGetRawText(string name, out ReadOnlyMemory<char> text)
    {
        var i = _fields.IndexOf(name);
        if (i >= 0 && _fields.ValueAt(i) is { Kind: FieldValueKind.Span } value)
        {
            text = value.Memory;
            return true;
        }

        text = default;
        return false;
    }

    /// <summary>Materialize a single field as a <see cref="JsonNode"/>; null if absent (or a JSON null).</summary>
    public JsonNode? GetValue(string name)
    {
        if (_materialized != null)
        {
            return _materialized[name];
        }

        var i = _fields.IndexOf(name);
        return i >= 0 ? _fields.ValueAt(i).ToJsonNode() : null;
    }

    /// <summary>
    /// Materialize the whole result as a <see cref="JsonObject"/>. Cached:
    /// repeated calls return the same instance.
    /// </summary>
    public JsonObject ToJsonObject() => _materialized ??= _fields.ToJsonObject();

    /// <summary>Serialize the result, writing slice-backed values straight from the input message.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        if (_materialized is not null)
        {
            _materialized.WriteTo(writer);
            return;
        }

        _fields.WriteTo(writer);
    }

    /// <summary>Compact JSON text (same format as <see cref="LogNormContext.NormalizeToString"/>).</summary>
    public string ToJsonString()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, JsonText.CompactWriterOptions))
        {
            WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
