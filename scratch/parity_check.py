#!/usr/bin/env python3
"""
Parity harness: extracts add_rule/execute pairs from the C project's
tests/*.sh shell fixtures (a loose bash DSL, not executed as bash) and
replays each one against both the C reference `lognormalizer` binary and
the C# port's CLI, diffing the JSON output semantically.

This is a throwaway verification script, not part of the shipped port.
"""
import glob
import json
import os
import re
import subprocess
import tempfile

TESTS_DIR = "/home/user/DeltaZulu.Normalize/tests"
C_BIN = "/home/user/DeltaZulu.Normalize/src/lognormalizer"
C_LIBDIR = "/home/user/DeltaZulu.Normalize/src/.libs"
CSHARP_CLI_DLL = "/home/user/DeltaZulu.Normalize/csharp/tools/LogNormalizer.Cli/bin/Debug/net10.0/lognormalizer.dll"

SKIP_FILES = {"exec.sh", "options.sh"}
# constructs / features intentionally out of scope for this port or the harness
SKIP_SUBSTR = ["_v1.sh", "turbo", "err_callback", "very_long_logline"]

# DOTALL so a quoted body can span multiple physical lines (e.g. multi-line
# "alternative"/"repeat" rule definitions written across several lines in the
# .sh fixture); MULTILINE so ^/$ still anchor per line for the leading
# whitespace and trailing inline-comment parts of the match.
CALL_RE = re.compile(
    r"""^[ \t]*(?:(reset_rules)\b[ \t]*(?:\#[^\n]*)?$|(add_rule|execute|execute_with_string)\s+(['"])(.*?)\3[ \t]*(?:\#[^\n]*)?$)""",
    re.MULTILINE | re.DOTALL,
)


# A distinct sentinel for "output could not be parsed as JSON", so two
# unrelated parse failures never compare equal to each other or to a literal
# JSON null.
class _ParseError:
    def __init__(self, raw):
        self.raw = raw

    def __eq__(self, other):
        return False

    def __repr__(self):
        return f"<unparseable: {self.raw!r}>"


def extract_calls(path):
    """Yield ('add_rule'|'execute'|'reset', text) in file order, best-effort."""
    with open(path, "r", errors="replace") as f:
        text = f.read()
    calls = []
    for m in CALL_RE.finditer(text):
        if m.group(1):
            calls.append(("reset", None))
        else:
            calls.append((m.group(2), m.group(4)))
    return calls


def run_c(rulebase_text, message) -> str:
    with tempfile.NamedTemporaryFile("w", suffix=".rb", delete=False) as f:
        f.write(rulebase_text)
        rb_path = f.name
    try:
        env = dict(os.environ)
        env["LD_LIBRARY_PATH"] = C_LIBDIR
        p = subprocess.run(
            [C_BIN, "-r", rb_path, "-e", "json"],
            input=message + "\n", capture_output=True, text=True, env=env, timeout=10,
        )
        if p.returncode != 0:
            raise RuntimeError(f"C binary exited {p.returncode}: {p.stderr.strip()}")
        return p.stdout.strip()
    finally:
        os.unlink(rb_path)


def run_csharp(rulebase_text, message):
    with tempfile.NamedTemporaryFile("w", suffix=".rb", delete=False) as f:
        f.write(rulebase_text)
        rb_path = f.name
    try:
        p = subprocess.run(
            ["dotnet", CSHARP_CLI_DLL, "-r", rb_path],
            input=message + "\n", capture_output=True, text=True, timeout=10,
        )
        if p.returncode != 0:
            raise RuntimeError(f"C# CLI exited {p.returncode}: {p.stderr.strip()}")
        return p.stdout.strip(), p.stderr.strip()
    finally:
        os.unlink(rb_path)


def normalize_json(s):
    try:
        return json.loads(s)
    except Exception:
        return _ParseError(s)


def main() -> None:
    files = sorted(glob.glob(os.path.join(TESTS_DIR, "*.sh")))
    total = 0
    mismatches = []
    parse_errors = []
    crashes = []

    for path in files:
        base = os.path.basename(path)
        if base in SKIP_FILES or any(s in base for s in SKIP_SUBSTR):
            continue
        calls = extract_calls(path)
        if not any(fn == "add_rule" for fn, _ in calls):
            continue

        rulebase_lines = []
        for fn, body in calls:
            if fn == "reset":
                rulebase_lines = []
            elif fn == "add_rule":
                rulebase_lines.append(body)
            elif fn in ("execute", "execute_with_string"):
                if fn == "execute_with_string":
                    # first add_rule is the rulebase string itself; skip (rare, complex)
                    continue
                rb_text = "\n".join(rulebase_lines) + "\n"
                if not rulebase_lines or not any(line.startswith("version=") for line in rulebase_lines):
                    continue
                message = body
                total += 1
                try:
                    c_out = run_c(rb_text, message)
                except Exception as e:
                    crashes.append((base, message, f"C binary error: {e}"))
                    continue
                try:
                    cs_out, cs_err = run_csharp(rb_text, message)
                except Exception as e:
                    crashes.append((base, message, f"C# binary error: {e}"))
                    continue

                c_json = normalize_json(c_out)
                cs_json = normalize_json(cs_out)
                if isinstance(c_json, _ParseError) or isinstance(cs_json, _ParseError):
                    parse_errors.append((base, message, c_out, cs_out))
                elif c_json != cs_json:
                    mismatches.append((base, message, c_out, cs_out, cs_err))

    print(f"Total execute() cases replayed: {total}")
    print(f"Mismatches: {len(mismatches)}")
    print(f"Unparseable outputs: {len(parse_errors)}")
    print(f"Crashes/errors: {len(crashes)}")
    print()
    for base, msg, err in crashes[:30]:
        print(f"[CRASH] {base}: {msg!r}\n  {err}")
    print()
    for base, msg, c_out, cs_out in parse_errors[:30]:
        print(f"[UNPARSEABLE] {base}: {msg!r}\n  C:  {c_out!r}\n  CS: {cs_out!r}")
    print()
    for base, msg, c_out, cs_out, cs_err in mismatches[:80]:
        print(f"[MISMATCH] {base}")
        print(f"  msg: {msg!r}")
        print(f"  C:  {c_out}")
        print(f"  CS: {cs_out}")
        if cs_err:
            print(f"  CS stderr: {cs_err}")
        print()

    if len(mismatches) > 80:
        print(f"... and {len(mismatches) - 80} more mismatches")


if __name__ == "__main__":
    main()
