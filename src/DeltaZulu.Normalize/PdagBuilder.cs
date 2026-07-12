using System.Text;
using System.Text.Json.Nodes;
using DeltaZulu.Normalize.Parsers;

namespace DeltaZulu.Normalize;

/// <summary>
/// PDAG construction and optimization (port of the build-time half of pdag.c).
///
/// Build phase: each rule is split into motifs; every motif adds an edge (and
/// destination node) to the DAG unless an identical edge already exists, in
/// which case the existing path is walked ("merged"). Literal text is
/// expanded into one single-character literal edge per char.
///
/// Optimization phase (run once after loading): the outgoing edges of every
/// node are sorted into priority order and runs of mergeable single-char
/// literal edges are compacted into multi-char literal edges.
/// </summary>
internal static class PdagBuilder
{
    /// <summary>
    /// Find a user-defined type component by name, optionally creating it.
    /// Returns the type index, or -1 when not found (and not adding).
    /// </summary>
    public static int FindType(LogNormContext ctx, string name, bool add)
    {
        for (int i = 0; i < ctx.TypePdags.Count; ++i)
        {
            if (ctx.TypePdags[i].Name == name)
                return i;
        }
        if (!add)
            return -1;
        ctx.TypePdags.Add(new TypePdag { Name = name, Dag = new Pdag(ctx) });
        return ctx.TypePdags.Count - 1;
    }

    /// <summary>
    /// Build a parser instance from a parser config object
    /// (port of ln_newParser). Returns null on error.
    /// </summary>
    public static ParserInstance? NewParser(LogNormContext ctx, JsonObject prscnf)
    {
        /* canonical config text is captured before any keys are removed */
        string textconf = JsonText.ToCompactString(prscnf);
        int assignedPrio = ParserTable.DefaultUserPriority;
        int parserPrio;
        byte prsid;
        int custType = -1;

        if (prscnf["type"] is not JsonValue typeVal)
        {
            ctx.Error($"parser type missing in config: {textconf}");
            return null;
        }
        string typeName = typeVal.GetValue<string>();
        if (typeName.StartsWith('@'))
        {
            prsid = ParserTable.CustomTypeId;
            custType = FindType(ctx, typeName, add: false);
            parserPrio = ParserTable.CustomTypePriority;
            if (custType < 0)
            {
                ctx.Error($"unknown user-defined type '{typeName}'");
                return null;
            }
        }
        else
        {
            prsid = ParserTable.NameToId(typeName);
            if (prsid == ParserTable.InvalidId)
            {
                ctx.Error($"invalid field type '{typeName}'");
                return null;
            }
            parserPrio = ParserTable.Parsers[prsid].Priority;
        }

        string? name = null;
        if (prscnf["name"] is JsonValue nameVal)
        {
            string s = nameVal.GetValue<string>();
            if (s != "-")
                name = s;
        }

        if (prscnf["priority"] is JsonValue)
            assignedPrio = (int)JsonText.GetLenientInt64(prscnf["priority"]);

        /* remove processed items so the remainder can go to the parser's construct */
        prscnf.Remove("type");
        prscnf.Remove("priority");
        prscnf.Remove("name");

        var node = new ParserInstance
        {
            PrsId = prsid,
            Conf = textconf,
        };
        node.Priority = ((assignedPrio << 8) & unchecked((int)0xffffff00)) | (parserPrio & 0xff);
        node.Name = name;
        if (prsid == ParserTable.CustomTypeId)
        {
            node.CustomTypeIndex = custType;
        }
        else if (ParserTable.Parsers[prsid].Construct is { } construct)
        {
            if (construct(ctx, prscnf, out object? pdata) != 0)
                return null;
            node.ParserData = pdata;
        }
        return node;
    }

    /// <summary>
    /// Add one parser instance to the DAG at <paramref name="pdag"/> (port of
    /// ln_pdagAddParserInstance). If an identical parser already exists on
    /// this node, the existing edge is reused and <paramref name="nextnode"/>
    /// is set to its destination — this is how common rule prefixes merge
    /// into shared DAG paths. A non-null <paramref name="nextnode"/> on entry
    /// makes the new edge point at that node (used for "alternative"
    /// branches, which must re-join at a single node).
    /// </summary>
    private static int AddParserInstance(LogNormContext ctx, JsonObject prscnf, Pdag pdag, ref Pdag? nextnode)
    {
        ParserInstance? parser = NewParser(ctx, prscnf);
        if (parser == null)
            return ErrorCodes.BadConfig;

        foreach (ParserInstance existing in pdag.Parsers)
        {
            if (existing.PrsId == parser.PrsId && existing.Conf == parser.Conf)
            {
                /* reusing the existing edge, not adding a new one: the node's
                 * incoming-edge count is unchanged, so RefCount must not bump */
                nextnode = existing.Node;
                return 0;
            }
        }

        /* a new parser type for this node */
        if (nextnode == null)
            nextnode = new Pdag(ctx);
        else
            nextnode.RefCount++;
        parser.Node = nextnode;
        pdag.Parsers.Add(parser);
        return 0;
    }

    private const int ModeSeq = 0;
    private const int ModeAlternative = 1;

    /// <summary>Add an array of parser configs, either as a sequence or as alternatives.</summary>
    private static int AddParsers(LogNormContext ctx, JsonArray prscnf, int mode, ref Pdag pdag, ref Pdag? pNextnode)
    {
        Pdag dag = pdag;
        Pdag? nextnode = pNextnode;

        foreach (JsonNode? item in prscnf)
        {
            int r;
            if (item is JsonArray arr)
            {
                Pdag localDag = dag;
                r = AddParserInternal(ctx, ref localDag, mode, arr, ref nextnode);
                if (r != 0) return r;
                if (mode == ModeSeq)
                    dag = localDag;
            }
            else if (item is JsonObject obj)
            {
                r = AddParserInternal(ctx, ref dag, mode, obj, ref nextnode);
                if (r != 0) return r;
            }
            else
            {
                ctx.Error($"bug: parser config entry of wrong type: {JsonText.ToCompactString(item)}");
                return ErrorCodes.BadConfig;
            }
            if (mode == ModeSeq)
            {
                dag = nextnode!;
                pNextnode = nextnode;
                nextnode = null;
            }
        }

        if (mode != ModeSeq)
            dag = nextnode!;
        pdag = dag;
        return 0;
    }

    /// <summary>
    /// Add a parser config (object or array) to the DAG, advancing
    /// <paramref name="pdag"/> to the node reached after it. Handles the
    /// special "alternative" type, whose sub-parsers all branch from the
    /// current node and re-join at a common next node.
    /// </summary>
    private static int AddParserInternal(LogNormContext ctx, ref Pdag pdag, int mode, JsonNode prscnf, ref Pdag? nextnode)
    {
        int r;
        if (prscnf is JsonObject obj)
        {
            string? ftype = (obj["type"] as JsonValue)?.GetValue<string>();
            if (ftype == "alternative")
            {
                if (obj["parser"] is not JsonArray alternatives)
                {
                    ctx.Error($"alternative type needs array of parsers. Object: '{JsonText.ToCompactString(obj)}'");
                    return ErrorCodes.BadConfig;
                }
                r = AddParsers(ctx, alternatives, ModeAlternative, ref pdag, ref nextnode);
                if (r != 0) return r;
            }
            else
            {
                r = AddParserInstance(ctx, obj, pdag, ref nextnode);
                if (r != 0) return r;
                if (mode == ModeSeq)
                    pdag = nextnode!;
            }
        }
        else if (prscnf is JsonArray arr)
        {
            r = AddParsers(ctx, arr, ModeSeq, ref pdag, ref nextnode);
            if (r != 0) return r;
        }
        else
        {
            ctx.Error($"bug: parser config of wrong type: '{JsonText.ToCompactString(prscnf)}'");
            return ErrorCodes.BadConfig;
        }
        return 0;
    }

    /// <summary>
    /// Add a parser config to the DAG at <paramref name="pdag"/> and advance
    /// <paramref name="pdag"/> to the following node (port of ln_pdagAddParser).
    /// </summary>
    public static int AddParser(LogNormContext ctx, ref Pdag pdag, JsonNode prscnf)
    {
        Pdag? nextnode = null;
        return AddParserInternal(ctx, ref pdag, ModeSeq, prscnf, ref nextnode);
    }

    /* ---------- optimization ---------- */

    /// <summary>
    /// Literal path compaction: merge chains of single-char literal edges
    /// into one multi-char literal edge. A literal edge is only mergeable
    /// with its successor when it extracts no field, does not terminate a
    /// rule, and the intermediate node is not shared (RefCount == 1) and has
    /// exactly one (literal) outgoing edge.
    /// </summary>
    private static void CompactLiteralPath(ParserInstance prs)
    {
        while (true)
        {
            if (!(prs.PrsId == ParserTable.LiteralId
                  && prs.Name == null
                  && !prs.Node.IsTerminal
                  && prs.Node.RefCount == 1
                  && prs.Node.Parsers.Count == 1
                  && prs.Node.Parsers[0].PrsId == ParserTable.LiteralId
                  && prs.Node.Parsers[0].Name == null
                  && prs.Node.Parsers[0].Node.RefCount == 1))
            {
                return;
            }

            ParserInstance childPrs = prs.Node.Parsers[0];
            LiteralParser.CombineData(prs.ParserData!, childPrs.ParserData!);
            prs.Node = childPrs.Node;
        }
    }

    private static void OptimizeComponent(Pdag dag, HashSet<Pdag> visited)
    {
        /* shared nodes (prefix merging, "alternative" re-joins) are reachable
         * via many paths; without this check the walk re-visits them once per
         * path, which is exponential in nested-diamond depth */
        if (!visited.Add(dag))
            return;

        if (dag.Parsers.Count > 1)
        {
            /* stable sort keeps rule-definition order for equal priorities */
            var sorted = dag.Parsers.OrderBy(p => p.Priority).ToList();
            dag.Parsers.Clear();
            dag.Parsers.AddRange(sorted);
        }

        foreach (ParserInstance prs in dag.Parsers)
        {
            CompactLiteralPath(prs);
            OptimizeComponent(prs.Node, visited);
        }
    }

    private static void DeleteComponentIds(Pdag dag, HashSet<Pdag>? visited = null)
    {
        if (visited != null && !visited.Add(dag))
            return;
        dag.RulebaseId = null;
        foreach (ParserInstance prs in dag.Parsers)
            DeleteComponentIds(prs.Node, visited);
    }

    /// <summary>
    /// Merge an already-present component ID with a newly generated one. This
    /// happens when the "alternative" parser makes a node reachable via
    /// multiple textual paths; the result is a best-effort "[a|b]" display.
    /// </summary>
    private static void FixComponentId(Pdag dag, string newId)
    {
        string curr = dag.RulebaseId!;
        int i = 0;
        int len = Math.Min(curr.Length, newId.Length);
        while (i < len && curr[i] == newId[i])
            ++i;
        if (i >= 1 && curr[i - 1] == '%')
            --i;
        string updated = $"{curr[..i]}[{curr[i..]}|{newId[i..]}]";
        DeleteComponentIds(dag);
        dag.RulebaseId = updated;
    }

    /// <summary>Assign human-readable node identifiers (used by stats/DOT output).</summary>
    private static void SetComponentIds(Pdag dag, string? prefix)
    {
        if (prefix == null)
            return;
        if (dag.RulebaseId == null)
        {
            dag.RulebaseId = prefix;
        }
        else
        {
            FixComponentId(dag, prefix);
            prefix = dag.RulebaseId;
        }
        foreach (ParserInstance prs in dag.Parsers)
        {
            string id;
            if (prs.PrsId == ParserTable.LiteralId)
            {
                string lit = LiteralParser.DataForDisplay(prs.ParserData!);
                id = prs.Name == null
                    ? prefix + lit
                    : $"{prefix}%{prs.Name}:{ParserTable.IdToName(prs.PrsId)}:{lit}%";
            }
            else
            {
                id = $"{prefix}%{prs.Name ?? "-"}:{ParserTable.IdToName(prs.PrsId)}%";
            }
            SetComponentIds(prs.Node, id);
        }
    }

    /// <summary>Optimize the full PDAG, i.e. all components (port of ln_pdagOptimize).
    /// Component IDs (a display aid for stats/DOT) are assigned separately by
    /// <see cref="AssignComponentIds"/> — their merge step intentionally
    /// re-visits shared nodes and must not run on the normalize path.</summary>
    public static void Optimize(LogNormContext ctx)
    {
        var visited = new HashSet<Pdag>();
        foreach (TypePdag t in ctx.TypePdags)
            OptimizeComponent(t.Dag, visited);
        OptimizeComponent(ctx.Root, visited);
    }

    /// <summary>Assign human-readable node IDs on demand (DOT/stats display).
    /// Clears any previous assignment first: SetComponentIds merges IDs when it
    /// re-reaches a node, so re-running it over existing IDs would corrupt them.</summary>
    public static void AssignComponentIds(LogNormContext ctx)
    {
        var cleared = new HashSet<Pdag>();
        foreach (TypePdag t in ctx.TypePdags)
            DeleteComponentIds(t.Dag, cleared);
        DeleteComponentIds(ctx.Root, cleared);

        foreach (TypePdag t in ctx.TypePdags)
            SetComponentIds(t.Dag, "");
        SetComponentIds(ctx.Root, "");
    }

    /* ---------- DOT graph generation (debug aid) ---------- */

    public static string GenerateDot(LogNormContext ctx)
    {
        AssignComponentIds(ctx);
        var sb = new StringBuilder();
        var ids = new Dictionary<Pdag, int>();
        var visited = new HashSet<Pdag>();
        sb.Append("digraph pdag {\n");
        GenerateDotRec(ctx.Root, sb, ids, visited);
        sb.Append("}\n");
        return sb.ToString();
    }

    private static int DotId(Pdag dag, Dictionary<Pdag, int> ids)
    {
        if (!ids.TryGetValue(dag, out int id))
        {
            id = ids.Count;
            ids[dag] = id;
        }
        return id;
    }

    private static void GenerateDotRec(Pdag dag, StringBuilder sb, Dictionary<Pdag, int> ids, HashSet<Pdag> visited)
    {
        int id = DotId(dag, ids);
        if (!visited.Add(dag))
            return;
        sb.Append($"l{id} [ label=\"{dag.RefCount}\"");
        if (dag.IsTerminal)
            sb.Append(" style=\"bold\"");
        sb.Append("]\n");
        foreach (ParserInstance prs in dag.Parsers)
        {
            sb.Append($"l{id} -> l{DotId(prs.Node, ids)} [label=\"");
            sb.Append(ParserTable.IdToName(prs.PrsId));
            sb.Append(':');
            if (prs.PrsId == ParserTable.LiteralId)
            {
                foreach (char c in LiteralParser.DataForDisplay(prs.ParserData!))
                {
                    if (c != '\\' && c != '"')
                        sb.Append(c);
                }
            }
            sb.Append("\" style=\"dotted\"]\n");
            GenerateDotRec(prs.Node, sb, ids, visited);
        }
    }
}
