using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>Discriminator for <see cref="FieldValue"/>.</summary>
internal enum FieldValueKind : byte
{
    /// <summary>A committed JSON null (also the default value's kind).</summary>
    Null = 0,

    /// <summary>A slice of the input string; materialized only on demand.</summary>
    Span = 1,

    /// <summary>An already-built <see cref="JsonNode"/> subtree.</summary>
    Node = 2,

    /// <summary>A nested <see cref="FieldCollector"/> (custom-type sub-walk).</summary>
    Object = 3,
}

/// <summary>
/// The value half of a flat result entry: either a zero-copy slice of the
/// input message, a <see cref="JsonNode"/> subtree (eager/structured parsers),
/// a nested collector (custom types), or JSON null. The explicit kind byte
/// distinguishes the default value from a zero-length span and from a
/// committed null.
/// </summary>
internal readonly struct FieldValue
{
    public readonly FieldValueKind Kind;

    /* string (span source) | JsonNode | FieldCollector, per Kind */
    private readonly int _len;
    private readonly int _offs;
    private readonly object? _ref;

    private FieldValue(FieldValueKind kind, object? reference, int offs, int len)
    {
        Kind = kind;
        _ref = reference;
        _offs = offs;
        _len = len;
    }

    /// <summary>The nested collector; only valid when <see cref="Kind"/> is Object.</summary>
    public FieldCollector Collector => (FieldCollector)_ref!;

    /// <summary>The input slice; only valid when <see cref="Kind"/> is Span.</summary>
    public ReadOnlyMemory<char> Memory => ((string)_ref!).AsMemory(_offs, _len);

    /// <summary>The node; only valid when <see cref="Kind"/> is Node.</summary>
    public JsonNode NodeRef => (JsonNode)_ref!;

    public static FieldValue Node(JsonNode? node)
        => node == null ? default : new(FieldValueKind.Node, node, 0, 0);

    public static FieldValue Object(FieldCollector collector)
        => new(FieldValueKind.Object, collector, 0, 0);

    public static FieldValue Span(string source, int offs, int len)
                            => new(FieldValueKind.Span, source, offs, len);

    /// <summary>
    /// Materialize as a <see cref="JsonNode"/>. Span values allocate their
    /// string here; Node values are detached from any previous parent so the
    /// caller can attach the result.
    /// </summary>
    public JsonNode? ToJsonNode() => Kind switch {
        FieldValueKind.Span => JsonValue.Create(Memory.ToString()),
        FieldValueKind.Node => Normalizer.Detach(NodeRef),
        FieldValueKind.Object => Collector.ToJsonObject(),
        _ => null,
    };

    /// <summary>
    /// Serialize directly, writing Span values from the input slice without
    /// allocating a string.
    /// </summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        switch (Kind)
        {
            case FieldValueKind.Span:
                writer.WriteStringValue(((string)_ref!).AsSpan(_offs, _len));
                break;

            case FieldValueKind.Node:
                NodeRef.WriteTo(writer);
                break;

            case FieldValueKind.Object:
                Collector.WriteTo(writer);
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }
}
