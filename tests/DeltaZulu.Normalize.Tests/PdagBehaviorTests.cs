using static DeltaZulu.Normalize.Tests.TestHelpers;

namespace DeltaZulu.Normalize.Tests;

/// <summary>
/// Cases exercising PDAG-level behavior rather than a single motif: repeat,
/// alternative, user-defined types, annotations, and backtracking/priority
/// across rules sharing a common prefix. Ported from repeat_*.sh,
/// alternative_*.sh, usrdef_*.sh, annotate.sh, strict_prefix_*.sh and
/// field_rest.sh.
/// </summary>
[TestClass]
public class PdagBehaviorTests
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void AddRuleOption_AttachesMockupRuleMetadata()
    {
        var (r, j) = TestHelpers.Normalize("rule=:hello %name:word%", "hello world", LogNormOptions.AddRule);
        Assert.AreEqual(0, r);
        Assert.AreEqual("hello %name:word%", j["metadata"]!["rule"]!["mockup"]!.GetValue<string>());
    }

    [TestMethod]
    public void Alternative_TriesEachBranchInOrder()
    {
        const string rb = """
            rule=:a %{"type":"alternative", "parser":[{"name":"num", "type":"number"}, {"name":"hex", "type":"hexnumber"}]}% b
            """;
        AssertJsonEquals("""{ "num": "4711" }""", TestHelpers.Normalize(rb, "a 4711 b").Json);
        AssertJsonEquals("""{ "hex": "0x4711" }""", TestHelpers.Normalize(rb, "a 0x4711 b").Json);
    }

    [TestMethod]
    public void Alternative_AcceptsNestedSequenceWithTrailingCommas()
    {
        const string rb = """
            rule=:a %{ "type":"alternative", "parser":[
                [ {"type":"number", "name":"num1"}, {"type":"literal", "text":":"}, {"type":"number", "name":"num"}, ],
                {"type":"hexnumber", "name":"hex"}
            ] }% b
            """;
        AssertJsonEquals("""{ "num1": "47", "num": "11" }""", TestHelpers.Normalize(rb, "a 47:11 b").Json);
        AssertJsonEquals("""{ "hex": "0x4711" }""", TestHelpers.Normalize(rb, "a 0x4711 b").Json);
    }

    [TestMethod]
    public void Annotate_AddsStaticFieldsPerTag()
    {
        const string rb = """
            rule=ABC,WIN:<%-:number%>1 %-:date-rfc5424% %-:word% %tag:word% - - -
            rule=ABC:<%-:number%>1 %-:date-rfc5424% %-:word% %tag:word% + - -
            rule=WIN:<%-:number%>1 %-:date-rfc5424% %-:word% %tag:word% . - -
            annotate=WIN:+annot1="WIN" # inline-comment
            annotate=ABC:+annot2="ABC"
            """;
        /* note: ln_normalize() always adds "event.tags" for a tagged rule match
         * (the reference lognormalizer CLI strips it by default; -T re-enables it
         * — since we're testing the library API directly, it is always present). */
        AssertJsonEquals("""{ "tag": "TAG", "event.tags": ["WIN"], "annot1": "WIN" }""",
            TestHelpers.Normalize(rb, "<37>1 2016-11-03T23:59:59+03:00 server.example.net TAG . - -").Json);
        AssertJsonEquals("""{ "tag": "TAG", "event.tags": ["ABC"], "annot2": "ABC" }""",
            TestHelpers.Normalize(rb, "<37>1 2016-11-03T23:59:59+03:00 server.example.net TAG + - -").Json);
        AssertJsonEquals("""{ "tag": "TAG", "event.tags": ["ABC", "WIN"], "annot1": "WIN", "annot2": "ABC" }""",
            TestHelpers.Normalize(rb, "<6>1 2016-09-02T07:41:07+02:00 server.example.net TAG - - -").Json);
    }

    [TestMethod]
    public void Backtracking_PicksMostSpecificMatchingRuleAmongSharedPrefix()
    {
        const string rb = """
            rule=:%iface:char-to:\x3a%\x3a%ip:ipv4%/%port:number% (%label2:char-to:)%)
            rule=:%iface:char-to:\x3a%\x3a%ip:ipv4%/%port:number% (%label2:char-to:)%)%tail:rest%
            rule=:%iface:char-to:\x3a%\x3a%ip:ipv4%/%port:number%
            rule=:%iface:char-to:\x3a%\x3a%ip:ipv4%/%port:number%%tail:rest%
            """;
        /* note: the "no-tail" rule and its "with-tail" sibling share a DAG node; since
         * "rest" always matches (even zero bytes), it takes priority over stopping at
         * this node's own terminal status, so "tail":"" is genuinely present here — the
         * upstream shell test's assert_output_json_eq only checks a subset of fields,
         * which is why its fixture omits it (verified against the C reference binary). */
        AssertJsonEquals(
            """{ "tail": "", "label2": "40.30.20.10/35", "port": "35", "ip": "10.20.30.40", "iface": "Outside" }""",
            TestHelpers.Normalize(rb, "Outside:10.20.30.40/35 (40.30.20.10/35)").Json);

        AssertJsonEquals(
            """{ "tail": " with rest", "label2": "40.30.20.10/35", "port": "35", "ip": "10.20.30.40", "iface": "Outside" }""",
            TestHelpers.Normalize(rb, "Outside:10.20.30.40/35 (40.30.20.10/35) with rest").Json);

        AssertJsonEquals(
            """{ "tail": " (40.30.20.10/35 brace missing", "port": "35", "ip": "10.20.30.40", "iface": "Outside" }""",
            TestHelpers.Normalize(rb, "Outside:10.20.30.40/35 (40.30.20.10/35 brace missing").Json);

        var (r, j) = TestHelpers.Normalize(rb, "Outside:10.20.30.40/aa 40.30.20.10/35");
        Assert.AreNotEqual(0, r);
        AssertJsonEquals("""
            { "originalmsg": "Outside:10.20.30.40/aa 40.30.20.10/35", "unparsed-data": "aa 40.30.20.10/35" }
            """, j);
    }

    [TestMethod]
    public void CustomType_DotDotNamePushesValueUpToCallSite()
    {
        const string rb = """
            type=@IPaddr:%..:ipv4%
            type=@IPaddr:%..:ipv6%
            rule=:an ip address %ip:@IPaddr%
            """;
        AssertJsonEquals("""{ "ip": "10.0.0.1" }""", TestHelpers.Normalize(rb, "an ip address 10.0.0.1").Json);
        AssertJsonEquals("""{ "ip": "127::1" }""", TestHelpers.Normalize(rb, "an ip address 127::1").Json);
    }

    [TestMethod]
    public void CustomType_NestedDotDotUnwrapsUnderNamedIntermediateType()
    {
        const string rb = """
            type=@byte:%..:hexnumber%
            type=@twobytes:%f1:@byte% %f2:@byte%
            rule=:two bytes %.:@twobytes% stop
            """;

        AssertJsonEquals("""{ "f1":"0xff", "f2":"0x16" }""",
            TestHelpers.Normalize(rb, "two bytes 0xff 0x16 stop").Json);
    }

    [TestMethod]
    public void CustomType_NamedFieldInsideTypeDefinition()
    {
        const string rb = """
            type=@IPaddr:%ip:ipv4%
            type=@IPaddr:%ip:ipv6%
            rule=:an ip address %.:@IPaddr%
            """;
        AssertJsonEquals("""{ "ip": "10.0.0.1" }""", TestHelpers.Normalize(rb, "an ip address 10.0.0.1").Json);
        AssertJsonEquals("""{ "ip": "127::1" }""", TestHelpers.Normalize(rb, "an ip address 127::1").Json);
        AssertJsonEquals("""{ "ip": "2001:DB8:0:1::10:1FF" }""",
            TestHelpers.Normalize(rb, "an ip address 2001:DB8:0:1::10:1FF").Json);
    }


    [TestMethod]
    public void FullJsonFieldFollowedByEscapedPercentKeepsRuleOnOneLogicalLine()
    {
        const string rb = """
            rule=:Rule-ID: %.:string{"matching.permitted":[
                {"class":"digit"},
                {"chars":"abcdefghijklmnopqrstuvwxyz"},
                {"chars":"ABCDEFGHIJKLMNOPQRSTUVWXYZ"},
                {"chars":"-"},
            ], "quoting.escape.mode":"none", "matching.mode":"lazy"}%%resta:rest%
            """;

        AssertJsonEquals("""{ ".": "XY7azl704-84a39894783423467a33f5b48bccd23c-a0n63i2", "resta": " LWL" }""",
            TestHelpers.Normalize(rb, "Rule-ID: XY7azl704-84a39894783423467a33f5b48bccd23c-a0n63i2 LWL").Json);
    }

    [TestMethod]
    public void Prefix_LineIsPrependedToEveryRule()
    {
        const string rb = "prefix=%timestamp:date-rfc3164% %hostname:word% \n" +
                    "rule=:hello %name:word%";
        var (r, j) = TestHelpers.Normalize(rb, "Aug 18 13:18:45 myhost hello world");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""
            { "timestamp": "Aug 18 13:18:45", "hostname": "myhost", "name": "world" }
            """, j);
    }

    [TestMethod]
    public void PrefixRules_ShorterRuleStillMatchesWhenLongerOneFails()
    {
        const string rb = """
            rule=:a word %w1:word%
            rule=:a word %w1:word% another word %w2:word%
            """;
        AssertJsonEquals("""{ "w2": "w2", "w1": "w1" }""", TestHelpers.Normalize(rb, "a word w1 another word w2").Json);
        AssertJsonEquals("""{ "w1": "w1" }""", TestHelpers.Normalize(rb, "a word w1").Json);
    }

    [TestMethod]
    public void Repeat_AcceptsNestedAlternativeWithTrailingCommas()
    {
        const string rb = """
            rule=:a %{ "name":"numbers", "type":"repeat",
                "parser": { "type":"alternative", "parser":[
                    [ {"type":"number", "name":"n1"}, {"type":"literal", "text":":"}, {"type":"number", "name":"n2"}, ],
                    {"type":"hexnumber", "name":"hex"}
                ] },
                "while":[ {"type":"literal", "text":", "} ]
            }% b
            """;
        AssertJsonEquals("""{ "numbers":[ { "n1":"1", "n2":"2" }, { "n1":"3", "n2":"4" } ] }""",
            TestHelpers.Normalize(rb, "a 1:2, 3:4 b").Json);
        AssertJsonEquals("""{ "numbers":[ { "hex":"0x4711" } ] }""",
            TestHelpers.Normalize(rb, "a 0x4711 b").Json);
    }

    [TestMethod]
    public void Repeat_CollectsMultiFieldPerIteration()
    {
        const string rb = """
            rule=:a %{"name":"numbers", "type":"repeat",
                "parser":[ {"name":"n1", "type":"number"}, {"type":"literal", "text":":"}, {"name":"n2", "type":"number"} ],
                "while":[ {"type":"literal", "text":", "} ]
               }% b %w:word%
            """;
        var (r, j) = TestHelpers.Normalize(rb, "a 1:2, 3:4, 5:6, 7:8 b test");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""
            { "w": "test", "numbers": [ { "n2": "2", "n1": "1" }, { "n2": "4", "n1": "3" },
                                         { "n2": "6", "n1": "5" }, { "n2": "8", "n1": "7" } ] }
            """, j);
    }

    [TestMethod]
    public void Repeat_CollectsSingleFieldPerIteration()
    {
        const string rb = """
            rule=:a %{"name":"numbers", "type":"repeat", "parser": {"name":"n", "type":"number"}, "while": {"type":"literal", "text":", "} }% b %w:word%
            """;
        var (r, j) = TestHelpers.Normalize(rb, "a 1, 2, 3, 4 b test");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""
            { "w": "test", "numbers": [ { "n": "1" }, { "n": "2" }, { "n": "3" }, { "n": "4" } ] }
            """, j);
    }

    [TestMethod]
    public void Repeat_FailOnDuplicate_SkipsAlreadyExtractedFields()
    {
        /* merge-mode repeat: fields committed by earlier rounds must be
         * visible to later rounds' duplicate check, steering round 2 to the
         * second alternative. This pins the extraction timing the two-phase
         * (measure, then materialize on unwind) walker must preserve. */
        const string rb = """
            rule=:l %{"name":".", "type":"repeat", "option.failOnDuplicate":true,
                "parser": {"type":"alternative", "parser":[ {"name":"a", "type":"number"}, {"name":"b", "type":"number"} ]},
                "while": {"type":"literal", "text":" "} }%
            """;
        var (r, j) = TestHelpers.Normalize(rb, "l 1 2");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{ "a": "1", "b": "2" }""", j);
    }

    [TestMethod]
    public void Repeat_PermitMismatchInParser_StopsAtLastGoodMatch()
    {
        const string rb = """
            rule=:l %{"name":"ns", "type":"repeat", "option.permitMismatchInParser":true,
                "parser": {"name":"n", "type":"number"},
                "while": {"type":"literal", "text":" "} }% END
            """;
        var (r, j) = TestHelpers.Normalize(rb, "l 1 2 END");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{ "ns": [ { "n": "1" }, { "n": "2" } ] }""", j);
    }

    [TestMethod]
    public void Repeat_StopsWhenParserAndWhileBothMatchZeroWidth()
    {
        /* "char-separated" always succeeds and can match zero characters
         * when already positioned on a terminator; if both "parser" and
         * "while" do so at the same offset forever, the loop must break
         * instead of hanging. */
        const string rb = """
            rule=:a %{"name":"reps", "type":"repeat",
                "parser": {"name":"n", "type":"char-sep", "extradata":","},
                "while": {"type":"char-sep", "extradata":","} }% b
            """;
        var task = System.Threading.Tasks.Task.Run(() => TestHelpers.Normalize(rb, "a ,b"));
        Assert.IsTrue(task.Wait(System.TimeSpan.FromSeconds(5), TestContext.CancellationToken), "repeat parser hung instead of terminating");
    }
}
