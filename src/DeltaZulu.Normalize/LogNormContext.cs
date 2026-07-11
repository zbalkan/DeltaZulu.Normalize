using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>
/// The library context: holds the compiled rulebase (the PDAG and its
/// user-defined-type components), annotations and configuration. Load one or
/// more rulebases, then call <see cref="Normalize"/> for each message.
///
/// A context is intended to be built once and then used read-only; concurrent
/// <see cref="Normalize"/> calls on a fully loaded context are safe apart
/// from the (racy but benign) node usage counters.
/// </summary>
public sealed class LogNormContext
{
    public LogNormContext()
    {
        Root = new Pdag(this);
    }

    /// <summary>Root of the main PDAG component.</summary>
    public Pdag Root { get; }

    internal List<TypePdag> TypePdags { get; } = new();

    internal AnnotationSet Annotations { get; } = new();

    /// <summary>Options controlling extra output fields.</summary>
    public LogNormOptions Options { get; set; }

    /// <summary>Receives debug trace messages when set.</summary>
    public Action<string>? DebugCallback { get; set; }

    /// <summary>Receives error messages (e.g. from rulebase loading) when set.</summary>
    public Action<string>? ErrorCallback { get; set; }

    /// <summary>Total number of PDAG nodes created in this context.</summary>
    public int NodeCount { get; internal set; }

    /* rulebase loading state (samp.c: ln_ctx fields) */
    internal string? RulePrefix;
    internal int IncludeLevel;
    internal string? ConfFile;
    internal int ConfLineNumber;

    private readonly object _pdagLock = new();
    private bool _pdagOptimized;

    /// <summary>
    /// Optimization (literal-path compaction) is deferred until the PDAG is
    /// actually used, rather than run after every top-level rulebase load.
    /// Compaction is not safe to run more than once against the same nodes
    /// (a later load's parser lookup keys off pre-compaction config text), so
    /// running it once, after all loads a caller makes before first use, is
    /// what keeps "load several rulebases, then normalize" working correctly.
    /// </summary>
    internal void EnsureOptimized()
    {
        lock (_pdagLock)
        {
            if (_pdagOptimized)
                return;

            PdagBuilder.Optimize(this);
            _pdagOptimized = true;
        }
    }

    private int LoadWhileUnoptimized(Func<int> load)
    {
        lock (_pdagLock)
        {
            if (_pdagOptimized)
            {
                Error("rulebases cannot be loaded after the context has been optimized by Normalize or GenerateDot");
                return ErrorCodes.BadConfig;
            }

            return load();
        }
    }

    /// <summary>
    /// Load a rulebase file (must be a v2 rulebase starting with "version=2").
    /// Rules are added to whatever has been loaded before.
    /// </summary>
    /// <returns>0 on success, non-zero otherwise</returns>
    public int LoadSamples(string path) => LoadWhileUnoptimized(() => RulebaseLoader.LoadFile(this, path));

    /// <summary>
    /// Load rulebase content from a string. The string must contain the rule
    /// lines only, without the "version=2" header.
    /// </summary>
    /// <returns>0 on success, non-zero otherwise</returns>
    public int LoadSamplesFromString(string rulebase) => LoadWhileUnoptimized(() => RulebaseLoader.LoadString(this, rulebase));

    /// <summary>
    /// Normalize a message against the loaded rulebase.
    ///
    /// On success (return 0) <paramref name="result"/> holds the extracted
    /// fields. On failure it holds "originalmsg" and "unparsed-data", exactly
    /// like the C library.
    /// </summary>
    public int Normalize(string message, out JsonObject result)
    {
        EnsureOptimized();
        return Normalizer.Normalize(this, message, out result);
    }

    /// <summary>Convenience wrapper returning the result as a JSON string.</summary>
    public int NormalizeToString(string message, out string json)
    {
        int r = Normalize(message, out JsonObject obj);
        json = JsonText.ToCompactString(obj);
        return r;
    }

    /// <summary>
    /// Generate a GraphViz DOT description of the main PDAG component,
    /// useful to inspect how the rulebase was compiled.
    /// </summary>
    public string GenerateDot()
    {
        EnsureOptimized();
        return PdagBuilder.GenerateDot(this);
    }

    internal void Debug(string msg) => DebugCallback?.Invoke(msg);

    internal void Error(string msg)
    {
        var cb = ErrorCallback;
        if (cb != null)
        {
            string where = ConfFile == null ? "" : $"{ConfFile}[{ConfLineNumber}]: ";
            cb(where + msg);
        }
    }
}
