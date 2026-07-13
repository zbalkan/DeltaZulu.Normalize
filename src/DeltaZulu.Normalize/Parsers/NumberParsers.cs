using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize.Parsers;

/// <summary>How extracted values are emitted into the JSON result.</summary>
internal enum FormatMode
{
    AsString = 0,
    AsNumber = 1,
    AsTimestampUnix = 2,
    AsTimestampUnixMs = 3,
}

internal static class NumberParsers
{
    internal sealed class NumberData
    {
        public long MaxVal;
        public FormatMode FmtMode = FormatMode.AsString;
    }

    public static int ConstructNumber(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        var data = new NumberData();
        foreach ((var key, var val) in config)
        {
            if (key == "maxval")
            {
                data.MaxVal = JsonText.GetLenientInt64(val);
            }
            else if (key == "format")
            {
                var fmtmode = JsonText.GetLenientString(val) ?? string.Empty;
                if (fmtmode == "number")
                {
                    data.FmtMode = FormatMode.AsNumber;
                }
                else if (fmtmode == "string")
                {
                    data.FmtMode = FormatMode.AsString;
                }
                else
                {
                    ctx.Error($"invalid value for number:format {fmtmode}");
                }
            }
            else if (!IsDashName(key, val))
            {
                ctx.Error($"invalid param for number: {key}");
            }
        }
        pdata = data;
        return 0;
    }

    /// <summary>An unnamed field ("-") leaves a name key in the config; constructs must tolerate it.</summary>
    internal static bool IsDashName(string key, JsonNode? val)
        => key == "name" && JsonText.GetLenientString(val) == "-";

    /// <summary>A decimal digit run (always held as 64 bit internally).</summary>
    public static int ParseNumber(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var data = (NumberData?)pdata;
        var fmtMode = data?.FmtMode ?? FormatMode.AsString;
        var maxval = data?.MaxVal ?? 0;

        long val = 0;
        int i;
        for (i = offs; i < npb.StrLen && TextRules.IsDigit(npb.Str[i]); i++)
        {
            val = unchecked(val * 10 + npb.Str[i] - '0');
        }

        if (maxval > 0 && val > maxval)
        {
            return ErrorCodes.WrongParser;
        }

        if (i == offs)
        {
            return ErrorCodes.WrongParser;
        }

        parsed = i - offs;
        if (wantValue)
        {
            value = fmtMode == FormatMode.AsString
                ? JsonValue.Create(npb.Str.Substring(offs, parsed))
                : JsonValue.Create(val);
        }
        return 0;
    }

    internal sealed class FloatData
    {
        public FormatMode FmtMode = FormatMode.AsString;
    }

    public static int ConstructFloat(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        var data = new FloatData();
        foreach ((var key, var val) in config)
        {
            if (key == "format")
            {
                var fmtmode = JsonText.GetLenientString(val) ?? string.Empty;
                if (fmtmode == "number")
                {
                    data.FmtMode = FormatMode.AsNumber;
                }
                else if (fmtmode == "string")
                {
                    data.FmtMode = FormatMode.AsString;
                }
                else
                {
                    ctx.Error($"invalid value for float:format {fmtmode}");
                }
            }
            else if (!IsDashName(key, val))
            {
                ctx.Error($"invalid param for float: {key}");
            }
        }
        pdata = data;
        return 0;
    }

    /// <summary>A real number in decimal floating-point form.</summary>
    public static int ParseFloat(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var data = (FloatData)pdata!;
        var i = offs;
        var isNeg = false;
        double val = 0;
        var seenPoint = false;
        double frac = 10;

        if (npb.At(i) == '-')
        {
            isNeg = true;
            i++;
        }
        for (; i < npb.StrLen; i++)
        {
            var c = npb.Str[i];
            if (c == '.')
            {
                if (seenPoint)
                {
                    break;
                }

                seenPoint = true;
            }
            else if (TextRules.IsDigit(c))
            {
                if (seenPoint)
                {
                    val += (c - '0') / frac;
                    frac *= 10;
                }
                else
                {
                    val = val * 10 + c - '0';
                }
            }
            else
            {
                break;
            }
        }
        if (i == offs)
        {
            return ErrorCodes.WrongParser;
        }

        if (isNeg)
        {
            val *= -1;
        }

        parsed = i - offs;
        if (wantValue)
        {
            var raw = npb.Str.Substring(offs, parsed);
            if (data.FmtMode == FormatMode.AsString)
            {
                value = JsonValue.Create(raw);
            }
            else
            {
                /* the C library serializes with the original text; keep the
                 * raw representation when it is valid JSON */
                value = IsValidJsonNumber(raw)
                    ? JsonNode.Parse(raw)
                    : JsonValue.Create(val);
            }
        }
        return 0;
    }

    /// <summary>
    /// Whether text scanned by ParseFloat ("-?" + digits with at most one '.')
    /// is a valid JSON number token: JSON forbids a bare sign, a missing
    /// integer part, a trailing '.', and leading zeros ("00.5", "01").
    /// </summary>
    private static bool IsValidJsonNumber(ReadOnlySpan<char> raw)
    {
        var i = raw[0] == '-' ? 1 : 0;
        if (i == raw.Length || raw[i] == '.')
        {
            return false;
        }

        if (raw[i] == '0' && i + 1 < raw.Length && raw[i + 1] != '.')
        {
            return false;
        }

        return raw[^1] != '.';
    }

    internal sealed class HexNumberData
    {
        public ulong MaxVal;
        public FormatMode FmtMode = FormatMode.AsString;
    }

    public static int ConstructHexNumber(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        var data = new HexNumberData();
        foreach ((var key, var val) in config)
        {
            if (key == "maxval")
            {
                data.MaxVal = unchecked((ulong)JsonText.GetLenientInt64(val));
            }
            else if (key == "format")
            {
                var fmtmode = JsonText.GetLenientString(val) ?? string.Empty;
                if (fmtmode == "number")
                {
                    data.FmtMode = FormatMode.AsNumber;
                }
                else if (fmtmode == "string")
                {
                    data.FmtMode = FormatMode.AsString;
                }
                else
                {
                    ctx.Error($"invalid value for hexnumber:format {fmtmode}");
                }
            }
            else if (!IsDashName(key, val))
            {
                ctx.Error($"invalid param for hexnumber: {key}");
            }
        }
        pdata = data;
        return 0;
    }

    /// <summary>
    /// A hex number: "0x" followed by hex digits, terminated by whitespace
    /// (which is required; a hex number at end-of-line does not match).
    /// </summary>
    public static int ParseHexNumber(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var data = (HexNumberData)pdata!;
        var i = offs;

        if (npb.At(i) != '0' || npb.At(i + 1) != 'x')
        {
            return ErrorCodes.WrongParser;
        }

        ulong val = 0;
        for (i += 2; i < npb.StrLen && TextRules.IsHexDigit(npb.Str[i]); i++)
        {
            val = unchecked(val * 16 + (ulong)TextRules.HexVal(npb.Str[i]));
        }

        if (!TextRules.IsSpace(npb.At(i)))
        {
            return ErrorCodes.WrongParser;
        }

        if (data.MaxVal > 0 && val > data.MaxVal)
        {
            return ErrorCodes.WrongParser;
        }

        parsed = i - offs;
        if (wantValue)
        {
            value = data.FmtMode == FormatMode.AsString
                ? JsonValue.Create(npb.Str.Substring(offs, parsed))
                : JsonValue.Create(unchecked((long)val));
        }
        return 0;
    }
}