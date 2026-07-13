using DeltaZulu.Normalize.PatternMining;

namespace DeltaZulu.Normalize.Tests;

[TestClass]
public sealed class PatternMiningTests
{
    [TestMethod]
    public async Task MineAsync_GeneratesTypedPatternFromStableStructure()
    {
        var source = new MemoryLogSource([
            "login failed for alice from 192.0.2.10 port 22",
            "login failed for bob from 192.0.2.11 port 2222",
            "login failed for charlie from 198.51.100.5 port 2200"
        ]);

        var miner = new LogClusterMiner();
        var result = await miner.MineAsync(source, new MiningOptions { Support = 3 });

        Assert.AreEqual(1, result.Suggestions.Count);
        var suggestion = result.Suggestions[0];
        StringAssert.Contains(suggestion.LiblognormSuggestion, "%field1:word%");
        StringAssert.Contains(suggestion.LiblognormSuggestion, "%field2:ipv4%");
        StringAssert.Contains(suggestion.LiblognormSuggestion, "%field3:posint%");
        Assert.AreEqual(3, suggestion.Candidate.RawSupport);
    }

    [TestMethod]
    public async Task MineAsync_CountsRepeatedWordOncePerRecord()
    {
        var source = new MemoryLogSource([
            "ERROR ERROR disk full",
            "ERROR disk slow"
        ]);

        var result = await new LogClusterMiner().MineAsync(source, new MiningOptions { Support = 2 });

        Assert.AreEqual(2, result.FrequentWords["ERROR"]);
        Assert.AreEqual(2, result.FrequentWords["disk"]);
    }

    [TestMethod]
    public async Task MineAsync_PreservesVariableLengthGap()
    {
        var source = new MemoryLogSource([
            "Interface DMZ-link down",
            "Interface HQ core link down"
        ]);

        var result = await new LogClusterMiner().MineAsync(source, new MiningOptions { Support = 2 });

        Assert.AreEqual(1, result.Suggestions.Count);
        StringAssert.Contains(result.Suggestions[0].LogClusterPattern, "*{1,3}");
    }
}
