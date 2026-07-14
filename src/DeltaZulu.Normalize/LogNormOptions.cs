namespace DeltaZulu.Normalize;

/// <summary>
/// Context options, mirroring the LN_CTXOPT_* flags of the C library.
/// </summary>
[Flags]
public enum LogNormOptions : uint
{
    None = 0,

    /// <summary>Always add the original message to the output (not just on failure).</summary>
    AddOriginalMessage = 0x04,

    /// <summary>Add a mock-up of the matching rule to the metadata.</summary>
    AddRule = 0x08,

    /// <summary>Add the matching rule's location (file, line number) to the metadata.</summary>
    AddRuleLocation = 0x10,

    /// <summary>
    /// Maintain per-node usage counters during normalization (see
    /// <see cref="LogNormContext.GetStats"/>). Off by default: the counters
    /// write to memory shared by all concurrent Normalize calls, which costs
    /// multithreaded throughput. Not a C library flag (the C engine always
    /// counts); the high bit avoids collision with future LN_CTXOPT_* values.
    /// </summary>
    CollectStats = 0x8000_0000,
}
