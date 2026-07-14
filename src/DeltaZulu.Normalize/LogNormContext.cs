using System.Text.Json.Nodes;

namespace DeltaZulu.Normalize;

/// <summary>
/// <para>
/// The library context: holds the rulebase (the builder PDAG and its
/// user-defined-type components), annotations and configuration. Load one or
/// more rulebases, then call <see cref="Normalize"/> for each message.
/// </para>
/// <para>
/// Normalization runs against an immutable compiled snapshot of the rulebase,
/// so concurrent <see cref="Normalize"/> calls are always safe. Rulebases may
/// be loaded at any time, including after normalization has started ("hot
/// reload"): a load invalidates the snapshot and the next Normalize compiles
/// and atomically publishes a new one, while in-flight normalizations finish
/// on the snapshot they started with.
/// </para>
/// </summary>
public sealed class LogNormContext
{
    internal string? ConfFile;

    internal int ConfLineNumber;

    internal int IncludeLevel;

    internal string? RulePrefix;

    private readonly Lock _pdagLock = new();

    private CompiledPdag? _snapshot;

    public LogNormContext()
    {
        Root = new Pdag(this);
    }

    /// <summary>Receives debug trace messages when set.</summary>
    public Action<string>? DebugCallback { get; set; }

    /// <summary>Receives error messages (e.g. from rulebase loading) when set.</summary>
    public Action<string>? ErrorCallback { get; set; }

    /// <summary>Total number of PDAG nodes created in this context.</summary>
    public int NodeCount { get; internal set; }

    /// <summary>Options controlling extra output fields.</summary>
    public LogNormOptions Options { get; set; }

    /// <summary>Root of the main PDAG component.</summary>
    public Pdag Root { get; }

    internal AnnotationSet Annotations { get; } = new();
    internal List<TypePdag> TypePdags { get; } = new();
    /* rulebase loading state (samp.c: ln_ctx fields) */

    /// <summary>
    /// Generate a GraphViz DOT description of the main PDAG component,
    /// useful to inspect how the rulebase was compiled.
    /// </summary>
    public string GenerateDot() => PdagCompiler.GenerateDot(EnsureCompiled());

    /// <summary>
    /// Total node-visit and backtrack counts accumulated by normalization.
    /// Requires <see cref="LogNormOptions.CollectStats"/> (set before first
    /// use); returns zeros otherwise. Counters restart when a rulebase load
    /// replaces the compiled snapshot.
    /// </summary>
    public (long NodesVisited, long Backtracks) GetStats()
    {
        var snap = Volatile.Read(ref _snapshot);
        if (snap?.StatsCalled == null || snap.StatsBacktracked == null)
        {
            return (0, 0);
        }

        long called = 0, backtracked = 0;
        foreach (var v in snap.StatsCalled)
        {
            called += v;
        }

        foreach (var v in snap.StatsBacktracked)
        {
            backtracked += v;
        }

        return (called, backtracked);
    }

    /// <summary>
    /// Load a rulebase file (must be a v2 rulebase starting with "version=2")
    /// or a directory of rulebase files (loaded recursively, equivalent to
    /// <see cref="LoadSamplesFromDirectory"/> with its defaults).
    /// Rules are added to whatever has been loaded before; loading is allowed
    /// at any time, including after normalization has started.
    /// </summary>
    /// <returns>0 on success, non-zero otherwise</returns>
    public int LoadSamples(string path) => LoadUnderLock(() => RulebaseLoader.Load(this, path));

    /// <summary>
    /// Load every rulebase file in <paramref name="directory"/> — and, unless
    /// <paramref name="recursive"/> is false, its subdirectories — into this
    /// context, building one combined PDAG. Files are loaded in ordinal path
    /// order (deterministic across runs and platforms); hidden files (name
    /// starting with '.') are skipped. Each file must be a complete v2
    /// rulebase with its own "version=2" header, and a prefix= set in one
    /// file does not leak into the next. Loading stops at the first file
    /// that fails; rules loaded up to that point remain in the context.
    /// </summary>
    /// <param name="directory">Directory to load rulebase files from.</param>
    /// <param name="searchPattern">File name pattern to match (default: all files).</param>
    /// <param name="recursive">Whether to descend into subdirectories (default: true).</param>
    /// <returns>0 on success, non-zero otherwise</returns>
    public int LoadSamplesFromDirectory(string directory, string searchPattern = "*", bool recursive = true)
        => LoadUnderLock(() => RulebaseLoader.LoadDirectory(this, directory, searchPattern, recursive));

    /// <summary>
    /// Load rulebase content from a string. The string must contain the rule
    /// lines only, without the "version=2" header. Loading is allowed at any
    /// time, including after normalization has started.
    /// </summary>
    /// <returns>0 on success, non-zero otherwise</returns>
    public int LoadSamplesFromString(string rulebase) => LoadUnderLock(() => RulebaseLoader.LoadString(this, rulebase));

    /// <summary>
    /// <para>Normalize a message against the loaded rulebase.</para>
    /// <para>
    /// On success (return 0) <paramref name="result"/> holds the extracted
    /// fields. On failure it holds "originalmsg" and "unparsed-data", exactly
    /// like the C library.
    /// </para>
    /// </summary>
    public int Normalize(string message, out JsonObject result)
        => Normalizer.Normalize(this, EnsureCompiled(), message, out result);

    /// <summary>
    /// Normalize a message into a flat <see cref="NormalizeResult"/> whose
    /// string values are zero-copy slices of <paramref name="message"/>;
    /// JsonObject/JSON text are produced from it on demand. Prefer this
    /// overload on hot paths that only read a few fields or serialize the
    /// result straight to JSON text.
    /// </summary>
    public int Normalize(string message, out NormalizeResult result)
    {
        var r = Normalizer.Normalize(this, EnsureCompiled(), message, out FieldCollector fields);
        result = new NormalizeResult(r, fields);
        return r;
    }

    /// <summary>
    /// Convenience wrapper returning the result as a JSON string. Serializes
    /// straight from the flat result, so no intermediate JsonObject or
    /// per-field strings are allocated.
    /// </summary>
    public int NormalizeToString(string message, out string json)
    {
        var r = Normalize(message, out NormalizeResult result);
        json = result.ToJsonString();
        return r;
    }

    internal void Debug(string msg) => DebugCallback?.Invoke(msg);

    /// <summary>
    /// Return the current compiled snapshot, compiling one from the builder
    /// graph if none is published. Readers use a lock-free Volatile.Read;
    /// only compilation itself serializes on the lock.
    /// </summary>
    internal CompiledPdag EnsureCompiled()
    {
        return Volatile.Read(ref _snapshot) ?? CompileLocked();

        CompiledPdag CompileLocked()
        {
            lock (_pdagLock)
            {
                var snap = Volatile.Read(ref _snapshot);
                if (snap == null)
                {
                    snap = PdagCompiler.Compile(this);
                    Volatile.Write(ref _snapshot, snap);
                }
                return snap;
            }
        }
    }

    internal void Error(string msg)
    {
        var cb = ErrorCallback;
        if (cb != null)
        {
            var where = ConfFile == null ? string.Empty : $"{ConfFile}[{ConfLineNumber}]: ";
            cb(where + msg);
        }
    }

    private int LoadUnderLock(Func<int> load)
    {
        lock (_pdagLock)
        {
            var r = load();
            /* invalidate even on failure: rules may have been added before
             * the error was hit, and they must become visible consistently */
            Volatile.Write(ref _snapshot, null);
            return r;
        }
    }
}
