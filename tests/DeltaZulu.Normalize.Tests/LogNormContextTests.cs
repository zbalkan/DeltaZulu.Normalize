using System.Text.Json.Nodes;
using DeltaZulu.Normalize;
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
    public void LoadSamplesFromString_AfterNormalize_IsRejected()
    {
        var errors = new List<string>();
        var ctx = new LogNormContext { ErrorCallback = errors.Add };
        Assert.AreEqual(0, ctx.LoadSamplesFromString("rule=:abc %f:rest%"));
        Assert.AreEqual(0, ctx.Normalize("abc hello", out JsonObject _));

        int r = ctx.LoadSamplesFromString("rule=:abd %f:rest%");

        Assert.AreEqual(ErrorCodes.BadConfig, r);
        Assert.IsTrue(errors.Any(e => e.Contains("cannot be loaded after", StringComparison.Ordinal)));
    }

}
