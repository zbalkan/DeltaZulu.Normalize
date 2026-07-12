# DeltaZulu.Normalize (C# port)

A C# port of `liblognorm` v2's PDAG-based log normalization engine, targeting
.NET 10. It parses unstructured log text into structured JSON using the same
rulebase-compiled-to-a-parse-DAG model as the C library — see
[`../doc/pdag_implementation_model.rst`](../doc/pdag_implementation_model.rst)
for the underlying algorithm.

## Scope

This is a **direct port of the v2 engine only**:

- The v2 rulebase syntax (`version=2` header; `rule=`, `type=`, `prefix=`,
  `extendprefix=`, `annotate=`, `include=` lines; legacy, condensed and full
  JSON field syntax) and its 30 built-in motif parsers.
- The PDAG construction, optimization (priority sort + literal path
  compaction) and recursive-with-backtracking normalizer.
- Rule tags and `annotate=` static-field annotations.

**Not ported**, intentionally out of scope:

- The legacy v1 rulebase *engine* (a separate, older, non-PDAG normalizer;
  distinct from v2's "legacy field syntax," which *is* supported).
- TurboVM (an internal bytecode-compiled fast path for the C library — a
  performance optimization layered on top of the same v2 semantics, not a
  behavioral difference).
- Advanced/debug statistics collection, DOT statistics graphs.

## Layout

- `src/DeltaZulu.Normalize/` — the library.
  - `Pdag.cs`, `PdagBuilder.cs` — the parse DAG node/edge model and
    construction/optimization (rule loading → DAG, priority sort, literal
    compaction).
  - `Normalizer.cs` — the recursive PDAG walker with backtracking.
  - `RulebaseLoader.cs` — the `version=2` rulebase text parser.
  - `Parsers/` — the built-in motif parsers (literal, repeat, dates,
    numbers, network addresses, quoted/delimited strings, JSON, CEF,
    name-value lists, etc).
  - `Annotations.cs` — `annotate=` tag-triggered static fields.
- `tools/LogNormalizer.Cli/` — a small CLI (`lognormalizer`), analogous to
  the C project's `src/lognormalizer.c`: `-r <rulebase>` to load, then reads
  messages from stdin (or `-m <message>`) and prints one JSON object per line.
- `tests/DeltaZulu.Normalize.Tests/` — MSTest tests. Fixtures are behavioral cases
  ported from the C project's `tests/*.sh` shell fixtures (verified against
  the real expected output, not blindly copied — the original shell harness's
  `assert_output_json_eq` only checks a *subset* of fields are present, so a
  few fixtures needed completing with fields the original assertions omitted,
  e.g. `event.tags`).
- `scratch/parity_check.py` — a throwaway (not part of the build) parity
  harness used during development: it replays every `add_rule`/`execute`
  pair extracted from the C project's `tests/*.sh` files against both the
  real C `lognormalizer` binary and this port's CLI, and diffs the JSON
  output. At the time of writing this passes 381/381 replayed cases with
  zero mismatches and zero crashes across the full v2-relevant test corpus.
  Requires the C project to be built first (`autoreconf -fi && ./configure
  --enable-testbench && make`) — see the main repo's build instructions.

## Building and testing

```shell
cd csharp
dotnet build DeltaZulu.Normalize.slnx
dotnet test tests/DeltaZulu.Normalize.Tests
```

## Using the library

```csharp
using DeltaZulu.Normalize;

var ctx = new LogNormContext();
ctx.LoadSamplesFromString("""
    rule=:%date:date-rfc3164% %host:word% %tag:char-to:\x3a%: no longer listening on %ip:ipv4%#%port:number%
    """);

int r = ctx.Normalize("Oct 29 09:47:08 myhost sshd: no longer listening on 192.168.1.1#22", out var json);
// json: {"date":"Oct 29 09:47:08","host":"myhost","tag":"sshd","ip":"192.168.1.1","port":"22"}
```

## Using the CLI

```shell
dotnet run --project tools/LogNormalizer.Cli -- -r rules.rulebase -m 'some log line'
```

`event.tags` (an internal field populated whenever the matching rule carries
tags) is stripped from the CLI's output by default, matching the reference
`lognormalizer` tool; pass `-T` to include it.

## Known intentional behavior notes

- Like the C engine, the `rest` motif can match zero bytes — when two rules
  share a prefix and one extends the other with a trailing `%f:rest%`, the
  DAG node after the shared prefix is both terminal (for the shorter rule)
  and has an outgoing `rest` edge (for the longer one); edges are always
  tried before falling back to the node's own terminal status, so the
  `rest` branch wins and the longer rule's fields (`rest` = `""`) appear in
  the output. This matches the C engine's actual behavior, even though the
  upstream test suite's loose (subset-matching) assertions don't always
  spell it out.
- `json`-typed rulebase parameters are read leniently, matching json-c: a
  quoted numeric string (e.g. `"priority":"1000"`) is accepted anywhere an
  integer is expected.
- Text is processed as UTF-16 chars, not bytes. The C library sees UTF-8
  bytes, so its per-byte character classes behave differently on non-ASCII
  input. This port's `string` motif treats chars above U+00FF as permitted
  when no `matching.permitted` restriction is configured (like C's all-true
  byte table) and as not-permitted when one is (they cannot be named in the
  byte-range table). ASCII behavior is identical to the C engine.
