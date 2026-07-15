using System.Collections.Frozen;
using System.Text.Json.Nodes;
using DeltaZulu.Normalize.Parsers;

namespace DeltaZulu.Normalize;

/// <summary>
/// Builds parser-specific data from the (already reduced) JSON config of a
/// field. Returns 0 on success, <see cref="ErrorCodes.BadConfig"/> otherwise.
/// </summary>
internal delegate int ConstructFunc(LogNormContext ctx, JsonObject config, out object? pdata);

/// <summary>
/// Parse function of a motif parser.
/// </summary>
/// <param name="npb">the normalization parameter block (message + state)</param>
/// <param name="offs">offset into the message where matching must start</param>
/// <param name="pdata">parser-specific configuration data (from construct)</param>
/// <param name="parserName">field name of this parser instance (used by repeat)</param>
/// <param name="parsed">number of chars consumed on success</param>
/// <param name="wantValue">false when the value would be discarded (unnamed field);
/// parsers may then skip extraction entirely</param>
/// <param name="value">extracted value (only meaningful on success and when wanted)</param>
/// <returns>0 on success, <see cref="ErrorCodes.WrongParser"/> when the motif does not match</returns>
internal delegate int ParseFunc(
    Npb npb, ref int offs, object? pdata, string? parserName,
    out int parsed, bool wantValue, ref JsonNode? value);

/// <summary>Static description of one motif parser type.</summary>
internal sealed class ParserInfo
{
    /// <summary>Whether this parser is part of the public parser catalog.</summary>
    public bool CatalogExposed { get; init; } = true;

    public ConstructFunc? Construct { get; init; }

    /// <summary>Parser name as used in the rulebase.</summary>
    public required string Name { get; init; }

    public required ParseFunc Parse { get; init; }

    /// <summary>Parser-specific priority (0 = highest .. 255 = lowest / last resort).</summary>
    public int Priority { get; init; }

    /// <summary>Whether this parser needs rulebase configuration to be usable.</summary>
    public bool RequiresConfiguration { get; init; }

    /// <summary>How suggestion tooling should use this parser.</summary>
    public LiblognormParserSuggestionUse SuggestionUse { get; init; } =
        LiblognormParserSuggestionUse.InferFromSample;
}

/// <summary>
/// The parser lookup table. Parser IDs are indexes into <see cref="Parsers"/>;
/// the literal parser MUST be entry 0 and repeat entry 1 (the engine
/// special-cases both by ID).
/// </summary>
internal static class ParserTable
{
    internal const string RestParserName = "rest";
    internal const string WordParserName = "word";

    public const byte CustomTypeId = 254;

    /// <summary>Priority used for user-defined (custom) types.</summary>
    public const int CustomTypePriority = 16;

    /// <summary>Default user priority when a rule does not assign one.</summary>
    public const int DefaultUserPriority = 30000;

    public const byte InvalidId = 255;
    public const byte LiteralId = 0;
    public const byte RepeatId = 1;

    public static readonly ParserInfo[] Parsers =
    {
        new() { Name = "literal", Priority = 4, Construct = LiteralParser.Construct, Parse = LiteralParser.Parse, CatalogExposed = false, RequiresConfiguration = true, SuggestionUse = LiblognormParserSuggestionUse.None },
        new() { Name = "repeat", Priority = 4, Construct = RepeatParser.Construct, Parse = RepeatParser.Parse, CatalogExposed = false, RequiresConfiguration = true, SuggestionUse = LiblognormParserSuggestionUse.None },
        new() { Name = "date-rfc3164", Priority = 8, Construct = DateTimeParsers.ConstructRfc3164, Parse = DateTimeParsers.ParseRfc3164 },
        new() { Name = "date-rfc5424", Priority = 8, Construct = DateTimeParsers.ConstructRfc5424, Parse = DateTimeParsers.ParseRfc5424 },
        new() { Name = "number", Priority = 16, Construct = NumberParsers.ConstructNumber, Parse = NumberParsers.ParseNumber },
        new() { Name = "float", Priority = 16, Construct = NumberParsers.ConstructFloat, Parse = NumberParsers.ParseFloat },
        new() { Name = "hexnumber", Priority = 16, Construct = NumberParsers.ConstructHexNumber, Parse = NumberParsers.ParseHexNumber },
        new() { Name = "kernel-timestamp", Priority = 16, Parse = DateTimeParsers.ParseKernelTimestamp },
        new() { Name = "whitespace", Priority = 4, Parse = CoreParsers.ParseWhitespace },
        new() { Name = "ipv4", Priority = 4, Parse = NetworkParsers.ParseIPv4 },
        new() { Name = "ipv6", Priority = 4, Parse = NetworkParsers.ParseIPv6 },
        new() { Name = WordParserName, Priority = 32, Parse = CoreParsers.ParseWord },
        new() { Name = "alpha", Priority = 32, Parse = CoreParsers.ParseAlpha },
        new() { Name = RestParserName, Priority = 255, Parse = CoreParsers.ParseRest, SuggestionUse = LiblognormParserSuggestionUse.FallbackOnly },
        new() { Name = "op-quoted-string", Priority = 64, Construct = CoreParsers.ConstructOpQuotedString, Parse = CoreParsers.ParseOpQuotedString },
        new() { Name = "quoted-string", Priority = 64, Parse = CoreParsers.ParseQuotedString },
        new() { Name = "date-iso", Priority = 8, Parse = DateTimeParsers.ParseIsoDate },
        new() { Name = "time-24hr", Priority = 8, Parse = DateTimeParsers.ParseTime24hr },
        new() { Name = "time-12hr", Priority = 8, Parse = DateTimeParsers.ParseTime12hr },
        new() { Name = "duration", Priority = 16, Parse = DateTimeParsers.ParseDuration },
        new() { Name = "cisco-interface-spec", Priority = 4, Parse = NetworkParsers.ParseCiscoInterfaceSpec },
        new() { Name = "json", Priority = 4, Construct = StructuredParsers.ConstructJson, Parse = StructuredParsers.ParseJson },
        new() { Name = "cee-syslog", Priority = 4, Parse = StructuredParsers.ParseCeeSyslog },
        new() { Name = "mac48", Priority = 16, Parse = NetworkParsers.ParseMac48 },
        new() { Name = "cef", Priority = 4, Parse = StructuredParsers.ParseCef },
        new() { Name = "v2-iptables", Priority = 4, Parse = StructuredParsers.ParseV2IpTables },
        new() { Name = "name-value-list", Priority = 8, Construct = StructuredParsers.ConstructNameValue, Parse = StructuredParsers.ParseNameValue },
        new() { Name = "checkpoint-lea", Priority = 4, Construct = StructuredParsers.ConstructCheckpointLea, Parse = StructuredParsers.ParseCheckpointLea },
        new() { Name = "string-to", Priority = 32, Construct = CoreParsers.ConstructStringTo, Parse = CoreParsers.ParseStringTo, RequiresConfiguration = true, SuggestionUse = LiblognormParserSuggestionUse.None },
        new() { Name = "char-to", Priority = 32, Construct = CoreParsers.ConstructCharTo, Parse = CoreParsers.ParseCharTo, RequiresConfiguration = true, SuggestionUse = LiblognormParserSuggestionUse.None },
        new() { Name = "char-sep", Priority = 32, Construct = CoreParsers.ConstructCharSeparated, Parse = CoreParsers.ParseCharSeparated, RequiresConfiguration = true, SuggestionUse = LiblognormParserSuggestionUse.None },
        new() { Name = "string", Priority = 32, Construct = StringParser.Construct, Parse = StringParser.Parse },
    };

    private static readonly CompiledPdag EmptySnapshot = new() {
        Edges = [],
        Nodes = [new CompiledNode(0, 0, -1, 0)],
        Terminals = [],
        TypeRoots = [],
    };

    internal static IReadOnlyList<LiblognormParserDescriptor> CatalogParsers { get; } = BuildCatalogParsers();

    private static readonly FrozenDictionary<string, byte> CatalogIdsByName = BuildCatalogIdsByName();

    private static readonly FrozenDictionary<string, LiblognormParserDescriptor> CatalogParsersByName =
        CatalogParsers.ToFrozenDictionary(p => p.Name, StringComparer.Ordinal);

    internal static bool IsCatalogFullMatch(string parserName, ReadOnlySpan<char> sample)
    {
        if (!CatalogIdsByName.TryGetValue(parserName, out var id)
            || !CatalogParsersByName.TryGetValue(parserName, out var descriptor)
            || descriptor.RequiresConfiguration)
        {
            return false;
        }

        object? parserData = null;
        var parserInfo = Parsers[id];
        if (parserInfo.Construct is { } construct)
        {
            var ctx = new LogNormContext();
            if (construct(ctx, new JsonObject(), out parserData) != 0)
            {
                return false;
            }
        }

        var input = sample.ToString();
        var npb = new Npb { Ctx = new LogNormContext(), Snap = EmptySnapshot, Str = input };
        var offset = 0;
        JsonNode? value = null;
        var result = Dispatch(id, npb, ref offset, parserData, parserName: null,
            out var parsed, wantValue: false, ref value);

        return result == 0 && offset + parsed == input.Length;
    }

    internal static bool TryGetCatalogParser(string name, out LiblognormParserDescriptor parser) =>
        CatalogParsersByName.TryGetValue(name, out parser!);

    private static LiblognormParserDescriptor[] BuildCatalogParsers()
    {
        var descriptors = new List<LiblognormParserDescriptor>();

        foreach (var parser in Parsers)
        {
            if (!parser.CatalogExposed)
            {
                continue;
            }

            descriptors.Add(new LiblognormParserDescriptor(
                parser.Name,
                parser.Priority,
                parser.SuggestionUse,
                parser.RequiresConfiguration));
        }

        return descriptors.ToArray();
    }

    private static FrozenDictionary<string, byte> BuildCatalogIdsByName()
    {
        var idsByName = new Dictionary<string, byte>(StringComparer.Ordinal);
        for (var i = 0; i < Parsers.Length; i++)
        {
            if (Parsers[i].CatalogExposed)
            {
                idsByName.Add(Parsers[i].Name, (byte)i);
            }
        }

        return idsByName.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Number of switch cases in <see cref="Dispatch"/> (test guard).</summary>
    internal const int DispatchCaseCount = 32;

    /// <summary>
    /// Hot-path dispatch: a switch over the parser ID compiles to a jump
    /// table with direct (inlineable) calls, unlike the delegate table above,
    /// which stays for construct-time lookup and name mapping. The case order
    /// MUST match <see cref="Parsers"/> exactly; SwitchCoversWholeTable in the
    /// test suite guards the count.
    /// </summary>
    public static int Dispatch(byte prsId, Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        switch (prsId)
        {
            case 0: return LiteralParser.Parse(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 1: return RepeatParser.Parse(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 2: return DateTimeParsers.ParseRfc3164(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 3: return DateTimeParsers.ParseRfc5424(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 4: return NumberParsers.ParseNumber(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 5: return NumberParsers.ParseFloat(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 6: return NumberParsers.ParseHexNumber(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 7: return DateTimeParsers.ParseKernelTimestamp(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 8: return CoreParsers.ParseWhitespace(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 9: return NetworkParsers.ParseIPv4(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 10: return NetworkParsers.ParseIPv6(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 11: return CoreParsers.ParseWord(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 12: return CoreParsers.ParseAlpha(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 13: return CoreParsers.ParseRest(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 14: return CoreParsers.ParseOpQuotedString(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 15: return CoreParsers.ParseQuotedString(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 16: return DateTimeParsers.ParseIsoDate(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 17: return DateTimeParsers.ParseTime24hr(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 18: return DateTimeParsers.ParseTime12hr(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 19: return DateTimeParsers.ParseDuration(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 20: return NetworkParsers.ParseCiscoInterfaceSpec(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 21: return StructuredParsers.ParseJson(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 22: return StructuredParsers.ParseCeeSyslog(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 23: return NetworkParsers.ParseMac48(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 24: return StructuredParsers.ParseCef(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 25: return StructuredParsers.ParseV2IpTables(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 26: return StructuredParsers.ParseNameValue(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 27: return StructuredParsers.ParseCheckpointLea(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 28: return CoreParsers.ParseStringTo(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 29: return CoreParsers.ParseCharTo(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 30: return CoreParsers.ParseCharSeparated(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            case 31: return StringParser.Parse(npb, ref offs, pdata, parserName, out parsed, wantValue, ref value);
            default:
                parsed = 0;
                return ErrorCodes.WrongParser;
        }
    }

    public static string IdToName(byte id)
        => id == CustomTypeId ? "USER-DEFINED" : Parsers[id].Name;

    public static byte NameToId(string name)
    {
        for (var i = 0; i < Parsers.Length; ++i)
        {
            if (Parsers[i].Name == name)
            {
                return (byte)i;
            }
        }
        return InvalidId;
    }
}
