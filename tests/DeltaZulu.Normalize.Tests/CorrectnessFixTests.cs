using System.Text.Json.Nodes;
using System.Text;
using static DeltaZulu.Normalize.Tests.TestHelpers;

namespace DeltaZulu.Normalize.Tests;

/// <summary>
/// Regression tests for defects found during the hot-path review: the
/// string parser's permitted-table char truncation, float number-mode
/// formatting edge cases, and build-time blowup on heavily shared DAGs.
/// </summary>
[TestClass]
public class CorrectnessFixTests
{
    [TestMethod]
    public void StringParser_RestrictedSet_RejectsCharsAboveByteRange()
    {
        /* 'Ł' (U+0141) used to alias into table slot 0x41 ('A') and pass an
         * alpha-restricted match */
        const string rb = """
            rule=:%{"name":"f", "type":"string", "matching.permitted":[ {"class":"alpha"} ]}% rest
            """;
        var (r, _) = TestHelpers.Normalize(rb, "Łód rest");
        Assert.AreNotEqual(0, r, "non-ASCII char must not satisfy an alpha-restricted string");

        var (rOk, j) = TestHelpers.Normalize(rb, "Abc rest");
        Assert.AreEqual(0, rOk);
        AssertJsonEquals("""{ "f": "Abc" }""", j);
    }

    [TestMethod]
    public void StringParser_UnrestrictedSet_PermitsCharsAboveByteRange()
    {
        /* without matching.permitted every char is permitted, like the C
         * library's all-true byte table */
        const string rb = """
            rule=:%{"name":"f", "type":"string"}% rest
            """;
        var (r, j) = TestHelpers.Normalize(rb, "Łód rest");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{ "f": "Łód" }""", j);
    }

    [TestMethod]
    [DataRow("val 0.5 x", "0.5")]     /* plain: raw text kept */
    [DataRow("val -0.25 x", "-0.25")] /* negative */
    [DataRow("val 123 x", "123")]     /* integral */
    [DataRow("val 00.5 x", "0.5")]    /* leading zeros: invalid JSON, computed value */
    [DataRow("val 5. x", "5")]        /* trailing dot: invalid JSON, computed value */
    public void Float_NumberFormat_EmitsValidJson(string message, string expected)
    {
        const string rb = """
            rule=:val %{"name":"f", "type":"float", "format":"number"}% x
            """;
        var (r, j) = TestHelpers.Normalize(rb, message);
        Assert.AreEqual(0, r);
        Assert.AreEqual(expected, j["f"]!.ToJsonString());
    }

    [TestMethod]
    public void HexNumber_UppercaseDigits_ParseCorrectly()
    {
        const string rb = """
            rule=:h %{"name":"f", "type":"hexnumber", "format":"number"}% t
            """;
        var (r, j) = TestHelpers.Normalize(rb, "h 0xFF t");
        Assert.AreEqual(0, r);
        Assert.AreEqual("255", j["f"]!.ToJsonString());
    }

    [TestMethod]
    public void Dispatch_SwitchCoversWholeParserTable() =>
        /* the hot-path dispatch switch must have one case per table entry;
* this catches a parser added to the table but not to the switch */
        Assert.HasCount(ParserTable.DispatchCaseCount, ParserTable.Parsers,
            "ParserTable.Dispatch is out of sync with ParserTable.Parsers");

    [TestMethod]
    public void Stats_CollectedOnlyWhenOptionSet()
    {
        var ctx = new LogNormContext();
        Assert.AreEqual(0, ctx.LoadSamplesFromString("rule=:a %f:word%"));
        ctx.Normalize("a hello", out JsonObject _);
        Assert.AreEqual((0L, 0L), ctx.GetStats(), "stats must stay off by default");

        var statsCtx = new LogNormContext { Options = LogNormOptions.CollectStats };
        Assert.AreEqual(0, statsCtx.LoadSamplesFromString("rule=:a %f:word%"));
        Assert.AreEqual(0, statsCtx.Normalize("a hello", out JsonObject _));
        Assert.IsGreaterThan(0, statsCtx.GetStats().NodesVisited, "stats must accumulate when enabled");
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public void Build_DeeplyNestedAlternatives_CompletesQuickly()
    {
        /* every "alternative" forks and re-joins at a shared node; 24 of them
         * in sequence make the re-join nodes reachable via 2^24 textual paths.
         * Without visited-marking the optimizer walk is exponential. */
        var rule = new StringBuilder("rule=:start");
        for (var i = 0; i < 24; i++)
        {
            rule.Append("""
                 %{"type":"alternative", "parser":[ {"type":"literal", "text":"a"}, {"type":"literal", "text":"b"} ]}%
                """.Trim().Insert(0, " "));
        }

        rule.Append(" %f:word%");

        var msg = new StringBuilder("start");
        for (var i = 0; i < 24; i++)
        {
            msg.Append(i % 2 == 0 ? " a" : " b");
        }

        msg.Append(" end");

        var (r, j) = TestHelpers.Normalize(rule.ToString(), msg.ToString());
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{ "f": "end" }""", j);
    }
}