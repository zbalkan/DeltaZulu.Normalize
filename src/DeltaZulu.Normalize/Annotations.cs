using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>
/// Annotations (port of annot.c): "annotate=tag:+field=&quot;value&quot;"
/// rulebase lines attach constant fields to every event that carries the
/// given tag.
/// </summary>
internal sealed class AnnotationSet
{
    private readonly List<Annotation> _annotations = new();

    public bool IsEmpty => _annotations.Count == 0;

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
    public void Annotate(FieldCollector fields, JsonArray tagBucket)
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
                fields.Set(name, FieldValue.Node(JsonValue.Create(value)));
            }
        }
    }

    private Annotation? Find(string tag)
        => _annotations.FirstOrDefault(a => a.Tag == tag);

    private sealed class Annotation
    {
        public List<(string Name, string Value)> Ops { get; } = new();
        public required string Tag { get; init; }
        /* ops are kept in file order; the C code prepends to a linked list
         * and then iterates it, so it applies ops in reverse file order —
         * we replicate that at Annotate() time.
         *
         * Known, currently unreachable divergence: this is a single flat
         * list across every "annotate=" line for this tag. C's combine
         * algorithm (ln_combineAnnot in annot.c) instead nests per-statement,
         * which only produces a different relative order than this flat
         * list+single-reversal approach when one "annotate=" line has
         * multiple "+field=value" ops *and* a later, separate "annotate="
         * line re-annotates the same tag with an op that sets the same
         * field name. No rulebase in either project's test suite does this;
         * left as a flat list for simplicity rather than chasing parity with
         * an unobserved edge case. */
    }
}
