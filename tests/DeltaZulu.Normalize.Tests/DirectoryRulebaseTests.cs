using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeltaZulu.Normalize.Tests;

/// <summary>
/// Tests for loading a whole directory (tree) of rulebase files into one
/// combined PDAG: <see cref="LogNormContext.LoadSamplesFromDirectory"/>, the
/// directory auto-detection in <see cref="LogNormContext.LoadSamples"/>, and
/// "include=" lines naming a directory.
/// </summary>
[TestClass]
public class DirectoryRulebaseTests
{
    private string _root = "";

    [TestInitialize]
    public void CreateTempDirectory()
    {
        _root = Path.Combine(Path.GetTempPath(), "dznorm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void DeleteTempDirectory()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            /* best effort */
        }
    }

    private string WriteRulebase(string relativePath, string body)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "version=2\n" + body);
        return path;
    }

    private static LogNormContext NewContext(List<string> errors)
        => new() { ErrorCallback = errors.Add };

    private static void AssertMatches(LogNormContext ctx, string message, string field, string expected)
    {
        var r = ctx.Normalize(message, out JsonObject json);
        Assert.AreEqual(0, r, $"message did not normalize: '{message}' -> {json.ToJsonString()}");
        TestHelpers.AssertJsonContains(json, field, expected);
    }

    [TestMethod]
    public void LoadsAllFilesAcrossSubdirectories()
    {
        WriteRulebase("10-ssh.rulebase", "rule=:accepted login for %user:word%\n");
        WriteRulebase("sub/20-net.rulebase", "rule=:link up on %iface:word%\n");
        WriteRulebase("sub/deeper/30-num.rulebase", "rule=:count is %n:number%\n");

        var errors = new List<string>();
        var ctx = NewContext(errors);
        Assert.AreEqual(0, ctx.LoadSamplesFromDirectory(_root), string.Join("; ", errors));

        AssertMatches(ctx, "accepted login for alice", "user", "alice");
        AssertMatches(ctx, "link up on eth0", "iface", "eth0");
        AssertMatches(ctx, "count is 42", "n", "42");
    }

    [TestMethod]
    public void LoadSamplesAcceptsADirectoryPath()
    {
        WriteRulebase("a.rulebase", "rule=:hello %who:word%\n");
        WriteRulebase("nested/b.rulebase", "rule=:bye %who:word%\n");

        var errors = new List<string>();
        var ctx = NewContext(errors);
        Assert.AreEqual(0, ctx.LoadSamples(_root), string.Join("; ", errors));

        AssertMatches(ctx, "hello alice", "who", "alice");
        AssertMatches(ctx, "bye bob", "who", "bob");
    }

    [TestMethod]
    public void PrefixDoesNotLeakBetweenFiles()
    {
        /* "10-..." sorts before "20-...", so a leaking prefix= would force
         * the second file's rule to require the prefix text too */
        WriteRulebase("10-prefixed.rulebase", "prefix=PFX \nrule=:one %a:word%\n");
        WriteRulebase("20-plain.rulebase", "rule=:two %b:word%\n");

        var errors = new List<string>();
        var ctx = NewContext(errors);
        Assert.AreEqual(0, ctx.LoadSamplesFromDirectory(_root), string.Join("; ", errors));

        AssertMatches(ctx, "PFX one x", "a", "x");
        AssertMatches(ctx, "two y", "b", "y");
        Assert.AreNotEqual(0, ctx.Normalize("PFX two y", out _), "prefix leaked into the second file");
    }

    [TestMethod]
    public void SearchPatternFiltersFiles()
    {
        WriteRulebase("rules.rulebase", "rule=:real %a:word%\n");
        /* not a rulebase at all: would fail the version=2 check if loaded */
        File.WriteAllText(Path.Combine(_root, "README.txt"), "not a rulebase\n");

        var errors = new List<string>();
        var ctx = NewContext(errors);
        Assert.AreEqual(0, ctx.LoadSamplesFromDirectory(_root, "*.rulebase"), string.Join("; ", errors));

        AssertMatches(ctx, "real thing", "a", "thing");
    }

    [TestMethod]
    public void HiddenFilesAreSkipped()
    {
        WriteRulebase("rules.rulebase", "rule=:real %a:word%\n");
        File.WriteAllText(Path.Combine(_root, ".gitkeep"), "");

        var errors = new List<string>();
        var ctx = NewContext(errors);
        Assert.AreEqual(0, ctx.LoadSamplesFromDirectory(_root), string.Join("; ", errors));

        AssertMatches(ctx, "real thing", "a", "thing");
    }

    [TestMethod]
    public void NonRecursiveLoadIgnoresSubdirectories()
    {
        WriteRulebase("top.rulebase", "rule=:top %a:word%\n");
        WriteRulebase("sub/inner.rulebase", "rule=:inner %b:word%\n");

        var errors = new List<string>();
        var ctx = NewContext(errors);
        Assert.AreEqual(0, ctx.LoadSamplesFromDirectory(_root, recursive: false), string.Join("; ", errors));

        AssertMatches(ctx, "top x", "a", "x");
        Assert.AreNotEqual(0, ctx.Normalize("inner y", out _), "subdirectory file was loaded despite recursive: false");
    }

    [TestMethod]
    public void FirstFailingFileStopsTheLoad()
    {
        WriteRulebase("10-good.rulebase", "rule=:good %a:word%\n");
        File.WriteAllText(Path.Combine(_root, "20-bad.rulebase"), "no version header\n");

        var errors = new List<string>();
        var ctx = NewContext(errors);
        Assert.AreNotEqual(0, ctx.LoadSamplesFromDirectory(_root));
        Assert.IsTrue(errors.Any(e => e.Contains("20-bad.rulebase")),
            $"error should name the failing file, got: {string.Join("; ", errors)}");

        /* rules loaded before the failure remain usable */
        AssertMatches(ctx, "good x", "a", "x");
    }

    [TestMethod]
    public void EmptyDirectoryIsAnError()
    {
        var errors = new List<string>();
        var ctx = NewContext(errors);
        Assert.AreNotEqual(0, ctx.LoadSamplesFromDirectory(_root));
        Assert.IsTrue(errors.Any(e => e.Contains("no rulebase files")), string.Join("; ", errors));
    }

    [TestMethod]
    public void MissingDirectoryIsAnError()
    {
        var errors = new List<string>();
        var ctx = NewContext(errors);
        Assert.AreNotEqual(0, ctx.LoadSamplesFromDirectory(Path.Combine(_root, "does-not-exist")));
        Assert.IsTrue(errors.Any(e => e.Contains("cannot open rulebase directory")), string.Join("; ", errors));
    }

    [TestMethod]
    public void IncludeLineMayNameADirectory()
    {
        WriteRulebase("extra/one.rulebase", "rule=:extra one %a:word%\n");
        WriteRulebase("extra/two.rulebase", "rule=:extra two %b:word%\n");
        var main = WriteRulebase("main.rulebase",
            $"rule=:main %m:word%\ninclude={Path.Combine(_root, "extra")}\n");

        var errors = new List<string>();
        var ctx = NewContext(errors);
        Assert.AreEqual(0, ctx.LoadSamples(main), string.Join("; ", errors));

        AssertMatches(ctx, "main x", "m", "x");
        AssertMatches(ctx, "extra one y", "a", "y");
        AssertMatches(ctx, "extra two z", "b", "z");
    }

    [TestMethod]
    public void ManyFilesBuildOneCombinedPdag()
    {
        for (var i = 0; i < 200; i++)
        {
            WriteRulebase($"gen/{i / 20}/rules-{i:D3}.rulebase", $"rule=:generated {i} value %v{i}:number%\n");
        }

        var errors = new List<string>();
        var ctx = NewContext(errors);
        Assert.AreEqual(0, ctx.LoadSamplesFromDirectory(_root), string.Join("; ", errors));

        AssertMatches(ctx, "generated 0 value 10", "v0", "10");
        AssertMatches(ctx, "generated 123 value 55", "v123", "55");
        AssertMatches(ctx, "generated 199 value 99", "v199", "99");
    }
}