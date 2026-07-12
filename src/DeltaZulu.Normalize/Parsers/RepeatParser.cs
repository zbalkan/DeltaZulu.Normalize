using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize.Parsers;

/// <summary>
/// The "repeat" special motif: repeatedly applies a sub-parser as long as a
/// "while" condition sub-parser matches afterwards. Used for constructs like
/// space-separated lists of tokens. Both sub-parsers are themselves small
/// PDAGs (built once, at construct time), which keeps looping inside motif
/// behaviour and the outer PDAG acyclic.
/// </summary>
internal static class RepeatParser
{
    /// <summary>Construct-time data: builder sub-graphs, compiled into the
    /// snapshot arena (as <see cref="CompiledData"/>) by the PDAG compiler.</summary>
    internal sealed class Data
    {
        public required Pdag Parser { get; init; }
        public required Pdag WhileCond { get; init; }
        public bool PermitMismatchInParser;
        public bool FailOnDuplicate;
    }

    /// <summary>Runtime data: root node indices of the two sub-components
    /// within the snapshot carried by the npb.</summary>
    internal sealed class CompiledData
    {
        public int ParserRoot { get; init; }
        public int WhileRoot { get; init; }
        public bool PermitMismatchInParser;
        public bool FailOnDuplicate;
    }

    /// <summary>
    /// The "repeat" parser supports a "." field name only when its "parser"
    /// part is a single parser definition, since otherwise there would be no
    /// unambiguous way to decide which of several fields to place directly
    /// into the array.
    /// </summary>
    private static bool ChkNoDupeDotInParserDefs(LogNormContext ctx, JsonNode? parsers)
    {
        var nParsers = 0;
        var nDots = 0;
        if (parsers is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonObject obj && obj["name"] is JsonValue nameVal)
                {
                    ++nParsers;
                    if (nameVal.GetValue<string>() == ".")
                    {
                        ++nDots;
                    }
                }
            }
        }
        if (nParsers > 1 && nDots > 0)
        {
            ctx.Error("'repeat' parser supports dot name only if single parser is used " +
                      $"in 'parser' part, invalid construct: {JsonText.ToCompactString(parsers)}");
            return false;
        }
        return true;
    }

    public static int Construct(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        pdata = null;
        Pdag? parser = null;
        Pdag? whileCond = null;
        var permitMismatch = false;
        var failOnDuplicate = false;

        foreach ((var key, var val) in config)
        {
            if (key == "parser")
            {
                if (!ChkNoDupeDotInParserDefs(ctx, val))
                {
                    return ErrorCodes.BadConfig;
                }

                var root = new Pdag(ctx);
                var endnode = root;
                if (PdagBuilder.AddParser(ctx, ref endnode, val!) != 0)
                {
                    return ErrorCodes.BadConfig;
                }

                endnode.IsTerminal = true;
                parser = root;
            }
            else if (key == "while")
            {
                var root = new Pdag(ctx);
                var endnode = root;
                if (PdagBuilder.AddParser(ctx, ref endnode, val!) != 0)
                {
                    return ErrorCodes.BadConfig;
                }

                endnode.IsTerminal = true;
                whileCond = root;
            }
            else if (string.Equals(key, "option.permitMismatchInParser", StringComparison.OrdinalIgnoreCase))
            {
                if (val is JsonValue jv && jv.TryGetValue(out bool b))
                {
                    permitMismatch = b;
                }
            }
            else if (string.Equals(key, "option.failOnDuplicate", StringComparison.OrdinalIgnoreCase))
            {
                if (val is JsonValue jv2 && jv2.TryGetValue(out bool b2))
                {
                    failOnDuplicate = b2;
                }
            }
            else
            {
                ctx.Error($"invalid param for repeat: {JsonText.ToCompactString(val)}");
            }
        }

        if (parser == null || whileCond == null)
        {
            ctx.Error("repeat parser needs 'parser','while' parameters");
            return ErrorCodes.BadConfig;
        }

        pdata = new Data {
            Parser = parser,
            WhileCond = whileCond,
            PermitMismatchInParser = permitMismatch,
            FailOnDuplicate = failOnDuplicate,
        };
        return 0;
    }

    public static int Parse(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var data = (CompiledData)pdata!;
        var strtoffs = offs;
        var lastMatch = strtoffs;
        var lastKnownGood = strtoffs;
        JsonArray? jsonArr = null;
        JsonObject? parsedValue = null;
        var parsedToSave = npb.ParsedTo;
        var longestParsedToSave = npb.LongestParsedTo;
        var mergeResults = parserName == ".";
        int r;

        do
        {
            var roundStart = strtoffs;
            parsedValue ??= new JsonObject();
            var endNode = -1;
            r = Normalizer.NormalizeRec(npb, data.ParserRoot, strtoffs, bPartialMatch: true,
                parsedValue, ref endNode, data.FailOnDuplicate, parsedValue, parserName);
            strtoffs = npb.ParsedTo;

            if (r != 0)
            {
                parsedValue = null;
                if (data.PermitMismatchInParser)
                {
                    strtoffs = lastKnownGood; /* go back to final match */
                    break; /* -> success */
                }
                npb.LongestParsedTo = Math.Max(lastMatch, longestParsedToSave);
                return ErrorCodes.WrongParser;
            }

            if (!mergeResults)
            {
                jsonArr ??= new JsonArray();

                /* a member named "." means: place only that value into the array */
                JsonNode? toAdd = parsedValue;
                foreach ((var key, var val) in parsedValue)
                {
                    if (key == ".")
                    {
                        toAdd = val;
                    }
                }
                if (!ReferenceEquals(toAdd, parsedValue))
                {
                    parsedValue.Remove("."); /* detach, so no clone is needed */
                }

                jsonArr.Add(toAdd);
                parsedValue = null;
            }

            npb.ParsedTo = 0;
            lastMatch = lastKnownGood;
            lastKnownGood = strtoffs; /* record position in case the while-check fails */
            endNode = -1;
            r = Normalizer.NormalizeRec(npb, data.WhileRoot, strtoffs, bPartialMatch: true,
                null, ref endNode, failOnDuplicate: false, curJson: null, parserName);
            if (r == 0)
            {
                strtoffs = npb.ParsedTo;
            }

            /* neither the parser nor the while-condition advanced the offset
             * this round; looping further would never terminate */
            if (strtoffs == roundStart)
            {
                break;
            }
        } while (r == 0);

        parsed = strtoffs - offs;
        if (wantValue)
        {
            value = mergeResults ? parsedValue : jsonArr;
        }

        npb.ParsedTo = parsedToSave;
        return 0;
    }
}