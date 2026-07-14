using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize.Tests;

[TestClass]
public class FieldCollectorTests
{
    private static string WriteToString(FieldCollector c)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            c.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [TestMethod]
    public void Set_AppendsInOrder()
    {
        var c = new FieldCollector();
        c.Set("a", FieldValue.Span("hello world", 0, 5));
        c.Set("b", FieldValue.Node(JsonValue.Create(42)));
        c.Set("c", FieldValue.Node(null));

        Assert.AreEqual(3, c.Count);
        Assert.AreEqual("a", c.NameAt(0));
        Assert.AreEqual("b", c.NameAt(1));
        Assert.AreEqual("c", c.NameAt(2));
        Assert.AreEqual("""{"a":"hello","b":42,"c":null}""", WriteToString(c));
    }

    [TestMethod]
    public void Set_ReplacesInPlaceKeepingPosition()
    {
        var c = new FieldCollector();
        c.Set("a", FieldValue.Span("first", 0, 5));
        c.Set("b", FieldValue.Span("second", 0, 6));
        c.Set("a", FieldValue.Span("third", 0, 5));

        Assert.AreEqual(2, c.Count);
        Assert.AreEqual("a", c.NameAt(0));
        Assert.AreEqual("""{"a":"third","b":"second"}""", WriteToString(c));
    }

    [TestMethod]
    public void Set_GrowsPastInitialCapacity()
    {
        var c = new FieldCollector();
        for (var i = 0; i < 20; ++i)
        {
            c.Set($"f{i}", FieldValue.Node(JsonValue.Create(i)));
        }

        Assert.AreEqual(20, c.Count);
        Assert.AreEqual("f0", c.NameAt(0));
        Assert.AreEqual("f19", c.NameAt(19));
        Assert.AreEqual(19, c.ToJsonObject()["f19"]!.GetValue<int>());
    }

    [TestMethod]
    public void IndexOf_IsOrdinal()
    {
        var c = new FieldCollector();
        c.Set("Field", FieldValue.Node(JsonValue.Create(1)));

        Assert.IsTrue(c.Contains("Field"));
        Assert.IsFalse(c.Contains("field"));
        Assert.AreEqual(-1, c.IndexOf("missing"));
    }

    [TestMethod]
    public void ToJsonObject_MaterializesInEntryOrder()
    {
        var nested = new FieldCollector();
        nested.Set("inner", FieldValue.Span("xyz", 1, 2));

        var c = new FieldCollector();
        c.Set("s", FieldValue.Span("abcdef", 2, 3));
        c.Set("n", FieldValue.Node(new JsonObject { ["k"] = "v" }));
        c.Set("o", FieldValue.Object(nested));
        c.Set("z", FieldValue.Node(null));

        var obj = c.ToJsonObject();
        Assert.AreSequenceEqual(new[] { "s", "n", "o", "z" }, obj.Select(kv => kv.Key).ToArray());
        Assert.AreEqual("cde", obj["s"]!.GetValue<string>());
        Assert.AreEqual("v", obj["n"]!["k"]!.GetValue<string>());
        Assert.AreEqual("yz", obj["o"]!["inner"]!.GetValue<string>());
        Assert.IsNull(obj["z"]);
    }

    [TestMethod]
    public void ToJsonObject_DetachesAlreadyParentedNodes()
    {
        var parent = new JsonObject();
        var child = JsonValue.Create("owned");
        parent["p"] = child;

        var c = new FieldCollector();
        c.Set("a", FieldValue.Node(child));

        var obj = c.ToJsonObject();
        Assert.AreEqual("owned", obj["a"]!.GetValue<string>());
        /* original parent keeps its node; the collector cloned on materialize */
        Assert.AreEqual("owned", parent["p"]!.GetValue<string>());
    }

    [TestMethod]
    public void FieldValue_DefaultIsNullKind()
    {
        FieldValue v = default;
        Assert.AreEqual(FieldValueKind.Null, v.Kind);
        Assert.IsNull(v.ToJsonNode());
        Assert.AreEqual(FieldValueKind.Null, FieldValue.Node(null).Kind);
    }

    [TestMethod]
    public void FieldValue_ZeroLengthSpanIsNotNull()
    {
        var v = FieldValue.Span("abc", 1, 0);
        Assert.AreEqual(FieldValueKind.Span, v.Kind);
        var node = v.ToJsonNode();
        Assert.AreEqual(string.Empty, node!.GetValue<string>());
    }

    [TestMethod]
    public void FieldValue_FullStringSpanMaterializesOriginalInstance()
    {
        const string source = "the whole message";
        var v = FieldValue.Span(source, 0, source.Length);
        var node = v.ToJsonNode();
        Assert.IsTrue(ReferenceEquals(source, node!.GetValue<string>()));
    }
}
