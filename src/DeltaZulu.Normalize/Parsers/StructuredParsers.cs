using System.Text;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize.Parsers;

/// <summary>
/// Motifs for (semi-)structured payloads: embedded JSON, CEE syslog,
/// iptables logs, generic name=value lists, ArcSight CEF and Checkpoint LEA.
/// </summary>
internal static class StructuredParsers
{
    /* ---------- json ---------- */

    internal sealed class JsonData
    {
        public bool SkipEmpty;
    }

    public static int ConstructJson(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        pdata = null;
        if (config["extradata"] is not JsonValue ed)
        {
            return 0; /* no parameter */
        }

        var flag = JsonText.GetLenientString(ed);
        if (string.Equals(flag, "skipempty", StringComparison.OrdinalIgnoreCase))
        {
            pdata = new JsonData { SkipEmpty = true };
            return 0;
        }
        ctx.Error($"invalid flag for JSON parser: {flag}");
        return ErrorCodes.BadConfig;
    }

    /// <summary>
    /// Recursively drop empty strings, arrays and objects (and JSON nulls,
    /// which json-c represents as NULL). Returns null with empty=true when
    /// the whole value vanishes.
    /// </summary>
    private static (JsonNode? Node, bool Empty) PruneEmpty(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return (null, true);

            case JsonArray arr:
                {
                    var pruned = new JsonArray();
                    foreach (var elem in arr)
                    {
                        (var child, var empty) = PruneEmpty(elem);
                        if (!empty)
                        {
                            pruned.Add(child);
                        }
                    }
                    return pruned.Count == 0 ? (null, true) : (pruned, false);
                }
            case JsonObject obj:
                {
                    var pruned = new JsonObject();
                    foreach ((var key, var val) in obj)
                    {
                        (var child, var empty) = PruneEmpty(val);
                        if (!empty)
                        {
                            pruned[key] = child;
                        }
                    }
                    return pruned.Count == 0 ? (null, true) : (pruned, false);
                }
            case JsonValue v when v.TryGetValue(out string? s):
                return s.Length == 0 ? (null, true) : (v.DeepClone(), false);

            default:
                return (node.DeepClone(), false);
        }
    }

    /// <summary>
    /// Embedded JSON: parses one JSON value (object or array) out of the
    /// message; trailing data after it is permitted. Trailing whitespace is
    /// consumed, matching json-c tokener behaviour.
    /// </summary>
    public static int ParseJson(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var data = (JsonData?)pdata;
        var i = offs;

        if (i == npb.StrLen)
        {
            return ErrorCodes.WrongParser;
        }

        if (npb.Str[i] != '{' && npb.Str[i] != '[')
        {
            return ErrorCodes.WrongParser; /* cannot be JSON, RFC4627 Sect. 2 */
        }

        if (!JsonText.TryParseValue(npb.Str, i, out var json, out var consumed))
        {
            return ErrorCodes.WrongParser;
        }

        i += consumed;
        while (i < npb.StrLen && TextRules.IsSpace(npb.Str[i]))
        {
            ++i;
        }

        parsed = i - offs;

        if (wantValue)
        {
            if (data is { SkipEmpty: true })
            {
                (var prunedNode, var empty) = PruneEmpty(json);
                if (empty)
                {
                    return 0; /* value stays unset; field becomes JSON null */
                }

                json = prunedNode;
            }
            value = json;
        }
        return 0;
    }

    /* ---------- cee-syslog ---------- */

    /// <summary>
    /// CEE syslog: "@cee:" followed by exactly one JSON object that spans the
    /// entire remainder of the message.
    /// </summary>
    public static int ParseCeeSyslog(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var s = npb.Str;
        var i = offs;

        if (npb.StrLen < i + 7 /* "@cee:{}" is the minimum */
            || s[i] != '@' || s[i + 1] != 'c' || s[i + 2] != 'e' || s[i + 3] != 'e' || s[i + 4] != ':')
        {
            return ErrorCodes.WrongParser;
        }

        for (i += 5; i < npb.StrLen && TextRules.IsSpace(s[i]); ++i) { }

        if (i == npb.StrLen || s[i] != '{')
        {
            return ErrorCodes.WrongParser; /* note: arrays are not permitted in CEE mode */
        }

        if (!JsonText.TryParseValue(s, i, out var json, out var consumed))
        {
            return ErrorCodes.WrongParser;
        }

        if (i + consumed != npb.StrLen)
        {
            return ErrorCodes.WrongParser;
        }

        parsed = npb.StrLen;
        if (wantValue)
        {
            value = json;
        }

        return 0;
    }

    /* ---------- v2-iptables ---------- */

    /* the permitted name char set is deliberately slim (upper case only) so
     * the motif does not match random words like "DF" in ordinary text */

    private static bool IsValidIPTablesNameChar(char c) => c >= 'A' && c <= 'Z';

    private static bool ParseIPTablesNameValue(Npb npb, ref int offs, JsonObject? valroot)
    {
        var s = npb.Str;
        var i = offs;

        var iName = i;
        while (i < npb.StrLen && IsValidIPTablesNameChar(s[i]))
        {
            ++i;
        }

        if (i == iName || (i < npb.StrLen && s[i] != '=' && s[i] != ' '))
        {
            return false; /* no name at all */
        }

        var lenName = i - iName;

        var iVal = -1;
        var lenVal = 0;
        if (i < npb.StrLen && s[i] != ' ')
        {
            /* we have a real value (not just a flag name like "DF") */
            ++i; /* skip '=' */
            iVal = i;
            while (i < npb.StrLen && !TextRules.IsSpace(s[i]))
            {
                ++i;
            }

            lenVal = i - iVal;
        }

        offs = i;
        if (valroot != null)
        {
            valroot[s.Substring(iName, lenName)] =
                iVal == -1 ? null : JsonValue.Create(s.Substring(iVal, lenVal));
        }
        return true;
    }

    /// <summary>
    /// The structured part of iptables logs: two or more NAME[=value] fields
    /// separated by single spaces, running to end-of-message.
    /// </summary>
    public static int ParseV2IpTables(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var i = offs;
        var nfields = 0;

        /* stage one: detect only (extraction is expensive; mismatches are common) */
        while (i < npb.StrLen)
        {
            if (!ParseIPTablesNameValue(npb, ref i, null))
            {
                return ErrorCodes.WrongParser;
            }

            ++nfields;
            /* exactly one SP is permitted between fields */
            if (i < npb.StrLen && npb.Str[i] == ' ')
            {
                ++i;
            }
        }
        if (nfields < 2)
        {
            return ErrorCodes.WrongParser;
        }

        parsed = i - offs;

        /* stage two: extract */
        if (wantValue)
        {
            i = offs;
            var obj = new JsonObject();
            while (i < npb.StrLen)
            {
                if (!ParseIPTablesNameValue(npb, ref i, obj))
                {
                    return ErrorCodes.WrongParser;
                }

                while (i < npb.StrLen && TextRules.IsSpace(npb.Str[i]))
                {
                    ++i;
                }
            }
            value = obj;
        }
        return 0;
    }

    /* ---------- name-value-list ---------- */

    internal sealed class NameValueData
    {
        public char Sep;                 /* separator between pairs (0 = whitespace) */
        public char Ass;                 /* assignment char between key and value (0 = '=') */
        public bool IgnoreWhitespaces;   /* trim surrounding whitespace of keys/values */
    }

    public static int ConstructNameValue(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        pdata = null;
        var data = new NameValueData();

        foreach (var key in new[] { "extradata", "separator" })
        {
            if (config[key] is JsonValue val)
            {
                var s = JsonText.GetLenientString(val)!;
                if (s.Length != 1)
                {
                    ctx.Error($"name-value-list's '{key}' field should only be 1 character");
                    return ErrorCodes.BadConfig;
                }
                data.Sep = s[0];
            }
        }
        if (config["assignator"] is JsonValue assVal)
        {
            var s = JsonText.GetLenientString(assVal)!;
            if (s.Length != 1)
            {
                ctx.Error("name-value-list's 'assignator' field should only be 1 character");
                return ErrorCodes.BadConfig;
            }
            data.Ass = s[0];
        }
        if (config.TryGetPropertyValue("ignore_whitespaces", out var iw))
        {
            if (iw is JsonValue v && v.TryGetValue(out bool b))
            {
                data.IgnoreWhitespaces = b;
            }
            else
            {
                ctx.Error("name-value-list's 'ignore_whitespaces' field should be boolean");
                return ErrorCodes.BadConfig;
            }
        }
        pdata = data;
        return 0;
    }

    private static bool IsValidNameChar(char c)
        => TextRules.IsAlnum(c) || c == '.' || c == '_' || c == '-';

    /// <summary>Parse one name=value pair (values may be '"' or '\'' quoted; escapes stay raw).</summary>
    private static bool ParseNameValuePair(Npb npb, ref int offs, JsonObject? valroot,
        char sep, char ass, bool ignoreWs)
    {
        var s = npb.Str;
        var i = offs;

        if (ignoreWs)
        {
            while (i < npb.StrLen && TextRules.IsSpace(s[i]))
            {
                i++;
            }
        }

        var iName = i;
        /* with an explicit assignator, scan for it; otherwise validate name chars */
        while (i < npb.StrLen && (ass != 0 ? s[i] != ass : IsValidNameChar(s[i])))
        {
            ++i;
        }

        if (i == iName || (ass != 0 ? npb.At(i) != ass : npb.At(i) != '='))
        {
            return false; /* no name at all */
        }

        var lenName = i - iName;
        if (ignoreWs)
        {
            while (lenName > 0 && TextRules.IsSpace(s[iName + lenName - 1]))
            {
                lenName--;
            }
        }

        ++i; /* skip assignator */

        if (ignoreWs)
        {
            while (i < npb.StrLen && TextRules.IsSpace(s[i]))
            {
                i++;
            }
        }

        var quoting = i < npb.StrLen ? s[i] : '\0';
        if (i < npb.StrLen && (quoting == '"' || quoting == '\''))
        {
            i++;
        }
        else
        {
            quoting = '\0'; /* no quoting detected */
        }

        var iVal = i;
        var continuousBackslash = 0;
        if (quoting != '\0')
        {
            /* wait for an unescaped matching quote (odd backslash run = escaped) */
            while (i < npb.StrLen && (s[i] != quoting || continuousBackslash % 2 == 1))
            {
                if (s[i] == '\\')
                {
                    continuousBackslash++;
                }
                else
                {
                    continuousBackslash = 0;
                }

                ++i;
            }
        }
        else
        {
            /* scan until the (unescaped) separator: whitespace by default */
            while (i < npb.StrLen
                   && ((sep == 0 ? !TextRules.IsSpace(s[i]) : s[i] != sep)
                       || continuousBackslash % 2 == 1))
            {
                if (s[i] == '\\')
                {
                    continuousBackslash++;
                }
                else
                {
                    continuousBackslash = 0;
                }

                ++i;
            }
        }

        var iValEnd = i;

        /* skip the closing quote, if any */
        if (i < npb.StrLen && s[i] == quoting)
        {
            ++i;
        }
        else if (quoting != '\0')
        {
            return false;
        }

        var lenVal = quoting != '\0' ? iValEnd - iVal : i - iVal;
        if (ignoreWs && quoting == '\0')
        {
            while (lenVal > 0 && TextRules.IsSpace(s[iVal + lenVal - 1]))
            {
                lenVal--;
            }
        }

        offs = i;
        if (valroot != null)
        {
            valroot[s.Substring(iName, lenName)] = s.Substring(iVal, lenVal);
        }

        return true;
    }

    /// <summary>
    /// A list of name=value pairs running to end-of-message (or to the first
    /// non-pair text). Values may be quoted; separator/assignator/whitespace
    /// handling is configurable.
    /// </summary>
    public static int ParseNameValue(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        var data = (NameValueData?)pdata;
        var sep = data?.Sep ?? '\0';
        var ass = data?.Ass ?? '\0';
        var ignoreWs = data?.IgnoreWhitespaces ?? false;

        var i = RunNameValueStage(npb, offs, null, sep, ass, ignoreWs);
        parsed = i - offs;

        if (wantValue)
        {
            var obj = new JsonObject();
            RunNameValueStage(npb, offs, obj, sep, ass, ignoreWs);
            value = obj;
        }
        return 0;
    }

    private static int RunNameValueStage(Npb npb, int i, JsonObject? valroot,
        char sep, char ass, bool ignoreWs)
    {
        while (i < npb.StrLen)
        {
            if (!ParseNameValuePair(npb, ref i, valroot, sep, ass, ignoreWs))
            {
                break;
            }

            if (ignoreWs && sep != 0)
            {
                while (i < npb.StrLen && TextRules.IsSpace(npb.Str[i]))
                {
                    ++i;
                }
            }
            /* require at least one separator after the value */
            if (i < npb.StrLen && !(sep == 0 ? TextRules.IsSpace(npb.Str[i]) : npb.Str[i] == sep))
            {
                break;
            }

            while (i < npb.StrLen && (sep == 0 ? TextRules.IsSpace(npb.Str[i]) : npb.Str[i] == sep))
            {
                ++i;
            }
        }
        return i;
    }

    /* ---------- cef ---------- */

    /// <summary>
    /// Find the end of a CEF extension value: scan for the next unquoted '='
    /// and back up to the beginning of the word in front of it (which is the
    /// next extension's name).
    /// </summary>
    private static bool CefParseExtensionValue(Npb npb, ref int iEndVal)
    {
        var s = npb.Str;
        var i = iEndVal;
        var iLastWordBegin = 0;
        var hadSP = false;
        var inEscape = false;
        for (; i < npb.StrLen; ++i)
        {
            if (inEscape)
            {
                if (s[i] != '=' && s[i] != '\\' && s[i] != 'r' && s[i] != 'n' && s[i] != '/')
                {
                    return false;
                }

                inEscape = false;
            }
            else if (s[i] == '=')
            {
                break;
            }
            else if (s[i] == '\\')
            {
                inEscape = true;
            }
            else if (s[i] == ' ')
            {
                hadSP = true;
            }
            else if (hadSP)
            {
                iLastWordBegin = i;
                hadSP = false;
            }
        }

        /* a dangling escape at end-of-string (no char to complete it) would
         * otherwise let the caller index one past the value below */
        if (inEscape)
        {
            return false;
        }

        /* iLastWordBegin can never be offset zero: the CEF header starts there */
        if (i < npb.StrLen)
        {
            iEndVal = iLastWordBegin == 0 ? i : iLastWordBegin - 1;
        }
        else
        {
            iEndVal = i;
        }

        return true;
    }

    /// <summary>Scan an extension name; ArcSight also uses '_' and '.' despite the spec.</summary>
    private static bool CefParseName(Npb npb, ref int i)
    {
        var s = npb.Str;
        while (i < npb.StrLen && s[i] != '=')
        {
            if (!(TextRules.IsAlnum(s[i]) || s[i] == '_' || s[i] == '.'))
            {
                return false;
            }

            ++i;
        }
        return true;
    }

    /// <summary>
    /// Parse CEF extensions: name=value pairs whose values may contain
    /// unquoted spaces, requiring a look-ahead for the next name.
    /// </summary>
    private static bool CefParseExtensions(Npb npb, ref int offs, JsonObject? jroot)
    {
        var s = npb.Str;
        var i = offs;

        while (i < npb.StrLen)
        {
            while (i < npb.StrLen && s[i] == ' ')
            {
                ++i;
            }

            var iName = i;
            if (!CefParseName(npb, ref i))
            {
                return false;
            }

            if (npb.At(i) != '=')
            {
                return false;
            }

            var lenName = i - iName;

            var iValue = i;
            var lenValue = 0;
            if (i < npb.StrLen)
            {
                ++i; /* skip '=' */
                iValue = i;
                if (!CefParseExtensionValue(npb, ref i))
                {
                    return false;
                }

                lenValue = i - iValue;
                ++i; /* skip past value */
            }

            if (jroot != null)
            {
                var sb = new StringBuilder(lenValue);
                for (var iSrc = 0; iSrc < lenValue; ++iSrc)
                {
                    var c = s[iValue + iSrc];
                    if (c == '\\')
                    {
                        ++iSrc; /* the next char is known to exist */
                        sb.Append(s[iValue + iSrc] switch {
                            '=' => '=',
                            'n' => '\n',
                            'r' => '\r',
                            '\\' => '\\',
                            '/' => '/',
                            _ => '\0', /* cannot happen: validated above */
                        });
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                jroot[s.Substring(iName, lenName)] = sb.ToString();
            }
        }

        offs = npb.StrLen; /* this parser consumes everything or fails */
        return true;
    }

    /// <summary>Get one '|'-delimited CEF header field, handling "\|" and "\\" escapes.</summary>
    private static bool CefGetHdrField(Npb npb, ref int offs, out string? val, bool want)
    {
        val = null;
        var s = npb.Str;
        var i = offs;
        while (i < npb.StrLen && s[i] != '|')
        {
            if (s[i] == '\\')
            {
                ++i; /* skip escape char */
                if (npb.At(i) != '\\' && npb.At(i) != '|')
                {
                    return false;
                }
            }
            ++i;
        }
        if (npb.At(i) != '|')
        {
            return false;
        }

        var iBegin = offs;
        offs = i + 1;
        if (!want)
        {
            return true;
        }

        var len = i - iBegin;
        var sb = new StringBuilder(len);
        for (var iSrc = 0; iSrc < len; ++iSrc)
        {
            if (s[iBegin + iSrc] == '\\')
            {
                ++iSrc; /* validated above */
            }

            sb.Append(s[iBegin + iSrc]);
        }
        val = sb.ToString();
        return true;
    }

    /// <summary>ArcSight Common Event Format (CEF) version 0.</summary>
    public static int ParseCef(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var s = npb.Str;
        var i = offs;

        /* minimum header: "CEF:0|x|x|x|x|x|x|" --> 17 chars */
        if (npb.StrLen < i + 17
            || s[i] != 'C' || s[i + 1] != 'E' || s[i + 2] != 'F' || s[i + 3] != ':'
            || s[i + 4] != '0' || s[i + 5] != '|')
        {
            return ErrorCodes.WrongParser;
        }

        i += 6; /* position after '|' */

        if (!CefGetHdrField(npb, ref i, out var vendor, wantValue))
        {
            return ErrorCodes.WrongParser;
        }

        if (!CefGetHdrField(npb, ref i, out var product, wantValue))
        {
            return ErrorCodes.WrongParser;
        }

        if (!CefGetHdrField(npb, ref i, out var version, wantValue))
        {
            return ErrorCodes.WrongParser;
        }

        if (!CefGetHdrField(npb, ref i, out var sigID, wantValue))
        {
            return ErrorCodes.WrongParser;
        }

        if (!CefGetHdrField(npb, ref i, out var name, wantValue))
        {
            return ErrorCodes.WrongParser;
        }

        if (!CefGetHdrField(npb, ref i, out var severity, wantValue))
        {
            return ErrorCodes.WrongParser;
        }

        while (i < npb.StrLen && s[i] == ' ') /* skip leading SP */
        {
            ++i;
        }

        /* first validate the extensions, then extract on the second pass */
        var iBeginExtensions = i;
        if (!CefParseExtensions(npb, ref i, null))
        {
            return ErrorCodes.WrongParser;
        }

        parsed = i - offs;

        if (wantValue)
        {
            var obj = new JsonObject {
                ["DeviceVendor"] = vendor,
                ["DeviceProduct"] = product,
                ["DeviceVersion"] = version,
                ["SignatureID"] = sigID,
                ["Name"] = name,
                ["Severity"] = severity,
            };
            var jext = new JsonObject();
            obj["Extensions"] = jext;
            var iExt = iBeginExtensions;
            CefParseExtensions(npb, ref iExt, jext);
            value = obj;
        }
        return 0;
    }

    /* ---------- checkpoint-lea ---------- */

    internal sealed class CheckpointLeaData
    {
        public char Terminator; /* '\0' = none */
    }

    public static int ConstructCheckpointLea(LogNormContext ctx, JsonObject config, out object? pdata)
    {
        var data = new CheckpointLeaData();
        if (config["terminator"] is JsonValue val)
        {
            var optval = JsonText.GetLenientString(val)!;
            if (optval.Length != 1)
            {
                ctx.Error($"terminator must be exactly one character but is: '{optval}'");
                pdata = null;
                return ErrorCodes.BadConfig;
            }
            data.Terminator = optval[0];
        }
        pdata = data;
        return 0;
    }

    /// <summary>Checkpoint LEA on-disk format: "name: value;" pairs.</summary>
    public static int ParseCheckpointLea(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var data = (CheckpointLeaData)pdata!;
        var s = npb.Str;
        var i = offs;
        var foundFields = 0;
        JsonObject? obj = null;

        while (i < npb.StrLen)
        {
            while (i < npb.StrLen && s[i] == ' ') /* skip leading SP */
            {
                ++i;
            }

            if (i == npb.StrLen)
            {
                /* trailing space is OK */
                if (foundFields == 0)
                {
                    return ErrorCodes.WrongParser;
                }

                break;
            }
            ++foundFields;

            var iName = i;
            if (i < npb.StrLen && s[i] == data.Terminator)
            {
                break;
            }

            while (i < npb.StrLen && s[i] != ':')
            {
                ++i;
            }

            if (i + 1 >= npb.StrLen || s[i] != ':')
            {
                return ErrorCodes.WrongParser;
            }
            /* sometimes there are multiple colons */
            while (i + 1 < npb.StrLen && s[i + 1] == ':')
            {
                i++;
            }

            var lenName = i - iName;
            ++i; /* skip ':' */

            while (i < npb.StrLen && s[i] == ' ')
            {
                ++i;
            }

            if (i == npb.StrLen)
            {
                return ErrorCodes.WrongParser;
            }

            int iValue;
            int lenValue;
            if (s[i] == '"')
            {
                /* quoted value; a quote preceded by an odd backslash run is escaped */
                var continuousBackslash = 0;
                iValue = i + 1;
                i++;
                while (i < npb.StrLen && (s[i] != '"' || (continuousBackslash & 1) == 1))
                {
                    if (s[i] == '\\')
                    {
                        ++continuousBackslash;
                    }
                    else
                    {
                        continuousBackslash = 0;
                    }

                    ++i;
                }
                lenValue = i - iValue; /* the quotes are not part of the value */
                if (i == npb.StrLen)
                {
                    return ErrorCodes.WrongParser;
                }

                ++i; /* skip '"' */
            }
            else
            {
                iValue = i;
                while (i < npb.StrLen && s[i] != ';' && s[i] != data.Terminator)
                {
                    ++i;
                }

                lenValue = i - iValue;
                while (lenValue > 0 && s[iValue + lenValue - 1] == ' ')
                {
                    --lenValue;
                }
            }

            while (i < npb.StrLen && s[i] == ' ')
            {
                ++i;
            }

            if (i >= npb.StrLen || (s[i] != ';' && s[i] != data.Terminator))
            {
                return ErrorCodes.WrongParser;
            }

            if (s[i] == ';')
            {
                ++i; /* skip ';' */
            }

            if (wantValue)
            {
                obj ??= new JsonObject();
                obj[s.Substring(iName, lenName)] = s.Substring(iValue, lenValue);
            }
        }

        parsed = i - offs;
        if (wantValue && obj != null)
        {
            value = obj;
        }

        return 0;
    }
}