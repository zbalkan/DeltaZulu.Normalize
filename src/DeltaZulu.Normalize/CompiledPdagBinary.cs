using System.Buffers;
using System.Text.Json.Nodes;
using DeltaZulu.Normalize.Parsers;

namespace DeltaZulu.Normalize;

/// <summary>Binary persistence for compiled PDAG snapshots.</summary>
internal static class CompiledPdagBinary
{
    private const uint Magic = 0x47414450; // PDAG, little-endian
    private const ushort Version = 1;

    public static void Write(CompiledPdag snap, Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(ParserTable.Parsers.Length);
        w.Write(snap.Nodes.Length);
        w.Write(snap.Edges.Length);
        w.Write(snap.Terminals.Length);
        w.Write(snap.TypeRoots.Length);
        foreach (var n in snap.Nodes)
        {
            w.Write(n.EdgeStart); w.Write(n.EdgeCount); w.Write(n.TerminalIdx); w.Write(n.RefCount);
        }
        foreach (var e in snap.Edges)
        {
            w.Write(e.PrsId); w.Write(e.LiteralFirstChar); w.Write(e.TargetNode); w.Write(e.CustomTypeIdx);
            w.Write((byte)e.Extract); w.Write(e.Name != null); if (e.Name != null) w.Write(e.Name);
            WriteData(w, e.PrsId, e.Data);
        }
        foreach (var t in snap.Terminals)
        {
            w.Write(t.RulebaseFile != null); if (t.RulebaseFile != null) w.Write(t.RulebaseFile);
            w.Write(t.RulebaseLineNumber);
            var tags = t.Tags?.ToJsonString();
            w.Write(tags != null); if (tags != null) w.Write(tags);
        }
        foreach (var root in snap.TypeRoots) w.Write(root);
    }

    public static CompiledPdag Read(Stream stream, LogNormOptions options)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not a compiled PDAG binary file.");
        if (r.ReadUInt16() != Version) throw new InvalidDataException("Unsupported compiled PDAG binary version.");
        if (r.ReadInt32() != ParserTable.Parsers.Length) throw new InvalidDataException("Compiled PDAG parser table does not match this library version.");
        var nodes = new CompiledNode[r.ReadInt32()];
        var edges = new CompiledEdge[r.ReadInt32()];
        var terminals = new TerminalInfo[r.ReadInt32()];
        var typeRoots = new int[r.ReadInt32()];
        for (var i = 0; i < nodes.Length; i++) nodes[i] = new CompiledNode(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        for (var i = 0; i < edges.Length; i++)
        {
            var prsId = r.ReadByte(); var first = r.ReadChar(); var target = r.ReadInt32(); var custom = r.ReadInt32();
            var extract = (ExtractMode)r.ReadByte(); var name = r.ReadBoolean() ? r.ReadString() : null;
            var data = ReadData(r, prsId);
            edges[i] = new CompiledEdge(prsId, first, target, custom, data, name, extract);
        }
        for (var i = 0; i < terminals.Length; i++)
        {
            terminals[i] = new TerminalInfo {
                RulebaseFile = r.ReadBoolean() ? r.ReadString() : null,
                RulebaseLineNumber = r.ReadInt32(),
                Tags = r.ReadBoolean() ? JsonNode.Parse(r.ReadString())!.AsArray() : null,
            };
        }
        for (var i = 0; i < typeRoots.Length; i++) typeRoots[i] = r.ReadInt32();
        var snap = new CompiledPdag { Nodes = nodes, Edges = edges, Terminals = terminals, TypeRoots = typeRoots };
        if ((options & LogNormOptions.CollectStats) != 0)
        {
            snap.StatsCalled = new int[nodes.Length];
            snap.StatsBacktracked = new int[nodes.Length];
        }
        return snap;
    }

    private static void WriteData(BinaryWriter w, byte prsId, object? data)
    {
        w.Write(data != null);
        if (data == null)
        {
            return;
        }
        switch (ParserTable.IdToName(prsId))
        {
            case "literal": w.Write(((LiteralParser.Data)data!).Lit); break;
            case "repeat": var rp = (RepeatParser.CompiledData)data!; w.Write(rp.ParserRoot); w.Write(rp.WhileRoot); w.Write(rp.PermitMismatchInParser); w.Write(rp.FailOnDuplicate); break;
            case "number": var nd = (NumberParsers.NumberData)data!; w.Write((byte)nd.FmtMode); w.Write(nd.MaxVal); break;
            case "float": w.Write((byte)((NumberParsers.FloatData)data!).FmtMode); break;
            case "hexnumber": var hd = (NumberParsers.HexNumberData)data!; w.Write((byte)hd.FmtMode); w.Write(hd.MaxVal); break;
            case "date-rfc3164" or "date-rfc5424": w.Write((byte)((DateTimeParsers.DateData)data!).FmtMode); break;
            case "op-quoted-string": w.Write(((CoreParsers.OpQuotedStringData)data!).Escape); break;
            case "json": w.Write(((StructuredParsers.JsonData)data!).SkipEmpty); break;
            case "name-value-list": var nv = (StructuredParsers.NameValueData)data!; w.Write(nv.Ass); w.Write(nv.Sep); w.Write(nv.IgnoreWhitespaces); break;
            case "checkpoint-lea": w.Write(((StructuredParsers.CheckpointLeaData)data!).Terminator); break;
            case "string-to": w.Write(((CoreParsers.StringToData)data!).ToFind); break;
            case "char-to": w.Write(((CoreParsers.CharToData)data!).TermText); break;
            case "char-sep": w.Write(((CoreParsers.CharSeparatedData)data!).TermText); break;
            case "string": WriteStringData(w, (StringParser.Data)data!); break;
        }
    }

    private static object? ReadData(BinaryReader r, byte prsId)
    {
        if (!r.ReadBoolean())
        {
            return null;
        }
        return ParserTable.IdToName(prsId) switch {
        "literal" => new LiteralParser.Data { Lit = r.ReadString() },
        "repeat" => new RepeatParser.CompiledData { ParserRoot = r.ReadInt32(), WhileRoot = r.ReadInt32(), PermitMismatchInParser = r.ReadBoolean(), FailOnDuplicate = r.ReadBoolean() },
        "number" => new NumberParsers.NumberData { FmtMode = (FormatMode)r.ReadByte(), MaxVal = r.ReadInt64() },
        "float" => new NumberParsers.FloatData { FmtMode = (FormatMode)r.ReadByte() },
        "hexnumber" => new NumberParsers.HexNumberData { FmtMode = (FormatMode)r.ReadByte(), MaxVal = r.ReadUInt64() },
        "date-rfc3164" or "date-rfc5424" => new DateTimeParsers.DateData { FmtMode = (FormatMode)r.ReadByte() },
        "op-quoted-string" => new CoreParsers.OpQuotedStringData { Escape = r.ReadBoolean() },
        "json" => new StructuredParsers.JsonData { SkipEmpty = r.ReadBoolean() },
        "name-value-list" => new StructuredParsers.NameValueData { Ass = r.ReadChar(), Sep = r.ReadChar(), IgnoreWhitespaces = r.ReadBoolean() },
        "checkpoint-lea" => new StructuredParsers.CheckpointLeaData { Terminator = r.ReadChar() },
        "string-to" => new CoreParsers.StringToData { ToFind = r.ReadString() },
        "char-to" => CreateCharTo(r.ReadString()),
        "char-sep" => CreateCharSep(r.ReadString()),
        "string" => ReadStringData(r),
        _ => null,
        };
    }

    private static CoreParsers.CharToData CreateCharTo(string s) => new() { TermText = s, TermChars = SearchValues.Create(s) };
    private static CoreParsers.CharSeparatedData CreateCharSep(string s) => new() { TermText = s, TermChars = SearchValues.Create(s) };

    private static void WriteStringData(BinaryWriter w, StringParser.Data d)
    {
        w.Write(d.DashIsEmpty); w.Write((byte)d.EscMd); w.Write((byte)d.Matching); w.Write(d.QCharBegin); w.Write(d.QCharEnd);
        w.Write((byte)d.QuoteMode); w.Write(d.Restricted); w.Write(d.StripQuotes);
        foreach (var bits in d.ExportPermChars()) w.Write(bits);
    }

    private static StringParser.Data ReadStringData(BinaryReader r)
    {
        var d = new StringParser.Data { DashIsEmpty = r.ReadBoolean(), EscMd = (StringParser.EscMode)r.ReadByte(), Matching = (StringParser.MatchingMode)r.ReadByte(), QCharBegin = r.ReadChar(), QCharEnd = r.ReadChar(), QuoteMode = (StringParser.QuoteMode)r.ReadByte(), Restricted = r.ReadBoolean(), StripQuotes = r.ReadBoolean() };
        Span<ulong> bits = stackalloc ulong[4];
        for (var i = 0; i < bits.Length; i++) bits[i] = r.ReadUInt64();
        d.ImportPermChars(bits);
        return d;
    }
}
