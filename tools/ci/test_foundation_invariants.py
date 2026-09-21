"""Slice-1 Foundation invariants that prepare the future ci.yml ↔ pr-validation.yml parity test.

The 13-job pin itself is already enforced by the AZR-001 guard, by REL-000
``test_166`` and by ``SyntheticEnvironmentArchitectureTests``; here it is asserted
again only as the anchor for the job-id correspondence checks below.
"""

from __future__ import annotations

import re
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import classify_changes as classifier  # noqa: E402

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
CI_WORKFLOW = REPOSITORY_ROOT / ".github/workflows/ci.yml"
PR_VALIDATION_WORKFLOW = REPOSITORY_ROOT / ".github/workflows/pr-validation.yml"
CONFIG = classifier.load_config(classifier.DEFAULT_CONFIG_PATH)

FOUNDATION_JOB_IDS = [
    "normative",
    "dotnet",
    "runtime-contracts",
    "web",
    "realtime-e2e",
    "outbox-signalr-delivery",
    "driver-stops-pwa",
    "public-tracking",
    "operations-dashboard",
    "delivery-simulation",
    "infrastructure",
    "backup-restore",
    "rel000",
]
# Future PR Validation jobs that decompose the Foundation `normative` job or are new.
NORMATIVE_DECOMPOSITION = {"secret-scan", "normative-contracts", "azr-static"}
FUTURE_ONLY_JOB_IDS = {"classify", "pr-gate"} | NORMATIVE_DECOMPOSITION
JOB_LINE = re.compile(r"^  ([a-z0-9_-]+):$")


def workflow_lines() -> list[str]:
    return CI_WORKFLOW.read_text(encoding="utf-8").splitlines()


def job_ids(lines: list[str]) -> list[str]:
    start = lines.index("jobs:")
    return [match.group(1) for match in (JOB_LINE.match(line) for line in lines[start + 1 :]) if match]


def job_block(lines: list[str], job_id: str) -> list[str]:
    ids = job_ids(lines)
    start = lines.index(f"  {job_id}:")
    following = [lines.index(f"  {other}:") for other in ids if lines.index(f"  {other}:") > start]
    end = min(following) if following else len(lines)
    return lines[start:end]


class FoundationInvariantTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.lines = workflow_lines()
        cls.ids = job_ids(cls.lines)

    def test_workflow_name_is_foundation_ci(self):
        self.assertEqual("name: Foundation CI", self.lines[0])

    def test_push_main_trigger_is_retained(self):
        text = "\n".join(self.lines[: self.lines.index("jobs:")])
        self.assertRegex(text, r"push:\n\s+branches:\n\s+- main")

    def test_exactly_thirteen_jobs_with_expected_ids(self):
        self.assertEqual(13, len(self.ids))
        self.assertEqual(FOUNDATION_JOB_IDS, self.ids)

    def test_no_future_pr_validation_job_was_added(self):
        self.assertFalse(FUTURE_ONLY_JOB_IDS & set(self.ids))

    def test_rel000_remains_the_terminal_job_needing_every_other_job(self):
        block = "\n".join(job_block(self.lines, "rel000"))
        self.assertIn("name: Validate MVP-0 internal release evidence", block)
        self.assertIn("if: always()", block)
        for job_id in FOUNDATION_JOB_IDS[:-1]:
            self.assertIn(f"      - {job_id}\n", block + "\n", job_id)

    def test_shared_future_job_ids_correspond_to_foundation_jobs(self):
        shared = set(CONFIG["jobs"]) - NORMATIVE_DECOMPOSITION
        self.assertEqual(set(FOUNDATION_JOB_IDS) - {"normative"}, shared)
        self.assertEqual({"rel000"}, set(CONFIG["full_only_jobs"]))

    def test_normative_job_carries_the_decomposed_controls(self):
        block = "\n".join(job_block(self.lines, "normative"))
        self.assertIn("Validate canonical contracts", block)
        self.assertIn("Build and lint Bicep templates", block)
        self.assertIn("Run AZR-001 §28 static guards", block)
        self.assertIn("Secret scan (AZR-001 §29", block)

    def test_ci_tooling_tests_run_inside_normative(self):
        block = "\n".join(job_block(self.lines, "normative"))
        self.assertIn("- name: Test CI tooling", block)
        self.assertIn('python -m unittest discover -s ./tools/ci -p "test_*.py"', block)
        for other in FOUNDATION_JOB_IDS[1:]:
            self.assertNotIn("Test CI tooling", "\n".join(job_block(self.lines, other)), other)

    def test_main_source_guard_is_not_enforced_yet(self):
        text = "\n".join(self.lines)
        self.assertNotIn("main_source_guard", text)
        self.assertNotIn("classify_changes", text)
        self.assertNotIn("pr_gate", text)

    def test_pr_validation_workflow_does_not_exist_yet(self):
        self.assertFalse(PR_VALIDATION_WORKFLOW.exists())


if __name__ == "__main__":
    unittest.main()
