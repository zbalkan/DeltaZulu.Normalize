using System.Runtime.CompilerServices;
using System.Text;

namespace DeltaZulu.Normalize;

/// <summary>
/// <para>Character classification and rulebase-string helpers.</para>
/// <para>
/// The C library operates on bytes with "C" locale ctype semantics; these
/// helpers reproduce that behaviour on chars (ASCII-only classification) so
/// that parsing decisions match the reference implementation.
/// </para>
/// </summary>
internal static class TextRules
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HexVal(char c)
    {
        if (IsDigit(c))
        {
            return c - '0';
        }

        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }

        return c - 'A' + 10;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAlnum(char c) => IsAlpha(c) || IsDigit(c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAlpha(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDigit(char c) => c >= '0' && c <= '9';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsHexDigit(char c)
        => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSpace(char c)
        => c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r';

    /// <summary>
    /// Unescape a rulebase literal the way libestr's es_unescapeStr does:
    /// backslash escapes for \\ \" \' \a \b \f \n \r \t \v, hex bytes as
    /// \xHH. An unrecognized escape keeps the backslash verbatim.
    /// </summary>
    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c != '\\' || i + 1 == s.Length)
            {
                sb.Append(c);
                ++i;
                continue;
            }
            var e = s[i + 1];
            switch (e)
            {
                case '\\': sb.Append('\\'); i += 2; break;
                case '"': sb.Append('"'); i += 2; break;
                case '\'': sb.Append('\''); i += 2; break;
                case 'a': sb.Append('\a'); i += 2; break;
                case 'b': sb.Append('\b'); i += 2; break;
                case 'f': sb.Append('\f'); i += 2; break;
                case 'n': sb.Append('\n'); i += 2; break;
                case 'r': sb.Append('\r'); i += 2; break;
                case 't': sb.Append('\t'); i += 2; break;
                case 'v': sb.Append('\v'); i += 2; break;
                case 'x':
                    if (i + 3 < s.Length && IsHexDigit(s[i + 2]) && IsHexDigit(s[i + 3]))
                    {
                        sb.Append((char)((HexVal(s[i + 2]) * 16) + HexVal(s[i + 3])));
                        i += 4;
                    }
                    else
                    {
                        sb.Append(c);
                        ++i;
                    }
                    break;

                default:
                    sb.Append(c); /* keep backslash, escape not recognized */
                    ++i;
                    break;
            }
        }
        return sb.ToString();
    }
}
