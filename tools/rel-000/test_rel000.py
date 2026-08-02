#!/usr/bin/env python3
"""Focused false-green tests for the REL-000 validator."""

from __future__ import annotations

import copy
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
import io
from pathlib import Path
from unittest.mock import patch


MODULE_PATH = Path(__file__).with_name("rel000.py")
SPEC = importlib.util.spec_from_file_location("rel000", MODULE_PATH)
assert SPEC and SPEC.loader
rel000 = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = rel000
SPEC.loader.exec_module(rel000)

RUNNER_PATH = Path(__file__).with_name("run_focused_tests.py")
RUNNER_SPEC = importlib.util.spec_from_file_location("run_focused_tests", RUNNER_PATH)
assert RUNNER_SPEC and RUNNER_SPEC.loader
focused_runner = importlib.util.module_from_spec(RUNNER_SPEC)
RUNNER_SPEC.loader.exec_module(focused_runner)


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]


class Rel000FocusedTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-tests-")
        self.root = Path(self.temp.name)
        normative = rel000.load_normative(REPOSITORY_ROOT)
        selected, by_id = rel000.normative_items(normative)
        self.selected = copy.deepcopy(selected)
        self.by_id = copy.deepcopy(by_id)
        self.all_items = list(self.by_id.values())
        self.gates = copy.deepcopy(normative["gates"])
        self.item_evidence = {
            "items": [
                {
                    "id": item_id,
                    "implementation_status": "BLOCKED",
                    "implementation_commits_or_prs": [],
                    "implementation_paths": [],
                    "required_test_sources": [],
                    "authoritative_ci_jobs": [],
                    "rollback_reference": None,
                    "known_limitations": [],
                    "open_gates": ["Issue #5"] if item_id == "REL-000" else [],
                }
                for item_id in [item["id"] for item in self.selected]
            ]
        }
        self.job_results = {job: "success" for job in rel000.REQUIRED_JOBS}
        self.execution_results = []

    def tearDown(self) -> None:
        self.temp.cleanup()

    def assert_reason(self, expected: str, action) -> rel000.ValidationFailure:
        with self.assertRaises(rel000.ValidationFailure) as raised:
            action()
        self.assertEqual(expected, raised.exception.reason_code)
        self.assertEqual(
            "REL000_TECHNICAL_VALIDATION_FAILED",
            raised.exception.as_dict()["result"],
        )
        return raised.exception

    def validate_items(self, evidence=None, selected=None, by_id=None):
        return rel000.validate_item_evidence(
            self.root,
            selected or self.selected,
            by_id or self.by_id,
            evidence or self.item_evidence,
            "a" * 40,
            self.job_results,
            self.execution_results,
            ancestor_checker=lambda *_: True,
        )

    def normative_with_fin001(self, **changes):
        items = copy.deepcopy(self.all_items)
        fin001 = next(item for item in items if item["id"] == "FIN-001")
        fin001.update(changes)
        return {"backlog": {"items": items}}

    @staticmethod
    def execution_result(
        *,
        job="dotnet",
        project="tests/example.csproj",
        name="Example.Tests.required",
        category="Example",
        outcome="PASSED",
        executed=True,
        skipped=False,
        run_id="100",
    ):
        return {
            "job": job,
            "test_project": project,
            "fully_qualified_test_name": name,
            "category": category,
            "outcome": outcome,
            "executed": executed,
            "skipped": skipped,
            "duration": "00:00:00.100",
            "trx_or_result_digest": "1" * 64,
            "artifact_id": 1,
            "artifact_name": f"rel000-execution-{job}",
            "artifact_digest": "2" * 64,
            "content_digest": "3" * 64,
            "source_head_sha": "b" * 40,
            "tested_git_sha": "b" * 40,
            "base_main_sha": "a" * 40,
            "workflow_run_id": run_id,
            "workflow_run_attempt": "1",
        }

    def make_audit(self):
        high = {
            "GHSA-6gpp-xcg3-4w24",
            "GHSA-89xv-2m56-2m9x",
            "GHSA-f88m-g3jw-g9cj",
            "GHSA-m99w-x7hq-7vfj",
            "GHSA-mh99-v99m-4gvg",
            "GHSA-p9j2-gv94-2wf4",
        }
        advisories = []
        for advisory_id in sorted(rel000.EXPECTED_BASE_ADVISORIES):
            package = (
                "sharp"
                if advisory_id == rel000.ISSUE5_ADVISORY
                else "brace-expansion"
                if advisory_id == "GHSA-mh99-v99m-4gvg"
                else "next"
            )
            versions = (
                ["0.34.5"]
                if package == "sharp"
                else ["1.1.16", "5.0.7"]
                if package == "brace-expansion"
                else ["16.2.10"]
            )
            advisories.append(
                {
                    "advisory_id": advisory_id,
                    "package": package,
                    "installed_versions": versions,
                    "severity": "high" if advisory_id in high else "moderate",
                    "affected_range": "<=affected",
                    "patched_range": ">=patched",
                    "direct_or_transitive": "direct" if package == "next" else "transitive",
                    "dependency_path_count": 1,
                    "fix_available": True,
                    "fix_compatibility": (
                        "compatible_patch_available"
                        if package == "next"
                        else "requires_compatibility_assessment"
                    ),
                }
            )
        return {
            "command_executed": True,
            "command_exit_code": 1,
            "parse_succeeded": True,
            "totals": copy.deepcopy(rel000.EXPECTED_BASE_AUDIT_TOTALS),
            "advisories": advisories,
        }

    def make_security_inputs(self):
        issue5 = {
            "number": 5,
            "state": "OPEN",
            "title": "sharp",
            "url": "https://github.com/example/issues/5",
            "tracked_advisory_ids": [rel000.ISSUE5_ADVISORY],
        }
        additional = {
            "number": 30,
            "state": "OPEN",
            "title": rel000.ADDITIONAL_SECURITY_ISSUE_TITLE,
            "url": "https://github.com/example/issues/30",
            "tracked_advisory_ids": sorted(
                rel000.EXPECTED_BASE_ADVISORIES - {rel000.ISSUE5_ADVISORY}
            ),
        }
        clean = {
            "dependency_manifest_changed": False,
            "dependency_lockfile_changed": False,
            "dependency_diff_against_base": "CLEAN",
        }
        return issue5, additional, self.make_audit(), self.make_audit(), clean

    def make_remediation_inputs(
        self,
        remaining_ids=None,
        *,
        issue5_state="OPEN",
        issue30_state="OPEN",
    ):
        issue5, issue30, base, _, _ = self.make_security_inputs()
        issue5["state"] = issue5_state
        issue30["state"] = issue30_state
        remaining = {
            value.lower()
            for value in (
                remaining_ids
                if remaining_ids is not None
                else {rel000.ISSUE5_ADVISORY}
            )
        }
        branch_advisories = [
            copy.deepcopy(item)
            for item in base["advisories"]
            if item["advisory_id"].lower() in remaining
        ]
        totals = {
            severity: sum(item["severity"] == severity for item in branch_advisories)
            for severity in ("critical", "high", "moderate", "low")
        }
        totals["total"] = len(branch_advisories)
        branch = {
            "command_executed": True,
            "command_exit_code": 1 if branch_advisories else 0,
            "parse_succeeded": True,
            "totals": totals,
            "advisories": branch_advisories,
        }
        dependency_diff = {
            "dependency_manifest_changed": True,
            "dependency_lockfile_changed": True,
            "dependency_workspace_changed": True,
            "dependency_diff_against_base": "AUTHORIZED_SECURITY_REMEDIATION",
            "changed_dependency_files": sorted(rel000.SECURITY_REMEDIATION_DEPENDENCY_FILES),
            "lockfile_consistency_verified": True,
            "vulnerable_lock_versions": (
                ["sharp@0.34.5"]
                if rel000.ISSUE5_ADVISORY.lower() in remaining
                else []
            ),
        }
        return issue5, issue30, base, branch, dependency_diff

    def make_dependency_repo(self):
        root = self.root / "dependency-repo"
        web = root / "apps/web"
        web.mkdir(parents=True)
        package = {
            "name": "@paquetenvia/web",
            "private": True,
            "dependencies": {
                "next": "16.2.10",
                "react": "19.2.7",
                "react-dom": "19.2.7",
            },
            "devDependencies": {
                "eslint": "9.39.5",
                "eslint-config-next": "16.2.10",
            },
        }
        (web / "package.json").write_text(json.dumps(package, indent=2) + "\n", encoding="utf-8")
        (web / "pnpm-workspace.yaml").write_text(
            "overrides:\n  postcss: 8.5.21\n", encoding="utf-8"
        )
        (web / "pnpm-lock.yaml").write_text(
            "lockfileVersion: '9.0'\n"
            "importers:\n  .:\n    dependencies:\n      next:\n"
            "        specifier: 16.2.10\n        version: 16.2.10\n"
            "    devDependencies:\n      eslint-config-next:\n"
            "        specifier: 16.2.10\n        version: 16.2.10\n"
            "packages:\n  next@16.2.10:\n    resolution: {}\n"
            "  eslint-config-next@16.2.10:\n    resolution: {}\n"
            "  brace-expansion@1.1.16:\n    resolution: {}\n"
            "  brace-expansion@5.0.7:\n    resolution: {}\n"
            "  sharp@0.34.5:\n    resolution: {}\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "init", "-q"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.email", "rel000@example.invalid"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.name", "REL-000 Tests"], cwd=root, check=True)
        subprocess.run(["git", "add", "."], cwd=root, check=True)
        subprocess.run(["git", "commit", "-qm", "base"], cwd=root, check=True)
        base = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
        package["dependencies"]["next"] = "16.2.11"
        package["devDependencies"]["eslint-config-next"] = "16.2.11"
        (web / "package.json").write_text(json.dumps(package, indent=2) + "\n", encoding="utf-8")
        (web / "pnpm-workspace.yaml").write_text(
            "overrides:\n"
            "  postcss: 8.5.21\n"
            "  \"brace-expansion@<1.1.17\": 1.1.17\n"
            "  \"brace-expansion@>=4.0.0 <5.0.8\": 5.0.8\n",
            encoding="utf-8",
        )
        (web / "pnpm-lock.yaml").write_text(
            "lockfileVersion: '9.0'\n"
            "importers:\n  .:\n    dependencies:\n      next:\n"
            "        specifier: 16.2.11\n        version: 16.2.11\n"
            "    devDependencies:\n      eslint-config-next:\n"
            "        specifier: 16.2.11\n        version: 16.2.11\n"
            "packages:\n  next@16.2.11:\n    resolution: {}\n"
            "  eslint-config-next@16.2.11:\n    resolution: {}\n"
            "  brace-expansion@1.1.17:\n    resolution: {}\n"
            "  brace-expansion@5.0.8:\n    resolution: {}\n"
            "  sharp@0.34.5:\n    resolution: {}\n",
            encoding="utf-8",
        )
        return root, base

    def make_cross_tenant(self):
        source = self.root / "cross-tenant-tests.txt"
        source.write_text(
            "\n".join(sorted(rel000.REQUIRED_CROSS_TENANT_CATEGORIES)),
            encoding="utf-8",
        )
        manifest = {
            "sources": [
                {
                    "evidence_id": evidence_id,
                    "job": "dotnet",
                    "test_project": "tests/example.csproj",
                    "fully_qualified_test_name": f"Example.Tests.{evidence_id}",
                    "category": evidence_id,
                    "expected_presence": True,
                    "source_path": source.name,
                    "source_match": evidence_id,
                }
                for evidence_id in sorted(rel000.REQUIRED_CROSS_TENANT_CATEGORIES)
            ]
        }
        self.execution_results = [
            self.execution_result(
                name=source["fully_qualified_test_name"],
                category=source["category"],
            )
            for source in manifest["sources"]
        ]
        return manifest

    def make_verified_item(self):
        for name in ("implementation.txt", "required-test.txt", "rollback.md"):
            (self.root / name).write_text(name, encoding="utf-8")
        evidence = copy.deepcopy(self.item_evidence)
        item = next(entry for entry in evidence["items"] if entry["id"] == "FND-001")
        item.update(
            {
                "implementation_status": "VERIFIED",
                "implementation_commits_or_prs": ["a" * 40],
                "implementation_paths": ["implementation.txt"],
                "required_test_sources": ["required-test.txt"],
                "authoritative_ci_jobs": ["dotnet"],
                "required_tests": [
                    {
                        "source_path": "required-test.txt",
                        "job": "dotnet",
                        "test_project": "tests/example.csproj",
                        "fully_qualified_test_name": "Example.Tests.required",
                        "category": "Example",
                    }
                ],
                "rollback_reference": "rollback.md#fnd-001",
            }
        )
        self.execution_results = [self.execution_result()]
        return evidence, item

    def make_ops001(self) -> Path:
        directory = self.root / "ops001"
        directory.mkdir()
        report = {
            "orders_planned": 20,
            "orders_created": 20,
            "orders_delivered": 20,
            "assignments_created": 20,
            "pickup_proofs_expected": 20,
            "pickup_proofs_completed": 20,
            "delivery_proofs_expected": 20,
            "delivery_proofs_completed": 20,
            "domain_events_expected": 180,
            "domain_events_persisted": 180,
            "missing_aggregate_versions": 0,
            "realtime_events_expected": 160,
            "realtime_events_matched": 160,
            "realtime_events_missing": 0,
            "realtime_events_unexpected": 0,
            "realtime_events_mismatched": 0,
            "realtime_observation_percent": 100,
            "audits_expected": 340,
            "audits_exactly_matched": 340,
            "audits_missing": 0,
            "audits_duplicated": 0,
            "audits_mismatched": 0,
            "secondary_tenant_rows": 0,
            "outbox_dead_expected": 1,
            "outbox_dead_actual": 1,
            "stale_recoveries": 1,
            "stale_lease_rejections": 1,
            "newer_message_processed_after_poison": True,
        }
        rel000.write_json(directory / "ops001-delivery-simulation.json", report)
        (directory / "delivery-simulation.trx").write_text(
            """<?xml version="1.0" encoding="utf-8"?>
<TestRun><Results>
<UnitTestResult testName="Two_consecutive_runs_deliver_twenty_orders_with_worker_recovery" outcome="Passed" />
<UnitTestResult testName="Cancellation_of_real_runner_releases_owned_resources_and_allows_next_run" outcome="Passed" />
</Results></TestRun>
""",
            encoding="utf-8",
        )
        return directory

    @staticmethod
    def passing_rollback_execution():
        return {
            "rollback_scenarios_expected": 14,
            "rollback_scenarios_executed": 14,
            "rollback_scenarios_passed": 14,
            "rollback_scenarios_failed": 0,
            "rollback_cleanup_verified": True,
            "foreign_targets_preserved": True,
            "successful_report_after_failure": False,
        }

    @staticmethod
    def append_only(prefix=""):
        return {
            f"{prefix}tables_expected": 4,
            f"{prefix}permission_checks_verified": 4,
            f"{prefix}triggers_verified": 4,
            f"{prefix}update_guards_verified": 4,
            f"{prefix}delete_guards_verified": 4,
            f"{prefix}trigger_failures_verified": 8,
            f"{prefix}permission_failures": 0,
            f"{prefix}rows_intact_verified": 8,
            f"{prefix}contract_sqlstate": "42501",
            f"{prefix}contract_message": "qualified_table_is_append_only",
        }

    def make_ops002(self) -> Path:
        directory = self.root / "ops002"
        directory.mkdir()
        report = {
            "result": "RESTORE_DRILL_PASSED",
            "observed_data_loss": 0,
            "current_guaranteed_rpo": "NOT_ESTABLISHED",
            "source_destroyed_before_restore": True,
            "target_was_clean": True,
            "baseline_assertions_passed": True,
            "module_migrations_asserted": True,
            "rls_assertions_passed": True,
            "append_only_assertions_passed": True,
            "object_integrity_passed": True,
            "plaintext_residue_detected": False,
            "redis_restored": False,
            "mailpit_restored": False,
            **self.append_only("append_only_"),
            "append_only_before_restart": self.append_only(),
            "append_only_after_restart": self.append_only(),
        }
        negative = {
            "required_checks": 70,
            "passed_checks": 70,
            "checks": [{"name": f"check_{index}", "passed": True} for index in range(70)],
        }
        rel000.write_json(directory / "ops002-restore-drill-report.json", report)
        rel000.write_json(directory / "ops002-negative-tests.json", negative)
        (directory / "backup.tar.gz.age").write_bytes(b"encrypted")
        return directory

    def make_artifact(self, run_id="100", tested="b" * 40):
        directory = self.root / f"artifact-{len(list(self.root.iterdir()))}"
        directory.mkdir()
        (directory / "data.json").write_text("{}\n", encoding="utf-8")
        trace = {
            "source_head_sha": tested,
            "tested_git_sha": tested,
            "base_main_sha": "a" * 40,
            "git_relationship": "same_commit",
        }
        rel000.create_provenance(
            directory,
            "artifact-name",
            run_id,
            "1",
            **trace,
        )
        return directory, trace

    # 1-8: exact P0 inventory, FIN-001 classification and evidence.
    def test_01_rel000_omitted(self):
        evidence = copy.deepcopy(self.item_evidence)
        evidence["items"] = [item for item in evidence["items"] if item["id"] != "REL-000"]
        self.assert_reason("P0_ITEM_MISSING", lambda: self.validate_items(evidence))

    def test_02_duplicate_p0_item(self):
        evidence = copy.deepcopy(self.item_evidence)
        evidence["items"].append(copy.deepcopy(evidence["items"][0]))
        self.assert_reason("P0_ITEM_DUPLICATED", lambda: self.validate_items(evidence))

    def test_03_unknown_p0_item(self):
        evidence = copy.deepcopy(self.item_evidence)
        evidence["items"][0]["id"] = "UNKNOWN-001"
        self.assert_reason("P0_ITEM_UNKNOWN", lambda: self.validate_items(evidence))

    def test_04_fin001_in_mvp0_fails(self):
        self.assert_reason(
            "FIN001_RELEASE_CLASSIFICATION_INVALID",
            lambda: rel000.normative_items(self.normative_with_fin001(release="MVP-0")),
        )

    def test_04b_fin001_in_other_release_fails(self):
        self.assert_reason(
            "FIN001_RELEASE_CLASSIFICATION_INVALID",
            lambda: rel000.normative_items(self.normative_with_fin001(release="MVP-2")),
        )

    def test_04c_fin001_without_p0_priority_fails(self):
        self.assert_reason(
            "FIN001_PRIORITY_INVALID",
            lambda: rel000.normative_items(self.normative_with_fin001(priority="P1")),
        )

    def test_04d_fin001_without_ext001_fails(self):
        self.assert_reason(
            "FIN001_DEPENDENCY_SET_INVALID",
            lambda: rel000.normative_items(
                self.normative_with_fin001(depends_on=["DSP-002", "RTE-001"])
            ),
        )

    def test_04e_fin001_without_rte001_fails(self):
        self.assert_reason(
            "FIN001_DEPENDENCY_SET_INVALID",
            lambda: rel000.normative_items(
                self.normative_with_fin001(depends_on=["DSP-002", "EXT-001"])
            ),
        )

    def test_04f_fin001_without_dsp002_fails(self):
        self.assert_reason(
            "FIN001_DEPENDENCY_SET_INVALID",
            lambda: rel000.normative_items(
                self.normative_with_fin001(depends_on=["EXT-001", "RTE-001"])
            ),
        )

    def test_04g_fin001_with_unexpected_dependency_fails(self):
        self.assert_reason(
            "FIN001_DEPENDENCY_SET_INVALID",
            lambda: rel000.normative_items(
                self.normative_with_fin001(
                    depends_on=["DSP-002", "EXT-001", "RTE-001", "REL-000"]
                )
            ),
        )

    def test_05_fin001_in_mvp0_manifest_is_unknown(self):
        evidence = copy.deepcopy(self.item_evidence)
        evidence["items"].append(
            {
                "id": "FIN-001",
                "implementation_status": "BLOCKED",
                "implementation_commits_or_prs": [],
                "implementation_paths": [],
                "required_test_sources": [],
                "authoritative_ci_jobs": [],
                "rollback_reference": None,
                "known_limitations": [],
                "open_gates": [],
            }
        )
        self.assert_reason("P0_ITEM_UNKNOWN", lambda: self.validate_items(evidence))

    def test_05b_inventory_with_30_rows_fails(self):
        evidence = copy.deepcopy(self.item_evidence)
        evidence["items"].append(copy.deepcopy(evidence["items"][0]))
        self.assert_reason("P0_ITEM_DUPLICATED", lambda: self.validate_items(evidence))

    def test_05c_inventory_with_28_rows_fails(self):
        evidence = copy.deepcopy(self.item_evidence)
        evidence["items"].pop()
        self.assert_reason("P0_ITEM_MISSING", lambda: self.validate_items(evidence))

    def test_05d_exact_29_item_inventory_passes(self):
        result = self.validate_items(copy.deepcopy(self.item_evidence))
        self.assertEqual(29, result["mvp0_p0_items_expected"])
        self.assertEqual(29, result["mvp0_p0_items_evaluated"])
        self.assertNotIn("FIN-001", [item["id"] for item in result["items"]])

    def test_05e_rel000_falsely_verified_fails(self):
        evidence = copy.deepcopy(self.item_evidence)
        next(item for item in evidence["items"] if item["id"] == "REL-000")[
            "implementation_status"
        ] = "VERIFIED"
        self.assert_reason("OWNER_APPROVAL_FALSELY_ASSERTED", lambda: self.validate_items(evidence))

    def test_06_dependency_missing(self):
        normative = {"backlog": {"items": copy.deepcopy(self.all_items)}}
        normative["backlog"]["items"][0]["depends_on"] = ["MISSING-001"]
        self.assert_reason("DEPENDENCY_NOT_FOUND", lambda: rel000.normative_items(normative))

    def test_07_evidence_path_missing(self):
        evidence = copy.deepcopy(self.item_evidence)
        evidence["items"][0]["implementation_paths"] = ["missing/implementation.cs"]
        self.assert_reason("EVIDENCE_PATH_NOT_FOUND", lambda: self.validate_items(evidence))

    def test_08_test_source_missing(self):
        evidence = copy.deepcopy(self.item_evidence)
        evidence["items"][0]["required_test_sources"] = ["missing/test.cs"]
        self.assert_reason("TEST_SOURCE_NOT_FOUND", lambda: self.validate_items(evidence))

    # 9-12: rollback and cross-tenant sources.
    def test_09_rollback_missing(self):
        self.assert_reason(
            "ROLLBACK_MISSING",
            lambda: rel000.validate_rollback(
                self.root,
                {"items": []},
                self.selected,
                self.passing_rollback_execution(),
            ),
        )

    def test_09b_fin001_rollback_row_is_unknown(self):
        self.assert_reason(
            "ROLLBACK_ITEM_UNKNOWN",
            lambda: rel000.validate_rollback(
                self.root,
                {"items": [{"owning_backlog_item": "FIN-001"}]},
                self.selected,
                self.passing_rollback_execution(),
            ),
        )

    def test_10_cross_tenant_category_missing(self):
        manifest = self.make_cross_tenant()
        manifest["sources"].pop()
        self.assert_reason(
            "CROSS_TENANT_CATEGORY_MISSING",
            lambda: rel000.validate_cross_tenant(
                self.root, manifest, self.job_results, self.execution_results
            ),
        )

    def test_11_cross_tenant_category_failed(self):
        manifest = self.make_cross_tenant()
        jobs = copy.deepcopy(self.job_results)
        jobs["dotnet"] = "failure"
        self.assert_reason(
            "CROSS_TENANT_SOURCE_FAILED",
            lambda: rel000.validate_cross_tenant(
                self.root, manifest, jobs, self.execution_results
            ),
        )

    def test_12_cross_tenant_required_skipped(self):
        manifest = self.make_cross_tenant()
        jobs = copy.deepcopy(self.job_results)
        jobs["dotnet"] = "skipped"
        self.assert_reason(
            "CROSS_TENANT_REQUIRED_SKIPPED",
            lambda: rel000.validate_cross_tenant(
                self.root, manifest, jobs, self.execution_results
            ),
        )

    def test_50_cross_tenant_job_success_required_test_missing(self):
        manifest = self.make_cross_tenant()
        self.execution_results.pop()
        self.assert_reason(
            "REQUIRED_TEST_RESULT_MISSING",
            lambda: rel000.validate_cross_tenant(
                self.root, manifest, self.job_results, self.execution_results
            ),
        )

    def test_51_cross_tenant_job_success_required_test_skipped(self):
        manifest = self.make_cross_tenant()
        self.execution_results[0].update(
            outcome="NOT_EXECUTED", executed=False, skipped=True
        )
        self.assert_reason(
            "REQUIRED_TEST_SKIPPED",
            lambda: rel000.validate_cross_tenant(
                self.root, manifest, self.job_results, self.execution_results
            ),
        )

    def test_52_cross_tenant_job_success_wrong_category(self):
        manifest = self.make_cross_tenant()
        manifest["sources"][0]["category"] = "WrongCategory"
        self.assert_reason(
            "REQUIRED_TEST_RESULT_MISSING",
            lambda: rel000.validate_cross_tenant(
                self.root, manifest, self.job_results, self.execution_results
            ),
        )

    def test_53_cross_tenant_job_success_wrong_project(self):
        manifest = self.make_cross_tenant()
        manifest["sources"][0]["test_project"] = "tests/wrong.csproj"
        self.assert_reason(
            "REQUIRED_TEST_RESULT_MISSING",
            lambda: rel000.validate_cross_tenant(
                self.root, manifest, self.job_results, self.execution_results
            ),
        )

    def test_54_cross_tenant_duplicate_contradictory_result(self):
        manifest = self.make_cross_tenant()
        contradictory = copy.deepcopy(self.execution_results[0])
        contradictory["outcome"] = "FAILED"
        self.execution_results.append(contradictory)
        self.assert_reason(
            "EXECUTION_RESULT_CONTRADICTORY",
            lambda: rel000.validate_cross_tenant(
                self.root, manifest, self.job_results, self.execution_results
            ),
        )

    def test_54b_cross_tenant_stale_trx_context(self):
        result = self.execution_result(run_id="99")
        self.assert_reason(
            "EXECUTION_RESULT_RUN_MISMATCH",
            lambda: rel000.validate_execution_context(
                result,
                {"source_head_sha": "b" * 40, "tested_git_sha": "b" * 40},
                "100",
                "1",
            ),
        )

    def test_55_verified_item_unknown_authoritative_job(self):
        evidence, item = self.make_verified_item()
        item["authoritative_ci_jobs"] = ["imaginary-job"]
        self.assert_reason("AUTHORITATIVE_JOB_UNKNOWN", lambda: self.validate_items(evidence))

    def test_56_verified_item_successful_unrelated_job(self):
        evidence, item = self.make_verified_item()
        item["authoritative_ci_jobs"].append("web")
        self.assert_reason(
            "AUTHORITATIVE_JOB_UNRELATED", lambda: self.validate_items(evidence)
        )

    def test_57_verified_item_required_test_absent(self):
        evidence, _ = self.make_verified_item()
        self.execution_results.clear()
        self.assert_reason(
            "REQUIRED_TEST_RESULT_MISSING", lambda: self.validate_items(evidence)
        )

    def test_58_verified_item_required_test_skipped(self):
        evidence, _ = self.make_verified_item()
        self.execution_results[0].update(
            outcome="NOT_EXECUTED", executed=False, skipped=True
        )
        self.assert_reason("REQUIRED_TEST_SKIPPED", lambda: self.validate_items(evidence))

    def test_59_verified_item_required_test_wrong_project(self):
        evidence, item = self.make_verified_item()
        item["required_tests"][0]["test_project"] = "tests/wrong.csproj"
        self.assert_reason(
            "REQUIRED_TEST_RESULT_MISSING", lambda: self.validate_items(evidence)
        )

    def test_60_verified_item_source_exists_but_test_not_executed(self):
        evidence, _ = self.make_verified_item()
        self.execution_results.clear()
        self.assert_reason(
            "REQUIRED_TEST_RESULT_MISSING", lambda: self.validate_items(evidence)
        )

    def test_60b_verified_item_required_test_from_another_run(self):
        result = self.execution_result(run_id="99")
        self.assert_reason(
            "EXECUTION_RESULT_RUN_MISMATCH",
            lambda: rel000.validate_execution_context(
                result,
                {"source_head_sha": "b" * 40, "tested_git_sha": "b" * 40},
                "100",
                "1",
            ),
        )

    def test_61_focused_runner_rejects_missing_discovered_case(self):
        suite = unittest.TestSuite([unittest.FunctionTestCase(lambda: None)])
        with self.assertRaises(focused_runner.FocusedTestCountMismatch) as raised:
            focused_runner.execute_suite(suite, 2, stream=io.StringIO())
        self.assertEqual(
            "REL000_FOCUSED_TEST_COUNT_MISMATCH",
            raised.exception.reason_code,
        )

    # 13-18: OPS-001 provenance and exact correlation.
    def test_13_ops001_artifact_missing(self):
        self.assert_reason(
            "ARTIFACT_MISSING",
            lambda: rel000.validate_artifact(
                self.root / "absent",
                "artifact-name",
                "1",
                "a" * 64,
                {},
                "1",
                "1",
                lambda _: True,
            ),
        )

    def test_14_ops001_artifact_from_other_sha(self):
        directory, trace = self.make_artifact()
        other_trace = copy.deepcopy(trace)
        other_trace["tested_git_sha"] = "c" * 40
        self.assert_reason(
            "ARTIFACT_SHA_MISMATCH",
            lambda: rel000.validate_artifact(
                directory,
                "artifact-name",
                "1",
                "d" * 64,
                other_trace,
                "100",
                "1",
                lambda path: path.endswith(".json"),
            ),
        )

    def test_15_ops001_metric_missing(self):
        directory = self.make_ops001()
        report = rel000.load_json(directory / "ops001-delivery-simulation.json")
        del report["orders_planned"]
        rel000.write_json(directory / "ops001-delivery-simulation.json", report)
        self.assert_reason("OPS_METRIC_MISSING", lambda: rel000.validate_ops001(directory))

    def test_16_missing_order_not_compensated_by_extra_count(self):
        directory = self.make_ops001()
        report = rel000.load_json(directory / "ops001-delivery-simulation.json")
        report["orders_created"] = 21
        report["orders_delivered"] = 19
        rel000.write_json(directory / "ops001-delivery-simulation.json", report)
        self.assert_reason("OPS001_METRIC_INVALID", lambda: rel000.validate_ops001(directory))

    def test_17_unexpected_realtime_does_not_compensate_missing(self):
        directory = self.make_ops001()
        report = rel000.load_json(directory / "ops001-delivery-simulation.json")
        report["realtime_events_missing"] = 1
        report["realtime_events_unexpected"] = 1
        rel000.write_json(directory / "ops001-delivery-simulation.json", report)
        self.assert_reason("OPS001_REALTIME_MISSING", lambda: rel000.validate_ops001(directory))

    def test_18_duplicate_audit_does_not_compensate_missing(self):
        directory = self.make_ops001()
        report = rel000.load_json(directory / "ops001-delivery-simulation.json")
        report["audits_missing"] = 1
        report["audits_duplicated"] = 1
        rel000.write_json(directory / "ops001-delivery-simulation.json", report)
        self.assert_reason("OPS001_AUDIT_MISSING", lambda: rel000.validate_ops001(directory))

    # 19-24: OPS-002 provenance, restore and append-only.
    def test_19_ops002_artifact_missing(self):
        self.assert_reason(
            "OPS002_REPORT_MISSING",
            lambda: rel000.validate_ops002(self.root / "absent"),
        )

    def test_20_ops002_artifact_from_other_run(self):
        directory, trace = self.make_artifact(run_id="99")
        self.assert_reason(
            "ARTIFACT_RUN_MISMATCH",
            lambda: rel000.validate_artifact(
                directory,
                "artifact-name",
                "1",
                "d" * 64,
                trace,
                "100",
                "1",
                lambda path: path.endswith(".json"),
            ),
        )

    def test_21_ops002_report_tampered(self):
        directory = self.make_ops002()
        report = rel000.load_json(directory / "ops002-restore-drill-report.json")
        report["result"] = "RESTORE_PASSED"
        rel000.write_json(directory / "ops002-restore-drill-report.json", report)
        self.assert_reason("OPS002_REPORT_TAMPERED", lambda: rel000.validate_ops002(directory))

    def test_22_append_only_permissions_insufficient(self):
        directory = self.make_ops002()
        report = rel000.load_json(directory / "ops002-restore-drill-report.json")
        report["append_only_permission_checks_verified"] = 3
        rel000.write_json(directory / "ops002-restore-drill-report.json", report)
        self.assert_reason("OPS002_APPEND_ONLY_INVALID", lambda: rel000.validate_ops002(directory))

    def test_23_restore_without_restart_evidence(self):
        directory = self.make_ops002()
        report = rel000.load_json(directory / "ops002-restore-drill-report.json")
        del report["append_only_after_restart"]
        rel000.write_json(directory / "ops002-restore-drill-report.json", report)
        self.assert_reason(
            "OPS002_RESTART_EVIDENCE_MISSING",
            lambda: rel000.validate_ops002(directory),
        )

    def test_24_plaintext_residue(self):
        directory = self.make_ops002()
        report = rel000.load_json(directory / "ops002-restore-drill-report.json")
        report["plaintext_residue_detected"] = True
        rel000.write_json(directory / "ops002-restore-drill-report.json", report)
        self.assert_reason("OPS002_PLAINTEXT_RESIDUE", lambda: rel000.validate_ops002(directory))

    # 25-29: Git, normative and stale-output traceability.
    def test_25_source_sha_malformed(self):
        self.assert_reason(
            "SOURCE_HEAD_SHA_MALFORMED",
            lambda: rel000.validate_sha("short", "SOURCE_HEAD_SHA_MALFORMED", "source_head_sha"),
        )

    def test_26_tested_sha_differs_from_checkout(self):
        repository = MODULE_PATH.parents[2]
        head = subprocess.check_output(
            ["git", "-C", str(repository), "rev-parse", "HEAD"], text=True
        ).strip()
        parent = subprocess.check_output(
            ["git", "-C", str(repository), "rev-parse", "HEAD^"], text=True
        ).strip()
        self.assert_reason(
            "TESTED_SHA_CHECKOUT_MISMATCH",
            lambda: rel000.validate_traceability(
                repository,
                parent,
                parent,
                parent,
                "same_commit",
            ),
        )
        self.assertNotEqual(head, parent)

    def test_27_source_head_not_ancestor(self):
        repository = MODULE_PATH.parents[2]
        head = subprocess.check_output(
            ["git", "-C", str(repository), "rev-parse", "HEAD"], text=True
        ).strip()
        tree = subprocess.check_output(
            ["git", "-C", str(repository), "rev-parse", "HEAD^{tree}"], text=True
        ).strip()
        environment = os.environ.copy()
        environment.update(
            {
                "GIT_AUTHOR_NAME": "REL000 synthetic guard",
                "GIT_AUTHOR_EMAIL": "rel000.invalid",
                "GIT_COMMITTER_NAME": "REL000 synthetic guard",
                "GIT_COMMITTER_EMAIL": "rel000.invalid",
            }
        )
        descendant = subprocess.check_output(
            [
                "git",
                "-C",
                str(repository),
                "commit-tree",
                tree,
                "-p",
                head,
                "-m",
                "REL000 synthetic non-ancestor",
            ],
            text=True,
            env=environment,
        ).strip()
        self.assert_reason(
            "SOURCE_HEAD_NOT_ANCESTOR",
            lambda: rel000.validate_traceability(
                repository,
                descendant,
                head,
                head,
                "source_head_is_ancestor_of_tested_commit",
            ),
        )

    def test_28_normative_modified(self):
        normative = self.root / "normative"
        normative.mkdir()
        (normative / "contract.txt").write_text("changed", encoding="utf-8")
        (normative / "CHECKSUMS_SHA256.txt").write_text(
            f"{'0' * 64}  contract.txt\n", encoding="utf-8"
        )
        self.assert_reason(
            "NORMATIVE_MODIFIED",
            lambda: rel000.validate_normative_checksums(normative),
        )

    def test_29_partial_report_reused(self):
        output = self.root / "prior-report"
        output.mkdir()
        (output / "rel000-internal-release-report.json").write_text("{}", encoding="utf-8")
        self.assert_reason("STALE_REPORT_REUSED", lambda: rel000.assert_output_is_fresh(output))

    # 30-35: owner state, gates, issue, cancellation.
    def test_30_resolved_scope_does_not_imply_owner_approval(self):
        report = {
            "owner_approval_status": "APPROVED",
            "release_candidate_status": "BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION",
            "dependency_security_status": "BLOCKED",
            "rel000_def_001_status": "RESOLVED",
            "normative_scope_status": "RESOLVED",
            "normative_scope_decision": "FIN001_MOVED_TO_MVP1",
            "audit_tracking_gap_detected": False,
            "audit_tracking_gap_count": 0,
            "technical_evidence_status": "PASSED",
            "result": "REL000_EVIDENCE_GENERATED",
        }
        self.assert_reason(
            "OWNER_APPROVAL_FALSELY_ASSERTED",
            lambda: rel000.validate_owner_state(report),
        )

    def test_31_success_emitted_with_technical_failure(self):
        report = {
            "owner_approval_status": "PENDING",
            "release_candidate_status": "BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION",
            "dependency_security_status": "BLOCKED",
            "rel000_def_001_status": "RESOLVED",
            "normative_scope_status": "RESOLVED",
            "normative_scope_decision": "FIN001_MOVED_TO_MVP1",
            "audit_tracking_gap_detected": False,
            "audit_tracking_gap_count": 0,
            "technical_evidence_status": "FAILED",
            "result": "REL000_EVIDENCE_GENERATED",
        }
        self.assert_reason(
            "SUCCESS_WITH_TECHNICAL_FAILURE",
            lambda: rel000.validate_owner_state(report),
        )

    def test_32_ext001_started(self):
        self.assert_reason(
            "EXT001_STARTED",
            lambda: rel000.validate_extension_not_started(
                self.root, {"ext001_started": True}
            ),
        )

    def test_33_open_gate_removed(self):
        open_ids = sorted(rel000.REQUIRED_OPEN_DECISIONS - {"GATE-017"})
        gates = {
            "open_decisions": [{"id": gate, "severity": "BLOCKER"} for gate in open_ids],
            "resolved_decisions": [{"id": "GATE-002"}],
        }
        self.assert_reason("OPEN_GATE_OMITTED", lambda: rel000.validate_decisions(gates))

    def test_33b_rel000_def001_resolution_is_structurally_valid(self):
        result = rel000.validate_decisions(copy.deepcopy(self.gates))
        self.assertIn(
            "REL-000-DEF-001",
            [decision["id"] for decision in result["resolved_decisions"]],
        )

    def test_33c_rel000_def001_resolution_drift_fails(self):
        gates = copy.deepcopy(self.gates)
        decision = next(
            item for item in gates["resolved_decisions"] if item["id"] == "REL-000-DEF-001"
        )
        decision["ext001_authorized"] = True
        self.assert_reason(
            "REL000_DEF001_DECISION_INVALID",
            lambda: rel000.validate_decisions(gates),
        )

    def test_34_issue5_closed_while_present(self):
        issue, additional, base, branch, clean = self.make_security_inputs()
        issue["state"] = "CLOSED"
        self.assert_reason(
            "SECURITY_ISSUE_CLOSED_WHILE_ADVISORY_PRESENT",
            lambda: rel000.validate_issue_and_audit(
                issue, additional, base, branch, clean
            ),
        )

    def test_35_cancellation_before_publication(self):
        with patch.dict(
            os.environ,
            {
                "REL000_TEST_MODE": "true",
                "REL000_TEST_CANCEL_BEFORE_PUBLISH": "true",
            },
            clear=False,
        ):
            self.assert_reason(
                "GENERATION_CANCELLED",
                rel000.assert_generation_not_cancelled,
            )

    # 36-48: inherited audit comparison, tracking, and blocked release semantics.
    def test_36_exact_inherited_audit_is_reported_as_blocked(self):
        inputs = self.make_security_inputs()
        result = rel000.validate_issue_and_audit(*inputs)
        self.assertEqual(11, result["base_totals"]["total"])
        self.assertEqual(0, result["deltas"]["total"])
        self.assertEqual(11, len(result["dependency_advisories"]))

    def test_37_branch_total_increases(self):
        issue5, additional, base, branch, clean = self.make_security_inputs()
        branch["advisories"].append(
            {
                **copy.deepcopy(
                    next(item for item in branch["advisories"] if item["severity"] == "high")
                ),
                "advisory_id": "GHSA-1111-2222-3333",
            }
        )
        branch["totals"]["high"] += 1
        branch["totals"]["total"] += 1
        self.assert_reason(
            "NEW_DEPENDENCY_ADVISORY",
            lambda: rel000.validate_issue_and_audit(issue5, additional, base, branch, clean),
        )

    def test_38_branch_high_increases(self):
        issue5, additional, base, branch, clean = self.make_security_inputs()
        moderate = next(item for item in branch["advisories"] if item["severity"] == "moderate")
        moderate["severity"] = "high"
        branch["totals"]["high"] += 1
        branch["totals"]["moderate"] -= 1
        self.assert_reason(
            "DEPENDENCY_ADVISORY_SEVERITY_INCREASED",
            lambda: rel000.validate_issue_and_audit(issue5, additional, base, branch, clean),
        )

    def test_39_branch_moderate_increases(self):
        issue5, additional, base, branch, clean = self.make_security_inputs()
        high = next(item for item in branch["advisories"] if item["severity"] == "high")
        high["severity"] = "moderate"
        branch["totals"]["high"] -= 1
        branch["totals"]["moderate"] += 1
        self.assert_reason(
            "BRANCH_ADVISORY_SET_CHANGED",
            lambda: rel000.validate_issue_and_audit(issue5, additional, base, branch, clean),
        )

    def test_40_branch_critical_appears(self):
        issue5, additional, base, branch, clean = self.make_security_inputs()
        high = next(item for item in branch["advisories"] if item["severity"] == "high")
        high["severity"] = "critical"
        branch["totals"]["high"] -= 1
        branch["totals"]["critical"] += 1
        self.assert_reason(
            "AUDIT_CRITICAL_PRESENT",
            lambda: rel000.validate_issue_and_audit(issue5, additional, base, branch, clean),
        )

    def test_41_affected_package_set_changes(self):
        issue5, additional, base, branch, clean = self.make_security_inputs()
        branch["advisories"][0]["package"] = "unexpected-package"
        self.assert_reason(
            "NEW_AFFECTED_DEPENDENCY_PACKAGE",
            lambda: rel000.validate_issue_and_audit(issue5, additional, base, branch, clean),
        )

    def test_42_audit_command_omitted(self):
        issue5, additional, base, branch, clean = self.make_security_inputs()
        branch["command_executed"] = False
        self.assert_reason(
            "AUDIT_COMMAND_NOT_EXECUTED",
            lambda: rel000.validate_issue_and_audit(issue5, additional, base, branch, clean),
        )

    def test_43_audit_output_unparseable(self):
        issue5, additional, base, branch, clean = self.make_security_inputs()
        branch["parse_succeeded"] = False
        self.assert_reason(
            "AUDIT_PARSE_FAILED",
            lambda: rel000.validate_issue_and_audit(issue5, additional, base, branch, clean),
        )

    def test_44_dependency_lockfile_changed(self):
        issue5, additional, base, branch, clean = self.make_security_inputs()
        clean["dependency_lockfile_changed"] = True
        clean["dependency_diff_against_base"] = "DIRTY"
        self.assert_reason(
            "DEPENDENCY_FILES_CHANGED",
            lambda: rel000.validate_issue_and_audit(issue5, additional, base, branch, clean),
        )

    def test_45_passed_security_still_blocks_on_owner_decision(self):
        report = {
            "owner_approval_status": "PENDING",
            "dependency_security_status": "PASSED",
            "release_candidate_status": "BLOCKED_BY_OWNER_DECISION",
            "rel000_def_001_status": "RESOLVED",
            "normative_scope_status": "RESOLVED",
            "normative_scope_decision": "FIN001_MOVED_TO_MVP1",
            "audit_tracking_gap_detected": False,
            "audit_tracking_gap_count": 0,
            "technical_evidence_status": "PASSED",
            "result": "REL000_EVIDENCE_GENERATED",
        }
        rel000.validate_owner_state(report)

    def test_46_release_candidate_unblocked(self):
        report = {
            "owner_approval_status": "PENDING",
            "dependency_security_status": "BLOCKED",
            "release_candidate_status": "READY_FOR_RELEASE",
            "rel000_def_001_status": "RESOLVED",
            "normative_scope_status": "RESOLVED",
            "normative_scope_decision": "FIN001_MOVED_TO_MVP1",
            "audit_tracking_gap_detected": False,
            "audit_tracking_gap_count": 0,
            "technical_evidence_status": "PASSED",
            "result": "REL000_EVIDENCE_GENERATED",
        }
        self.assert_reason(
            "RELEASE_CANDIDATE_NOT_BLOCKED",
            lambda: rel000.validate_owner_state(report),
        )

    def test_47_tracking_gap_falsely_reported(self):
        report = {
            "owner_approval_status": "PENDING",
            "dependency_security_status": "BLOCKED",
            "release_candidate_status": "BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION",
            "rel000_def_001_status": "RESOLVED",
            "normative_scope_status": "RESOLVED",
            "normative_scope_decision": "FIN001_MOVED_TO_MVP1",
            "audit_tracking_gap_detected": True,
            "audit_tracking_gap_count": 10,
            "technical_evidence_status": "PASSED",
            "result": "REL000_EVIDENCE_GENERATED",
        }
        self.assert_reason(
            "AUDIT_TRACKING_GAP_INVALID",
            lambda: rel000.validate_owner_state(report),
        )

    def test_48_issue5_claims_all_advisories(self):
        issue5, additional, base, branch, clean = self.make_security_inputs()
        issue5["tracked_advisory_ids"] = sorted(rel000.EXPECTED_BASE_ADVISORIES)
        self.assert_reason(
            "ISSUE5_SCOPE_MISREPRESENTED",
            lambda: rel000.validate_issue_and_audit(issue5, additional, base, branch, clean),
        )

    def test_49_sanitizer_preserves_dependency_path_classification(self):
        raw_path = self.root / "audit-raw.json"
        output_path = self.root / "audit-sanitized.json"
        rel000.write_json(
            raw_path,
            {
                "advisories": {
                    "1": {
                        "findings": [{"version": "16.2.10", "paths": [".>next"]}],
                        "github_advisory_id": "GHSA-1111-2222-3333",
                        "module_name": "next",
                        "vulnerable_versions": "<16.2.11",
                        "patched_versions": ">=16.2.11",
                        "severity": "high",
                    },
                    "2": {
                        "findings": [{"version": "0.34.5", "paths": [".>next>sharp"]}],
                        "github_advisory_id": "GHSA-4444-5555-6666",
                        "module_name": "sharp",
                        "vulnerable_versions": "<0.35.0",
                        "patched_versions": ">=0.35.0",
                        "severity": "high",
                    },
                },
                "metadata": {
                    "vulnerabilities": {
                        "critical": 0,
                        "high": 2,
                        "moderate": 0,
                        "low": 0,
                    }
                },
            },
        )
        rel000.sanitize_audit(raw_path, output_path, True, 1)
        advisories = {
            item["package"]: item
            for item in rel000.load_json(output_path)["advisories"]
        }
        self.assertEqual("direct", advisories["next"]["direct_or_transitive"])
        self.assertEqual("transitive", advisories["sharp"]["direct_or_transitive"])
        self.assertEqual(1, advisories["next"]["dependency_path_count"])

    # 62-85: controlled dependency-remediation states and fail-closed graph checks.
    def test_62_security_base_audit_not_executed(self):
        inputs = list(self.make_remediation_inputs())
        inputs[2]["command_executed"] = False
        self.assert_reason(
            "AUDIT_COMMAND_NOT_EXECUTED",
            lambda: rel000.validate_issue_and_audit(
                *inputs, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_63_security_branch_audit_not_executed(self):
        inputs = list(self.make_remediation_inputs())
        inputs[3]["command_executed"] = False
        self.assert_reason(
            "AUDIT_COMMAND_NOT_EXECUTED",
            lambda: rel000.validate_issue_and_audit(
                *inputs, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_64_security_audit_not_parseable(self):
        inputs = list(self.make_remediation_inputs())
        inputs[3]["parse_succeeded"] = False
        self.assert_reason(
            "AUDIT_PARSE_FAILED",
            lambda: rel000.validate_issue_and_audit(
                *inputs, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_65_security_critical_advisory_rejected(self):
        inputs = list(self.make_remediation_inputs())
        inputs[3]["advisories"][0]["severity"] = "critical"
        inputs[3]["totals"].update(high=0, critical=1)
        self.assert_reason(
            "AUDIT_CRITICAL_PRESENT",
            lambda: rel000.validate_issue_and_audit(
                *inputs, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_66_security_new_advisory_rejected(self):
        inputs = list(self.make_remediation_inputs())
        added = copy.deepcopy(inputs[3]["advisories"][0])
        added["advisory_id"] = "GHSA-1111-2222-3333"
        inputs[3]["advisories"].append(added)
        inputs[3]["totals"]["high"] += 1
        inputs[3]["totals"]["total"] += 1
        self.assert_reason(
            "NEW_DEPENDENCY_ADVISORY",
            lambda: rel000.validate_issue_and_audit(
                *inputs, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_67_security_severity_increase_rejected(self):
        moderate_id = next(
            item["advisory_id"]
            for item in self.make_audit()["advisories"]
            if item["severity"] == "moderate"
        )
        inputs = list(self.make_remediation_inputs({moderate_id}))
        inputs[3]["advisories"][0]["severity"] = "high"
        inputs[3]["totals"].update(high=1, moderate=0)
        self.assert_reason(
            "DEPENDENCY_ADVISORY_SEVERITY_INCREASED",
            lambda: rel000.validate_issue_and_audit(
                *inputs, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_68_security_new_affected_package_rejected(self):
        inputs = list(self.make_remediation_inputs())
        inputs[3]["advisories"][0]["package"] = "other-image-library"
        self.assert_reason(
            "NEW_AFFECTED_DEPENDENCY_PACKAGE",
            lambda: rel000.validate_issue_and_audit(
                *inputs, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_69_unauthorized_dependency_file_rejected(self):
        root, base = self.make_dependency_repo()
        extra = root / "tools/package.json"
        extra.parent.mkdir()
        extra.write_text("{}\n", encoding="utf-8")
        self.assert_reason(
            "UNAUTHORIZED_DEPENDENCY_FILE_CHANGED",
            lambda: rel000.validate_dependency_diff(
                root, base, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_70_inconsistent_lockfile_rejected(self):
        root, base = self.make_dependency_repo()
        lock = root / "apps/web/pnpm-lock.yaml"
        lock.write_text(
            lock.read_text(encoding="utf-8").replace("  next@16.2.11:\n", ""),
            encoding="utf-8",
        )
        self.assert_reason(
            "LOCKFILE_INCONSISTENT",
            lambda: rel000.validate_dependency_diff(
                root, base, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_71_closed_issue_with_present_advisory_rejected(self):
        inputs = self.make_remediation_inputs(issue5_state="CLOSED")
        self.assert_reason(
            "SECURITY_ISSUE_CLOSED_WHILE_ADVISORY_PRESENT",
            lambda: rel000.validate_issue_and_audit(
                *inputs, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_72_removed_advisory_open_issue_is_pending_merge(self):
        removed = "GHSA-4633-3j49-mh5q"
        remaining = rel000.EXPECTED_BASE_ADVISORIES - {removed}
        result = rel000.validate_issue_and_audit(
            *self.make_remediation_inputs(remaining),
            rel000.SECURITY_REMEDIATION,
        )
        record = next(
            item for item in result["remediated_advisories"]
            if item["advisory_id"] == removed
        )
        self.assertEqual("REMEDIATED_PENDING_MERGE", record["remediation_status"])

    def test_73_issue30_fully_remediated_pending_merge(self):
        result = rel000.validate_issue_and_audit(
            *self.make_remediation_inputs({rel000.ISSUE5_ADVISORY}),
            rel000.SECURITY_REMEDIATION,
        )
        self.assertEqual(
            "REMEDIATED_PENDING_MERGE", result["issue_30_remediation_status"]
        )

    def test_74_issue30_partial_when_only_brace_remains(self):
        result = rel000.validate_issue_and_audit(
            *self.make_remediation_inputs(
                {rel000.ISSUE5_ADVISORY, "GHSA-mh99-v99m-4gvg"}
            ),
            rel000.SECURITY_REMEDIATION,
        )
        self.assertEqual("PARTIALLY_REMEDIATED", result["issue_30_remediation_status"])

    def test_75_sharp_present_keeps_issue5_blocked(self):
        result = rel000.validate_issue_and_audit(
            *self.make_remediation_inputs(), rel000.SECURITY_REMEDIATION
        )
        self.assertEqual("UNRESOLVED", result["issue_5_remediation_status"])
        self.assertEqual(
            "BLOCKED_BY_UPSTREAM_COMPATIBILITY", result["sharp_remediation_status"]
        )

    def test_76_sharp_absent_open_issue_is_pending_merge(self):
        result = rel000.validate_issue_and_audit(
            *self.make_remediation_inputs(set()), rel000.SECURITY_REMEDIATION
        )
        self.assertEqual(
            "REMEDIATED_PENDING_MERGE", result["issue_5_remediation_status"]
        )

    def test_77_zero_advisories_open_issues_is_not_passed(self):
        result = rel000.validate_issue_and_audit(
            *self.make_remediation_inputs(set()), rel000.SECURITY_REMEDIATION
        )
        self.assertEqual("REMEDIATED_PENDING_MERGE", result["dependency_security_status"])

    def test_78_zero_advisories_closed_issues_is_passed(self):
        result = rel000.validate_issue_and_audit(
            *self.make_remediation_inputs(
                set(), issue5_state="CLOSED", issue30_state="CLOSED"
            ),
            rel000.SECURITY_REMEDIATION,
        )
        self.assertEqual("PASSED", result["dependency_security_status"])
        self.assertEqual("BLOCKED_BY_OWNER_DECISION", result["release_candidate_status"])

    def test_79_passed_security_does_not_imply_owner_approval(self):
        report = {
            "owner_approval_status": "APPROVED",
            "dependency_security_status": "PASSED",
            "release_candidate_status": "BLOCKED_BY_OWNER_DECISION",
            "rel000_def_001_status": "RESOLVED",
            "normative_scope_status": "RESOLVED",
            "normative_scope_decision": "FIN001_MOVED_TO_MVP1",
            "audit_tracking_gap_detected": False,
            "audit_tracking_gap_count": 0,
        }
        self.assert_reason(
            "OWNER_APPROVAL_FALSELY_ASSERTED",
            lambda: rel000.validate_owner_state(report),
        )

    def test_80_passed_security_does_not_verify_rel000(self):
        report = {
            "owner_approval_status": "PENDING",
            "dependency_security_status": "PASSED",
            "release_candidate_status": "BLOCKED_BY_OWNER_DECISION",
            "mvp0_p0_items_verified": 29,
            "mvp0_p0_items_blocked": 0,
            "rel000_def_001_status": "RESOLVED",
            "normative_scope_status": "RESOLVED",
            "normative_scope_decision": "FIN001_MOVED_TO_MVP1",
            "audit_tracking_gap_detected": False,
            "audit_tracking_gap_count": 0,
        }
        self.assert_reason(
            "REL000_FALSELY_VERIFIED", lambda: rel000.validate_owner_state(report)
        )

    def test_81_normal_mode_still_rejects_dependency_drift(self):
        dirty = {
            "dependency_manifest_changed": True,
            "dependency_lockfile_changed": True,
            "dependency_diff_against_base": "DIRTY",
        }
        issue5, issue30, base, branch, _ = self.make_security_inputs()
        self.assert_reason(
            "DEPENDENCY_FILES_CHANGED",
            lambda: rel000.validate_issue_and_audit(
                issue5,
                issue30,
                base,
                branch,
                dirty,
                rel000.NORMAL_RELEASE_EVIDENCE,
            ),
        )

    def test_82_incompatible_override_rejected(self):
        root, base = self.make_dependency_repo()
        workspace = root / "apps/web/pnpm-workspace.yaml"
        workspace.write_text(
            workspace.read_text(encoding="utf-8").replace(
                '"brace-expansion@>=4.0.0 <5.0.8": 5.0.8',
                '"brace-expansion@>=4.0.0 <5.0.8": 1.1.17',
            ),
            encoding="utf-8",
        )
        self.assert_reason(
            "INCOMPATIBLE_DEPENDENCY_OVERRIDE",
            lambda: rel000.validate_dependency_diff(
                root, base, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_83_duplicate_vulnerable_lock_version_rejected(self):
        root, base = self.make_dependency_repo()
        lock = root / "apps/web/pnpm-lock.yaml"
        lock.write_text(
            lock.read_text(encoding="utf-8")
            + "  brace-expansion@5.0.7:\n    resolution: {}\n",
            encoding="utf-8",
        )
        self.assert_reason(
            "VULNERABLE_VERSION_RETAINED",
            lambda: rel000.validate_dependency_diff(
                root, base, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_84_prerelease_dependency_rejected(self):
        root, base = self.make_dependency_repo()
        lock = root / "apps/web/pnpm-lock.yaml"
        lock.write_text(
            lock.read_text(encoding="utf-8")
            + "  brace-expansion@5.0.8-rc.0:\n    resolution: {}\n",
            encoding="utf-8",
        )
        self.assert_reason(
            "PRERELEASE_DEPENDENCY_REJECTED",
            lambda: rel000.validate_dependency_diff(
                root, base, rel000.SECURITY_REMEDIATION
            ),
        )

    def test_85_dynamic_focused_count_is_self_consistent(self):
        class PassingCase(unittest.TestCase):
            def runTest(self):
                self.assertTrue(True)

        suite = unittest.TestSuite([PassingCase()])
        discovered = focused_runner.count_cases(suite)
        result = focused_runner.execute_suite(suite, discovered, stream=io.StringIO())
        self.assertEqual(result["python_tests_expected"], result["python_tests_discovered"])
        self.assertEqual(result["python_tests_executed"], result["python_tests_discovered"])
        self.assertEqual(result["python_tests_passed"], result["python_tests_executed"])
        self.assertEqual(0, result["python_tests_failed"])
        self.assertEqual(0, result["python_tests_skipped"])


class SharpRemediationPolicyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-sharp-tests-")
        self.root = Path(self.temp.name)
        self.policy_value = json.loads(
            (REPOSITORY_ROOT / "tools/rel-000/security-remediation-policy.json").read_text(
                encoding="utf-8"
            )
        )

    def tearDown(self) -> None:
        self.temp.cleanup()

    def assert_reason(self, expected: str, action) -> rel000.ValidationFailure:
        with self.assertRaises(rel000.ValidationFailure) as raised:
            action()
        self.assertEqual(expected, raised.exception.reason_code)
        return raised.exception

    def write_policy(self, value=None) -> Path:
        path = self.root / "policy.json"
        path.write_text(json.dumps(value or self.policy_value), encoding="utf-8")
        return path

    def policy(self):
        return rel000.load_remediation_policy(self.write_policy())

    def authorization(self):
        return copy.deepcopy(self.policy_value["active_remediations"][0])

    @staticmethod
    def sharp_advisory(advisory_id=rel000.ISSUE5_ADVISORY, package="sharp", severity="high"):
        return {
            "advisory_id": advisory_id,
            "package": package,
            "installed_versions": ["0.34.5"],
            "severity": severity,
            "affected_range": "<0.35.0",
            "patched_range": ">=0.35.0",
            "direct_or_transitive": "transitive",
            "dependency_path_count": 1,
            "fix_available": True,
            "fix_compatibility": "requires_compatibility_assessment",
        }

    def audit(self, advisories, *, executed=True, parsed=True):
        totals = {
            key: sum(item["severity"] == key for item in advisories)
            for key in ("critical", "high", "moderate", "low")
        }
        totals["total"] = len(advisories)
        return {
            "command_executed": executed,
            "command_exit_code": 1 if advisories else 0,
            "parse_succeeded": parsed,
            "totals": totals,
            "advisories": copy.deepcopy(advisories),
        }

    def security_inputs(self, *, branch=None, issue5="OPEN", issue30="CLOSED"):
        base = self.audit([self.sharp_advisory()])
        return (
            {
                "number": 5,
                "state": issue5,
                "title": "sharp",
                "url": "https://github.com/example/issues/5",
                "tracked_advisory_ids": [rel000.ISSUE5_ADVISORY],
            },
            {
                "number": 30,
                "state": issue30,
                "title": rel000.ADDITIONAL_SECURITY_ISSUE_TITLE,
                "url": "https://github.com/example/issues/30",
                "tracked_advisory_ids": sorted(rel000.ISSUE30_ADVISORIES),
            },
            base,
            branch if branch is not None else self.audit([]),
            {
                "dependency_diff_against_base": "AUTHORIZED_SECURITY_REMEDIATION",
                "vulnerable_lock_versions": [],
            },
        )

    def validate_security(self, **kwargs):
        return rel000.validate_issue_and_audit(
            *self.security_inputs(**kwargs),
            rel000.SECURITY_REMEDIATION,
            self.authorization(),
        )

    @staticmethod
    def passing_smoke(version="0.35.3"):
        return {
            "result": "SHARP_RUNTIME_SMOKE_PASSED",
            "sharp_version": version,
            "dependency_parent": "next",
            "node_version": "24.13.0",
            "platform": "linux",
            "architecture": "x64",
            "output_format": "png",
            "output_bytes": 95,
        }

    def dependency_repo(self):
        root = self.root / "repo"
        web = root / "apps/web"
        web.mkdir(parents=True)
        package = {
            "name": "@paquetenvia/web",
            "private": True,
            "dependencies": {
                "next": "16.2.11",
                "react": "19.2.7",
                "react-dom": "19.2.7",
            },
            "devDependencies": {"eslint-config-next": "16.2.11"},
        }
        workspace = (
            "overrides:\n"
            "  postcss: 8.5.21\n"
            "  \"brace-expansion@<1.1.17\": 1.1.17\n"
            "  \"brace-expansion@>=4.0.0 <5.0.8\": 5.0.8\n"
        )
        base_lock = (
            "lockfileVersion: '9.0'\n"
            "overrides:\n"
            "  postcss: 8.5.21\n"
            "importers:\n  .:\n    dependencies:\n      next:\n"
            "        specifier: 16.2.11\n        version: 16.2.11\n"
            "    devDependencies:\n      eslint-config-next:\n"
            "        specifier: 16.2.11\n        version: 16.2.11\n"
            "packages:\n  next@16.2.11:\n    resolution: {}\n"
            "  eslint-config-next@16.2.11:\n    resolution: {}\n"
            "  sharp@0.34.5:\n    resolution: {}\n"
            "snapshots:\n  next@16.2.11:\n    optionalDependencies:\n      sharp: 0.34.5\n"
        )
        (web / "package.json").write_text(json.dumps(package), encoding="utf-8")
        (web / "pnpm-workspace.yaml").write_text(workspace, encoding="utf-8")
        (web / "pnpm-lock.yaml").write_text(base_lock, encoding="utf-8")
        subprocess.run(["git", "init", "-q"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.email", "rel000@example.invalid"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.name", "REL-000 Tests"], cwd=root, check=True)
        subprocess.run(["git", "add", "."], cwd=root, check=True)
        subprocess.run(["git", "commit", "-qm", "base"], cwd=root, check=True)
        base = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
        (web / "pnpm-workspace.yaml").write_text(
            workspace + '  "next@16.2.11>sharp": 0.35.3\n', encoding="utf-8"
        )
        (web / "pnpm-lock.yaml").write_text(
            base_lock.replace("  postcss: 8.5.21\n", "  postcss: 8.5.21\n  next@16.2.11>sharp: 0.35.3\n")
            .replace("sharp@0.34.5", "sharp@0.35.3")
            .replace("sharp: 0.34.5", "sharp: 0.35.3"),
            encoding="utf-8",
        )
        return root, base, self.authorization()

    def validate_dependency(self, root, base, authorization=None):
        return rel000.validate_dependency_diff(
            root,
            base,
            rel000.SECURITY_REMEDIATION,
            authorization or self.authorization(),
        )

    def test_86_policy_v1_rejected(self):
        value = copy.deepcopy(self.policy_value); value["format_version"] = "paquetenvia-rel000-security-remediation-policy-v1"
        self.assert_reason("REMEDIATION_POLICY_FORMAT_INVALID", lambda: rel000.load_remediation_policy(self.write_policy(value)))

    def test_87_policy_missing_rejected(self):
        self.assert_reason("JSON_INPUT_INVALID", lambda: rel000.load_remediation_policy(self.root / "missing.json"))

    def test_88_unknown_policy_format_rejected(self):
        value = copy.deepcopy(self.policy_value); value["format_version"] = "unknown"
        self.assert_reason("REMEDIATION_POLICY_FORMAT_INVALID", lambda: rel000.load_remediation_policy(self.write_policy(value)))

    def test_89_unknown_remediation_id_rejected(self):
        self.assert_reason("REMEDIATION_ID_UNKNOWN", lambda: rel000.resolve_rel000_mode(self.policy(), "fix/security-sharp-035-override", "UNKNOWN"))

    def test_90_wrong_branch_resolves_normal(self):
        self.assertEqual(rel000.NORMAL_RELEASE_EVIDENCE, rel000.resolve_rel000_mode(self.policy(), "fix/other", rel000.SHARP_REMEDIATION_ID))

    def test_91_wrong_base_rejected(self):
        self.assert_reason("SECURITY_REMEDIATION_BASE_MISMATCH", lambda: rel000.validate_mode_authorization(rel000.SECURITY_REMEDIATION, self.policy(), "fix/security-sharp-035-override", "a" * 40, rel000.SHARP_REMEDIATION_ID))

    def test_92_wrong_issue_rejected(self):
        auth = self.authorization(); auth["tracked_issue"] = 6
        self.assert_reason("REMEDIATION_TRACKED_ISSUE_INVALID", lambda: rel000.validate_issue_and_audit(*self.security_inputs(), rel000.SECURITY_REMEDIATION, auth))

    def test_93_wrong_base_advisory_rejected(self):
        auth = self.authorization(); auth["expected_base_advisories"] = ["GHSA-1111-2222-3333"]
        self.assert_reason("BASE_ADVISORY_SET_UNEXPECTED", lambda: rel000.validate_issue_and_audit(*self.security_inputs(), rel000.SECURITY_REMEDIATION, auth))

    def test_94_zero_base_advisories_rejected(self):
        inputs = list(self.security_inputs()); inputs[2] = self.audit([])
        self.assert_reason("BASE_AUDIT_UNEXPECTED", lambda: rel000.validate_issue_and_audit(*inputs, rel000.SECURITY_REMEDIATION, self.authorization()))

    def test_95_multiple_base_advisories_rejected(self):
        inputs = list(self.security_inputs()); inputs[2] = self.audit([self.sharp_advisory(), self.sharp_advisory("GHSA-1111-2222-3333")])
        self.assert_reason("BASE_AUDIT_UNEXPECTED", lambda: rel000.validate_issue_and_audit(*inputs, rel000.SECURITY_REMEDIATION, self.authorization()))

    def test_96_base_audit_not_executed(self):
        inputs = list(self.security_inputs()); inputs[2]["command_executed"] = False
        self.assert_reason("AUDIT_COMMAND_NOT_EXECUTED", lambda: rel000.validate_issue_and_audit(*inputs, rel000.SECURITY_REMEDIATION, self.authorization()))

    def test_97_branch_audit_not_executed(self):
        inputs = list(self.security_inputs()); inputs[3]["command_executed"] = False
        self.assert_reason("AUDIT_COMMAND_NOT_EXECUTED", lambda: rel000.validate_issue_and_audit(*inputs, rel000.SECURITY_REMEDIATION, self.authorization()))

    def test_98_unparseable_audit_rejected(self):
        inputs = list(self.security_inputs()); inputs[3]["parse_succeeded"] = False
        self.assert_reason("AUDIT_PARSE_FAILED", lambda: rel000.validate_issue_and_audit(*inputs, rel000.SECURITY_REMEDIATION, self.authorization()))

    def test_99_new_advisory_rejected(self):
        branch = self.audit([self.sharp_advisory("GHSA-1111-2222-3333")])
        self.assert_reason("NEW_DEPENDENCY_ADVISORY", lambda: self.validate_security(branch=branch))

    def test_100_new_affected_package_rejected(self):
        branch = self.audit([self.sharp_advisory(package="other")])
        self.assert_reason("NEW_AFFECTED_DEPENDENCY_PACKAGE", lambda: self.validate_security(branch=branch))

    def test_101_severity_increase_rejected(self):
        inputs = list(self.security_inputs(branch=self.audit([self.sharp_advisory(severity="high")]))); inputs[2]["advisories"][0]["severity"] = "moderate"; inputs[2]["totals"].update(high=0, moderate=1)
        auth = self.authorization(); auth["expected_base_totals"].update(high=0, moderate=1)
        self.assert_reason("DEPENDENCY_ADVISORY_SEVERITY_INCREASED", lambda: rel000.validate_issue_and_audit(*inputs, rel000.SECURITY_REMEDIATION, auth))

    def test_102_critical_advisory_rejected(self):
        self.assert_reason("AUDIT_CRITICAL_PRESENT", lambda: self.validate_security(branch=self.audit([self.sharp_advisory(severity="critical")])))

    def test_103_vulnerable_sharp_retained_rejected(self):
        root, base, auth = self.dependency_repo(); lock = root / "apps/web/pnpm-lock.yaml"; lock.write_text(lock.read_text().replace("sharp@0.35.3", "sharp@0.34.5"), encoding="utf-8")
        self.assert_reason("SHARP_LOCK_VERSION_INVALID", lambda: self.validate_dependency(root, base, auth))

    def test_104_duplicate_sharp_versions_rejected(self):
        root, base, auth = self.dependency_repo(); lock = root / "apps/web/pnpm-lock.yaml"; lock.write_text(lock.read_text() + "  sharp@0.34.5:\n    resolution: {}\n", encoding="utf-8")
        self.assert_reason("SHARP_LOCK_VERSION_INVALID", lambda: self.validate_dependency(root, base, auth))

    def test_105_direct_sharp_dependency_rejected(self):
        root, base, auth = self.dependency_repo(); path = root / "apps/web/package.json"; value = json.loads(path.read_text()); value["dependencies"]["sharp"] = "0.35.3"; path.write_text(json.dumps(value), encoding="utf-8")
        auth["allowed_dependency_files"].append("apps/web/package.json"); auth["required_dependency_files"].append("apps/web/package.json")
        self.assert_reason("DIRECT_SHARP_DEPENDENCY_REJECTED", lambda: self.validate_dependency(root, base, auth))

    def test_106_required_override_missing(self):
        root, base, auth = self.dependency_repo(); path = root / "apps/web/pnpm-workspace.yaml"; path.write_text(path.read_text().replace('  "next@16.2.11>sharp": 0.35.3\n', '# required Sharp override intentionally absent\n'), encoding="utf-8")
        self.assert_reason("SECURITY_REMEDIATION_OVERRIDE_INVALID", lambda: self.validate_dependency(root, base, auth))

    def test_107_broad_override_rejected(self):
        root, base, auth = self.dependency_repo(); path = root / "apps/web/pnpm-workspace.yaml"; path.write_text(path.read_text().replace("next@16.2.11>sharp", "sharp"), encoding="utf-8")
        self.assert_reason("SECURITY_REMEDIATION_OVERRIDE_INVALID", lambda: self.validate_dependency(root, base, auth))

    def test_108_wrong_parent_override_rejected(self):
        root, base, auth = self.dependency_repo(); path = root / "apps/web/pnpm-workspace.yaml"; path.write_text(path.read_text().replace("next@16.2.11>sharp", "other@1.0.0>sharp"), encoding="utf-8")
        self.assert_reason("SECURITY_REMEDIATION_OVERRIDE_INVALID", lambda: self.validate_dependency(root, base, auth))

    def test_109_wrong_override_version_rejected(self):
        root, base, auth = self.dependency_repo(); path = root / "apps/web/pnpm-workspace.yaml"; path.write_text(path.read_text().replace("0.35.3", "0.35.2"), encoding="utf-8")
        self.assert_reason("SECURITY_REMEDIATION_OVERRIDE_INVALID", lambda: self.validate_dependency(root, base, auth))

    def test_110_prerelease_sharp_rejected(self):
        root, base, auth = self.dependency_repo(); auth["target_sharp_version"] = "0.35.4-rc.1"; auth["allowed_overrides"]["next@16.2.11>sharp"] = "0.35.4-rc.1"
        for relative in ("apps/web/pnpm-workspace.yaml", "apps/web/pnpm-lock.yaml"):
            path = root / relative; path.write_text(path.read_text().replace("0.35.3", "0.35.4-rc.1"), encoding="utf-8")
        self.assert_reason("SHARP_TARGET_VERSION_INVALID", lambda: self.validate_dependency(root, base, auth))

    def test_111_prerelease_next_rejected(self):
        root, base, auth = self.dependency_repo(); path = root / "apps/web/package.json"; value = json.loads(path.read_text()); value["dependencies"]["next"] = "16.3.0-rc.1"; value["devDependencies"]["eslint-config-next"] = "16.3.0-rc.1"; path.write_text(json.dumps(value), encoding="utf-8"); auth["allowed_dependency_files"].append("apps/web/package.json"); auth["required_dependency_files"].append("apps/web/package.json")
        self.assert_reason("PRERELEASE_DEPENDENCY_REJECTED", lambda: self.validate_dependency(root, base, auth))

    def test_112_next_alignment_rejected(self):
        root, base, auth = self.dependency_repo(); path = root / "apps/web/package.json"; value = json.loads(path.read_text()); value["devDependencies"]["eslint-config-next"] = "16.2.12"; path.write_text(json.dumps(value), encoding="utf-8"); auth["allowed_dependency_files"].append("apps/web/package.json"); auth["required_dependency_files"].append("apps/web/package.json")
        self.assert_reason("NEXT_DEPENDENCY_ALIGNMENT_INVALID", lambda: self.validate_dependency(root, base, auth))

    def test_113_unauthorized_dependency_file_rejected(self):
        root, base, auth = self.dependency_repo(); path = root / "tools/package.json"; path.parent.mkdir(); path.write_text("{}", encoding="utf-8")
        self.assert_reason("UNAUTHORIZED_DEPENDENCY_FILE_CHANGED", lambda: self.validate_dependency(root, base, auth))

    def test_114_existing_override_change_rejected(self):
        root, base, auth = self.dependency_repo(); path = root / "apps/web/pnpm-workspace.yaml"; path.write_text(path.read_text().replace("postcss: 8.5.21", "postcss: 8.5.20"), encoding="utf-8")
        self.assert_reason("SECURITY_REMEDIATION_OVERRIDE_INVALID", lambda: self.validate_dependency(root, base, auth))

    def test_115_inconsistent_lockfile_rejected(self):
        root, base, auth = self.dependency_repo(); path = root / "apps/web/pnpm-lock.yaml"; path.write_text(path.read_text().replace("      sharp: 0.35.3\n", ""), encoding="utf-8")
        self.assert_reason("LOCKFILE_INCONSISTENT", lambda: self.validate_dependency(root, base, auth))

    def test_116_smoke_not_executed_rejected(self):
        self.assert_reason("SHARP_RUNTIME_SMOKE_NOT_EXECUTED", lambda: rel000.validate_sharp_runtime_smoke(None, rel000.SECURITY_REMEDIATION, self.authorization()))

    def test_117_failed_smoke_rejected(self):
        smoke = self.passing_smoke(); smoke["result"] = "FAILED"
        self.assert_reason("SHARP_RUNTIME_SMOKE_FAILED", lambda: rel000.validate_sharp_runtime_smoke(smoke, rel000.SECURITY_REMEDIATION, self.authorization()))

    def test_118_vulnerable_smoke_version_rejected(self):
        self.assert_reason("SHARP_RUNTIME_SMOKE_VERSION_INVALID", lambda: rel000.validate_sharp_runtime_smoke(self.passing_smoke("0.34.5"), rel000.SECURITY_REMEDIATION, self.authorization()))

    def test_119_closed_issue5_with_advisory_rejected(self):
        branch = self.audit([self.sharp_advisory()])
        self.assert_reason("SECURITY_ISSUE_CLOSED_WHILE_ADVISORY_PRESENT", lambda: self.validate_security(branch=branch, issue5="CLOSED"))

    def test_120_open_issue30_rejected(self):
        self.assert_reason("ADDITIONAL_SECURITY_ISSUE_STATE_INVALID", lambda: self.validate_security(issue30="OPEN"))

    def test_121_zero_audit_open_issue5_pending_merge(self):
        self.assertEqual("REMEDIATED_PENDING_MERGE", self.validate_security()["dependency_security_status"])

    def test_122_zero_audit_closed_issues_passed(self):
        self.assertEqual("PASSED", self.validate_security(issue5="CLOSED")["dependency_security_status"])

    def test_123_passed_security_keeps_owner_pending(self):
        report = {"owner_approval_status": "PENDING", "dependency_security_status": "PASSED", "release_candidate_status": "BLOCKED_BY_OWNER_DECISION", "rel000_def_001_status": "RESOLVED", "normative_scope_status": "RESOLVED", "normative_scope_decision": "FIN001_MOVED_TO_MVP1", "audit_tracking_gap_detected": False, "audit_tracking_gap_count": 0}
        rel000.validate_owner_state(report)

    def test_124_passed_security_does_not_verify_rel000(self):
        report = {"owner_approval_status": "PENDING", "dependency_security_status": "PASSED", "release_candidate_status": "BLOCKED_BY_OWNER_DECISION", "mvp0_p0_items_verified": 29, "mvp0_p0_items_blocked": 0, "rel000_def_001_status": "RESOLVED", "normative_scope_status": "RESOLVED", "normative_scope_decision": "FIN001_MOVED_TO_MVP1", "audit_tracking_gap_detected": False, "audit_tracking_gap_count": 0}
        self.assert_reason("REL000_FALSELY_VERIFIED", lambda: rel000.validate_owner_state(report))

    def test_125_normal_mode_rejects_dependency_drift(self):
        root, base, _ = self.dependency_repo()
        self.assert_reason("DEPENDENCY_FILES_CHANGED", lambda: rel000.validate_dependency_diff(root, base, rel000.NORMAL_RELEASE_EVIDENCE))

    def test_126_historical_authorization_not_active(self):
        historical = self.policy_value["historical_remediations"][0]
        self.assert_reason("REMEDIATION_ID_NOT_ACTIVE", lambda: rel000.resolve_rel000_mode(self.policy(), historical["authorized_source_branch"], historical["id"]))

    def test_127_new_authorization_does_not_match_historical_branch(self):
        historical_branch = self.policy_value["historical_remediations"][0]["authorized_source_branch"]
        self.assertEqual(rel000.NORMAL_RELEASE_EVIDENCE, rel000.resolve_rel000_mode(self.policy(), historical_branch, rel000.SHARP_REMEDIATION_ID))

    def test_128_dynamic_counts_equal(self):
        suite = unittest.TestSuite([unittest.FunctionTestCase(lambda: None)]); count = focused_runner.count_cases(suite); result = focused_runner.execute_suite(suite, count, stream=io.StringIO())
        self.assertEqual(result["python_tests_expected"], result["python_tests_discovered"]); self.assertEqual(result["python_tests_executed"], result["python_tests_discovered"]); self.assertEqual(result["python_tests_passed"], result["python_tests_executed"])

    def test_129_dynamic_failed_zero(self):
        suite = unittest.TestSuite([unittest.FunctionTestCase(lambda: None)]); result = focused_runner.execute_suite(suite, 1, stream=io.StringIO()); self.assertEqual(0, result["python_tests_failed"])

    def test_130_dynamic_skipped_zero(self):
        suite = unittest.TestSuite([unittest.FunctionTestCase(lambda: None)]); result = focused_runner.execute_suite(suite, 1, stream=io.StringIO()); self.assertEqual(0, result["python_tests_skipped"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
