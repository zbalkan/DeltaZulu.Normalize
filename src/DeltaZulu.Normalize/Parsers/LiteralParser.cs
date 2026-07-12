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
        var lit = ((Data)pdata!).Lit;

        /* we must always report how far we got, matched or not (the partial
         * length feeds the "unparsed-data" diagnostics) */
        if (lit.Length == 1)
        {
            parsed = npb.At(offs) == lit[0] ? 1 : 0;
        }
        else if (lit.Length == 2)
        {
            parsed = npb.At(offs) == lit[0] ? (npb.At(offs + 1) == lit[1] ? 2 : 1) : 0;
        }
        else
        {
            /* vectorized prefix compare for longer literals */
            parsed = npb.Str.AsSpan(offs).CommonPrefixLength(lit);
        }
        if (parsed != lit.Length)
        {
            return ErrorCodes.WrongParser;
        }

        if (wantValue)
        {
            value = JsonValue.Create(npb.Str.Substring(offs, parsed));
        }

        return 0;
    }

    /// <summary>Combine two literal data blocks during path compaction.</summary>
    public static void CombineData(object org, object add)
        => ((Data)org).Lit += ((Data)add).Lit;

    public static string DataForDisplay(object pdata) => ((Data)pdata).Lit;
}