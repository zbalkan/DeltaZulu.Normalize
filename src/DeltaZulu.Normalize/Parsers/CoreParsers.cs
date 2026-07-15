using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize.Parsers;

/// <summary>
/// Simple text motifs: whitespace, word, alpha, rest, string-to, char-to,
/// char-sep and the quoted-string variants. Scans use the vectorized span
/// search primitives (IndexOf/IndexOfAny/IndexOfAnyExcept).
/// </summary>
internal static class CoreParsers
{
    private static readonly SearchValues<char> AlphaChars =
            SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>The C-locale isspace() set (see TextRules.IsSpace).</summary>
    private static readonly SearchValues<char> SpaceChars = SearchValues.Create(" \t\n\v\f\r");

    public static int ConstructCharSeparated(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        pdata = null;
        if (config["extradata"] is not JsonValue ed)
        {
            ctx.Error("char-separated type needs 'extradata' parameter");
            return ErrorCodes.BadConfig;
        }
        var termChars = JsonText.GetLenientString(ed)!;
        pdata = new CharSeparatedData { TermText = termChars, TermChars = SearchValues.Create(termChars) };
        return 0;
    }

    public static int ConstructCharTo(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        pdata = null;
        if (config["extradata"] is not JsonValue ed)
        {
            ctx.Error("char-to type needs 'extradata' parameter");
            return ErrorCodes.BadConfig;
        }
        var termChars = JsonText.GetLenientString(ed)!;
        pdata = new CharToData { TermText = termChars, TermChars = SearchValues.Create(termChars) };
        return 0;
    }

    public static int ConstructOpQuotedString(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        pdata = null;
        var data = new OpQuotedStringData();
        if (config.TryGetPropertyValue("escape", out var obj))
        {
            if (obj is JsonValue v && v.TryGetValue(out bool b))
            {
                data.Escape = b;
            }
            else
            {
                ctx.Error("op-quoted-string's 'escape' field should be boolean");
                return ErrorCodes.BadConfig;
            }
        }
        pdata = data;
        return 0;
    }

    public static int ConstructStringTo(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        pdata = null;
        if (config["extradata"] is not JsonValue ed)
        {
            ctx.Error("string-to type needs 'extradata' parameter");
            return ErrorCodes.BadConfig;
        }
        var toFind = JsonText.GetLenientString(ed)!;
        if (toFind.Length == 0)
        {
            ctx.Error("string-to type needs non-empty 'extradata' parameter");
            return ErrorCodes.BadConfig;
        }
        pdata = new StringToData { ToFind = toFind };
        return 0;
    }

    /// <summary>A run of alphabetic characters.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ParseAlpha(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        var rem = npb.Str.AsSpan(offs);
        var idx = rem.IndexOfAnyExcept(AlphaChars);
        parsed = idx < 0 ? rem.Length : idx;
        if (parsed == 0)
        {
            return ErrorCodes.WrongParser;
        }

        if (wantValue)
        {
            value = JsonValue.Create(npb.Str.Substring(offs, parsed));
        }

        return 0;
    }

    /// <summary>Everything up to a terminator char or end-of-string; always succeeds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ParseCharSeparated(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        var data = (CharSeparatedData)pdata!;
        var rem = npb.Str.AsSpan(offs);
        var idx = rem.IndexOfAny(data.TermChars);
        parsed = idx < 0 ? rem.Length : idx;
        if (wantValue)
        {
            value = JsonValue.Create(npb.Str.Substring(offs, parsed));
        }

        return 0;
    }

    /// <summary>Everything up to one of a set of terminator characters, which must be present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ParseCharTo(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var data = (CharToData)pdata!;
        var idx = npb.Str.AsSpan(offs).IndexOfAny(data.TermChars);
        if (idx <= 0) /* the terminator must exist, beyond a non-empty prefix */
        {
            return ErrorCodes.WrongParser;
        }

        parsed = idx;
        if (wantValue)
        {
            value = JsonValue.Create(npb.Str.Substring(offs, parsed));
        }

        return 0;
    }

    /// <summary>
    /// An optionally quoted string: either a space-delimited word, or a
    /// double-quoted string (quotes stripped; escape handling per config).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ParseOpQuotedString(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var escape = pdata is OpQuotedStringData d && d.Escape;
        var i = offs;
        string cstr;

        if (i == npb.StrLen)
        {
            return ErrorCodes.WrongParser;
        }

        if (npb.Str[i] != '"')
        {
            var idxSp = npb.Str.AsSpan(offs).IndexOf(' ');
            i = idxSp < 0 ? npb.StrLen : offs + idxSp;
            if (i == offs)
            {
                return ErrorCodes.WrongParser;
            }

            parsed = i - offs;
            cstr = npb.Str.Substring(offs, parsed);
        }
        else if (escape)
        {
            ++i;
            /* the closing quote must not be escaped; a backslash escapes
             * itself, so only an odd run of backslashes escapes the quote */
            var continuousBackslash = 0;
            while (i < npb.StrLen && (npb.Str[i] != '"' || (continuousBackslash & 1) == 1))
            {
                if (npb.Str[i] == '\\')
                {
                    continuousBackslash++;
                }
                else
                {
                    continuousBackslash = 0;
                }

                ++i;
            }
            if (i == npb.StrLen || npb.Str[i] != '"')
            {
                return ErrorCodes.WrongParser;
            }

            var end = i;
            i = offs + 1; /* eat starting quote */
            var sb = new StringBuilder(end - i);
            while (i < end)
            {
                if (npb.Str[i] == '\\' && (npb.At(i + 1) == '\\' || npb.At(i + 1) == '"'))
                {
                    i++;
                }

                sb.Append(npb.Str[i++]);
            }
            cstr = sb.ToString();
            parsed = i + 1 - offs; /* "eat" terminal double quote */
        }
        else
        {
            var idxQ = npb.Str.AsSpan(offs + 1).IndexOf('"');
            if (idxQ < 0)
            {
                return ErrorCodes.WrongParser;
            }

            parsed = idxQ + 2;
            cstr = npb.Str.Substring(offs + 1, parsed - 2);
        }

        if (wantValue)
        {
            value = JsonValue.Create(cstr);
        }

        return 0;
    }

    /// <summary>A double-quoted string without escape support; quotes are stripped.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ParseQuotedString(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        if (offs + 2 > npb.StrLen || npb.Str[offs] != '"')
        {
            return ErrorCodes.WrongParser;
        }

        var idx = npb.Str.AsSpan(offs + 1).IndexOf('"');
        if (idx < 0)
        {
            return ErrorCodes.WrongParser;
        }

        parsed = idx + 2; /* both quotes are consumed */
        if (parsed == 3 && npb.Str[offs + 1] == '-')
        {
            return ErrorCodes.WrongParser;
        }

        if (wantValue)
        {
            value = JsonValue.Create(npb.Str.Substring(offs + 1, parsed - 2));
        }

        return 0;
    }

    /// <summary>Everything to end-of-string; always succeeds (even consuming zero chars).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ParseRest(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = npb.StrLen - offs;
        if (wantValue)
        {
            value = JsonValue.Create(npb.Str.Substring(offs, parsed));
        }

        return 0;
    }

    /// <summary>Everything up to a specific search string.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ParseStringTo(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var toFind = ((StringToData)pdata!).ToFind;

        /* the C scanner's inner loop needs a char *after* toFind[0] to ever
         * set the found flag, so a single-char search string never matches;
         * kept faithfully. The match can also never start at offs (the C
         * loop increments before comparing). */
        if (toFind.Length < 2 || offs >= npb.StrLen)
        {
            return ErrorCodes.WrongParser;
        }

        var idx = npb.Str.IndexOf(toFind, offs + 1, StringComparison.Ordinal);
        if (idx < 0)
        {
            return ErrorCodes.WrongParser;
        }

        parsed = idx - offs;
        if (wantValue)
        {
            value = JsonValue.Create(npb.Str.Substring(offs, parsed));
        }

        return 0;
    }

    /// <summary>All whitespace up to the first non-whitespace char; must start on whitespace.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ParseWhitespace(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        if (!TextRules.IsSpace(npb.At(offs)))
        {
            return ErrorCodes.WrongParser;
        }

        var rem = npb.Str.AsSpan(offs + 1);
        var idx = rem.IndexOfAnyExcept(SpaceChars);
        parsed = 1 + (idx < 0 ? rem.Length : idx);
        if (wantValue)
        {
            value = JsonValue.Create(npb.Str.Substring(offs, parsed));
        }

        return 0;
    }

    /// <summary>A space-delimited entity (fails only when positioned on a space).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ParseWord(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        var rem = npb.Str.AsSpan(offs);
        var idx = rem.IndexOf(' ');
        parsed = idx < 0 ? rem.Length : idx;
        if (parsed == 0)
        {
            return ErrorCodes.WrongParser;
        }

        if (wantValue)
        {
            value = JsonValue.Create(npb.Str.Substring(offs, parsed));
        }

        return 0;
    }

    /* ---------- string-to ---------- */

    internal sealed class CharSeparatedData
    {
        public required string TermText { get; init; }
        public required SearchValues<char> TermChars { get; init; }
    }

    internal sealed class CharToData
    {
        public required string TermText { get; init; }
        public required SearchValues<char> TermChars { get; init; }
    }

    internal sealed class OpQuotedStringData
    {
        public bool Escape;
    }

    internal sealed class StringToData
    {
        public required string ToFind { get; init; }
    }

    /* ---------- char-to ---------- */
    /* ---------- char-sep ---------- */
    /* ---------- quoted strings ---------- */
}
