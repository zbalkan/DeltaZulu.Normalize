namespace DeltaZulu.Normalize.Tests;

[TestClass]
public class LiblognormParserCatalogTests
{
    [TestMethod]
    public void Catalog_ExposesUsefulBuiltInMotifs()
    {
        ILiblognormParserCatalog catalog = LiblognormParserCatalog.Instance;

        Assert.IsTrue(catalog.TryGetParser("ipv4", out var ipv4));
        Assert.AreEqual("ipv4", ipv4.Name);
        Assert.AreEqual(4, ipv4.Priority);
        Assert.AreEqual(LiblognormParserSuggestionUse.InferFromSample, ipv4.SuggestionUse);
        Assert.IsTrue(ipv4.CanInferFromSample);
        Assert.IsTrue(ipv4.CanRenderWithoutConfiguration);
        Assert.IsFalse(ipv4.RequiresConfiguration);

        Assert.IsFalse(catalog.TryGetParser("literal", out _), "literal is an implementation detail, not a suggested motif capability");
        Assert.IsFalse(catalog.TryGetParser("repeat", out _), "repeat needs nested parser configuration");
    }

    [TestMethod]
    public void Catalog_IdentifiesMotifsThatNeedConfigurationOrArePoorSuggestions()
    {
        var catalog = LiblognormParserCatalog.Instance;

        Assert.IsTrue(catalog.TryGetParser("char-to", out var charTo));
        Assert.IsTrue(charTo.RequiresConfiguration);
        Assert.AreEqual(LiblognormParserSuggestionUse.None, charTo.SuggestionUse);
        Assert.IsFalse(charTo.CanInferFromSample);
        Assert.IsFalse(charTo.CanRenderWithoutConfiguration);

        Assert.IsTrue(catalog.TryGetParser("rest", out var rest));
        Assert.IsFalse(rest.RequiresConfiguration);
        Assert.AreEqual(LiblognormParserSuggestionUse.FallbackOnly, rest.SuggestionUse);
        Assert.IsFalse(rest.CanInferFromSample);
        Assert.IsTrue(rest.CanRenderWithoutConfiguration);
    }

    [TestMethod]
    public void Catalog_ExposesStableFallbackParserNames()
    {
        ILiblognormParserCatalog catalog = LiblognormParserCatalog.Instance;

        Assert.AreEqual("word", catalog.WordParserName);
        Assert.AreEqual("rest", catalog.RestParserName);
        Assert.IsTrue(catalog.TryGetParser(catalog.WordParserName, out _));
        Assert.IsTrue(catalog.TryGetParser(catalog.RestParserName, out _));
    }

    [TestMethod]
    public void Catalog_MetadataComesFromNormalizeParserTable()
    {
        ILiblognormParserCatalog catalog = LiblognormParserCatalog.Instance;

        foreach (var parser in ParserTable.Parsers)
        {
            Assert.AreEqual(parser.CatalogExposed, catalog.TryGetParser(parser.Name, out var descriptor));
            if (!parser.CatalogExposed)
            {
                continue;
            }

            Assert.AreEqual(parser.Priority, descriptor.Priority);
            Assert.AreEqual(parser.SuggestionUse, descriptor.SuggestionUse);
            Assert.AreEqual(parser.RequiresConfiguration, descriptor.RequiresConfiguration);
        }
    }

    [TestMethod]
    [DataRow("ipv4", "192.0.2.10", true)]
    [DataRow("ipv4", "192.0.2.10 trailing", false)]
    [DataRow("word", "alpha-123", true)]
    [DataRow("word", "two words", false)]
    [DataRow("char-to", "abc,", false)]
    [DataRow("literal", "abc", false)]
    public void IsFullMatch_RequiresOneExposedParserToConsumeEntireSample(string parserName, string sample, bool expected)
    {
        var catalog = LiblognormParserCatalog.Instance;

        Assert.AreEqual(expected, catalog.IsFullMatch(parserName, sample));
    }
}
