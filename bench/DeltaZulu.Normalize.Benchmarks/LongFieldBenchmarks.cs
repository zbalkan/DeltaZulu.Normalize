using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;

namespace DeltaZulu.Normalize.Benchmarks;

/// <summary>
/// Long-run scans: fields spanning hundreds of chars, where the vectorized
/// span primitives (IndexOf/IndexOfAny/CommonPrefixLength) diverge from
/// scalar per-char loops. Typical of application logs with long paths,
/// user-agent strings or embedded payloads.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 20)]
public class LongFieldBenchmarks
{
    private LogNormContext _ctx = null!;
    private string _charToMsg = null!;
    private string _wordMsg = null!;
    private string _literalMsg = null!;
    private string _quotedMsg = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ctx = new LogNormContext();
        _ctx.ErrorCallback = msg => throw new InvalidOperationException($"rulebase error: {msg}");
        var longLiteral = string.Concat(Enumerable.Repeat("prefix-segment/", 20)); /* 300 chars */
        var r = _ctx.LoadSamplesFromString($"""
            rule=:ct %f:char-to:;%; %r:rest%
            rule=:wd %f:word% %r:rest%
            rule=:qs %f:quoted-string% %r:rest%
            rule=:lit {longLiteral}%f:number%
            """);
        if (r != 0)
        {
            throw new InvalidOperationException("rulebase load failed");
        }

        var long300 = new string('x', 300);
        _charToMsg = $"ct {long300}; tail";
        _wordMsg = $"wd {long300} tail";
        _quotedMsg = $"qs \"{long300}\" tail";
        _literalMsg = $"lit {longLiteral}42";

        foreach ((var msg, var name) in new[]
                 { (_charToMsg, "char-to"), (_wordMsg, "word"), (_quotedMsg, "quoted"), (_literalMsg, "literal") })
        {
            if (_ctx.Normalize(msg, out JsonObject _) != 0)
            {
                throw new InvalidOperationException($"corpus error: {name} message does not match");
            }
        }
    }

    [Benchmark]
    public int LongCharTo() => _ctx.Normalize(_charToMsg, out JsonObject _);

    [Benchmark]
    public int LongWord() => _ctx.Normalize(_wordMsg, out JsonObject _);

    [Benchmark]
    public int LongQuoted() => _ctx.Normalize(_quotedMsg, out JsonObject _);

    [Benchmark]
    public int LongLiteral() => _ctx.Normalize(_literalMsg, out JsonObject _);

    /* flat-result variants: the 300-char field values stay slices of the
     * input message, so no per-field string copy occurs */

    [Benchmark]
    public int LongCharToFlat() => _ctx.Normalize(_charToMsg, out NormalizeResult _);

    [Benchmark]
    public int LongWordFlat() => _ctx.Normalize(_wordMsg, out NormalizeResult _);
}