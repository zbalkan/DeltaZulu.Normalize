using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>
/// Flat, ordered (name, value) list the walker commits extracted fields into,
/// replacing the previous practice of building a <see cref="JsonObject"/>
/// during the walk. <see cref="Set"/> follows the JsonObject indexer contract
/// (replace in place, keeping the original position) so field ordering and
/// duplicate handling stay identical; the JSON representations are produced
/// on demand by <see cref="ToJsonObject"/> / <see cref="WriteTo"/>.
/// Lookups are ordinal linear scans: realistic events have few fields, where
/// a scan over a compact array beats a hash table.
/// </summary>
internal sealed class FieldCollector
{
    private struct Entry
    {
        public string Name;
        public FieldValue Value;
    }

    private const int InitialCapacity = 4;

    private Entry[] _entries;
    private int _count;
    private bool _pooled;

    public FieldCollector()
    {
        _entries = new Entry[InitialCapacity];
    }

    private FieldCollector(bool pooled)
    {
        _entries = ArrayPool<Entry>.Shared.Rent(InitialCapacity);
        _pooled = pooled;
    }

    /// <summary>
    /// Create a collector whose backing array is rented from the shared pool,
    /// for the classic <c>Normalize(out JsonObject)</c> path: the collector
    /// is discarded the instant it is materialized, so every call would
    /// otherwise pay a fixed array allocation regardless of whether the
    /// caller ever touches the flat API. The caller must call
    /// <see cref="ReturnScratch"/> once fully done reading it (after
    /// materializing, never before), and must not let the instance escape
    /// past that point.
    /// </summary>
    public static FieldCollector RentScratch() => new(pooled: true);

    /// <summary>Return the rented backing array. No-op if not pooled (already returned, or never rented).</summary>
    public void ReturnScratch()
    {
        if (!_pooled)
        {
            return;
        }

        Array.Clear(_entries, 0, _count);
        ArrayPool<Entry>.Shared.Return(_entries);
        _entries = [];
        _count = 0;
        _pooled = false;
    }

    public int Count => _count;

    public string NameAt(int index) => _entries[index].Name;

    public FieldValue ValueAt(int index) => _entries[index].Value;

    public int IndexOf(string name)
    {
        for (var i = 0; i < _count; ++i)
        {
            if (string.Equals(_entries[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    public bool Contains(string name) => IndexOf(name) >= 0;

    /// <summary>Replace in place if the name exists, else append.</summary>
    public void Set(string name, FieldValue value)
    {
        var i = IndexOf(name);
        if (i >= 0)
        {
            _entries[i].Value = value;
            return;
        }

        if (_count == _entries.Length)
        {
            Grow();
        }

        _entries[_count].Name = name;
        _entries[_count].Value = value;
        _count++;
    }

    /// <summary>
    /// Grow past capacity. A pooled array is returned here rather than
    /// resized in place (ArrayPool arrays can't be resized); past this
    /// point the collector holds a plain heap array like any other, so
    /// growing again needs no further pool bookkeeping.
    /// </summary>
    private void Grow()
    {
        var bigger = new Entry[_entries.Length * 2];
        Array.Copy(_entries, bigger, _count);
        if (_pooled)
        {
            Array.Clear(_entries, 0, _count);
            ArrayPool<Entry>.Shared.Return(_entries);
            _pooled = false;
        }

        _entries = bigger;
    }

    /// <summary>Materialize as a <see cref="JsonObject"/>, in entry order.</summary>
    public JsonObject ToJsonObject()
    {
        var obj = new JsonObject();
        for (var i = 0; i < _count; ++i)
        {
            obj[_entries[i].Name] = _entries[i].Value.ToJsonNode();
        }

        return obj;
    }

    /// <summary>Serialize as a JSON object without materializing nodes for Span entries.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        for (var i = 0; i < _count; ++i)
        {
            writer.WritePropertyName(_entries[i].Name);
            _entries[i].Value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
