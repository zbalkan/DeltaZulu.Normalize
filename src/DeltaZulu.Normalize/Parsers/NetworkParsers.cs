using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize.Parsers;

/// <summary>Network address motifs: ipv4, ipv6, mac48 and cisco-interface-spec.</summary>
internal static class NetworkParsers
{
    /// <summary>
    /// A Cisco ASA interface spec: [interface:]ip/port [SP (ip2/port2)] [[SP](username)].
    /// </summary>
    public static int ParseCiscoInterfaceSpec(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var s = npb.Str;
        var i = offs;

        if (npb.At(i) == ':' || TextRules.IsSpace(npb.At(i)) || i >= npb.StrLen)
        {
            return ErrorCodes.WrongParser;
        }

        /* if the spec starts with an IP there is no interface name */
        var haveInterface = false;
        int idxInterface = 0, lenInterface = 0;
        var haveIP = false;
        var idxIP = i;
        int lenIP;
        if (MatchIPv4(npb, i, out lenIP))
        {
            haveIP = true;
            i += lenIP - 1; /* position on delimiter */
        }
        else
        {
            idxInterface = i;
            while (i < npb.StrLen)
            {
                if (TextRules.IsSpace(s[i]))
                {
                    return ErrorCodes.WrongParser;
                }

                if (s[i] == ':')
                {
                    break;
                }

                ++i;
            }
            lenInterface = i - idxInterface;
            haveInterface = true;
        }
        if (i == npb.StrLen)
        {
            return ErrorCodes.WrongParser;
        }

        ++i; /* skip colon */

        if (!haveIP)
        {
            idxIP = i;
            if (!MatchIPv4(npb, i, out lenIP))
            {
                return ErrorCodes.WrongParser;
            }

            i += lenIP;
        }
        if (i == npb.StrLen || s[i] != '/')
        {
            return ErrorCodes.WrongParser;
        }

        ++i; /* skip slash */
        var idxPort = i;
        if (!MatchNumber(npb, i, out var lenPort))
        {
            return ErrorCodes.WrongParser;
        }

        i += lenPort;

        /* optional second ip/port; need at least 5 chars: " (::1)" */
        var haveIP2 = false;
        int idxIP2 = 0, lenIP2 = 0, idxPort2 = 0, lenPort2 = 0;
        if (i + 5 < npb.StrLen && s[i] == ' ' && s[i + 1] == '(')
        {
            var iTmp = i + 2; /* skip over " (" */
            idxIP2 = iTmp;
            if (MatchIPv4(npb, iTmp, out lenIP2))
            {
                iTmp += lenIP2;
                /* faithful port of the C library, which (buggily) skips one
                 * char here without verifying it is '/' */
                if (i < npb.StrLen || npb.At(iTmp) == '/')
                {
                    ++iTmp; /* skip slash */
                    idxPort2 = iTmp;
                    if (MatchNumber(npb, iTmp, out lenPort2))
                    {
                        iTmp += lenPort2;
                        if (iTmp < npb.StrLen && s[iTmp] == ')')
                        {
                            i = iTmp + 1; /* match, use new index */
                            haveIP2 = true;
                        }
                    }
                }
            }
        }

        /* optional username; need at least 3 chars: "(n)" */
        var haveUser = false;
        int idxUser = 0, lenUser = 0;
        if ((i + 2 < npb.StrLen && s[i] == '(' && !TextRules.IsSpace(s[i + 1]))
            || (i + 3 < npb.StrLen && s[i] == ' ' && s[i + 1] == '(' && !TextRules.IsSpace(s[i + 2])))
        {
            idxUser = i + (s[i] == ' ' ? 2 : 1); /* skip [SP]'(' */
            var iTmp = idxUser;
            while (iTmp < npb.StrLen && !TextRules.IsSpace(s[iTmp]) && s[iTmp] != ')')
            {
                ++iTmp; /* just scan */
            }

            if (iTmp < npb.StrLen && s[iTmp] == ')')
            {
                i = iTmp + 1; /* we have a match, use new index */
                haveUser = true;
                lenUser = iTmp - idxUser;
            }
        }

        if (wantValue)
        {
            var obj = new JsonObject();
            if (haveInterface)
            {
                obj["interface"] = s.Substring(idxInterface, lenInterface);
            }

            obj["ip"] = s.Substring(idxIP, lenIP);
            obj["port"] = s.Substring(idxPort, lenPort);
            if (haveIP2)
            {
                obj["ip2"] = s.Substring(idxIP2, lenIP2);
                obj["port2"] = s.Substring(idxPort2, lenPort2);
            }
            if (haveUser)
            {
                obj["user"] = s.Substring(idxUser, lenUser);
            }

            value = obj;
        }

        parsed = i - offs;
        return 0;
    }

    public static int ParseIPv4(Npb npb, ref int offs, object? pdata, string? parserName,
            out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var i = offs;
        if (i + 7 > npb.StrLen)
        {
            return ErrorCodes.WrongParser; /* an IPv4 addr requires at least 7 chars */
        }

        if (!CheckIPv4AddrByte(npb, ref i))
        {
            return ErrorCodes.WrongParser;
        }

        if (i == npb.StrLen || npb.Str[i++] != '.')
        {
            return ErrorCodes.WrongParser;
        }

        if (!CheckIPv4AddrByte(npb, ref i))
        {
            return ErrorCodes.WrongParser;
        }

        if (i == npb.StrLen || npb.Str[i++] != '.')
        {
            return ErrorCodes.WrongParser;
        }

        if (!CheckIPv4AddrByte(npb, ref i))
        {
            return ErrorCodes.WrongParser;
        }

        if (i == npb.StrLen || npb.Str[i++] != '.')
        {
            return ErrorCodes.WrongParser;
        }

        if (!CheckIPv4AddrByte(npb, ref i))
        {
            return ErrorCodes.WrongParser;
        }

        parsed = i - offs;
        if (wantValue)
        {
            value = JsonValue.Create(npb.Str.Substring(offs, parsed));
        }

        return 0;
    }

    /// <summary>
    /// An IPv6 address per RFC4291 Section 2.2, incl. "::" abbreviation and
    /// embedded IPv4 tails.
    /// </summary>
    public static int ParseIPv6(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var s = npb.Str;
        var i = offs;
        var beginBlock = i;
        var hasIPv4 = false;
        var nBlocks = 0;
        var had0Abbrev = false;

        if (i + 2 > npb.StrLen)
        {
            return ErrorCodes.WrongParser; /* needs at least "::" */
        }

        /* first block must be non-empty */
        if (!(TextRules.IsHexDigit(s[i]) || (s[i] == ':' && s[i + 1] == ':')))
        {
            return ErrorCodes.WrongParser;
        }

        /* try for all potential blocks plus one more (so we see errors) */
        for (var j = 0; j < 9; ++j)
        {
            beginBlock = i;
            if (!SkipIPv6AddrBlock(npb, ref i))
            {
                return ErrorCodes.WrongParser;
            }

            nBlocks++;
            if (i == npb.StrLen)
            {
                goto chk_ok;
            }

            if (s[i] != ':' && s[i] != '.')
            {
                goto chk_ok;
            }

            if (s[i] == '.')
            {
                hasIPv4 = true;
                break;
            }
            if (nBlocks == 8)
            {
                goto chk_ok;
            }

            i++; /* eat ':' */
            if (i == npb.StrLen)
            {
                goto chk_ok;
            }

            if (had0Abbrev)
            {
                if (s[i] == ':')
                {
                    return ErrorCodes.WrongParser;
                }
            }
            else if (s[i] == ':')
            {
                had0Abbrev = true;
                ++i;
                if (i == npb.StrLen)
                {
                    goto chk_ok;
                }
            }
        }

        if (hasIPv4)
        {
            --nBlocks;
            /* prevent a pure IPv4 address from being recognized */
            if (beginBlock == offs)
            {
                return ErrorCodes.WrongParser;
            }

            i = beginBlock;
            if (!MatchIPv4(npb, i, out var ipv4Parsed))
            {
                return ErrorCodes.WrongParser;
            }

            i += ipv4Parsed;
        }

    chk_ok:
        if (nBlocks > 8)
        {
            return ErrorCodes.WrongParser;
        }

        if (had0Abbrev && nBlocks >= 8)
        {
            return ErrorCodes.WrongParser;
        }
        /* check for a missing trailing block; two chars are always present here */
        if (s[i - 1] == ':' && s[i - 2] != ':')
        {
            return ErrorCodes.WrongParser;
        }

        parsed = i - offs;
        if (wantValue)
        {
            value = JsonValue.Create(npb.Str.Substring(offs, parsed));
        }

        return 0;
    }

    /// <summary>An IEEE 802 MAC-48 address: six hex byte groups joined by ':' or '-'.</summary>
    public static int ParseMac48(Npb npb, ref int offs, object? pdata, string? parserName,
        out int parsed, bool wantValue, ref JsonNode? value)
    {
        parsed = 0;
        var s = npb.Str;
        var i = offs;

        if (npb.StrLen < i + 17 /* this motif has exactly 17 chars */
            || !TextRules.IsHexDigit(s[i]) || !TextRules.IsHexDigit(s[i + 1]))
        {
            return ErrorCodes.WrongParser;
        }

        var delim = s[i + 2];
        if (delim != ':' && delim != '-')
        {
            return ErrorCodes.WrongParser;
        }

        for (var b = 1; b < 6; ++b)
        {
            var g = i + (b * 3);
            if (!TextRules.IsHexDigit(s[g]) || !TextRules.IsHexDigit(s[g + 1]))
            {
                return ErrorCodes.WrongParser;
            }

            if (b < 5 && s[g + 2] != delim)
            {
                return ErrorCodes.WrongParser;
            }
        }

        parsed = 17;
        if (wantValue)
        {
            value = JsonValue.Create(npb.Str.Substring(offs, 17));
        }

        return 0;
    }

    /// <summary>Match an IPv4 address at i without extracting; used by ipv6 and cisco.</summary>
    internal static bool MatchIPv4(Npb npb, int i, out int len)
    {
        var offs = i;
        JsonNode? dummy = null;
        var r = ParseIPv4(npb, ref offs, null, null, out len, wantValue: false, ref dummy);
        return r == 0;
    }

    /// <summary>Check one dotted-quad byte: 1-3 digits, value at most 255.</summary>
    private static bool CheckIPv4AddrByte(Npb npb, ref int offs)
    {
        var i = offs;
        if (i == npb.StrLen || !TextRules.IsDigit(npb.Str[i]))
        {
            return false;
        }

        var val = npb.Str[i++] - '0';
        if (i < npb.StrLen && TextRules.IsDigit(npb.Str[i]))
        {
            val = (val * 10) + npb.Str[i++] - '0';
            if (i < npb.StrLen && TextRules.IsDigit(npb.Str[i]))
            {
                val = (val * 10) + npb.Str[i++] - '0';
            }
        }
        if (val > 255)
        {
            return false;
        }

        offs = i;
        return true;
    }

    /// <summary>Match a plain digit run (number parser without config), used by cisco.</summary>
    private static bool MatchNumber(Npb npb, int i, out int len)
    {
        var offs = i;
        JsonNode? dummy = null;
        var r = NumberParsers.ParseNumber(npb, ref offs, null, null, out len, wantValue: false, ref dummy);
        return r == 0;
    }

    /// <summary>Skip past one IPv6 address block (up to 4 hex digits).</summary>
    private static bool SkipIPv6AddrBlock(Npb npb, ref int offs)
    {
        if (offs == npb.StrLen)
        {
            return false;
        }

        int j;
        for (j = 0; j < 4 && offs + j < npb.StrLen && TextRules.IsHexDigit(npb.Str[offs + j]); ++j) { }
        offs += j;
        return true;
    }
}
