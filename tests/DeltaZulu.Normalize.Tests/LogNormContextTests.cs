using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeltaZulu.Normalize.Tests;

/// <summary>Cases exercising the <see cref="LogNormContext"/> loading API directly (not single rulebase text).</summary>
[TestClass]
public class LogNormContextTests
{
    [TestMethod]
    public void LoadSamplesFromString_TwiceOnSameContext_BothRuleSetsMatch()
    {
        /* the second rule shares a literal prefix ("ab") with the first; the
         * PDAG must not compact literal paths until all loads are done, else
         * the second load's rule attaches after the first rule's compacted
         * "abc" chunk instead of after "ab" and never matches */
        var ctx = new LogNormContext();
        Assert.AreEqual(0, ctx.LoadSamplesFromString("rule=:abc %f:rest%"));
        Assert.AreEqual(0, ctx.LoadSamplesFromString("rule=:abd %f:rest%"));

        Assert.AreEqual(0, ctx.Normalize("abc hello", out JsonObject j1));
        Assert.AreEqual("hello", j1["f"]!.GetValue<string>());

        Assert.AreEqual(0, ctx.Normalize("abd world", out JsonObject j2));
        Assert.AreEqual("world", j2["f"]!.GetValue<string>());
    }

    [TestMethod]
    public void LoadSamplesFromString_AfterNormalize_HotReloads()
    {
        /* loading after first use recompiles the snapshot: old rules keep
         * matching (incl. the shared "ab" literal prefix, which must merge
         * with the earlier rule's path, not with its compacted form) and the
         * new rule becomes visible */
        var ctx = new LogNormContext();
        Assert.AreEqual(0, ctx.LoadSamplesFromString("rule=:abc %f:rest%"));
        Assert.AreEqual(0, ctx.Normalize("abc hello", out JsonObject j1));
        Assert.AreEqual("hello", j1["f"]!.GetValue<string>());

        Assert.AreEqual(0, ctx.LoadSamplesFromString("rule=:abd %f:rest%"));

        Assert.AreEqual(0, ctx.Normalize("abd world", out JsonObject j2));
        Assert.AreEqual("world", j2["f"]!.GetValue<string>());
        Assert.AreEqual(0, ctx.Normalize("abc again", out JsonObject j3));
        Assert.AreEqual("again", j3["f"]!.GetValue<string>());
    }

    [TestMethod]
    public void HotReload_ConcurrentWithNormalization_IsSafe()
    {
        var ctx = new LogNormContext();
        Assert.AreEqual(0, ctx.LoadSamplesFromString("rule=:msg 0 %f:rest%"));

        using var stop = new CancellationTokenSource();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => {
            while (!stop.IsCancellationRequested)
            {
                /* the initial rule must keep matching across every reload */
                if (ctx.Normalize("msg 0 payload", out JsonObject j) != 0
                    || j["f"]?.GetValue<string>() != "payload")
                {
                    failures.Enqueue($"unexpected result: {j.ToJsonString()}");
                    return;
                }
            }
        })).ToArray();

        for (var i = 1; i <= 50; i++)
        {
            Assert.AreEqual(0, ctx.LoadSamplesFromString($"rule=:msg {i} %f:rest%"));
        }

        stop.Cancel();
        Task.WaitAll(readers, TimeSpan.FromSeconds(30));

        Assert.AreEqual(0, failures.Count, string.Join("\n", failures));

        /* every loaded rule is visible afterwards */
        Assert.AreEqual(0, ctx.Normalize("msg 50 done", out JsonObject last));
        Assert.AreEqual("done", last["f"]!.GetValue<string>());
    }

    [TestMethod]
    public void LoadSamplesFromString_AcceptsCrlfLineEndings()
    {
        /* CRLF must terminate a logical line without the '\r' leaking into
         * the rule pattern as a literal; otherwise every non-final rule of a
         * CRLF rulebase (e.g. a multi-line C# raw string in a source file
         * checked out with core.autocrlf, or a rulebase file edited on
         * Windows) compiles to a PDAG path no real message can match */
        var ctx = new LogNormContext();
        Assert.AreEqual(0, ctx.LoadSamplesFromString(
            "rule=:duration %field:duration% bytes\r\nrule=:duration %field:duration%\r\n"));

        Assert.AreEqual(0, ctx.Normalize("duration 0:00:42 bytes", out JsonObject j));
        Assert.AreEqual("0:00:42", j["field"]!.GetValue<string>());
    }
}