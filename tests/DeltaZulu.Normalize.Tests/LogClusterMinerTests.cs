using LogCluster.Cli;

namespace DeltaZulu.Normalize.Tests;

[TestClass]
public class LogClusterMinerTests
{
    [TestMethod]
    public void InternalMultiwordGaps_AreRenderedAsUnresolvedSketchesNotRestParsers()
    {
        var options = LogClusterOptions.Parse(["--min-support", "2"]);
        var records = new[] {
            new LogRecord(1, "Interface Ethernet 1 down at node node1", "test"),
            new LogRecord(2, "Interface Ethernet 1 2 down at node node2", "test"),
        };

        var result = new LogClusterMiner(options).Mine(records);
        var candidate = result.Candidates.Single(c => c.LogClusterPattern.StartsWith("Interface", StringComparison.Ordinal));

        Assert.IsFalse(candidate.IsExecutableRule);
        Assert.Contains("/* unresolved gap:", candidate.LiblognormRule);
        Assert.DoesNotContain("%field1:rest% down at node", candidate.LiblognormRule);
        Assert.IsNotEmpty(candidate.RuleWarnings);
    }
}
