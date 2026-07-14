using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize.Tests;

internal static class TestHelpers
{
    public static void AssertJsonContains(JsonNode? json, string fieldName, string expectedValue)
    {
        if (json is not JsonObject)
        {
            Assert.Fail($"Expected JSON object, got {json?.GetType().Name ?? "null"}");
        }

        // Safely cast now that the guard clause has passed
        var obj = json.AsObject();

        if (!obj.TryGetPropertyValue(fieldName, out var fieldValue))
        {
            Assert.Fail($"Field '{fieldName}' not found in JSON: {obj.ToJsonString()}");
        }

        /* an expectation written as a JSON object/array literal is compared
          * structurally (order-independent); anything else is the expected
          * plain string value of the field */
        var trimmed = expectedValue.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            var expectedNode = JsonNode.Parse(expectedValue);
            if (!JsonNode.DeepEquals(Normalize(expectedNode), Normalize(fieldValue?.DeepClone())))
            {
                Assert.Fail($"Field '{fieldName}' mismatch.\nExpected: {expectedNode?.ToJsonString()}\nActual:   {fieldValue?.ToJsonString() ?? "null"}");
            }
        }
        else
        {
            var actualValue = fieldValue is JsonValue jv && jv.TryGetValue(out string? s)
               ? s
               : fieldValue?.ToJsonString() ?? "null";
            if (actualValue != expectedValue)
            {
                Assert.Fail($"Field '{fieldName}' mismatch.\nExpected: {expectedValue}\nActual:   {actualValue}");
            }
        }
    }

    /// <summary>Assert that the normalized JSON output equals the expected JSON (order-independent).</summary>
    public static void AssertJsonEquals(string expectedJson, JsonNode? actual)
    {
        var expected = JsonNode.Parse(expectedJson);
        var equal = JsonNode.DeepEquals(Normalize(expected), Normalize(actual));
        if (!equal)
        {
            Assert.Fail($"JSON mismatch.\nExpected: {expected?.ToJsonString()}\nActual:   {actual?.ToJsonString()}");
        }
    }

    /// <summary>Build a context from rulebase body text (without the "version=2" header) and normalize one message.</summary>
    public static (int Result, JsonObject Json) Normalize(string rulebaseBody, string message, LogNormOptions options = LogNormOptions.None)
    {
        var ctx = new LogNormContext { Options = options };
        var errors = new List<string>();
        ctx.ErrorCallback = errors.Add;
        var loadResult = ctx.LoadSamplesFromString(rulebaseBody);
        Assert.AreEqual(0, loadResult, $"rulebase failed to load: {string.Join("; ", errors)}");

        var r = ctx.Normalize(message, out JsonObject json);
        return (r, json);
    }

    /// <summary>Recursively sort object keys so DeepEquals is order-independent.</summary>
    private static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var sorted = new JsonObject();
                    foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                    {
                        sorted[key] = Normalize(obj[key]?.DeepClone());
                    }

                    return sorted;
                }
            case JsonArray arr:
                {
                    var result = new JsonArray();
                    foreach (var item in arr)
                    {
                        result.Add(Normalize(item?.DeepClone()));
                    }

                    return result;
                }
            default:
                return node;
        }
    }
}
