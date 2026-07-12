namespace DeltaZulu.Normalize;

/// <summary>
/// Error codes, kept value-compatible with the C library (DeltaZulu.Normalize.h).
/// A return value of 0 always means success.
/// </summary>
public static class ErrorCodes
{
    public const int Ok = 0;
    public const int NoMem = -1;
    public const int InvalidFieldDescriptor = -1;
    public const int BadConfig = -250;
    public const int BadParserState = -500;

    /// <summary>The parser did not match at this position (normal control flow, not an error).</summary>
    public const int WrongParser = -1000;
}