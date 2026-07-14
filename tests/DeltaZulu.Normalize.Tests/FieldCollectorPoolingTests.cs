using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize.Tests;

/// <summary>
/// The classic Normalize(out JsonObject) overload rents its scratch
/// FieldCollector's backing array from ArrayPool&lt;Entry&gt;.Shared instead of
/// allocating it fresh every call, since the collector is discarded the
/// instant it is materialized. These pin the correctness properties that
/// make reusing that array across calls (and across threads) safe: results
/// must not leak between calls, growth past the pooled capacity must not
/// corrupt or double-return the array, and concurrent callers must not
/// observe each other's in-flight state.
/// </summary>
[TestClass]
public class FieldCollectorPoolingTests
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void ConcurrentNormalizeCalls_DoNotCorruptEachOthersResults()
    {
        var ctx = new LogNormContext();
        Assert.AreEqual(0, ctx.LoadSamplesFromString("rule=:msg %n:number% payload %f:rest%"));

        var failures = new ConcurrentQueue<string>();
        var tasks = Enumerable.Range(0, Environment.ProcessorCount * 2).Select(t => Task.Run(() => {
            for (var i = 0; i < 500; i++)
            {
                var msg = $"msg {t} payload thread{t}-iter{i}";
                if (ctx.Normalize(msg, out JsonObject j) != 0
                    || j["n"]?.GetValue<string>() != t.ToString()
                    || j["f"]?.GetValue<string>() != $"thread{t}-iter{i}")
                {
                    failures.Enqueue($"thread {t} iter {i}: got {j.ToJsonString()}");
                    return;
                }
            }
        }, TestContext.CancellationToken)).ToArray();

        Assert.IsTrue(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), "concurrent normalization workers did not finish within the timeout");
        Assert.IsEmpty(failures, string.Join("\n", failures));
    }

    [TestMethod]
    public void FailedMatch_AlsoRoundTripsThePooledArrayCorrectly()
    {
        /* a non-match still commits exactly two fields (originalmsg,
         * unparsed-data) via AddUnparsedField, exercising the same
         * rent/return path as a successful match */
        var ctx = new LogNormContext();
        Assert.AreEqual(0, ctx.LoadSamplesFromString("rule=:hello %f:word%"));

        for (var i = 0; i < 20; i++)
        {
            var msg = $"goodbye {i}";
            Assert.AreNotEqual(0, ctx.Normalize(msg, out JsonObject j));
            Assert.AreEqual(2, j.Count);
            Assert.AreEqual(msg, j["originalmsg"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public void MoreFieldsThanPooledCapacity_GrowsAndReturnsCorrectResult()
    {
        /* ArrayPool<T>.Shared buckets round a Rent(4) request up to its
         * smallest bucket (16 entries), so a handful of extra fields is not
         * enough to force Grow() - the test must exceed that real bucket
         * size, not just the requested InitialCapacity, to actually drive
         * the pooled-array-returned-mid-flight / heap-array-fallback path
         * and the double-return safeguard. */
        const int fieldCount = 20;
        var ctx = new LogNormContext();
        var fields = string.Join(" ", Enumerable.Range(1, fieldCount).Select(i => $"%f{i}:word%"));
        Assert.AreEqual(0, ctx.LoadSamplesFromString($"rule=:a {fields}"));

        var msg = "a " + string.Join(" ", Enumerable.Range(1, fieldCount));
        Assert.AreEqual(0, ctx.Normalize(msg, out JsonObject j));
        for (var i = 1; i <= fieldCount; i++)
        {
            Assert.AreEqual(i.ToString(), j[$"f{i}"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public void RepeatedCalls_DoNotLeakFieldsAcrossPooledArrayReuse()
    {
        var ctx = new LogNormContext();
        Assert.AreEqual(0, ctx.LoadSamplesFromString("rule=:a %f:word%"));

        for (var i = 0; i < 50; i++)
        {
            var msg = $"a value{i}";
            Assert.AreEqual(0, ctx.Normalize(msg, out JsonObject j));
            Assert.AreEqual(1, j.Count, $"iteration {i}: stale fields from a previous call leaked in: {j.ToJsonString()}");
            Assert.AreEqual($"value{i}", j["f"]!.GetValue<string>());
        }
    }
}
