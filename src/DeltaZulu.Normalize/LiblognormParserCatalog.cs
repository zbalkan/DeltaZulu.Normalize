namespace DeltaZulu.Normalize;

/// <summary>How a built-in motif should be used by rule-suggestion tooling.</summary>
public enum LiblognormParserSuggestionUse
{
    None,
    InferFromSample,
    FallbackOnly,
}

/// <summary>Public metadata for one built-in liblognorm motif parser.</summary>
public sealed record LiblognormParserDescriptor(
    string Name,
    int Priority,
    LiblognormParserSuggestionUse SuggestionUse,
    bool RequiresConfiguration)
{
    public bool CanInferFromSample => SuggestionUse == LiblognormParserSuggestionUse.InferFromSample;

    public bool CanRenderWithoutConfiguration => !RequiresConfiguration;
}

/// <summary>
/// Catalog of the built-in liblognorm motif parsers that are useful to external
/// tooling, such as rule editors and parser-suggestion experiences.
/// </summary>
public interface ILiblognormParserCatalog
{
    IReadOnlyList<LiblognormParserDescriptor> Parsers { get; }

    string WordParserName { get; }

    string RestParserName { get; }

    bool TryGetParser(string name, out LiblognormParserDescriptor parser);

    bool IsFullMatch(string parserName, ReadOnlySpan<char> sample);
}

/// <summary>Default catalog for DeltaZulu.Normalize's built-in liblognorm motif parsers.</summary>
public sealed class LiblognormParserCatalog : ILiblognormParserCatalog
{
    public static ILiblognormParserCatalog Instance { get; } = new LiblognormParserCatalog();

    public IReadOnlyList<LiblognormParserDescriptor> Parsers => ParserTable.CatalogParsers;

    public string WordParserName => ParserTable.WordParserName;

    public string RestParserName => ParserTable.RestParserName;

    public bool TryGetParser(string name, out LiblognormParserDescriptor parser)
        => ParserTable.TryGetCatalogParser(name, out parser);

    public bool IsFullMatch(string parserName, ReadOnlySpan<char> sample) =>
        ParserTable.IsCatalogFullMatch(parserName, sample);
}
