namespace DeltaZulu.Normalize;

/// <summary>
/// The "normalization parameter block" (npb): per-message state shared by the
/// recursive normalizer and all motif parsers. The PDAG itself stays
/// read-only during normalization; everything mutable lives here.
/// </summary>
internal sealed class Npb
{
    public required LogNormContext Ctx { get; init; }

    /// <summary>The compiled rulebase snapshot this message is parsed against.
    /// Carried here so nested walks (custom types, "repeat") reach the same
    /// snapshot even while the context is being reloaded concurrently.</summary>
    public required CompiledPdag Snap { get; init; }

    /// <summary>The to-be-normalized message.</summary>
    public required string Str { get; init; }

    /// <summary>Length of <see cref="Str"/> (kept explicit to mirror the C code).</summary>
    public int StrLen => Str.Length;

    /// <summary>Up to which position the current (successful) parse path consumed input.</summary>
    public int ParsedTo;

    /// <summary>The furthest position any attempted path ever reached (for "unparsed-data").</summary>
    public int LongestParsedTo;

    /// <summary>
    /// Mock-up of the matching rule (only populated with <see cref="LogNormOptions.AddRule"/>).
    /// Segments are appended while unwinding the recursion, i.e. deepest-first;
    /// they are reversed when the rule string is emitted.
    /// </summary>
    public List<string>? RuleSegments;

    /// <summary>Character at <paramref name="i"/>, or NUL past the end.
    /// Mirrors the C library reading its NUL-terminated buffer at str[strLen].</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public char At(int i) => i < Str.Length ? Str[i] : '\0';
}