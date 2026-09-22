"""Slice-2 workflow invariants: Foundation CI (authoritative, 13 jobs) and PR Validation.

The 13-job pin itself is already enforced by the AZR-001 guard, by REL-000
``test_166`` and by ``SyntheticEnvironmentArchitectureTests``; here it is asserted
again as the anchor for the trigger split and the job-id correspondence checks.
Substantive job parity between the two workflows lives in ``test_workflow_parity``.
"""

from __future__ import annotations

import re
import sys
import unittest
from pathlib import Path

import yaml

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
NORMATIVE_DECOMPOSITION = ["secret-scan", "normative-contracts", "azr-static"]
PR_VALIDATION_JOB_IDS = ["classify", *NORMATIVE_DECOMPOSITION, *FOUNDATION_JOB_IDS[1:], "pr-gate"]
STRUCTURAL_JOB_IDS = {"classify", "pr-gate"}
JOB_LINE = re.compile(r"^  ([a-z0-9_-]+):$")


def load_workflow(path: Path) -> dict:
    data = yaml.safe_load(path.read_text(encoding="utf-8"))
    # PyYAML (YAML 1.1) reads the bare `on:` key as boolean True.
    data["on"] = data.pop(True, data.get("on"))
    return data


def job_ids_from_text(path: Path) -> list[str]:
    lines = path.read_text(encoding="utf-8").splitlines()
    start = lines.index("jobs:")
    return [match.group(1) for match in (JOB_LINE.match(line) for line in lines[start + 1 :]) if match]


def required_condition(job_id: str) -> str:
    return (
        "needs.classify.result == 'success' && "
        f"contains(fromJSON(needs.classify.outputs.plan).required_jobs, '{job_id}')"
    )


class FoundationInvariantTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = load_workflow(CI_WORKFLOW)
        cls.text = CI_WORKFLOW.read_text(encoding="utf-8")

    def test_workflow_name_is_foundation_ci(self):
        self.assertEqual("Foundation CI", self.workflow["name"])
        self.assertTrue(self.text.startswith("name: Foundation CI\n"))

    def test_pull_request_trigger_is_narrowed_to_base_main(self):
        self.assertEqual({"branches": ["main"]}, self.workflow["on"]["pull_request"])

    def test_push_main_trigger_is_retained(self):
        self.assertEqual({"branches": ["main"]}, self.workflow["on"]["push"])
        self.assertEqual({"pull_request", "push"}, set(self.workflow["on"]))

    def test_exactly_thirteen_jobs_with_expected_ids(self):
        self.assertEqual(FOUNDATION_JOB_IDS, list(self.workflow["jobs"]))
        self.assertEqual(FOUNDATION_JOB_IDS, job_ids_from_text(CI_WORKFLOW))
        self.assertEqual(13, len(self.workflow["jobs"]))

    def test_pr_validation_structural_and_decomposed_jobs_are_absent(self):
        self.assertFalse((STRUCTURAL_JOB_IDS | set(NORMATIVE_DECOMPOSITION)) & set(self.workflow["jobs"]))

    def test_rel000_remains_the_terminal_job_needing_every_other_job(self):
        rel000 = self.workflow["jobs"]["rel000"]
        self.assertEqual("Validate MVP-0 internal release evidence", rel000["name"])
        self.assertEqual("always()", rel000["if"])
        self.assertEqual(FOUNDATION_JOB_IDS[:-1], rel000["needs"])

    def test_shared_pr_validation_job_ids_correspond_to_foundation_jobs(self):
        shared = set(CONFIG["jobs"]) - set(NORMATIVE_DECOMPOSITION)
        self.assertEqual(set(FOUNDATION_JOB_IDS) - {"normative"}, shared)
        self.assertEqual({"rel000"}, set(CONFIG["full_only_jobs"]))

    def test_normative_job_carries_the_decomposed_controls(self):
        names = [step["name"] for step in self.workflow["jobs"]["normative"]["steps"]]
        self.assertIn("Validate canonical contracts", names)
        self.assertIn("Build and lint Bicep templates (AZR-001 §28.1, offline)", names)
        self.assertIn("Run AZR-001 §28 static guards", names)
        self.assertTrue(any(name.startswith("Secret scan (AZR-001 §29") for name in names))

    def test_ci_tooling_tests_run_inside_normative(self):
        for job_id, job in self.workflow["jobs"].items():
            runs = [step.get("run", "") for step in job["steps"]]
            has_tooling = any('python -m unittest discover -s ./tools/ci -p "test_*.py"' in run for run in runs)
            self.assertEqual(job_id == "normative", has_tooling, job_id)

    def test_main_source_guard_and_classification_are_not_wired_into_foundation(self):
        self.assertNotIn("main_source_guard", self.text)
        self.assertNotIn("classify_changes", self.text)
        self.assertNotIn("pr_gate", self.text)
        self.assertNotIn("fromJSON(needs.classify", self.text)


class PrValidationInvariantTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = load_workflow(PR_VALIDATION_WORKFLOW)
        cls.text = PR_VALIDATION_WORKFLOW.read_text(encoding="utf-8")
        cls.jobs = cls.workflow["jobs"]

    def test_workflow_exists_and_is_named_pr_validation(self):
        self.assertTrue(PR_VALIDATION_WORKFLOW.exists())
        self.assertEqual("PR Validation", self.workflow["name"])

    def test_only_pull_request_into_development_triggers(self):
        self.assertEqual(["pull_request"], list(self.workflow["on"]))
        trigger = self.workflow["on"]["pull_request"]
        self.assertEqual(["development"], trigger["branches"])
        self.assertEqual(["opened", "synchronize", "reopened", "labeled", "unlabeled"], trigger["types"])
        self.assertNotIn("paths", trigger)
        self.assertNotIn("paths-ignore", trigger)

    def test_no_push_dispatch_schedule_or_target_triggers(self):
        for forbidden in ("push", "workflow_dispatch", "schedule", "pull_request_target", "workflow_run"):
            self.assertNotIn(forbidden, self.workflow["on"], forbidden)
        self.assertNotIn("pull_request_target", self.text)
        self.assertNotIn("workflow_dispatch", self.text)

    def test_seventeen_jobs_in_expected_order(self):
        self.assertEqual(PR_VALIDATION_JOB_IDS, list(self.jobs))
        self.assertEqual(PR_VALIDATION_JOB_IDS, job_ids_from_text(PR_VALIDATION_WORKFLOW))
        self.assertEqual(17, len(self.jobs))

    def test_classified_job_ids_are_exactly_the_configuration_jobs(self):
        classified = [job_id for job_id in self.jobs if job_id not in STRUCTURAL_JOB_IDS]
        self.assertEqual(list(CONFIG["jobs"]), classified)

    def test_least_privilege_permissions(self):
        self.assertEqual({"actions": "read", "contents": "read", "issues": "read"}, self.workflow["permissions"])
        for job_id, job in self.jobs.items():
            for scope, level in (job.get("permissions") or {}).items():
                self.assertEqual("read", level, f"{job_id}: {scope}")
        for forbidden in ("id-token", "contents: write", "pull-requests: write", "deployments", "packages: write"):
            self.assertNotIn(forbidden, self.text, forbidden)

    def test_no_azure_credentials_environments_or_deployment(self):
        for forbidden in ("azure/login", "environment:", "az login", "az deployment", "deploy-core.ps1", "AZURE_CLIENT_ID"):
            self.assertNotIn(forbidden, self.text, forbidden)
        for job_id, job in self.jobs.items():
            self.assertNotIn("environment", job, job_id)

    def test_pr_scoped_concurrency_cancels_superseded_runs(self):
        self.assertEqual(
            {"group": "pr-validation-${{ github.event.pull_request.number }}", "cancel-in-progress": True},
            self.workflow["concurrency"],
        )

    def test_every_job_runs_self_hosted(self):
        for job_id, job in self.jobs.items():
            self.assertEqual("self-hosted", job["runs-on"], job_id)

    def test_classify_job_contract(self):
        classify = self.jobs["classify"]
        self.assertEqual("Classify PR impact", classify["name"])
        self.assertNotIn("needs", classify)
        self.assertNotIn("if", classify)
        self.assertEqual("${{ steps.classify.outputs.plan }}", classify["outputs"]["plan"])

    def test_fork_precheck_runs_before_any_checkout_or_repository_code(self):
        steps = self.jobs["classify"]["steps"]
        first = steps[0]
        self.assertEqual("Reject pull requests from forks before any checkout", first["name"])
        self.assertNotIn("uses", first)
        self.assertEqual("${{ github.event.pull_request.head.repo.full_name }}", first["env"]["HEAD_REPO"])
        self.assertEqual("${{ github.repository }}", first["env"]["REPOSITORY"])
        self.assertIn('[ "${HEAD_REPO}" != "${REPOSITORY}" ]', first["run"])
        self.assertIn("exit 1", first["run"])
        self.assertNotIn("git ", first["run"])
        self.assertNotIn("python", first["run"])
        checkout_index = next(i for i, step in enumerate(steps) if str(step.get("uses", "")).startswith("actions/checkout@"))
        self.assertGreater(checkout_index, 0)
        self.assertEqual(0, steps[checkout_index]["with"]["fetch-depth"])

    def test_classifier_and_certification_run_from_tested_first_parent(self):
        steps = {step["name"]: step for step in self.jobs["classify"]["steps"]}
        extract = steps["Extract trusted CI tooling from the tested first parent"]
        self.assertEqual("${{ github.sha }}", extract["env"]["TESTED_SHA"])
        self.assertIn('git rev-parse --verify "${TESTED_SHA}^1"', extract["run"])
        for path in ("tools/ci/classify_changes.py", "tools/ci/change-domains.json", "tools/azr-001/azr001_static_guards.py"):
            self.assertIn(path, extract["run"], path)
        self.assertIn('git show "${trusted_base}:${path}"', extract["run"])
        classify = steps["Classify tested changes with trusted tooling"]
        self.assertIn('python "${PV_TRUSTED_DIR}/tools/ci/classify_changes.py"', classify["run"])
        self.assertIn('--config "${PV_TRUSTED_DIR}/tools/ci/change-domains.json"', classify["run"])
        self.assertNotIn("python ./tools/ci/classify_changes.py", classify["run"])
        self.assertNotIn("python tools/ci/classify_changes.py", classify["run"])
        certify = steps["Resolve exact Foundation certification of the main head"]
        self.assertIn('python "${PV_TRUSTED_DIR}/tools/azr-001/azr001_static_guards.py" deploy-gate', certify["run"])

    def test_classifier_receives_tested_merge_provenance_never_event_base_sha(self):
        classify = next(step for step in self.jobs["classify"]["steps"] if step.get("id") == "classify")
        env = classify["env"]
        self.assertEqual("${{ github.sha }}", env["TESTED_SHA"])
        self.assertEqual("${{ github.event.pull_request.head.sha }}", env["SOURCE_HEAD_SHA"])
        self.assertEqual("${{ github.head_ref }}", env["HEAD_REF"])
        self.assertEqual("${{ github.event.pull_request.head.repo.full_name }}", env["HEAD_REPO"])
        self.assertEqual("${{ toJSON(github.event.pull_request.labels.*.name) }}", env["LABELS_JSON"])
        self.assertEqual("${{ steps.certify.outputs.certified-main-sha }}", env["CERTIFIED_MAIN_SHA"])
        for flag in ("--tested-git-sha", "--source-head-sha", "--head-ref", "--head-repo", "--repository", "--certified-main-sha", "--label", "--github-output"):
            self.assertIn(flag, classify["run"], flag)
        self.assertNotIn("pull_request.base.sha", "\n".join(str(step) for step in self.jobs["classify"]["steps"]))

    def test_certification_is_attempted_only_for_head_main_and_proves_exact_push_main_run(self):
        certify = next(step for step in self.jobs["classify"]["steps"] if step.get("id") == "certify")
        self.assertEqual("github.head_ref == 'main'", certify["if"])
        self.assertEqual("${{ github.token }}", certify["env"]["GH_TOKEN"])
        run = certify["run"]
        self.assertIn("actions/workflows/ci.yml/runs?event=push&branch=main&head_sha=${SOURCE_HEAD_SHA}", run)
        for flag in (
            '--tested-git-sha "${SOURCE_HEAD_SHA}"',
            "--dispatch-ref refs/heads/main",
            '--dispatch-sha "${SOURCE_HEAD_SHA}"',
            '--foundation-run-id "${run_id}"',
            "--foundation-run-json",
            "--foundation-jobs-json",
        ):
            self.assertIn(flag, run, flag)
        self.assertIn('certified=""', run)
        self.assertIn('echo "certified-main-sha=${certified}" >> "$GITHUB_OUTPUT"', run)
        self.assertIn("MAIN_BACKSYNC_UNCERTIFIED", run)

    def test_every_classified_job_depends_on_classify_and_the_single_plan(self):
        for job_id in CONFIG["jobs"]:
            job = self.jobs[job_id]
            self.assertEqual("classify" if job_id != "rel000" else PR_VALIDATION_JOB_IDS[:-2], job["needs"], job_id)
            expected = required_condition(job_id)
            if job_id == "rel000":
                expected = "always() && " + expected
            self.assertEqual(expected, job["if"], job_id)

    def test_no_job_reimplements_path_selection(self):
        for job_id, job in self.jobs.items():
            self.assertNotIn("paths", job, job_id)
            condition = str(job.get("if", ""))
            self.assertNotIn("github.event.pull_request.changed_files", condition)
            self.assertNotIn("files", condition)

    def test_pr_gate_contract(self):
        gate = self.jobs["pr-gate"]
        self.assertEqual("PR Gate", gate["name"])
        self.assertEqual("always()", gate["if"])
        self.assertEqual(PR_VALIDATION_JOB_IDS[:-1], gate["needs"])
        self.assertEqual("${{ toJSON(needs) }}", gate["env"]["PV_NEEDS_JSON"])
        self.assertEqual("${{ needs.classify.result }}", gate["env"]["PV_CLASSIFY_RESULT"])
        self.assertEqual("${{ needs.classify.outputs.plan }}", gate["env"]["PV_PLAN_JSON"])

    def test_pr_gate_fails_closed_before_checkout_when_classifier_did_not_succeed(self):
        steps = self.jobs["pr-gate"]["steps"]
        first = steps[0]
        self.assertNotIn("uses", first)
        self.assertIn('[ "${PV_CLASSIFY_RESULT}" != "success" ]', first["run"])
        self.assertIn("PR_GATE_CLASSIFICATION_UNTRUSTED", first["run"])
        self.assertIn("exit 1", first["run"])
        checkout_index = next(i for i, step in enumerate(steps) if str(step.get("uses", "")).startswith("actions/checkout@"))
        self.assertGreater(checkout_index, 0)

    def test_pr_gate_runs_from_tested_first_parent(self):
        steps = {step["name"]: step for step in self.jobs["pr-gate"]["steps"]}
        extract = steps["Extract trusted PR Gate from the tested first parent"]
        self.assertEqual("${{ github.sha }}", extract["env"]["TESTED_SHA"])
        self.assertIn('git rev-parse --verify "${TESTED_SHA}^1"', extract["run"])
        for path in ("tools/ci/pr_gate.py", "tools/ci/classify_changes.py", "tools/ci/change-domains.json"):
            self.assertIn(path, extract["run"], path)
        evaluate = steps["Evaluate PR Gate with trusted tooling"]
        self.assertIn('python "${PV_TRUSTED_GATE_DIR}/tools/ci/pr_gate.py"', evaluate["run"])
        self.assertIn('--needs-json "${PV_NEEDS_JSON}"', evaluate["run"])
        self.assertIn('--plan-json "${PV_PLAN_JSON}"', evaluate["run"])
        self.assertIn('--summary "$GITHUB_STEP_SUMMARY"', evaluate["run"])
        self.assertNotIn("python ./tools/ci/pr_gate.py", evaluate["run"])

    def test_main_source_guard_is_not_wired_yet(self):
        self.assertNotIn("main_source_guard", self.text)

    def test_workflow_self_change_classifies_full(self):
        for path in (".github/workflows/pr-validation.yml", "tools/ci/pr_gate.py", "tools/ci/change-domains.json"):
            plan = classifier.classify_paths(
                CONFIG,
                [path],
                head_ref="feature/x",
                head_repo="Eduard0corona/Paquetenvia",
                repository="Eduard0corona/Paquetenvia",
            )
            self.assertEqual("FULL", plan["classification"], path)
            self.assertIn("FULL:CI_SELF", plan["reasons"], path)


if __name__ == "__main__":
    unittest.main()
