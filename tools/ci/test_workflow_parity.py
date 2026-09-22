"""ci.yml ↔ pr-validation.yml parity.

PR Validation intentionally duplicates the Foundation job bodies so that a job which
does run validates exactly what Foundation validates; change-awareness only skips
whole jobs. These tests prove that duplication stayed equivalent:

* shared jobs: identical except the orchestration keys ``needs`` and ``if``;
* rel000: identical except ``needs``/``if``, the logical ``normative`` projection
  synthesised from the three decomposed normative controls, and the REL-000
  workflow-provenance profile (``--workflow-profile pr-validation``);
* normative decomposition: every semantic step of the Foundation ``normative`` job
  appears in exactly one of ``secret-scan`` / ``normative-contracts`` / ``azr-static``
  with an identical step body.
"""

from __future__ import annotations

import copy
import unittest
from pathlib import Path

import yaml

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
CI_WORKFLOW = REPOSITORY_ROOT / ".github/workflows/ci.yml"
PR_VALIDATION_WORKFLOW = REPOSITORY_ROOT / ".github/workflows/pr-validation.yml"

SHARED_JOB_IDS = [
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
]
DECOMPOSED_JOB_IDS = ["secret-scan", "normative-contracts", "azr-static"]
ORCHESTRATION_KEYS = {"needs", "if"}
# Scaffolding a separate job legitimately repeats; it carries no validation semantics.
SCAFFOLD_STEP_NAMES = {"Check out repository", "Set up Python", "Install validator dependencies"}
# Every semantic Foundation normative step and the decomposed job that must own it.
SEMANTIC_STEP_OWNER = {
    "Validate canonical contracts": "normative-contracts",
    "Verify AI-06 checksum": "normative-contracts",
    "Verify AI-18 remains unchanged": "normative-contracts",
    "Test CI tooling": "normative-contracts",
    "Install pinned Bicep CLI": "azr-static",
    "Build and lint Bicep templates (AZR-001 §28.1, offline)": "azr-static",
    "Test AZR-001 static guard tooling": "azr-static",
    "Run AZR-001 §28 static guards": "azr-static",
    "Secret scan (AZR-001 §29, Gitleaks 8.30.1 pinned by digest)": "secret-scan",
}
FOUNDATION_NORMATIVE_PROJECTION = '"normative":"${{ needs.normative.result }}"'
PROVENANCE_STEP_NAME = "Capture and sanitize workflow provenance"
# The only intended step-body difference in rel000: the REL-000 provenance profile.
FOUNDATION_PROVENANCE_TAIL = '--head-sha "$REL000_SOURCE_HEAD_SHA"\n'
PR_VALIDATION_PROVENANCE_TAIL = '--head-sha "$REL000_SOURCE_HEAD_SHA" \\\n  --workflow-profile pr-validation\n'
PR_VALIDATION_NORMATIVE_PROJECTION = (
    '"normative":"${{ needs.secret-scan.result == \'success\' && needs.normative-contracts.result == \'success\' '
    "&& needs.azr-static.result == 'success' && 'success' || "
    "format('decomposed[secret-scan={0},normative-contracts={1},azr-static={2}]', "
    "needs.secret-scan.result, needs.normative-contracts.result, needs.azr-static.result) }}\""
)


def load_jobs(path: Path) -> dict:
    return yaml.safe_load(path.read_text(encoding="utf-8"))["jobs"]


def without_orchestration(job: dict) -> dict:
    return {key: value for key, value in job.items() if key not in ORCHESTRATION_KEYS}


def step_names(job: dict) -> list[str]:
    return [step["name"] for step in job["steps"]]


def with_foundation_provenance_profile(rel000_job: dict) -> dict:
    """PR Validation rel000 with its only intended step difference normalised away."""
    job = copy.deepcopy(rel000_job)
    step = next(step for step in job["steps"] if step["name"] == PROVENANCE_STEP_NAME)
    assert step["run"].count(PR_VALIDATION_PROVENANCE_TAIL) == 1
    step["run"] = step["run"].replace(PR_VALIDATION_PROVENANCE_TAIL, FOUNDATION_PROVENANCE_TAIL)
    return job


class SharedJobParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.foundation = load_jobs(CI_WORKFLOW)
        cls.pr = load_jobs(PR_VALIDATION_WORKFLOW)

    def test_shared_jobs_are_identical_except_orchestration(self):
        for job_id in SHARED_JOB_IDS:
            with self.subTest(job=job_id):
                foundation = self.foundation[job_id]
                pr = self.pr[job_id]
                self.assertFalse(ORCHESTRATION_KEYS & set(foundation), "Foundation shared jobs carry no orchestration")
                self.assertEqual(without_orchestration(foundation), without_orchestration(pr))
                self.assertEqual("classify", pr["needs"])
                self.assertIn(f"required_jobs, '{job_id}')", pr["if"])

    def test_shared_job_substance_is_covered_explicitly(self):
        # Redundant with the whole-job equality above, but names the covered facets.
        for job_id in SHARED_JOB_IDS:
            with self.subTest(job=job_id):
                foundation = self.foundation[job_id]
                pr = self.pr[job_id]
                self.assertEqual(foundation["name"], pr["name"])
                self.assertEqual(foundation["runs-on"], pr["runs-on"])
                self.assertEqual(foundation.get("timeout-minutes"), pr.get("timeout-minutes"))
                self.assertEqual(foundation.get("outputs"), pr.get("outputs"))
                self.assertEqual(foundation.get("env"), pr.get("env"))
                self.assertEqual(foundation.get("permissions"), pr.get("permissions"))
                self.assertEqual(step_names(foundation), step_names(pr))
                for left, right in zip(foundation["steps"], pr["steps"], strict=True):
                    for key in ("uses", "run", "shell", "env", "with", "if", "id", "working-directory"):
                        self.assertEqual(left.get(key), right.get(key), f"{job_id}: {left['name']}: {key}")

    def test_shared_job_artifact_names_are_preserved(self):
        for job_id in SHARED_JOB_IDS:
            uploads = [
                step["with"]["name"]
                for step in self.foundation[job_id]["steps"]
                if str(step.get("uses", "")).startswith("actions/upload-artifact@")
            ]
            pr_uploads = [
                step["with"]["name"]
                for step in self.pr[job_id]["steps"]
                if str(step.get("uses", "")).startswith("actions/upload-artifact@")
            ]
            self.assertEqual(uploads, pr_uploads, job_id)


class Rel000ParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.foundation = load_jobs(CI_WORKFLOW)["rel000"]
        cls.pr = load_jobs(PR_VALIDATION_WORKFLOW)["rel000"]

    def test_rel000_is_identical_except_orchestration_and_normative_projection(self):
        foundation = without_orchestration(self.foundation)
        pr = with_foundation_provenance_profile(without_orchestration(self.pr))
        results = pr["env"]["REL000_JOB_RESULTS_JSON"]
        self.assertIn(PR_VALIDATION_NORMATIVE_PROJECTION, results)
        pr["env"]["REL000_JOB_RESULTS_JSON"] = results.replace(
            PR_VALIDATION_NORMATIVE_PROJECTION, FOUNDATION_NORMATIVE_PROJECTION
        )
        self.assertEqual(foundation, pr)

    def test_rel000_substantive_steps_and_artifact_references_are_equivalent(self):
        self.assertEqual(step_names(self.foundation), step_names(self.pr))
        self.assertEqual(self.foundation["steps"], with_foundation_provenance_profile(self.pr)["steps"])
        for key in (
            "REL000_EXECUTION_ARTIFACT_OUTPUTS_JSON",
            "REL000_OPS001_ARTIFACT_NAME",
            "REL000_OPS001_ARTIFACT_ID",
            "REL000_OPS001_ARTIFACT_DIGEST",
            "REL000_OPS002_ARTIFACT_NAME",
            "REL000_OPS002_ARTIFACT_ID",
            "REL000_OPS002_ARTIFACT_DIGEST",
            "REL000_REMEDIATION_ID",
            "REL000_REMEDIATION_POLICY_PATH",
        ):
            self.assertEqual(self.foundation["env"][key], self.pr["env"][key], key)

    def test_rel000_upstream_orchestration_is_the_only_intended_difference(self):
        self.assertEqual("always()", self.foundation["if"])
        self.assertEqual(
            "always() && needs.classify.result == 'success' && "
            "contains(fromJSON(needs.classify.outputs.plan).required_jobs, 'rel000')",
            self.pr["if"],
        )
        self.assertEqual(["normative", *SHARED_JOB_IDS], self.foundation["needs"])
        self.assertEqual(["classify", *DECOMPOSED_JOB_IDS, *SHARED_JOB_IDS], self.pr["needs"])

    def test_logical_normative_result_requires_all_three_decomposed_controls(self):
        results = self.pr["env"]["REL000_JOB_RESULTS_JSON"]
        projection = results[results.index('"normative":') : results.index('","dotnet"')]
        for job_id in DECOMPOSED_JOB_IDS:
            self.assertIn(f"needs.{job_id}.result == 'success'", projection, job_id)
        self.assertNotIn("needs.normative.result", results)
        # Every other upstream result is projected exactly as Foundation projects it.
        for job_id in SHARED_JOB_IDS:
            fragment = f'"{job_id}":"${{{{ needs.{job_id}.result }}}}"'
            self.assertIn(fragment, self.foundation["env"]["REL000_JOB_RESULTS_JSON"], job_id)
            self.assertIn(fragment, results, job_id)

    def test_rel000_provenance_profile_is_the_only_step_difference(self):
        foundation = next(step for step in self.foundation["steps"] if step["name"] == PROVENANCE_STEP_NAME)
        pr = next(step for step in self.pr["steps"] if step["name"] == PROVENANCE_STEP_NAME)
        self.assertNotIn("--workflow-profile", foundation["run"])
        self.assertNotIn("--workflow-profile", CI_WORKFLOW.read_text(encoding="utf-8"))
        self.assertEqual(1, pr["run"].count("--workflow-profile pr-validation"))
        self.assertEqual(1, pr["run"].count("sanitize-workflow-provenance"))
        self.assertEqual(foundation["run"], with_foundation_provenance_profile(self.pr)["steps"][self.pr["steps"].index(pr)]["run"])
        differing = [
            step["name"]
            for step, other in zip(self.foundation["steps"], self.pr["steps"], strict=True)
            if step != other
        ]
        self.assertEqual([PROVENANCE_STEP_NAME], differing)

    def test_rel000_verifies_upstream_from_the_projected_results(self):
        verify = next(step for step in self.pr["steps"] if step["name"] == "Verify every authoritative upstream job succeeded")
        self.assertIn('Where-Object { $_.Value -cne "success" }', verify["run"])
        self.assertIn("REL000_AUTHORITATIVE_UPSTREAM_FAILED", verify["run"])


class NormativeDecompositionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.normative = load_jobs(CI_WORKFLOW)["normative"]
        pr = load_jobs(PR_VALIDATION_WORKFLOW)
        cls.decomposed = {job_id: pr[job_id] for job_id in DECOMPOSED_JOB_IDS}

    def foundation_step(self, name: str) -> dict:
        return next(step for step in self.normative["steps"] if step["name"] == name)

    def test_semantic_step_owner_map_covers_every_foundation_normative_step(self):
        semantic = [name for name in step_names(self.normative) if name not in SCAFFOLD_STEP_NAMES]
        self.assertEqual(sorted(SEMANTIC_STEP_OWNER), sorted(semantic))

    def test_every_semantic_step_appears_in_exactly_one_decomposed_job_with_identical_body(self):
        for name, owner in SEMANTIC_STEP_OWNER.items():
            with self.subTest(step=name):
                owners = [job_id for job_id, job in self.decomposed.items() if name in step_names(job)]
                self.assertEqual([owner], owners)
                pr_step = next(step for step in self.decomposed[owner]["steps"] if step["name"] == name)
                self.assertEqual(self.foundation_step(name), pr_step)

    def test_decomposed_jobs_carry_no_step_outside_the_foundation_normative_job(self):
        foundation_names = set(step_names(self.normative))
        for job_id, job in self.decomposed.items():
            self.assertTrue(set(step_names(job)) <= foundation_names, job_id)

    def test_semantic_steps_keep_foundation_relative_order(self):
        foundation_order = [name for name in step_names(self.normative) if name in SEMANTIC_STEP_OWNER]
        for job_id, job in self.decomposed.items():
            names = [name for name in step_names(job) if name in SEMANTIC_STEP_OWNER]
            self.assertEqual([name for name in foundation_order if name in names], names, job_id)

    def test_scaffold_matches_foundation(self):
        for job_id, job in self.decomposed.items():
            for step in job["steps"]:
                if step["name"] in SCAFFOLD_STEP_NAMES:
                    self.assertEqual(self.foundation_step(step["name"]), step, f"{job_id}: {step['name']}")

    def test_secret_scan_keeps_pinned_image_config_redaction_and_tested_range(self):
        step = next(step for step in self.decomposed["secret-scan"]["steps"] if step["name"].startswith("Secret scan"))
        self.assertEqual(
            "ghcr.io/gitleaks/gitleaks:v8.30.1@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f",
            step["env"]["GITLEAKS_IMAGE"],
        )
        self.assertEqual("${{ github.sha }}", step["env"]["TESTED_SHA"])
        self.assertIn('range="${TESTED_SHA}^1..${TESTED_SHA}"', step["run"])
        self.assertIn("--config /scan/.gitleaks.toml", step["run"])
        self.assertIn("--redact", step["run"])
        self.assertIn("SCANNER_FAILURE / BLOCKED", step["run"])
        self.assertNotIn("pull_request.base.sha", step["run"])
        checkout = self.decomposed["secret-scan"]["steps"][0]
        self.assertTrue(str(checkout["uses"]).startswith("actions/checkout@"))
        self.assertEqual(0, checkout["with"]["fetch-depth"])

    def test_azr_static_is_offline(self):
        text = yaml.safe_dump(self.decomposed["azr-static"])
        for forbidden in ("azure/login", "az login", "az deployment", "id-token", "AZURE_"):
            self.assertNotIn(forbidden, text, forbidden)
        names = step_names(self.decomposed["azr-static"])
        self.assertLess(names.index("Install pinned Bicep CLI"), names.index("Build and lint Bicep templates (AZR-001 §28.1, offline)"))
        self.assertLess(names.index("Build and lint Bicep templates (AZR-001 §28.1, offline)"), names.index("Run AZR-001 §28 static guards"))

    def test_decomposed_jobs_have_no_azure_or_write_permissions(self):
        for job_id, job in self.decomposed.items():
            self.assertNotIn("permissions", job, job_id)
            self.assertNotIn("environment", job, job_id)


if __name__ == "__main__":
    unittest.main()
