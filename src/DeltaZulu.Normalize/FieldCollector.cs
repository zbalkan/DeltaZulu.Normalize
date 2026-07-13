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

    private Entry[] _entries = new Entry[4];
    private int _count;

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
            Array.Resize(ref _entries, _count * 2);
        }

        _entries[_count].Name = name;
        _entries[_count].Value = value;
        _count++;
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
