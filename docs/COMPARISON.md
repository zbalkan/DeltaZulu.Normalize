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
  aren't exercised: `v2-iptables` (no C# tests at all), `name-value-list`
  custom `separator`/`assignator`/`ignore_whitespaces` and quoted values,
  `checkpoint-lea` quoting/`terminator`, `number`/`hexnumber` `maxval`,
  `string` `option.dashIsEmpty` positive case and `matching.mode:"lazy"`,
  and `op-quoted-string`'s `escape` option.

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
