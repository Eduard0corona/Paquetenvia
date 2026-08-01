#!/usr/bin/env python3
"""Programmatic REL-000 unittest runner with contractual test counts."""

from __future__ import annotations

import argparse
import io
import json
import sys
import unittest
from pathlib import Path
from typing import Any


class FocusedTestCountMismatch(Exception):
    reason_code = "REL000_FOCUSED_TEST_COUNT_MISMATCH"

    def __init__(self, expected: int, discovered: int) -> None:
        super().__init__(
            f"{self.reason_code}: expected {expected} focused tests, discovered {discovered}."
        )
        self.expected = expected
        self.discovered = discovered


def count_cases(suite: unittest.TestSuite) -> int:
    return suite.countTestCases()


def execute_suite(
    suite: unittest.TestSuite,
    expected: int,
    *,
    stream: io.TextIOBase | None = None,
) -> dict[str, Any]:
    discovered = count_cases(suite)
    if discovered != expected:
        raise FocusedTestCountMismatch(expected, discovered)

    result = unittest.TextTestRunner(
        stream=stream or sys.stderr,
        verbosity=2,
    ).run(suite)
    executed = result.testsRun
    failed = len(result.failures) + len(result.errors) + len(result.unexpectedSuccesses)
    skipped = len(result.skipped)
    passed = executed - failed - skipped
    output = {
        "format_version": "paquetenvia-rel000-focused-tests-v2",
        "python_tests_expected": expected,
        "python_tests_discovered": discovered,
        "python_tests_executed": executed,
        "python_tests_passed": passed,
        "python_tests_failed": failed,
        "python_tests_skipped": skipped,
        "successful": (
            expected == discovered
            and executed == discovered
            and passed == executed
            and failed == 0
            and skipped == 0
            and result.wasSuccessful()
        ),
    }
    if not output["successful"]:
        raise RuntimeError(
            "REL000_PYTHON_FOCUSED_TESTS_FAILED: "
            f"executed={executed}, failed={failed}, skipped={skipped}."
        )
    return output


def discover_suite(test_file: Path) -> unittest.TestSuite:
    return unittest.defaultTestLoader.discover(
        start_dir=str(test_file.parent),
        pattern=test_file.name,
        top_level_dir=str(test_file.parent),
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--test-file",
        type=Path,
        default=Path(__file__).with_name("test_rel000.py"),
    )
    parser.add_argument("--expected", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        output = execute_suite(discover_suite(args.test_file.resolve()), args.expected)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(
            json.dumps(output, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        return 0
    except FocusedTestCountMismatch as exc:
        print(
            json.dumps(
                {
                    "result": "REL000_TECHNICAL_VALIDATION_FAILED",
                    "reason_code": exc.reason_code,
                    "python_tests_expected": exc.expected,
                    "python_tests_discovered": exc.discovered,
                },
                sort_keys=True,
            ),
            file=sys.stderr,
        )
        return 1
    except RuntimeError as exc:
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
