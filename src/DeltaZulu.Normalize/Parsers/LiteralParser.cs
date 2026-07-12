using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize.Parsers;

/// <summary>
/// The literal motif: matches an exact character sequence. Literals are
/// ordinary motifs so the normalizer can evaluate all edges uniformly and in
/// priority order. During rule loading each literal char becomes its own
/// edge; the optimizer later compacts runs into multi-char literals.
/// </summary>
internal static class LiteralParser
{
    internal sealed class Data
    {
        public required string Lit { get; set; }
    }

    public static int Construct(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        pdata = null;
        if (config["text"] is not JsonValue text)
        {
            ctx.Error("literal type needs 'text' parameter");
            return ErrorCodes.BadConfig;
        }
        pdata = new Data { Lit = text.GetValue<string>() };
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Parse(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        string lit = ((Data)pdata!).Lit;

        /* vectorized prefix compare; we must always report how far we got
         * (the partial length feeds the "unparsed-data" diagnostics) */
        parsed = npb.Str.AsSpan(offs).CommonPrefixLength(lit);
        if (parsed != lit.Length)
            return ErrorCodes.WrongParser;
        if (wantValue)
            value = JsonValue.Create(npb.Str.Substring(offs, parsed));
        return 0;
    }

    /// <summary>Combine two literal data blocks during path compaction.</summary>
    public static void CombineData(object org, object add)
        => ((Data)org).Lit += ((Data)add).Lit;

    public static string DataForDisplay(object pdata) => ((Data)pdata).Lit;
}
