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

    [TestMethod]
    public void Mine_MaterializeAndStreamStrategiesProduceIdenticalOutput()
    {
        var records = new[] {
            new LogRecord(1, "Interface Ethernet 1 down at node node1", "test"),
            new LogRecord(2, "Interface Ethernet 1 down at node node2", "test"),
            new LogRecord(3, "Interface Ethernet 1 down at node node3", "test"),
        };

        var materialized = new LogClusterMiner(LogClusterOptions.Parse(["--materialize"])).Mine(() => records, estimatedInputBytes: long.MaxValue);
        var streamed = new LogClusterMiner(LogClusterOptions.Parse(["--stream"])).Mine(() => records, estimatedInputBytes: 0);

        Assert.AreEqual(materialized.RecordCount, streamed.RecordCount);
        Assert.HasCount(materialized.Candidates.Count, streamed.Candidates);
        Assert.AreSequenceEqual(
            materialized.Candidates.Select(c => c.LogClusterPattern).ToArray(), streamed.Candidates.Select(c => c.LogClusterPattern).ToArray());
        Assert.AreSequenceEqual(
            materialized.Candidates.Select(c => c.Support).ToArray(), streamed.Candidates.Select(c => c.Support).ToArray());
    }

    [TestMethod]
    public void Mine_MergesTrailingAnchorShiftedVariantsIntoOneCandidate()
    {
        var options = LogClusterOptions.Parse(["--min-support", "2"]);
        var records = new[] {
            new LogRecord(1, "Interface down node1", "test"),
            new LogRecord(2, "Interface down node2", "test"),
            new LogRecord(3, "Interface down node3 restart", "test"),
            new LogRecord(4, "Interface down node4 restart", "test"),
        };

        var result = new LogClusterMiner(options).Mine(records);
        var matches = result.Candidates.Where(c => c.LogClusterPattern.StartsWith("Interface down", StringComparison.Ordinal)).ToArray();

        Assert.HasCount(1, matches);
        var merged = matches[0];
        Assert.AreEqual(4, merged.Support);
        var trailingGap = merged.Gaps[^1];
        Assert.AreEqual(1, trailingGap.MinWords);
        Assert.AreEqual(2, trailingGap.MaxWords);
    }

    [TestMethod]
    public void Mine_ThrowsWhenInputBytesExceedMaxInputBytes()
    {
        var options = LogClusterOptions.Parse(["--max-input-bytes", "10"]);
        var records = new[] {
            new LogRecord(1, "this line is definitely over ten bytes", "test"),
        };

        Assert.ThrowsExactly<LogClusterInputTooLargeException>(() => new LogClusterMiner(options).Mine(records));
    }

    [TestMethod]
    public void Mine_ThrowsWhenRecordCountExceedsMaxRecords()
    {
        var options = LogClusterOptions.Parse(["--max-records", "2"]);
        var records = new[] {
            new LogRecord(1, "line one", "test"),
            new LogRecord(2, "line two", "test"),
            new LogRecord(3, "line three", "test"),
        };

        Assert.ThrowsExactly<LogClusterInputTooLargeException>(() => new LogClusterMiner(options).Mine(records));
    }

    [TestMethod]
    public void ShouldStream_ForcedOptionsOverrideTheHeuristic()
    {
        Assert.IsFalse(LogClusterMiner.ShouldStream(estimatedInputBytes: long.MaxValue, LogClusterOptions.Parse(["--materialize"])));
        Assert.IsTrue(LogClusterMiner.ShouldStream(estimatedInputBytes: 0, LogClusterOptions.Parse(["--stream"])));
    }

    [TestMethod]
    public void ShouldStream_LargeEstimateWithoutOverrideStreams()
    {
        var options = LogClusterOptions.Parse([]);
        Assert.IsTrue(LogClusterMiner.ShouldStream(estimatedInputBytes: long.MaxValue / 8, options));
        Assert.IsFalse(LogClusterMiner.ShouldStream(estimatedInputBytes: 1, options));
    }
}
