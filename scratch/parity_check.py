#!/usr/bin/env python3
"""
Parity harness for the C lognormalizer and the C# port.

The harness parses add_rule/execute pairs from the C project's tests/*.sh
fixtures. The shell files are treated as a loose DSL and are never executed.
Each runnable case is replayed against both implementations, and their JSON
outputs are compared semantically.

This is a verification utility, not part of the shipped port.
"""

from __future__ import annotations

import argparse
import difflib
import glob
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
from contextlib import contextmanager
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterator, Sequence

REPO_ROOT = Path(__file__).resolve().parents[1]

DEFAULT_TESTS_DIR = Path(
    os.environ.get("PARITY_TESTS_DIR", str(REPO_ROOT / "tests"))
)
DEFAULT_C_BIN = os.environ.get(
    "PARITY_C_BIN",
    shutil.which("lognormalizer")
    or str(REPO_ROOT.parent / "src" / "lognormalizer"),
)
DEFAULT_CSHARP_CLI_DLL = Path(
    os.environ.get(
        "PARITY_CSHARP_CLI_DLL",
        str(
            REPO_ROOT
            / "src"
            / "tools"
            / "LogNormalizer.Cli"
            / "bin"
            / "Debug"
            / "net10.0"
            / "lognormalizer.dll"
        ),
    )
)

SKIP_FILES = {"exec.sh", "options.sh"}
SKIP_SUBSTRINGS = ("_v1.sh", "turbo", "err_callback", "very_long_logline")

# DOTALL permits quoted bodies to span physical lines. MULTILINE keeps ^ and $
# anchored to individual fixture lines around each call.
CALL_RE = re.compile(
    r"""^[ \t]*(?:(reset_rules)\b[ \t]*(?:\#[^\n]*)?$|(add_rule|execute|execute_with_string)\s+(['"])(.*?)\3[ \t]*(?:\#[^\n]*)?$)""",
    re.MULTILINE | re.DOTALL,
)


class SetupError(RuntimeError):
    """Raised when a required path, executable, or runtime is unavailable."""


class ExecutionError(RuntimeError):
    """Raised when an implementation cannot execute a parity case."""

    def __init__(
        self,
        implementation: str,
        command: Sequence[str],
        reason: str,
        *,
        returncode: int | None = None,
        stdout: str = "",
        stderr: str = "",
    ) -> None:
        super().__init__(reason)
        self.implementation = implementation
        self.command = tuple(command)
        self.reason = reason
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr

    def __str__(self) -> str:
        parts = [
            f"{self.implementation}: {self.reason}",
            f"command: {shlex.join(self.command)}",
        ]
        if self.returncode is not None:
            parts.append(f"exit code: {self.returncode}")
        if self.stdout.strip():
            parts.append(f"stdout: {self.stdout.strip()!r}")
        if self.stderr.strip():
            parts.append(f"stderr: {self.stderr.strip()!r}")
        return "; ".join(parts)


@dataclass(frozen=True)
class Configuration:
    tests_dir: Path
    c_bin: Path
    c_libdir: Path | None
    dotnet: Path
    csharp_cli_dll: Path
    timeout_seconds: float
    keep_temp: bool
    quiet: bool
    verbose: bool
    fail_fast: bool
    max_details: int


@dataclass(frozen=True)
class TestCase:
    fixture: Path
    fixture_case_number: int
    rulebase_text: str
    message: str

    @property
    def label(self) -> str:
        return f"{self.fixture.name}#{self.fixture_case_number}"


@dataclass
class DiscoveryStats:
    fixture_files_found: int = 0
    fixture_files_selected: int = 0
    skipped_by_name: int = 0
    skipped_without_rules: int = 0
    skipped_execute_with_string: int = 0
    skipped_without_rulebase: int = 0
    skipped_without_version: int = 0


@dataclass(frozen=True)
class ProcessOutput:
    stdout: str
    stderr: str
    duration_seconds: float


@dataclass(frozen=True)
class JsonParseFailure:
    raw: str
    error: str


@dataclass(frozen=True)
class CaseFailure:
    case: TestCase
    c_output: str = ""
    csharp_output: str = ""
    csharp_stderr: str = ""
    c_json: Any = None
    csharp_json: Any = None
    c_parse_error: JsonParseFailure | None = None
    csharp_parse_error: JsonParseFailure | None = None
    execution_errors: tuple[ExecutionError, ...] = field(default_factory=tuple)


@dataclass
class RunSummary:
    passed: int = 0
    mismatches: list[CaseFailure] = field(default_factory=list)
    parse_errors: list[CaseFailure] = field(default_factory=list)
    execution_errors: list[CaseFailure] = field(default_factory=list)
    elapsed_seconds: float = 0.0

    @property
    def failed(self) -> int:
        return len(self.mismatches) + len(self.parse_errors) + len(self.execution_errors)


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Replay C lognormalizer shell fixtures against the C reference "
            "binary and the C# CLI, then compare their JSON output."
        )
    )
    parser.add_argument(
        "--tests-dir",
        type=Path,
        default=DEFAULT_TESTS_DIR,
        help="Fixture directory containing *.sh files (env: PARITY_TESTS_DIR).",
    )
    parser.add_argument(
        "--c-bin",
        default=DEFAULT_C_BIN,
        help="C lognormalizer executable or command name (env: PARITY_C_BIN).",
    )
    parser.add_argument(
        "--c-libdir",
        type=Path,
        default=None,
        help=(
            "Directory prepended to LD_LIBRARY_PATH. Defaults to PARITY_C_LIBDIR "
            "or <resolved C binary directory>/lib."
        ),
    )
    parser.add_argument(
        "--csharp-cli-dll",
        type=Path,
        default=DEFAULT_CSHARP_CLI_DLL,
        help="C# CLI DLL path (env: PARITY_CSHARP_CLI_DLL).",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=10.0,
        help="Per-implementation timeout in seconds (default: 10).",
    )
    parser.add_argument(
        "--max-details",
        type=int,
        default=80,
        help="Maximum detailed records printed per failure category (default: 80).",
    )
    parser.add_argument(
        "--keep-temp",
        action="store_true",
        help="Keep generated rulebase files and print their paths.",
    )
    parser.add_argument(
        "--fail-fast",
        action="store_true",
        help="Stop after the first mismatch, parse failure, or execution error.",
    )
    output_group = parser.add_mutually_exclusive_group()
    output_group.add_argument(
        "-v",
        "--verbose",
        action="store_true",
        help="Print discovery details and successful command stderr.",
    )
    output_group.add_argument(
        "-q",
        "--quiet",
        action="store_true",
        help="Suppress per-case progress; the final report is still printed.",
    )
    return parser.parse_args(argv)


def resolve_executable(value: str, description: str) -> Path:
    candidate = Path(value).expanduser()
    contains_separator = os.sep in value or (os.altsep is not None and os.altsep in value)

    if candidate.is_absolute() or contains_separator:
        resolved = candidate.resolve()
        if not resolved.is_file():
            raise SetupError(f"{description} does not exist or is not a file: {resolved}")
    else:
        located = shutil.which(value)
        if located is None:
            raise SetupError(
                f"{description} was not found on PATH: {value!r}. "
                f"Set the corresponding command-line option or environment variable."
            )
        resolved = Path(located).resolve()

    if not os.access(resolved, os.X_OK):
        raise SetupError(f"{description} is not executable: {resolved}")
    return resolved


def validate_configuration(args: argparse.Namespace) -> Configuration:
    if args.timeout <= 0:
        raise SetupError("--timeout must be greater than zero")
    if args.max_details < 0:
        raise SetupError("--max-details cannot be negative")

    tests_dir = args.tests_dir.expanduser().resolve()
    if not tests_dir.is_dir():
        raise SetupError(f"fixture directory does not exist: {tests_dir}")

    c_bin = resolve_executable(args.c_bin, "C lognormalizer binary")
    dotnet = resolve_executable("dotnet", ".NET host")

    csharp_cli_dll = args.csharp_cli_dll.expanduser().resolve()
    if not csharp_cli_dll.is_file():
        raise SetupError(
            f"C# CLI DLL does not exist: {csharp_cli_dll}. "
            "Build LogNormalizer.Cli first or set --csharp-cli-dll."
        )

    configured_libdir = args.c_libdir
    if configured_libdir is None:
        env_libdir = os.environ.get("PARITY_C_LIBDIR")
        configured_libdir = Path(env_libdir) if env_libdir else c_bin.parent / "lib"

    c_libdir = configured_libdir.expanduser().resolve()
    if not c_libdir.exists():
        # A system-installed binary may not need a private library directory. Treat
        # the default derived path as optional, but reject an explicitly configured
        # missing path because it is almost certainly a configuration error.
        explicitly_configured = args.c_libdir is not None or "PARITY_C_LIBDIR" in os.environ
        if explicitly_configured:
            raise SetupError(f"configured C library directory does not exist: {c_libdir}")
        c_libdir = None
    elif not c_libdir.is_dir():
        raise SetupError(f"C library path is not a directory: {c_libdir}")

    return Configuration(
        tests_dir=tests_dir,
        c_bin=c_bin,
        c_libdir=c_libdir,
        dotnet=dotnet,
        csharp_cli_dll=csharp_cli_dll,
        timeout_seconds=args.timeout,
        keep_temp=args.keep_temp,
        quiet=args.quiet,
        verbose=args.verbose,
        fail_fast=args.fail_fast,
        max_details=args.max_details,
    )


def verify_dotnet(config: Configuration) -> str:
    command = [str(config.dotnet), "--version"]
    try:
        completed = subprocess.run(
            command,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=config.timeout_seconds,
            check=False,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise SetupError(f"failed to invoke .NET host: {exc}") from exc

    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip() or "no diagnostics"
        raise SetupError(f"dotnet --version failed with exit code {completed.returncode}: {detail}")
    return completed.stdout.strip()


def extract_calls(path: Path) -> list[tuple[str, str | None]]:
    """Return recognized fixture calls in source order."""
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        raise SetupError(f"cannot read fixture {path}: {exc}") from exc

    calls: list[tuple[str, str | None]] = []
    for match in CALL_RE.finditer(text):
        if match.group(1):
            calls.append(("reset", None))
        else:
            calls.append((match.group(2), match.group(4)))
    return calls


def discover_cases(
    tests_dir: Path, *, verbose: bool = False
) -> tuple[list[TestCase], DiscoveryStats]:
    fixture_paths = [Path(path) for path in sorted(glob.glob(str(tests_dir / "*.sh")))]
    stats = DiscoveryStats(fixture_files_found=len(fixture_paths))
    cases: list[TestCase] = []

    for path in fixture_paths:
        base = path.name
        if base in SKIP_FILES or any(value in base for value in SKIP_SUBSTRINGS):
            stats.skipped_by_name += 1
            if verbose:
                print(f"[DISCOVERY] skip fixture by policy: {base}", file=sys.stderr)
            continue

        calls = extract_calls(path)
        if not any(function == "add_rule" for function, _ in calls):
            stats.skipped_without_rules += 1
            if verbose:
                print(f"[DISCOVERY] skip fixture without add_rule: {base}", file=sys.stderr)
            continue

        stats.fixture_files_selected += 1
        rulebase_lines: list[str] = []
        fixture_case_number = 0

        for function, body in calls:
            if function == "reset":
                rulebase_lines.clear()
                continue

            if function == "add_rule":
                assert body is not None
                rulebase_lines.append(body)
                continue

            if function == "execute_with_string":
                stats.skipped_execute_with_string += 1
                continue

            if function != "execute":
                continue

            assert body is not None
            if not rulebase_lines:
                stats.skipped_without_rulebase += 1
                continue
            if not any(line.lstrip().startswith("version=") for line in rulebase_lines):
                stats.skipped_without_version += 1
                continue

            fixture_case_number += 1
            cases.append(
                TestCase(
                    fixture=path,
                    fixture_case_number=fixture_case_number,
                    rulebase_text="\n".join(rulebase_lines) + "\n",
                    message=body,
                )
            )

    return cases, stats


@contextmanager
def temporary_rulebase(text: str, *, keep: bool) -> Iterator[Path]:
    path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            suffix=".rb",
            prefix="lognormalizer-parity-",
            delete=False,
        ) as handle:
            handle.write(text)
            handle.flush()
            path = Path(handle.name)
        yield path
    finally:
        if path is None:
            return
        if keep:
            print(f"[TEMP] kept rulebase: {path}", file=sys.stderr)
        else:
            try:
                path.unlink(missing_ok=True)
            except OSError as exc:
                print(f"[WARN] failed to remove temporary rulebase {path}: {exc}", file=sys.stderr)


def run_process(
    implementation: str,
    command: Sequence[str],
    *,
    message: str,
    cwd: Path,
    env: dict[str, str] | None,
    timeout_seconds: float,
) -> ProcessOutput:
    started = time.monotonic()
    try:
        completed = subprocess.run(
            list(command),
            input=message + "\n",
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=env,
            timeout=timeout_seconds,
            cwd=cwd,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        stdout = exc.stdout if isinstance(exc.stdout, str) else ""
        stderr = exc.stderr if isinstance(exc.stderr, str) else ""
        raise ExecutionError(
            implementation,
            command,
            f"timed out after {timeout_seconds:g} seconds",
            stdout=stdout,
            stderr=stderr,
        ) from exc
    except FileNotFoundError as exc:
        raise ExecutionError(
            implementation,
            command,
            f"executable was not found: {exc.filename}",
        ) from exc
    except OSError as exc:
        raise ExecutionError(implementation, command, f"failed to start process: {exc}") from exc

    duration = time.monotonic() - started
    if completed.returncode != 0:
        raise ExecutionError(
            implementation,
            command,
            "process returned a non-zero exit code",
            returncode=completed.returncode,
            stdout=completed.stdout,
            stderr=completed.stderr,
        )

    return ProcessOutput(
        stdout=completed.stdout.strip(),
        stderr=completed.stderr.strip(),
        duration_seconds=duration,
    )


def run_c(config: Configuration, rulebase_path: Path, message: str) -> ProcessOutput:
    env = dict(os.environ)
    if config.c_libdir is not None:
        existing = env.get("LD_LIBRARY_PATH", "")
        entries = [str(config.c_libdir)]
        if existing:
            entries.append(existing)
        env["LD_LIBRARY_PATH"] = os.pathsep.join(entries)

    return run_process(
        "C reference",
        [str(config.c_bin), "-r", str(rulebase_path), "-e", "json"],
        message=message,
        cwd=config.tests_dir,
        env=env,
        timeout_seconds=config.timeout_seconds,
    )


def run_csharp(config: Configuration, rulebase_path: Path, message: str) -> ProcessOutput:
    return run_process(
        "C# port",
        [str(config.dotnet), str(config.csharp_cli_dll), "-r", str(rulebase_path)],
        message=message,
        cwd=config.tests_dir,
        env=None,
        timeout_seconds=config.timeout_seconds,
    )


def parse_json(raw: str) -> tuple[Any | None, JsonParseFailure | None]:
    if not raw:
        return None, JsonParseFailure(raw=raw, error="process produced empty stdout")
    try:
        return json.loads(raw), None
    except json.JSONDecodeError as exc:
        return None, JsonParseFailure(
            raw=raw,
            error=f"{exc.msg} at line {exc.lineno}, column {exc.colno}",
        )


def preview_message(message: str, limit: int = 90) -> str:
    rendered = repr(message)
    if len(rendered) <= limit:
        return rendered
    return rendered[: limit - 3] + "..."


def print_configuration(config: Configuration, dotnet_version: str) -> None:
    print("Parity harness configuration", file=sys.stderr)
    print(f"  fixtures:     {config.tests_dir}", file=sys.stderr)
    print(f"  C binary:     {config.c_bin}", file=sys.stderr)
    print(f"  C library:    {config.c_libdir or '<system loader paths>'}", file=sys.stderr)
    print(f"  dotnet:       {config.dotnet} ({dotnet_version})", file=sys.stderr)
    print(f"  C# CLI:       {config.csharp_cli_dll}", file=sys.stderr)
    print(f"  timeout:      {config.timeout_seconds:g}s per process", file=sys.stderr)
    print(file=sys.stderr)


def print_discovery(stats: DiscoveryStats, case_count: int) -> None:
    print("Fixture discovery", file=sys.stderr)
    print(f"  fixture files found:          {stats.fixture_files_found}", file=sys.stderr)
    print(f"  fixture files selected:       {stats.fixture_files_selected}", file=sys.stderr)
    print(f"  skipped by filename policy:   {stats.skipped_by_name}", file=sys.stderr)
    print(f"  skipped without add_rule:     {stats.skipped_without_rules}", file=sys.stderr)
    print(f"  skipped execute_with_string:  {stats.skipped_execute_with_string}", file=sys.stderr)
    print(f"  skipped without rulebase:     {stats.skipped_without_rulebase}", file=sys.stderr)
    print(f"  skipped without version:      {stats.skipped_without_version}", file=sys.stderr)
    print(f"  runnable execute cases:       {case_count}", file=sys.stderr)
    print(file=sys.stderr)


def execute_cases(config: Configuration, cases: Sequence[TestCase]) -> RunSummary:
    summary = RunSummary()
    started = time.monotonic()
    total = len(cases)

    for index, case in enumerate(cases, start=1):
        case_started = time.monotonic()
        c_result: ProcessOutput | None = None
        csharp_result: ProcessOutput | None = None
        errors: list[ExecutionError] = []

        with temporary_rulebase(case.rulebase_text, keep=config.keep_temp) as rulebase_path:
            try:
                c_result = run_c(config, rulebase_path, case.message)
            except ExecutionError as exc:
                errors.append(exc)

            try:
                csharp_result = run_csharp(config, rulebase_path, case.message)
            except ExecutionError as exc:
                errors.append(exc)

        status: str
        if errors:
            summary.execution_errors.append(
                CaseFailure(
                    case=case,
                    c_output=c_result.stdout if c_result else "",
                    csharp_output=csharp_result.stdout if csharp_result else "",
                    csharp_stderr=csharp_result.stderr if csharp_result else "",
                    execution_errors=tuple(errors),
                )
            )
            status = "ERROR"
        else:
            assert c_result is not None and csharp_result is not None
            c_json, c_parse_error = parse_json(c_result.stdout)
            csharp_json, csharp_parse_error = parse_json(csharp_result.stdout)

            if c_parse_error is not None or csharp_parse_error is not None:
                summary.parse_errors.append(
                    CaseFailure(
                        case=case,
                        c_output=c_result.stdout,
                        csharp_output=csharp_result.stdout,
                        csharp_stderr=csharp_result.stderr,
                        c_parse_error=c_parse_error,
                        csharp_parse_error=csharp_parse_error,
                    )
                )
                status = "UNPARSEABLE"
            elif c_json != csharp_json:
                summary.mismatches.append(
                    CaseFailure(
                        case=case,
                        c_output=c_result.stdout,
                        csharp_output=csharp_result.stdout,
                        csharp_stderr=csharp_result.stderr,
                        c_json=c_json,
                        csharp_json=csharp_json,
                    )
                )
                status = "MISMATCH"
            else:
                summary.passed += 1
                status = "PASS"

            if config.verbose and csharp_result.stderr:
                print(
                    f"[STDERR] {case.label} C#: {csharp_result.stderr}",
                    file=sys.stderr,
                )

        case_duration = time.monotonic() - case_started
        if not config.quiet:
            print(
                f"[{index:>{len(str(total))}}/{total}] {status:<11} "
                f"{case.label:<36} {case_duration:7.3f}s  {preview_message(case.message)}",
                file=sys.stderr,
            )

        if config.fail_fast and status != "PASS":
            print("[INFO] fail-fast requested; stopping after first failure", file=sys.stderr)
            break

    summary.elapsed_seconds = time.monotonic() - started
    return summary


def pretty_json(value: Any) -> str:
    return json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False)


def semantic_diff(c_json: Any, csharp_json: Any) -> str:
    c_lines = pretty_json(c_json).splitlines()
    csharp_lines = pretty_json(csharp_json).splitlines()
    return "\n".join(
        difflib.unified_diff(
            c_lines,
            csharp_lines,
            fromfile="C reference",
            tofile="C# port",
            lineterm="",
        )
    )


def print_failure_header(kind: str, failure: CaseFailure) -> None:
    print(f"[{kind}] {failure.case.label}")
    print(f"  fixture: {failure.case.fixture}")
    print(f"  message: {failure.case.message!r}")


def print_detailed_failures(summary: RunSummary, max_details: int) -> None:
    print()

    for failure in summary.execution_errors[:max_details]:
        print_failure_header("ERROR", failure)
        for error in failure.execution_errors:
            print(f"  {error}")
        print()

    for failure in summary.parse_errors[:max_details]:
        print_failure_header("UNPARSEABLE", failure)
        if failure.c_parse_error is not None:
            print(f"  C parse error:  {failure.c_parse_error.error}")
            print(f"  C stdout:       {failure.c_parse_error.raw!r}")
        else:
            print(f"  C stdout:       {failure.c_output!r}")
        if failure.csharp_parse_error is not None:
            print(f"  C# parse error: {failure.csharp_parse_error.error}")
            print(f"  C# stdout:      {failure.csharp_parse_error.raw!r}")
        else:
            print(f"  C# stdout:      {failure.csharp_output!r}")
        if failure.csharp_stderr:
            print(f"  C# stderr:      {failure.csharp_stderr!r}")
        print()

    for failure in summary.mismatches[:max_details]:
        print_failure_header("MISMATCH", failure)
        print("  semantic diff:")
        for line in semantic_diff(failure.c_json, failure.csharp_json).splitlines():
            print(f"    {line}")
        if failure.csharp_stderr:
            print(f"  C# stderr: {failure.csharp_stderr!r}")
        print()

    categories = (
        ("execution errors", len(summary.execution_errors)),
        ("unparseable outputs", len(summary.parse_errors)),
        ("mismatches", len(summary.mismatches)),
    )
    for label, count in categories:
        omitted = count - max_details
        if omitted > 0:
            print(f"... {omitted} additional {label} omitted")


def print_summary(summary: RunSummary, discovered_case_count: int) -> None:
    executed = summary.passed + summary.failed
    print()
    print("Parity summary")
    print(f"  discovered cases:     {discovered_case_count}")
    print(f"  executed cases:       {executed}")
    print(f"  passed:               {summary.passed}")
    print(f"  mismatches:           {len(summary.mismatches)}")
    print(f"  unparseable outputs:  {len(summary.parse_errors)}")
    print(f"  execution errors:     {len(summary.execution_errors)}")
    print(f"  elapsed:              {summary.elapsed_seconds:.3f}s")


def main(argv: Sequence[str] | None = None) -> int:
    try:
        args = parse_args(argv)
        config = validate_configuration(args)
        dotnet_version = verify_dotnet(config)
        cases, discovery_stats = discover_cases(
            config.tests_dir,
            verbose=config.verbose,
        )

        if not cases:
            raise SetupError(
                "no runnable execute cases were discovered; check the fixture directory "
                "and the parser/skip rules"
            )

        if not config.quiet:
            print_configuration(config, dotnet_version)
            print_discovery(discovery_stats, len(cases))

        summary = execute_cases(config, cases)
        print_summary(summary, len(cases))
        print_detailed_failures(summary, config.max_details)
        return 1 if summary.failed else 0

    except SetupError as exc:
        print(f"[FATAL] {exc}", file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print("\n[INTERRUPTED] parity run cancelled by user", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())