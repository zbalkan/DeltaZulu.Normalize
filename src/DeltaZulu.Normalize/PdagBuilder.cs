using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>
/// PDAG construction (port of the build-time half of pdag.c).
///
/// Each rule is split into motifs; every motif adds an edge (and destination
/// node) to the builder graph unless an identical edge already exists, in
/// which case the existing path is walked ("merged"). Literal text is
/// expanded into one single-character literal edge per char.
///
/// The optimization passes pdag.c applies in place (priority sort, literal
/// path compaction) run during snapshot compilation instead — see
/// <see cref="PdagCompiler"/> — so this graph stays append-only and further
/// rulebases can be loaded at any time (hot reload).
/// </summary>
internal static class PdagBuilder
{
    /// <summary>
    /// Find a user-defined type component by name, optionally creating it.
    /// Returns the type index, or -1 when not found (and not adding).
    /// </summary>
    public static int FindType(LogNormContext ctx, string name, bool add)
    {
        for (var i = 0; i < ctx.TypePdags.Count; ++i)
        {
            if (ctx.TypePdags[i].Name == name)
            {
                return i;
            }
        }
        if (!add)
        {
            return -1;
        }

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
        var textconf = JsonText.ToCompactString(prscnf);
        var assignedPrio = ParserTable.DefaultUserPriority;
        int parserPrio;
        byte prsid;
        var custType = -1;

        if (prscnf["type"] is not JsonValue typeVal)
        {
            ctx.Error($"parser type missing in config: {textconf}");
            return null;
        }
        var typeName = typeVal.GetValue<string>();
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
            var s = nameVal.GetValue<string>();
            if (s != "-")
            {
                name = s;
            }
        }

        if (prscnf["priority"] is JsonValue)
        {
            assignedPrio = (int)JsonText.GetLenientInt64(prscnf["priority"]);
        }

        /* remove processed items so the remainder can go to the parser's construct */
        prscnf.Remove("type");
        prscnf.Remove("priority");
        prscnf.Remove("name");

        var node = new ParserInstance {
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
            if (construct(ctx, prscnf, out var pdata) != 0)
            {
                return null;
            }

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
        var parser = NewParser(ctx, prscnf);
        if (parser == null)
        {
            return ErrorCodes.BadConfig;
        }

        foreach (var existing in pdag.Parsers)
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
        {
            nextnode = new Pdag(ctx);
        }
        else
        {
            nextnode.RefCount++;
        }

        parser.Node = nextnode;
        pdag.Parsers.Add(parser);
        return 0;
    }

    private const int ModeSeq = 0;
    private const int ModeAlternative = 1;

    /// <summary>Add an array of parser configs, either as a sequence or as alternatives.</summary>
    private static int AddParsers(LogNormContext ctx, JsonArray prscnf, int mode, ref Pdag pdag, ref Pdag? pNextnode)
    {
        var dag = pdag;
        var nextnode = pNextnode;

        foreach (var item in prscnf)
        {
            int r;
            if (item is JsonArray arr)
            {
                var localDag = dag;
                r = AddParserInternal(ctx, ref localDag, mode, arr, ref nextnode);
                if (r != 0)
                {
                    return r;
                }

                if (mode == ModeSeq)
                {
                    dag = localDag;
                }
            }
            else if (item is JsonObject obj)
            {
                r = AddParserInternal(ctx, ref dag, mode, obj, ref nextnode);
                if (r != 0)
                {
                    return r;
                }
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
        {
            dag = nextnode!;
        }

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
            var ftype = (obj["type"] as JsonValue)?.GetValue<string>();
            if (ftype == "alternative")
            {
                if (obj["parser"] is not JsonArray alternatives)
                {
                    ctx.Error($"alternative type needs array of parsers. Object: '{JsonText.ToCompactString(obj)}'");
                    return ErrorCodes.BadConfig;
                }
                r = AddParsers(ctx, alternatives, ModeAlternative, ref pdag, ref nextnode);
                if (r != 0)
                {
                    return r;
                }
            }
            else
            {
                r = AddParserInstance(ctx, obj, pdag, ref nextnode);
                if (r != 0)
                {
                    return r;
                }

                if (mode == ModeSeq)
                {
                    pdag = nextnode!;
                }
            }
        }
        else if (prscnf is JsonArray arr)
        {
            r = AddParsers(ctx, arr, ModeSeq, ref pdag, ref nextnode);
            if (r != 0)
            {
                return r;
            }
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
}