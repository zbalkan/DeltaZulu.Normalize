using static DeltaZulu.Normalize.Tests.TestHelpers;

namespace DeltaZulu.Normalize.Tests;

/// <summary>
/// <para>
/// Real-world rulebase patterns from production rulebases (Sagan, rsyslog,
/// liblognorm-rulebase, etc.). Tests edge cases, escape sequences, and
/// complex patterns found in actual log normalization use cases.
/// </para>
/// <para>
/// Note: in the v2 rulebase format the sample starts immediately after the
/// tag-separating ':' of "rule=...:", so any whitespace written there is
/// part of the pattern and must appear in the message as well.
/// </para>
/// </summary>
[TestClass]
public class RealWorldRulebaseTests
{
    [TestMethod]
    public void EscapedColonsInLiterals()
    {
        // "\:" is not a recognized escape (es_unescapeStr keeps the backslash
        // verbatim); a colon inside a literal is either written plainly or as
        // the hex escape "\x3a".
        const string rb = "rule=:error\\x3a PAM\\x3a Authentication failure for %user:word%";
        var (r, j) = TestHelpers.Normalize(rb, "error: PAM: Authentication failure for jsmith");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"user": "jsmith"}""", j);
    }

    [TestMethod]
    public void HexEncodedDelimiterColon()
    {
        // char-to matches up to — but does not consume — the terminator, so
        // the pattern must repeat it as a literal after the field.
        const string rb = "rule=:%field1:char-to:\\x3a%: %field2:word%";
        var (r, j) = TestHelpers.Normalize(rb, "value1: value2");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"field1": "value1", "field2": "value2"}""", j);
    }

    [TestMethod]
    public void HexEncodedDelimiterComma()
    {
        const string rb = "rule=:%field1:char-sep:\\x2c%,%field2:word%";
        var (r, j) = TestHelpers.Normalize(rb, "value1,value2");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"field1": "value1", "field2": "value2"}""", j);
    }

    [TestMethod]
    public void HexEncodedDelimiterSingleQuote()
    {
        const string rb = "rule=:%field:char-to:\\x27%'";
        var (r, j) = TestHelpers.Normalize(rb, "myvalue'");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"field": "myvalue"}""", j);
    }

    [TestMethod]
    public void HexEncodedBackslashInDomainUsername()
    {
        const string rb = "rule=:User: %domain:char-to:\\x5c%\\%username:word%";
        var (r, j) = TestHelpers.Normalize(rb, "User: MYDOMAIN\\jsmith");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"domain": "MYDOMAIN", "username": "jsmith"}""", j);
    }

    [TestMethod]
    public void IgnoredFieldWithWord()
    {
        const string rb = "rule=:%-:word% %user:word% %action:word%";
        var (r, j) = TestHelpers.Normalize(rb, "skipped user1 login");
        Assert.AreEqual(0, r);
        // Unnamed fields should not appear in output
        AssertJsonEquals("""{"user": "user1", "action": "login"}""", j);
    }

    [TestMethod]
    public void IgnoredFieldWithRest()
    {
        const string rb = "rule=:%user:word% %-:rest%";
        var (r, j) = TestHelpers.Normalize(rb, "jsmith some ignored data here");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"user": "jsmith"}""", j);
    }

    [TestMethod]
    public void IPv4InIPv6Notation()
    {
        const string rb = "rule=:Client Address: ::ffff:%src:ipv4%";
        var (r, j) = TestHelpers.Normalize(rb, "Client Address: ::ffff:192.168.1.1");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"src": "192.168.1.1"}""", j);
    }

    [TestMethod]
    public void MultipleRuleVariantsWithWhitespace()
    {
        const string rb = """
            rule=:SSH from %src:ipv4% on port %port:number%
            rule=:SSH from %src:ipv4%  on port %port:number%
            """;

        // Single space variant
        var (r1, j1) = TestHelpers.Normalize(rb, "SSH from 10.0.0.1 on port 22");
        Assert.AreEqual(0, r1);
        AssertJsonEquals("""{"src": "10.0.0.1", "port": "22"}""", j1);

        // Double space variant
        var (r2, j2) = TestHelpers.Normalize(rb, "SSH from 10.0.0.1  on port 22");
        Assert.AreEqual(0, r2);
        AssertJsonEquals("""{"src": "10.0.0.1", "port": "22"}""", j2);
    }

    [TestMethod]
    public void PrefixDirectiveAccumulation()
    {
        const string rb = """
            prefix=PREFIX1_
            extendprefix=PREFIX2_
            rule=:%field:word%
            """;

        var (r, j) = TestHelpers.Normalize(rb, "PREFIX1_PREFIX2_value");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"field": "value"}""", j);
    }

    [TestMethod]
    public void ComplexFieldSequenceWithMixedMotifs()
    {
        const string rb = """
            rule=:%timestamp:time-24hr% %hostname:word% %tag:char-to:\x3a%: %message:rest%
            """;

        var (r, j) = TestHelpers.Normalize(rb, "14:23:45 myhost sshd: Connection closed by 10.0.0.1");
        Assert.AreEqual(0, r);
        AssertJsonContains(j, "timestamp", "14:23:45");
        AssertJsonContains(j, "hostname", "myhost");
        AssertJsonContains(j, "tag", "sshd");
        AssertJsonContains(j, "message", "Connection closed by 10.0.0.1");
    }

    [TestMethod]
    public void UserDefinedTypeWithPrefix()
    {
        const string rb = """
            type=@ipport:%ip:ipv4%#%port:number%
            rule=:Service %name:word% listening on %addr:@ipport%
            """;

        var (r, j) = TestHelpers.Normalize(rb, "Service sshd listening on 192.168.1.1#22");
        Assert.AreEqual(0, r);
        AssertJsonContains(j, "name", "sshd");
        AssertJsonContains(j, "addr", """{"ip": "192.168.1.1", "port": "22"}""");
    }

    [TestMethod]
    public void StringToMotifWithCustomDelimiter()
    {
        // string-to's search string (extradata) may be several characters —
        // here " to ". A single-char search string never matches (the C
        // scanner's inner loop quirk, kept by this port), and the delimiter
        // is not consumed, so it must follow as a literal.
        const string rb = "rule=:From %sender:string-to: to % to %receiver:word%";
        var (r, j) = TestHelpers.Normalize(rb, "From user1@example.com to admin");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"sender": "user1@example.com", "receiver": "admin"}""", j);
    }

    [TestMethod]
    public void CharSeparatedWithMultipleOccurrences()
    {
        // char-sep does not consume the separator; each ',' is re-matched as
        // a literal between the fields.
        const string rb = "rule=:Values: %v1:char-sep:\\x2c%, %v2:char-sep:\\x2c%, %v3:char-sep:\\x2c%";
        var (r, j) = TestHelpers.Normalize(rb, "Values: val1, val2, val3");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"v1": "val1", "v2": "val2", "v3": "val3"}""", j);
    }

    [TestMethod]
    public void QuotedStringVariants()
    {
        const string rb = "rule=:Message: %msg:quoted-string%";
        var (r, j) = TestHelpers.Normalize(rb, "Message: \"error occurred\"");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"msg": "error occurred"}""", j);
    }

    [TestMethod]
    public void EmptyFieldInSequence()
    {
        // word is greedy up to the next space and fails on a zero-length
        // match, so two adjacent word fields can never both match; char-sep
        // however may match empty, giving an empty field value in sequence.
        const string rb = "rule=:%field1:word%%field2:char-sep:\\x20% %field3:word%";
        var (r, j) = TestHelpers.Normalize(rb, "value1value2 value3");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"field1": "value1value2", "field2": "", "field3": "value3"}""", j);
    }

    [TestMethod]
    public void EscapedDoublePercentInLiteral()
    {
        const string rb = "rule=:Progress: 100%% complete at %time:word%";
        var (r, j) = TestHelpers.Normalize(rb, "Progress: 100% complete at noon");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{"time": "noon"}""", j);
    }

    [TestMethod]
    public void DateRfc3164WithSyslogFormat()
    {
        const string rb = "rule=:%timestamp:date-rfc3164% %hostname:word% %tag:char-to:\\x5b%[%pid:number%]: %msg:rest%";
        var (r, j) = TestHelpers.Normalize(rb, "Oct 29 09:47:08 myhost sshd[1234]: Connection accepted");
        Assert.AreEqual(0, r);
        AssertJsonContains(j, "timestamp", "Oct 29 09:47:08");
        AssertJsonContains(j, "hostname", "myhost");
        AssertJsonContains(j, "tag", "sshd");
        AssertJsonContains(j, "pid", "1234");
    }

    [TestMethod]
    public void OverlappingRulePrefix()
    {
        const string rb = """
            prefix=ERR:
            rule=:%code:number%
            rule=:%code:number% %detail:word%
            """;

        var (r1, j1) = TestHelpers.Normalize(rb, "ERR:404");
        Assert.AreEqual(0, r1);
        AssertJsonContains(j1, "code", "404");

        var (r2, j2) = TestHelpers.Normalize(rb, "ERR:500 database");
        Assert.AreEqual(0, r2);
        AssertJsonContains(j2, "code", "500");
        AssertJsonContains(j2, "detail", "database");
    }

    [TestMethod]
    public void AlternativeFieldMatching()
    {
        const string rb = """
            rule=:User %user:word% logged in from %src:ipv4%
            rule=:User %user:word% logged in from %src:word%
            """;

        var (r1, j1) = TestHelpers.Normalize(rb, "User jsmith logged in from 192.168.1.100");
        Assert.AreEqual(0, r1);
        AssertJsonContains(j1, "src", "192.168.1.100");

        var (r2, j2) = TestHelpers.Normalize(rb, "User jsmith logged in from vpn.example.com");
        Assert.AreEqual(0, r2);
        AssertJsonContains(j2, "src", "vpn.example.com");
    }

    [TestMethod]
    public void NumberFieldWithFormatModifier()
    {
        const string rb = "rule=:%code:number{\"format\": \"number\"}%";
        var (r, j) = TestHelpers.Normalize(rb, "42");
        Assert.AreEqual(0, r);
        // Extract as number, not string
        AssertJsonEquals("""{"code": 42}""", j);
    }
}