# Benchmark results

Run with:

```shell
dotnet run -c Release --project bench/DeltaZulu.Normalize.Benchmarks -- --filter '*'
```

Environment for all tables: Ubuntu 24.04, Intel Xeon 2.80GHz (4 physical cores),
.NET 10.0.9 (RyuJIT AVX-512), BenchmarkDotNet 0.14.0, IterationCount=10 WarmupCount=3.
Times and allocations are per single `Normalize` call.

Scenarios:

- **MatchFast** — 200-rule trie-heavy rulebase (shared literal prefixes), matching messages.
- **MatchBacktrack** — rules sharing a greedy `%word% %word%` prefix, distinct literal
  tails; messages match the last tail (maximal sibling exploration).
- **NoMatchTrie / NoMatchBacktrack** — messages that match nothing (full exploration).
- **Structured** — `json`, `cef`, `name-value-list`, `repeat` motifs.
- **ConcurrentNormalize** — 2000 messages via `Parallel.For` on one shared context,
  vs. **SingleThreadNormalize** for the same work on one thread.

## Baseline (commit: Phase 0, pre-optimization)

| Method                | Mean       | Gen0   | Allocated |
|---------------------- |-----------:|-------:|----------:|
| MatchFast             |   615.2 ns | 0.0579 |     606 B |
| MatchBacktrack        |   653.1 ns | 0.0674 |     704 B |
| NoMatchTrie           |   337.4 ns | 0.0477 |     496 B |
| NoMatchBacktrack      |   485.3 ns | 0.0642 |     664 B |
| Structured            | 1,977.0 ns | 0.2861 |   2,972 B |
| ConcurrentNormalize   |   664.4 ns | 0.0596 |     607 B |
| SingleThreadNormalize |   610.4 ns | 0.0586 |     606 B |

Observations:

- **Concurrency does not scale**: 4 cores are *slower per message* than 1 thread.
  Consistent with the shared per-node stats writes (`StatsCalled++`) causing cache-line
  ping-pong on every node visit (plus `Parallel.For` overhead at this small per-op cost).
- Allocations are significant everywhere — including **NoMatch** scenarios (496–664 B for
  messages that produce no fields), confirming eager value materialization on backtracked
  paths.

## Phase 2 — compiled PDAG snapshot (flat struct arrays, switch dispatch, literal first-char pre-filter, opt-in stats)

| Method                | Mean       | vs baseline | Allocated |
|---------------------- |-----------:|------------:|----------:|
| MatchFast             |   436.8 ns |       −29 % |     614 B |
| MatchBacktrack        |   476.1 ns |       −27 % |     712 B |
| NoMatchTrie           |   215.2 ns |       −36 % |     504 B |
| NoMatchBacktrack      |   317.5 ns |       −35 % |     672 B |
| Structured            | 1,832.3 ns |        −7 % |   2,980 B |
| ConcurrentNormalize   |   129.9 ns |      −80 %  |     615 B |
| SingleThreadNormalize |   429.1 ns |       −30 % |     614 B |

Observations:

- **Concurrency now scales**: 4 cores deliver ~3.3× the single-thread throughput
  (129.9 vs 429.1 ns/op); the read-only snapshot removed the shared-write ceiling.
- All single-thread scenarios improved 27–36 % from the contiguous edge array, jump-table
  dispatch and the literal first-char filter. Structured moved least (its cost is inside
  the big motif parsers, not the walker).
- Allocations unchanged, as expected — that is Phase 3's target.

## Phase 3 — two-phase match/extract (measure on descent, materialize on the success unwind)

Edges are classified at compile time: RawSpan (value == matched substring: one
Substring at unwind, no re-parse), Eager (repeat + structured motifs, whose match phase
is the expensive part), Deferred (derived values: cheap re-run at unwind).

| Method                | Mean       | vs Phase 2 | Allocated | vs Phase 2 |
|---------------------- |-----------:|-----------:|----------:|-----------:|
| MatchFast             |   459.5 ns |       +5 % |     614 B |          — |
| MatchBacktrack        |   482.5 ns |        ~0 % |     712 B |          — |
| NoMatchTrie           |   203.4 ns |       −6 % |     475 B |       −6 % |
| NoMatchBacktrack      |   264.1 ns |      −17 % |     491 B |      −27 % |
| Structured            | 1,814.7 ns |        ~0 % |   2,840 B |       −5 % |
| ConcurrentNormalize   |   137.0 ns |        ~0 % |     615 B |          — |
| SingleThreadNormalize |   480.2 ns |        ~0 % |     614 B |          — |

Observations:

- Backtracked/non-matching paths no longer allocate extraction values (NoMatchBacktrack
  −17 % time, −27 % bytes); winning-path scenarios stay flat because RawSpan
  materialization costs the same one Substring the eager code paid, just later.
- A naive uniform deferral (re-run every parser at unwind) was measured first and
  regressed MatchFast +17 % and Structured +24 % — the per-edge ExtractMode
  classification is what makes the trade-off pay.

## Phase 4 — vectorized parser scans (SearchValues, IndexOf/IndexOfAnyExcept, CommonPrefixLength)

The standard scenarios (5–15 char tokens) stay flat — SIMD needs run length to pay.
On long fields (300-char runs, `LongFieldBenchmarks`) the effect is decisive:

| Method      | Before (scalar) | After (vectorized) | Speed-up |
|------------ |----------------:|-------------------:|---------:|
| LongCharTo  |      1,040.2 ns |           371.6 ns |    2.8× |
| LongQuoted  |        744.9 ns |           367.7 ns |    2.0× |
| LongLiteral |        431.7 ns |           196.3 ns |    2.2× |
| LongWord    |        522.4 ns |           366.6 ns |    1.4× |

`char-to`/`char-sep` additionally drop from O(run · terminators) to a vectorized O(run),
and the `json` motif no longer allocates a UTF-8 copy of the candidate slice (Structured
allocations 2,840 → 2,798 B).

## Phase 5 — flat result type + zero-copy RawSpan slices

The walker commits into a flat `FieldCollector` instead of building a
`JsonObject` during the walk; RawSpan values are stored as
`ReadOnlyMemory<char>` slices of the input. `Normalize(out JsonObject)`
materializes at the end; the new `Normalize(out NormalizeResult)` /
`NormalizeToString` never build a `JsonObject` at all and serialize slices
directly. Measured on the sandbox host (numbers differ from the tables above
in absolute terms; compare within this table only — "before" is the same host
on the previous commit):

| Method                    | Before     | After      | Allocated before | after   |
|-------------------------- |-----------:|-----------:|-----------------:|--------:|
| Structured (JsonObject)   |   2.63 µs  |   2.57 µs  |          2.73 KB | 2.56 KB |
| StructuredFlatOnly (new)  |          — |   2.24 µs  |                — | 2.33 KB |
| StructuredToJsonText (new)|          — |   2.98 µs  |                — | 3.02 KB |
| LongCharTo (JsonObject)   |   546 ns   |   769 ns   |          1,096 B | 1,280 B |
| LongCharToFlat (new)      |          — |   286 ns   |                — |   280 B |
| LongWordFlat (new)        |          — |   308 ns   |                — |   280 B |

Observations:

- The flat path removes the per-field Substring entirely: on 300-char fields
  it is ~1.9× faster than the previous engine and allocates 280 B vs 1,096 B
  (the remaining bytes are the collector itself; the field values are slices).
- The Structured corpus is dominated by Eager motifs (json/cef/name-value/
  repeat) that build JsonNode trees regardless, so the flat-only gain there is
  ~15 % time / ~15 % bytes.
- The classic `out JsonObject` overload keeps its cost on realistic events
  (Structured slightly improves) but pays a fixed ~200 ns / ~200 B collector
  overhead on tiny 2-field messages (LongCharTo above), since it now runs
  flat-walk + materialize. Callers on that path at volume should move to
  `NormalizeResult`.
