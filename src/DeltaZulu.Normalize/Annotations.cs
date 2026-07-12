using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>
/// Annotations (port of annot.c): "annotate=tag:+field=&quot;value&quot;"
/// rulebase lines attach constant fields to every event that carries the
/// given tag.
/// </summary>
internal sealed class AnnotationSet
{
    private sealed class Annotation
    {
        public required string Tag { get; init; }
        /* ops are kept in file order; the C code prepends to a linked list
         * and then iterates it, so it applies ops in reverse file order —
         * we replicate that at Annotate() time. */
        public List<(string Name, string Value)> Ops { get; } = new();
    }

    private readonly List<Annotation> _annotations = new();

    public bool IsEmpty => _annotations.Count == 0;

    private Annotation? Find(string tag)
        => _annotations.FirstOrDefault(a => a.Tag == tag);

    /// <summary>Add one "+name=value" operation for a tag.</summary>
    public void AddOp(string tag, string name, string value)
    {
        var annot = Find(tag);
        if (annot == null)
        {
            annot = new Annotation { Tag = tag };
            _annotations.Add(annot);
        }
        annot.Ops.Add((name, value));
    }

    /// <summary>Annotate an event according to its tag bucket (port of ln_annotate).</summary>
    public void Annotate(JsonObject json, JsonArray tagBucket)
    {
        if (IsEmpty)
        {
            return;
        }

        /* tags are processed last-to-first, and within a tag the ops are
         * applied in reverse file order (see note above) — this reproduces
         * the field ordering of the C library */
        for (var i = tagBucket.Count - 1; i >= 0; --i)
        {
            if (tagBucket[i] is not JsonValue tagVal)
            {
                continue;
            }

            var annot = Find(tagVal.GetValue<string>());
            if (annot == null)
            {
                continue;
            }

            for (var j = annot.Ops.Count - 1; j >= 0; --j)
            {
                (var name, var value) = annot.Ops[j];
                json[name] = value;
            }
        }
    }
}