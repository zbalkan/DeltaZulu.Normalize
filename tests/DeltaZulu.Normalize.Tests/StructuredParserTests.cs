using Microsoft.VisualStudio.TestTools.UnitTesting;
using static DeltaZulu.Normalize.Tests.TestHelpers;

namespace DeltaZulu.Normalize.Tests;

/// <summary>Cases ported from tests/field_json.sh, field_cef.sh, field_checkpoint-lea.sh, field_name_value.sh.</summary>
[TestClass]
public class StructuredParserTests
{
    [TestMethod]
    public void Json_ExtractsEmbeddedObject()
    {
        var (r, j) = TestHelpers.Normalize("rule=:%field:json%", """{"f1": "1", "f2": 2}""");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{ "field": { "f1": "1", "f2": 2 } }""", j);
    }

    [TestMethod]
    public void Json_ConsumesTrailingWhitespaceAsPartOfMatch()
    {
        const string rb = """
            rule=:begin %field:json%
            rule=:begin %field:json%end
            rule=:%field:json%end
            rule=:%field:json%
            """;
        AssertJsonEquals("""{ "field": { "f1": "1", "f2": 2 } }""",
            TestHelpers.Normalize(rb, """{"f1": "1", "f2": 2}      """).Json);
        AssertJsonEquals("""{ "field": { "f1": "1", "f2": 2 } }""",
            TestHelpers.Normalize(rb, """begin {"f1": "1", "f2": 2} end""").Json);
    }

    [TestMethod]
    public void Json_TwoFieldsInOneRule()
    {
        var (r, j) = TestHelpers.Normalize("rule=:%field1:json%-%field2:json%", """{"f1": "1"}-{"f2": 2}""");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{ "field2": { "f2": 2 }, "field1": { "f1": "1" } }""", j);
    }

    [TestMethod]
    public void Json_RejectsInvalidJsonAndBareNumberLookingLikeTime()
    {
        var (r1, _) = TestHelpers.Normalize("rule=:%field:json%", """{"f1": "1", f2: 2}""");
        Assert.AreNotEqual(0, r1);

        /* regression guard for json-c issue #181: a bare "15:00" must not be misdetected as JSON */
        var (r2, j2) = TestHelpers.Normalize("rule=:%field:json%", "15:00");
        Assert.AreNotEqual(0, r2);
        AssertJsonEquals("""{ "originalmsg": "15:00", "unparsed-data": "15:00" }""", j2);
    }

    [TestMethod]
    public void Cef_ParsesHeaderAndExtensions()
    {
        var (r, j) = TestHelpers.Normalize("rule=:%f:cef%",
            "CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| aa=field1 bb=this is a value cc=field 3");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""
            { "f": { "DeviceVendor": "Vendor", "DeviceProduct": "Product", "DeviceVersion": "Version",
                     "SignatureID": "Signature ID", "Name": "some name", "Severity": "Severity",
                     "Extensions": { "aa": "field1", "bb": "this is a value", "cc": "field 3" } } }
            """, j);
    }

    [TestMethod]
    public void Cef_HandlesEscapedPipeAndEqualsAndBackslash()
    {
        var (r, j) = TestHelpers.Normalize("rule=:%f:cef%",
            "CEF:0|Vendor|Product\\|1\\|\\\\|Version|Signature ID|some name|Severity| aa=field1 bb=this is a name\\=value cc=field 3");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""
            { "f": { "DeviceVendor": "Vendor", "DeviceProduct": "Product|1|\\", "DeviceVersion": "Version",
                     "SignatureID": "Signature ID", "Name": "some name", "Severity": "Severity",
                     "Extensions": { "aa": "field1", "bb": "this is a name=value", "cc": "field 3" } } }
            """, j);
    }

    [TestMethod]
    public void Cef_RejectsInvalidExtensionPunctuationAndEscape()
    {
        var (r1, _) = TestHelpers.Normalize("rule=:%f:cef%",
            "CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| n,me=value");
        Assert.AreNotEqual(0, r1);

        var (r2, _) = TestHelpers.Normalize("rule=:%f:cef%",
            "CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| name=v\\alue");
        Assert.AreNotEqual(0, r2);
    }

    [TestMethod]
    public void Cef_RejectsDanglingBackslashAtEndOfLastExtensionValue()
    {
        /* a trailing, unfinished escape in the very last extension value must
         * be rejected, not crash the extraction with an out-of-range access */
        var (r, _) = TestHelpers.Normalize("rule=:%f:cef%",
            "CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| bb=value\\");
        Assert.AreNotEqual(0, r);
    }

    [TestMethod]
    public void CheckpointLea_ParsesSemicolonTerminatedFields()
    {
        var (r, j) = TestHelpers.Normalize("rule=:%f:checkpoint-lea%", "tcp_flags: RST-ACK; src: 192.168.0.1;");
        Assert.AreEqual(0, r);
        AssertJsonEquals("""{ "f": { "tcp_flags": "RST-ACK", "src": "192.168.0.1" } }""", j);
    }

    [TestMethod]
    public void NameValueList_ParsesMultiplePairs()
    {
        const string rb = "rule=:%f:name-value-list%";
        AssertJsonEquals("""{ "f": { "name": "value" } }""", TestHelpers.Normalize(rb, "name=value").Json);
        AssertJsonEquals(
            """{ "f": { "name1": "value1", "name2": "value2", "name3": "value3" } }""",
            TestHelpers.Normalize(rb, "name1=value1 name2=value2 name3=value3").Json);
        AssertJsonEquals(
            """{ "f": { "name1": "", "name2": "value2", "name3": "value3" } }""",
            TestHelpers.Normalize(rb, "name1= name2=value2 name3=value3 ").Json);
    }

    [DataTestMethod]
    [DataRow("name")]
    [DataRow("noname1 name2=value2 name3=value3 ")]
    public void NameValueList_RejectsMalformedInput(string message)
    {
        var (r, _) = TestHelpers.Normalize("rule=:%f:name-value-list%", message);
        Assert.AreNotEqual(0, r);
    }
}
