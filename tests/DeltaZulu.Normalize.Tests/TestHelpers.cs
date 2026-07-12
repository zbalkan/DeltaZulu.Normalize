using System.Text.Json.Nodes;
using DeltaZulu.Normalize;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeltaZulu.Normalize.Tests;

internal static class TestHelpers
{
    /// <summary>Build a context from rulebase body text (without the "version=2" header) and normalize one message.</summary>
    public static (int Result, JsonObject Json) Normalize(string rulebaseBody, string message, LogNormOptions options = LogNormOptions.None)
    {
        var ctx = new LogNormContext { Options = options };
        var errors = new List<string>();
        ctx.ErrorCallback = errors.Add;
        int loadResult = ctx.LoadSamplesFromString(rulebaseBody);
        Assert.IsTrue(loadResult == 0, $"rulebase failed to load: {string.Join("; ", errors)}");

        int r = ctx.Normalize(message, out JsonObject json);
        return (r, json);
    }

    /// <summary>Assert that the normalized JSON output equals the expected JSON (order-independent).</summary>
    public static void AssertJsonEquals(string expectedJson, JsonNode? actual)
    {
        JsonNode? expected = JsonNode.Parse(expectedJson);
        bool equal = JsonNode.DeepEquals(Normalize(expected), Normalize(actual));
        if (!equal)
        {
            Assert.Fail($"JSON mismatch.\nExpected: {expected?.ToJsonString()}\nActual:   {actual?.ToJsonString()}");
        }
    }

    /// <summary>Assert that a JSON object contains an expected field with a specific value.</summary>
    public static void AssertJsonContains(JsonNode? json, string fieldName, string expectedValue)
    {
        if (json is not JsonObject obj)
        {
            Assert.Fail($"Expected JSON object, got {json?.GetType().Name ?? "null"}");
        }
        if (!obj.TryGetPropertyValue(fieldName, out JsonNode? fieldValue))
        {
            Assert.Fail($"Field '{fieldName}' not found in JSON: {obj.ToJsonString()}");
        }
        string actualValue = fieldValue?.ToJsonString() ?? "null";
        JsonNode? expectedNode = JsonNode.Parse(expectedValue);
        string expectedJson = expectedNode?.ToJsonString() ?? "null";
        if (actualValue != expectedJson)
        {
            Assert.Fail($"Field '{fieldName}' mismatch.\nExpected: {expectedJson}\nActual:   {actualValue}");
        }
    }

    /// <summary>Recursively sort object keys so DeepEquals is order-independent.</summary>
    private static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var sorted = new JsonObject();
                foreach (string key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                    sorted[key] = Normalize(obj[key]?.DeepClone());
                return sorted;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (JsonNode? item in arr)
                    result.Add(Normalize(item?.DeepClone()));
                return result;
            }
            default:
                return node;
        }
    }
}
