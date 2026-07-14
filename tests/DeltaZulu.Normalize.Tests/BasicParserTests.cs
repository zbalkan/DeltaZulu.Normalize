using System.Text.Json.Nodes;
using static DeltaZulu.Normalize.Tests.TestHelpers;

namespace DeltaZulu.Normalize.Tests;

/// <summary>
/// Cases ported from tests/field_*.sh in the C project (behavioral fixtures,
/// not source code): each motif's success and failure paths.
/// </summary>
[TestClass]
public class BasicParserTests
{
    [TestMethod]
    public void CharTo_CoercesNonStringExtradataInsteadOfThrowing()
    {
        /* json-c's json_object_get_string() coerces any scalar to text; a
         * bare number here should behave like the string "5", not crash.
         * The trailing literal "5" consumes the terminator char-to itself
         * doesn't consume, so the whole message matches. */
        var (r, j) = TestHelpers.Normalize("""rule=:%f:char-to{"extradata":5}%5""", "12345");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"f": "1234"}""", j);
    }

    [TestMethod]
    [DataRow("duration 0:00:42 bytes", "0:00:42")]
    [DataRow("duration 0:00:42", "0:00:42")]
    [DataRow("duration 9:00:42 bytes", "9:00:42")]
    [DataRow("duration 00:00:42 bytes", "00:00:42")]
    [DataRow("duration 37:59:42 bytes", "37:59:42")]
    public void Duration_AcceptsOneOrTwoDigitHours(string message, string expected)
    {
        const string rb = """
            rule=:duration %field:duration% bytes
            rule=:duration %field:duration%
            """;
        var (r, j) = TestHelpers.Normalize(rb, message);
        Assert.AreEqual(0, r);
        AssertJsonEquals($$"""{"field": "{{expected}}"}""", j);
    }

    [TestMethod]
    public void Duration_RejectsOutOfRangeMinutes()
    {
        const string rb = """
            rule=:duration %field:duration% bytes
            rule=:duration %field:duration%
            """;
        var (r, j) = TestHelpers.Normalize(rb, "duration 37:60:42 bytes");
        Assert.AreNotEqual(0, r);
        Assert.AreEqual("37:60:42 bytes", j["unparsed-data"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("here is a number %num:float% in floating pt form", "here is a number 15.9 in floating pt form", "15.9")]
    [DataRow("here is a negative number %num:float% for you", "here is a negative number -4.2 for you", "-4.2")]
    [DataRow("here is another real number %num:float%.", "here is another real number 2.71.", "2.71")]
    public void Float_ExtractsDecimalValue(string pattern, string message, string expected)
    {
        var (r, j) = TestHelpers.Normalize($"rule=:{pattern}", message);
        Assert.AreEqual(0, r);
        AssertJsonEquals($$"""{"num": "{{expected}}"}""", j);
    }

    [TestMethod]
    public void HexNumber_RequiresWhitespaceTerminator()
    {
        const string rb = "rule=:here is a number %num:hexnumber% in hex form";
        var (r1, j1) = TestHelpers.Normalize(rb, "here is a number 0x1234 in hex form");
        Assert.AreEqual(0, r1);
        AssertJsonEquals("""{"num": "0x1234"}""", j1);

        var (r2, j2) = TestHelpers.Normalize(rb, "here is a number 0x1234in hex form");
        Assert.AreNotEqual(0, r2);
        AssertJsonEquals("""
            { "originalmsg": "here is a number 0x1234in hex form", "unparsed-data": "0x1234in hex form" }
            """, j2);
    }

    [TestMethod]
    [DataRow("ABCD:EF01:2345:6789:ABCD:EF01:2345:6789")]
    [DataRow("ABCD:EF01:2345:6789:abcd:EF01:2345:6789")]
    [DataRow("2001:DB8:0:0:8:800:200C:417A")]
    [DataRow("0:0:0:0:0:0:0:1")]
    [DataRow("2001:DB8::8:800:200C:417A")]
    [DataRow("FF01::101")]
    [DataRow("::1")]
    [DataRow("::")]
    [DataRow("0:0:0:0:0:0:13.1.68.3")]
    [DataRow("::13.1.68.3")]
    [DataRow("::FFFF:129.144.52.38")]
    public void IPv6_AcceptsRfc4291Examples(string addr)
    {
        var (r, j) = TestHelpers.Normalize("rule=:%f:ipv6%", addr);
        Assert.AreEqual(0, r);
        AssertJsonEquals($$"""{ "f": "{{addr}}" }""", j);
    }

    [TestMethod]
    [DataRow("2001:DB8::8::800:200C:417A")] // two "::" sequences
    [DataRow("ABCD:EF01:2345:6789:ABCD:EF01:2345::6789")] // "::" with too many blocks
    [DataRow(":0:0:0:0:0:0:1")] // missing first digit
    [DataRow("0:0:0:0:0:0:0:")] // missing last digit
    [DataRow("13.1.68.3")] // pure IPv4 address must not match
    public void IPv6_RejectsInvalidAddresses(string addr)
    {
        var (r, _) = TestHelpers.Normalize("rule=:%f:ipv6%", addr);
        Assert.AreNotEqual(0, r);
    }

    [TestMethod]
    public void IPv6_RejectsTooManyBlocksButReportsPartialUnparsed()
    {
        var (r, j) = TestHelpers.Normalize("rule=:%f:ipv6%", "ABCD:EF01:2345:6789:ABCD:EF01:2345:1:6798");
        Assert.AreNotEqual(0, r);
        AssertJsonEquals("""
            {"originalmsg": "ABCD:EF01:2345:6789:ABCD:EF01:2345:1:6798", "unparsed-data": ":6798" }
            """, j);
    }

    [TestMethod]
    public void Literal_PreservesHexEscapesAsRawByteChars()
    {
        /* \xHH escapes mirror libestr byte escapes: each escaped byte becomes
         * one char with that byte value, even if the byte run is not valid or
         * complete UTF-8. */
        var (r, j) = TestHelpers.Normalize("""rule=:caf\xC3\xA9 %f:rest%""", "caf\u00C3\u00A9 bar");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{ "f": "bar" }""", j);

        Assert.AreNotEqual(0, TestHelpers.Normalize("""rule=:caf\xC3\xA9 %f:rest%""", "café bar").Result);
    }

    [TestMethod]
    [DataRow("f0:f6:1c:5f:cc:a2")]
    [DataRow("f0-f6-1c-5f-cc-a2")]
    public void Mac48_AcceptsColonAndHyphenDelimited(string mac)
    {
        var (r, j) = TestHelpers.Normalize("rule=:%field:mac48%", mac);
        Assert.AreEqual(0, r);
        AssertJsonEquals($$"""{"field": "{{mac}}"}""", j);
    }

    [TestMethod]
    [DataRow("f0-f6:1c:5f:cc-a2")] // mixed delimiters
    [DataRow("f0:f6:1c:xf:cc:a2")] // non-hex digit
    public void Mac48_RejectsMalformed(string mac)
    {
        var (r, _) = TestHelpers.Normalize("rule=:%field:mac48%", mac);
        Assert.AreNotEqual(0, r);
    }

    [TestMethod]
    public void Number_ExtractsDigitsAndFailsOnTrailingLetters()
    {
        const string rb = "rule=:here is a number %num:number% in dec form";
        var (r1, j1) = TestHelpers.Normalize(rb, "here is a number 1234 in dec form");
        Assert.AreEqual(0, r1);
        AssertJsonEquals("""{"num": "1234"}""", j1);

        var (r2, j2) = TestHelpers.Normalize(rb, "here is a number 1234in dec form");
        Assert.AreNotEqual(0, r2);
        AssertJsonEquals("""
            { "originalmsg": "here is a number 1234in dec form", "unparsed-data": "in dec form" }
            """, j2);
    }

    [TestMethod]
    public void QuotedString_StripsOuterQuotes()
    {
        const string rb = "rule=:%f:quoted-string%";
        AssertJsonEquals("""{ "f": "alpha beta" }""", TestHelpers.Normalize(rb, "\"alpha beta\"").Json);
        AssertJsonEquals("""{ "f": "" }""", TestHelpers.Normalize(rb, "\"\"").Json);

        var (r, j) = TestHelpers.Normalize(rb, "\"unterminated");
        Assert.AreNotEqual(0, r);
        AssertJsonEquals("""
            { "originalmsg": "\"unterminated", "unparsed-data": "\"unterminated" }
            """, j);
    }

    [TestMethod]
    public void StringParser_DashIsEmptyAcceptsPythonBooleanAndRejectsQuotedDash()
    {
        const string rb = """rule=:%str:string{"option.dashIsEmpty":True}%""";

        AssertJsonEquals("""{ "str": "" }""", TestHelpers.Normalize(rb, "-").Json);

        var quotedDash = TestHelpers.Normalize(rb, "\"-\"");
        Assert.AreNotEqual(0, quotedDash.Result);
        AssertJsonEquals("""{ "originalmsg": "\"-\"", "unparsed-data": "\"-\"" }""", quotedDash.Json);
    }

    [TestMethod]
    public void StringParser_RejectsNonBooleanDashIsEmpty()
    {
        var errors = new List<string>();
        var ctx = new LogNormContext { ErrorCallback = errors.Add };

        var r = ctx.LoadSamplesFromString("""rule=:%f:string{"option.dashIsEmpty":"true"}%""");

        Assert.AreEqual(ErrorCodes.BadConfig, r);
        Assert.Contains(e => e.Contains("option.dashIsEmpty", StringComparison.Ordinal), errors);
    }


    [TestMethod]
    public void Include_ResolvesRelativeToIncludingRulebaseFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dzn-rb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var main = Path.Combine(dir, "main.rulebase");
            File.WriteAllText(main, "version=2\ninclude=inc.rulebase\n");
            File.WriteAllText(Path.Combine(dir, "inc.rulebase"), "version=2\nrule=:%mac:mac48%\n");

            var ctx = new LogNormContext();
            Assert.AreEqual(0, ctx.LoadSamples(main));
            Assert.AreEqual(0, ctx.Normalize("f0:f6:1c:5f:cc:a2", out JsonObject json));
            AssertJsonEquals("""{ "mac": "f0:f6:1c:5f:cc:a2" }""", json);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Include_UsesLegacyLiblognormRulebasesEnvironmentFallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dzn-rb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var oldNew = Environment.GetEnvironmentVariable("DeltaZulu.Normalize_RULEBASES");
        var oldLegacy = Environment.GetEnvironmentVariable("LIBLOGNORM_RULEBASES");
        try
        {
            File.WriteAllText(Path.Combine(dir, "inc.rulebase"), "version=2\nrule=:%mac:mac48%\n");
            Environment.SetEnvironmentVariable("DeltaZulu.Normalize_RULEBASES", null);
            Environment.SetEnvironmentVariable("LIBLOGNORM_RULEBASES", dir);

            var ctx = new LogNormContext();
            Assert.AreEqual(0, ctx.LoadSamplesFromString("include=inc.rulebase"));
            Assert.AreEqual(0, ctx.Normalize("f0:f6:1c:5f:cc:a2", out JsonObject json));
            AssertJsonEquals("""{ "mac": "f0:f6:1c:5f:cc:a2" }""", json);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DeltaZulu.Normalize_RULEBASES", oldNew);
            Environment.SetEnvironmentVariable("LIBLOGNORM_RULEBASES", oldLegacy);
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void StringTo_RejectsEmptyExtradataAsBadConfig()
    {
        var errors = new List<string>();
        var ctx = new LogNormContext { ErrorCallback = errors.Add };
        var r = ctx.LoadSamplesFromString("rule=:%f:string-to:%");
        Assert.AreNotEqual(0, r);
    }
}
