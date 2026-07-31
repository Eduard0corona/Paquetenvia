#!/usr/bin/env python3
"""Exercise REL-000 publication rollback through real PowerShell subprocesses."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
from pathlib import Path


SCENARIOS = (
    "cancel-before-output",
    "cancel-after-staging",
    "cancel-after-first-json",
    "error-after-final-output",
    "preexisting-output",
    "artifact-other-sha",
    "artifact-other-run",
    "manifest-incomplete",
    "staging-link-rejected",
    "foreign-target-preserved",
    "no-success-after-failure",
    "staging-partial-removed",
    "output-partial-removed",
    "source-inputs-unmodified",
)


def digest_tree(root: Path) -> str:
    digest = hashlib.sha256()
    for path in sorted(root.rglob("*")):
        if path.is_file():
            digest.update(path.relative_to(root).as_posix().encode("utf-8"))
            digest.update(path.read_bytes())
    return digest.hexdigest()


def run_scenario(wrapper: Path, scenario: str) -> None:
    with tempfile.TemporaryDirectory(prefix="paquetenvia-rel000-rollback-") as temporary:
        root = Path(temporary).resolve()
        inputs = root / "inputs"
        foreign = root / "foreign"
        inputs.mkdir()
        foreign.mkdir()
        (inputs / "artifact.json").write_text('{"source":"preserve"}\n', encoding="utf-8")
        (foreign / "sentinel.txt").write_text("preserve\n", encoding="utf-8")
        before = digest_tree(inputs)
        environment = {
            **os.environ,
            "REL000_TEST_MODE": "true",
        }
        powershell = shutil.which("pwsh") or shutil.which("powershell")
        if powershell is None:
            raise RuntimeError("PowerShell is unavailable")
        result = subprocess.run(
            [
                powershell,
                "-NoLogo",
                "-NoProfile",
                "-File",
                str(wrapper),
                "-SyntheticRollbackScenario",
                scenario,
                "-SyntheticRoot",
                str(root),
            ],
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            env=environment,
        )
        if result.returncode == 0:
            raise RuntimeError(f"{scenario}: wrapper unexpectedly succeeded")
        if digest_tree(inputs) != before:
            raise RuntimeError(f"{scenario}: source inputs were modified")
        if not (foreign / "sentinel.txt").is_file():
            raise RuntimeError(f"{scenario}: foreign target was not preserved")
        if (root / "staging").exists():
            raise RuntimeError(f"{scenario}: partial staging was not removed")
        output = root / "output"
        if scenario == "preexisting-output":
            if not (output / "preexisting.txt").is_file():
                raise RuntimeError(f"{scenario}: preexisting destination was modified")
        elif output.exists():
            raise RuntimeError(f"{scenario}: partial output was not removed")
        published = (
            {
                path.name
                for path in output.iterdir()
                if path.is_file()
            }
            if output.is_dir()
            else set()
        )
        expected = {
            "rel000-internal-release-report.json",
            "rel000-p0-evidence.json",
            "rel000-cross-tenant-evidence.json",
            "rel000-rollback-evidence.json",
        }
        if published == expected:
            raise RuntimeError(f"{scenario}: a successful report survived failure")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--wrapper", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    passed = 0
    failures: list[str] = []
    for scenario in SCENARIOS:
        try:
            run_scenario(args.wrapper.resolve(), scenario)
            passed += 1
        except Exception as exc:  # noqa: BLE001 - aggregate every independent scenario
            failures.append(f"{scenario}: {exc}")

    result = {
        "format_version": "paquetenvia-rel000-rollback-execution-v2",
        "rollback_scenarios_expected": len(SCENARIOS),
        "rollback_scenarios_executed": len(SCENARIOS),
        "rollback_scenarios_passed": passed,
        "rollback_scenarios_failed": len(failures),
        "rollback_cleanup_verified": not failures,
        "foreign_targets_preserved": not failures,
        "successful_report_after_failure": False,
        "failures": failures,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    if failures:
        print(json.dumps(result, ensure_ascii=False), file=os.sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
