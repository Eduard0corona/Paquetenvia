"""Unit tests for the fail-closed PR Gate."""

from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import classify_changes as classifier  # noqa: E402
import pr_gate as gate  # noqa: E402

CONFIG = classifier.load_config(classifier.DEFAULT_CONFIG_PATH)
ALL_JOBS = list(CONFIG["jobs"])
REPOSITORY = "Eduard0corona/Paquetenvia"


CERTIFIED = "c" * 40
# FULL through a non-dependency full domain (SECURITY_CONTROL); DEPS is policy-blocked.
FULL_NON_DEPS_PATH = ".gitleaks.toml"
SECURITY_REMEDIATION_BRANCHES = ("fix/security-2026-09-next-critical", "fix/security-2026-08-web-transitives")


def plan_for(paths, head_ref="feature/x", labels=None, certified_main_sha=None):
    return classifier.classify_paths(
        CONFIG,
        list(paths),
        head_ref=head_ref,
        head_repo=REPOSITORY,
        repository=REPOSITORY,
        labels=labels,
        source_head_sha=CERTIFIED if head_ref == "main" else None,
        certified_main_sha=certified_main_sha,
    )


def needs_for(plan, overrides=None, classify_result="success", include_plan=True):
    """Build a toJSON(needs)-shaped dict consistent with the plan, then apply overrides."""
    required = set(plan["required_jobs"])
    needs = {
        "classify": {
            "result": classify_result,
            "outputs": {"plan": classifier.serialize_plan(plan)} if include_plan else {},
        }
    }
    for job in ALL_JOBS:
        needs[job] = {"result": "success" if job in required else "skipped", "outputs": {}}
    for job, result in (overrides or {}).items():
        if result is None:
            needs.pop(job, None)
        else:
            needs[job] = {"result": result, "outputs": {}}
    return needs


def reasons(evaluation):
    return [item["reason"] for item in evaluation["failures"]]


class PassTests(unittest.TestCase):
    def test_selective_success(self):
        plan = plan_for(["apps/web/src/driver/a.tsx"])
        evaluation = gate.evaluate(None, needs_for(plan), CONFIG)
        self.assertEqual("PASS", evaluation["verdict"], evaluation["failures"])
        self.assertEqual(len(ALL_JOBS), len(evaluation["matrix"]))

    def test_full_success(self):
        plan = plan_for([FULL_NON_DEPS_PATH])
        self.assertEqual("FULL", plan["classification"])
        self.assertEqual("PASS", gate.evaluate(None, needs_for(plan), CONFIG)["verdict"])

    def test_main_backsync_success(self):
        plan = plan_for(["apps/web/package.json"], head_ref="main", certified_main_sha=CERTIFIED)
        self.assertEqual("MAIN_BACKSYNC", plan["classification"])
        evaluation = gate.evaluate(None, needs_for(plan), CONFIG)
        self.assertEqual("PASS", evaluation["verdict"])
        rel000 = next(row for row in evaluation["matrix"] if row["job"] == "rel000")
        self.assertEqual({"required": False, "result": "skipped"}, {"required": rel000["required"], "result": rel000["result"]})

    def test_uncertified_main_backsync_is_full_and_requires_rel000(self):
        plan = plan_for(["docs/adr/x.md"], head_ref="main")
        self.assertEqual("FULL", plan["classification"])
        self.assertIn("FULL:MAIN_BACKSYNC_UNCERTIFIED", plan["reasons"])
        self.assertEqual("PASS", gate.evaluate(None, needs_for(plan), CONFIG)["verdict"])
        evaluation = gate.evaluate(None, needs_for(plan, {"rel000": "skipped"}), CONFIG)
        self.assertEqual([gate.REASON_REQUIRED_JOB_SKIPPED], reasons(evaluation))

    def test_explicit_plan_argument_is_used(self):
        plan = plan_for(["docs/adr/x.md"])
        evaluation = gate.evaluate(classifier.serialize_plan(plan), needs_for(plan, include_plan=False), CONFIG)
        self.assertEqual("PASS", evaluation["verdict"])

    def test_non_required_skipped_is_accepted(self):
        plan = plan_for(["docs/adr/x.md"])
        evaluation = gate.evaluate(None, needs_for(plan), CONFIG)
        self.assertEqual("PASS", evaluation["verdict"])
        self.assertTrue(all(row["result"] == "skipped" for row in evaluation["matrix"] if not row["required"]))


class ClassificationTrustTests(unittest.TestCase):
    def test_classifier_failure(self):
        plan = plan_for(["docs/adr/x.md"])
        for result in ("failure", "skipped", "cancelled", None):
            with self.subTest(result=result):
                needs = needs_for(plan)
                if result is None:
                    needs.pop("classify")
                else:
                    needs["classify"]["result"] = result
                evaluation = gate.evaluate(None, needs, CONFIG)
                self.assertEqual("FAIL", evaluation["verdict"])
                self.assertEqual([gate.REASON_CLASSIFICATION_UNTRUSTED], reasons(evaluation))

    def test_missing_plan_output(self):
        plan = plan_for(["docs/adr/x.md"])
        evaluation = gate.evaluate(None, needs_for(plan, include_plan=False), CONFIG)
        self.assertEqual([gate.REASON_CLASSIFICATION_UNTRUSTED], reasons(evaluation))

    def test_malformed_plan(self):
        plan = plan_for(["docs/adr/x.md"])
        for bad in ("{not json", "[]", '{"format":"pv-plan-v1"}'):
            with self.subTest(plan=bad):
                evaluation = gate.evaluate(bad, needs_for(plan), CONFIG)
                self.assertEqual([gate.REASON_CLASSIFICATION_UNTRUSTED], reasons(evaluation))

    def test_wrong_plan_format(self):
        plan = dict(plan_for(["docs/adr/x.md"]))
        plan["format"] = "pv-plan-v0"
        evaluation = gate.evaluate(json.dumps(plan), needs_for(plan), CONFIG)
        self.assertEqual([gate.REASON_CLASSIFICATION_UNTRUSTED], reasons(evaluation))

    def test_plan_without_universal_job_is_untrusted(self):
        plan = dict(plan_for(["docs/adr/x.md"]))
        plan["required_jobs"] = []
        evaluation = gate.evaluate(json.dumps(plan), needs_for(plan), CONFIG)
        self.assertEqual([gate.REASON_CLASSIFICATION_UNTRUSTED], reasons(evaluation))

    def test_full_plan_missing_jobs_is_untrusted(self):
        plan = dict(plan_for(["global.json"]))
        plan["required_jobs"] = ["secret-scan"]
        evaluation = gate.evaluate(json.dumps(plan), needs_for(plan), CONFIG)
        self.assertEqual([gate.REASON_CLASSIFICATION_UNTRUSTED], reasons(evaluation))

    def test_needs_not_an_object(self):
        evaluation = gate.evaluate(None, [], CONFIG)
        self.assertEqual("FAIL", evaluation["verdict"])


class JobResultTests(unittest.TestCase):
    def test_secret_scan_failure(self):
        plan = plan_for(["docs/adr/x.md"])
        for result in ("failure", "skipped", "cancelled"):
            with self.subTest(result=result):
                evaluation = gate.evaluate(None, needs_for(plan, {"secret-scan": result}), CONFIG)
                self.assertEqual([gate.REASON_SECRET_SCAN_FAILED], reasons(evaluation))

    def test_required_skipped(self):
        plan = plan_for(["apps/web/src/driver/a.tsx"])
        evaluation = gate.evaluate(None, needs_for(plan, {"driver-stops-pwa": "skipped"}), CONFIG)
        self.assertEqual([gate.REASON_REQUIRED_JOB_SKIPPED], reasons(evaluation))

    def test_required_failed(self):
        plan = plan_for(["apps/web/src/driver/a.tsx"])
        evaluation = gate.evaluate(None, needs_for(plan, {"web": "failure"}), CONFIG)
        self.assertEqual([gate.REASON_REQUIRED_JOB_FAILED], reasons(evaluation))

    def test_required_cancelled(self):
        plan = plan_for(["src/a.cs"])
        evaluation = gate.evaluate(None, needs_for(plan, {"backup-restore": "cancelled"}), CONFIG)
        self.assertEqual([gate.REASON_REQUIRED_JOB_FAILED], reasons(evaluation))

    def test_full_plan_rel000_skipped_is_required_skipped(self):
        plan = plan_for([FULL_NON_DEPS_PATH])
        evaluation = gate.evaluate(None, needs_for(plan, {"rel000": "skipped"}), CONFIG)
        self.assertEqual([gate.REASON_REQUIRED_JOB_SKIPPED], reasons(evaluation))

    def test_non_required_failure(self):
        plan = plan_for(["docs/adr/x.md"])
        evaluation = gate.evaluate(None, needs_for(plan, {"dotnet": "failure"}), CONFIG)
        self.assertEqual([gate.REASON_UNEXPECTED_JOB_RESULT], reasons(evaluation))

    def test_non_required_cancelled(self):
        plan = plan_for(["docs/adr/x.md"])
        evaluation = gate.evaluate(None, needs_for(plan, {"dotnet": "cancelled"}), CONFIG)
        self.assertEqual([gate.REASON_UNEXPECTED_JOB_RESULT], reasons(evaluation))

    def test_non_required_success_contradicts_plan(self):
        plan = plan_for(["docs/adr/x.md"])
        evaluation = gate.evaluate(None, needs_for(plan, {"dotnet": "success"}), CONFIG)
        self.assertEqual([gate.REASON_UNEXPECTED_JOB_RESULT], reasons(evaluation))

    def test_missing_job(self):
        plan = plan_for(["docs/adr/x.md"])
        evaluation = gate.evaluate(None, needs_for(plan, {"infrastructure": None}), CONFIG)
        self.assertEqual("FAIL", evaluation["verdict"])
        self.assertIn(gate.REASON_UNEXPECTED_JOB_RESULT, reasons(evaluation))
        self.assertEqual(len(ALL_JOBS) - 1, len(evaluation["matrix"]))

    def test_unexpected_job_id(self):
        plan = plan_for(["docs/adr/x.md"])
        evaluation = gate.evaluate(None, needs_for(plan, {"mystery-job": "success"}), CONFIG)
        self.assertIn(gate.REASON_UNEXPECTED_JOB_RESULT, reasons(evaluation))

    def test_unknown_result_string(self):
        plan = plan_for(["docs/adr/x.md"])
        evaluation = gate.evaluate(None, needs_for(plan, {"web": "neutral"}), CONFIG)
        self.assertEqual([gate.REASON_UNEXPECTED_JOB_RESULT], reasons(evaluation))

    def test_multiple_failures_are_all_reported(self):
        plan = plan_for(["apps/web/src/driver/a.tsx"])
        evaluation = gate.evaluate(
            None, needs_for(plan, {"web": "failure", "driver-stops-pwa": "skipped", "dotnet": "success"}), CONFIG
        )
        self.assertEqual(
            {gate.REASON_REQUIRED_JOB_FAILED, gate.REASON_REQUIRED_JOB_SKIPPED, gate.REASON_UNEXPECTED_JOB_RESULT},
            set(reasons(evaluation)),
        )


class DependencyPolicyTests(unittest.TestCase):
    """Owner policy: dependency drift enters development only as a certified MAIN_BACKSYNC."""

    def test_config_declares_the_dependency_domain_as_full(self):
        domain = next(item for item in CONFIG["domains"] if item["name"] == gate.DEPENDENCY_DOMAIN)
        self.assertTrue(domain["full"])

    def test_ordinary_dependency_change_is_rejected_even_when_every_job_succeeded(self):
        for path in ("global.json", "apps/web/package.json", "apps/web/pnpm-lock.yaml", "Directory.Packages.props", ".nvmrc"):
            with self.subTest(path=path):
                plan = plan_for([path])
                self.assertEqual("FULL", plan["classification"])
                self.assertEqual(["DEPS"], plan["domains"])
                evaluation = gate.evaluate(None, needs_for(plan), CONFIG)
                self.assertEqual("FAIL", evaluation["verdict"])
                self.assertEqual([gate.REASON_DEPENDENCY_CHANGE_NOT_ALLOWED], reasons(evaluation))
                self.assertEqual("FULL", evaluation["failures"][0]["classification"])

    def test_unmatched_path_is_full_but_not_a_dependency_rejection(self):
        plan = plan_for(["mystery/unknown.bin"])
        self.assertEqual("FULL", plan["classification"])
        self.assertIn("FULL:UNMATCHED_PATHS", plan["reasons"])
        self.assertEqual("PASS", gate.evaluate(None, needs_for(plan), CONFIG)["verdict"])

    def test_authorized_security_remediation_branch_is_still_rejected_into_development(self):
        for branch in SECURITY_REMEDIATION_BRANCHES:
            with self.subTest(branch=branch):
                plan = plan_for(["apps/web/package.json", "apps/web/pnpm-lock.yaml"], head_ref=branch)
                self.assertEqual("FULL", plan["classification"])
                evaluation = gate.evaluate(None, needs_for(plan), CONFIG)
                self.assertEqual([gate.REASON_DEPENDENCY_CHANGE_NOT_ALLOWED], reasons(evaluation))

    def test_full_ci_label_does_not_launder_a_dependency_change(self):
        plan = plan_for(["apps/web/package.json"], labels=["full-ci"])
        self.assertEqual("FULL", plan["classification"])
        evaluation = gate.evaluate(None, needs_for(plan), CONFIG)
        self.assertEqual([gate.REASON_DEPENDENCY_CHANGE_NOT_ALLOWED], reasons(evaluation))

    def test_dependency_change_mixed_with_selective_paths_is_rejected(self):
        plan = plan_for(["apps/web/src/driver/a.tsx", "apps/web/package.json"])
        self.assertEqual("FULL", plan["classification"])
        evaluation = gate.evaluate(None, needs_for(plan), CONFIG)
        self.assertEqual([gate.REASON_DEPENDENCY_CHANGE_NOT_ALLOWED], reasons(evaluation))

    def test_uncertified_main_head_with_dependency_drift_is_rejected(self):
        plan = plan_for(["apps/web/package.json"], head_ref="main")
        self.assertEqual("FULL", plan["classification"])
        evaluation = gate.evaluate(None, needs_for(plan), CONFIG)
        self.assertEqual([gate.REASON_DEPENDENCY_CHANGE_NOT_ALLOWED], reasons(evaluation))

    def test_certified_main_backsync_with_dependency_drift_may_pass(self):
        plan = plan_for(["apps/web/package.json", "global.json"], head_ref="main", certified_main_sha=CERTIFIED)
        self.assertEqual("MAIN_BACKSYNC", plan["classification"])
        self.assertIn("DEPS", plan["domains"])
        evaluation = gate.evaluate(None, needs_for(plan), CONFIG)
        self.assertEqual("PASS", evaluation["verdict"], evaluation["failures"])

    def test_certified_main_backsync_still_requires_its_jobs(self):
        plan = plan_for(["apps/web/package.json"], head_ref="main", certified_main_sha=CERTIFIED)
        evaluation = gate.evaluate(None, needs_for(plan, {"dotnet": "failure"}), CONFIG)
        self.assertEqual([gate.REASON_REQUIRED_JOB_FAILED], reasons(evaluation))

    def test_dependency_rejection_is_reported_alongside_job_failures(self):
        plan = plan_for(["global.json"])
        evaluation = gate.evaluate(None, needs_for(plan, {"web": "failure"}), CONFIG)
        self.assertEqual(
            [gate.REASON_DEPENDENCY_CHANGE_NOT_ALLOWED, gate.REASON_REQUIRED_JOB_FAILED], reasons(evaluation)
        )

    def test_dependency_rejection_is_printed_and_fails_the_cli(self):
        plan = plan_for(["global.json"])
        text = gate.format_matrix(gate.evaluate(None, needs_for(plan), CONFIG))
        self.assertIn("domains: DEPS", text)
        self.assertIn("PR_GATE_DEPENDENCY_CHANGE_NOT_ALLOWED", text)
        self.assertIn("PR Gate: FAIL", text)
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(1, gate.main(["--needs-json", json.dumps(needs_for(plan))]))


class FormattingAndCliTests(unittest.TestCase):
    def test_matrix_lists_required_and_actual(self):
        plan = plan_for(["apps/web/src/driver/a.tsx"])
        text = gate.format_matrix(gate.evaluate(None, needs_for(plan, {"web": "failure"}), CONFIG))
        self.assertIn("classification: SELECTIVE", text)
        self.assertIn("domains: WEB_DRIVER", text)
        self.assertIn("PR_GATE_REQUIRED_JOB_FAILED", text)
        self.assertIn("PR Gate: FAIL", text)
        self.assertRegex(text, r"web\s+yes\s+failure\s+PR_GATE_REQUIRED_JOB_FAILED")
        self.assertRegex(text, r"dotnet\s+no\s+skipped\s+OK")

    def test_cli_pass_and_summary(self):
        plan = plan_for(["docs/adr/x.md"])
        with tempfile.TemporaryDirectory() as tmp:
            summary = Path(tmp) / "summary.md"
            with contextlib.redirect_stdout(io.StringIO()):
                code = gate.main(["--needs-json", json.dumps(needs_for(plan)), "--summary", str(summary)])
            self.assertEqual(0, code)
            self.assertIn("PR Gate: PASS", summary.read_text(encoding="utf-8"))

    def test_cli_fail_exit_code(self):
        plan = plan_for(["docs/adr/x.md"])
        with contextlib.redirect_stdout(io.StringIO()):
            code = gate.main(["--needs-json", json.dumps(needs_for(plan, {"secret-scan": "failure"}))])
        self.assertEqual(1, code)

    def test_cli_unreadable_needs(self):
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, gate.main(["--needs-json", "{broken"]))


if __name__ == "__main__":
    unittest.main()
