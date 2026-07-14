using System.Text;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>
/// <para>
/// The runtime half of pdag.c: the recursive PDAG walker with backtracking,
/// operating on an immutable <see cref="CompiledPdag"/> snapshot.
/// </para>
/// <para>
/// At each node the outgoing edges are tried in priority order. When an edge
/// matches a prefix of the remaining input, the walker recurses into its
/// destination with the rest of the input; extracted values are only
/// committed to the result object if that recursive call ultimately reaches a
/// terminal node (so failed paths leave no partial extractions behind).
/// Parsing succeeds when a terminal node is reached at end-of-input (or
/// anywhere, for partial matches such as user-defined types).
/// </para>
/// </summary>
internal static class Normalizer
{
    private const string MetaKey = "metadata";
    private const string MetaRuleKey = "rule";

    /// <summary>No terminal node found (sentinel for the endNode index).</summary>
    private const int NoNode = -1;

    private const string OriginalMsgKey = "originalmsg";
    private const string RuleLocationKey = "location";
    private const string RuleMockupKey = "mockup";
    private const string UnparsedDataKey = "unparsed-data";

    /// <summary>Normalize a message against a snapshot (port of ln_normalize).</summary>
    public static int Normalize(LogNormContext ctx, CompiledPdag snap, string str, out FieldCollector fields)
    {
        fields = new FieldCollector();
        return NormalizeCore(ctx, snap, str, fields);
    }

    /// <summary>Normalize and materialize the result as a <see cref="JsonObject"/>.</summary>
    public static int Normalize(LogNormContext ctx, CompiledPdag snap, string str, out JsonObject result)
    {
        /* fields is discarded the instant it is materialized below, so a
         * rented array avoids paying a fixed allocation on every call for
         * callers who never touch the flat NormalizeResult API */
        var fields = FieldCollector.RentScratch();
        try
        {
            var r = NormalizeCore(ctx, snap, str, fields);
            result = fields.ToJsonObject();
            return r;
        }
        finally
        {
            fields.ReturnScratch();
        }
    }

    /// <summary>
    /// Recursive step of the normalizer (port of ln_normalizeRec).
    /// </summary>
    /// <param name="npb">normalization parameter block (message, state, snapshot)</param>
    /// <param name="nodeIdx">current PDAG node index</param>
    /// <param name="offs">position in the input where matching continues</param>
    /// <param name="bPartialMatch">succeed on a terminal node even before end-of-input</param>
    /// <param name="fields">result collector values are committed to (null discards them)</param>
    /// <param name="endNode">receives the terminal node's index when a match is found</param>
    /// <param name="failOnDuplicate">skip parsers whose field already exists (repeat option)</param>
    /// <param name="cur">collector checked for duplicates</param>
    /// <param name="parserName">name of the enclosing parser instance (repeat merge)</param>
    public static int NormalizeRec(Npb npb, int nodeIdx, int offs, bool bPartialMatch,
        FieldCollector? fields, ref int endNode, bool failOnDuplicate, FieldCollector? cur, string? parserName)
    {
        var snap = npb.Snap;
        var node = snap.Nodes[nodeIdx];
        var r = ErrorCodes.WrongParser;
#pragma warning disable IDE0059 // Unnecessary assignment of a value
        var parsedTo = npb.ParsedTo;
#pragma warning restore IDE0059 // Unnecessary assignment of a value
        var parsed = 0;
        FieldValue value = default;

        if (snap.StatsCalled is { } statsCalled)
        {
            Interlocked.Increment(ref statsCalled[nodeIdx]);
        }

        var edgeEnd = node.EdgeStart + node.EdgeCount;
        for (var iprs = node.EdgeStart; iprs < edgeEnd && r != 0; ++iprs)
        {
            ref readonly var prs = ref snap.Edges[iprs];

            /* a literal whose first char mismatches can be rejected without a
             * call; equivalent to a failed parse, which records no progress
             * (LongestParsedTo >= offs is already guaranteed by the caller) */
            if (prs.LiteralFirstChar != '\0'
                && prs.PrsId == ParserTable.LiteralId
                && npb.At(offs) != prs.LiteralFirstChar)
            {
                continue;
            }

            if (failOnDuplicate && CheckDuplicate(cur, prs.Name))
            {
                continue;
            }

            var i = offs;
            var attemptParsedTo = offs;
            var localR = TryParser(npb, ref i, ref parsed, ref value, out var valueMaterialized,
                in prs, failOnDuplicate, fields, prs.Name);
            if (localR == 0)
            {
                parsedTo = i + parsed;
                attemptParsedTo = parsedTo;
                /* potential hit, need to verify by walking the subtree */
                r = NormalizeRec(npb, prs.TargetNode, parsedTo, bPartialMatch, fields, ref endNode,
                    failOnDuplicate, cur, parserName);
                if (r == 0)
                {
                    if (!valueMaterialized && fields != null && prs.Name != null)
                    {
                        if (prs.Extract == ExtractMode.RawSpan)
                        {
                            /* zero-copy: the string is produced only if the
                             * result is materialized as JsonObject/JsonNode */
                            value = FieldValue.Span(npb.Str, offs, parsed);
                        }
                        else
                        {
                            MaterializeValue(npb, in prs, offs, ref value);
                        }
                    }
                    var rFix = CommitField(ref value, fields, in prs, failOnDuplicate);
                    if (rFix != 0)
                    {
                        return rFix;
                    }

                    if ((npb.Ctx.Options & LogNormOptions.AddRule) != 0)
                    {
                        AddRuleToMockup(npb, in prs);
                    }

                    if (parsedTo > npb.ParsedTo)
                    {
                        npb.ParsedTo = parsedTo;
                    }
                }
                else if (snap.StatsBacktracked is { } statsBacktracked)
                {
                    Interlocked.Increment(ref statsBacktracked[nodeIdx]);
                }
            }
            value = default; /* discard any uncommitted extraction */

            if (attemptParsedTo > npb.LongestParsedTo)
            {
                npb.LongestParsedTo = attemptParsedTo;
            }
        }

        /* A terminal node may also have outgoing continuation parsers when
         * multiple rules share a prefix. Continuations must be tried first;
         * the terminal is the fallback when no child path matched. Still
         * update ParsedTo here: custom-type parsers compute their consumed
         * length from this value after their nested NormalizeRec call. */
        if (r != 0 && node.IsTerminal && (offs == npb.StrLen || bPartialMatch))
        {
            endNode = nodeIdx;
            if (offs > npb.ParsedTo)
            {
                npb.ParsedTo = offs;
            }

            if (offs > npb.LongestParsedTo)
            {
                npb.LongestParsedTo = offs;
            }

            r = 0;
        }
        return r;
    }

    /// <summary>Detach a node from a previous parent so it can be re-added elsewhere.</summary>
    internal static JsonNode? Detach(JsonNode? node)
    {
        if (node?.Parent != null)
        {
            return node.DeepClone();
        }

        return node;
    }

    private static void AddRuleMetadata(Npb npb, FieldCollector fields, TerminalInfo endNode)
    {
        var ctx = npb.Ctx;
        JsonObject? metaRule = null;

        if ((ctx.Options & LogNormOptions.AddRule) != 0)
        {
            metaRule = new JsonObject();
            var sb = new StringBuilder();
            for (var i = npb.RuleSegments!.Count - 1; i >= 0; --i)
            {
                sb.Append(npb.RuleSegments[i]);
            }

            metaRule[RuleMockupKey] = sb.ToString();
        }

        if ((ctx.Options & LogNormOptions.AddRuleLocation) != 0)
        {
            metaRule ??= new JsonObject();
            metaRule[RuleLocationKey] = new JsonObject {
                ["file"] = endNode.RulebaseFile,
                ["line"] = endNode.RulebaseLineNumber,
            };
        }

        if (metaRule != null)
        {
            fields.Set(MetaKey, FieldValue.Node(new JsonObject { [MetaRuleKey] = metaRule }));
        }
    }

    /// <summary>Record the segment for the matching-rule mock-up (deepest-first, reversed at emit).</summary>
    private static void AddRuleToMockup(Npb npb, in CompiledEdge prs)
    {
        var segment = prs.PrsId == ParserTable.LiteralId
            ? Parsers.LiteralParser.DataForDisplay(prs.Data!)
            : $"%{prs.Name ?? "-"}:{ParserTable.IdToName(prs.PrsId)}%";
        npb.RuleSegments!.Add(segment);
    }

    private static void AddUnparsedField(string str, int offs, FieldCollector fields)
    {
        /* full-range and tail slices; a whole-string span materializes back
         * to the original string instance without a copy */
        var start = Math.Min(offs, str.Length);
        fields.Set(OriginalMsgKey, FieldValue.Span(str, 0, str.Length));
        fields.Set(UnparsedDataKey, FieldValue.Span(str, start, str.Length - start));
    }

    /// <summary>
    /// Skip a parser whose field already exists in the result. Only used by
    /// "repeat" with option.failOnDuplicate (port of checkDuplicate; the C
    /// call sites always pass a null value, so only the name check remains).
    /// </summary>
    private static bool CheckDuplicate(FieldCollector? fields, string? check)
        => fields != null && check != null && fields.Contains(check);

    /// <summary>
    /// Merge a successfully extracted value into the result collector,
    /// implementing the special field names (port of fixJSON):
    /// unnamed fields are dropped; "." splices an object's members into the
    /// parent; a single-member "..&quot; sub-object is unwrapped so the value
    /// appears directly under this parser's field name. On a duplicate under
    /// failOnDuplicate the splice aborts mid-way, leaving the keys already
    /// spliced committed — exactly like the C code.
    /// </summary>
    private static int CommitField(ref FieldValue value, FieldCollector? fields, in CompiledEdge prs, bool failOnDuplicate)
    {
        try
        {
            if (fields == null)
            {
                return 0;
            }

            if (prs.Name == null)
            {
                /* value matched but not to be persisted */
                return 0;
            }

            if (prs.Name == ".")
            {
                if (value.Kind == FieldValueKind.Object)
                {
                    var sub = value.Collector;
                    for (var i = 0; i < sub.Count; ++i)
                    {
                        if (failOnDuplicate && fields.Contains(sub.NameAt(i)))
                        {
                            return ErrorCodes.WrongParser;
                        }

                        fields.Set(sub.NameAt(i), sub.ValueAt(i));
                    }
                }
                else if (value.Kind == FieldValueKind.Node && value.NodeRef is JsonObject obj)
                {
                    foreach ((var key, var val) in obj.ToList())
                    {
                        if (failOnDuplicate && fields.Contains(key))
                        {
                            return ErrorCodes.WrongParser;
                        }

                        obj.Remove(key);
                        fields.Set(key, FieldValue.Node(val));
                    }
                }
                else
                {
                    fields.Set(prs.Name, value);
                }
                return 0;
            }

            /* check for a single "..": use its value under our own name */
            if (value.Kind == FieldValueKind.Object
                && value.Collector is { Count: 1 } subFields && subFields.NameAt(0) == "..")
            {
                if (failOnDuplicate && fields.Contains(prs.Name))
                {
                    return ErrorCodes.WrongParser;
                }

                fields.Set(prs.Name, subFields.ValueAt(0));
            }
            else if (value.Kind == FieldValueKind.Node
                && value.NodeRef is JsonObject { Count: 1 } subObj && subObj.ContainsKey(".."))
            {
                if (failOnDuplicate && fields.Contains(prs.Name))
                {
                    return ErrorCodes.WrongParser;
                }

                var dotdot = subObj[".."];
                subObj.Remove("..");
                fields.Set(prs.Name, FieldValue.Node(dotdot));
            }
            else
            {
                if (failOnDuplicate && fields.Contains(prs.Name))
                {
                    return ErrorCodes.WrongParser;
                }

                fields.Set(prs.Name, value);
            }
            return 0;
        }
        finally
        {
            value = default;
        }
    }

    /// <summary>
    /// Produce the value of an edge that matched during the measure phase, by
    /// re-running its parser at the recorded offset. Parsers are deterministic
    /// functions of (input, offset, config), so this reproduces the match
    /// exactly; it runs once, on the winning path only.
    /// </summary>
    private static void MaterializeValue(Npb npb, in CompiledEdge prs, int offs, ref FieldValue value)
    {
        var parsedToSave = npb.ParsedTo;
        if (prs.PrsId == ParserTable.CustomTypeId)
        {
            var sub = new FieldCollector();
            var endNode = NoNode;
            npb.ParsedTo = offs;
            var r = NormalizeRec(npb, npb.Snap.TypeRoots[prs.CustomTypeIdx], offs, bPartialMatch: true,
                sub, ref endNode, failOnDuplicate: false, cur: null, parserName: prs.Name);
            value = r == 0 ? FieldValue.Object(sub) : default;
        }
        else
        {
            var i = offs;
            JsonNode? node = null;
            ParserTable.Dispatch(prs.PrsId, npb, ref i, prs.Data, prs.Name,
                out _, wantValue: true, ref node);
            value = FieldValue.Node(node);
        }
        npb.ParsedTo = parsedToSave;
    }

    private static int NormalizeCore(LogNormContext ctx, CompiledPdag snap, string str, FieldCollector fields)
    {
        var npb = new Npb { Ctx = ctx, Snap = snap, Str = str };
        if ((ctx.Options & LogNormOptions.AddRule) != 0)
        {
            npb.RuleSegments = new List<string>();
        }

        var endNode = NoNode;
        var r = NormalizeRec(npb, snap.RootNode, 0, bPartialMatch: false, fields, ref endNode,
            failOnDuplicate: false, cur: null, parserName: null);

        if (r == 0 && snap.Nodes[endNode].IsTerminal)
        {
            /* success, finalize the event */
            var term = snap.Terminals[snap.Nodes[endNode].TerminalIdx];
            if (term.Tags != null)
            {
                /* Tags is shared with the builder graph and outlives this
                 * normalization, so it must be cloned eagerly */
                fields.Set("event.tags", FieldValue.Node(term.Tags.DeepClone()));
                ctx.Annotations.Annotate(fields, term.Tags);
            }
            if ((ctx.Options & LogNormOptions.AddOriginalMessage) != 0)
            {
                fields.Set(OriginalMsgKey, FieldValue.Span(str, 0, str.Length));
            }

            AddRuleMetadata(npb, fields, term);
        }
        else
        {
            AddUnparsedField(str, npb.LongestParsedTo, fields);
        }
        return r;
    }

    /// <summary>
    /// <para>
    /// Try a single edge at the given offset (port of tryParser). Custom
    /// types recursively parse against their own component, accepting a
    /// partial match of the remaining input.
    /// </para>
    /// <para>
    /// Match and extraction are split in two phases: this call normally only
    /// measures (wantValue: false) and the value is produced on the success
    /// unwind (see <see cref="ExtractMode"/>), so backtracked paths never
    /// allocate values. Edges compiled as Eager extract here, signalled via
    /// <paramref name="valueMaterialized"/>; a custom type under
    /// failOnDuplicate is also eager because its match consults
    /// <paramref name="curFields"/>, which deeper commits mutate before the
    /// unwind reaches this edge.
    /// </para>
    /// </summary>
    private static int TryParser(Npb npb, ref int offs, ref int parsed, ref FieldValue value,
        out bool valueMaterialized, in CompiledEdge prs, bool failOnDuplicate, FieldCollector? curFields,
        string? parserName)
    {
        int r;
        var parsedToSave = npb.ParsedTo;
        valueMaterialized = false;

        if (prs.PrsId == ParserTable.CustomTypeId)
        {
            if (prs.CustomTypeIdx < 0 || prs.CustomTypeIdx >= npb.Snap.TypeRoots.Length)
            {
                npb.ParsedTo = parsedToSave;
                return ErrorCodes.WrongParser;
            }
            var endNode = NoNode;
            npb.ParsedTo = offs;
            if (failOnDuplicate)
            {
                valueMaterialized = true;
                var sub = value.Kind == FieldValueKind.Object ? value.Collector : new FieldCollector();
                value = FieldValue.Object(sub);
                r = NormalizeRec(npb, npb.Snap.TypeRoots[prs.CustomTypeIdx], offs, bPartialMatch: true,
                    sub, ref endNode, failOnDuplicate: true, curFields, parserName);
                if (r != 0)
                {
                    value = default;
                }
            }
            else
            {
                /* measure only: values along the nested path are discarded
                 * (fields: null) and produced again on the success unwind */
                r = NormalizeRec(npb, npb.Snap.TypeRoots[prs.CustomTypeIdx], offs, bPartialMatch: true,
                    fields: null, ref endNode, failOnDuplicate: false, curFields, parserName);
            }
            parsed = npb.ParsedTo - offs;
        }
        else if (prs.Extract == ExtractMode.Eager)
        {
            valueMaterialized = true;
            JsonNode? node = null;
            r = ParserTable.Dispatch(prs.PrsId, npb, ref offs, prs.Data, parserName,
                out parsed, wantValue: prs.Name != null, ref node);
            value = FieldValue.Node(node);
        }
        else
        {
            JsonNode? node = null;
            r = ParserTable.Dispatch(prs.PrsId, npb, ref offs, prs.Data, parserName,
                out parsed, wantValue: false, ref node);
        }

        npb.ParsedTo = parsedToSave;
        return r;
    }
}
