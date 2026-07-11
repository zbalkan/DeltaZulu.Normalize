using System.Text;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>
/// The runtime half of pdag.c: the recursive PDAG walker with backtracking.
///
/// At each node the outgoing edges are tried in priority order. When an edge
/// matches a prefix of the remaining input, the walker recurses into its
/// destination with the rest of the input; extracted values are only
/// committed to the result object if that recursive call ultimately reaches a
/// terminal node (so failed paths leave no partial extractions behind).
/// Parsing succeeds when a terminal node is reached at end-of-input (or
/// anywhere, for partial matches such as user-defined types).
/// </summary>
internal static class Normalizer
{
    private const string MetaKey = "metadata";
    private const string OriginalMsgKey = "originalmsg";
    private const string UnparsedDataKey = "unparsed-data";
    private const string MetaRuleKey = "rule";
    private const string RuleMockupKey = "mockup";
    private const string RuleLocationKey = "location";

    /// <summary>
    /// Skip a parser whose field already exists in the result. Only used by
    /// "repeat" with option.failOnDuplicate (port of checkDuplicate; the C
    /// call sites always pass a null value, so only the name check remains).
    /// </summary>
    private static bool CheckDuplicate(JsonObject? json, string? check)
        => json != null && check != null && json.ContainsKey(check);

    /// <summary>
    /// Merge a successfully extracted value into the result object,
    /// implementing the special field names (port of fixJSON):
    /// unnamed fields are dropped; "." splices an object's members into the
    /// parent; a single-member "..&quot; sub-object is unwrapped so the value
    /// appears directly under this parser's field name.
    /// </summary>
    private static int FixJson(ref JsonNode? value, JsonObject? json, ParserInstance prs, bool failOnDuplicate)
    {
        try
        {
            if (json == null)
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
                if (value is JsonObject obj)
                {
                    foreach ((string key, JsonNode? val) in obj.ToList())
                    {
                        if (failOnDuplicate && json.ContainsKey(key))
                            return ErrorCodes.WrongParser;
                        obj.Remove(key);
                        json[key] = val;
                    }
                }
                else
                {
                    json[prs.Name] = Detach(value);
                }
                return 0;
            }

            /* check for a single "..": use its value under our own name */
            if (value is JsonObject sub && sub.Count == 1 && sub.ContainsKey(".."))
            {
                if (failOnDuplicate && json.ContainsKey(prs.Name))
                    return ErrorCodes.WrongParser;
                JsonNode? dotdot = sub[".."];
                sub.Remove("..");
                json[prs.Name] = dotdot;
            }
            else
            {
                if (failOnDuplicate && json.ContainsKey(prs.Name))
                    return ErrorCodes.WrongParser;
                json[prs.Name] = Detach(value);
            }
            return 0;
        }
        finally
        {
            value = null;
        }
    }

    /// <summary>Detach a node from a previous parent so it can be re-added elsewhere.</summary>
    internal static JsonNode? Detach(JsonNode? node)
    {
        if (node?.Parent != null)
            return node.DeepClone();
        return node;
    }

    /// <summary>
    /// Try a single edge at the given offset (port of tryParser). Custom
    /// types recursively parse against their own component, accepting a
    /// partial match of the remaining input.
    /// </summary>
    private static int TryParser(Npb npb, Pdag dag, ref int offs, ref int parsed, ref JsonNode? value,
        ParserInstance prs, bool failOnDuplicate, JsonObject? curJson, string? parserName)
    {
        int r;
        int parsedToSave = npb.ParsedTo;

        if (prs.PrsId == ParserTable.CustomTypeId)
        {
            if (prs.CustomTypeIndex < 0 || prs.CustomTypeIndex >= npb.Ctx.TypePdags.Count)
            {
                npb.ParsedTo = parsedToSave;
                return ErrorCodes.WrongParser;
            }
            value ??= new JsonObject();
            TypePdag custType = npb.Ctx.TypePdags[prs.CustomTypeIndex];
            Pdag? endNode = null;
            r = NormalizeRec(npb, custType.Dag, offs, bPartialMatch: true,
                (JsonObject)value, ref endNode, failOnDuplicate, curJson, parserName);
            parsed = npb.ParsedTo - offs;
            if (r != 0)
                value = null;
        }
        else
        {
            r = ParserTable.Parsers[prs.PrsId].Parse(npb, ref offs, prs.ParserData, parserName,
                out parsed, wantValue: prs.Name != null, ref value);
        }

        npb.ParsedTo = parsedToSave;
        return r;
    }

    /// <summary>Record the segment for the matching-rule mock-up (deepest-first, reversed at emit).</summary>
    private static void AddRuleToMockup(Npb npb, ParserInstance prs)
    {
        string segment = prs.PrsId == ParserTable.LiteralId
            ? Parsers.LiteralParser.DataForDisplay(prs.ParserData!)
            : $"%{prs.Name ?? "-"}:{ParserTable.IdToName(prs.PrsId)}%";
        npb.RuleSegments!.Add(segment);
    }

    /// <summary>
    /// Recursive step of the normalizer (port of ln_normalizeRec).
    /// </summary>
    /// <param name="npb">normalization parameter block</param>
    /// <param name="dag">current PDAG node</param>
    /// <param name="offs">position in the input where matching continues</param>
    /// <param name="bPartialMatch">succeed on a terminal node even before end-of-input</param>
    /// <param name="json">result object values are committed to (null discards them)</param>
    /// <param name="endNode">receives the terminal node when a match is found</param>
    /// <param name="failOnDuplicate">skip parsers whose field already exists (repeat option)</param>
    /// <param name="curJson">object checked for duplicates</param>
    /// <param name="parserName">name of the enclosing parser instance (repeat merge)</param>
    public static int NormalizeRec(Npb npb, Pdag dag, int offs, bool bPartialMatch,
        JsonObject? json, ref Pdag? endNode, bool failOnDuplicate, JsonObject? curJson, string? parserName)
    {
        int r = ErrorCodes.WrongParser;
        int parsedTo = npb.ParsedTo;
        int parsed = 0;
        JsonNode? value = null;

        dag.StatsCalled++;

        for (int iprs = 0; iprs < dag.Parsers.Count && r != 0; ++iprs)
        {
            ParserInstance prs = dag.Parsers[iprs];
            if (failOnDuplicate && CheckDuplicate(curJson, prs.Name))
                continue;

            int i = offs;
            int attemptParsedTo = offs;
            int localR = TryParser(npb, dag, ref i, ref parsed, ref value, prs, failOnDuplicate, json, prs.Name);
            if (localR == 0)
            {
                parsedTo = i + parsed;
                attemptParsedTo = parsedTo;
                /* potential hit, need to verify by walking the subtree */
                r = NormalizeRec(npb, prs.Node, parsedTo, bPartialMatch, json, ref endNode,
                    failOnDuplicate, curJson, parserName);
                if (r == 0)
                {
                    int rFix = FixJson(ref value, json, prs, failOnDuplicate);
                    if (rFix != 0)
                        return rFix;
                    if ((npb.Ctx.Options & LogNormOptions.AddRule) != 0)
                        AddRuleToMockup(npb, prs);
                    if (parsedTo > npb.ParsedTo)
                        npb.ParsedTo = parsedTo;
                }
                else
                {
                    dag.StatsBacktracked++;
                }
            }
            value = null; /* discard any uncommitted extraction */

            if (attemptParsedTo > npb.LongestParsedTo)
                npb.LongestParsedTo = attemptParsedTo;
        }

        /* A terminal node may also have outgoing continuation parsers when
         * multiple rules share a prefix. Continuations must be tried first;
         * the terminal is the fallback when no child path matched. Still
         * update ParsedTo here: custom-type parsers compute their consumed
         * length from this value after their nested NormalizeRec call. */
        if (r != 0 && dag.IsTerminal && (offs == npb.StrLen || bPartialMatch))
        {
            endNode = dag;
            if (offs > npb.ParsedTo)
                npb.ParsedTo = offs;
            if (offs > npb.LongestParsedTo)
                npb.LongestParsedTo = offs;
            r = 0;
        }
        return r;
    }

    private static void AddRuleMetadata(Npb npb, JsonObject json, Pdag endNode)
    {
        LogNormContext ctx = npb.Ctx;
        JsonObject? metaRule = null;

        if ((ctx.Options & LogNormOptions.AddRule) != 0)
        {
            metaRule = new JsonObject();
            var sb = new StringBuilder();
            for (int i = npb.RuleSegments!.Count - 1; i >= 0; --i)
                sb.Append(npb.RuleSegments[i]);
            metaRule[RuleMockupKey] = sb.ToString();
        }

        if ((ctx.Options & LogNormOptions.AddRuleLocation) != 0)
        {
            metaRule ??= new JsonObject();
            metaRule[RuleLocationKey] = new JsonObject
            {
                ["file"] = endNode.RulebaseFile,
                ["line"] = endNode.RulebaseLineNumber,
            };
        }

        if (metaRule != null)
        {
            json[MetaKey] = new JsonObject { [MetaRuleKey] = metaRule };
        }
    }

    private static void AddUnparsedField(string str, int offs, JsonObject json)
    {
        json[OriginalMsgKey] = str;
        json[UnparsedDataKey] = str[Math.Min(offs, str.Length)..];
    }

    /// <summary>Normalize a message (port of ln_normalize).</summary>
    public static int Normalize(LogNormContext ctx, string str, out JsonObject result)
    {
        var npb = new Npb { Ctx = ctx, Str = str };
        if ((ctx.Options & LogNormOptions.AddRule) != 0)
            npb.RuleSegments = new List<string>();

        result = new JsonObject();
        Pdag? endNode = null;
        int r = NormalizeRec(npb, ctx.Root, 0, bPartialMatch: false, result, ref endNode,
            failOnDuplicate: false, curJson: null, parserName: null);

        if (r == 0 && endNode!.IsTerminal)
        {
            /* success, finalize the event */
            if (endNode.Tags != null)
            {
                result["event.tags"] = endNode.Tags.DeepClone();
                ctx.Annotations.Annotate(result, endNode.Tags);
            }
            if ((ctx.Options & LogNormOptions.AddOriginalMessage) != 0)
                result[OriginalMsgKey] = str;
            AddRuleMetadata(npb, result, endNode);
        }
        else
        {
            AddUnparsedField(str, npb.LongestParsedTo, result);
        }
        return r;
    }
}
