# Comparison against upstream liblognorm (C)

This document records the result of a line-by-line audit of this port against
the upstream C project ([rsyslog/liblognorm](https://github.com/rsyslog/liblognorm),
`main` branch), scoped to the v2 engine (per this port's stated scope in the
top-level README). It covers four areas: the built-in motif parsers, the
rulebase loader/syntax, the PDAG engine (construction, optimization,
normalization/backtracking), and the public API/CLI/test suite.

For each area it lists:
- **Confirmed** — deviations already called out in this port's code comments
  or README, checked against the actual C source and found accurate.
- **Newly documented** — real, deliberate-looking behavioral differences that
  existing tests already pin but that weren't previously written down
  anywhere as a C-vs-C# difference. This document is what makes them
  official/explicit.
- **Audit findings** — things the audit turned up that look like plain
  oversights or gaps rather than considered decisions (crashes, missing
  coverage). Flagged for awareness, not (yet) fixed.
- **New capability** — features with no upstream equivalent at all, so
  there's nothing to "deviate" from.

Line numbers below reflect the state at the time of writing and the
`rsyslog/liblognorm` `main` HEAD used for the comparison; expect drift over
time.

## 1. Motif parsers

### Confirmed (already in code comments / README)

- `rest` can match zero bytes at a shared-prefix terminal/continuation node —
  `README.md` "Known intentional behavior notes", 1st bullet.
- `json`-typed rulebase parameters accept a quoted numeric string anywhere an
  int is expected (json-c leniency) — `JsonText.GetLenientInt64`
  (`src/DeltaZulu.Normalize/JsonText.cs`), README 2nd bullet.
- `string` motif treats chars above U+00FF as permitted only when
  unrestricted (UTF-16 vs. C's UTF-8-byte semantics) — README 3rd bullet.
- `string-to` with a single-character search string can never match — a
  faithful port of a real quirk in `parser.c`'s `ln_v2_parseStringTo` (its
  inner loop needs a char *after* `toFind[0]` to ever set the found flag) —
  `CoreParsers.cs` (`ParseStringTo`).
- `cisco-interface-spec`'s delimiter check after an embedded `ip2` is
  effectively dead code in C (compares the wrong loop variable, so any
  character is accepted where `/` is expected) — faithfully reproduced,
  `NetworkParsers.cs` (`ParseCiscoInterfaceSpec`).
- `number`-format floats: C serializes the JSON value using the raw matched
  text unconditionally, which can produce syntactically invalid JSON (e.g.
  `"00.5"`); this port checks validity and falls back to the computed numeric
  value when the raw text isn't valid JSON — `NumberParsers.cs` (`ParseFloat`),
  pinned by `CorrectnessFixTests.cs` ("leading zeros: invalid JSON, computed
  value"). This was in a code comment but not called out as a deviation in
  the README.

### Newly documented (deliberate, tested, previously unwritten)

- **`repeat` infinite-loop guard.** C's `ln_v2_parseRepeat` loops
  `do { parser; while-cond } while(r==0)` with no check that either match
  advanced the read offset — a rulebase where both the repeated parser and
  the `while` condition can match zero-width (e.g. a `char-sep` sitting
  exactly on its own separator) hangs the C engine forever.
  `RepeatParser.cs` breaks the loop when a round makes no forward progress.
  Pinned by `PdagBehaviorTests.Repeat_StopsWhenParserAndWhileBothMatchZeroWidth`
  (which carries a 5s timeout as a hang guard).
- **`cef` rejects a dangling trailing backslash in the last extension
  value**, where C accepts the message but copies a garbage/out-of-bounds
  byte into the extracted value (the escape-validation loop in
  `cefParseExtensionValue` only checks the *next* iteration, so a backslash
  that is the literal last character of the message is never validated).
  `StructuredParsers.cs` explicitly checks for a dangling `inEscape` state and
  fails the match instead. Pinned by
  `StructuredParserTests.Cef_RejectsDanglingBackslashAtEndOfLastExtensionValue`.
- **`string-to` treats an empty (present-but-blank) `extradata` as a
  rulebase-load error** (`ctx.Error(...)` + `BadConfig`), where C silently
  constructs a parser that can never match. Note this check is *not* applied
  to the sibling `char-to`/`char-sep` motifs, which still behave like C (load
  successfully, never match). Pinned by
  `BasicParserTests.StringTo_RejectsEmptyExtradataAsBadConfig`.
- **CRLF line endings** in rulebase files are treated as a line terminator
  throughout the loader (not just on the `version=2` header line, which is
  the only place C strips `\r`). In real C, every other line in a
  CRLF-encoded rulebase file gets a trailing `\r` silently baked into the
  parsed pattern, which then can never match real input — a genuine C
  correctness bug on Windows-edited/`core.autocrlf`-normalized files. This
  port fixes it (`RulebaseLoader.ReadLogicalLine` folds `\r\n` → `\n`
  everywhere, and the runaway-rule blank-line/comment check treats a bare
  `\r` as a terminator too), pinned by
  `LogNormContextTests.LoadSamplesFromString_AcceptsCrlfLineEndings`. Present
  in the code/commit history (`b064fe8`) but not previously in the README.
- **Normalizer `endNode` tag/metadata attribution fix.** This is the most
  consequential finding of the audit — see §3 below; it's cross-cutting
  (parser-independent) but manifests exactly in the shared-prefix + `rest`
  scenario the `rest`-zero-byte-match note already covers for *fields*.

### Audit findings (oversights, not decisions — flagged, not yet fixed)

- Missing test coverage for several motif options that exist in the code but
  aren't exercised by direct C# unit tests: `v2-iptables` (still covered only
  through the parity harness), some `name-value-list` custom
  `separator`/`assignator`/`ignore_whitespaces` combinations,
  `checkpoint-lea` quoting/`terminator`, `number`/`hexnumber` `maxval`, and
  `op-quoted-string`'s `escape` option. Recent tests now pin the previously
  listed `string` `option.dashIsEmpty` and `matching.mode:"lazy"` cases,
  including the upstream-compatible distinction between unquoted `-` as empty
  and quoted `"-"` as a literal dash.

## 2. Rulebase loader / v2 syntax

### Confirmed (already documented, verified accurate)

- Legacy `%name:type%`/`%name:type:param%` syntax, tag parsing (including the
  empty-tag-before-comma hard error matching upstream's
  `rule_empty_tag_segfault.sh` fix), literal `%%`-doubling, and `#`-comment
  recognition (only at buffer-start on both sides, so indented `  #` is not a
  comment either engine) are faithful 1:1 ports of `samp.c`.
- json-c leniency for quoted integers and trailing-whitespace consumption
  after a JSON value (README, confirmed against `json_tokener_parse_ex`
  behavior implied by upstream's own `parser_whitespace_jsoncnf.sh` fixture).
- `annotate=` field ordering: tags are processed last-to-first and, within a
  tag, ops are applied in reverse file order — matches `annot.c`'s
  prepend-then-walk-backward linked-list semantics exactly for the common
  one-op-per-line case that appears in every real fixture on both sides
  (`Annotations.cs` code comment; README doesn't repeat this, the comment is
  the source of truth).

### Newly documented (deliberate, previously unwritten)

- **CRLF handling** — see §1 above; applies to the loader broadly, not just
  a specific motif.
- **`LIBLOGNORM_RULEBASES` environment variable is renamed to
  `DeltaZulu.Normalize_RULEBASES`** for the `include=`/`-r` search-path
  fallback (`RulebaseLoader.cs`). Functionally faithful (same two-step
  "literal path, else env-var-relative unless absolute" resolution as
  `tryOpenRBFile` in `samp.c`), but the name change was previously silent —
  worth knowing if you're porting rulebases that rely on the env var.
- **`include=` filename trimming only strips trailing whitespace.** C's
  `processInclude` nulls out *any* interior whitespace character it scans
  backward past (an unconditional backward loop, not a "stop at first
  non-space" trim), so a real C engine would mis-truncate an include
  filename containing an internal space. This port trims only the trailing
  end. Practically unlikely to matter (rulebase filenames rarely contain
  spaces), and untested on either side, but it's a genuine, deliberate-in-effect
  divergence in the more defensible direction.
- **`extendprefix=` before any `prefix=` line is treated as equivalent to
  `prefix=`, gracefully**, where upstream's `extendPrefix` (`samp.c`) calls
  `es_addBuf` with no NULL-guard on an as-yet-unallocated prefix — unlike
  `getPrefix`'s explicit `*str == NULL` branch — so a "bare" `extendprefix=`
  plausibly misbehaves in real C. Documented in code at the
  `RulebaseLoader.ProcessLine` `"extendprefix"` case.

### Audit findings

- No 64KB rulebase line-length cap (C's `ln_sampRead` uses a fixed
  `char buf[64*1024]` and errors on overflow); the port's `StringBuilder`
  accepts arbitrarily long lines. Unlikely to matter in practice.
- Test coverage gaps: no C# test for `runaway_rule_comment.sh`'s scenario
  (blank/comment lines interleaved with a runaway rule, under `version=2`),
  none for the `*_RULEBASES` env var fallback in either its single-file or
  directory-search form, none for a JSON field definition spanning multiple
  physical lines (`parser_LF_jsoncnf.sh`/`parser_whitespace_jsoncnf.sh`), and
  `RealWorldRulebaseTests.cs` uses hand-authored rulebases "inspired by" real
  projects rather than exercising liblognorm's own bundled
  `rulebases/{cisco,messages,sample}.rulebase` files end-to-end.

### New capability (not a deviation — no C equivalent to compare against)

- **Parity harness include materialization.** The development-only
  `scratch/parity_check.py` now materializes include chains before replaying
  extracted upstream fixtures, so include-heavy parity cases compare the same
  effective rulebase on both engines instead of depending on transient working
  directories.
- **Directory-tree rulebase loading** (`LoadSamplesFromDirectory`, a
  directory named by `include=`, deterministic ordinal ordering, hidden-file
  skipping, no prefix leakage between files). Confirmed by reading
  `liblognorm.c`/`samp.c`: the C library has no `opendir`/`readdir`/
  `S_ISDIR` anywhere — `ln_sampLoad` only ever `fopen()`s a single named
  file. The README describes this feature's behavior but doesn't say
  outright that C has nothing like it at all; worth being explicit that this
  is the single largest "deviation," in the sense of being pure addition
  rather than a changed behavior.

## 3. PDAG engine (construction, optimization, normalization)

### Confirmed (already documented, verified accurate — including by building
and running the real C library)

- Edge-priority ordering and the "try edges, fall back to this node's own
  terminal status" algorithm match `doc/pdag_implementation_model.rst` and
  `pdag.c`'s `ln_normalizeRec` structurally.
- Literal path compaction uses the identical gating conditions as
  `optLitPathCompact` in `pdag.c` (single ref-count, single parser, no name,
  non-terminal). The port doesn't mutate the builder graph in place the way
  C mutates `ctx->pdag` — this is a deliberate, already-commented enabler
  for hot reload (`PdagCompiler.cs`), not an independent behavior change;
  the compiled result is equivalent either way.
- Per-parser priority values and the `(assignedPrio << 8) | parserPrio`
  combination formula match `pdag.c` exactly across all 30 built-in motifs.
- Stats counters (`called`/`backtracked`) are plain non-atomic increments on
  both sides — the "racy but benign, exactly like the C library's counters"
  comment (`CompiledPdag.cs`) is accurate; C's `struct` fields are likewise
  plain `unsigned int`s with no atomics anywhere in `pdag.c`/`pdag.h`.
- Nothing from TurboVM (bytecode compilation, arena allocation, SIMD
  scanning) leaked into the plain recursive walker; the README's claim that
  it's "not a behavioral difference" holds.

### Newly documented — the most significant finding of this audit

- **The normalizer's `endNode` can end up pointing at the wrong node in C,
  misattributing `event.tags` and rule-location metadata to a shallower rule
  than the one whose fields were actually extracted.** C's terminal-fallback
  check (`pdag.c`, end of `ln_normalizeRec`) is:
  ```c
  if(dag->flags.isTerminal && (offs == npb->strLen || bPartialMatch)) {
      *endNode = dag;   /* unconditional overwrite, even if r==0 already */
      r = 0;
  }
  ```
  with no guard on `r`. This port's `Normalizer.cs` adds one:
  ```csharp
  if (r != 0 && node.IsTerminal && (offs == npb.StrLen || bPartialMatch))
  {
      endNode = nodeIdx;
      ...
  }
  ```
  Effect: when two rules share a prefix and the shared node is both terminal
  (the shorter rule) and has an outgoing `rest` edge (the longer rule) — the
  exact scenario the README's `rest`-zero-byte-match note already documents
  for *fields* — the C engine's field extraction correctly follows the
  deeper `rest` edge (already committed via `fixJSON` before this check
  runs), but on unwinding the recursion, this unconditional line stomps
  `*endNode` back to the shallower node. Since `event.tags` and
  `addRuleMetadata` are read from `endNode` after normalization completes
  (`pdag.c`, `ln_normalize`), **C emits fields from the longer rule but tags
  from the shorter rule** — an internally inconsistent result. This port's
  guard keeps `endNode` consistent with the actual matching path.

  Empirically verified by building the real C library from source and
  running an equivalent rulebase/input through both:
  ```
  version=2
  rule=SHORT:%iface:char-to:\x3a%\x3a%ip:ipv4%
  rule=LONG:%iface:char-to:\x3a%\x3a%ip:ipv4%%tail:rest%
  ```
  against `eth0:10.0.0.1` — reference `lognormalizer` returns
  `event.tags:["SHORT"]` alongside `tail` (a LONG-only field); this port's
  CLI returns `event.tags:["LONG"]`, consistent with the fields it emitted.

  Neither test suite exercises tagged rules in a shared-prefix + `rest`
  scenario, so this was untested (and unnoticed) on both sides before this
  audit. Recorded here as a confirmed, real, and — barring a hot-reload-style
  reason to reproduce it faithfully — probably-desirable divergence.

### Audit findings

- **Priority-sort tie-breaking relies on stability that C's `qsort` doesn't
  formally guarantee.** This port sorts parsers with a stable `OrderBy`;
  C uses `qsort`, which the C standard does not require to be stable. On
  glibc (the reference platform) `qsort` happens to be a stable merge sort
  in practice, so observed behavior matches, but a different libc — or a
  future glibc — could produce a different tie-break order in C without the
  port following suit. Not a bug today, a portability nuance worth knowing.
- No test in either suite covers the `"priority"` JSON override that
  inverts default precedence (upstream `tests/parser_prios.sh`), despite
  both engines fully implementing it.
- Only the two-branch `alternative` case is covered by a C# test; the
  nested-array form and the historically-segfaulting literal/number
  combination (upstream `alternative_nested.sh`, `alternative_segfault.sh`,
  `alternative_three.sh`) have no direct equivalent.

### New capability (not a deviation)

- **Hot reload.** C's `ln_pdagOptimize` runs synchronously and destructively
  on the live, shared `ctx->pdag` at the end of every top-level load call —
  there is no snapshotting, copy-on-write, or locking anywhere in the C
  library, so concurrently loading and normalizing on the same context is a
  data race by construction in C. This port's append-only builder graph +
  immutable compiled snapshot (published via `Volatile.Write`/`Read`, with
  locking only around compilation) makes concurrent load+normalize safe.
  Already framed correctly as an added capability in the README/`Layout`
  section, not a behavior change to compare against.
- **Flat result type (`NormalizeResult`).** The engine commits extracted
  fields into a flat (name, value) collector during the walk; the
  `JsonObject` the classic overload returns is materialized from it at the
  end, and the `NormalizeResult` overload exposes the flat form directly —
  string values as zero-copy slices of the input message, with JSON text
  serialized straight from the slices. The C library has no equivalent
  (its `json_object` result *is* the working representation). The fixJSON
  special-name semantics ("." splice including the mid-splice duplicate
  abort, single-".." unwrap, unnamed-field drop) are ported onto the
  collector unchanged.

## 4. API, error codes, `annotate=`, CLI, and test-suite breadth

### Confirmed (already documented, verified accurate)

- `ErrorCodes.cs`'s "value-compatible with the C library" claim holds for
  every code it defines (`NoMem=-1`, `BadConfig=-250`, `BadParserState=-500`,
  `WrongParser=-1000` all match `liblognorm.h`).
- `LogNormOptions.CollectStats` is correctly noted as "not a C library flag"
  — C's `dag->stats.called`/`backtracked` counters are incremented
  unconditionally, gated by no ctxopt at all.
- `annotate=`'s field-ordering comment in `Annotations.cs` (tags processed
  last-to-first; ops within a tag in reverse file order) is accurate for
  every real-world pattern in either test suite (one op per `annotate=`
  line).
- The CLI's `event.tags`-stripped-by-default / `-T`-restores-it behavior
  matches `lognormalizer.c` exactly.
- Upstream's own shell-test assertion helper, `assert_output_json_eq`, really
  does only check that fields present in the *expected* JSON literal also
  appear (correctly) in the actual output — it never flags extra actual
  fields — confirmed by reading `tests/json_eq.c`'s `obj_eq()`. The README's
  and `PdagBehaviorTests.cs`'s claim that this is why some ported fixtures
  needed "completing" with omitted fields (e.g. `event.tags`) is accurate.
  (Note this port's own `TestHelpers.AssertJsonEquals` defaults to *stricter*
  full structural equality, with a separate `AssertJsonContains` helper for
  intentional subset checks — the opposite default from upstream.)

### Newly documented (deliberate, previously unwritten)

- `ErrorCodes.cs` omits `LN_RB_LINE_TOO_LONG` (-1001) and
  `LN_OVER_SIZE_LIMIT` (-1002). Low practical severity: neither is actually
  returned anywhere in upstream's own C source (dead/reserved codes), and no
  upstream test references them, but worth noting the "value-compatible"
  claim is an enumeration of the codes that matter, not literally every code
  the header defines.
- **The CLI (`tools/LogNormalizer.Cli`) is a narrower slice of
  `lognormalizer.c`'s flag surface by design**, consistent with this port's
  stated scope (no encoders, no stats/debug output, no v1 engine): no
  `-e/-E` (XML/CSV/CEE-syslog output encoders — none of `enc_csv.c`/
  `enc_xml.c`/`enc_syslog.c` are ported at all), no `-d` (DOT graph export,
  even though the underlying `LogNormContext.GenerateDot()` exists in the
  library and just isn't wired up to a flag), no `-oaddRuleLocation` CLI
  flag (same story — the library option exists, the CLI doesn't expose it),
  and no `-p/-P/-t/-L/-H/-U/-v/-V/-s/-S/-x/-R` flags. Conversely, `-m` (normalize
  a single message inline without stdin) and `-r` accepting a directory are
  pure C# additions with no upstream flag to match.
- A narrow, currently-unreachable-in-practice `annotate=` ordering edge case:
  if a single `annotate=` line has *multiple* `+field=value` ops **and** the
  same tag is also annotated by a separate, later `annotate=` line, C's
  nested-list combine algorithm (`ln_combineAnnot`) produces a subtly
  different relative order between ops from different lines than this
  port's flat-list-plus-single-reversal approach would, *if* those ops from
  different lines set the same field name. No fixture on either side
  exercises multi-op-per-line + tag-reused-across-lines, so this has never
  actually been observed; documented in code on `AnnotationSet.Annotation.Ops`
  rather than chased with a test for an unobserved case.

### Audit findings

- No CLI-level test project/tests exist for `tools/LogNormalizer.Cli` at
  all (upstream has `lognormalizer-invld-call.sh` for argument validation).
- `RulebaseLoader.cs`'s runaway-rule detection logic exists
  (`PeekIsRunawayRule`) but no C# test exercises it (no upstream-style
  "unmatched percent signs" fixture ported).
- Coverage of user-defined `type=` edge cases (duplicate-type conflicts, the
  historical `usrdef_nested_segfault.sh` regression, dotted/nested-IP
  variants) is much thinner in C# (1 test) than upstream (9 dedicated
  fixtures).


## Summary: what to do with this

The items under "Newly documented" above are now the canonical record of
deliberate C#-vs-C behavioral differences that existed in code/tests but had
no write-up; treat this file (and the README's "Known intentional behavior
notes" section, which links here) as the place to add the next one. The
remaining "Audit findings" are not decisions — they're mostly test coverage
gaps, plus the two low-severity, low-risk items noted inline (missing dead
error codes, no rulebase line-length cap).

## Annex: Parity check result

To ensure parity, we make use of the tests under lublognorm repository. Using the same input and expecting same output with liblognorm's lognormalizer is the target.

The test is run on Ubuntu on WSL2 over windows 11. It requires installing dotnet on Ubuntu, and updating the paths in the script. In order to have the lognormalizer binary, I compiled from the source and installed on the WSL2 instance, hence `/usr/local/bin/lognormalizer` exists. After building the normalizer with `dotnet build`, you can run the parity tests.

```text
Parity harness configuration
  fixtures:     /home/zafer/liblognorm-2.1.0.src/tests
  C binary:     /usr/local/bin/lognormalizer
  C library:    <system loader paths>
  dotnet:       /usr/lib/dotnet/dotnet (10.0.109)
  C# CLI:       /home/zafer/DeltaZulu.Normalize/src/tools/LogNormalizer.Cli/bin/Debug/net10.0/lognormalizer.dll
  timeout:      10s per process

Fixture discovery
  fixture files found:          139
  fixture files selected:       110
  skipped by filename policy:   26
  skipped without add_rule:     3
  skipped execute_with_string:  0
  skipped without rulebase:     0
  skipped without version:      130
  runnable execute cases:       374

[  1/374] PASS        alternative_nested.sh#1                0.140s  'a 47:11 b'
[  2/374] PASS        alternative_nested.sh#2                0.128s  'a 0x4711 b'
[  3/374] PASS        alternative_segfault.sh#1              0.119s  '1.2.3.4 - TEST_OK'
[  4/374] PASS        alternative_segfault.sh#2              0.118s  '1.2.3.4 100 TEST_OK'
[  5/374] PASS        alternative_segfault.sh#3              0.108s  '1.2.3.4 ERR TEST_OK'
[  6/374] PASS        alternative_simple.sh#1                0.111s  'a 4711 b'
[  7/374] PASS        alternative_simple.sh#2                0.120s  'a 0x4711 b'
[  8/374] PASS        alternative_three.sh#1                 0.104s  'a 4711 b'
[  9/374] PASS        alternative_three.sh#2                 0.123s  'a 0x4711 b'
[ 10/374] PASS        alternative_three.sh#3                 0.134s  'a 0xyz b'
[ 11/374] PASS        annotate.sh#1                          0.138s  '<37>1 2016-11-03T23:59:59+03:00 server.example.net TAG . - -'
[ 12/374] PASS        annotate.sh#2                          0.150s  '<37>1 2016-11-03T23:59:59+03:00 server.example.net TAG + - -'
[ 13/374] PASS        annotate.sh#3                          0.125s  '<6>1 2016-09-02T07:41:07+02:00 server.example.net TAG - - -'
[ 14/374] PASS        field_cee-syslog.sh#1                  0.099s  '@cee:{"f1": "1", "f2": 2}'
[ 15/374] PASS        field_cee-syslog.sh#2                  0.113s  '@cee:{"f1": "1", "f2": 2} '
[ 16/374] PASS        field_cee-syslog.sh#3                  0.148s  '@cee: {"f1": "1", "f2": 2}'
[ 17/374] PASS        field_cee-syslog.sh#4                  0.115s  '@cee:     {"f1": "1", "f2": 2}'
[ 18/374] PASS        field_cee-syslog.sh#5                  0.119s  '@cee: {"f1": "1", "f2": 2} data'
[ 19/374] PASS        field_cee-syslog_jsoncnf.sh#1          0.096s  '@cee:{"f1": "1", "f2": 2}'
[ 20/374] PASS        field_cee-syslog_jsoncnf.sh#2          0.100s  '@cee:{"f1": "1", "f2": 2} '
[ 21/374] PASS        field_cee-syslog_jsoncnf.sh#3          0.096s  '@cee: {"f1": "1", "f2": 2}'
[ 22/374] PASS        field_cee-syslog_jsoncnf.sh#4          0.099s  '@cee:     {"f1": "1", "f2": 2}'
[ 23/374] PASS        field_cee-syslog_jsoncnf.sh#5          0.105s  '@cee: {"f1": "1", "f2": 2} data'
[ 24/374] PASS        field_cef.sh#1                         0.079s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| aa=field1 bb=this is a v...
[ 25/374] PASS        field_cef.sh#2                         0.070s  'CEF:0|Vendor|Product\\|1\\|\\\\|Version|Signature ID|some name|Severity| aa=field1 bb=...
[ 26/374] PASS        field_cef.sh#3                         0.076s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| aa=field1 bb=this is a \...
[ 27/374] PASS        field_cef.sh#4                         0.076s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity|'
[ 28/374] PASS        field_cef.sh#5                         0.084s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| name=value'
[ 29/374] PASS        field_cef.sh#6                         0.091s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| name=val\\nue'
[ 30/374] PASS        field_cef.sh#7                         0.100s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| n,me=value'
[ 31/374] PASS        field_cef.sh#8                         0.100s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| name=v\\alue'
[ 32/374] PASS        field_cef.sh#9                         0.085s  'CEF:0|V\\endor|Product|Version|Signature ID|some name|Severity| name=value'
[ 33/374] PASS        field_cef.sh#10                        0.086s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| '
[ 34/374] PASS        field_cef.sh#11                        0.088s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity|   '
[ 35/374] PASS        field_cef.sh#12                        0.091s  'CEF:0|Vendor'
[ 36/374] PASS        field_cef.sh#13                        0.090s  'CEF:1|Vendor|Product|Version|Signature ID|some name|Severity| aa=field1 bb=this is a \...
[ 37/374] PASS        field_cef.sh#14                        0.084s  ''
[ 38/374] PASS        field_cef.sh#15                        0.090s  'CEF:0|ArcSight|ArcSight|10.0.0.15.0|rule:101|FOO-UNIX-Bypassing Golden Host-Direct Roo...
[ 39/374] PASS        field_cef_jsoncnf.sh#1                 0.103s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| aa=field1 bb=this is a v...
[ 40/374] PASS        field_cef_jsoncnf.sh#2                 0.112s  'CEF:0|Vendor|Product\\|1\\|\\\\|Version|Signature ID|some name|Severity| aa=field1 bb=...
[ 41/374] PASS        field_cef_jsoncnf.sh#3                 0.101s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| aa=field1 bb=this is a \...
[ 42/374] PASS        field_cef_jsoncnf.sh#4                 0.099s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity|'
[ 43/374] PASS        field_cef_jsoncnf.sh#5                 0.094s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| name=value'
[ 44/374] PASS        field_cef_jsoncnf.sh#6                 0.095s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| name=val\\nue'
[ 45/374] PASS        field_cef_jsoncnf.sh#7                 0.101s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| n,me=value'
[ 46/374] PASS        field_cef_jsoncnf.sh#8                 0.101s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| name=v\\alue'
[ 47/374] PASS        field_cef_jsoncnf.sh#9                 0.087s  'CEF:0|V\\endor|Product|Version|Signature ID|some name|Severity| name=value'
[ 48/374] PASS        field_cef_jsoncnf.sh#10                0.084s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity| '
[ 49/374] PASS        field_cef_jsoncnf.sh#11                0.086s  'CEF:0|Vendor|Product|Version|Signature ID|some name|Severity|   '
[ 50/374] PASS        field_cef_jsoncnf.sh#12                0.106s  'CEF:0|Vendor'
[ 51/374] PASS        field_cef_jsoncnf.sh#13                0.091s  'CEF:1|Vendor|Product|Version|Signature ID|some name|Severity| aa=field1 bb=this is a \...
[ 52/374] PASS        field_cef_jsoncnf.sh#14                0.076s  ''
[ 53/374] PASS        field_cef_jsoncnf.sh#15                0.091s  'CEF:0|ArcSight|ArcSight|10.0.0.15.0|rule:101|FOO-UNIX-Bypassing Golden Host-Direct Roo...
[ 54/374] PASS        field_checkpoint-lea-terminator.sh#1   0.082s  '[ tcp_flags: RST-ACK; src: 192.168.0.1; ]'
[ 55/374] PASS        field_checkpoint-lea-terminator.sh#2   0.082s  '[ tcp_flags: RST-ACK; src: 192.168.0.1 ]'
[ 56/374] PASS        field_checkpoint-lea-terminator.sh#3   0.086s  '[ tcp_flags:"RST-ACK"; src:"192.168.0.1"; ]'
[ 57/374] PASS        field_checkpoint-lea-terminator.sh#4   0.107s  '[ tcp_flags:"RST-ACK"; src:"192.168.0.1" ]'
[ 58/374] PASS        field_checkpoint-lea-terminator.sh#5   0.121s  '[ key:"value with \\"escaped quote\\""; path:"C:\\\\Windows\\\\System32" ]'
[ 59/374] PASS        field_checkpoint-lea.sh#1              0.118s  'tcp_flags: RST-ACK; src: 192.168.0.1;'
[ 60/374] PASS        field_checkpoint-lea_jsoncnf.sh#1      0.122s  'tcp_flags: RST-ACK; src: 192.168.0.1;'
[ 61/374] PASS        field_checkpoint-lea_jsoncnf.sh#2      0.102s  'tcp_flags:"RST-ACK"; src:"192.168.0.1";'
[ 62/374] PASS        field_cisco-interface-spec-at-EOL.sh#1   0.087s  'begin outside:192.0.2.1/50349 end'
[ 63/374] PASS        field_cisco-interface-spec-at-EOL.sh#2   0.082s  'begin outside:192.0.2.1/50349'
[ 64/374] PASS        field_duration.sh#1                    0.085s  'duration 0:00:42 bytes'
[ 65/374] PASS        field_duration.sh#2                    0.076s  'duration 0:00:42'
[ 66/374] PASS        field_duration.sh#3                    0.071s  'duration 9:00:42 bytes'
[ 67/374] PASS        field_duration.sh#4                    0.091s  'duration 00:00:42 bytes'
[ 68/374] PASS        field_duration.sh#5                    0.093s  'duration 37:59:42 bytes'
[ 69/374] PASS        field_duration.sh#6                    0.087s  'duration 37:60:42 bytes'
[ 70/374] PASS        field_duration_jsoncnf.sh#1            0.113s  'duration 0:00:42 bytes'
[ 71/374] PASS        field_duration_jsoncnf.sh#2            0.115s  'duration 0:00:42'
[ 72/374] PASS        field_duration_jsoncnf.sh#3            0.113s  'duration 9:00:42 bytes'
[ 73/374] PASS        field_duration_jsoncnf.sh#4            0.111s  'duration 00:00:42 bytes'
[ 74/374] PASS        field_duration_jsoncnf.sh#5            0.109s  'duration 37:59:42 bytes'
[ 75/374] PASS        field_duration_jsoncnf.sh#6            0.110s  'duration 37:60:42 bytes'
[ 76/374] PASS        field_float-fmt_number.sh#1            0.105s  'here is a number 15.9 in floating pt form'
[ 77/374] PASS        field_float-fmt_number.sh#2            0.104s  'here is a negative number -4.2 for you'
[ 78/374] PASS        field_float-fmt_number.sh#3            0.105s  'here is another real number 2.71.'
[ 79/374] PASS        field_float.sh#1                       0.093s  'here is a number 15.9 in floating pt form'
[ 80/374] PASS        field_float.sh#2                       0.116s  'here is a negative number -4.2 for you'
[ 81/374] PASS        field_float.sh#3                       0.090s  'here is another real number 2.71.'
[ 82/374] PASS        field_float_jsoncnf.sh#1               0.113s  'here is a number 15.9 in floating pt form'
[ 83/374] PASS        field_float_jsoncnf.sh#2               0.114s  'here is a negative number -4.2 for you'
[ 84/374] PASS        field_float_jsoncnf.sh#3               0.120s  'here is another real number 2.71.'
[ 85/374] PASS        field_hexnumber-fmt_number.sh#1        0.110s  'here is a number 0x1234 in hex form'
[ 86/374] PASS        field_hexnumber-fmt_number.sh#2        0.117s  'here is a number 0x1234in hex form'
[ 87/374] PASS        field_hexnumber.sh#1                   0.089s  'here is a number 0x1234 in hex form'
[ 88/374] PASS        field_hexnumber.sh#2                   0.091s  'here is a number 0x1234in hex form'
[ 89/374] PASS        field_hexnumber_jsoncnf.sh#1           0.115s  'here is a number 0x1234 in hex form'
[ 90/374] PASS        field_hexnumber_jsoncnf.sh#2           0.112s  'here is a number 0x1234in hex form'
[ 91/374] PASS        field_hexnumber_range.sh#1             0.098s  'here is a number 0x12 in hex form'
[ 92/374] PASS        field_hexnumber_range.sh#2             0.109s  'here is a number 0x0 in hex form'
[ 93/374] PASS        field_hexnumber_range.sh#3             0.114s  'here is a number 0xBf in hex form'
[ 94/374] PASS        field_hexnumber_range.sh#4             0.110s  'here is a number 0xc0 in hex form'
[ 95/374] PASS        field_hexnumber_range_jsoncnf.sh#1     0.118s  'here is a number 0x12 in hex form'
[ 96/374] PASS        field_hexnumber_range_jsoncnf.sh#2     0.107s  'here is a number 0x0 in hex form'
[ 97/374] PASS        field_hexnumber_range_jsoncnf.sh#3     0.107s  'here is a number 0xBf in hex form'
[ 98/374] PASS        field_hexnumber_range_jsoncnf.sh#4     0.092s  'here is a number 0xc0 in hex form'
[ 99/374] PASS        field_ipv6.sh#1                        0.091s  'ABCD:EF01:2345:6789:ABCD:EF01:2345:6789'
[100/374] PASS        field_ipv6.sh#2                        0.090s  'ABCD:EF01:2345:6789:abcd:EF01:2345:6789'
[101/374] PASS        field_ipv6.sh#3                        0.073s  '2001:DB8:0:0:8:800:200C:417A'
[102/374] PASS        field_ipv6.sh#4                        0.075s  '0:0:0:0:0:0:0:1'
[103/374] PASS        field_ipv6.sh#5                        0.084s  '2001:DB8::8:800:200C:417A'
[104/374] PASS        field_ipv6.sh#6                        0.086s  'FF01::101'
[105/374] PASS        field_ipv6.sh#7                        0.074s  '::1'
[106/374] PASS        field_ipv6.sh#8                        0.076s  '::'
[107/374] PASS        field_ipv6.sh#9                        0.080s  '0:0:0:0:0:0:13.1.68.3'
[108/374] PASS        field_ipv6.sh#10                       0.078s  '::13.1.68.3'
[109/374] PASS        field_ipv6.sh#11                       0.074s  '::FFFF:129.144.52.38'
[110/374] PASS        field_ipv6.sh#12                       0.074s  '2001:DB8::8::800:200C:417A'
[111/374] PASS        field_ipv6.sh#13                       0.078s  'ABCD:EF01:2345:6789:ABCD:EF01:2345::6789'
[112/374] PASS        field_ipv6.sh#14                       0.082s  'ABCD:EF01:2345:6789:ABCD:EF01:2345:1:6798'
[113/374] PASS        field_ipv6.sh#15                       0.072s  ':0:0:0:0:0:0:1'
[114/374] PASS        field_ipv6.sh#16                       0.069s  '0:0:0:0:0:0:0:'
[115/374] PASS        field_ipv6.sh#17                       0.081s  '13.1.68.3'
[116/374] PASS        field_ipv6_jsoncnf.sh#1                0.098s  'ABCD:EF01:2345:6789:ABCD:EF01:2345:6789'
[117/374] PASS        field_ipv6_jsoncnf.sh#2                0.085s  'ABCD:EF01:2345:6789:abcd:EF01:2345:6789'
[118/374] PASS        field_ipv6_jsoncnf.sh#3                0.091s  '2001:DB8:0:0:8:800:200C:417A'
[119/374] PASS        field_ipv6_jsoncnf.sh#4                0.089s  '0:0:0:0:0:0:0:1'
[120/374] PASS        field_ipv6_jsoncnf.sh#5                0.094s  '2001:DB8::8:800:200C:417A'
[121/374] PASS        field_ipv6_jsoncnf.sh#6                0.084s  'FF01::101'
[122/374] PASS        field_ipv6_jsoncnf.sh#7                0.090s  '::1'
[123/374] PASS        field_ipv6_jsoncnf.sh#8                0.082s  '::'
[124/374] PASS        field_ipv6_jsoncnf.sh#9                0.091s  '0:0:0:0:0:0:13.1.68.3'
[125/374] PASS        field_ipv6_jsoncnf.sh#10               0.100s  '::13.1.68.3'
[126/374] PASS        field_ipv6_jsoncnf.sh#11               0.137s  '::FFFF:129.144.52.38'
[127/374] PASS        field_ipv6_jsoncnf.sh#12               0.131s  '2001:DB8::8::800:200C:417A'
[128/374] PASS        field_ipv6_jsoncnf.sh#13               0.115s  'ABCD:EF01:2345:6789:ABCD:EF01:2345::6789'
[129/374] PASS        field_ipv6_jsoncnf.sh#14               0.111s  'ABCD:EF01:2345:6789:ABCD:EF01:2345:1:6798'
[130/374] PASS        field_ipv6_jsoncnf.sh#15               0.121s  ':0:0:0:0:0:0:1'
[131/374] PASS        field_ipv6_jsoncnf.sh#16               0.110s  '0:0:0:0:0:0:0:'
[132/374] PASS        field_ipv6_jsoncnf.sh#17               0.114s  '13.1.68.3'
[133/374] PASS        field_json.sh#1                        0.119s  '{"f1": "1", "f2": 2}'
[134/374] PASS        field_json.sh#2                        0.126s  '{"f1": "1", "f2": 2}'
[135/374] PASS        field_json.sh#3                        0.123s  '{"f1": "1", "f2": 2}      '
[136/374] PASS        field_json.sh#4                        0.131s  'begin {"f1": "1", "f2": 2}'
[137/374] PASS        field_json.sh#5                        0.128s  'begin {"f1": "1", "f2": 2}end'
[138/374] PASS        field_json.sh#6                        0.130s  'begin {"f1": "1", "f2": 2} end'
[139/374] PASS        field_json.sh#7                        0.104s  'begin {"f1": "1", "f2": 2} \t     end'
[140/374] PASS        field_json.sh#8                        0.094s  '{"f1": "1", "f2": 2}end'
[141/374] PASS        field_json.sh#9                        0.115s  '{"f1": "1", f2: 2}'
[142/374] PASS        field_json.sh#10                       0.108s  '{"f1": "1"}-{"f2": 2}'
[143/374] PASS        field_json.sh#11                       0.107s  '{"f1": "1", "f2": 2}'
[144/374] PASS        field_json.sh#12                       0.075s  '15:00'
[145/374] PASS        field_json_jsoncnf.sh#1                0.095s  '{"f1": "1", "f2": 2}'
[146/374] PASS        field_json_jsoncnf.sh#2                0.102s  '{"f1": "1", "f2": 2}'
[147/374] PASS        field_json_jsoncnf.sh#3                0.111s  '{"f1": "1", "f2": 2}      '
[148/374] PASS        field_json_jsoncnf.sh#4                0.131s  'begin {"f1": "1", "f2": 2}'
[149/374] PASS        field_json_jsoncnf.sh#5                0.143s  'begin {"f1": "1", "f2": 2}end'
[150/374] PASS        field_json_jsoncnf.sh#6                0.122s  'begin {"f1": "1", "f2": 2} end'
[151/374] PASS        field_json_jsoncnf.sh#7                0.146s  'begin {"f1": "1", "f2": 2} \t     end'
[152/374] PASS        field_json_jsoncnf.sh#8                0.144s  '{"f1": "1", "f2": 2}end'
[153/374] PASS        field_json_jsoncnf.sh#9                0.172s  '{"f1": "1", f2: 2}'
[154/374] PASS        field_json_jsoncnf.sh#10               0.171s  '{"f1": "1"}-{"f2": 2}'
[155/374] PASS        field_json_jsoncnf.sh#11               0.136s  '{"f1": "1", "f2": 2}'
[156/374] PASS        field_json_jsoncnf.sh#12               0.096s  '15:00'
[157/374] PASS        field_json_skipempty.sh#1              0.110s  '{"f1": "1", "f2": 2, "f3": "", "f4": {}, "f5": []}'
[158/374] PASS        field_json_skipempty.sh#2              0.110s  '{"f1": "1", "f2": 2, "f3": "", "f4": {}, "f5": []}'
[159/374] PASS        field_kernel_timestamp.sh#1            0.087s  'begin [12345.123456] end'
[160/374] PASS        field_kernel_timestamp.sh#2            0.097s  'begin [12345.123456]'
[161/374] PASS        field_kernel_timestamp.sh#3            0.111s  '[12345.123456]'
[162/374] PASS        field_kernel_timestamp.sh#4            0.099s  '[154469.133028]'
[163/374] PASS        field_kernel_timestamp.sh#5            0.094s  '[123456789012.123456]'
[164/374] PASS        field_kernel_timestamp.sh#6            0.090s  '[1234.123456]'
[165/374] PASS        field_kernel_timestamp.sh#7            0.087s  '[1234567890123.123456]'
[166/374] PASS        field_kernel_timestamp.sh#8            0.086s  '[123456789012.12345]'
[167/374] PASS        field_kernel_timestamp.sh#9            0.092s  '[123456789012.1234567]'
[168/374] PASS        field_kernel_timestamp.sh#10           0.102s  '(123456789012.123456]'
[169/374] PASS        field_kernel_timestamp.sh#11           0.097s  '[123456789012.123456'
[170/374] PASS        field_kernel_timestamp_jsoncnf.sh#1    0.126s  'begin [12345.123456] end'
[171/374] PASS        field_kernel_timestamp_jsoncnf.sh#2    0.117s  'begin [12345.123456]'
[172/374] PASS        field_kernel_timestamp_jsoncnf.sh#3    0.100s  '[12345.123456]'
[173/374] PASS        field_kernel_timestamp_jsoncnf.sh#4    0.095s  '[154469.133028]'
[174/374] PASS        field_kernel_timestamp_jsoncnf.sh#5    0.115s  '[123456789012.123456]'
[175/374] PASS        field_kernel_timestamp_jsoncnf.sh#6    0.125s  '[1234.123456]'
[176/374] PASS        field_kernel_timestamp_jsoncnf.sh#7    0.116s  '[1234567890123.123456]'
[177/374] PASS        field_kernel_timestamp_jsoncnf.sh#8    0.117s  '[123456789012.12345]'
[178/374] PASS        field_kernel_timestamp_jsoncnf.sh#9    0.120s  '[123456789012.1234567]'
[179/374] PASS        field_kernel_timestamp_jsoncnf.sh#10   0.141s  '(123456789012.123456]'
[180/374] PASS        field_kernel_timestamp_jsoncnf.sh#11   0.107s  '[123456789012.123456'
[181/374] PASS        field_mac48.sh#1                       0.084s  'f0:f6:1c:5f:cc:a2'
[182/374] PASS        field_mac48.sh#2                       0.091s  'f0-f6-1c-5f-cc-a2'
[183/374] PASS        field_mac48.sh#3                       0.091s  'f0-f6:1c:5f:cc-a2'
[184/374] PASS        field_mac48.sh#4                       0.087s  'f0:f6:1c:xf:cc:a2'
[185/374] PASS        field_mac48_jsoncnf.sh#1               0.114s  'f0:f6:1c:5f:cc:a2'
[186/374] PASS        field_mac48_jsoncnf.sh#2               0.105s  'f0-f6-1c-5f-cc-a2'
[187/374] PASS        field_mac48_jsoncnf.sh#3               0.104s  'f0-f6:1c:5f:cc-a2'
[188/374] PASS        field_mac48_jsoncnf.sh#4               0.104s  'f0:f6:1c:xf:cc:a2'
[189/374] PASS        field_name_value.sh#1                  0.090s  'name=value'
[190/374] PASS        field_name_value.sh#2                  0.084s  'name1=value1 name2=value2 name3=value3'
[191/374] PASS        field_name_value.sh#3                  0.089s  'name1=value1 name2=value2 name3=value3 '
[192/374] PASS        field_name_value.sh#4                  0.089s  'name1= name2=value2 name3=value3 '
[193/374] PASS        field_name_value.sh#5                  0.090s  'origin=core.action processed=67 failed=0 suspended=0 suspended.duration=0 resumed=0 '
[194/374] PASS        field_name_value.sh#6                  0.092s  'name'
[195/374] PASS        field_name_value.sh#7                  0.088s  'noname1 name2=value2 name3=value3 '
[196/374] PASS        field_name_value_jsoncnf.sh#1          0.115s  'name=value'
[197/374] PASS        field_name_value_jsoncnf.sh#2          0.114s  'name1=value1 name2=value2 name3=value3'
[198/374] PASS        field_name_value_jsoncnf.sh#3          0.109s  'name1=value1 name2=value2 name3=value3 '
[199/374] PASS        field_name_value_jsoncnf.sh#4          0.108s  'name1= name2=value2 name3=value3 '
[200/374] PASS        field_name_value_jsoncnf.sh#5          0.106s  'origin=core.action processed=67 failed=0 suspended=0 suspended.duration=0 resumed=0 '
[201/374] PASS        field_name_value_jsoncnf.sh#6          0.104s  'name'
[202/374] PASS        field_name_value_jsoncnf.sh#7          0.109s  'noname1 name2=value2 name3=value3 '
[203/374] PASS        field_name_value_whitespace.sh#1       0.114s  'name:value'
[204/374] PASS        field_name_value_whitespace.sh#2       0.110s  'name1:value1,name2:value2,name3:value3'
[205/374] PASS        field_name_value_whitespace.sh#3       0.116s  ' name1: abcd, name2 : value2 ,name3 :value3 '
[206/374] PASS        field_name_value_whitespace.sh#4       0.104s  'name1:"value1" , name2 : "value2" , name3 : value3 '
[207/374] PASS        field_name_value_whitespace.sh#5       0.104s  'name1:   , name2 : value2'
[208/374] PASS        field_name_value_whitespace.sh#6       0.110s  'name1:   '
[209/374] PASS        field_name_value_whitespace.sh#7       0.109s  ' name1: abcd, name2 : value2 ,name3 :value3 '
[210/374] PASS        field_number-fmt_number.sh#1           0.102s  'here is a number 1234 in dec form'
[211/374] PASS        field_number-fmt_number.sh#2           0.089s  'here is a number 1234in dec form'
[212/374] PASS        field_number.sh#1                      0.076s  'here is a number 1234 in dec form'
[213/374] PASS        field_number.sh#2                      0.078s  'here is a number 1234in dec form'
[214/374] PASS        field_number_maxval.sh#1               0.096s  'here is a number 234 in dec form'
[215/374] PASS        field_number_maxval.sh#2               0.100s  'here is a number 1234in dec form'
[216/374] PASS        field_op_quoted_string_escape.sh#1     0.111s  '"test with \\" quote"'
[217/374] PASS        field_op_quoted_string_escape.sh#2     0.096s  '"test with \\\\ slash"'
[218/374] PASS        field_op_quoted_string_escape.sh#3     0.097s  '"mixed \\\\ and \\" escapes"'
[219/374] PASS        field_op_quoted_string_escape.sh#4     0.090s  'word'
[220/374] PASS        field_quoted_string.sh#1               0.072s  '"alpha beta"'
[221/374] PASS        field_quoted_string.sh#2               0.076s  '""'
[222/374] PASS        field_quoted_string.sh#3               0.073s  '"unterminated'
[223/374] PASS        field_rest.sh#1                        0.071s  'Outside:10.20.30.40/35 (40.30.20.10/35)'
[224/374] PASS        field_rest.sh#2                        0.078s  'Outside:10.20.30.40/35 (40.30.20.10/35) with rest'
[225/374] PASS        field_rest.sh#3                        0.069s  'Outside:10.20.30.40/35 (40.30.20.10/35 brace missing'
[226/374] PASS        field_rest.sh#4                        0.067s  'Outside:10.20.30.40/35 40.30.20.10/35'
[227/374] PASS        field_rest.sh#5                        0.067s  'not at all!'
[228/374] PASS        field_rest.sh#6                        0.073s  'Outside 10.20.30.40/35 40.30.20.10/35'
[229/374] PASS        field_rest.sh#7                        0.083s  'Outside:10.20.30.40/aa 40.30.20.10/35'
[230/374] PASS        field_rest_jsoncnf.sh#1                0.091s  'Outside:10.20.30.40/35 (40.30.20.10/35)'
[231/374] PASS        field_rest_jsoncnf.sh#2                0.099s  'Outside:10.20.30.40/35 (40.30.20.10/35) with rest'
[232/374] PASS        field_rest_jsoncnf.sh#3                0.094s  'Outside:10.20.30.40/35 (40.30.20.10/35 brace missing'
[233/374] PASS        field_rest_jsoncnf.sh#4                0.105s  'Outside:10.20.30.40/35 40.30.20.10/35'
[234/374] PASS        field_rest_jsoncnf.sh#5                0.102s  'not at all!'
[235/374] PASS        field_rest_jsoncnf.sh#6                0.098s  'Outside 10.20.30.40/35 40.30.20.10/35'
[236/374] PASS        field_rest_jsoncnf.sh#7                0.126s  'Outside:10.20.30.40/aa 40.30.20.10/35'
[237/374] PASS        field_rfc5424timestamp-fmt_timestamp-unix-ms.sh#1   0.098s  'here is a timestamp 2000-03-11T14:15:16+01:00 in RFC5424 format'
[238/374] PASS        field_rfc5424timestamp-fmt_timestamp-unix-ms.sh#2   0.098s  'here is a timestamp 2000-03-11T14:15:16.1+01:00 in RFC5424 format'
[239/374] PASS        field_rfc5424timestamp-fmt_timestamp-unix-ms.sh#3   0.089s  'here is a timestamp 2000-03-11T14:15:16.12+01:00 in RFC5424 format'
[240/374] PASS        field_rfc5424timestamp-fmt_timestamp-unix-ms.sh#4   0.089s  'here is a timestamp 2000-03-11T14:15:16.123+01:00 in RFC5424 format'
[241/374] PASS        field_rfc5424timestamp-fmt_timestamp-unix-ms.sh#5   0.088s  'here is a timestamp 2000-03-11T14:15:16.1234+01:00 in RFC5424 format'
[242/374] PASS        field_rfc5424timestamp-fmt_timestamp-unix-ms.sh#6   0.085s  'here is a timestamp 2000-03-11T14:15:16.123456789+01:00 in RFC5424 format'
[243/374] PASS        field_rfc5424timestamp-fmt_timestamp-unix-ms.sh#7   0.080s  'here is a timestamp 2000-03-11T14:15:16+01:00in RFC5424 format'
[244/374] PASS        field_rfc5424timestamp-fmt_timestamp-unix.sh#1   0.074s  'here is a timestamp 2000-03-11T14:15:16+01:00 in RFC5424 format'
[245/374] PASS        field_rfc5424timestamp-fmt_timestamp-unix.sh#2   0.071s  'here is a timestamp 2000-03-11T14:15:16.321+01:00 in RFC5424 format'
[246/374] PASS        field_rfc5424timestamp-fmt_timestamp-unix.sh#3   0.069s  'here is a timestamp 2000-03-11T14:15:16+01:00in RFC5424 format'
[247/374] PASS        field_string.sh#1                      0.061s  'a test b'
[248/374] PASS        field_string.sh#2                      0.059s  'a "test" b'
[249/374] PASS        field_string.sh#3                      0.058s  'a "test with space" b'
[250/374] PASS        field_string.sh#4                      0.058s  'a "test with "" double escape" b'
[251/374] PASS        field_string.sh#5                      0.064s  'a "test with \\" backslash escape" b'
[252/374] PASS        field_string.sh#6                      0.082s  'a test b'
[253/374] PASS        field_string.sh#7                      0.072s  'a "test" b'
[254/374] PASS        field_string.sh#8                      0.076s  'a test b'
[255/374] PASS        field_string.sh#9                      0.073s  'a [test] b'
[256/374] PASS        field_string.sh#10                     0.075s  'a [test test2] b'
[257/374] PASS        field_string_dashIsEmpty.sh#1          0.070s  '"-"'
[258/374] PASS        field_string_dashIsEmpty.sh#2          0.066s  '"-"'
[259/374] PASS        field_string_doc_sample_lazy.sh#1      0.073s  '12:34 56'
[260/374] PASS        field_string_lazy_matching.sh#1        0.077s  'Rule-ID: XY7azl704-84a39894783423467a33f5b48bccd23c-a0n63i2\\r\\nQNas: '
[261/374] PASS        field_string_lazy_matching.sh#2        0.079s  'Rule-ID: XY7azl704-84a39894783423467a33f5b48bccd23c-a0n63i2 LWL'
[262/374] PASS        field_string_perm_chars.sh#1           0.072s  'a abc b'
[263/374] PASS        field_string_perm_chars.sh#2           0.071s  'a abcd b'
[264/374] PASS        field_string_perm_chars.sh#3           0.072s  'a abbbbbcccbaaaa b'
[265/374] PASS        field_string_perm_chars.sh#4           0.071s  'a "abc" b'
[266/374] PASS        field_string_perm_chars.sh#5           0.084s  'a abc b'
[267/374] PASS        field_string_perm_chars.sh#6           0.077s  'a 12x3 b'
[268/374] PASS        field_string_perm_chars.sh#7           0.071s  'a abcdefghijklmnopqrstuvwxyZ b'
[269/374] PASS        field_string_perm_chars.sh#8           0.070s  'a abcd1 b'
[270/374] PASS        field_string_perm_chars.sh#9           0.074s  'a abcdefghijklmnopqrstuvwxyZ b'
[271/374] PASS        field_string_perm_chars.sh#10          0.072s  'a abcd1 b'
[272/374] PASS        field_string_perm_chars.sh#11          0.072s  'a abcd1_ b'
[273/374] PASS        field_v2-iptables.sh#1                 0.057s  'iptables output denied: IN= OUT=eth0 SRC=176.9.56.141 DST=168.192.14.3 LEN=32 TOS=0x00...
[274/374] PASS        field_v2-iptables_jsoncnf.sh#1         0.068s  'iptables output denied: IN= OUT=eth0 SRC=176.9.56.141 DST=168.192.14.3 LEN=32 TOS=0x00...
[275/374] PASS        field_v2-iptables_jsoncnf.sh#2         0.057s  'iptables: IN=value SECOND=test'
[276/374] PASS        field_v2-iptables_jsoncnf.sh#3         0.059s  'iptables: IN= SECOND=test'
[277/374] PASS        field_v2-iptables_jsoncnf.sh#4         0.058s  'iptables: IN SECOND=test'
[278/374] PASS        field_v2-iptables_jsoncnf.sh#5         0.062s  'iptables: IN=invalue OUT=outvalue'
[279/374] PASS        field_v2-iptables_jsoncnf.sh#6         0.057s  'iptables: IN= OUT=outvalue'
[280/374] PASS        field_v2-iptables_jsoncnf.sh#7         0.061s  'iptables: IN OUT=outvalue'
[281/374] PASS        field_v2-iptables_jsoncnf.sh#8         0.069s  'iptables: in=value'
[282/374] PASS        field_v2-iptables_jsoncnf.sh#9         0.060s  'iptables: in='
[283/374] PASS        field_v2-iptables_jsoncnf.sh#10        0.058s  'iptables: in'
[284/374] PASS        field_v2-iptables_jsoncnf.sh#11        0.056s  'iptables: IN'
[285/374] PASS        field_v2-iptables_jsoncnf.sh#12        0.058s  'iptables: IN=invalue  OUT=outvalue'
[286/374] PASS        field_v2-iptables_jsoncnf.sh#13        0.062s  'iptables: IN=  OUT=outvalue'
[287/374] PASS        field_v2-iptables_jsoncnf.sh#14        0.062s  'iptables: IN  OUT=outvalue'
[288/374] PASS        field_whitespace.sh#1                  0.066s  'word1  word2'
[289/374] PASS        field_whitespace.sh#2                  0.065s  'word1 word2'
[290/374] PASS        field_whitespace.sh#3                  0.063s  '"word1"\t"word2"'
[291/374] PASS        field_whitespace.sh#4                  0.059s  '"word1"\t   \t"word2"'
[292/374] PASS        field_whitespace_jsoncnf.sh#1          0.072s  'word1  word2'
[293/374] PASS        field_whitespace_jsoncnf.sh#2          0.069s  'word1 word2'
[294/374] PASS        field_whitespace_jsoncnf.sh#3          0.070s  '"word1"\t"word2"'
[295/374] PASS        field_whitespace_jsoncnf.sh#4          0.070s  '"word1"\t   \t"word2"'
[296/374] PASS        include.sh#1                           0.057s  'f0:f6:1c:5f:cc:a2'
[297/374] PASS        include_RULEBASES.sh#1                 0.058s  'f0:f6:1c:5f:cc:a2'
[298/374] PASS        include_RULEBASES.sh#2                 0.058s  'f0:f6:1c:5f:cc:a2'
[299/374] PASS        literal.sh#1                           0.067s  'a'
[300/374] PASS        literal.sh#2                           0.066s  'Test a'
[301/374] PASS        literal.sh#3                           0.083s  'Test a End'
[302/374] PASS        literal.sh#4                           0.071s  'a 4711:0x4712 b'
[303/374] PASS        missing_line_ending.sh#1               0.053s  'f0:f6:1c:5f:cc:a2'
[304/374] PASS        missing_line_ending.sh#2               0.051s  'f0-f6-1c-5f-cc-a2'
[305/374] PASS        missing_line_ending.sh#3               0.049s  'f0-f6:1c:5f:cc-a2'
[306/374] PASS        missing_line_ending.sh#4               0.048s  'f0:f6:1c:xf:cc:a2'
[307/374] PASS        names.sh#1                             0.068s  'a 4711:0x4712 b'
[308/374] PASS        names.sh#2                             0.070s  'a 4711:0x4712 b'
[309/374] PASS        names.sh#3                             0.069s  'a 4711:0x4712 b'
[310/374] PASS        names.sh#4                             0.054s  'a 4711:0x4712 b'
[311/374] PASS        parser_LF_jsoncnf.sh#1                 0.067s  'here is a number 0x1234 in hex form'
[312/374] PASS        parser_LF_jsoncnf.sh#2                 0.075s  'here is a number 0x1234in hex form'
[313/374] PASS        parser_eof_hardening.sh#1              0.074s  'abc'
[314/374] PASS        parser_eof_hardening.sh#2              0.060s  'pre'
[315/374] PASS        parser_eof_hardening.sh#3              0.067s  '[1,2]'
[316/374] PASS        parser_eof_hardening.sh#4              0.058s  'pre'
[317/374] PASS        parser_eof_hardening.sh#5              0.057s  'src:   '
[318/374] PASS        parser_eof_hardening.sh#6              0.059s  'src: "unterminated'
[319/374] PASS        parser_eof_hardening.sh#7              0.055s  'src::'
[320/374] PASS        parser_prios.sh#1                      0.069s  'f0:f6:1c:5f:cc:a2'
[321/374] PASS        parser_prios.sh#2                      0.069s  'f0-f6-1c-5f-cc-a2'
[322/374] PASS        parser_prios.sh#3                      0.095s  'f0-f6:1c:5f:cc-a2'
[323/374] PASS        parser_prios.sh#4                      0.087s  'f0:f6:1c:5f:cc:a2'
[324/374] PASS        parser_prios.sh#5                      0.092s  'f0-f6-1c-5f-cc-a2'
[325/374] PASS        parser_prios.sh#6                      0.078s  'f0-f6:1c:5f:cc-a2'
[326/374] PASS        parser_whitespace_jsoncnf.sh#1         0.083s  'here is a number 0x1234 in hex form'
[327/374] PASS        parser_whitespace_jsoncnf.sh#2         0.072s  'here is a number 0x1234in hex form'
[328/374] PASS        repeat_alternative_nested.sh#1         0.071s  'a 1:2, 3:4, 5:6, 7:8 b'
[329/374] PASS        repeat_alternative_nested.sh#2         0.073s  'a 0x4711 b'
[330/374] PASS        repeat_alternative_nested.sh#3         0.074s  'a 1:2, 0x4711 b'
[331/374] PASS        repeat_fail_on_duplicate.sh#1          0.073s  'a 1:2 b'
[332/374] PASS        repeat_mismatch_in_while.sh#1          0.078s  'Aug 18 13:18:45 192.168.99.2 %ASA-6-106015: Deny TCP (no connection) from 173.252.88.6...
[333/374] PASS        repeat_mismatch_in_while.sh#2          0.074s  'Aug 18 13:18:45 192.168.99.2 %ASA-6-106015: Deny TCP (no connection) from 173.252.88.6...
[334/374] PASS        repeat_mismatch_in_while.sh#3          0.075s  'Aug 18 13:18:45 192.168.99.2 %ASA-6-106015: Deny TCP (no connection) from 173.252.88.6...
[335/374] PASS        repeat_name_dot.sh#1                   0.069s  'a 1:2, 3:4, 5:6, 7:8 b test'
[336/374] PASS        repeat_named_while_segfault.sh#1       0.071s  'a 1 : 2 b'
[337/374] PASS        repeat_simple.sh#1                     0.071s  'a 1:2, 3:4, 5:6, 7:8 b test'
[338/374] PASS        repeat_very_simple.sh#1                0.070s  'a 1, 2, 3, 4 b test'
[339/374] PASS        repeat_while_alternative.sh#1          0.068s  'a 1:2, 3:4,5:6, 7:8 b test'
[340/374] PASS        rule_last_str_long.sh#1                0.067s  'string'
[341/374] PASS        rule_last_str_long.sh#2                0.059s  'before string'
[342/374] PASS        rule_last_str_long.sh#3                0.056s  'string after'
[343/374] PASS        rule_last_str_long.sh#4                0.061s  'before string after'
[344/374] PASS        rule_last_str_long.sh#5                0.073s  'before string middle string'
[345/374] PASS        rule_last_str_short.sh#1               0.069s  'string'
[346/374] PASS        runaway_rule.sh#1                      0.069s  'data'
[347/374] PASS        runaway_rule_comment.sh#1              0.062s  'data'
[348/374] PASS        seq_simple.sh#1                        0.078s  'a 4711:0x4712 b'
[349/374] PASS        strict_prefix_actual_sample1.sh#1      0.081s  'Sep 28 23:53:19 192.168.123.99 BL-WLC01: *dtlArpTask: Sep 28 23:53:19.614: #LOG-3-Q_IN...
[350/374] PASS        strict_prefix_matching_1.sh#1          0.063s  'a word w1 another word w2'
[351/374] PASS        strict_prefix_matching_1.sh#2          0.065s  'a word w1'
[352/374] PASS        strict_prefix_matching_2.sh#1          0.093s  'a word w1 l b'
[353/374] PASS        strict_prefix_matching_2.sh#2          0.087s  'a word w1 l2 b'
[354/374] PASS        strict_prefix_matching_2.sh#3          0.085s  'a word w1 l3 b'
[355/374] PASS        usrdef_actual1.sh#1                    0.078s  'a pid[4711] b'
[356/374] PASS        usrdef_actual1.sh#2                    0.085s  'a iface inside:10.0.0.1 b'
[357/374] PASS        usrdef_actual1.sh#3                    0.079s  'a iface inside:10.0.0.1/514 b'
[358/374] PASS        usrdef_actual1.sh#4                    0.075s  'a iface inside/10.0.0.1(514) b'
[359/374] PASS        usrdef_ipaddr.sh#1                     0.062s  'an ip address 10.0.0.1'
[360/374] PASS        usrdef_ipaddr.sh#2                     0.060s  'an ip address 127::1'
[361/374] PASS        usrdef_ipaddr.sh#3                     0.063s  'an ip address 2001:DB8:0:1::10:1FF'
[362/374] PASS        usrdef_ipaddr_dotdot.sh#1              0.065s  'an ip address 10.0.0.1'
[363/374] PASS        usrdef_ipaddr_dotdot.sh#2              0.062s  'an ip address 127::1'
[364/374] PASS        usrdef_ipaddr_dotdot.sh#3              0.069s  'an ip address 2001:DB8:0:1::10:1FF'
[365/374] PASS        usrdef_ipaddr_dotdot2.sh#1             0.063s  'a word word1 an ip address 10.0.0.1 another word word2'
[366/374] PASS        usrdef_ipaddr_dotdot2.sh#2             0.061s  'a word word1 an ip address 2001:DB8:0:1::10:1FF another word word2'
[367/374] PASS        usrdef_ipaddr_dotdot3.sh#1             0.076s  'a word word1 an ip address 10.0.0.1 another word word2'
[368/374] PASS        usrdef_ipaddr_dotdot3.sh#2             0.081s  'a word word1 an ip address 2001:DB8:0:1::10:1FF another word word2'
[369/374] PASS        usrdef_ipaddr_dotdot3.sh#3             0.089s  'a word word1 an ip address 111 another word word2'
[370/374] PASS        usrdef_nested_segfault.sh#1            0.072s  'two bytes 0xff 0x16 stop'
[371/374] PASS        usrdef_simple.sh#1                     0.071s  'a word w1 a byte 0xff another word w2'
[372/374] PASS        usrdef_two.sh#1                        0.080s  'a word w1 a byte 0xff another word w2'
[373/374] PASS        usrdef_two.sh#2                        0.075s  'a word w1 a byte TEST another word w2'
[374/374] PASS        usrdef_twotypes.sh#1                   0.079s  'a word w1 a byte 0xff another word w2'

Parity summary
  discovered cases:     374
  executed cases:       374
  passed:               374
  mismatches:           0
  unparseable outputs:  0
  execution errors:     0
  elapsed:              33.575s
```
