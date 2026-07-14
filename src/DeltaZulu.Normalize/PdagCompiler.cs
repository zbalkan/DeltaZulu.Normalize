using System.Text;
using DeltaZulu.Normalize.Parsers;

namespace DeltaZulu.Normalize;

/// <summary>
/// Compiles the mutable builder graph (<see cref="Pdag"/>/<see cref="ParserInstance"/>)
/// into an immutable <see cref="CompiledPdag"/> snapshot. The optimization
/// passes that pdag.c applies in place (priority sort, literal path
/// compaction) happen here, during flattening, so the builder graph is never
/// mutated and can be compiled again after further rulebase loads — which is
/// what makes hot reload possible.
/// </summary>
internal static class PdagCompiler
{
    public static CompiledPdag Compile(LogNormContext ctx)
    {
        var state = new State();

        /* the main component first, so its root lands at index 0 */
        state.CompileNode(ctx.Root, optimize: true);

        var typeRoots = new int[ctx.TypePdags.Count];
        for (var i = 0; i < ctx.TypePdags.Count; ++i)
        {
            typeRoots[i] = state.CompileNode(ctx.TypePdags[i].Dag, optimize: true);
        }

        var snap = new CompiledPdag {
            Nodes = state.BuildNodes(out var edges),
            Edges = edges,
            Terminals = state.Terminals.ToArray(),
            TypeRoots = typeRoots,
        };
        if ((ctx.Options & LogNormOptions.CollectStats) != 0)
        {
            snap.StatsCalled = new int[snap.Nodes.Length];
            snap.StatsBacktracked = new int[snap.Nodes.Length];
        }
        return snap;
    }

    /// <summary>
    /// GraphViz DOT description of the snapshot's main component (port of
    /// ln_genDotPDAGGraph, previously generated from the builder graph).
    /// </summary>
    public static string GenerateDot(CompiledPdag snap)
    {
        var sb = new StringBuilder();
        var visited = new HashSet<int>();
        sb.Append("digraph pdag {\n");
        GenerateDotRec(snap, snap.RootNode, sb, visited);
        sb.Append("}\n");
        return sb.ToString();
    }

    private static void GenerateDotRec(CompiledPdag snap, int nodeIdx, StringBuilder sb, HashSet<int> visited)
    {
        if (!visited.Add(nodeIdx))
        {
            return;
        }

        var node = snap.Nodes[nodeIdx];
        sb.Append($"l{nodeIdx} [ label=\"{node.RefCount}\"");
        if (node.IsTerminal)
        {
            sb.Append(" style=\"bold\"");
        }

        sb.Append("]\n");
        for (var i = node.EdgeStart; i < node.EdgeStart + node.EdgeCount; ++i)
        {
            var edge = snap.Edges[i];
            sb.Append($"l{nodeIdx} -> l{edge.TargetNode} [label=\"");
            sb.Append(ParserTable.IdToName(edge.PrsId));
            sb.Append(':');
            if (edge.PrsId == ParserTable.LiteralId)
            {
                foreach (var c in LiteralParser.DataForDisplay(edge.Data!))
                {
                    if (c != '\\' && c != '"')
                    {
                        sb.Append(c);
                    }
                }
            }
            sb.Append("\" style=\"dotted\"]\n");
            GenerateDotRec(snap, edge.TargetNode, sb, visited);
        }
    }

    private sealed class State
    {
        public readonly List<TerminalInfo> Terminals = new();

        private readonly Dictionary<Pdag, int> _map = new();

        private readonly List<TempNode> _nodes = new();

        /// <summary>Flatten the per-node edge lists into the final contiguous arrays.</summary>
        public CompiledNode[] BuildNodes(out CompiledEdge[] edges)
        {
            var nodes = new CompiledNode[_nodes.Count];
            var edgeCount = 0;
            foreach (var t in _nodes)
            {
                edgeCount += t.Edges.Count;
            }

            edges = new CompiledEdge[edgeCount];

            var offset = 0;
            for (var i = 0; i < _nodes.Count; ++i)
            {
                var t = _nodes[i];
                t.Edges.CopyTo(edges, offset);
                nodes[i] = new CompiledNode(offset, t.Edges.Count, t.TerminalIdx, t.RefCount);
                offset += t.Edges.Count;
            }
            return nodes;
        }

        /// <summary>
        /// <para>
        /// Compile one builder node (and, recursively, everything reachable
        /// from it) into the arena, returning its node index. Shared builder
        /// nodes map to a single compiled node, preserving the DAG shape.
        /// </para>
        /// <para>
        /// <paramref name="optimize"/> selects the passes ln_pdagOptimize runs
        /// on the main and type components: priority-sorting each node's edges
        /// and compacting unnamed literal chains. "repeat" sub-components are
        /// compiled verbatim (the C engine never optimizes them, and sorting
        /// would change their evaluation order).
        /// </para>
        /// </summary>
        public int CompileNode(Pdag dag, bool optimize)
        {
            if (_map.TryGetValue(dag, out var idx))
            {
                return idx;
            }

            idx = _nodes.Count;
            _map[dag] = idx;
            var tmp = new TempNode { RefCount = dag.RefCount };
            _nodes.Add(tmp);

            if (dag.IsTerminal)
            {
                tmp.TerminalIdx = Terminals.Count;
                Terminals.Add(new TerminalInfo {
                    Tags = dag.Tags,
                    RulebaseFile = dag.RulebaseFile,
                    RulebaseLineNumber = dag.RulebaseLineNumber,
                });
            }

            /* stable sort keeps rule-definition order for equal priorities */
            IEnumerable<ParserInstance> parsers = optimize && dag.Parsers.Count > 1
                ? dag.Parsers.OrderBy(p => p.Priority)
                : dag.Parsers;

            foreach (var prs in parsers)
            {
                var data = prs.ParserData;
                var target = prs.Node;

                if (optimize && prs.PrsId == ParserTable.LiteralId && prs.Name == null)
                {
                    /* literal path compaction: merge a chain of single-char
                     * literal edges into one multi-char literal edge. Only
                     * chains through unshared, non-terminal, single-edge
                     * nodes qualify (same conditions as pdag.c's
                     * optLitPathCompact); skipped nodes are simply never
                     * compiled. The builder's literal data is not mutated —
                     * a merged edge gets fresh data. */
                    var lit = ((LiteralParser.Data)data!).Lit;
                    var merged = false;
                    while (!target.IsTerminal
                           && target.RefCount == 1
                           && target.Parsers.Count == 1
                           && target.Parsers[0].PrsId == ParserTable.LiteralId
                           && target.Parsers[0].Name == null
                           && target.Parsers[0].Node.RefCount == 1)
                    {
                        lit += ((LiteralParser.Data)target.Parsers[0].ParserData!).Lit;
                        target = target.Parsers[0].Node;
                        merged = true;
                    }
                    if (merged)
                    {
                        data = new LiteralParser.Data { Lit = lit };
                    }
                }
                else if (prs.PrsId == ParserTable.RepeatId)
                {
                    var rd = (RepeatParser.Data)data!;
                    data = new RepeatParser.CompiledData {
                        ParserRoot = CompileNode(rd.Parser, optimize: false),
                        WhileRoot = CompileNode(rd.WhileCond, optimize: false),
                        PermitMismatchInParser = rd.PermitMismatchInParser,
                        FailOnDuplicate = rd.FailOnDuplicate,
                    };
                }

                var targetIdx = CompileNode(target, optimize);
                var firstChar = '\0';
                if (prs.PrsId == ParserTable.LiteralId)
                {
                    var lit = ((LiteralParser.Data)data!).Lit;
                    if (lit.Length > 0)
                    {
                        firstChar = lit[0];
                    }
                }
                tmp.Edges.Add(new CompiledEdge(prs.PrsId, firstChar, targetIdx,
                    prs.CustomTypeIndex, data, prs.Name, ClassifyExtract(prs.PrsId, data)));
            }
            return idx;
        }

        /// <summary>
        /// Pick the cheapest correct extraction mode for a parser instance.
        /// RawSpan is only valid when the parser's wanted value is exactly the
        /// matched substring — for the format-configurable parsers that
        /// depends on their construct-time data, which is why this runs at
        /// compile time and not per message.
        /// </summary>
        private static ExtractMode ClassifyExtract(byte prsId, object? data) => ParserTable.IdToName(prsId) switch {
            /* value == matched substring, unconditionally */
            "literal" or "whitespace" or "word" or "alpha" or "rest" or "kernel-timestamp" or "date-iso" or "time-24hr" or "time-12hr" or "duration" or "ipv4" or "ipv6" or "mac48" or "string-to" or "char-to" or "char-sep" => ExtractMode.RawSpan,
            /* value == matched substring in the default string format only */
            "number" => ((NumberParsers.NumberData)data!).FmtMode == FormatMode.AsString
                            ? ExtractMode.RawSpan : ExtractMode.Deferred,
            "float" => ((NumberParsers.FloatData)data!).FmtMode == FormatMode.AsString
                            ? ExtractMode.RawSpan : ExtractMode.Deferred,
            "hexnumber" => ((NumberParsers.HexNumberData)data!).FmtMode == FormatMode.AsString
                            ? ExtractMode.RawSpan : ExtractMode.Deferred,
            "date-rfc3164" or "date-rfc5424" => ((DateTimeParsers.DateData)data!).FmtMode == FormatMode.AsString
                            ? ExtractMode.RawSpan : ExtractMode.Deferred,
            /* matching is the expensive part; re-running it to extract
             * would cost more than eager extraction saves */
            "repeat" or "json" or "cee-syslog" or "cef" or "v2-iptables" or "name-value-list" or "checkpoint-lea" => ExtractMode.Eager,
            /* derived values (stripped quotes, unescaping, sub-objects):
             * re-run the cheap parse on the success unwind */
            _ => ExtractMode.Deferred,
        };

        private sealed class TempNode
        {
            public readonly List<CompiledEdge> Edges = new();
            public int RefCount;
            public int TerminalIdx = -1;
        }
    }

    /* ---------- DOT graph generation (debug aid) ---------- */
}
