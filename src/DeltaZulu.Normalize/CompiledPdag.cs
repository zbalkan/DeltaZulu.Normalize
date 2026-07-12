using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>
/// How an edge's value is produced. Matching and extraction are split so
/// backtracked paths never pay for extraction; the mode picks the cheapest
/// correct way to produce the value on the success unwind.
/// </summary>
internal enum ExtractMode : byte
{
    /// <summary>Re-run the parser with wantValue on the success unwind.
    /// Correct for any deterministic parser; used when the value is derived
    /// (stripped quotes, numeric conversion, sub-objects).</summary>
    Deferred = 0,

    /// <summary>The value is exactly the matched substring; materialize it
    /// from the recorded (offset, length) without re-parsing.</summary>
    RawSpan = 1,

    /// <summary>Extract during matching. Used for "repeat" (which builds its
    /// results as a side effect of matching anyway) and the structured motifs
    /// whose match phase is expensive enough that re-running it would cost
    /// more than eager extraction saves.</summary>
    Eager = 2,
}

/// <summary>
/// One outgoing edge of a compiled PDAG node. The runtime analog of
/// <see cref="ParserInstance"/>, flattened into a struct so a node's edges sit
/// contiguously in one cache-friendly array.
/// </summary>
internal readonly struct CompiledEdge
{
    /// <summary>Parser-specific configuration data (from the parser's construct
    /// function; replaced by a compiled variant for "repeat").</summary>
    public readonly object? Data;

    /// <summary>Field name, or null when the value is matched but not extracted ("-").</summary>
    public readonly string? Name;

    /// <summary>Index of the node this edge branches to when the parser matches.</summary>
    public readonly int TargetNode;

    /// <summary>Index into <see cref="CompiledPdag.TypeRoots"/> when
    /// <see cref="PrsId"/> is the custom-type ID; -1 otherwise.</summary>
    public readonly int CustomTypeIdx;

    /// <summary>Parser ID: index into <see cref="ParserTable.Parsers"/>, or
    /// <see cref="ParserTable.CustomTypeId"/>.</summary>
    public readonly byte PrsId;

    /// <summary>How this edge's value is produced (decided at compile time
    /// from the parser type and its configuration).</summary>
    public readonly ExtractMode Extract;

    /// <summary>First char of a literal edge's text, letting the walker reject
    /// the edge without a call. '\0' disables the filter (non-literal edges,
    /// and the rare literal that is empty or starts with NUL).</summary>
    public readonly char LiteralFirstChar;

    public CompiledEdge(byte prsId, char literalFirstChar, int targetNode,
        int customTypeIdx, object? data, string? name, ExtractMode extract)
    {
        PrsId = prsId;
        LiteralFirstChar = literalFirstChar;
        TargetNode = targetNode;
        CustomTypeIdx = customTypeIdx;
        Data = data;
        Name = name;
        Extract = extract;
    }
}

/// <summary>A compiled PDAG node: a slice of the shared edge array plus terminal info.</summary>
internal readonly struct CompiledNode
{
    /// <summary>Index of this node's first edge in <see cref="CompiledPdag.Edges"/>.</summary>
    public readonly int EdgeStart;

    /// <summary>Number of edges (evaluated in stored order, which is priority order).</summary>
    public readonly int EdgeCount;

    /// <summary>Index into <see cref="CompiledPdag.Terminals"/>, or -1 when no rule ends here.</summary>
    public readonly int TerminalIdx;

    /// <summary>The builder node's incoming-edge count (DOT display only).</summary>
    public readonly int RefCount;

    public bool IsTerminal => TerminalIdx >= 0;

    public CompiledNode(int edgeStart, int edgeCount, int terminalIdx, int refCount)
    {
        EdgeStart = edgeStart;
        EdgeCount = edgeCount;
        TerminalIdx = terminalIdx;
        RefCount = refCount;
    }
}

/// <summary>Metadata of a rule that terminates at a node.</summary>
internal sealed class TerminalInfo
{
    public JsonArray? Tags;
    public string? RulebaseFile;
    public int RulebaseLineNumber;
}

/// <summary>
/// An immutable, flattened snapshot of the whole rulebase: the main component,
/// all user-defined-type components and all "repeat" sub-components share one
/// node/edge arena. Normalization only ever reads a snapshot, so a context can
/// be reloaded by compiling a new snapshot and atomically swapping it in while
/// in-flight normalizations finish on the old one.
/// </summary>
internal sealed class CompiledPdag
{
    /// <summary>All nodes; index 0 is the main component's root.</summary>
    public required CompiledNode[] Nodes { get; init; }

    /// <summary>All edges, grouped per node in priority order.</summary>
    public required CompiledEdge[] Edges { get; init; }

    /// <summary>Terminal metadata, indexed by <see cref="CompiledNode.TerminalIdx"/>.</summary>
    public required TerminalInfo[] Terminals { get; init; }

    /// <summary>Root node index of each user-defined type component.</summary>
    public required int[] TypeRoots { get; init; }

    /// <summary>Root node index of the main component.</summary>
    public int RootNode => 0;

    /// <summary>Per-node usage counters, allocated only with
    /// <see cref="LogNormOptions.CollectStats"/>. Increments are unsynchronized
    /// (racy but benign), exactly like the C library's counters.</summary>
    public int[]? StatsCalled;

    public int[]? StatsBacktracked;
}