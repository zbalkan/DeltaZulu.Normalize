#!/usr/bin/env python3
"""
Generate a large mixed log fixture and run an end-to-end CLI benchmark with hyperfine.

The fixture intentionally combines semi-structured JSON/CEF/key-value events with
unstructured text and unmatched noise so the benchmark measures rulebase loading,
PDAG walking, structured parser costs, output serialization, and realistic miss
paths over enough input to amortize process startup.
"""

from __future__ import annotations

import argparse
import os
import random
import shlex
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Sequence

REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CSHARP_CLI_DLL = REPO_ROOT / "src" / "tools" / "LogNormalizer.Cli" / "bin" / "Release" / "net10.0" / "lognormalizer.dll"
DEFAULT_C_BIN = os.environ.get("HYPERFINE_C_BIN", shutil.which("lognormalizer") or "")
DEFAULT_LINE_COUNT = 200_000

RULEBASE_TEXT = r'''version=2
rule=:svc=%svc:word% level=%level:word% trace=%trace:word% user=%user:word% action=%action:word% duration_ms=%duration:number% attrs=%attrs:json%
rule=:CEF:0|DeltaZulu|Fixture|%version:number%|%sig:number%|%name:char-to:\x7c%|%severity:number%|%extensions:cef%
rule=:%month:word% %day:number% %time:time-24hr% %host:word% %app:char-to:\x5b%[%pid:number%]: %message:rest%
rule=:audit user=%user:word% ip=%ip:ipv4% port=%port:number% status=%status:word% details=%details:name-value-list%
rule=:unstructured %category:word% %message:rest%
'''

SERVICES = ("auth", "billing", "edge", "scheduler", "search", "worker")
LEVELS = ("INFO", "WARN", "ERROR", "DEBUG")
USERS = ("ada", "grace", "linus", "margaret", "alan", "katherine")
ACTIONS = ("login", "logout", "refresh", "charge", "query", "enqueue")
HOSTS = ("web-01", "web-02", "api-03", "db-01", "worker-07")
MONTHS = ("Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec")
WORDS = ("timeout", "accepted", "rejected", "started", "finished", "retrying", "cache", "upstream", "token", "payload")


@dataclass(frozen=True)
class FixturePaths:
    directory: Path
    rulebase: Path
    messages: Path


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run hyperfine against large generated lognormalizer input.")
    parser.add_argument("--line-count", type=int, default=DEFAULT_LINE_COUNT, help=f"messages to generate (default: {DEFAULT_LINE_COUNT})")
    parser.add_argument("--seed", type=int, default=8675309, help="deterministic RNG seed for fixture generation")
    parser.add_argument("--fixture-dir", type=Path, default=None, help="reuse or create fixtures in this directory instead of a temp dir")
    parser.add_argument("--keep-fixture", action="store_true", help="keep a temporary generated fixture and print its location")
    parser.add_argument("--csharp-cli-dll", type=Path, default=DEFAULT_CSHARP_CLI_DLL, help="Release C# CLI DLL to benchmark")
    parser.add_argument("--c-bin", default=DEFAULT_C_BIN, help="optional C lognormalizer binary to benchmark too (env: HYPERFINE_C_BIN)")
    parser.add_argument("--runs", type=int, default=10, help="hyperfine measurement runs per command")
    parser.add_argument("--warmup", type=int, default=3, help="hyperfine warmup runs per command")
    parser.add_argument("--export-json", type=Path, default=None, help="optional hyperfine JSON output path")
    parser.add_argument("--build", action="store_true", help="build the C# CLI in Release before benchmarking")
    parser.add_argument("--generate-only", action="store_true", help="write the fixture files and skip invoking hyperfine")
    return parser.parse_args(argv)


def require_executable(name: str) -> str:
    resolved = shutil.which(name)
    if resolved is None:
        raise SystemExit(f"error: required executable not found on PATH: {name}")
    return resolved


def generate_line(index: int, rng: random.Random) -> str:
    kind = index % 5
    if kind == 0:
        attrs = '{{"region":"us-{zone}","attempt":{attempt},"flags":["{flag}","bulk"],"bytes":{bytes}}}'.format(
            zone=rng.randint(1, 4), attempt=rng.randint(1, 5), flag=rng.choice(("hot", "cold", "canary")), bytes=rng.randint(128, 900_000)
        )
        return f"svc={rng.choice(SERVICES)} level={rng.choice(LEVELS)} trace={index:012x} user={rng.choice(USERS)} action={rng.choice(ACTIONS)} duration_ms={rng.randint(1, 25000)} attrs={attrs}"
    if kind == 1:
        return f"CEF:0|DeltaZulu|Fixture|1|{1000 + index % 400}|{rng.choice(ACTIONS)} event|{rng.randint(1, 10)}|src=10.{rng.randint(0,255)}.{rng.randint(0,255)}.{rng.randint(1,254)} spt={rng.randint(1,65535)} suser={rng.choice(USERS)} request=/api/{rng.choice(ACTIONS)}/{index} msg={rng.choice(WORDS)}"
    if kind == 2:
        return f"{rng.choice(MONTHS)} {rng.randint(1,28):02d} {rng.randint(0,23):02d}:{rng.randint(0,59):02d}:{rng.randint(0,59):02d} {rng.choice(HOSTS)} {rng.choice(SERVICES)}[{100 + index % 9000}]: {' '.join(rng.choice(WORDS) for _ in range(16))} id={index}"
    if kind == 3:
        return f"audit user={rng.choice(USERS)} ip=192.168.{rng.randint(0,255)}.{rng.randint(1,254)} port={rng.randint(1,65535)} status={rng.choice(('allow','deny','challenge'))} details=method=POST path=/v1/{rng.choice(ACTIONS)} bytes={rng.randint(10,100000)} agent=fixture-{index % 17}"
    return f"noise line {index} {' '.join(rng.choice(WORDS) for _ in range(28))}"


def write_fixture(directory: Path, line_count: int, seed: int) -> FixturePaths:
    if line_count <= 0:
        raise SystemExit("error: --line-count must be greater than zero")
    directory.mkdir(parents=True, exist_ok=True)
    rulebase = directory / "large-mixed.rulebase"
    messages = directory / "large-mixed.log"
    rulebase.write_text(RULEBASE_TEXT, encoding="utf-8")
    rng = random.Random(seed)
    with messages.open("w", encoding="utf-8", newline="\n") as handle:
        for index in range(line_count):
            handle.write(generate_line(index, rng))
            handle.write("\n")
    return FixturePaths(directory=directory, rulebase=rulebase, messages=messages)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    hyperfine = None if args.generate_only else require_executable("hyperfine")
    dotnet = require_executable("dotnet")

    if args.build:
        subprocess.run([dotnet, "build", "-c", "Release", str(REPO_ROOT / "src" / "tools" / "LogNormalizer.Cli")], check=True)

    csharp_cli_dll = args.csharp_cli_dll.expanduser().resolve()
    if not csharp_cli_dll.is_file():
        raise SystemExit(f"error: C# CLI DLL not found: {csharp_cli_dll}. Run with --build or build Release first.")

    temp_dir: tempfile.TemporaryDirectory[str] | None = None
    if args.fixture_dir is None:
        temp_dir = tempfile.TemporaryDirectory(prefix="deltazulu-hyperfine-")
        fixture_dir = Path(temp_dir.name)
    else:
        fixture_dir = args.fixture_dir.expanduser().resolve()

    try:
        paths = write_fixture(fixture_dir, args.line_count, args.seed)
        if args.generate_only:
            print(f"Generated fixture directory: {paths.directory}")
            print(f"Rulebase: {paths.rulebase}")
            print(f"Messages: {paths.messages}")
            return 0

        assert hyperfine is not None
        commands = [f"{shlex.quote(dotnet)} {shlex.quote(str(csharp_cli_dll))} -r {shlex.quote(str(paths.rulebase))} < {shlex.quote(str(paths.messages))} > /dev/null"]
        if args.c_bin:
            c_bin = Path(args.c_bin).expanduser().resolve() if os.sep in args.c_bin else Path(require_executable(args.c_bin))
            commands.append(f"{shlex.quote(str(c_bin))} -r {shlex.quote(str(paths.rulebase))} < {shlex.quote(str(paths.messages))} > /dev/null")

        hyperfine_cmd = [hyperfine, "--warmup", str(args.warmup), "--runs", str(args.runs)]
        if args.export_json is not None:
            hyperfine_cmd.extend(["--export-json", str(args.export_json.expanduser())])
        hyperfine_cmd.extend(commands)
        print(f"Fixture: {paths.messages} ({args.line_count} lines)", file=sys.stderr)
        print(f"Rulebase: {paths.rulebase}", file=sys.stderr)
        return subprocess.run(hyperfine_cmd, check=False).returncode
    finally:
        if temp_dir is not None:
            if args.keep_fixture:
                print(f"Kept fixture directory: {fixture_dir}", file=sys.stderr)
            else:
                temp_dir.cleanup()


if __name__ == "__main__":
    raise SystemExit(main())
