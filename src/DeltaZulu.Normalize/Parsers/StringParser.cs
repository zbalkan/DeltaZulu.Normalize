using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize.Parsers;

/// <summary>
/// The generic "string" motif: a configurable run of "permitted" characters,
/// optionally quoted, with configurable escaping and end-of-match strictness.
/// This is the most parameterizable of the built-in text motifs.
/// </summary>
internal static class StringParser
{
    internal enum QuoteMode
    { Auto = 0, None = 1, Required = 2 }

    internal enum EscMode
    { None = 0, Backslash = 1, Double = 2, Both = 3 }

    internal enum MatchingMode
    { Exact = 0, Lazy = 1 }

    internal sealed class Data
    {
        public QuoteMode QuoteMode = QuoteMode.Auto;
        public bool StripQuotes = true;
        public EscMode EscMd = EscMode.Both;
        public MatchingMode Matching = MatchingMode.Exact;
        public bool DashIsEmpty;
        public char QCharBegin = '"';
        public char QCharEnd = '"';

        /// <summary>Permitted-character bitset for the U+0000..U+00FF range,
        /// packed as 4 ulongs (256 bits) instead of a bool[256] for a smaller
        /// footprint and cheaper per-char membership test in the hot loop.</summary>
        private readonly ulong[] _permChars = new ulong[4];

        /// <summary>True when "matching.permitted" restricted the set; chars
        /// above U+00FF (unrepresentable in the table) are then rejected.
        /// Without a restriction every char is permitted, like the C library's
        /// all-true byte table.</summary>
        public bool Restricted;

        public void FillAllPermChars() => _permChars[0] = _permChars[1] = _permChars[2] = _permChars[3] = ulong.MaxValue;

        public void ClearPermChars() => Array.Clear(_permChars);

        public void SetPermChar(int c) => _permChars[c >> 6] |= 1UL << (c & 63);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsPermChar(char c) => (_permChars[c >> 6] & (1UL << (c & 63))) != 0;
    }

    private static void AddPermittedCharArr(Data data, string chars)
    {
        foreach (var c in chars)
        {
            data.SetPermChar((byte)c);
        }
    }

    private static void AddPermittedFromTo(Data data, char from, char to)
    {
        for (int c = from; c <= to; ++c)
        {
            data.SetPermChar(c);
        }
    }

    private static void AddPermittedChars(Data data, JsonNode? val)
    {
        var s = JsonText.GetLenientString(val);
        if (s != null)
        {
            AddPermittedCharArr(data, s);
        }
    }

    private static void AddPermittedCharsViaArray(LogNormContext ctx, Data data, JsonArray arr)
    {
        foreach (var elem in arr)
        {
            if (elem is not JsonObject eobj)
            {
                continue;
            }

            foreach ((var key, var val) in eobj)
            {
                if (string.Equals(key, "chars", StringComparison.OrdinalIgnoreCase))
                {
                    AddPermittedChars(data, val);
                }
                else if (string.Equals(key, "class", StringComparison.OrdinalIgnoreCase))
                {
                    var optval = JsonText.GetLenientString(val) ?? string.Empty;
                    if (string.Equals(optval, "digit", StringComparison.OrdinalIgnoreCase))
                    {
                        AddPermittedCharArr(data, "0123456789");
                    }
                    else if (string.Equals(optval, "hexdigit", StringComparison.OrdinalIgnoreCase))
                    {
                        AddPermittedCharArr(data, "0123456789aAbBcCdDeEfF");
                    }
                    else if (string.Equals(optval, "alpha", StringComparison.OrdinalIgnoreCase))
                    {
                        AddPermittedFromTo(data, 'a', 'z');
                        AddPermittedFromTo(data, 'A', 'Z');
                    }
                    else if (string.Equals(optval, "alnum", StringComparison.OrdinalIgnoreCase))
                    {
                        AddPermittedCharArr(data, "0123456789");
                        AddPermittedFromTo(data, 'a', 'z');
                        AddPermittedFromTo(data, 'A', 'Z');
                    }
                    else
                    {
                        ctx.Error($"invalid character class '{optval}'");
                    }
                }
            }
        }
    }

    public static int Construct(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        var data = new Data();
        data.FillAllPermChars();

        foreach ((var key, var val) in config)
        {
            if (string.Equals(key, "quoting.mode", StringComparison.OrdinalIgnoreCase))
            {
                var optval = JsonText.GetLenientString(val) ?? string.Empty;
                if (string.Equals(optval, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    data.QuoteMode = QuoteMode.Auto;
                }
                else if (string.Equals(optval, "none", StringComparison.OrdinalIgnoreCase))
                {
                    data.QuoteMode = QuoteMode.None;
                }
                else if (string.Equals(optval, "required", StringComparison.OrdinalIgnoreCase))
                {
                    data.QuoteMode = QuoteMode.Required;
                }
                else
                {
                    ctx.Error($"invalid quoting.mode for string parser: {optval}");
                    pdata = null;
                    return ErrorCodes.BadConfig;
                }
            }
            else if (string.Equals(key, "quoting.escape.mode", StringComparison.OrdinalIgnoreCase))
            {
                var optval = JsonText.GetLenientString(val) ?? string.Empty;
                if (string.Equals(optval, "none", StringComparison.OrdinalIgnoreCase))
                {
                    data.EscMd = EscMode.None;
                }
                else if (string.Equals(optval, "backslash", StringComparison.OrdinalIgnoreCase))
                {
                    data.EscMd = EscMode.Backslash;
                }
                else if (string.Equals(optval, "double", StringComparison.OrdinalIgnoreCase))
                {
                    data.EscMd = EscMode.Double;
                }
                else if (string.Equals(optval, "both", StringComparison.OrdinalIgnoreCase))
                {
                    data.EscMd = EscMode.Both;
                }
                else
                {
                    ctx.Error($"invalid quoting.escape.mode for string parser: {optval}");
                    pdata = null;
                    return ErrorCodes.BadConfig;
                }
            }
            else if (string.Equals(key, "quoting.char.begin", StringComparison.OrdinalIgnoreCase))
            {
                var optval = JsonText.GetLenientString(val) ?? string.Empty;
                if (optval.Length != 1)
                {
                    ctx.Error($"quoting.char.begin must be exactly one character but is: '{optval}'");
                    pdata = null;
                    return ErrorCodes.BadConfig;
                }
                data.QCharBegin = optval[0];
            }
            else if (string.Equals(key, "quoting.char.end", StringComparison.OrdinalIgnoreCase))
            {
                var optval = JsonText.GetLenientString(val) ?? string.Empty;
                if (optval.Length != 1)
                {
                    ctx.Error($"quoting.char.end must be exactly one character but is: '{optval}'");
                    pdata = null;
                    return ErrorCodes.BadConfig;
                }
                data.QCharEnd = optval[0];
            }
            else if (string.Equals(key, "matching.permitted", StringComparison.OrdinalIgnoreCase))
            {
                data.ClearPermChars();
                data.Restricted = true;
                if (val is JsonValue sv && sv.TryGetValue(out string? _))
                {
                    AddPermittedChars(data, val);
                }
                else if (val is JsonArray arr)
                {
                    AddPermittedCharsViaArray(ctx, data, arr);
                }
                else
                {
                    ctx.Error($"matching.permitted is invalid object type, given as '{JsonText.ToCompactString(val)}");
                }
            }
            else if (string.Equals(key, "matching.mode", StringComparison.OrdinalIgnoreCase))
            {
                var optval = JsonText.GetLenientString(val) ?? string.Empty;
                if (string.Equals(optval, "strict", StringComparison.OrdinalIgnoreCase))
                {
                    data.Matching = MatchingMode.Exact;
                }
                else if (string.Equals(optval, "lazy", StringComparison.OrdinalIgnoreCase))
                {
                    data.Matching = MatchingMode.Lazy;
                }
                else
                {
                    ctx.Error($"invalid matching.mode for string parser: {optval}");
                    pdata = null;
                    return ErrorCodes.BadConfig;
                }
            }
            else if (string.Equals(key, "option.dashIsEmpty", StringComparison.OrdinalIgnoreCase))
            {
                if (val is JsonValue jv && jv.TryGetValue(out bool b))
                {
                    data.DashIsEmpty = b;
                }
                else
                {
                    ctx.Error($"option.dashIsEmpty is invalid object type, given as '{JsonText.ToCompactString(val)}'");
                    pdata = null;
                    return ErrorCodes.BadConfig;
                }
            }
            else
            {
                ctx.Error($"invalid param for hexnumber: {JsonText.ToCompactString(val)}");
            }
        }

        if (data.QuoteMode == QuoteMode.None)
        {
            data.EscMd = EscMode.None;
        }

        pdata = data;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Parse(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var data = (Data)pdata!;
        var s = npb.Str;
        var i = offs;
        var haveQuotes = false;
        var hadEndQuote = false;
        var hadEscape = false;

        if (i == npb.StrLen)
        {
            return ErrorCodes.WrongParser;
        }

        if (data.QuoteMode == QuoteMode.Auto && npb.Str[i] == data.QCharBegin)
        {
            haveQuotes = true;
            ++i;
        }
        else if (data.QuoteMode == QuoteMode.Required)
        {
            if (npb.Str[i] == data.QCharBegin)
            {
                haveQuotes = true;
                ++i;
            }
            else
            {
                return ErrorCodes.WrongParser;
            }
        }

        while (i < npb.StrLen)
        {
            if (haveQuotes && s[i] == data.QCharEnd)
            {
                if (data.EscMd == EscMode.Double || data.EscMd == EscMode.Both)
                {
                    /* may be escaped by doubling the quote char */
                    if (i + 1 < npb.StrLen && s[i + 1] == data.QCharEnd)
                    {
                        hadEscape = true;
                        ++i;
                    }
                    else
                    {
                        hadEndQuote = true;
                        break;
                    }
                }
                else
                {
                    hadEndQuote = true;
                    break;
                }
            }

            if (s[i] == '\\' && i + 1 < npb.StrLen
                && (data.EscMd == EscMode.Backslash || data.EscMd == EscMode.Both))
            {
                hadEscape = true;
                i++; /* skip esc char */
            }

            /* terminating conditions */
            if (!haveQuotes && s[i] == ' ')
            {
                break;
            }
            /* the table covers U+0000..U+00FF; a char above that range can
* never appear in a restricted set, and is permitted (like any
* byte in the C library's all-true table) in an unrestricted one */
            if (s[i] > 'ÿ' ? data.Restricted : !data.IsPermChar(s[i]))
            {
                break;
            }

            i++;
        }

        if (haveQuotes && !hadEndQuote)
        {
            return ErrorCodes.WrongParser;
        }

        if (i == offs)
        {
            return ErrorCodes.WrongParser;
        }

        if (i - offs < 1 || data.Matching == MatchingMode.Exact)
        {
            var trmChkIdx = haveQuotes ? i + 1 : i;
            if (trmChkIdx != npb.StrLen && npb.At(trmChkIdx) != ' ')
            {
                return ErrorCodes.WrongParser;
            }
        }

        parsed = i - offs;
        if (hadEndQuote)
        {
            ++parsed; /* skip quote */
        }

        if (wantValue)
        {
            if (data.DashIsEmpty)
            {
                var isQuotedDash = haveQuotes && parsed == 3 && s.AsSpan(offs, 3).SequenceEqual("\"-\"");
                var isBareDash = !haveQuotes && parsed == 1 && s[offs] == '-';
                if (isQuotedDash || isBareDash)
                {
                    value = JsonValue.Create(string.Empty);
                    return 0;
                }
            }

            int strt, len;
            if (haveQuotes && data.StripQuotes)
            {
                strt = offs + 1;
                len = parsed - 2; /* remove begin AND end quote */
            }
            else
            {
                strt = offs;
                len = parsed;
            }
            var cstr = s.Substring(strt, len);
            if (hadEscape)
            {
                cstr = Unescape(cstr, data);
            }

            value = JsonValue.Create(cstr);
        }
        return 0;
    }

    /// <summary>
    /// Remove escape characters from an extracted value. An escape leader
    /// (a backslash, or the first of a doubled quote char) is dropped and
    /// the character right after it is kept verbatim without being
    /// re-examined as a leader itself — i.e. leader/escaped pairs are
    /// consumed two chars at a time, not filtered independently. This
    /// matters for runs like an escaped backslash ("\\\\" -> "\\"): treating
    /// each backslash independently would wrongly drop both.
    /// </summary>
    private static string Unescape(string cstr, Data data)
    {
        var buffer = ArrayPool<char>.Shared.Rent(cstr.Length);
        try
        {
            var outLen = 0;
            var j = 0;
            while (j < cstr.Length)
            {
                var isDoubledQuote = cstr[j] == data.QCharEnd && j + 1 < cstr.Length && cstr[j + 1] == data.QCharEnd
                    && (data.EscMd == EscMode.Double || data.EscMd == EscMode.Both);
                var isBackslashLeader = cstr[j] == '\\' && (data.EscMd == EscMode.Backslash || data.EscMd == EscMode.Both);

                if (isDoubledQuote || isBackslashLeader)
                {
                    if (j + 1 < cstr.Length)
                    {
                        buffer[outLen++] = cstr[j + 1];
                        j += 2;
                    }
                    else
                    {
                        j++; /* trailing lone escape char; nothing follows to keep */
                    }
                }
                else
                {
                    buffer[outLen++] = cstr[j];
                    j++;
                }
            }
            return new string(buffer, 0, outLen);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}