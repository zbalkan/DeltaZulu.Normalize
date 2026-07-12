using System.Text;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>
/// Rulebase (v2 "samples file") loader: parses rulebase text into calls
/// against <see cref="PdagBuilder"/>.
///
/// A rulebase is a sequence of logical lines, each one of:
/// "version=2" (file header only), "prefix=...", "extendprefix=...",
/// "rule=[tags:]pattern", "type=@name:pattern", "annotate=tag:+field=&quot;value&quot;...",
/// or "include=path". A rule/type pattern is itself a sequence of literal
/// text and "%name:type[params]%" field definitions.
///
/// Scope note: only the v2 rulebase syntax is supported. The legacy v1
/// engine (a separate, older normalizer with its own rulebase dialect) is
/// out of scope for this port — the PDAG model is what v2 introduced and is
/// the point of this exercise.
/// </summary>
internal static class RulebaseLoader
{
    private const int MaxFieldNameLen = 1024;
    private const int MaxTypeNameLen = 1024;

    /// <summary>
    /// Load a rulebase path: a single file, or a directory tree of rulebase
    /// files (loaded recursively, see <see cref="LoadDirectory"/>).
    /// </summary>
    public static int Load(LogNormContext ctx, string path)
    {
        /* a path that exists as given wins over the DeltaZulu.Normalize_RULEBASES
         * fallback; within each, a file wins over a directory */
        if (File.Exists(path))
        {
            return LoadFile(ctx, path);
        }

        if (Directory.Exists(path))
        {
            return LoadDirectory(ctx, path, "*", recursive: true);
        }

        if (ResolveRulebasePath(path) != null)
        {
            return LoadFile(ctx, path);
        }

        if (ResolveRulebaseDirectory(path) != null)
        {
            return LoadDirectory(ctx, path, "*", recursive: true);
        }

        return LoadFile(ctx, path); /* emits the "cannot open rulebase" error */
    }

    /// <summary>Load a rulebase file (must start with a "version=2" header line).</summary>
    public static int LoadFile(LogNormContext ctx, string path)
    {
        var savedFile = ctx.ConfFile;
        var savedLine = ctx.ConfLineNumber;
        ctx.ConfFile = path;
        ctx.ConfLineNumber = 0;
        ctx.IncludeLevel++;

        var r = SampLoad(ctx, path);

        ctx.IncludeLevel--;
        ctx.ConfFile = savedFile;
        ctx.ConfLineNumber = savedLine;
        return r;
    }

    /// <summary>
    /// Load every rulebase file found in a directory (and, when
    /// <paramref name="recursive"/> is set, its subdirectories). Files are
    /// loaded in ordinal path order so the result is deterministic across
    /// platforms and runs; hidden files (name starting with '.') are skipped.
    /// Each file is independent: it must carry its own "version=2" header and
    /// any prefix= it sets does not leak into the next file. Loading stops at
    /// the first file that fails.
    /// </summary>
    public static int LoadDirectory(LogNormContext ctx, string directory, string searchPattern, bool recursive)
    {
        var resolved = ResolveRulebaseDirectory(directory);
        if (resolved == null)
        {
            ctx.Error($"cannot open rulebase directory '{directory}'");
            return 1;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(resolved, searchPattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ctx.Error($"cannot enumerate rulebase directory '{directory}': {ex.Message}");
            return 1;
        }

        Array.Sort(files, StringComparer.Ordinal);

        var savedPrefix = ctx.RulePrefix;
        var loaded = 0;
        foreach (var file in files)
        {
            if (Path.GetFileName(file).StartsWith('.'))
            {
                continue;
            }

            ctx.RulePrefix = savedPrefix;
            var r = LoadFile(ctx, file);
            if (r != 0)
            {
                ctx.RulePrefix = savedPrefix;
                return r;
            }
            loaded++;
        }
        ctx.RulePrefix = savedPrefix;

        if (loaded == 0)
        {
            ctx.Error($"no rulebase files match '{searchPattern}' in directory '{directory}'");
            return 1;
        }
        return 0;
    }

    /// <summary>Load rulebase content from a string (no "version=2" header expected).</summary>
    public static int LoadString(LogNormContext ctx, string rulebase)
    {
        var savedFile = ctx.ConfFile;
        var savedLine = ctx.ConfLineNumber;
        ctx.ConfFile = "--NO-FILE--";
        ctx.ConfLineNumber = 0;
        ctx.IncludeLevel++;

        var r = RunLoad(ctx, rulebase, checkRunaway: false);

        ctx.IncludeLevel--;
        ctx.ConfFile = savedFile;
        ctx.ConfLineNumber = savedLine;
        return r;
    }

    private static string? ResolveRulebasePath(string file)
    {
        if (File.Exists(file))
        {
            return file;
        }

        var rbLib = Environment.GetEnvironmentVariable("DeltaZulu.Normalize_RULEBASES");
        if (rbLib == null || Path.IsPathRooted(file))
        {
            return null;
        }

        var candidate = Path.Combine(rbLib, file);
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? ResolveRulebaseDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            return directory;
        }

        var rbLib = Environment.GetEnvironmentVariable("DeltaZulu.Normalize_RULEBASES");
        if (rbLib == null || Path.IsPathRooted(directory))
        {
            return null;
        }

        var candidate = Path.Combine(rbLib, directory);
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static int SampLoad(LogNormContext ctx, string file)
    {
        var resolved = ResolveRulebasePath(file);
        if (resolved == null)
        {
            ctx.Error($"cannot open rulebase '{file}'");
            return 1;
        }

        string text;
        try
        {
            text = File.ReadAllText(resolved);
        }
        catch (IOException ex)
        {
            ctx.Error($"cannot open rulebase '{file}': {ex.Message}");
            return 1;
        }

        var newlineIdx = text.IndexOf('\n');
        var firstLine = (newlineIdx >= 0 ? text[..newlineIdx] : text).TrimEnd('\r');
        if (firstLine != "version=2")
        {
            ctx.Error($"rulebase '{file}' must be version 2 " +
                      "(v1 rulebases are not supported by this port)");
            return 1;
        }

        ctx.ConfLineNumber++; /* "version=2" is line 1 */
        var body = newlineIdx >= 0 ? text[(newlineIdx + 1)..] : string.Empty;
        return RunLoad(ctx, body, checkRunaway: true);
    }

    /// <summary>
    /// Peek ahead over blank lines and comment lines (without consuming any
    /// input) to see whether the next real content looks like the start of a
    /// new rule. Used to detect "runaway rules": a multi-line rule left open
    /// by an unmatched '%' will otherwise silently swallow everything up to
    /// the next accidental '%' pair, which is almost always a config typo.
    /// </summary>
    private static bool PeekIsRunawayRule(string text, int pos)
    {
        while (true)
        {
            while (pos < text.Length && (text[pos] == '\n' || text[pos] == '\r'))
            {
                pos++;
            }

            if (pos >= text.Length)
            {
                return false;
            }

            if (text[pos] == '#')
            {
                var nl = text.IndexOf('\n', pos);
                if (nl < 0)
                {
                    return false;
                }

                pos = nl + 1;
                continue;
            }
            break;
        }
        return pos + 5 <= text.Length && text.AsSpan(pos, 5).SequenceEqual("rule=");
    }

    /// <summary>
    /// Read the next logical rulebase line: comment lines are dropped and a
    /// '%'-delimited field definition may itself span physical lines (any
    /// newline inside it is simply removed, not converted to a space).
    /// </summary>
    private static string? ReadLogicalLine(LogNormContext ctx, string text, ref int pos, bool checkRunaway)
    {
        var buf = new StringBuilder();
        var inField = false;

        while (true)
        {
            if (pos >= text.Length)
            {
                return buf.Length == 0 ? null : buf.ToString();
            }

            var c = text[pos++];
            if (c == '\r' && pos < text.Length && text[pos] == '\n')
            {
                c = text[pos++]; /* CRLF line ending: fold to a single LF */
            }

            if (c == '\n')
            {
                ctx.ConfLineNumber++;
                if (inField && checkRunaway && PeekIsRunawayRule(text, pos))
                {
                    ctx.Error("line has 'rule=' at begin of line, which does look like a typo in the " +
                              "previous lines (unmatched '%' character) and is forbidden. If valid, " +
                              "please re-format the rule to start with other characters. Rule ignored.");
                    inField = false;
                    buf.Clear();
                }
                if (!inField && buf.Length != 0)
                {
                    return buf.ToString();
                }
            }
            else if (c == '#' && buf.Length == 0)
            {
                var nl = text.IndexOf('\n', pos);
                if (nl < 0)
                {
                    pos = text.Length;
                }
                else
                {
                    pos = nl + 1;
                    ctx.ConfLineNumber++;
                }
            }
            else
            {
                if (c == '%')
                {
                    inField = !inField;
                }

                buf.Append(c);
            }
        }
    }

    private static int RunLoad(LogNormContext ctx, string text, bool checkRunaway)
    {
        var pos = 0;
        while (true)
        {
            var line = ReadLogicalLine(ctx, text, ref pos, checkRunaway);
            if (line == null)
            {
                return 0;
            }

            var r = ProcessLine(ctx, line);
            if (r != 0)
            {
                return r;
            }
        }
    }

    /* ---------- line dispatch ---------- */

    private static int ProcessLine(LogNormContext ctx, string line)
    {
        var eq = line.IndexOf('=');
        var lineType = eq < 0 ? line : line[..eq];
        var offs = eq < 0 ? line.Length : eq + 1;

        switch (lineType)
        {
            case "prefix":
                ctx.RulePrefix = line[offs..];
                return 0;

            case "extendprefix":
                ctx.RulePrefix = (ctx.RulePrefix ?? "") + line[offs..];
                return 0;

            case "rule":
                return ProcessRule(ctx, line, offs);

            case "type":
                return ProcessType(ctx, line, offs);

            case "annotate":
                return ProcessAnnotate(ctx, line, offs);

            case "include":
                return ProcessInclude(ctx, line, offs);

            default:
                ctx.Error($"invalid record type detected: '{lineType}'");
                return 1;
        }
    }

    /* ---------- rule / type ---------- */

    private static bool ProcessTags(LogNormContext ctx, string line, ref int offs, out JsonArray? tagBucket)
    {
        tagBucket = null;
        var i = offs;
        var sb = new StringBuilder();

        while (i < line.Length && line[i] != ':')
        {
            if (line[i] == ',')
            {
                if (sb.Length == 0)
                {
                    return false; /* empty tag before a comma */
                }

                (tagBucket ??= new JsonArray()).Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(line[i]);
            }
            ++i;
        }
        if (i >= line.Length || line[i] != ':')
        {
            return false; /* the ':' separator is mandatory, even with no tags */
        }

        ++i;
        if (sb.Length > 0)
        {
            (tagBucket ??= new JsonArray()).Add(sb.ToString());
        }

        offs = i;
        return true;
    }

    private static int ProcessRule(LogNormContext ctx, string line, int offs)
    {
        if (!ProcessTags(ctx, line, ref offs, out var tagBucket))
        {
            ctx.Error($"error parsing tags in rule line: '{line}'");
            return 1;
        }
        if (offs == line.Length)
        {
            ctx.Error("error: actual message sample part is missing");
            return 1;
        }
        var rule = (ctx.RulePrefix ?? string.Empty) + line[offs..];
        var root = ctx.Root;
        return AddSampToTree(ctx, rule, ref root, tagBucket);
    }

    private static bool GetTypeName(LogNormContext ctx, string line, ref int offs, out string typeName)
    {
        typeName = string.Empty;
        var i = offs;
        if (i >= line.Length || line[i] != '@')
        {
            ctx.Error("user-defined type name must start with '@'");
            return false;
        }
        var sb = new StringBuilder();
        while (i < line.Length && line[i] != ':' && sb.Length < MaxTypeNameLen - 1)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                ctx.Error("user-defined type name must not contain whitespace");
                return false;
            }
            sb.Append(line[i]);
            ++i;
        }
        if (i >= line.Length || line[i] != ':')
        {
            return false;
        }

        typeName = sb.ToString();
        offs = i + 1;
        return true;
    }

    private static int ProcessType(LogNormContext ctx, string line, int offs)
    {
        if (!GetTypeName(ctx, line, ref offs, out var typeName))
        {
            return 1;
        }

        if (offs == line.Length)
        {
            ctx.Error("error: actual message sample part is missing in type def");
            return 1;
        }
        var td = PdagBuilder.FindType(ctx, typeName, add: true);
        var dag = ctx.TypePdags[td].Dag;
        return AddSampToTree(ctx, line[offs..], ref dag, null);
    }

    /* ---------- annotate ---------- */

    private static bool GetFieldName(string line, ref int i, out string name)
    {
        var sb = new StringBuilder();
        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '.'))
        {
            sb.Append(line[i++]);
        }

        name = sb.ToString();
        return true;
    }

    private static void SkipWhitespace(string line, ref int i)
    {
        while (i < line.Length && char.IsWhiteSpace(line[i]))
        {
            ++i;
        }
    }

    private static int ProcessAnnotate(LogNormContext ctx, string line, int offs)
    {
        GetFieldName(line, ref offs, out var tag);
        SkipWhitespace(line, ref offs);
        if (tag.Length == 0 || offs >= line.Length || line[offs] != ':')
        {
            ctx.Error($"invalid tag field in annotation, line is '{line}'");
            return 1;
        }
        ++offs;

        while (offs < line.Length)
        {
            SkipWhitespace(line, ref offs);
            if (offs == line.Length)
            {
                break;
            }

            if (line[offs] == '#')
            {
                break; /* inline comment: rest of line ignored */
            }

            if (line[offs] != '+')
            {
                ctx.Error($"invalid annotate operation '{line[offs]}': {line[offs..]}");
                return 1;
            }
            ++offs;
            if (offs == line.Length)
            {
                return 1;
            }

            GetFieldName(line, ref offs, out var fieldName);
            if (offs == line.Length || line[offs] != '=')
            {
                return 1;
            }

            ++offs;
            SkipWhitespace(line, ref offs);
            if (offs == line.Length || line[offs] != '"')
            {
                return 1;
            }

            ++offs;

            var val = new StringBuilder();
            while (offs < line.Length && line[offs] != '"')
            {
                val.Append(line[offs++]);
            }

            offs = offs == line.Length ? offs : offs + 1;

            ctx.Annotations.AddOp(tag, fieldName, val.ToString());
        }
        return 0;
    }

    /* ---------- include ---------- */

    private static int ProcessInclude(LogNormContext ctx, string line, int offs)
    {
        var fname = line[offs..].TrimEnd();
        var savedFile = ctx.ConfFile;
        var savedLine = ctx.ConfLineNumber;
        var r = ctx.LoadSamples(fname);
        ctx.ConfFile = savedFile;
        ctx.ConfLineNumber = savedLine;
        return r;
    }

    /* ---------- rule pattern: literal/field splitting ---------- */

    private static JsonObject NewLiteralParserConfig(char lit)
        => new() { ["type"] = "literal", ["text"] = lit.ToString() };

    /// <summary>
    /// Consume a run of literal text up to the next field definition (or
    /// end of pattern). A doubled "%%" collapses to one literal '%'; a
    /// trailing single '%' at the very end of the pattern is dropped. The
    /// collected text is then unescaped (backslash + hex escapes) and added
    /// to the DAG one character at a time — the optimizer compacts runs of
    /// single-char literals afterwards.
    /// </summary>
    private static int ParseLiteral(LogNormContext ctx, ref Pdag pdag, string rule, ref int i)
    {
        var sb = new StringBuilder();
        while (i < rule.Length)
        {
            if (rule[i] == '%')
            {
                if (i + 1 < rule.Length && rule[i + 1] != '%')
                {
                    break; /* field start ends the literal */
                }

                if (++i == rule.Length)
                {
                    break;
                }
            }
            sb.Append(rule[i]);
            ++i;
        }

        var lit = TextRules.Unescape(sb.ToString());
        foreach (var c in lit)
        {
            var r = PdagBuilder.AddParser(ctx, ref pdag, NewLiteralParserConfig(c));
            if (r != 0)
            {
                return r;
            }
        }
        return 0;
    }

    private static bool ParseLegacyFieldDescr(LogNormContext ctx, string rule, ref int i, out JsonObject? config)
    {
        config = null;
        var nameSb = new StringBuilder();
        while (nameSb.Length < MaxFieldNameLen - 1 && i < rule.Length && rule[i] != ':')
        {
            nameSb.Append(rule[i++]);
        }

        if (nameSb.Length == MaxFieldNameLen - 1)
        {
            ctx.Error($"field name too long in: {rule}");
            return false;
        }
        if (i == rule.Length || nameSb.Length == 0)
        {
            ctx.Error($"field definition wrong in: {rule}");
            return false;
        }
        if (rule[i] != ':')
        {
            ctx.Error($"missing colon in: {rule}");
            return false;
        }
        ++i; /* skip ':' */

        /* type name: scan to ':', '{' or '%', then trim trailing whitespace */
        var typeStart = i;
        var j = i;
        while (j < rule.Length && rule[j] != ':' && rule[j] != '{' && rule[j] != '%')
        {
            ++j;
        }

        var typeEnd = j;
        while (typeEnd > typeStart && char.IsWhiteSpace(rule[typeEnd - 1]))
        {
            --typeEnd;
        }

        var ftype = rule.Substring(typeStart, typeEnd - typeStart);
        i = j; /* resume scanning at the untrimmed end */

        if (i == rule.Length)
        {
            ctx.Error($"premature end (missing %%?) in: {rule}");
            return false;
        }

        JsonObject? inlineParams = null;
        if (rule[i] == '{')
        {
            if (!JsonText.TryParseValue(rule, i, out var parsed, out var consumed) || parsed is not JsonObject po)
            {
                ctx.Error($"invalid json in '{rule[i..]}'");
                return false;
            }
            inlineParams = po;
            i += consumed;
        }

        string? extradata = null;
        if (i < rule.Length && rule[i] == '%')
        {
            ++i;
        }
        else
        {
            /* legacy extradata: raw text up to the next '%' (no doubling escape here,
             * only the general backslash/hex escapes applied afterwards) */
            ++i; /* skip the ':' that separates type from extradata */
            var edSb = new StringBuilder();
            while (i < rule.Length)
            {
                if (rule[i] == '%')
                {
                    ++i;
                    break;
                }
                edSb.Append(rule[i++]);
            }
            extradata = TextRules.Unescape(edSb.ToString());
        }

        var cfg = new JsonObject {
            ["name"] = nameSb.ToString(),
            ["type"] = ftype,
        };
        if (extradata != null)
        {
            cfg["extradata"] = extradata;
        }

        if (inlineParams != null)
        {
            foreach ((var key, var val) in inlineParams.ToList())
            {
                inlineParams.Remove(key);
                cfg[key] = val;
            }
        }
        config = cfg;
        return true;
    }

    /// <summary>
    /// Parse one field definition. The buffer must be positioned on the
    /// leading '%'. Supports the "full JSON" form (a raw JSON object/array
    /// directly after '%') and the legacy/condensed "name:type{params}" form.
    /// </summary>
    private static int AddFieldDescr(LogNormContext ctx, ref Pdag pdag, string rule, ref int i)
    {
        ++i; /* eat '%' */
        while (i < rule.Length && char.IsWhiteSpace(rule[i]))
        {
            ++i;
        }

        JsonNode? config;
        if (i < rule.Length && (rule[i] == '{' || rule[i] == '['))
        {
            if (!JsonText.TryParseValue(rule, i, out config, out var consumed)
                || config == null || i + consumed >= rule.Length || rule[i + consumed] != '%')
            {
                ctx.Error($"invalid json in '{rule[i..]}'");
                return ErrorCodes.BadConfig;
            }
            i += consumed + 1; /* also eat the closing '%' */
        }
        else
        {
            if (!ParseLegacyFieldDescr(ctx, rule, ref i, out var legacyCfg))
            {
                return ErrorCodes.BadConfig;
            }

            config = legacyCfg;
        }

        return PdagBuilder.AddParser(ctx, ref pdag, config!);
    }

    /// <summary>
    /// Split a rule/type pattern into literal chunks and field definitions
    /// and add them all to the DAG in sequence, then mark the resulting node
    /// terminal (assigning tags, for rules).
    /// </summary>
    private static int AddSampToTree(LogNormContext ctx, string rule, ref Pdag dag, JsonArray? tagBucket)
    {
        var i = 0;
        while (i < rule.Length)
        {
            var r = ParseLiteral(ctx, ref dag, rule, ref i);
            if (r != 0)
            {
                return r;
            }

            if (i < rule.Length)
            {
                r = AddFieldDescr(ctx, ref dag, rule, ref i);
                if (r != 0)
                {
                    return r;
                }

                if (i == rule.Length)
                {
                    /* finish with an empty literal to avoid false merging with a
                     * later rule that happens to continue with the same text */
                    r = ParseLiteral(ctx, ref dag, rule, ref i);
                    if (r != 0)
                    {
                        return r;
                    }
                }
            }
        }

        dag.IsTerminal = true;
        dag.Tags = tagBucket;
        dag.RulebaseFile = ctx.ConfFile;
        dag.RulebaseLineNumber = ctx.ConfLineNumber;
        return 0;
    }
}