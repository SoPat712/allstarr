#!/usr/bin/env python3
"""Run one test gate, preserve its result, and print a small timing report."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


@dataclass
class Counts:
    discovered: int = 0
    passed: int = 0
    failed: int = 0
    skipped: int = 0

    @property
    def executed(self) -> int:
        return self.passed + self.failed


@dataclass
class TestTiming:
    name: str
    milliseconds: float


DEFAULT_CHILD_TIMEOUT_SECONDS = 600.0
TIMEOUT_EXIT_CODE = 124


_ANSI_ESCAPE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")


def _strip_ansi(value: str) -> str:
    return _ANSI_ESCAPE.sub("", value)


def _version(command: str, cwd: Path | None = None) -> str:
    executable = command.split()[0]
    if shutil.which(executable) is None:
        return "unavailable"
    try:
        result = subprocess.run(
            command.split(), cwd=cwd, check=False, capture_output=True, text=True, timeout=10
        )
    except (OSError, subprocess.TimeoutExpired):
        return "unavailable"
    value = (result.stdout or result.stderr).strip().splitlines()
    return value[0] if value else "unavailable"


def _local_node_tool_version(tool: str, cwd: Path) -> str:
    if not (cwd / "node_modules" / ".bin" / tool).is_file():
        return "unavailable"
    return _version(f"npx --no-install {tool} --version", cwd)


def _git_sha(cwd: Path) -> str:
    try:
        result = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=cwd,
            check=False,
            capture_output=True,
            text=True,
            timeout=10,
        )
    except (OSError, subprocess.TimeoutExpired):
        return "unavailable"
    return result.stdout.strip() if result.returncode == 0 else "unavailable"


def _merge_counts(target: Counts, values: Counts) -> None:
    target.passed = max(target.passed, values.passed)
    target.failed = max(target.failed, values.failed)
    target.skipped = max(target.skipped, values.skipped)
    target.discovered = max(target.discovered, values.discovered)


def _parse_counts(output: str) -> Counts:
    output = _strip_ansi(output)
    counts = Counts()

    # dotnet test's console summary and common TRX console summaries.
    for pattern, order in (
        (
            r"Failed:\s*(\d+)\s*,\s*Passed:\s*(\d+)\s*,\s*Skipped:\s*(\d+)\s*,\s*Total:\s*(\d+)",
            (0, 1, 2, 3),
        ),
        (
            r"Tests\s+run:\s*(\d+)\s*,\s*Passed:\s*(\d+)\s*,\s*Failed:\s*(\d+)\s*,\s*Skipped:\s*(\d+)",
            (2, 1, 3, 0),
        ),
    ):
        match = re.search(pattern, output, re.IGNORECASE)
        if match:
            values = [int(value) for value in match.groups()]
            counts.failed, counts.passed, counts.skipped, counts.discovered = (
                values[index] for index in order
            )
            break

    # Vitest and Playwright summaries (the parenthesized value is discovered).
    for pattern in (
        r"(?:Tests|Test Files)\s+(?:(\d+)\s+failed,?\s*)?(?:(\d+)\s+passed,?\s*)?(?:(\d+)\s+skipped,?\s*)?(?:\((\d+)\))",
        r"(?:(\d+)\s+failed,?\s*)?(?:(\d+)\s+passed,?\s*)?(?:(\d+)\s+skipped,?\s*)?\((\d+)\s+(?:tests?|specs?)\)",
    ):
        for match in re.finditer(pattern, output, re.IGNORECASE):
            failed, passed, skipped, discovered = match.groups()
            parsed = Counts(
                discovered=int(discovered),
                passed=int(passed or 0),
                failed=int(failed or 0),
                skipped=int(skipped or 0),
            )
            _merge_counts(counts, parsed)

    # Playwright's default/line reporters omit the discovered total.
    summary = re.search(
        r"(?:(\d+)\s+failed,?\s*)?(?:(\d+)\s+passed,?\s*)?(?:(\d+)\s+skipped,?\s*)?\([^\n)]*\)",
        output,
        re.IGNORECASE,
    )
    if summary:
        failed, passed, skipped = summary.groups()
        parsed = Counts(
            passed=int(passed or 0), failed=int(failed or 0), skipped=int(skipped or 0)
        )
        parsed.discovered = parsed.passed + parsed.failed + parsed.skipped
        _merge_counts(counts, parsed)

    # Reporters may put each outcome on a separate line.
    for value, outcome in re.findall(r"(?m)^\s*(\d+)\s+(failed|passed|skipped)\b", output, re.IGNORECASE):
        parsed_value = int(value)
        if outcome.lower() == "passed":
            counts.passed = max(counts.passed, parsed_value)
        elif outcome.lower() == "failed":
            counts.failed = max(counts.failed, parsed_value)
        else:
            counts.skipped = max(counts.skipped, parsed_value)

    if counts.discovered == 0 and counts.passed + counts.failed + counts.skipped:
        counts.discovered = counts.passed + counts.failed + counts.skipped
    return counts


def _parse_retries(output: str) -> int:
    """Count retry attempts reported by Playwright or a compatible reporter."""
    output = _strip_ansi(output)
    attempts = len(re.findall(r"\bretry\s*#\s*\d+\b", output, re.IGNORECASE))
    for pattern in (
        r"\b(\d+)\s+(?:test\s+)?retries?\b",
        r"\bretries?\s*[:=]\s*(\d+)\b",
        r"\b(\d+)\s+flaky\b",
    ):
        values = [int(value) for value in re.findall(pattern, output, re.IGNORECASE)]
        if values:
            attempts = max(attempts, max(values))
    return attempts


def _required_test_failures(returncode: int, counts: Counts, retried: int) -> list[str]:
    if returncode != 0:
        return []
    failures = []
    if counts.discovered == 0:
        failures.append("zero tests discovered")
    if counts.failed:
        failures.append(f"{counts.failed} failed")
    if counts.skipped:
        failures.append(f"{counts.skipped} skipped")
    if retried:
        failures.append(f"{retried} retried")
    return failures


def _duration(value: str) -> float | None:
    value = value.strip()
    if value.endswith("ms"):
        return float(value[:-2])
    if value.endswith("s"):
        return float(value[:-1]) * 1000
    if value.startswith("PT"):
        seconds = re.fullmatch(r"PT(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?", value)
        if seconds:
            hours, minutes, seconds_value = (float(item or 0) for item in seconds.groups())
            return (hours * 3600 + minutes * 60 + seconds_value) * 1000
    timespan = re.fullmatch(r"(?:(\d+)\.)?(\d+):(\d{2}):(\d{2})(?:\.(\d+))?", value)
    if timespan:
        days, hours, minutes, seconds, fraction = timespan.groups()
        whole_ms = ((int(days or 0) * 24 + int(hours)) * 60 + int(minutes)) * 60 + int(seconds)
        return whole_ms * 1000 + (float(f"0.{fraction}") * 1000 if fraction else 0)
    return None


def _parse_trx(path: Path) -> tuple[Counts, list[TestTiming]]:
    counts = Counts()
    timings: list[TestTiming] = []
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError):
        return counts, timings
    for result in root.iter():
        if not result.tag.endswith("UnitTestResult"):
            continue
        outcome = result.attrib.get("outcome", "").lower()
        counts.discovered += 1
        if outcome == "passed":
            counts.passed += 1
        elif outcome == "skipped":
            counts.skipped += 1
        else:
            counts.failed += 1
        milliseconds = _duration(result.attrib.get("duration", ""))
        if milliseconds is not None:
            timings.append(TestTiming(result.attrib.get("testName", "unnamed"), milliseconds))
    return counts, timings


def _walk_playwright_json(
    value: object,
    timings: list[TestTiming],
    names: tuple[str, ...] = (),
    in_result: bool = False,
) -> None:
    """Collect Playwright result durations with their suite/spec title path."""
    if isinstance(value, dict):
        title = value.get("title")
        if not isinstance(title, str) or not title:
            title = value.get("file") if "specs" in value or "suites" in value else None
        child_names = names + (title,) if isinstance(title, str) and title else names

        duration = value.get("duration")
        if in_result and isinstance(duration, (int, float)) and not isinstance(duration, bool):
            timings.append(TestTiming(" > ".join(child_names) or "unnamed", float(duration)))
        elif "tests" in value and isinstance(duration, (int, float)) and not isinstance(duration, bool):
            # Some compatible reporters put the aggregate duration on the spec.
            timings.append(TestTiming(" > ".join(child_names) or "unnamed", float(duration)))

        for key, child in value.items():
            if key == "results":
                _walk_playwright_json(child, timings, child_names, True)
            elif key not in {"duration", "title", "file"}:
                _walk_playwright_json(child, timings, child_names, False)
    elif isinstance(value, list):
        for child in value:
            _walk_playwright_json(child, timings, names, in_result)


def _read_playwright_json(path: Path, timings: list[TestTiming]) -> None:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return
    _walk_playwright_json(value, timings)


def _parse_timings(
    output: str,
    trx_paths: Iterable[Path],
    playwright_json_paths: Iterable[Path] = (),
) -> list[TestTiming]:
    output = _strip_ansi(output)
    timings: list[TestTiming] = []
    for path in trx_paths:
        _, parsed = _parse_trx(path)
        timings.extend(parsed)
    # Verbose Vitest/Playwright reporters commonly end a test line with “12ms”.
    for match in re.finditer(r"(?:✓|✗|×|PASS|FAIL)\s+(.+?)\s+(\d+(?:\.\d+)?(?:ms|s))\s*$", output, re.MULTILINE):
        milliseconds = _duration(match.group(2))
        if milliseconds is not None:
            timings.append(TestTiming(match.group(1).strip(), milliseconds))
    paths = list(playwright_json_paths)
    paths.extend(
        Path(candidate)
        for candidate in re.findall(
            r"(?:PLAYWRIGHT_JSON_OUTPUT_FILE|playwright-report/[^\s]+\.json)=(\S+)", output
        )
    )
    seen: set[Path] = set()
    for path in paths:
        if path in seen:
            continue
        seen.add(path)
        _read_playwright_json(path, timings)
    return sorted(timings, key=lambda item: item.milliseconds, reverse=True)


def _repository_root(cwd: Path) -> Path:
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            cwd=cwd,
            check=False,
            capture_output=True,
            text=True,
            timeout=10,
        )
    except (OSError, subprocess.TimeoutExpired):
        return cwd
    return Path(result.stdout.strip()) if result.returncode == 0 else cwd


def _report(
    name: str,
    command: list[str],
    elapsed_ms: float,
    returncode: int,
    output: str,
    trx_paths: list[Path],
    cwd: Path,
    playwright_json_paths: Iterable[Path] = (),
    timeout_seconds: float | None = None,
) -> str:
    counts = _parse_counts(output)
    for path in trx_paths:
        parsed, _ = _parse_trx(path)
        _merge_counts(counts, parsed)
    timings = _parse_timings(output, trx_paths, playwright_json_paths)
    retried = _parse_retries(output)
    versions = {
        "python": _version("python3 --version", cwd),
        "node": _version("node --version", cwd),
        "npm": _version("npm --version", cwd),
        "dotnet": _version("dotnet --version", cwd),
        "playwright": _local_node_tool_version("playwright", cwd),
        "vitest": _local_node_tool_version("vitest", cwd),
    }
    lines = [
        f"TIMING name={name} duration_ms={elapsed_ms:.0f} exit_code={returncode}"
        + (" timed_out=1" if timeout_seconds is not None else ""),
        f"SHA {_git_sha(_repository_root(cwd))}",
        "TOOLS " + " ".join(f"{key}={value}" for key, value in versions.items()),
        "COUNTS "
        + " ".join(
            (
                f"discovered={counts.discovered}",
                f"executed={counts.executed}",
                f"passed={counts.passed}",
                f"failed={counts.failed}",
                f"skipped={counts.skipped}",
                f"retried={retried}",
            )
        ),
    ]
    if timeout_seconds is not None:
        lines.append(f"TIMEOUT name={name} limit_seconds={timeout_seconds:g} exit_code={returncode}")
    lines.extend(
        f"SLOWEST name={item.name} duration_ms={item.milliseconds:.0f}" for item in timings[:20]
    )
    if not timings:
        lines.append("SLOWEST unavailable")
    return "\n".join(lines) + "\n"


def _self_test() -> None:
    counts = _parse_counts("Tests run: 4, Passed: 3, Failed: 1, Skipped: 0, Total: 4")
    assert counts == Counts(discovered=4, passed=3, failed=1)
    counts = _parse_counts(
        "\x1b[2m Tests \x1b[22m \x1b[1m\x1b[32m39 passed\x1b[39m\x1b[22m "
        "\x1b[90m(39)\x1b[39m"
    )
    assert counts == Counts(discovered=39, passed=39)
    assert _parse_retries("34 passed (11.9s)") == 0
    assert _parse_retries("Retry #1\n1 flaky") == 1
    assert _parse_retries("Retries: 2") == 2
    assert _required_test_failures(0, Counts(discovered=1, passed=1), 0) == []
    assert _required_test_failures(0, Counts(), 0) == ["zero tests discovered"]
    assert _required_test_failures(0, Counts(discovered=2, passed=1, failed=1), 0) == ["1 failed"]
    assert _required_test_failures(0, Counts(discovered=2, passed=1, skipped=1), 0) == ["1 skipped"]
    assert _required_test_failures(0, Counts(discovered=1, passed=1), 1) == ["1 retried"]
    assert _required_test_failures(7, Counts(), 1) == []
    with tempfile.TemporaryDirectory() as directory:
        cwd = Path(directory)
        assert _local_node_tool_version("playwright", cwd) == "unavailable"
        assert _local_node_tool_version("vitest", cwd) == "unavailable"
    assert _duration("PT1.25S") == 1250
    assert _duration("12ms") == 12
    assert _duration("00:00:01.2500000") == 1250
    assert _duration("1.02:03:04.5") == 93784500
    assert DEFAULT_CHILD_TIMEOUT_SECONDS >= 600
    with tempfile.TemporaryDirectory() as directory:
        report_path = Path(directory) / "playwright.json"
        report_path.write_text(
            json.dumps(
                {
                    "suites": [
                        {
                            "title": "parity.e2e.ts",
                            "suites": [
                                {
                                    "title": "settings",
                                    "specs": [
                                        {
                                            "title": "loads",
                                            "tests": [{"results": [{"duration": 120}]}],
                                        }
                                    ],
                                }
                            ],
                            "specs": [
                                {
                                    "title": "home",
                                    "tests": [{"results": [{"duration": 40}, {"duration": 20}]}],
                                }
                            ],
                        }
                    ]
                }
            ),
            encoding="utf-8",
        )
        assert _parse_timings("", [], [report_path]) == [
            TestTiming("parity.e2e.ts > settings > loads", 120),
            TestTiming("parity.e2e.ts > home", 40),
            TestTiming("parity.e2e.ts > home", 20),
        ]
        timeout_report = _report(
            "fixture",
            ["fixture"],
            600,
            TIMEOUT_EXIT_CODE,
            "",
            [],
            cwd=Path(directory),
            timeout_seconds=DEFAULT_CHILD_TIMEOUT_SECONDS,
        )
        assert "timed_out=1" in timeout_report
        assert "TIMEOUT name=fixture limit_seconds=600 exit_code=124" in timeout_report


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv == ["--self-test"]:
        _self_test()
        return 0
    if "--" not in argv:
        raise SystemExit("usage: timing_report.py [options] -- command [args…]")
    separator = argv.index("--")
    parser = argparse.ArgumentParser()
    parser.add_argument("--name", required=True)
    parser.add_argument("--cwd", default=".")
    parser.add_argument("--trx", action="append", default=[])
    parser.add_argument("--require-tests", action="store_true")
    parser.add_argument(
        "--playwright-json",
        "--playwright-json-output",
        "--playwright-json-output-file",
        dest="playwright_json",
        metavar="PATH",
        help="write Playwright JSON results to PATH and include per-test timings",
    )
    parser.add_argument(
        "--timeout",
        "--timeout-seconds",
        "--child-timeout-seconds",
        dest="timeout_seconds",
        type=float,
        help="child watchdog in seconds (default: 600; env: TIMING_REPORT_TIMEOUT_SECONDS)",
    )
    options = parser.parse_args(argv[:separator])
    command = argv[separator + 1 :]
    if not command:
        raise SystemExit("timing_report.py: a child command is required")
    cwd = Path(options.cwd).resolve()
    trx_paths = [Path(path).resolve() if os.path.isabs(path) else cwd / path for path in options.trx]
    timeout_value = options.timeout_seconds
    if timeout_value is None:
        timeout_text = os.environ.get("TIMING_REPORT_TIMEOUT_SECONDS") or os.environ.get(
            "TIMING_REPORT_CHILD_TIMEOUT_SECONDS"
        )
        try:
            timeout_value = float(timeout_text) if timeout_text else DEFAULT_CHILD_TIMEOUT_SECONDS
        except ValueError as error:
            raise SystemExit("timing_report.py: timeout must be a positive number") from error
    if timeout_value <= 0:
        raise SystemExit("timing_report.py: timeout must be a positive number")

    playwright_json = options.playwright_json or os.environ.get("PLAYWRIGHT_JSON_OUTPUT_FILE")
    playwright_json_paths: list[Path] = []
    child_env = os.environ.copy()
    if playwright_json:
        child_env["PLAYWRIGHT_JSON_OUTPUT_FILE"] = playwright_json
        playwright_json_paths.append(
            Path(playwright_json).resolve() if os.path.isabs(playwright_json) else cwd / playwright_json
        )

    started = time.monotonic()
    timed_out = False
    try:
        result = subprocess.run(
            command,
            cwd=cwd,
            check=False,
            text=True,
            capture_output=True,
            env=child_env,
            timeout=timeout_value,
        )
        returncode = result.returncode
        output = (result.stdout or "") + (result.stderr or "")
    except subprocess.TimeoutExpired as error:
        timed_out = True
        returncode = TIMEOUT_EXIT_CODE
        stdout = error.stdout.decode(errors="replace") if isinstance(error.stdout, bytes) else error.stdout or ""
        stderr = error.stderr.decode(errors="replace") if isinstance(error.stderr, bytes) else error.stderr or ""
        output = stdout + stderr
    elapsed_ms = (time.monotonic() - started) * 1000
    sys.stdout.write(output)
    if playwright_json:
        sys.stdout.write(f"PLAYWRIGHT_JSON_OUTPUT_FILE={playwright_json}\n")
    report = _report(
        options.name,
        command,
        elapsed_ms,
        returncode,
        output,
        trx_paths,
        cwd,
        playwright_json_paths,
        timeout_seconds=timeout_value if timed_out else None,
    )
    sys.stdout.write(report)
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as summary:
            summary.write(f"### {options.name}\n\n```text\n{report}```\n")
    if options.require_tests:
        counts = _parse_counts(output)
        for path in trx_paths:
            parsed, _ = _parse_trx(path)
            _merge_counts(counts, parsed)
        failures = _required_test_failures(returncode, counts, _parse_retries(output))
        if failures:
            print("TEST_GATE failure=" + ", ".join(failures))
            return 1
    return returncode


if __name__ == "__main__":
    raise SystemExit(main())
