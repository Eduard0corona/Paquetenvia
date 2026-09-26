#!/usr/bin/env python3
"""Focused false-green tests for the REL-000 validator."""

from __future__ import annotations

import argparse
import contextlib
import copy
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import io
import zipfile
from pathlib import Path
from typing import Any
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

    def test_05f_rel000_verified_requires_valid_owner_and_focused_evidence(self):
        evidence = copy.deepcopy(self.item_evidence)
        implementation = self.root / "tools/rel-000/rel000.py"
        tests = self.root / "tools/rel-000/test_rel000.py"
        rollback = self.root / "docs/releases/mvp-0-internal-release-report.md"
        for path in (implementation, tests, rollback):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("evidence\n", encoding="utf-8")
        item = next(item for item in evidence["items"] if item["id"] == "REL-000")
        item.update(
            implementation_status="VERIFIED",
            implementation_commits_or_prs=["a" * 40],
            implementation_paths=["tools/rel-000/rel000.py"],
            required_test_sources=["tools/rel-000/test_rel000.py"],
            required_tests=[
                {
                    "source_path": "tools/rel-000/test_rel000.py",
                    "job": "rel000",
                    "test_project": "tools/rel-000/test_rel000.py",
                    "fully_qualified_test_name": "OwnerApprovalDecisionTests",
                    "category": "REL000_OWNER_APPROVAL",
                }
            ],
            authoritative_ci_jobs=["rel000"],
            rollback_reference="docs/releases/mvp-0-internal-release-report.md#rollback-rel-000",
            open_gates=[],
        )
        result = rel000.validate_item_evidence(
            self.root,
            self.selected,
            self.by_id,
            evidence,
            "a" * 40,
            self.job_results,
            self.execution_results,
            owner_approval={"record": {}},
            focused_tests={
                "focused_tests_expected": 1,
                "focused_tests_discovered": 1,
                "focused_tests_executed": 1,
                "focused_tests_passed": 1,
                "focused_tests_failed": 0,
                "focused_tests_skipped": 0,
            },
            ancestor_checker=lambda *_: True,
        )
        rel_item = next(item for item in result["items"] if item["id"] == "REL-000")
        self.assertEqual("VERIFIED", rel_item["implementation_status"])
        self.assertEqual("PASSED", rel_item["required_tests"][0]["outcome"])

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

    def test_32_mvp0_item_evidence_rejects_ext001_started(self):
        self.assert_reason(
            "EXT001_ALREADY_STARTED",
            lambda: rel000.validate_mvp0_extension_state({"ext001_started": True}),
        )

    def test_32a_post_mvp0_ext_paths_do_not_change_historical_state(self):
        tracked = subprocess.run(
            ["git", "-C", str(REPOSITORY_ROOT), "ls-files"],
            check=True,
            capture_output=True,
            text=True,
            encoding="utf-8",
        ).stdout.splitlines()
        post_mvp0_ext_paths = [
            path
            for path in tracked
            if re.search(r"(^|[/_-])ext-?001([/_.-]|$)", path, re.IGNORECASE)
        ]

        self.assertGreaterEqual(len(post_mvp0_ext_paths), 1)
        rel000.validate_mvp0_extension_state({"ext001_started": False})

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


class OwnerApprovalDecisionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-owner-tests-")
        self.root = Path(self.temp.name)
        self.record = json.loads(
            (REPOSITORY_ROOT / rel000.OWNER_DECISION_PATH).read_text(encoding="utf-8")
        )
        self.historical_source = subprocess.run(
            [
                "git",
                "-C",
                str(REPOSITORY_ROOT),
                "show",
                f"{rel000.APPROVED_EVIDENCE_MAIN_SHA}:{rel000.APPROVED_EXT001_SOURCE_PATH}",
            ],
            check=True,
            capture_output=True,
            text=True,
            encoding="utf-8",
        ).stdout

    def tearDown(self) -> None:
        self.temp.cleanup()

    def assert_reason(self, expected: str, action) -> rel000.ValidationFailure:
        with self.assertRaises(rel000.ValidationFailure) as raised:
            action()
        self.assertEqual(expected, raised.exception.reason_code)
        return raised.exception

    def valid_metadata(self) -> dict:
        return {
            "id": rel000.APPROVED_ARTIFACT_ID,
            "name": rel000.APPROVED_ARTIFACT_NAME,
            "size_in_bytes": rel000.APPROVED_ARTIFACT_ZIP_SIZE,
            "digest": rel000.APPROVED_ARTIFACT_DIGEST,
            "expired": False,
            "created_at": rel000.APPROVED_ARTIFACT_CREATED_AT,
            "expires_at": "2026-08-16T13:38:41Z",
            "workflow_run": {
                "id": int(rel000.APPROVED_WORKFLOW_RUN_ID),
                "head_sha": rel000.APPROVED_EVIDENCE_MAIN_SHA,
            },
        }

    def valid_zip(self) -> Path:
        path = self.root / "approved.zip"
        source = REPOSITORY_ROOT / rel000.APPROVED_SNAPSHOT_DIRECTORY
        with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name in sorted(rel000.APPROVED_SNAPSHOT_FILES):
                archive.writestr(name, (source / name).read_bytes())
        return path

    def validate_artifact(self, metadata=None, zip_path=None):
        path = zip_path or self.valid_zip()
        value = metadata or self.valid_metadata()
        value["size_in_bytes"] = path.stat().st_size
        with patch.object(
            rel000,
            "sha256_file",
            return_value=rel000.APPROVED_ARTIFACT_DIGEST.removeprefix("sha256:"),
        ), patch.object(rel000, "APPROVED_ARTIFACT_ZIP_SIZE", path.stat().st_size):
            return rel000.validate_approved_artifact(
                value,
                path,
                now=rel000.dt.datetime(2026, 8, 2, tzinfo=rel000.dt.timezone.utc),
            )

    def approved_report(self) -> dict:
        return {
            "dependency_security_status": "PASSED",
            "technical_evidence_status": "PASSED",
            "mvp0_p0_items_expected": 29,
            "mvp0_p0_items_evaluated": 29,
            "mvp0_p0_items_verified": 29,
            "mvp0_p0_items_blocked": 0,
            "blocked_ids": [],
            "known_security_issues": [{"id": "Issue #5", "url": "https://example.invalid"}],
            "additional_security_tracking_issue": {"number": 30, "url": "https://example.invalid"},
        }

    def remediation_report(self) -> dict:
        report = self.approved_report()
        report.update(
            {
                "dependency_security_status": "REMEDIATED_PENDING_MERGE",
                "release_candidate_status": "BLOCKED_BY_SECURITY_REMEDIATION_MERGE_AND_OWNER_DECISION",
                "known_security_issues": [{"id": "Issue #38", "state": "OPEN", "url": "https://example.invalid"}],
                "additional_security_tracking_issue": {
                    "number": 40,
                    "state": "OPEN",
                    "url": "https://example.invalid",
                },
            }
        )
        return report

    def approval(self) -> dict:
        return {
            "record": self.record,
            "decision_record_sha": "d" * 40,
            "decision_record_blob_sha": "e" * 40,
            "artifact": {
                "technical_artifact_contains_ext001_started": False,
                "storage": "VERSIONED_REDACTED_SNAPSHOT",
                "manifest_path": rel000.APPROVED_SNAPSHOT_MANIFEST_PATH,
                "snapshot_file_count": 4,
                "live_artifact_required": False,
                "original_expires_at": rel000.APPROVED_ARTIFACT_EXPIRES_AT,
            },
            "ext001": {
                "path": rel000.APPROVED_EXT001_SOURCE_PATH,
                "blob_sha": rel000.APPROVED_EXT001_SOURCE_BLOB_SHA,
                "source_sha": rel000.APPROVED_EVIDENCE_MAIN_SHA,
                "ext001_started": False,
            },
        }

    def test_175_exact_owner_decision_is_valid(self):
        self.assertEqual("APPROVE", rel000.validate_owner_decision_record(self.record)["decision"])

    def test_176_missing_decision_record_fails(self):
        self.assert_reason("OWNER_DECISION_RECORD_MISSING", lambda: rel000.load_owner_decision(self.root / "missing.json"))

    def test_177_invalid_decision_json_fails(self):
        path = self.root / "invalid.json"
        path.write_text("{", encoding="utf-8")
        self.assert_reason("OWNER_DECISION_JSON_INVALID", lambda: rel000.load_owner_decision(path))

    def test_178_decision_scalar_fields_fail_closed(self):
        cases = {
            "format_version": ("wrong", "OWNER_DECISION_FORMAT_INVALID"),
            "decision_id": ("wrong", "OWNER_DECISION_ID_INVALID"),
            "decision": ("REJECT", "OWNER_DECISION_VALUE_INVALID"),
            "decision_statement": ("", "OWNER_DECISION_STATEMENT_EMPTY"),
            "decision_reason": ("", "OWNER_DECISION_REASON_EMPTY"),
            "decided_on": ("not-a-date", "OWNER_DECISION_DATE_INVALID"),
            "decided_by": ("automation", "OWNER_DECISION_ACTOR_INVALID"),
        }
        for field, (value, reason) in cases.items():
            with self.subTest(field=field):
                record = copy.deepcopy(self.record)
                record[field] = value
                self.assert_reason(reason, lambda record=record: rel000.validate_owner_decision_record(record))

    def test_179_modified_statement_fails(self):
        record = copy.deepcopy(self.record)
        record["decision_statement"] = "Apruebo MVP-0"
        self.assert_reason("OWNER_DECISION_STATEMENT_INVALID", lambda: rel000.validate_owner_decision_record(record))

    def test_180_evidence_anchor_fields_fail_closed(self):
        cases = {
            "main_sha": ("a" * 40, "EXT001_STATE_SOURCE_MAIN_MISMATCH"),
            "workflow_run_id": ("1", "OWNER_DECISION_RUN_ID_MISMATCH"),
            "workflow_run_attempt": (2, "OWNER_DECISION_RUN_ATTEMPT_MISMATCH"),
            "artifact_id": (1, "OWNER_DECISION_ARTIFACT_ID_MISMATCH"),
            "artifact_name": ("wrong", "OWNER_DECISION_ARTIFACT_NAME_MISMATCH"),
            "artifact_digest": ("sha256:" + "0" * 64, "OWNER_DECISION_ARTIFACT_DIGEST_MISMATCH"),
        }
        for field, (value, reason) in cases.items():
            with self.subTest(field=field):
                record = copy.deepcopy(self.record)
                record["approved_evidence"][field] = value
                self.assert_reason(reason, lambda record=record: rel000.validate_owner_decision_record(record))

    def test_181_unauthorized_scope_flags_fail(self):
        for field in rel000.OWNER_APPROVAL_SCOPE:
            with self.subTest(field=field):
                record = copy.deepcopy(self.record)
                record["scope"][field] = not rel000.OWNER_APPROVAL_SCOPE[field]
                expected = "EXT001_ALREADY_STARTED" if field == "ext001_started" else "OWNER_DECISION_SCOPE_INVALID"
                self.assert_reason(expected, lambda record=record: rel000.validate_owner_decision_record(record))

    def test_181b_evidence_preservation_anchor_is_exact(self):
        for field, value in {
            "mode": "LIVE_ARTIFACT",
            "manifest_path": "elsewhere.json",
            "snapshot_directory": "elsewhere",
            "historical_artifact_live_access_required": True,
        }.items():
            with self.subTest(field=field):
                record = copy.deepcopy(self.record)
                record["evidence_preservation"][field] = value
                self.assert_reason(
                    "OWNER_DECISION_EVIDENCE_PRESERVATION_INVALID",
                    lambda record=record: rel000.validate_owner_decision_record(record),
                )

    def test_182_historical_artifact_without_ext_and_versioned_false_passes(self):
        artifact = self.validate_artifact()
        ext001 = rel000.validate_ext001_state_document(
            rel000.APPROVED_EVIDENCE_MAIN_SHA,
            rel000.APPROVED_EXT001_SOURCE_BLOB_SHA,
            self.historical_source,
        )
        self.assertFalse(artifact["technical_artifact_contains_ext001_started"])
        self.assertFalse(ext001["ext001_started"])

    def test_183_versioned_ext_field_missing_fails(self):
        source = json.loads(self.historical_source)
        del source["ext001_started"]
        self.assert_reason(
            "EXT001_STARTED_FIELD_MISSING",
            lambda: rel000.validate_ext001_state_document(rel000.APPROVED_EVIDENCE_MAIN_SHA, rel000.APPROVED_EXT001_SOURCE_BLOB_SHA, json.dumps(source)),
        )

    def test_184_versioned_ext_true_fails(self):
        source = json.loads(self.historical_source)
        source["ext001_started"] = True
        self.assert_reason(
            "EXT001_ALREADY_STARTED",
            lambda: rel000.validate_ext001_state_document(rel000.APPROVED_EVIDENCE_MAIN_SHA, rel000.APPROVED_EXT001_SOURCE_BLOB_SHA, json.dumps(source)),
        )

    def test_185_versioned_ext_string_fails(self):
        source = json.loads(self.historical_source)
        source["ext001_started"] = "false"
        self.assert_reason(
            "EXT001_STARTED_FIELD_TYPE_INVALID",
            lambda: rel000.validate_ext001_state_document(rel000.APPROVED_EVIDENCE_MAIN_SHA, rel000.APPROVED_EXT001_SOURCE_BLOB_SHA, json.dumps(source)),
        )

    def test_186_ext_blob_mismatch_fails(self):
        self.assert_reason(
            "EXT001_STATE_SOURCE_BLOB_MISMATCH",
            lambda: rel000.validate_ext001_state_document(rel000.APPROVED_EVIDENCE_MAIN_SHA, "0" * 40, self.historical_source),
        )

    def test_187_ext_main_mismatch_fails(self):
        self.assert_reason(
            "EXT001_STATE_SOURCE_MAIN_MISMATCH",
            lambda: rel000.validate_ext001_state_document("0" * 40, rel000.APPROVED_EXT001_SOURCE_BLOB_SHA, self.historical_source),
        )

    def test_188_technical_artifact_missing_fails(self):
        self.assert_reason(
            "APPROVED_ARTIFACT_INACCESSIBLE",
            lambda: rel000.validate_approved_artifact(
                self.valid_metadata(), self.root / "missing.zip", now=rel000.dt.datetime(2026, 8, 2, tzinfo=rel000.dt.timezone.utc)
            ),
        )

    def test_189_artifact_expired_fails(self):
        metadata = self.valid_metadata()
        metadata["expired"] = True
        self.assert_reason("APPROVED_ARTIFACT_EXPIRED", lambda: self.validate_artifact(metadata=metadata))

    def test_190_artifact_content_allowlist_fails(self):
        path = self.valid_zip()
        with zipfile.ZipFile(path, "a") as archive:
            archive.writestr("fifth.json", "{}")
        self.assert_reason("APPROVED_ARTIFACT_CONTENT_INVALID", lambda: self.validate_artifact(zip_path=path))

    def test_191_artifact_digest_recalculation_fails(self):
        path = self.valid_zip()
        metadata = self.valid_metadata()
        metadata["size_in_bytes"] = path.stat().st_size
        self.assert_reason(
            "APPROVED_ARTIFACT_ZIP_DIGEST_MISMATCH",
            lambda: self._validate_digest_failure(metadata, path),
        )

    def _validate_digest_failure(self, metadata, path):
        with patch.object(rel000, "APPROVED_ARTIFACT_ZIP_SIZE", path.stat().st_size):
            return rel000.validate_approved_artifact(
                metadata,
                path,
                now=rel000.dt.datetime(2026, 8, 2, tzinfo=rel000.dt.timezone.utc),
            )

    def test_192_invalid_technical_evidence_fails(self):
        path = self.valid_zip()
        rewritten = self.root / "invalid-technical.zip"
        with zipfile.ZipFile(path) as source, zipfile.ZipFile(rewritten, "w") as target:
            for name in source.namelist():
                value = json.loads(source.read(name))
                if name == "rel000-internal-release-report.json":
                    value["technical_evidence_status"] = "FAILED"
                target.writestr(name, json.dumps(value))
        self.assert_reason("APPROVED_ARTIFACT_FILE_SIZE_MISMATCH", lambda: self.validate_artifact(zip_path=rewritten))

    def test_193_green_ci_merge_artifact_or_pr_metadata_do_not_infer_approval(self):
        base = {
            "owner_approval_status": "PENDING",
            "release_candidate_status": "BLOCKED_BY_OWNER_DECISION",
            "dependency_security_status": "PASSED",
            "rel000_def_001_status": "RESOLVED",
            "normative_scope_status": "RESOLVED",
            "normative_scope_decision": "FIN001_MOVED_TO_MVP1",
            "audit_tracking_gap_detected": False,
            "audit_tracking_gap_count": 0,
        }
        for irrelevant in ({"ci": "success"}, {"merged": True}, {"artifact_valid": True}, {"pr_body": "approved"}):
            with self.subTest(irrelevant=irrelevant):
                report = {**base, **irrelevant}
                rel000.validate_owner_state(report)
                self.assertEqual("PENDING", report["owner_approval_status"])

    def test_194_valid_approval_emits_internal_state_and_explicit_ext_false(self):
        report = rel000.apply_owner_approval(
            self.approved_report(), self.approval(), rel000.NORMAL_RELEASE_EVIDENCE
        )
        rel000.validate_approved_owner_state(report)
        self.assertEqual("APPROVED", report["owner_approval_status"])
        self.assertEqual("APPROVED_FOR_MVP0_INTERNAL", report["release_candidate_status"])
        self.assertFalse(report["ext001_started"])
        self.assertFalse(report["technical_artifact_contains_ext001_started"])
        self.assertEqual("VERSIONED_REDACTED_SNAPSHOT", report["approved_evidence_storage"])
        self.assertFalse(report["approved_evidence_live_artifact_required"])
        self.assertEqual(4, report["approved_evidence_snapshot_file_count"])
        self.assertNotIn("https://", json.dumps(report))

    def test_195_inventory_changes_only_rel000(self):
        historical = json.loads(self.historical_source)
        current = json.loads((REPOSITORY_ROOT / rel000.APPROVED_EXT001_SOURCE_PATH).read_text(encoding="utf-8"))
        old = {item["id"]: item for item in historical["items"]}
        new = {item["id"]: item for item in current["items"]}
        changed = [item_id for item_id in old if old[item_id] != new[item_id]]
        self.assertEqual(["REL-000"], changed)
        self.assertEqual(29, len(new))
        self.assertEqual(29, sum(item["implementation_status"] == "VERIFIED" for item in new.values()))
        self.assertEqual([], [item["id"] for item in new.values() if item["implementation_status"] == "BLOCKED"])

    def test_196_public_allowlist_stays_four_json(self):
        self.assertEqual(
            {
                "rel000-internal-release-report.json",
                "rel000-p0-evidence.json",
                "rel000-cross-tenant-evidence.json",
                "rel000-rollback-evidence.json",
            },
            rel000.OUTPUT_FILES,
        )
        self.assertNotIn("mvp-0-owner-decision.json", rel000.OUTPUT_FILES)

    def test_197_missing_versioned_ext_source_fails(self):
        self.assert_reason(
            "EXT001_STATE_SOURCE_MISSING",
            lambda: rel000.validate_ext001_state_source(self.root, rel000.APPROVED_EVIDENCE_MAIN_SHA),
        )

    def test_198_mvp0_ext_state_missing_or_wrong_type_fails(self):
        self.assert_reason(
            "EXT001_STARTED_FIELD_MISSING",
            lambda: rel000.validate_mvp0_extension_state({}),
        )
        self.assert_reason(
            "EXT001_STARTED_FIELD_TYPE_INVALID",
            lambda: rel000.validate_mvp0_extension_state({"ext001_started": "false"}),
        )

    def test_199_open_security_issues_fail(self):
        self.assert_reason(
            "OWNER_APPROVAL_ISSUE_5_OPEN",
            lambda: rel000.validate_owner_approval_issues({"state": "OPEN"}, {"state": "CLOSED"}),
        )
        self.assert_reason(
            "OWNER_APPROVAL_ISSUE_30_OPEN",
            lambda: rel000.validate_owner_approval_issues({"state": "CLOSED"}, {"state": "OPEN"}),
        )

    def test_200_security_or_technical_failure_blocks_transition(self):
        security = self.approved_report()
        security["dependency_security_status"] = "FAILED"
        self.assert_reason(
            "OWNER_APPROVAL_DEPENDENCY_SECURITY_NOT_PASSED",
            lambda: rel000.apply_owner_approval(
                security, self.approval(), rel000.NORMAL_RELEASE_EVIDENCE
            ),
        )
        pending = self.approved_report()
        pending["dependency_security_status"] = "REMEDIATED_PENDING_MERGE"
        self.assert_reason(
            "OWNER_APPROVAL_DEPENDENCY_SECURITY_NOT_PASSED",
            lambda: rel000.apply_owner_approval(
                pending, self.approval(), rel000.NORMAL_RELEASE_EVIDENCE
            ),
        )
        technical = self.approved_report()
        technical["technical_evidence_status"] = "FAILED"
        self.assert_reason(
            "OWNER_APPROVAL_TECHNICAL_EVIDENCE_NOT_PASSED",
            lambda: rel000.apply_owner_approval(
                technical, self.approval(), rel000.NORMAL_RELEASE_EVIDENCE
            ),
        )

    def test_200a_security_remediation_preserves_pending_merge_state(self):
        report = rel000.apply_owner_approval(
            self.remediation_report(), self.approval(), rel000.SECURITY_REMEDIATION
        )
        rel000.validate_security_remediation_owner_state(report)
        self.assertEqual("APPROVED", report["owner_approval_status"])
        self.assertEqual("REMEDIATED_PENDING_MERGE", report["dependency_security_status"])
        self.assertNotEqual("PASSED", report["dependency_security_status"])
        self.assertEqual("BLOCKED_BY_SECURITY_REMEDIATION_MERGE", report["release_candidate_status"])
        self.assertEqual("SECURITY_REMEDIATION_READY_FOR_MERGE", report["technical_gate_outcome"])
        self.assertEqual("REL000_SECURITY_REMEDIATION_READY_FOR_MERGE", report["result"])
        self.assertEqual("OPEN", report["known_security_issues"][0]["state"])
        self.assertEqual("OPEN", report["additional_security_tracking_issue"]["state"])

    def test_200b_security_remediation_rejects_other_dependency_states(self):
        for status in ("BLOCKED", "PASSED"):
            with self.subTest(status=status):
                report = self.remediation_report()
                report["dependency_security_status"] = status
                self.assert_reason(
                    "OWNER_APPROVAL_DEPENDENCY_SECURITY_NOT_PASSED",
                    lambda report=report: rel000.apply_owner_approval(
                        report, self.approval(), rel000.SECURITY_REMEDIATION
                    ),
                )

    def test_200c_security_remediation_rejects_failed_technical_evidence(self):
        report = self.remediation_report()
        report["technical_evidence_status"] = "FAILED"
        self.assert_reason(
            "OWNER_APPROVAL_TECHNICAL_EVIDENCE_NOT_PASSED",
            lambda: rel000.apply_owner_approval(
                report, self.approval(), rel000.SECURITY_REMEDIATION
            ),
        )

    def test_200d_security_remediation_rejects_invalid_durable_approval(self):
        approval = self.approval()
        approval["artifact"]["storage"] = "LIVE_ARTIFACT"
        self.assert_reason(
            "SECURITY_REMEDIATION_OWNER_STATE_INVALID",
            lambda: rel000.validate_security_remediation_owner_state(
                rel000.apply_owner_approval(
                    self.remediation_report(), approval, rel000.SECURITY_REMEDIATION
                )
            ),
        )

    def test_200e_owner_transition_rejects_unknown_mode(self):
        self.assert_reason(
            "REL000_MODE_INVALID",
            lambda: rel000.apply_owner_approval(
                self.approved_report(), self.approval(), "UNKNOWN"
            ),
        )

    def test_201_live_artifact_metadata_fields_fail_closed(self):
        cases = {
            "id": (1, "APPROVED_ARTIFACT_ID_MISMATCH"),
            "name": ("wrong", "APPROVED_ARTIFACT_NAME_MISMATCH"),
            "digest": ("sha256:" + "0" * 64, "APPROVED_ARTIFACT_DIGEST_MISMATCH"),
        }
        for field, (value, reason) in cases.items():
            with self.subTest(field=field):
                metadata = self.valid_metadata()
                metadata[field] = value
                self.assert_reason(reason, lambda metadata=metadata: self.validate_artifact(metadata=metadata))

    def test_202_live_artifact_run_and_source_fail_closed(self):
        run = self.valid_metadata()
        run["workflow_run"]["id"] = 1
        self.assert_reason("APPROVED_ARTIFACT_RUN_MISMATCH", lambda: self.validate_artifact(metadata=run))
        source = self.valid_metadata()
        source["workflow_run"]["head_sha"] = "0" * 40
        self.assert_reason("APPROVED_ARTIFACT_SOURCE_SHA_MISMATCH", lambda: self.validate_artifact(metadata=source))


class DurableApprovedEvidenceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-durable-tests-")
        self.root = Path(self.temp.name) / "repository"
        self.snapshot = self.root / rel000.APPROVED_SNAPSHOT_DIRECTORY
        self.snapshot.parent.mkdir(parents=True)
        shutil.copytree(REPOSITORY_ROOT / rel000.APPROVED_SNAPSHOT_DIRECTORY, self.snapshot)
        self.manifest = self.root / rel000.APPROVED_SNAPSHOT_MANIFEST_PATH
        self.versioned = {
            relative: (self.root / relative).read_bytes()
            for relative in (
                rel000.APPROVED_SNAPSHOT_MANIFEST_PATH,
                *(f"{rel000.APPROVED_SNAPSHOT_DIRECTORY}/{name}" for name in rel000.APPROVED_SNAPSHOT_FILES),
            )
        }

    def tearDown(self) -> None:
        self.temp.cleanup()

    def assert_reason(self, expected: str, action) -> rel000.ValidationFailure:
        with self.assertRaises(rel000.ValidationFailure) as raised:
            action()
        self.assertEqual(expected, raised.exception.reason_code)
        return raised.exception

    def validate_snapshot(self, *, loader=None):
        return rel000.validate_approved_evidence_snapshot(
            self.root,
            self.manifest,
            self.snapshot,
            "f" * 40,
            versioned_bytes_loader=loader or (lambda relative: self.versioned[relative]),
        )

    def metadata(self, zip_path: Path) -> dict:
        return {
            "id": rel000.APPROVED_ARTIFACT_ID,
            "name": rel000.APPROVED_ARTIFACT_NAME,
            "size_in_bytes": zip_path.stat().st_size,
            "digest": rel000.APPROVED_ARTIFACT_DIGEST,
            "expired": False,
            "created_at": rel000.APPROVED_ARTIFACT_CREATED_AT,
            "expires_at": rel000.APPROVED_ARTIFACT_EXPIRES_AT,
            "workflow_run": {
                "id": int(rel000.APPROVED_WORKFLOW_RUN_ID),
                "head_sha": rel000.APPROVED_EVIDENCE_MAIN_SHA,
            },
        }

    def zip_from_snapshot(self, *, omit=None, extra=None, overrides=None) -> Path:
        path = Path(self.temp.name) / f"capture-{len(list(Path(self.temp.name).glob('*.zip')))}.zip"
        overrides = overrides or {}
        with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name in sorted(rel000.APPROVED_SNAPSHOT_FILES):
                if name == omit:
                    continue
                archive.writestr(name, overrides.get(name, (self.snapshot / name).read_bytes()))
            if extra:
                archive.writestr(extra, b"{}")
        return path

    def capture(self, zip_path: Path, output: Path, metadata=None):
        value = metadata or self.metadata(zip_path)
        metadata_path = Path(self.temp.name) / f"metadata-{len(list(Path(self.temp.name).glob('metadata-*.json')))}.json"
        metadata_path.write_text(json.dumps(value), encoding="utf-8")
        with patch.object(rel000, "APPROVED_ARTIFACT_ZIP_SIZE", zip_path.stat().st_size), patch.object(
            rel000,
            "sha256_file",
            return_value=rel000.APPROVED_ARTIFACT_DIGEST.removeprefix("sha256:"),
        ):
            return rel000.capture_approved_evidence(
                metadata_path,
                zip_path,
                output,
                now=rel000.dt.datetime(2026, 8, 2, tzinfo=rel000.dt.timezone.utc),
            )

    def rewrite_snapshot_json(self, name: str, mutate) -> dict[str, dict[str, Any]]:
        value = json.loads((self.snapshot / name).read_text(encoding="utf-8"))
        mutate(value)
        raw = (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
        (self.snapshot / name).write_bytes(raw)
        expected = copy.deepcopy(rel000.APPROVED_SNAPSHOT_FILES)
        expected[name] = {"size_bytes": len(raw), "sha256": rel000.hashlib.sha256(raw).hexdigest()}
        return expected

    def validate_semantic_mutation(self, name: str, mutate):
        expected = self.rewrite_snapshot_json(name, mutate)
        with patch.object(rel000, "APPROVED_SNAPSHOT_FILES", expected):
            rel000.write_json(self.manifest, rel000.approved_snapshot_manifest())
            return self.validate_snapshot(loader=lambda relative: (self.root / relative).read_bytes())

    def test_205_valid_snapshot_passes_without_live_artifact(self):
        with patch.object(rel000, "validate_approved_artifact", side_effect=AssertionError("network path used")):
            result = self.validate_snapshot()
        self.assertEqual("VERSIONED_REDACTED_SNAPSHOT", result["storage"])
        self.assertFalse(result["live_artifact_required"])

    def test_206_snapshot_validation_does_not_use_current_date(self):
        class ExplodingDateTime:
            @classmethod
            def now(cls, *_args, **_kwargs):
                raise AssertionError("current date must not be consulted")

        with patch.object(rel000.dt, "datetime", ExplodingDateTime):
            self.validate_snapshot()

    def test_207_capture_valid_metadata_and_zip_is_byte_exact(self):
        zip_path = self.zip_from_snapshot()
        output = Path(self.temp.name) / "captured"
        manifest = self.capture(zip_path, output)
        self.assertEqual(manifest, json.loads((output / "approved-evidence-manifest.json").read_text(encoding="utf-8")))
        self.assertEqual(zip_path.stat().st_size, manifest["capture"]["artifact_zip_size_bytes"])
        for name in rel000.APPROVED_SNAPSHOT_FILES:
            self.assertEqual((self.snapshot / name).read_bytes(), (output / name).read_bytes())

    def test_208_capture_metadata_digest_and_expiry_fail_closed(self):
        zip_path = self.zip_from_snapshot()
        for field, value, reason in (
            ("id", 1, "APPROVED_ARTIFACT_ID_MISMATCH"),
            ("digest", "sha256:" + "0" * 64, "APPROVED_ARTIFACT_DIGEST_MISMATCH"),
            ("expired", True, "APPROVED_ARTIFACT_EXPIRED"),
        ):
            with self.subTest(field=field):
                metadata = self.metadata(zip_path)
                metadata[field] = value
                self.assert_reason(reason, lambda metadata=metadata: self.capture(zip_path, Path(self.temp.name) / f"out-{field}", metadata))

    def test_209_capture_rejects_missing_and_additional_files(self):
        for zip_path in (
            self.zip_from_snapshot(omit="rel000-rollback-evidence.json"),
            self.zip_from_snapshot(extra="extra.json"),
        ):
            with self.subTest(zip=zip_path.name):
                self.assert_reason(
                    "APPROVED_ARTIFACT_CONTENT_INVALID",
                    lambda zip_path=zip_path: self.capture(zip_path, Path(self.temp.name) / f"out-{zip_path.stem}"),
                )

    def test_210_capture_rejects_file_hash_or_size_change(self):
        name = "rel000-cross-tenant-evidence.json"
        original = (self.snapshot / name).read_bytes()
        for changed, reason in ((original + b"\n", "APPROVED_ARTIFACT_FILE_SIZE_MISMATCH"), (b"!" + original[1:], "APPROVED_ARTIFACT_FILE_HASH_MISMATCH")):
            zip_path = self.zip_from_snapshot(overrides={name: changed})
            with self.subTest(reason=reason):
                self.assert_reason(reason, lambda zip_path=zip_path: self.capture(zip_path, Path(self.temp.name) / f"out-{reason}"))

    def test_211_capture_rejects_invalid_json_and_secret(self):
        name = "rel000-cross-tenant-evidence.json"
        invalid = b"!" + (self.snapshot / name).read_bytes()[1:]
        zip_path = self.zip_from_snapshot(overrides={name: invalid})
        expected = copy.deepcopy(rel000.APPROVED_SNAPSHOT_FILES)
        expected[name] = {"size_bytes": len(invalid), "sha256": rel000.hashlib.sha256(invalid).hexdigest()}
        with patch.object(rel000, "APPROVED_SNAPSHOT_FILES", expected):
            self.assert_reason(
                "APPROVED_ARTIFACT_CONTENT_INVALID",
                lambda: self.capture(zip_path, Path(self.temp.name) / "invalid-json"),
            )
        self.assert_reason(
            "REDACTION_FORBIDDEN_VALUE",
            lambda: rel000._assert_snapshot_redacted({"x.json": {"contact": "owner@example.com"}}),
        )

    def test_212_capture_rejects_existing_output_and_cleans_partial_failure(self):
        zip_path = self.zip_from_snapshot()
        existing = Path(self.temp.name) / "existing"
        existing.mkdir()
        (existing / "keep.txt").write_text("keep", encoding="utf-8")
        self.assert_reason("APPROVED_SNAPSHOT_OUTPUT_EXISTS", lambda: self.capture(zip_path, existing))
        output = Path(self.temp.name) / "partial"
        with patch.object(Path, "write_bytes", side_effect=OSError("synthetic write failure")):
            with self.assertRaises(OSError):
                self.capture(zip_path, output)
        self.assertFalse(output.exists())

    def test_213_capture_rejects_symlink_output(self):
        link = Path(self.temp.name) / "output-link"
        original = Path.is_symlink
        with patch.object(Path, "is_symlink", lambda path: True if path == link else original(path)):
            self.assert_reason("APPROVED_SNAPSHOT_OUTPUT_EXISTS", lambda: self.capture(self.zip_from_snapshot(), link))

    def test_214_snapshot_manifest_failures(self):
        cases = {
            "format_version": "wrong",
            "decision_id": "wrong",
        }
        for field, value in cases.items():
            with self.subTest(field=field):
                manifest = json.loads(self.manifest.read_text(encoding="utf-8"))
                manifest[field] = value
                self.manifest.write_text(json.dumps(manifest), encoding="utf-8")
                self.assert_reason("APPROVED_SNAPSHOT_MANIFEST_INVALID", self.validate_snapshot)
                self.manifest.write_bytes(self.versioned[rel000.APPROVED_SNAPSHOT_MANIFEST_PATH])

    def test_215_snapshot_anchor_and_file_list_failures(self):
        manifest = json.loads(self.manifest.read_text(encoding="utf-8"))
        manifest["capture"]["artifact_id"] = 1
        self.manifest.write_text(json.dumps(manifest), encoding="utf-8")
        self.assert_reason("APPROVED_SNAPSHOT_MANIFEST_INVALID", self.validate_snapshot)
        self.manifest.write_bytes(self.versioned[rel000.APPROVED_SNAPSHOT_MANIFEST_PATH])
        (self.snapshot / "extra.json").write_text("{}", encoding="utf-8")
        self.assert_reason("APPROVED_SNAPSHOT_FILE_LIST_INVALID", self.validate_snapshot)

    def test_216_snapshot_missing_modified_and_not_versioned_fail(self):
        target = self.snapshot / "rel000-rollback-evidence.json"
        original = target.read_bytes()
        target.unlink()
        self.assert_reason("APPROVED_SNAPSHOT_FILE_LIST_INVALID", self.validate_snapshot)
        target.write_bytes(original + b"\n")
        self.assert_reason("APPROVED_SNAPSHOT_FILE_SIZE_MISMATCH", self.validate_snapshot)
        target.write_bytes(original)
        self.assert_reason(
            "APPROVED_SNAPSHOT_NOT_VERSIONED",
            lambda: self.validate_snapshot(loader=lambda relative: b"wrong" if relative.endswith(target.name) else self.versioned[relative]),
        )

    def test_217_snapshot_rejects_path_traversal_and_symlink(self):
        manifest = json.loads(self.manifest.read_text(encoding="utf-8"))
        manifest["files"][0]["name"] = "../escape.json"
        self.manifest.write_text(json.dumps(manifest), encoding="utf-8")
        self.assert_reason("APPROVED_SNAPSHOT_MANIFEST_INVALID", self.validate_snapshot)
        self.manifest.write_bytes(self.versioned[rel000.APPROVED_SNAPSHOT_MANIFEST_PATH])
        target = self.snapshot / "rel000-rollback-evidence.json"
        original = Path.is_symlink
        with patch.object(Path, "is_symlink", lambda path: True if path == target else original(path)):
            self.assert_reason("APPROVED_SNAPSHOT_LINK_REJECTED", self.validate_snapshot)

    def test_218_snapshot_rejects_technical_provenance_inventory_and_ext_drift(self):
        report_name = "rel000-internal-release-report.json"
        self.assert_reason(
            "APPROVED_TECHNICAL_EVIDENCE_INVALID",
            lambda: self.validate_semantic_mutation(report_name, lambda value: value.__setitem__("technical_evidence_status", "FAILED")),
        )

    def test_219_snapshot_rejects_provenance_drift(self):
        self.assert_reason(
            "APPROVED_ARTIFACT_PROVENANCE_INVALID",
            lambda: self.validate_semantic_mutation(
                "rel000-internal-release-report.json",
                lambda value: value["execution_provenance"].__setitem__("mixed_attempt_evidence", True),
            ),
        )

    def test_220_snapshot_rejects_inventory_drift(self):
        self.assert_reason(
            "APPROVED_TECHNICAL_EVIDENCE_INVALID",
            lambda: self.validate_semantic_mutation(
                "rel000-internal-release-report.json",
                lambda value: value.__setitem__("mvp0_p0_items_verified", 29),
            ),
        )

    def test_221_snapshot_rejects_fabricated_historical_ext_state(self):
        self.assert_reason(
            "APPROVED_ARTIFACT_SCHEMA_DRIFT",
            lambda: self.validate_semantic_mutation(
                "rel000-internal-release-report.json",
                lambda value: value.__setitem__("ext001_started", False),
            ),
        )

    def test_222_snapshot_rejects_missing_or_invalid_manifest(self):
        original = self.manifest.read_bytes()
        self.manifest.unlink()
        self.assert_reason("APPROVED_SNAPSHOT_MISSING", self.validate_snapshot)
        self.manifest.write_text("{", encoding="utf-8")
        self.assert_reason("APPROVED_SNAPSHOT_MANIFEST_INVALID", self.validate_snapshot)
        self.manifest.write_bytes(original)

    def test_223_snapshot_rejects_historical_anchor_drift(self):
        cases = {
            "artifact_id": 1,
            "artifact_zip_sha256": "0" * 64,
            "source_sha": "0" * 40,
        }
        original = self.manifest.read_bytes()
        for field, value in cases.items():
            with self.subTest(field=field):
                manifest = json.loads(original)
                manifest["capture"][field] = value
                self.manifest.write_text(json.dumps(manifest), encoding="utf-8")
                self.assert_reason("APPROVED_SNAPSHOT_MANIFEST_INVALID", self.validate_snapshot)
        self.manifest.write_bytes(original)

    def test_224_snapshot_rejects_duplicate_or_incomplete_manifest_list(self):
        original = self.manifest.read_bytes()
        for mutate in (
            lambda files: files.append(copy.deepcopy(files[0])),
            lambda files: files.pop(),
        ):
            manifest = json.loads(original)
            mutate(manifest["files"])
            self.manifest.write_text(json.dumps(manifest), encoding="utf-8")
            self.assert_reason("APPROVED_SNAPSHOT_MANIFEST_INVALID", self.validate_snapshot)
        self.manifest.write_bytes(original)

    def test_225_snapshot_rejects_same_size_hash_change_and_invalid_json(self):
        target = self.snapshot / "rel000-cross-tenant-evidence.json"
        original = target.read_bytes()
        changed = b"!" + original[1:]
        target.write_bytes(changed)
        self.assert_reason("APPROVED_SNAPSHOT_FILE_HASH_MISMATCH", self.validate_snapshot)
        expected = copy.deepcopy(rel000.APPROVED_SNAPSHOT_FILES)
        expected[target.name] = {"size_bytes": len(changed), "sha256": rel000.hashlib.sha256(changed).hexdigest()}
        with patch.object(rel000, "APPROVED_SNAPSHOT_FILES", expected):
            rel000.write_json(self.manifest, rel000.approved_snapshot_manifest())
            self.assert_reason(
                "APPROVED_SNAPSHOT_JSON_INVALID",
                lambda: self.validate_snapshot(loader=lambda relative: (self.root / relative).read_bytes()),
            )


class WorkflowProvenanceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-provenance-tests-")
        self.root = Path(self.temp.name)
        self.fixture = json.loads(
            (REPOSITORY_ROOT / "tests/fixtures/rel-000/partial-rerun-provenance.json").read_text(
                encoding="utf-8"
            )
        )
        self.sha = self.fixture["head_sha"]
        self.trace = {
            "source_head_sha": self.sha,
            "tested_git_sha": self.sha,
            "base_main_sha": self.sha,
        }

    def tearDown(self) -> None:
        self.temp.cleanup()

    def write_manifest(self, payload=None, name="provenance.json") -> Path:
        path = self.root / name
        path.write_text(json.dumps(payload if payload is not None else self.fixture), encoding="utf-8")
        return path

    def validate(self, payload=None, *, current_attempt=None):
        value = payload if payload is not None else self.fixture
        attempt = current_attempt if current_attempt is not None else value.get("current_attempt")
        return rel000.validate_workflow_provenance(
            self.write_manifest(value),
            self.trace,
            self.fixture["workflow_run_id"],
            attempt,
        )

    def assert_reason(self, expected: str, action) -> rel000.ValidationFailure:
        with self.assertRaises(rel000.ValidationFailure) as raised:
            action()
        self.assertEqual(expected, raised.exception.reason_code)
        return raised.exception

    def attempt_one(self):
        payload = copy.deepcopy(self.fixture)
        payload["current_attempt"] = 1
        payload["jobs"] = [job for job in payload["jobs"] if job["run_attempt"] == 1]
        for job in payload["jobs"]:
            job["status"] = "completed"
            job["conclusion"] = "success"
        jobs = {job["job_key"]: job for job in payload["jobs"]}
        for artifact in payload["artifacts"]:
            artifact["producer_attempt"] = 1
            artifact["producer_job_id"] = jobs[artifact["job_key"]]["job_id"]
        return payload

    def full_rerun(self):
        payload = self.attempt_one()
        payload["current_attempt"] = 2
        first_attempt = copy.deepcopy(payload["jobs"])
        for job in first_attempt:
            rerun = copy.deepcopy(job)
            rerun["job_id"] += 100000000
            rerun["run_attempt"] = 2
            if rerun["job_key"] == "rel000":
                rerun["status"] = "in_progress"
                rerun["conclusion"] = ""
            payload["jobs"].append(rerun)
        jobs = {
            job["job_key"]: job for job in payload["jobs"] if job["run_attempt"] == 2
        }
        for index, artifact in enumerate(payload["artifacts"], start=1):
            artifact["producer_attempt"] = 2
            artifact["producer_job_id"] = jobs[artifact["job_key"]]["job_id"]
            artifact["artifact_id"] += 100000000
            artifact["artifact_digest"] = f"{index:x}" * 64
        return payload

    def write_partial_raw_metadata(self):
        raw = self.root / "raw"
        raw.mkdir()
        attempt_one_jobs = []
        for index, job in enumerate(item for item in self.fixture["jobs"] if item["run_attempt"] == 1):
            attempt_one_jobs.append(
                {
                    "id": job["job_id"],
                    "name": job["job_name"],
                    "run_attempt": 1,
                    "status": job["status"],
                    "conclusion": job["conclusion"],
                    "started_at": f"2026-08-02T08:25:{index:02d}Z",
                    "completed_at": f"2026-08-02T08:30:{index:02d}Z",
                }
            )
        (raw / "attempt-1-run.json").write_text(
            json.dumps(
                {
                    "id": int(self.fixture["workflow_run_id"]),
                    "run_attempt": 1,
                    "head_sha": self.sha,
                    "run_started_at": "2026-08-02T08:24:51Z",
                }
            ),
            encoding="utf-8",
        )
        (raw / "attempt-1-jobs.json").write_text(
            json.dumps({"total_count": 13, "jobs": attempt_one_jobs}), encoding="utf-8"
        )
        actual_attempt_two = {
            job["job_key"]: job for job in self.fixture["jobs"] if job["run_attempt"] == 2
        }
        attempt_two_jobs = []
        for index, prior in enumerate(attempt_one_jobs):
            key = next(
                key for key, name in rel000.AUTHORITATIVE_JOB_NAMES.items() if name == prior["name"]
            )
            if key in actual_attempt_two:
                actual = actual_attempt_two[key]
                attempt_two_jobs.append(
                    {
                        "id": actual["job_id"],
                        "name": actual["job_name"],
                        "run_attempt": 2,
                        "status": actual["status"],
                        "conclusion": actual["conclusion"],
                        "started_at": f"2026-08-02T09:00:{index:02d}Z",
                        "completed_at": f"2026-08-02T09:02:{index:02d}Z",
                    }
                )
            else:
                retained = copy.deepcopy(prior)
                retained["id"] += 100000000
                retained["run_attempt"] = 2
                attempt_two_jobs.append(retained)
        (raw / "attempt-2-run.json").write_text(
            json.dumps(
                {
                    "id": int(self.fixture["workflow_run_id"]),
                    "run_attempt": 2,
                    "head_sha": self.sha,
                    "run_started_at": "2026-08-02T08:59:34Z",
                }
            ),
            encoding="utf-8",
        )
        (raw / "attempt-2-jobs.json").write_text(
            json.dumps({"total_count": 13, "jobs": attempt_two_jobs}), encoding="utf-8"
        )
        raw_artifacts = {
            "artifacts": [
                {
                    "id": artifact["artifact_id"],
                    "name": artifact["artifact_name"],
                    "digest": artifact["artifact_digest"],
                    "expired": False,
                    "workflow_run": {
                        "id": int(self.fixture["workflow_run_id"]),
                        "head_sha": self.sha,
                    },
                }
                for artifact in self.fixture["artifacts"]
            ]
        }
        artifacts_path = raw / "artifacts.json"
        artifacts_path.write_text(json.dumps(raw_artifacts), encoding="utf-8")
        outputs = json.dumps(
            [
                {
                    "artifact_name": artifact["artifact_name"],
                    "artifact_id": str(artifact["artifact_id"]),
                    "artifact_digest": artifact["artifact_digest"],
                }
                for artifact in self.fixture["artifacts"]
            ]
        )
        return raw, artifacts_path, outputs

    def test_131_attempt_one_provenance_passes(self):
        result = self.validate(self.attempt_one())
        self.assertFalse(result["report"]["mixed_attempt_evidence"])

    def test_132_full_rerun_provenance_passes(self):
        attempt_one = self.attempt_one()
        rerun = self.full_rerun()
        result = self.validate(rerun)
        self.assertFalse(result["report"]["mixed_attempt_evidence"])
        self.assertTrue(all(item["producer_attempt"] == 2 for item in result["report"]["jobs"]))
        old_artifacts = {item["job_key"]: item for item in attempt_one["artifacts"]}
        new_artifacts = {item["job_key"]: item for item in rerun["artifacts"]}
        self.assertTrue(
            all(new_artifacts[key]["artifact_id"] != old_artifacts[key]["artifact_id"] for key in new_artifacts)
        )
        self.assertTrue(
            all(
                new_artifacts[key]["artifact_digest"] != old_artifacts[key]["artifact_digest"]
                for key in new_artifacts
            )
        )
        reported = {item["job"]: item for item in result["report"]["jobs"]}
        for key, artifact in new_artifacts.items():
            self.assertEqual(artifact["artifact_id"], reported[key]["artifact_id"])
            self.assertEqual(f"sha256:{artifact['artifact_digest']}", reported[key]["artifact_digest"])

    def test_133_partial_rerun_provenance_passes(self):
        result = self.validate()
        self.assertTrue(result["report"]["mixed_attempt_evidence"])
        attempts = {item["job"]: item["producer_attempt"] for item in result["report"]["jobs"]}
        self.assertEqual(2, attempts["realtime-e2e"])
        self.assertEqual(1, attempts["dotnet"])
        retained = next(item for item in result["report"]["jobs"] if item["job"] == "dotnet")
        expected = next(item for item in self.fixture["artifacts"] if item["job_key"] == "dotnet")
        self.assertEqual(expected["artifact_id"], retained["artifact_id"])
        self.assertEqual(expected["artifact_digest"], retained["artifact_digest"])

    def test_134_attempt_n_uses_each_latest_executed_job(self):
        payload = self.attempt_one()
        payload["current_attempt"] = 4
        next_id = 99000000000
        for attempt, keys in ((2, ("dotnet", "rel000")), (3, ("realtime-e2e", "rel000")), (4, ("outbox-signalr-delivery", "rel000"))):
            for key in keys:
                source = next(job for job in payload["jobs"] if job["job_key"] == key)
                job = copy.deepcopy(source)
                job["job_id"] = next_id
                next_id += 1
                job["run_attempt"] = attempt
                job["status"] = "in_progress" if key == "rel000" and attempt == 4 else "completed"
                job["conclusion"] = "" if key == "rel000" and attempt == 4 else "success"
                payload["jobs"].append(job)
        latest = {}
        for job in payload["jobs"]:
            if job["job_key"] not in latest or job["run_attempt"] > latest[job["job_key"]]["run_attempt"]:
                latest[job["job_key"]] = job
        for artifact in payload["artifacts"]:
            job = latest[artifact["job_key"]]
            artifact["producer_attempt"] = job["run_attempt"]
            artifact["producer_job_id"] = job["job_id"]
        result = self.validate(payload)
        attempts = {item["job"]: item["producer_attempt"] for item in result["report"]["jobs"]}
        self.assertEqual(2, attempts["dotnet"])
        self.assertEqual(3, attempts["realtime-e2e"])
        self.assertEqual(4, attempts["outbox-signalr-delivery"])

    def test_135_stale_signalr_artifact_after_rerun_fails(self):
        payload = copy.deepcopy(self.fixture)
        artifact = next(item for item in payload["artifacts"] if item["job_key"] == "realtime-e2e")
        artifact["producer_attempt"] = 1
        artifact["producer_job_id"] = 91474837038
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_STALE_AFTER_RERUN", lambda: self.validate(payload))

    def test_136_full_rerun_rejects_attempt_one_artifact(self):
        payload = self.full_rerun()
        artifact = next(item for item in payload["artifacts"] if item["job_key"] == "dotnet")
        old_artifact = next(item for item in self.attempt_one()["artifacts"] if item["job_key"] == "dotnet")
        artifact.update(old_artifact)
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_STALE_AFTER_RERUN", lambda: self.validate(payload))

    def test_137_latest_producer_failure_fails(self):
        payload = copy.deepcopy(self.fixture)
        latest = next(job for job in payload["jobs"] if job["job_key"] == "realtime-e2e" and job["run_attempt"] == 2)
        latest["conclusion"] = "failure"
        self.assert_reason("WORKFLOW_PROVENANCE_PRODUCER_NOT_SUCCESSFUL", lambda: self.validate(payload))

    def test_138_latest_producer_cancelled_fails(self):
        payload = copy.deepcopy(self.fixture)
        latest = next(job for job in payload["jobs"] if job["job_key"] == "realtime-e2e" and job["run_attempt"] == 2)
        latest["conclusion"] = "cancelled"
        self.assert_reason("WORKFLOW_PROVENANCE_PRODUCER_NOT_SUCCESSFUL", lambda: self.validate(payload))

    def test_139_latest_producer_skipped_fails(self):
        payload = copy.deepcopy(self.fixture)
        latest = next(job for job in payload["jobs"] if job["job_key"] == "realtime-e2e" and job["run_attempt"] == 2)
        latest["conclusion"] = "skipped"
        self.assert_reason("WORKFLOW_PROVENANCE_PRODUCER_NOT_SUCCESSFUL", lambda: self.validate(payload))

    def test_140_producer_attempt_future_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["artifacts"][0]["producer_attempt"] = 3
        self.assert_reason("WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_FUTURE", lambda: self.validate(payload))

    def test_141_producer_attempt_zero_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["artifacts"][0]["producer_attempt"] = 0
        self.assert_reason("WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_INVALID", lambda: self.validate(payload))

    def test_142_producer_attempt_missing_fails(self):
        payload = copy.deepcopy(self.fixture)
        del payload["artifacts"][0]["producer_attempt"]
        self.assert_reason("WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_MISSING", lambda: self.validate(payload))

    def test_143_producer_attempt_absent_from_jobs_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["artifacts"][0]["producer_attempt"] = 2
        self.assert_reason("WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_UNKNOWN", lambda: self.validate(payload))

    def test_144_wrong_run_id_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["workflow_run_id"] = "999"
        self.assert_reason("WORKFLOW_PROVENANCE_RUN_MISMATCH", lambda: self.validate(payload))

    def test_145_wrong_head_sha_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["head_sha"] = "d" * 40
        self.assert_reason("WORKFLOW_PROVENANCE_HEAD_SHA_MISMATCH", lambda: self.validate(payload))

    def test_146_current_attempt_zero_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["current_attempt"] = 0
        self.assert_reason("WORKFLOW_PROVENANCE_CURRENT_ATTEMPT_INVALID", lambda: self.validate(payload, current_attempt=0))

    def test_147_unknown_job_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["jobs"][0]["job_key"] = "unknown"
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_UNKNOWN", lambda: self.validate(payload))

    def test_148_duplicate_job_attempt_fails(self):
        payload = copy.deepcopy(self.fixture)
        duplicate = copy.deepcopy(payload["jobs"][0])
        duplicate["job_id"] += 999999
        payload["jobs"].append(duplicate)
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_AMBIGUOUS", lambda: self.validate(payload))

    def test_149_expected_job_missing_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["jobs"] = [job for job in payload["jobs"] if job["job_key"] != "dotnet"]
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_MISSING", lambda: self.validate(payload))

    def test_150_artifact_missing_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["artifacts"] = payload["artifacts"][1:]
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_MISSING", lambda: self.validate(payload))

    def test_151_artifact_id_invalid_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["artifacts"][0]["artifact_id"] = 0
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_ID_INVALID", lambda: self.validate(payload))

    def test_152_artifact_digest_invalid_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["artifacts"][0]["artifact_digest"] = "not-a-digest"
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_DIGEST_INVALID", lambda: self.validate(payload))

    def test_153_artifact_name_mismatch_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["artifacts"][0]["artifact_name"] = "wrong"
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_NAME_MISMATCH", lambda: self.validate(payload))

    def test_154_artifact_producer_job_id_mismatch_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["artifacts"][0]["producer_job_id"] += 1
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_JOB_MISMATCH", lambda: self.validate(payload))

    def test_155_artifact_other_run_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["artifacts"][0]["workflow_run_id"] = "999"
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_RUN_MISMATCH", lambda: self.validate(payload))

    def test_156_artifact_other_sha_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["artifacts"][0]["head_sha"] = "d" * 40
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_SHA_MISMATCH", lambda: self.validate(payload))

    def test_157_artifact_reused_fails(self):
        payload = copy.deepcopy(self.fixture)
        payload["artifacts"][1]["artifact_id"] = payload["artifacts"][0]["artifact_id"]
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_REUSED", lambda: self.validate(payload))

    def test_158_multiple_artifacts_for_one_job_fails(self):
        payload = copy.deepcopy(self.fixture)
        duplicate = copy.deepcopy(payload["artifacts"][0])
        duplicate["artifact_id"] += 999999
        payload["artifacts"].append(duplicate)
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_AMBIGUOUS", lambda: self.validate(payload))

    def test_159_diagnostic_signalr_artifact_cannot_be_authoritative(self):
        payload = copy.deepcopy(self.fixture)
        artifact = next(item for item in payload["artifacts"] if item["job_key"] == "realtime-e2e")
        artifact["artifact_name"] = "realtime-e2e-failure-results-attempt-2"
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_NAME_MISMATCH", lambda: self.validate(payload))

    def test_160_manifest_missing_fails(self):
        self.assert_reason(
            "WORKFLOW_PROVENANCE_MANIFEST_MISSING",
            lambda: rel000.validate_workflow_provenance(
                self.root / "missing.json", self.trace, self.fixture["workflow_run_id"], 2
            ),
        )

    def test_161_manifest_nonparseable_fails(self):
        path = self.root / "broken.json"
        path.write_text("{", encoding="utf-8")
        self.assert_reason(
            "WORKFLOW_PROVENANCE_MANIFEST_INVALID",
            lambda: rel000.validate_workflow_provenance(path, self.trace, self.fixture["workflow_run_id"], 2),
        )

    def test_162_manifest_incomplete_fails(self):
        payload = copy.deepcopy(self.fixture)
        del payload["jobs"]
        self.assert_reason("WORKFLOW_PROVENANCE_MANIFEST_INCOMPLETE", lambda: self.validate(payload))

    def test_163_source_sha_mismatch_is_rejected(self):
        result = Rel000FocusedTests.execution_result()
        result["source_head_sha"] = "c" * 40
        self.assert_reason(
            "EXECUTION_RESULT_SHA_MISMATCH",
            lambda: rel000.validate_execution_context(result, self.trace, "100", "1"),
        )

    def test_164_tested_sha_mismatch_is_rejected(self):
        result = Rel000FocusedTests.execution_result()
        result["tested_git_sha"] = "c" * 40
        self.assert_reason(
            "EXECUTION_RESULT_SHA_MISMATCH",
            lambda: rel000.validate_execution_context(result, self.trace, "100", "1"),
        )

    def test_165_base_sha_mismatch_is_rejected(self):
        result = Rel000FocusedTests.execution_result()
        result["base_main_sha"] = "c" * 40
        self.assert_reason(
            "EXECUTION_RESULT_SHA_MISMATCH",
            lambda: rel000.validate_execution_context(result, self.trace, "100", "1"),
        )

    def test_165a_raw_partial_rerun_metadata_sanitizes_fail_closed(self):
        raw, artifacts, outputs = self.write_partial_raw_metadata()
        output = self.root / "sanitized.json"
        manifest = rel000.sanitize_workflow_provenance(
            raw,
            artifacts,
            outputs,
            output,
            self.fixture["workflow_run_id"],
            2,
            self.sha,
        )
        result = rel000.validate_workflow_provenance(
            output, self.trace, self.fixture["workflow_run_id"], 2
        )
        self.assertEqual(15, len(manifest["jobs"]))
        self.assertTrue(result["report"]["mixed_attempt_evidence"])

    def test_165b_unparseable_github_metadata_fails(self):
        raw, artifacts, outputs = self.write_partial_raw_metadata()
        (raw / "attempt-1-run.json").write_text("{", encoding="utf-8")
        self.assert_reason(
            "WORKFLOW_PROVENANCE_METADATA_INVALID",
            lambda: rel000.sanitize_workflow_provenance(
                raw,
                artifacts,
                outputs,
                self.root / "sanitized.json",
                self.fixture["workflow_run_id"],
                2,
                self.sha,
            ),
        )

    def test_165c_artifact_output_contradicting_github_metadata_fails(self):
        raw, artifacts, outputs = self.write_partial_raw_metadata()
        payload = json.loads(artifacts.read_text(encoding="utf-8"))
        payload["artifacts"][0]["digest"] = "sha256:" + "f" * 64
        artifacts.write_text(json.dumps(payload), encoding="utf-8")
        self.assert_reason(
            "WORKFLOW_PROVENANCE_ARTIFACT_DIGEST_MISMATCH",
            lambda: rel000.sanitize_workflow_provenance(
                raw,
                artifacts,
                outputs,
                self.root / "sanitized.json",
                self.fixture["workflow_run_id"],
                2,
                self.sha,
            ),
        )


class WorkflowProvenanceProfileTests(unittest.TestCase):
    """Workflow-provenance profiles: Foundation (exact 13) and PR Validation (exact 17).

    Raw GitHub-shaped attempt payloads are built deterministically here; no GitHub
    access is involved. Attempt 1 starts 08:24:51 and its jobs run 08:25–08:30;
    attempt 2 starts 08:59:34 and its re-executed jobs run from 09:00.
    """

    RUN_ID = "36000000001"
    SHA = "e" * 40
    ATTEMPT_STARTED = {1: "2026-09-21T08:24:51Z", 2: "2026-09-21T08:59:34Z"}
    JOB_HOUR = {1: "08:25", 2: "09:00"}
    FOUNDATION = rel000.WORKFLOW_PROVENANCE_PROFILE_FOUNDATION
    PR_VALIDATION = rel000.WORKFLOW_PROVENANCE_PROFILE_PR_VALIDATION

    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-profile-tests-")
        self.root = Path(self.temp.name)
        self.trace = {"source_head_sha": self.SHA, "tested_git_sha": self.SHA, "base_main_sha": self.SHA}

    def tearDown(self) -> None:
        self.temp.cleanup()

    # ----------------------------------------------------------------- fixtures

    def raw_job(self, profile, key, attempt, *, status="completed", conclusion="success", timestamps=True, job_id=None, name=None):
        names = rel000.WORKFLOW_PROVENANCE_PROFILES[profile].job_names
        index = list(names).index(key)
        job = {
            "id": job_id if job_id is not None else 90000000000 + attempt * 1000 + index,
            "name": name if name is not None else names[key],
            "run_attempt": attempt,
            "status": status,
            "conclusion": conclusion,
        }
        if timestamps:
            hour = self.JOB_HOUR[attempt]
            job["started_at"] = f"2026-09-21T{hour}:{index:02d}Z"
            job["completed_at"] = f"2026-09-21T{hour}:{index + 30:02d}Z" if status == "completed" else None
        else:
            job["started_at"] = None
            job["completed_at"] = None
        return job

    def attempt_jobs(self, profile, attempt, overrides=None):
        """Every job of the profile executed in `attempt` (aggregator in progress, downstream pending)."""
        spec = rel000.WORKFLOW_PROVENANCE_PROFILES[profile]
        jobs = []
        for key in spec.job_names:
            if key == spec.aggregator_job:
                kwargs = {"status": "in_progress", "conclusion": None}
            elif key in spec.downstream_jobs:
                kwargs = {"status": "queued", "conclusion": None, "timestamps": False}
            else:
                kwargs = {}
            kwargs.update((overrides or {}).get(key, {}))
            jobs.append(self.raw_job(profile, key, attempt, **kwargs))
        return jobs

    def artifacts(self):
        raw = []
        outputs = []
        for index, (key, name) in enumerate(rel000.EXECUTION_ARTIFACT_NAMES.items(), start=1):
            digest = f"{index:x}" * 64
            raw.append({"id": 8000 + index, "name": name, "digest": f"sha256:{digest}", "expired": False, "workflow_run": {"id": int(self.RUN_ID), "head_sha": self.SHA}})
            outputs.append({"artifact_name": name, "artifact_id": str(8000 + index), "artifact_digest": f"sha256:{digest}"})
        return raw, outputs

    def write_raw(self, profile, attempts, *, run_extra=None):
        """attempts: {attempt: jobs list}. Returns (raw_dir, artifacts_path, outputs_json)."""
        raw = self.root / f"raw-{len(list(self.root.iterdir()))}"
        raw.mkdir()
        spec = rel000.WORKFLOW_PROVENANCE_PROFILES[profile]
        for attempt, jobs in attempts.items():
            run = {"id": int(self.RUN_ID), "run_attempt": attempt, "head_sha": self.SHA, "run_started_at": self.ATTEMPT_STARTED[attempt], "name": spec.workflow_name, "event": "pull_request"}
            run.update(run_extra or {})
            (raw / f"attempt-{attempt}-run.json").write_text(json.dumps(run), encoding="utf-8")
            (raw / f"attempt-{attempt}-jobs.json").write_text(json.dumps({"total_count": len(jobs), "jobs": jobs}), encoding="utf-8")
        artifacts, outputs = self.artifacts()
        artifacts_path = raw / "artifacts.json"
        artifacts_path.write_text(json.dumps({"artifacts": artifacts}), encoding="utf-8")
        return raw, artifacts_path, json.dumps(outputs)

    def sanitize(self, profile, attempts, *, current_attempt=None, run_extra=None, workflow_profile=None):
        raw, artifacts_path, outputs = self.write_raw(profile, attempts, run_extra=run_extra)
        output = raw / "sanitized.json"
        manifest = rel000.sanitize_workflow_provenance(
            raw,
            artifacts_path,
            outputs,
            output,
            self.RUN_ID,
            current_attempt if current_attempt is not None else max(attempts),
            self.SHA,
            workflow_profile if workflow_profile is not None else profile,
        )
        return manifest, output

    def validate(self, path, current_attempt):
        return rel000.validate_workflow_provenance(path, self.trace, self.RUN_ID, current_attempt)

    def assert_reason(self, expected, action):
        with self.assertRaises(rel000.ValidationFailure) as raised:
            action()
        self.assertEqual(expected, raised.exception.reason_code)
        return raised.exception

    def partial_rerun_attempts(self, profile, *, second_attempt_overrides=None):
        """Attempt 1: producers succeed, aggregator (and downstream) fail. Attempt 2: producers
        retained with their original timestamps, aggregator re-executes, downstream pending."""
        spec = rel000.WORKFLOW_PROVENANCE_PROFILES[profile]
        first_overrides = {spec.aggregator_job: {"status": "completed", "conclusion": "failure"}}
        for key in spec.downstream_jobs:
            first_overrides[key] = {"status": "completed", "conclusion": "failure", "timestamps": True}
        first = self.attempt_jobs(profile, 1, first_overrides)
        second = []
        for job in first:
            key = next(k for k, name in spec.job_names.items() if name == job["name"])
            if key == spec.aggregator_job:
                second.append(self.raw_job(profile, key, 2, status="in_progress", conclusion=None))
            elif key in spec.downstream_jobs:
                second.append(self.raw_job(profile, key, 2, status="queued", conclusion=None, timestamps=False))
            elif key in (second_attempt_overrides or {}):
                second.append(self.raw_job(profile, key, 2, **second_attempt_overrides[key]))
            else:
                retained = copy.deepcopy(job)
                retained["id"] += 100000000
                retained["run_attempt"] = 2
                second.append(retained)
        return {1: first, 2: second}

    # ------------------------------------------------------- Foundation regression

    def test_170_foundation_exact_thirteen_jobs_pass_by_default(self):
        manifest, output = self.sanitize(self.FOUNDATION, {1: self.attempt_jobs(self.FOUNDATION, 1)}, workflow_profile=rel000.DEFAULT_WORKFLOW_PROVENANCE_PROFILE)
        self.assertEqual("foundation", manifest["workflow_profile"])
        self.assertEqual(13, len(manifest["jobs"]))
        self.assertEqual(13, len(rel000.AUTHORITATIVE_JOB_NAMES))
        result = self.validate(output, 1)
        self.assertEqual("foundation", result["workflow_profile"])
        self.assertEqual(10, len(result["artifacts_by_job"]))

    def test_171_foundation_rejects_twelve_fourteen_unknown_and_duplicate_jobs(self):
        base = self.attempt_jobs(self.FOUNDATION, 1)
        twelve = [job for job in base if job["name"] != "Validate web workspace"]
        fourteen = base + [self.raw_job(self.FOUNDATION, "web", 1, job_id=1, name="PR Gate")]
        unknown = copy.deepcopy(base)
        unknown[3]["name"] = "Secret scan"
        duplicate = copy.deepcopy(base)
        duplicate[3]["name"] = duplicate[4]["name"]
        for reason, jobs in (
            ("WORKFLOW_PROVENANCE_JOB_SET_INVALID", twelve),
            ("WORKFLOW_PROVENANCE_JOB_SET_INVALID", fourteen),
            ("WORKFLOW_PROVENANCE_JOB_UNKNOWN", unknown),
            ("WORKFLOW_PROVENANCE_JOB_AMBIGUOUS", duplicate),
        ):
            with self.subTest(reason=reason):
                self.assert_reason(reason, lambda jobs=jobs: self.sanitize(self.FOUNDATION, {1: jobs}))

    def test_172_foundation_producer_failure_and_aggregator_attempt_fail(self):
        for key, conclusion in (("normative", "failure"), ("dotnet", "skipped"), ("backup-restore", "cancelled")):
            with self.subTest(job=key):
                jobs = self.attempt_jobs(self.FOUNDATION, 1, {key: {"conclusion": conclusion}})
                self.assert_reason("WORKFLOW_PROVENANCE_PRODUCER_NOT_SUCCESSFUL", lambda jobs=jobs: self.sanitize(self.FOUNDATION, {1: jobs}))
        attempts = self.partial_rerun_attempts(self.FOUNDATION)
        stale = copy.deepcopy(attempts)
        stale[2] = [job if job["name"] != "Validate MVP-0 internal release evidence" else {**next(j for j in stale[1] if j["name"] == job["name"]), "id": job["id"], "run_attempt": 2} for job in stale[2]]
        self.assert_reason("WORKFLOW_PROVENANCE_AGGREGATOR_ATTEMPT_MISMATCH", lambda: self.sanitize(self.FOUNDATION, stale))

    def test_173_foundation_partial_rerun_semantics_unchanged(self):
        manifest, output = self.sanitize(self.FOUNDATION, self.partial_rerun_attempts(self.FOUNDATION))
        self.assertEqual(14, len(manifest["jobs"]))
        self.assertEqual({1: 13, 2: 1}, {a: sum(1 for j in manifest["jobs"] if j["run_attempt"] == a) for a in (1, 2)})
        result = self.validate(output, 2)
        self.assertFalse(result["report"]["mixed_attempt_evidence"])
        self.assertTrue(all(item["producer_attempt"] == 1 for item in result["report"]["jobs"]))
        rerun = self.partial_rerun_attempts(self.FOUNDATION, second_attempt_overrides={"dotnet": {"conclusion": "failure"}})
        self.assert_reason("WORKFLOW_PROVENANCE_PRODUCER_NOT_SUCCESSFUL", lambda: self.sanitize(self.FOUNDATION, rerun))

    def test_174_foundation_run_identity_is_checked_only_when_present(self):
        jobs = {1: self.attempt_jobs(self.FOUNDATION, 1)}
        manifest, _ = self.sanitize(self.FOUNDATION, jobs, run_extra={"name": None, "event": None})
        self.assertEqual("foundation", manifest["workflow_profile"])
        for run_extra in ({"event": "push"}, {"event": "pull_request"}):
            self.sanitize(self.FOUNDATION, jobs, run_extra=run_extra)
        self.assert_reason("WORKFLOW_PROVENANCE_WORKFLOW_MISMATCH", lambda: self.sanitize(self.FOUNDATION, jobs, run_extra={"name": "PR Validation"}))
        self.assert_reason("WORKFLOW_PROVENANCE_EVENT_INVALID", lambda: self.sanitize(self.FOUNDATION, jobs, run_extra={"event": "schedule"}))

    def test_175_foundation_pending_job_without_start_time_still_fails(self):
        jobs = self.attempt_jobs(self.FOUNDATION, 1, {"rel000": {"status": "queued", "conclusion": None, "timestamps": False}})
        self.assert_reason("WORKFLOW_PROVENANCE_METADATA_INCOMPLETE", lambda: self.sanitize(self.FOUNDATION, {1: jobs}))

    # ------------------------------------------------------- PR Validation profile

    def test_176_pr_validation_exact_seventeen_jobs_with_queued_pr_gate_pass(self):
        manifest, output = self.sanitize(self.PR_VALIDATION, {1: self.attempt_jobs(self.PR_VALIDATION, 1)})
        self.assertEqual("pr-validation", manifest["workflow_profile"])
        self.assertEqual(rel000.WORKFLOW_PROVENANCE_VERSION, manifest["format_version"])
        self.assertEqual(17, len(manifest["jobs"]))
        self.assertEqual(sorted(rel000.PR_VALIDATION_JOB_NAMES), sorted(job["job_key"] for job in manifest["jobs"]))
        gate = next(job for job in manifest["jobs"] if job["job_key"] == "pr-gate")
        self.assertEqual({"status": "queued", "conclusion": "", "run_attempt": 1}, {k: gate[k] for k in ("status", "conclusion", "run_attempt")})
        self.assertNotIn("normative", {job["job_key"] for job in manifest["jobs"]})
        self.assertEqual(10, len(manifest["artifacts"]))
        result = self.validate(output, 1)
        self.assertEqual("pr-validation", result["workflow_profile"])
        self.assertEqual("pr-validation", result["report"]["workflow_profile"])
        self.assertEqual(sorted(rel000.EXECUTION_ARTIFACT_NAMES), sorted(result["artifacts_by_job"]))

    def test_177_pr_validation_rejects_missing_observable_and_extra_jobs(self):
        base = self.attempt_jobs(self.PR_VALIDATION, 1)
        sixteen = [job for job in base if job["name"] != "Validate web workspace"]
        missing_classify = [job for job in base if job["name"] != "Classify PR impact"]
        eighteen = base + [self.raw_job(self.PR_VALIDATION, "web", 1, job_id=1, name="Validate normative baseline")]
        for jobs in (sixteen, missing_classify, eighteen):
            with self.subTest(count=len(jobs)):
                self.assert_reason("WORKFLOW_PROVENANCE_JOB_SET_INVALID", lambda jobs=jobs: self.sanitize(self.PR_VALIDATION, {1: jobs}))

    def test_178_pr_validation_rejects_unknown_renamed_and_duplicate_jobs(self):
        base = self.attempt_jobs(self.PR_VALIDATION, 1)
        unknown = copy.deepcopy(base)
        unknown[1]["name"] = "Validate normative baseline"
        renamed = copy.deepcopy(base)
        renamed[16]["name"] = "PR gate"
        duplicate = copy.deepcopy(base)
        duplicate[1]["name"] = "PR Gate"
        for reason, jobs in (
            ("WORKFLOW_PROVENANCE_JOB_UNKNOWN", unknown),
            ("WORKFLOW_PROVENANCE_JOB_UNKNOWN", renamed),
            ("WORKFLOW_PROVENANCE_JOB_AMBIGUOUS", duplicate),
        ):
            with self.subTest(reason=reason):
                self.assert_reason(reason, lambda jobs=jobs: self.sanitize(self.PR_VALIDATION, {1: jobs}))

    def test_179_pr_validation_every_upstream_control_must_succeed(self):
        for key in sorted(rel000.PR_VALIDATION_PRODUCER_JOBS):
            for conclusion in ("failure", "cancelled", "skipped"):
                with self.subTest(job=key, conclusion=conclusion):
                    jobs = self.attempt_jobs(self.PR_VALIDATION, 1, {key: {"conclusion": conclusion}})
                    exc = self.assert_reason("WORKFLOW_PROVENANCE_PRODUCER_NOT_SUCCESSFUL", lambda jobs=jobs: self.sanitize(self.PR_VALIDATION, {1: jobs}))
                    self.assertEqual(key, exc.details["job"])
        self.assertEqual(
            {"classify", "secret-scan", "normative-contracts", "azr-static"} | rel000.REQUIRED_JOBS - {"normative"},
            set(rel000.PR_VALIDATION_PRODUCER_JOBS),
        )

    def test_180_pr_validation_aggregator_must_be_current_attempt(self):
        attempts = self.partial_rerun_attempts(self.PR_VALIDATION)
        old_rel000 = next(j for j in attempts[1] if j["name"] == "Validate MVP-0 internal release evidence")
        attempts[2] = [job if job["name"] != old_rel000["name"] else {**old_rel000, "id": job["id"], "run_attempt": 2} for job in attempts[2]]
        self.assert_reason("WORKFLOW_PROVENANCE_AGGREGATOR_ATTEMPT_MISMATCH", lambda: self.sanitize(self.PR_VALIDATION, attempts))

    # -------------------------------------------------------- downstream PR Gate

    def test_181_pr_gate_is_topology_only_and_never_required_to_succeed(self):
        for overrides in (
            {"status": "queued", "conclusion": None, "timestamps": False},
            {"status": "waiting", "conclusion": None, "timestamps": False},
            {"status": "in_progress", "conclusion": None, "timestamps": False},
            {"status": "in_progress", "conclusion": None, "timestamps": True},
            {"status": "completed", "conclusion": "failure", "timestamps": True},
        ):
            with self.subTest(overrides=overrides):
                jobs = self.attempt_jobs(self.PR_VALIDATION, 1, {"pr-gate": overrides})
                manifest, output = self.sanitize(self.PR_VALIDATION, {1: jobs})
                self.assertEqual(17, len(manifest["jobs"]))
                self.validate(output, 1)

    def test_182_absent_pr_gate_is_the_expected_runtime_topology(self):
        # GitHub does not create the downstream job record until the aggregator
        # concludes, so while REL-000 sanitizes provenance only 16 jobs exist.
        without = [job for job in self.attempt_jobs(self.PR_VALIDATION, 1) if job["name"] != "PR Gate"]
        self.assertEqual(16, len(without))
        manifest, output = self.sanitize(self.PR_VALIDATION, {1: without})
        self.assertEqual(16, len(manifest["jobs"]))
        self.assertNotIn("pr-gate", {job["job_key"] for job in manifest["jobs"]})
        self.assertEqual(10, len(manifest["artifacts"]))
        self.assertEqual("pr-validation", self.validate(output, 1)["workflow_profile"])
        # the downstream job is optional, never a licence for a foreign job
        replaced = without + [self.raw_job(self.PR_VALIDATION, "pr-gate", 1, name="Validate normative baseline")]
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_UNKNOWN", lambda: self.sanitize(self.PR_VALIDATION, {1: replaced}))

    def test_182b_absent_pr_gate_still_enforces_the_observable_job_set(self):
        without = [job for job in self.attempt_jobs(self.PR_VALIDATION, 1) if job["name"] != "PR Gate"]
        # a missing observable control is still fail-closed at 15 jobs
        missing = [job for job in without if job["name"] != "Validate backup and restore"]
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_SET_INVALID", lambda: self.sanitize(self.PR_VALIDATION, {1: missing}))
        missing_classify = [job for job in without if job["name"] != "Classify PR impact"]
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_SET_INVALID", lambda: self.sanitize(self.PR_VALIDATION, {1: missing_classify}))
        # an unknown extra job is rejected even though the count reaches 17
        unknown = without + [self.raw_job(self.PR_VALIDATION, "web", 1, job_id=1, name="Validate normative baseline")]
        self.assertEqual(17, len(unknown))
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_UNKNOWN", lambda: self.sanitize(self.PR_VALIDATION, {1: unknown}))
        # producers are still required to have succeeded
        failed = [job for job in without if job["name"] != "Validate web workspace"]
        failed = failed + [self.raw_job(self.PR_VALIDATION, "web", 1, status="completed", conclusion="failure")]
        self.assert_reason("WORKFLOW_PROVENANCE_PRODUCER_NOT_SUCCESSFUL", lambda: self.sanitize(self.PR_VALIDATION, {1: failed}))

    def test_182c_foundation_profile_topology_is_unchanged(self):
        # Foundation has no downstream job: all 13 jobs stay mandatory.
        profile = rel000.WORKFLOW_PROVENANCE_PROFILES[rel000.WORKFLOW_PROVENANCE_PROFILE_FOUNDATION]
        self.assertEqual(frozenset(), profile.downstream_jobs)
        self.assertEqual(frozenset(rel000.AUTHORITATIVE_JOB_NAMES), profile.observable_jobs)
        self.assertEqual(13, len(profile.observable_jobs))
        base = self.attempt_jobs(self.FOUNDATION, 1)
        self.assertEqual(13, len(self.sanitize(self.FOUNDATION, {1: base})[0]["jobs"]))
        for name in ("Validate backup and restore", "Validate MVP-0 internal release evidence"):
            twelve = [job for job in base if job["name"] != name]
            self.assert_reason("WORKFLOW_PROVENANCE_JOB_SET_INVALID", lambda jobs=twelve: self.sanitize(self.FOUNDATION, {1: jobs}))

    def test_182d_pr_validation_observable_set_is_exactly_sixteen(self):
        profile = rel000.WORKFLOW_PROVENANCE_PROFILES[rel000.WORKFLOW_PROVENANCE_PROFILE_PR_VALIDATION]
        # the real workflow topology stays at 17 jobs
        self.assertEqual(17, len(profile.job_names))
        self.assertEqual(frozenset({"pr-gate"}), profile.downstream_jobs)
        self.assertEqual(16, len(profile.observable_jobs))
        self.assertNotIn("pr-gate", profile.observable_jobs)
        self.assertNotIn("PR Gate", profile.observable_job_names)

    def test_183_malformed_pending_pr_gate_metadata_fails(self):
        cases = (
            ("WORKFLOW_PROVENANCE_METADATA_INVALID", {"status": "queued", "conclusion": "success", "timestamps": False}),
            ("WORKFLOW_PROVENANCE_METADATA_INVALID", {"status": "completed", "conclusion": None, "timestamps": False}),
            ("WORKFLOW_PROVENANCE_METADATA_INVALID", {"status": "", "conclusion": None, "timestamps": False}),
        )
        for reason, overrides in cases:
            with self.subTest(overrides=overrides):
                jobs = self.attempt_jobs(self.PR_VALIDATION, 1, {"pr-gate": overrides})
                self.assert_reason(reason, lambda jobs=jobs: self.sanitize(self.PR_VALIDATION, {1: jobs}))
        # completed_at without started_at is contradictory
        jobs = self.attempt_jobs(self.PR_VALIDATION, 1)
        gate = next(j for j in jobs if j["name"] == "PR Gate")
        gate["completed_at"] = "2026-09-21T08:40:00Z"
        self.assert_reason("WORKFLOW_PROVENANCE_METADATA_INVALID", lambda: self.sanitize(self.PR_VALIDATION, {1: jobs}))
        # a pending downstream job is only legitimate in the current attempt
        attempts = self.partial_rerun_attempts(self.PR_VALIDATION)
        attempts[1] = self.attempt_jobs(self.PR_VALIDATION, 1, {"rel000": {"status": "completed", "conclusion": "failure"}})
        self.assert_reason("WORKFLOW_PROVENANCE_METADATA_INCOMPLETE", lambda: self.sanitize(self.PR_VALIDATION, attempts))
        # an invalid or reused job id on the downstream job still fails
        for job_id, reason in ((0, "WORKFLOW_PROVENANCE_JOB_ID_INVALID"), (90000001000, "WORKFLOW_PROVENANCE_JOB_ID_REUSED")):
            jobs = self.attempt_jobs(self.PR_VALIDATION, 1, {"pr-gate": {"job_id": job_id}})
            self.assert_reason(reason, lambda jobs=jobs: self.sanitize(self.PR_VALIDATION, {1: jobs}))

    # ------------------------------------------------------------ run identity

    def test_184_pr_validation_run_identity_is_mandatory(self):
        jobs = {1: self.attempt_jobs(self.PR_VALIDATION, 1)}
        self.assert_reason("WORKFLOW_PROVENANCE_RUN_IDENTITY_MISSING", lambda: self.sanitize(self.PR_VALIDATION, jobs, run_extra={"name": None}))
        self.assert_reason("WORKFLOW_PROVENANCE_RUN_IDENTITY_MISSING", lambda: self.sanitize(self.PR_VALIDATION, jobs, run_extra={"event": None}))
        self.assert_reason("WORKFLOW_PROVENANCE_WORKFLOW_MISMATCH", lambda: self.sanitize(self.PR_VALIDATION, jobs, run_extra={"name": "Foundation CI"}))
        self.assert_reason("WORKFLOW_PROVENANCE_EVENT_INVALID", lambda: self.sanitize(self.PR_VALIDATION, jobs, run_extra={"event": "push"}))

    # ------------------------------------------------------------- partial rerun

    def test_185_pr_validation_partial_rerun_passes(self):
        manifest, output = self.sanitize(self.PR_VALIDATION, self.partial_rerun_attempts(self.PR_VALIDATION))
        self.assertEqual("pr-validation", manifest["workflow_profile"])
        self.assertEqual(19, len(manifest["jobs"]))
        second = {job["job_key"]: job for job in manifest["jobs"] if job["run_attempt"] == 2}
        self.assertEqual({"rel000", "pr-gate"}, set(second))
        self.assertEqual("in_progress", second["rel000"]["status"])
        self.assertEqual("queued", second["pr-gate"]["status"])
        first_gate = next(job for job in manifest["jobs"] if job["job_key"] == "pr-gate" and job["run_attempt"] == 1)
        self.assertEqual("failure", first_gate["conclusion"])
        self.assertTrue(all(artifact["producer_attempt"] == 1 for artifact in manifest["artifacts"]))
        result = self.validate(output, 2)
        self.assertFalse(result["report"]["mixed_attempt_evidence"])
        self.assertEqual(2, result["latest_jobs"]["rel000"]["run_attempt"])

    def test_186_pr_validation_partial_rerun_negative_cases(self):
        rerun_failed = self.partial_rerun_attempts(self.PR_VALIDATION, second_attempt_overrides={"secret-scan": {"conclusion": "failure"}})
        self.assert_reason("WORKFLOW_PROVENANCE_PRODUCER_NOT_SUCCESSFUL", lambda: self.sanitize(self.PR_VALIDATION, rerun_failed))
        reused = self.partial_rerun_attempts(self.PR_VALIDATION)
        reused[2][4]["id"] = reused[1][4]["id"]
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_ID_REUSED", lambda: self.sanitize(self.PR_VALIDATION, reused))
        mismatch = self.partial_rerun_attempts(self.PR_VALIDATION)
        mismatch[2][4]["run_attempt"] = 1
        self.assert_reason("WORKFLOW_PROVENANCE_ATTEMPT_METADATA_INVALID", lambda: self.sanitize(self.PR_VALIDATION, mismatch))
        ambiguous = self.partial_rerun_attempts(self.PR_VALIDATION)
        ambiguous[2][4]["conclusion"] = "cancelled"  # retained record no longer matches its attempt-1 execution
        self.assert_reason("WORKFLOW_PROVENANCE_RETAINED_JOB_AMBIGUOUS", lambda: self.sanitize(self.PR_VALIDATION, ambiguous))
        # a producer re-executed in attempt 2 whose artifact still points at attempt 1 is stale
        _, output = self.sanitize(self.PR_VALIDATION, self.partial_rerun_attempts(self.PR_VALIDATION, second_attempt_overrides={"dotnet": {}}))
        payload = json.loads(output.read_text(encoding="utf-8"))
        artifact = next(item for item in payload["artifacts"] if item["job_key"] == "dotnet")
        self.assertEqual(2, artifact["producer_attempt"])
        artifact["producer_attempt"] = 1
        artifact["producer_job_id"] = next(j["job_id"] for j in payload["jobs"] if j["job_key"] == "dotnet" and j["run_attempt"] == 1)
        output.write_text(json.dumps(payload), encoding="utf-8")
        self.assert_reason("WORKFLOW_PROVENANCE_ARTIFACT_STALE_AFTER_RERUN", lambda: self.validate(output, 2))

    # ------------------------------------------------------------ manifest profile

    def test_187_manifest_profile_semantics(self):
        _, foundation = self.sanitize(self.FOUNDATION, {1: self.attempt_jobs(self.FOUNDATION, 1)})
        _, pr = self.sanitize(self.PR_VALIDATION, {1: self.attempt_jobs(self.PR_VALIDATION, 1)})
        foundation_payload = json.loads(foundation.read_text(encoding="utf-8"))
        pr_payload = json.loads(pr.read_text(encoding="utf-8"))

        def write(payload):
            path = self.root / f"manifest-{len(list(self.root.iterdir()))}.json"
            path.write_text(json.dumps(payload), encoding="utf-8")
            return path

        legacy = {k: v for k, v in foundation_payload.items() if k != "workflow_profile"}
        self.assertEqual("foundation", self.validate(write(legacy), 1)["workflow_profile"])
        self.assertEqual("pr-validation", self.validate(write(pr_payload), 1)["workflow_profile"])
        self.assert_reason("WORKFLOW_PROVENANCE_PROFILE_INVALID", lambda: self.validate(write({**pr_payload, "workflow_profile": "nightly"}), 1))
        self.assert_reason("WORKFLOW_PROVENANCE_PROFILE_INVALID", lambda: self.validate(write({**pr_payload, "workflow_profile": None}), 1))
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_UNKNOWN", lambda: self.validate(write({**pr_payload, "workflow_profile": "foundation"}), 1))
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_UNKNOWN", lambda: self.validate(write({**foundation_payload, "workflow_profile": "pr-validation"}), 1))
        legacy_pr = {k: v for k, v in pr_payload.items() if k != "workflow_profile"}
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_UNKNOWN", lambda: self.validate(write(legacy_pr), 1))
        # a pr-validation manifest sanitized before GitHub created the downstream job
        # is the expected topology and must validate
        truncated = {**pr_payload, "jobs": [job for job in pr_payload["jobs"] if job["job_key"] != "pr-gate"]}
        self.assertEqual("pr-validation", self.validate(write(truncated), 1)["workflow_profile"])
        # an observable job is still mandatory in the manifest
        no_producer = {**pr_payload, "jobs": [job for job in pr_payload["jobs"] if job["job_key"] != "web"]}
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_MISSING", lambda: self.validate(write(no_producer), 1))

    def test_188_unknown_profile_argument_fails_closed(self):
        jobs = {1: self.attempt_jobs(self.PR_VALIDATION, 1)}
        self.assert_reason("WORKFLOW_PROVENANCE_PROFILE_INVALID", lambda: self.sanitize(self.PR_VALIDATION, jobs, workflow_profile="nightly"))
        self.assert_reason("WORKFLOW_PROVENANCE_PROFILE_INVALID", lambda: self.sanitize(self.PR_VALIDATION, jobs, workflow_profile=""))
        # profile/topology mismatch at sanitize time
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_SET_INVALID", lambda: self.sanitize(self.PR_VALIDATION, jobs, workflow_profile=self.FOUNDATION, run_extra={"name": "Foundation CI"}))
        self.assert_reason("WORKFLOW_PROVENANCE_JOB_SET_INVALID", lambda: self.sanitize(self.FOUNDATION, {1: self.attempt_jobs(self.FOUNDATION, 1)}, workflow_profile=self.PR_VALIDATION, run_extra={"name": "PR Validation"}))

    def test_189_cli_accepts_profile_and_rejects_unknown(self):
        raw, artifacts_path, outputs = self.write_raw(self.PR_VALIDATION, {1: self.attempt_jobs(self.PR_VALIDATION, 1)})
        output = raw / "cli.json"
        argv = [
            "sanitize-workflow-provenance",
            "--attempts-directory", str(raw),
            "--artifacts", str(artifacts_path),
            "--artifact-outputs-json", outputs,
            "--output", str(output),
            "--workflow-run-id", self.RUN_ID,
            "--current-attempt", "1",
            "--head-sha", self.SHA,
        ]
        with patch.object(sys, "argv", ["rel000.py", *argv, "--workflow-profile", "pr-validation"]):
            self.assertEqual(0, rel000.main())
        self.assertEqual("pr-validation", json.loads(output.read_text(encoding="utf-8"))["workflow_profile"])
        with patch.object(sys, "argv", ["rel000.py", *argv, "--workflow-profile", "nightly"]), patch("sys.stderr", new=io.StringIO()) as stderr:
            self.assertEqual(1, rel000.main())
        self.assertIn("WORKFLOW_PROVENANCE_PROFILE_INVALID", stderr.getvalue())
        # Omitting the argument keeps the Foundation default, which rejects a PR Validation run.
        with patch.object(sys, "argv", ["rel000.py", *argv]), patch("sys.stderr", new=io.StringIO()) as stderr:
            self.assertEqual(1, rel000.main())
        self.assertIn("WORKFLOW_PROVENANCE_WORKFLOW_MISMATCH", stderr.getvalue())


class WorkflowPhysicalGuardsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.path = REPOSITORY_ROOT / ".github/workflows/ci.yml"
        cls.text = cls.path.read_text(encoding="utf-8")
        import yaml

        cls.workflow = yaml.safe_load(cls.text)

    def test_166_foundation_ci_keeps_exactly_thirteen_jobs(self):
        self.assertEqual(13, len(self.workflow["jobs"]))

    def test_167_rel000_permissions_are_read_only(self):
        self.assertEqual(
            {"actions": "read", "contents": "read", "issues": "read"},
            self.workflow["jobs"]["rel000"]["permissions"],
        )

    def test_168_workflow_queries_every_attempt_and_artifact_metadata(self):
        self.assertIn("/attempts/${attempt}/jobs?per_page=100", self.text)
        self.assertIn("/attempts/${attempt}", self.text)
        self.assertIn("/artifacts?per_page=100", self.text)

    def test_169_raw_metadata_is_cleaned_and_not_published(self):
        self.assertGreaterEqual(self.text.count("rel000-workflow-provenance-raw"), 2)
        publication = next(
            step
            for step in self.workflow["jobs"]["rel000"]["steps"]
            if step.get("name") == "Publish only redacted REL-000 evidence"
        )
        self.assertEqual("${{ runner.temp }}/rel000-output", publication["with"]["path"])

    def test_170_failed_signalr_junit_uses_non_authoritative_name(self):
        steps = self.workflow["jobs"]["realtime-e2e"]["steps"]
        diagnostic = next(step for step in steps if step.get("name") == "Publish failed realtime JUnit evidence")
        self.assertEqual("failure()", diagnostic["if"])
        self.assertEqual("actions/upload-artifact@v4", diagnostic["uses"])
        self.assertEqual(7, diagnostic["with"]["retention-days"])
        self.assertEqual("ignore", diagnostic["with"]["if-no-files-found"])
        self.assertFalse(diagnostic["with"]["name"].startswith("rel000-execution-"))
        self.assertNotIn("overwrite", diagnostic["with"])

    def test_171_successful_signalr_artifact_remains_authoritative(self):
        steps = self.workflow["jobs"]["realtime-e2e"]["steps"]
        authoritative = next(step for step in steps if step.get("name") == "Publish structured realtime execution evidence")
        self.assertEqual("rel000-execution-realtime-e2e", authoritative["with"]["name"])

    def test_172_signalr_execution_has_no_retry_or_timeout_change(self):
        job = self.workflow["jobs"]["realtime-e2e"]
        execute = next(step for step in job["steps"] if step.get("name") == "Execute real reconnect lifecycle")
        # A job-level wall-clock cap is allowed (it can only fail the job, never mask a reconnect
        # failure); it is pinned exactly so it cannot drift. The execution step itself stays free
        # of retry/sleep/timeout logic.
        self.assertEqual(20, job.get("timeout-minutes"))
        self.assertNotIn("timeout-minutes", execute)
        self.assertNotRegex(execute["run"].lower(), r"retry|sleep|timeout")

    def test_173_exact_authoritative_artifacts_replace_and_keep_outputs(self):
        expected_upload_steps = {
            "dotnet": "upload-dotnet-execution",
            "runtime-contracts": "upload-runtime-execution",
            "realtime-e2e": "upload-realtime-execution",
            "outbox-signalr-delivery": "upload-outbox-execution",
            "driver-stops-pwa": "upload-driver-execution",
            "public-tracking": "upload-tracking-execution",
            "operations-dashboard": "upload-operations-execution",
            "delivery-simulation": "upload-delivery-simulation",
            "infrastructure": "upload-infrastructure-execution",
            "backup-restore": "upload-backup-restore",
        }
        expected_names = rel000.EXECUTION_ARTIFACT_NAMES
        authoritative = []
        for job_key, job in self.workflow["jobs"].items():
            for step in job.get("steps", []):
                if (
                    step.get("uses") == "actions/upload-artifact@v4"
                    and step.get("with", {}).get("name") in expected_names.values()
                ):
                    authoritative.append((job_key, step))

        self.assertEqual(10, len(authoritative))
        self.assertEqual(set(expected_names), {job_key for job_key, _ in authoritative})
        self.assertEqual(
            set(expected_names.values()),
            {step["with"]["name"] for _, step in authoritative},
        )
        self.assertEqual(10, len({job_key for job_key, _ in authoritative}))

        for job_key, step in authoritative:
            self.assertEqual(expected_names[job_key], step["with"]["name"])
            self.assertEqual(expected_upload_steps[job_key], step["id"])
            self.assertEqual("actions/upload-artifact@v4", step["uses"])
            self.assertIs(True, step["with"].get("overwrite"))
            output_prefix = "artifact" if job_key in {"delivery-simulation", "backup-restore"} else "execution-artifact"
            outputs = self.workflow["jobs"][job_key]["outputs"]
            self.assertEqual(
                f"${{{{ steps.{step['id']}.outputs.artifact-id }}}}",
                outputs[f"{output_prefix}-id"],
            )
            self.assertEqual(
                f"${{{{ steps.{step['id']}.outputs.artifact-digest }}}}",
                outputs[f"{output_prefix}-digest"],
            )

        configured = json.loads(
            self.workflow["jobs"]["rel000"]["env"]["REL000_EXECUTION_ARTIFACT_OUTPUTS_JSON"]
        )
        self.assertEqual(set(expected_names.values()), {item["artifact_name"] for item in configured})
        self.assertTrue(all(item["artifact_id"] and item["artifact_digest"] for item in configured))

    def test_174_retry_timeout_and_assertion_policy_is_unchanged(self):
        expected_timeouts = {
            "normative": 15,
            "dotnet": 30,
            "runtime-contracts": 20,
            "web": 15,
            "realtime-e2e": 20,
            "outbox-signalr-delivery": 25,
            "driver-stops-pwa": 25,
            "public-tracking": 25,
            "operations-dashboard": 25,
            "delivery-simulation": 30,
            "infrastructure": 20,
            "backup-restore": 60,
            "rel000": 15,
        }
        self.assertEqual(
            expected_timeouts,
            {job_key: job.get("timeout-minutes") for job_key, job in self.workflow["jobs"].items()},
        )
        self.assertNotRegex(self.text.lower(), r"\bretry\b|\bsleep\b")
        self.assertEqual(1, self.text.count("Assert-ProjectResourcesAbsent"))
        self.assertEqual(1, self.text.count("Assert-Ops002IdentityFile"))
        upstream_guard = next(
            step
            for step in self.workflow["jobs"]["rel000"]["steps"]
            if step.get("name") == "Verify every authoritative upstream job succeeded"
        )
        self.assertIn('Where-Object { $_.Value -cne "success" }', upstream_guard["run"])
        self.assertIn('throw "REL000_AUTHORITATIVE_UPSTREAM_FAILED:', upstream_guard["run"])

    def test_203_owner_decision_and_durable_snapshot_are_explicit_inputs(self):
        validation = next(
            step
            for step in self.workflow["jobs"]["rel000"]["steps"]
            if step.get("name") == "Validate and generate redacted REL-000 evidence"
        )
        self.assertEqual(
            "docs/releases/mvp-0-owner-decision.json",
            validation["env"]["REL000_DECISION_RECORD_PATH"],
        )
        self.assertEqual(
            rel000.APPROVED_SNAPSHOT_MANIFEST_PATH,
            validation["env"]["REL000_APPROVED_EVIDENCE_MANIFEST_PATH"],
        )
        self.assertEqual(
            rel000.APPROVED_SNAPSHOT_DIRECTORY,
            validation["env"]["REL000_APPROVED_EVIDENCE_DIRECTORY"],
        )
        self.assertNotIn("/actions/artifacts/8834236041", self.text)
        self.assertNotIn("/artifacts/8834236041/zip", self.text)
        self.assertNotIn("REL000_APPROVED_ARTIFACT_METADATA_PATH", self.text)
        self.assertNotIn("REL000_APPROVED_ARTIFACT_ZIP_PATH", self.text)

    def test_204_owner_decision_is_not_a_published_fifth_file(self):
        publication = next(
            step
            for step in self.workflow["jobs"]["rel000"]["steps"]
            if step.get("name") == "Publish only redacted REL-000 evidence"
        )
        self.assertEqual("${{ runner.temp }}/rel000-output", publication["with"]["path"])
        self.assertNotIn("mvp-0-owner-decision.json", publication["with"]["path"])
        self.assertNotIn("rel000-approved-evidence", self.text)
        self.assertNotIn("retry", self.text.lower())


class SharpRemediationPolicyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-sharp-tests-")
        self.root = Path(self.temp.name)
        current_policy = json.loads(
            (REPOSITORY_ROOT / "tools/rel-000/security-remediation-policy.json").read_text(
                encoding="utf-8"
            )
        )
        sharp = copy.deepcopy(
            next(
                item
                for item in current_policy["historical_remediations"]
                if item["id"] == rel000.SHARP_REMEDIATION_ID
            )
        )
        sharp.pop("status", None)
        self.policy_value = copy.deepcopy(current_policy)
        self.policy_value["historical_remediations"] = [
            item
            for item in self.policy_value["historical_remediations"]
            if item["id"] != rel000.SHARP_REMEDIATION_ID
        ]
        self.policy_value["active_remediations"] = [sharp]

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


class BaselinePackageGraphLockfileTests(unittest.TestCase):
    baseline = "988926c7892af98015be2be9559682f156b2748b"

    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-project-lock-tests-")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def assert_reason(self, expected: str, action) -> rel000.ValidationFailure:
        with self.assertRaises(rel000.ValidationFailure) as raised:
            action()
        self.assertEqual(expected, raised.exception.reason_code)
        return raised.exception

    def test_k_changed_lockfiles_preserve_the_baseline_package_graph(self):
        all_changed = sorted(
            {
                path.replace("\\", "/")
                for output in (
                    rel000.run_git(
                        REPOSITORY_ROOT, "diff", "--name-only", self.baseline
                    ),
                    rel000.run_git(
                        REPOSITORY_ROOT,
                        "ls-files",
                        "--others",
                        "--exclude-standard",
                    ),
                )
                for path in output.splitlines()
                if path.strip()
            }
        )
        changed_lockfiles = sorted(
            path for path in all_changed if Path(path).name == "packages.lock.json"
        )
        lockfiles = rel000.validate_baseline_package_graph_lockfile_diff(
            REPOSITORY_ROOT,
            self.baseline,
            all_changed,
        )
        self.assertTrue(changed_lockfiles)
        self.assertEqual(changed_lockfiles, lockfiles)

    def existing_lock_repo(self):
        root = Path(self.temp.name) / "repo"
        project = root / "sample/Sample.csproj"
        lock = root / "sample/packages.lock.json"
        project.parent.mkdir(parents=True)
        (root / "Directory.Packages.props").write_text(
            "<Project><PropertyGroup>"
            "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>"
            "</PropertyGroup></Project>\n",
            encoding="utf-8",
        )
        project.write_text("<Project Sdk=\"Microsoft.NET.Sdk\" />\n", encoding="utf-8")
        source = REPOSITORY_ROOT / "tests/Paqueteria.UnitTests/packages.lock.json"
        baseline_lock = subprocess.check_output(
            ["git", "show", f"{self.baseline}:tests/Paqueteria.UnitTests/packages.lock.json"],
            cwd=REPOSITORY_ROOT,
            text=True,
        )
        lock.write_text(baseline_lock, encoding="utf-8")
        subprocess.run(["git", "init", "-q"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.email", "rel000@example.invalid"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.name", "REL-000 Tests"], cwd=root, check=True)
        subprocess.run(["git", "add", "."], cwd=root, check=True)
        subprocess.run(["git", "commit", "-qm", "base"], cwd=root, check=True)
        base = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
        return root, base, lock, source

    def new_project_repo(self):
        root = Path(self.temp.name) / "repo"
        catalog = root / "catalog"
        catalog.mkdir(parents=True)
        (root / "Directory.Packages.props").write_text(
            "<Project><PropertyGroup>"
            "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>"
            "</PropertyGroup><ItemGroup>"
            '<PackageVersion Include="Baseline.Direct" Version="1.2.3" />'
            "</ItemGroup></Project>\n",
            encoding="utf-8",
        )
        (catalog / "Catalog.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk" />\n', encoding="utf-8"
        )
        direct = {
            "type": "Direct",
            "requested": "[1.2.3, )",
            "resolved": "1.2.3",
            "contentHash": "baseline-direct-hash",
            "dependencies": {"Baseline.Transitive": "4.5.6"},
        }
        transitive = {
            "type": "Transitive",
            "resolved": "4.5.6",
            "contentHash": "baseline-transitive-hash",
        }
        baseline_lock = {
            "version": 2,
            "dependencies": {
                "net10.0": {
                    "Baseline.Direct": direct,
                    "Baseline.Transitive": transitive,
                }
            },
        }
        (catalog / "packages.lock.json").write_text(
            json.dumps(baseline_lock, indent=2) + "\n", encoding="utf-8"
        )
        subprocess.run(["git", "init", "-q"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.email", "rel000@example.invalid"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.name", "REL-000 Tests"], cwd=root, check=True)
        subprocess.run(["git", "add", "."], cwd=root, check=True)
        subprocess.run(["git", "commit", "-qm", "base"], cwd=root, check=True)
        base = subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=root, text=True
        ).strip()
        project = root / "new/New.csproj"
        lock = root / "new/packages.lock.json"
        project.parent.mkdir(parents=True)
        return root, base, project, lock, copy.deepcopy(baseline_lock)

    @staticmethod
    def write_new_project(project: Path, package_body: str = "") -> None:
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>'
            f"{package_body}"
            '<ProjectReference Include="../catalog/Catalog.csproj" />'
            "</ItemGroup></Project>\n",
            encoding="utf-8",
        )

    @staticmethod
    def write_lock(lock: Path, value: dict[str, Any]) -> None:
        lock.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

    def test_a_existing_lock_allows_only_project_node_changes(self):
        root, base, lock, source = self.existing_lock_repo()
        current = json.loads(lock.read_text(encoding="utf-8"))
        current["dependencies"]["net10.0"]["notifications.domain"] = {
            "type": "Project",
            "dependencies": {"Paqueteria.Domain": "[1.0.0, )"},
        }
        lock.write_text(json.dumps(current, indent=2) + "\n", encoding="utf-8")
        result = rel000.validate_dependency_diff(root, base, rel000.NORMAL_RELEASE_EVIDENCE)
        self.assertEqual("CLEAN", result["dependency_diff_against_base"])
        self.assertEqual(
            ["sample/packages.lock.json"],
            result["baseline_package_graph_lockfiles"],
        )

    def test_b_existing_lock_package_node_drift_fails_closed(self):
        root, base, lock, source = self.existing_lock_repo()
        current = json.loads(lock.read_text(encoding="utf-8"))
        package = next(
            node
            for node in current["dependencies"]["net10.0"].values()
            if node.get("type") != "Project" and "resolved" in node
        )
        package["resolved"] = "999.0.0"
        lock.write_text(json.dumps(current, indent=2) + "\n", encoding="utf-8")
        with self.assertRaises(rel000.ValidationFailure) as raised:
            rel000.validate_dependency_diff(root, base, rel000.NORMAL_RELEASE_EVIDENCE)
        self.assertEqual("UNAUTHORIZED_DEPENDENCY_FILE_CHANGED", raised.exception.reason_code)

    def test_c_new_project_with_only_project_references_is_clean(self):
        root, base, project, lock, current = self.new_project_repo()
        self.write_new_project(project)
        current["dependencies"]["net10.0"] = {
            "catalog": {"type": "Project"}
        }
        self.write_lock(lock, current)
        result = rel000.validate_dependency_diff(root, base, rel000.NORMAL_RELEASE_EVIDENCE)
        self.assertEqual(
            ["new/packages.lock.json"],
            result["baseline_package_graph_lockfiles"],
        )

    def test_d_new_project_allows_baseline_central_direct_package(self):
        root, base, project, lock, current = self.new_project_repo()
        self.write_new_project(
            project, '<PackageReference Include="Baseline.Direct" />'
        )
        self.write_lock(lock, current)
        result = rel000.validate_dependency_diff(root, base, rel000.NORMAL_RELEASE_EVIDENCE)
        self.assertEqual("CLEAN", result["dependency_diff_against_base"])
        self.assertEqual(
            ["new/packages.lock.json"],
            result["baseline_package_graph_lockfiles"],
        )

    def test_e_transitive_baseline_package_cannot_be_promoted_to_direct(self):
        root, base, project, lock, current = self.new_project_repo()
        self.write_new_project(
            project, '<PackageReference Include="Baseline.Transitive" />'
        )
        nodes = current["dependencies"]["net10.0"]
        nodes.pop("Baseline.Direct")
        nodes["Baseline.Transitive"]["type"] = "Direct"
        nodes["Baseline.Transitive"]["requested"] = "[4.5.6, )"
        self.write_lock(lock, current)
        self.assert_reason(
            "NUGET_NEW_PROJECT_DIRECT_PACKAGE_NOT_BASELINE_CENTRAL",
            lambda: rel000.validate_dependency_diff(root, base, rel000.NORMAL_RELEASE_EVIDENCE),
        )

    def test_f_new_direct_package_id_fails_closed(self):
        root, base, project, lock, current = self.new_project_repo()
        self.write_new_project(project, '<PackageReference Include="New.Package" />')
        current["dependencies"]["net10.0"] = {
            "New.Package": {
                "type": "Direct",
                "requested": "[1.0.0, )",
                "resolved": "1.0.0",
                "contentHash": "new-hash",
            }
        }
        self.write_lock(lock, current)
        self.assert_reason(
            "NUGET_NEW_PROJECT_DIRECT_PACKAGE_NOT_BASELINE_CENTRAL",
            lambda: rel000.validate_dependency_diff(root, base, rel000.NORMAL_RELEASE_EVIDENCE),
        )

    def test_g_local_package_reference_version_fails_closed(self):
        root, base, project, lock, current = self.new_project_repo()
        self.write_new_project(
            project,
            '<PackageReference Include="Baseline.Direct" Version="1.2.3" />',
        )
        self.write_lock(lock, current)
        self.assert_reason(
            "NUGET_NEW_PROJECT_LOCAL_VERSION",
            lambda: rel000.validate_dependency_diff(root, base, rel000.NORMAL_RELEASE_EVIDENCE),
        )

    def test_h_package_reference_version_override_fails_closed(self):
        root, base, project, lock, current = self.new_project_repo()
        self.write_new_project(
            project,
            '<PackageReference Include="Baseline.Direct" VersionOverride="1.2.3" />',
        )
        self.write_lock(lock, current)
        self.assert_reason(
            "NUGET_NEW_PROJECT_LOCAL_VERSION",
            lambda: rel000.validate_dependency_diff(root, base, rel000.NORMAL_RELEASE_EVIDENCE),
        )

    def test_i_new_lock_package_metadata_drift_fails_closed(self):
        root, base, project, lock, current = self.new_project_repo()
        self.write_new_project(
            project, '<PackageReference Include="Baseline.Direct" />'
        )
        current["dependencies"]["net10.0"]["Baseline.Transitive"][
            "contentHash"
        ] = "altered-hash"
        self.write_lock(lock, current)
        self.assert_reason(
            "UNAUTHORIZED_DEPENDENCY_FILE_CHANGED",
            lambda: rel000.validate_dependency_diff(root, base, rel000.NORMAL_RELEASE_EVIDENCE),
        )

    def test_j_custom_restore_source_fails_closed(self):
        root, base, project, lock, current = self.new_project_repo()
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            "<RestoreAdditionalProjectSources>https://packages.invalid/v3/index.json"
            "</RestoreAdditionalProjectSources></PropertyGroup><ItemGroup>"
            '<PackageReference Include="Baseline.Direct" />'
            "</ItemGroup></Project>\n",
            encoding="utf-8",
        )
        self.write_lock(lock, current)
        self.assert_reason(
            "NUGET_NEW_PROJECT_SOURCE_OVERRIDE",
            lambda: rel000.validate_dependency_diff(root, base, rel000.NORMAL_RELEASE_EVIDENCE),
        )


class WebTransitiveRemediationPolicyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-web-transitive-tests-")
        self.root = Path(self.temp.name)
        self.policy_path = REPOSITORY_ROOT / "tools/rel-000/security-remediation-policy.json"
        self.policy_value = json.loads(self.policy_path.read_text(encoding="utf-8"))

    def tearDown(self) -> None:
        self.temp.cleanup()

    def assert_reason(self, expected: str, action) -> rel000.ValidationFailure:
        with self.assertRaises(rel000.ValidationFailure) as raised:
            action()
        self.assertEqual(expected, raised.exception.reason_code)
        return raised.exception

    def policy(self):
        return rel000.load_remediation_policy(self.policy_path)

    def authorization(self):
        return copy.deepcopy(
            next(
                item
                for item in self.policy_value["active_remediations"]
                if item["id"] == rel000.WEB_TRANSITIVE_REMEDIATION_ID
            )
        )

    @staticmethod
    def advisory(advisory_id: str, package: str, severity: str, version: str):
        return {
            "advisory_id": advisory_id,
            "package": package,
            "installed_versions": [version],
            "severity": severity,
            "affected_range": "authorized-base-range",
            "patched_range": "authorized-target-range",
            "direct_or_transitive": "transitive",
            "dependency_path_count": 1,
            "fix_available": True,
            "fix_compatibility": "compatible_patch_available",
        }

    def base_advisories(self):
        return [
            self.advisory("GHSA-fxqj-rqcc-2cmp", "postcss", "moderate", "8.5.21"),
            self.advisory(
                "GHSA-rgw5-rvv9-x895",
                "brace-expansion",
                "high",
                "1.1.17,5.0.8",
            ),
            self.advisory("GHSA-2v37-7h3g-55p8", "nanoid", "high", "3.3.16"),
            self.advisory("GHSA-5p4m-2wfm-xmqj", "js-yaml", "high", "4.3.0"),
            self.advisory("GHSA-q939-rpr3-3284", "SSH.NET", "high", "2025.1.0"),
        ]

    @staticmethod
    def audit(advisories):
        values = copy.deepcopy(advisories)
        totals = {
            key: sum(item["severity"] == key for item in values)
            for key in ("critical", "high", "moderate", "low")
        }
        totals["total"] = len(values)
        return {
            "command_executed": True,
            "command_exit_code": 1 if values else 0,
            "parse_succeeded": True,
            "totals": totals,
            "advisories": values,
        }

    def security_inputs(self, *, branch=None, issue_state="OPEN", tracked_ids=None):
        authorization = self.authorization()
        return (
            {
                "number": 5,
                "state": "CLOSED",
                "title": "Sharp historical remediation",
                "url": "https://github.com/example/issues/5",
                "tracked_advisory_ids": [rel000.ISSUE5_ADVISORY],
            },
            {
                "number": 38,
                "state": issue_state,
                "title": authorization["tracked_issue_title"],
                "url": "https://github.com/example/issues/38",
                "tracked_advisory_ids": tracked_ids
                if tracked_ids is not None
                else authorization["issue_advisories"]["38"],
                "related_issue": {
                    "number": 40,
                    "state": issue_state,
                    "title": authorization["related_tracked_issue_title"],
                    "url": "https://github.com/example/issues/40",
                    "tracked_advisory_ids": authorization["issue_advisories"]["40"],
                },
            },
            self.audit(self.base_advisories()),
            branch if branch is not None else self.audit([]),
            {
                "dependency_diff_against_base": "AUTHORIZED_SECURITY_REMEDIATION",
                "vulnerable_lock_versions": [],
            },
        )

    def dependency_repo(self, source_sha=None):
        authorization = self.authorization()
        source_sha = source_sha or authorization["validated_target_sha"]
        root = self.root / "repo"
        authorized_files = sorted(
            set(authorization["allowed_dependency_files"])
            | set(authorization["allowed_non_dependency_files"])
        )
        input_files = sorted(set(authorized_files) | {"apps/web/package.json"})
        for relative in input_files:
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            content = subprocess.check_output(
                ["git", "show", f"{authorization['authorized_base_sha']}:{relative}"],
                cwd=REPOSITORY_ROOT,
                text=True,
            )
            path.write_text(content, encoding="utf-8")
        subprocess.run(["git", "init", "-q"], cwd=root, check=True)
        subprocess.run(
            ["git", "config", "user.email", "rel000@example.invalid"], cwd=root, check=True
        )
        subprocess.run(
            ["git", "config", "user.name", "REL-000 Tests"], cwd=root, check=True
        )
        subprocess.run(["git", "add", "."], cwd=root, check=True)
        subprocess.run(["git", "commit", "-qm", "base"], cwd=root, check=True)
        base = subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=root, text=True
        ).strip()
        for relative in authorized_files:
            source = (
                subprocess.check_output(
                    ["git", "show", f"{source_sha}:{relative}"],
                    cwd=REPOSITORY_ROOT,
                    text=True,
                )
                if source_sha
                else (REPOSITORY_ROOT / relative).read_text(encoding="utf-8")
            )
            (root / relative).write_text(
                source,
                encoding="utf-8",
            )
        return root, base, authorization

    def validate_dependency(self, root, base, authorization=None):
        return rel000.validate_dependency_diff(
            root,
            base,
            rel000.SECURITY_REMEDIATION,
            authorization or self.authorization(),
        )

    def test_web_transitive_authorization_remains_active_and_unchanged(self):
        policy = self.policy()
        self.assertEqual(
            {rel000.WEB_TRANSITIVE_REMEDIATION_ID, rel000.NEXT_CRITICAL_REMEDIATION_ID},
            {item["id"] for item in policy["active_remediations"]},
        )
        historical = {item["id"]: item for item in policy["historical_remediations"]}
        self.assertEqual("MERGED", historical[rel000.SHARP_REMEDIATION_ID]["status"])

    def test_web_transitive_mode_requires_exact_branch_and_id(self):
        policy = self.policy()
        authorization = self.authorization()
        self.assertEqual(
            rel000.SECURITY_REMEDIATION,
            rel000.resolve_rel000_mode(
                policy,
                authorization["authorized_source_branch"],
                authorization["id"],
            ),
        )
        self.assert_reason(
            "REMEDIATION_ID_REQUIRED",
            lambda: rel000.resolve_rel000_mode(
                policy, authorization["authorized_source_branch"]
            ),
        )

    def test_validated_target_matches_exact_dependency_authorization(self):
        authorization = self.authorization()
        self.assertEqual(
            "988926c7892af98015be2be9559682f156b2748b",
            authorization["validated_target_sha"],
        )
        root, base, authorization = self.dependency_repo(
            authorization["validated_target_sha"]
        )
        result = rel000.validate_dependency_diff(
            root,
            base,
            rel000.SECURITY_REMEDIATION,
            authorization,
        )
        self.assertEqual(475, result["lock_package_count"])
        self.assertEqual(["nanoid@3.3.18"], [key for key in result["added_lock_package_keys"] if key.startswith("nanoid@")])
        self.assertEqual("2026.0.0", result["nuget_versions"]["SSH.NET"])

    def test_nanoid_source_reconciliation_is_exact(self):
        reconciliation = self.authorization()["nanoid_source_reconciliation"]
        self.assertEqual("<3.3.18", reconciliation["github_advisory_affected_range"])
        self.assertEqual("3.3.18", reconciliation["github_advisory_first_patched_version"])
        self.assertEqual("ONE_HIGH_ADVISORY", reconciliation["pnpm_3_3_17_result"])
        self.assertEqual("AUDIT_ZERO", reconciliation["pnpm_3_3_18_result"])

    def test_exact_dependency_files_and_non_dependency_allowlist(self):
        authorization = self.authorization()
        self.assertEqual(
            {
                "apps/web/pnpm-lock.yaml",
                "apps/web/pnpm-workspace.yaml",
                "Directory.Packages.props",
                "tests/Paqueteria.ContractTests/packages.lock.json",
                "tests/Paqueteria.IntegrationTests/packages.lock.json",
            },
            set(authorization["required_dependency_files"]),
        )
        self.assertEqual(
            {
                ".github/workflows/ci.yml",
                "tests/fixtures/rel-000/security-tracking.json",
                "tools/rel-000/rel000.py",
                "tools/rel-000/security-remediation-policy.json",
                "tools/rel-000/test_rel000.py",
            },
            set(authorization["allowed_non_dependency_files"]),
        )

    def test_package_manifest_change_is_rejected(self):
        root, base, authorization = self.dependency_repo()
        path = root / "apps/web/package.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["dependencies"]["nanoid"] = "3.3.18"
        path.write_text(json.dumps(value), encoding="utf-8")
        self.assert_reason(
            "UNAUTHORIZED_DEPENDENCY_FILE_CHANGED",
            lambda: self.validate_dependency(root, base, authorization),
        )

    def test_nanoid_3_3_17_target_is_rejected(self):
        root, base, authorization = self.dependency_repo()
        workspace = root / "apps/web/pnpm-workspace.yaml"
        workspace.write_text(
            workspace.read_text(encoding="utf-8")
            .replace("nanoid@>=3.0.0 <3.3.18", "nanoid@>=3.0.0 <3.3.17")
            .replace(": 3.3.18", ": 3.3.17"),
            encoding="utf-8",
        )
        lock = root / "apps/web/pnpm-lock.yaml"
        lock.write_text(lock.read_text(encoding="utf-8").replace("nanoid@3.3.18", "nanoid@3.3.17").replace("nanoid: 3.3.18", "nanoid: 3.3.17"), encoding="utf-8")
        self.assert_reason(
            "SECURITY_REMEDIATION_OVERRIDE_INVALID",
            lambda: self.validate_dependency(root, base, authorization),
        )

    def test_release_age_exclusion_is_rejected(self):
        root, base, authorization = self.dependency_repo()
        workspace = root / "apps/web/pnpm-workspace.yaml"
        workspace.write_text(
            workspace.read_text(encoding="utf-8").replace(
                "overrides:\n",
                "minimumReleaseAgeExclude:\n  - postcss@8.5.23\noverrides:\n",
            ),
            encoding="utf-8",
        )
        self.assert_reason(
            "RELEASE_AGE_EXCLUSION_RETAINED",
            lambda: self.validate_dependency(root, base, authorization),
        )

    def test_sharp_integrity_drift_is_rejected(self):
        root, base, authorization = self.dependency_repo()
        lock = root / "apps/web/pnpm-lock.yaml"
        lock.write_text(
            lock.read_text(encoding="utf-8").replace(
                "sha512-ej0zVHuZGHCiABXcNxeYhpRnPNPAcvbG8RMdBAhDAxLKkCRVSpK3Iyu7qbqw3JMzoj0REeM6f3tJLtVwl0023Q==",
                "sha512-unauthorized-integrity",
            ),
            encoding="utf-8",
        )
        self.assert_reason(
            "SHARP_LOCK_INTEGRITY_CHANGED",
            lambda: self.validate_dependency(root, base, authorization),
        )

    def test_unexpected_file_is_rejected(self):
        root, base, authorization = self.dependency_repo()
        path = root / "unexpected.txt"
        path.write_text("outside allowlist", encoding="utf-8")
        self.assert_reason(
            "SECURITY_REMEDIATION_FILE_SCOPE_INVALID",
            lambda: self.validate_dependency(root, base, authorization),
        )

    def test_issue_38_and_zero_audit_are_pending_merge(self):
        result = rel000.validate_issue_and_audit(
            *self.security_inputs(),
            rel000.SECURITY_REMEDIATION,
            self.authorization(),
        )
        self.assertEqual("REMEDIATED_PENDING_MERGE", result["dependency_security_status"])
        self.assertEqual("Issue #38", result["known_security_issues"][1]["id"])
        self.assertEqual(4, result["known_security_issues"][1]["tracked_advisories"])
        self.assertEqual("Issue #40", result["known_security_issues"][2]["id"])
        self.assertEqual(1, result["known_security_issues"][2]["tracked_advisories"])

    def test_issue_38_wrong_advisory_set_is_rejected(self):
        self.assert_reason(
            "ADDITIONAL_SECURITY_TRACKING_INVALID",
            lambda: rel000.validate_issue_and_audit(
                *self.security_inputs(tracked_ids=["GHSA-1111-2222-3333"]),
                rel000.SECURITY_REMEDIATION,
                self.authorization(),
            ),
        )

    def test_issue_40_is_required_with_exact_ssh_net_advisory(self):
        inputs = list(self.security_inputs())
        inputs[1].pop("related_issue")
        self.assert_reason(
            "RELATED_SECURITY_TRACKING_INVALID",
            lambda: rel000.validate_issue_and_audit(
                *inputs,
                rel000.SECURITY_REMEDIATION,
                self.authorization(),
            ),
        )

    def test_nuget_lock_drift_outside_exact_graph_is_rejected(self):
        root, base, authorization = self.dependency_repo()
        lock = root / "tests/Paqueteria.ContractTests/packages.lock.json"
        lock.write_text(
            lock.read_text(encoding="utf-8").replace(
                '"resolved": "2.7.0"', '"resolved": "2.6.2"', 1
            ),
            encoding="utf-8",
        )
        self.assert_reason(
            "NUGET_LOCK_VERSION_INVALID",
            lambda: self.validate_dependency(root, base, authorization),
        )

    def test_combined_audit_sanitizer_deduplicates_nuget_projects(self):
        web_input = self.root / "web-audit.json"
        nuget_input = self.root / "nuget-audit.json"
        output = self.root / "combined-audit.json"
        web_input.write_text('{"vulnerabilities": {}}', encoding="utf-8")
        package = {
            "id": "SSH.NET",
            "resolvedVersion": "2025.1.0",
            "vulnerabilities": [
                {
                    "severity": "High",
                    "advisoryurl": "https://github.com/advisories/GHSA-q939-rpr3-3284",
                }
            ],
        }
        nuget_input.write_text(
            json.dumps(
                {
                    "version": 1,
                    "projects": [
                        {
                            "frameworks": [
                                {"transitivePackages": [package]},
                                {"transitivePackages": [package]},
                            ]
                        }
                    ],
                }
            ),
            encoding="utf-8",
        )
        rel000.sanitize_audit(web_input, output, True, 0, [nuget_input])
        audit = json.loads(output.read_text(encoding="utf-8"))
        self.assertEqual(1, audit["totals"]["total"])
        self.assertEqual("GHSA-Q939-RPR3-3284", audit["advisories"][0]["advisory_id"])
        self.assertEqual(2, audit["advisories"][0]["dependency_path_count"])

    def test_closed_consolidated_tracking_passes_zero_audit_on_main(self):
        inputs = list(self.security_inputs(issue_state="CLOSED"))
        inputs[2] = self.audit([])
        inputs[3] = self.audit([])
        inputs[4] = {
            "dependency_diff_against_base": "CLEAN",
            "vulnerable_lock_versions": [],
        }
        result = rel000.validate_issue_and_audit(
            *inputs,
            rel000.NORMAL_RELEASE_EVIDENCE,
            None,
        )
        self.assertEqual("PASSED", result["dependency_security_status"])

    def test_issue_38_must_remain_open_until_merge(self):
        self.assert_reason(
            "ADDITIONAL_SECURITY_ISSUE_STATE_INVALID",
            lambda: rel000.validate_issue_and_audit(
                *self.security_inputs(issue_state="CLOSED"),
                rel000.SECURITY_REMEDIATION,
                self.authorization(),
            ),
        )

    def test_nanoid_3_3_17_branch_audit_is_rejected(self):
        nanoid = self.advisory(
            "GHSA-2v37-7h3g-55p8", "nanoid", "high", "3.3.17"
        )
        self.assert_reason(
            "BRANCH_AUDIT_NOT_ZERO",
            lambda: rel000.validate_issue_and_audit(
                *self.security_inputs(branch=self.audit([nanoid])),
                rel000.SECURITY_REMEDIATION,
                self.authorization(),
            ),
        )

    def test_fixture_and_ci_bind_exact_issue_branch_and_remediation(self):
        fixture = json.loads(
            (REPOSITORY_ROOT / "tests/fixtures/rel-000/security-tracking.json").read_text(
                encoding="utf-8"
            )
        )
        workflow = (REPOSITORY_ROOT / ".github/workflows/ci.yml").read_text(
            encoding="utf-8"
        )
        self.assertEqual(38, fixture["issue_number"])
        self.assertEqual(48, fixture["remediation_issues"][rel000.NEXT_CRITICAL_REMEDIATION_ID])
        self.assertIn("fix/security-2026-08-web-transitives", workflow)
        self.assertIn("SEC-2026-08-SECURITY-BASELINE", workflow)
        self.assertIn("fix/security-2026-09-next-critical", workflow)
        self.assertIn("SEC-2026-09-NEXT-CRITICAL", workflow)
        self.assertNotIn("github.head_ref == 'fix/security-sharp-035-override'", workflow)


class NextCriticalRemediationPolicyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-next-critical-tests-")
        self.root = Path(self.temp.name)
        self.policy_path = REPOSITORY_ROOT / "tools/rel-000/security-remediation-policy.json"
        self.policy_value = json.loads(self.policy_path.read_text(encoding="utf-8"))

    def tearDown(self) -> None:
        self.temp.cleanup()

    def assert_reason(self, expected: str, action) -> rel000.ValidationFailure:
        with self.assertRaises(rel000.ValidationFailure) as raised:
            action()
        self.assertEqual(expected, raised.exception.reason_code)
        return raised.exception

    def policy(self):
        return rel000.load_remediation_policy(self.policy_path)

    def authorization(self):
        return copy.deepcopy(
            next(
                item
                for item in self.policy_value["active_remediations"]
                if item["id"] == rel000.NEXT_CRITICAL_REMEDIATION_ID
            )
        )

    def dependency_repo(self):
        authorization = self.authorization()
        root = self.root / "repo"
        files = authorization["required_dependency_files"]
        for relative in files:
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(
                subprocess.check_output(
                    ["git", "show", f"{authorization['authorized_base_sha']}:{relative}"],
                    cwd=REPOSITORY_ROOT,
                )
            )
        subprocess.run(["git", "init", "-q"], cwd=root, check=True)
        subprocess.run(
            ["git", "config", "user.email", "rel000@example.invalid"], cwd=root, check=True
        )
        subprocess.run(
            ["git", "config", "user.name", "REL-000 Tests"], cwd=root, check=True
        )
        subprocess.run(["git", "add", "."], cwd=root, check=True)
        subprocess.run(["git", "commit", "-qm", "base"], cwd=root, check=True)
        base = subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=root, text=True
        ).strip()
        for relative in files:
            (root / relative).write_bytes((REPOSITORY_ROOT / relative).read_bytes())
        return root, base, authorization

    def validate_dependency(self, root, base, authorization=None):
        return rel000.validate_dependency_diff(
            root,
            base,
            rel000.SECURITY_REMEDIATION,
            authorization or self.authorization(),
        )

    @staticmethod
    def advisory(advisory_id: str, package: str, severity: str, version: str):
        return {
            "advisory_id": advisory_id,
            "package": package,
            "installed_versions": [version],
            "severity": severity,
            "affected_range": "authorized-base-range",
            "patched_range": "authorized-target-range",
            "direct_or_transitive": "direct" if package in {"next", "vitest"} else "transitive",
            "dependency_path_count": 1,
            "fix_available": True,
            "fix_compatibility": "compatible_patch_available",
        }

    def base_advisories(self):
        return [
            self.advisory("GHSA-p293-qw3h-jr36", "next", "critical", "16.2.11"),
            self.advisory("GHSA-2xp9-vwfh-vxw4", "next", "critical", "16.2.11"),
            self.advisory("GHSA-c83g-rgw3-j3cx", "browserslist", "high", "4.28.6"),
            self.advisory("GHSA-73wf-gq98-2v4g", "browserslist", "high", "4.28.6"),
            self.advisory("GHSA-rgj7-g3m4-5g8c", "sharp", "high", "0.35.3"),
            self.advisory("GHSA-2883-xcg3-v3hh", "js-yaml", "high", "4.3.1"),
            self.advisory("GHSA-82fw-gwwq-j7x9", "vitest", "moderate", "4.1.10"),
            self.advisory("GHSA-82fw-gwwq-j7x9", "@vitest/mocker", "moderate", "4.1.10"),
            self.advisory(
                "GHSA-w5vr-8v7q-w6rv", "baseline-browser-mapping", "moderate", "2.10.43"
            ),
        ]

    @staticmethod
    def audit(advisories):
        values = copy.deepcopy(advisories)
        totals = {
            key: sum(item["severity"] == key for item in values)
            for key in ("critical", "high", "moderate", "low")
        }
        totals["total"] = len(values)
        return {
            "command_executed": True,
            "command_exit_code": 1 if values else 0,
            "parse_succeeded": True,
            "totals": totals,
            "advisories": values,
        }

    def test_next_critical_mode_requires_exact_branch_id_and_base(self):
        policy = self.policy()
        authorization = self.authorization()
        self.assertEqual(
            rel000.SECURITY_REMEDIATION,
            rel000.resolve_rel000_mode(
                policy, authorization["authorized_source_branch"], authorization["id"]
            ),
        )
        self.assertIsNotNone(
            rel000.validate_mode_authorization(
                rel000.SECURITY_REMEDIATION,
                policy,
                authorization["authorized_source_branch"],
                authorization["authorized_base_sha"],
                authorization["id"],
            )
        )

    def test_wrong_base_sha_is_rejected(self):
        authorization = self.authorization()
        self.assert_reason(
            "SECURITY_REMEDIATION_BASE_MISMATCH",
            lambda: rel000.validate_mode_authorization(
                rel000.SECURITY_REMEDIATION,
                self.policy(),
                authorization["authorized_source_branch"],
                "0" * 40,
                authorization["id"],
            ),
        )

    def test_expected_advisory_ids_and_cve_are_exact(self):
        authorization = self.authorization()
        self.assertEqual(
            set(authorization["expected_base_advisories"]),
            set(authorization["issue_advisories"]["48"]),
        )
        self.assertEqual(
            "CVE-2026-75604", authorization["expected_cves"]["GHSA-p293-qw3h-jr36"]
        )

    def test_vulnerable_and_target_versions_are_exact(self):
        changes = self.authorization()["expected_direct_version_changes"]
        self.assertEqual({"from": "16.2.11", "to": "16.3.3"}, changes["next"])
        self.assertEqual(changes["next"], changes["eslint-config-next"])
        self.assertEqual("16.3.3", self.authorization()["next_first_patched_version"])

    def test_previous_next_version_is_rejected(self):
        root, base, authorization = self.dependency_repo()
        path = root / "apps/web/package.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["dependencies"]["next"] = "16.3.2"
        value["devDependencies"]["eslint-config-next"] = "16.3.2"
        path.write_text(json.dumps(value), encoding="utf-8")
        self.assert_reason(
            "SECURITY_REMEDIATION_VERSION_INVALID",
            lambda: self.validate_dependency(root, base, authorization),
        )

    def test_prerelease_next_version_is_rejected(self):
        root, base, authorization = self.dependency_repo()
        path = root / "apps/web/package.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["dependencies"]["next"] = "16.3.3-rc.0"
        value["devDependencies"]["eslint-config-next"] = "16.3.3-rc.0"
        path.write_text(json.dumps(value), encoding="utf-8")
        self.assert_reason(
            "PRERELEASE_DEPENDENCY_REJECTED",
            lambda: self.validate_dependency(root, base, authorization),
        )

    def test_file_outside_allowlist_is_rejected(self):
        root, base, authorization = self.dependency_repo()
        (root / "unexpected.txt").write_text("outside allowlist", encoding="utf-8")
        self.assert_reason(
            "SECURITY_REMEDIATION_FILE_SCOPE_INVALID",
            lambda: self.validate_dependency(root, base, authorization),
        )

    def test_generated_lockfile_drift_is_rejected(self):
        root, base, authorization = self.dependency_repo()
        path = root / "apps/web/pnpm-lock.yaml"
        path.write_bytes(path.read_bytes() + b"\n")
        self.assert_reason(
            "LOCKFILE_GENERATED_GRAPH_INVALID",
            lambda: self.validate_dependency(root, base, authorization),
        )

    def test_unauthorized_package_manifest_drift_is_rejected(self):
        root, base, authorization = self.dependency_repo()
        path = root / "apps/web/package.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["dependencies"]["unauthorized"] = "1.0.0"
        path.write_text(json.dumps(value), encoding="utf-8")
        self.assert_reason(
            "UNAUTHORIZED_DEPENDENCY_MANIFEST_CHANGE",
            lambda: self.validate_dependency(root, base, authorization),
        )

    def test_next_and_eslint_config_alignment_is_required(self):
        root, base, authorization = self.dependency_repo()
        path = root / "apps/web/package.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["devDependencies"]["eslint-config-next"] = "16.3.2"
        path.write_text(json.dumps(value), encoding="utf-8")
        self.assert_reason(
            "NEXT_DEPENDENCY_ALIGNMENT_INVALID",
            lambda: self.validate_dependency(root, base, authorization),
        )

    def test_exact_generated_dependency_graph_is_accepted(self):
        root, base, authorization = self.dependency_repo()
        result = self.validate_dependency(root, base, authorization)
        self.assertEqual(475, result["lock_package_count"])
        self.assertEqual("0.35.4", result["sharp_override_version"])

    def test_zero_target_audit_has_no_critical_and_is_pending_merge(self):
        authorization = self.authorization()
        issue5 = {
            "number": 5,
            "state": "CLOSED",
            "title": "Sharp historical remediation",
            "url": "https://github.com/example/issues/5",
            "tracked_advisory_ids": [rel000.ISSUE5_ADVISORY],
        }
        tracking = {
            "number": 48,
            "state": "OPEN",
            "title": authorization["tracked_issue_title"],
            "url": "https://github.com/example/issues/48",
            "tracked_advisory_ids": authorization["issue_advisories"]["48"],
        }
        result = rel000.validate_issue_and_audit(
            issue5,
            tracking,
            self.audit(self.base_advisories()),
            self.audit([]),
            {
                "dependency_diff_against_base": "AUTHORIZED_SECURITY_REMEDIATION",
                "vulnerable_lock_versions": [],
            },
            rel000.SECURITY_REMEDIATION,
            authorization,
        )
        self.assertEqual(0, result["branch_totals"]["critical"])
        self.assertEqual("REMEDIATED_PENDING_MERGE", result["dependency_security_status"])


class TestedProvenanceResolutionTests(unittest.TestCase):
    """Regression coverage for issue #52: the authoritative pull_request base is the
    first parent of the tested merge commit, never ``pull_request.base.sha``.

    Historical context (SEC-2026-09 / PR #49): the event base SHA stayed pinned at the
    pull request creation baseline while GitHub regenerated the merge ref on top of the
    advanced main, so REL-000 received contradictory provenance and failed closed.
    """

    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-tested-provenance-")
        self.root = Path(self.temp.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        self.git("init", "-q")
        self.git("config", "user.email", "rel000@example.invalid")
        self.git("config", "user.name", "REL-000 Tests")
        self.git("config", "commit.gpgsign", "false")
        # A: baseline when the pull request was created.
        self.commit_file("main.txt", "A")
        self.A = self.head()
        # H: pull request head branched from A.
        (self.repo / "feature.txt").write_text("H\n", encoding="utf-8")
        self.git("add", "feature.txt")
        self.git("commit", "-qm", "H")
        self.H = self.head()
        # B: main advanced after the pull request was created.
        self.git("checkout", "-q", self.A)
        self.commit_file("main.txt", "B")
        self.B = self.head()
        # M: the merge ref GitHub actually tests = merge(B, H), first parent B.
        tree = self.git("rev-parse", f"{self.B}^{{tree}}")
        self.M = self.git("commit-tree", tree, "-p", self.B, "-p", self.H, "-m", "merge")
        self.git("checkout", "-q", self.M)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def git(self, *arguments: str, cwd: Path | None = None) -> str:
        return subprocess.check_output(
            ["git", *arguments], cwd=cwd or self.repo, text=True, encoding="utf-8"
        ).strip()

    def head(self) -> str:
        return self.git("rev-parse", "HEAD")

    def commit_file(self, name: str, content: str) -> None:
        (self.repo / name).write_text(content + "\n", encoding="utf-8")
        self.git("add", name)
        self.git("commit", "-qm", content)

    def assert_reason(self, expected: str, action) -> rel000.ValidationFailure:
        with self.assertRaises(rel000.ValidationFailure) as raised:
            action()
        self.assertEqual(expected, raised.exception.reason_code)
        return raised.exception

    def resolve(self, event_name="pull_request", tested=None, source=None, event_base=None, repo=None):
        return rel000.resolve_tested_provenance(
            repo or self.repo,
            event_name,
            tested or self.M,
            source or self.H,
            event_base,
        )

    # A. stale event base must not override the tested merge parent.
    def test_stale_pull_request_base_sha_does_not_override_tested_merge_parent(self):
        resolved = self.resolve(event_base=self.A)
        self.assertEqual(self.B, resolved["base_main_sha"])
        self.assertEqual(self.H, resolved["source_head_sha"])
        self.assertEqual(self.M, resolved["tested_git_sha"])
        self.assertEqual("source_head_is_ancestor_of_tested_commit", resolved["git_relationship"])
        self.assertEqual("tested_merge_first_parent", resolved["base_resolution"])
        self.assertEqual(self.A, resolved["event_pull_request_base_sha"])
        self.assertIs(False, resolved["event_pull_request_base_sha_authoritative"])
        self.assertIs(False, resolved["event_pull_request_base_sha_matches_tested_base"])
        self.assertNotEqual(self.A, resolved["base_main_sha"])

    # B. a fresh pull request where the event base already equals the tested parent.
    def test_matching_pull_request_base_sha_resolves_to_tested_merge_parent(self):
        resolved = self.resolve(event_base=self.B)
        self.assertEqual(self.B, resolved["base_main_sha"])
        self.assertIs(True, resolved["event_pull_request_base_sha_matches_tested_base"])

    def test_pull_request_resolution_ignores_absent_event_base(self):
        resolved = self.resolve()
        self.assertEqual(self.B, resolved["base_main_sha"])
        self.assertIsNone(resolved["event_pull_request_base_sha"])
        self.assertIsNone(resolved["event_pull_request_base_sha_matches_tested_base"])

    # C. push to main keeps source = tested = base = github.sha.
    def test_push_main_resolves_source_tested_and_base_to_same_commit(self):
        self.git("checkout", "-q", self.B)
        resolved = self.resolve(event_name="push", tested=self.B, source=self.B)
        self.assertEqual(self.B, resolved["source_head_sha"])
        self.assertEqual(self.B, resolved["tested_git_sha"])
        self.assertEqual(self.B, resolved["base_main_sha"])
        self.assertEqual("same_commit", resolved["git_relationship"])
        self.assertEqual("push_same_commit", resolved["base_resolution"])

    def test_push_never_invents_a_parent_as_base(self):
        self.git("checkout", "-q", self.B)
        resolved = self.resolve(event_name="push", tested=self.B, source=self.B)
        self.assertNotEqual(self.A, resolved["base_main_sha"])

    def test_push_with_foreign_source_head_fails_closed(self):
        self.git("checkout", "-q", self.B)
        self.assert_reason(
            "PUSH_SOURCE_TESTED_MISMATCH",
            lambda: self.resolve(event_name="push", tested=self.B, source=self.H),
        )

    # D. malformed tested commit: not a merge, or parents unavailable.
    def test_non_merge_tested_commit_fails_closed(self):
        self.git("checkout", "-q", self.H)
        failure = self.assert_reason(
            "TESTED_COMMIT_NOT_MERGE",
            lambda: self.resolve(tested=self.H, source=self.A),
        )
        self.assertEqual(1, failure.details["parent_count"])

    def test_merge_with_unavailable_parents_fails_closed(self):
        self.git("branch", "-q", "tested-merge", self.M)
        shallow = self.root / "shallow-1"
        self.git(
            "clone", "-q", "--depth", "1", "--branch", "tested-merge",
            self.repo.as_uri(), str(shallow), cwd=self.root,
        )
        self.assertEqual(self.M, self.git("rev-parse", "HEAD", cwd=shallow))
        self.assert_reason(
            "TESTED_MERGE_PARENT_UNAVAILABLE",
            lambda: self.resolve(repo=shallow),
        )

    def test_fetch_depth_two_is_sufficient_to_prove_tested_merge_parents(self):
        self.git("branch", "-q", "tested-merge", self.M)
        shallow = self.root / "shallow-2"
        self.git(
            "clone", "-q", "--depth", "2", "--branch", "tested-merge",
            self.repo.as_uri(), str(shallow), cwd=self.root,
        )
        resolved = self.resolve(repo=shallow, event_base=self.A)
        self.assertEqual(self.B, resolved["base_main_sha"])
        self.assertEqual(self.H, resolved["source_head_sha"])

    def test_tested_commit_absent_from_checkout_fails_closed(self):
        self.assert_reason(
            "TESTED_SHA_CHECKOUT_MISMATCH",
            lambda: self.resolve(tested="f" * 40),
        )

    def test_tested_commit_differs_from_checkout_fails_closed(self):
        self.git("checkout", "-q", self.B)
        self.assert_reason(
            "TESTED_SHA_CHECKOUT_MISMATCH",
            lambda: self.resolve(),
        )

    # E. declared source must be the second parent of the tested merge.
    def test_source_head_not_second_parent_of_tested_merge_fails_closed(self):
        failure = self.assert_reason(
            "TESTED_MERGE_SOURCE_MISMATCH",
            lambda: self.resolve(source=self.A),
        )
        self.assertEqual(self.H, failure.details["tested_merge_second_parent"])

    def test_source_head_equal_to_tested_base_fails_closed(self):
        self.assert_reason(
            "TESTED_MERGE_SOURCE_MISMATCH",
            lambda: self.resolve(source=self.B),
        )

    def test_pull_request_with_source_equal_to_tested_fails_closed(self):
        self.assert_reason(
            "PULL_REQUEST_SOURCE_IS_TESTED",
            lambda: self.resolve(tested=self.M, source=self.M),
        )

    def test_unsupported_event_fails_closed(self):
        self.assert_reason(
            "EVENT_NAME_UNSUPPORTED",
            lambda: self.resolve(event_name="workflow_dispatch"),
        )

    def test_malformed_inputs_fail_closed(self):
        self.assert_reason("TESTED_GIT_SHA_MALFORMED", lambda: self.resolve(tested="HEAD"))
        self.assert_reason("SOURCE_HEAD_SHA_MALFORMED", lambda: self.resolve(source="refs/pull/1/head"))
        self.assert_reason(
            "EVENT_PULL_REQUEST_BASE_SHA_MALFORMED",
            lambda: self.resolve(event_base="main"),
        )

    # REL-000 itself enforces the same topology on its declared trace.
    def test_rel000_traceability_rejects_stale_event_base_as_base_main_sha(self):
        failure = self.assert_reason(
            "TESTED_MERGE_BASE_MISMATCH",
            lambda: rel000.validate_traceability(
                self.repo, self.H, self.M, self.A, "source_head_is_ancestor_of_tested_commit"
            ),
        )
        self.assertEqual(self.B, failure.details["tested_merge_first_parent"])
        self.assertEqual(self.A, failure.details["declared_base_main_sha"])

    def test_rel000_traceability_accepts_tested_merge_first_parent(self):
        trace = rel000.validate_traceability(
            self.repo, self.H, self.M, self.B, "source_head_is_ancestor_of_tested_commit"
        )
        self.assertEqual(
            {
                "source_head_sha": self.H,
                "tested_git_sha": self.M,
                "base_main_sha": self.B,
                "git_relationship": "source_head_is_ancestor_of_tested_commit",
            },
            trace,
        )

    def test_rel000_traceability_preserves_push_semantics(self):
        self.git("checkout", "-q", self.B)
        trace = rel000.validate_traceability(self.repo, self.B, self.B, self.B, "same_commit")
        self.assertEqual(self.B, trace["base_main_sha"])
        self.assertEqual("same_commit", trace["git_relationship"])

    def test_resolver_command_exports_only_resolved_values_to_github_env(self):
        github_env = self.root / "github.env"
        github_env.write_text("EXISTING=1\n", encoding="utf-8")
        output = self.root / "resolved.json"
        args = argparse.Namespace(
            repository_root=self.repo,
            event_name="pull_request",
            tested_git_sha=self.M,
            source_head_sha=self.H,
            event_pull_request_base_sha=self.A,
            output=output,
            github_env=github_env,
        )
        with contextlib.redirect_stdout(io.StringIO()) as stdout:
            resolved = rel000.resolve_tested_provenance_command(args)
        self.assertEqual(self.B, resolved["base_main_sha"])
        self.assertEqual(
            [
                "EXISTING=1",
                f"REL000_SOURCE_HEAD_SHA={self.H}",
                f"REL000_TESTED_GIT_SHA={self.M}",
                f"REL000_BASE_MAIN_SHA={self.B}",
                "REL000_GIT_RELATIONSHIP=source_head_is_ancestor_of_tested_commit",
            ],
            github_env.read_text(encoding="utf-8").splitlines(),
        )
        self.assertNotIn(self.A, github_env.read_text(encoding="utf-8"))
        self.assertEqual(resolved, json.loads(output.read_text(encoding="utf-8")))
        self.assertEqual(resolved, json.loads(stdout.getvalue()))

    # H. security remediation validates against the tested base, never the event base.
    def remediation_policy(self, authorized_base_sha: str) -> Path:
        policy = json.loads(
            (REPOSITORY_ROOT / "tools/rel-000/security-remediation-policy.json").read_text(
                encoding="utf-8"
            )
        )
        for authorization in policy["active_remediations"]:
            if authorization["id"] == rel000.NEXT_CRITICAL_REMEDIATION_ID:
                authorization["authorized_base_sha"] = authorized_base_sha
        path = self.root / "policy.json"
        path.write_text(json.dumps(policy), encoding="utf-8")
        return path

    def test_security_remediation_authorizes_tested_base_despite_stale_event_base(self):
        resolved = self.resolve(event_base=self.A)
        policy = rel000.load_remediation_policy(self.remediation_policy(self.B))
        authorization = rel000.validate_mode_authorization(
            rel000.SECURITY_REMEDIATION,
            policy,
            "fix/security-2026-09-next-critical",
            resolved["base_main_sha"],
            rel000.NEXT_CRITICAL_REMEDIATION_ID,
        )
        self.assertEqual(self.B, authorization["authorized_base_sha"])

    def test_security_remediation_rejects_policy_pinned_to_stale_event_base(self):
        resolved = self.resolve(event_base=self.A)
        policy = rel000.load_remediation_policy(self.remediation_policy(self.A))
        failure = self.assert_reason(
            "SECURITY_REMEDIATION_BASE_MISMATCH",
            lambda: rel000.validate_mode_authorization(
                rel000.SECURITY_REMEDIATION,
                policy,
                "fix/security-2026-09-next-critical",
                resolved["base_main_sha"],
                rel000.NEXT_CRITICAL_REMEDIATION_ID,
            ),
        )
        self.assertEqual(self.A, failure.details["expected"])
        self.assertEqual(self.B, failure.details["actual"])


class ProducerBaseProvenanceTests(unittest.TestCase):
    """Producers and REL-000 must share one authoritative tested base (issue #52)."""

    STALE_BASE = "a" * 40
    TESTED_BASE = "b" * 40

    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-producer-base-")
        self.root = Path(self.temp.name)
        self.fixture = json.loads(
            (REPOSITORY_ROOT / "tests/fixtures/rel-000/partial-rerun-provenance.json").read_text(
                encoding="utf-8"
            )
        )
        self.run_id = self.fixture["workflow_run_id"]
        self.source = self.fixture["head_sha"]
        self.tested = "c" * 40
        self.trace = {
            "source_head_sha": self.source,
            "tested_git_sha": self.tested,
            "base_main_sha": self.TESTED_BASE,
        }
        self.executions = self.root / "executions"
        self.executions.mkdir()

    def tearDown(self) -> None:
        self.temp.cleanup()

    def assert_reason(self, expected: str, action) -> rel000.ValidationFailure:
        with self.assertRaises(rel000.ValidationFailure) as raised:
            action()
        self.assertEqual(expected, raised.exception.reason_code)
        return raised.exception

    def workflow_provenance(self, *, partial: bool = False) -> Path:
        payload = copy.deepcopy(self.fixture)
        if not partial:
            payload["current_attempt"] = 1
            payload["jobs"] = [job for job in payload["jobs"] if job["run_attempt"] == 1]
            for job in payload["jobs"]:
                job["status"] = "completed"
                job["conclusion"] = "success"
            jobs = {job["job_key"]: job for job in payload["jobs"]}
            for artifact in payload["artifacts"]:
                artifact["producer_attempt"] = 1
                artifact["producer_job_id"] = jobs[artifact["job_key"]]["job_id"]
        path = self.root / "workflow-provenance.json"
        path.write_text(json.dumps(payload), encoding="utf-8")
        return path

    def producer_attempts(self, *, partial: bool) -> dict[str, int]:
        if not partial:
            return {job: 1 for job in rel000.EXECUTION_EVIDENCE_JOBS}
        return {
            artifact["job_key"]: artifact["producer_attempt"]
            for artifact in self.fixture["artifacts"]
        }

    def write_manifest(self, job: str, base_main_sha: str, attempt: int) -> None:
        directory = self.executions / rel000.EXECUTION_ARTIFACT_NAMES[job]
        directory.mkdir(parents=True)
        result_file = directory / "results.json"
        result_file.write_text(json.dumps({"job": job}), encoding="utf-8")
        digest = rel000.sha256_file(result_file)
        files = [{"path": "results.json", "bytes": result_file.stat().st_size, "sha256": digest}]
        manifest = {
            "format_version": rel000.EXECUTION_EVIDENCE_VERSION,
            "artifact_name": rel000.EXECUTION_ARTIFACT_NAMES[job],
            "job": job,
            "workflow_run_id": self.run_id,
            "workflow_run_attempt": str(attempt),
            "source_head_sha": self.source,
            "tested_git_sha": self.tested,
            "base_main_sha": base_main_sha,
            "result_files": files,
            "content_digest": rel000.canonical_content_digest(files),
            "tests": [
                {
                    "job": job,
                    "test_project": "tests/example.csproj",
                    "fully_qualified_test_name": f"{job}.required",
                    "category": "Example",
                    "outcome": "PASSED",
                    "executed": True,
                    "skipped": False,
                    "duration": "00:00:00.100",
                    "trx_or_result_digest": digest,
                }
            ],
        }
        rel000.write_json(directory / "rel000-execution-results.json", manifest)

    def load(self, bases: dict[str, str], *, partial: bool = False, attempt: int | None = None):
        attempts = self.producer_attempts(partial=partial)
        for job, base in bases.items():
            self.write_manifest(job, base, attempts[job])
        return rel000.load_execution_evidence(
            self.executions,
            self.workflow_provenance(partial=partial),
            self.trace,
            self.run_id,
            attempt if attempt is not None else (2 if partial else 1),
        )

    def test_all_producers_sharing_tested_base_pass(self):
        tests, artifacts, report = self.load(
            {job: self.TESTED_BASE for job in rel000.EXECUTION_EVIDENCE_JOBS}
        )
        self.assertEqual(set(rel000.EXECUTION_EVIDENCE_JOBS), set(artifacts))
        self.assertEqual(
            {self.TESTED_BASE}, {artifact["base_main_sha"] for artifact in artifacts.values()}
        )
        self.assertEqual({self.TESTED_BASE}, {result["base_main_sha"] for result in tests})
        self.assertFalse(report["mixed_attempt_evidence"])

    # F. one producer carrying the stale event base is rejected.
    def test_producer_artifact_with_stale_base_is_rejected(self):
        bases = {job: self.TESTED_BASE for job in rel000.EXECUTION_EVIDENCE_JOBS}
        bases["dotnet"] = self.STALE_BASE
        failure = self.assert_reason("EXECUTION_RESULT_SHA_MISMATCH", lambda: self.load(bases))
        self.assertEqual("base_main_sha", failure.details["field"])

    # G. mixed producer bases are rejected even when every SHA is individually valid.
    def test_mixed_producer_bases_are_rejected(self):
        bases = {}
        for index, job in enumerate(sorted(rel000.EXECUTION_EVIDENCE_JOBS)):
            bases[job] = self.STALE_BASE if index % 2 else self.TESTED_BASE
        self.assertEqual(2, len(set(bases.values())))
        self.assert_reason("EXECUTION_RESULT_SHA_MISMATCH", lambda: self.load(bases))

    def test_all_producers_agreeing_on_stale_base_are_rejected_by_rel000(self):
        self.assert_reason(
            "EXECUTION_RESULT_SHA_MISMATCH",
            lambda: self.load({job: self.STALE_BASE for job in rel000.EXECUTION_EVIDENCE_JOBS}),
        )

    # I. partial rerun: same run ID never legitimizes a stale-base artifact.
    def test_partial_rerun_keeps_latest_producer_semantics_with_tested_base(self):
        tests, artifacts, report = self.load(
            {job: self.TESTED_BASE for job in rel000.EXECUTION_EVIDENCE_JOBS}, partial=True
        )
        self.assertTrue(report["mixed_attempt_evidence"])
        self.assertEqual(2, artifacts["realtime-e2e"]["producer_attempt"])
        self.assertEqual(
            {self.TESTED_BASE}, {artifact["base_main_sha"] for artifact in artifacts.values()}
        )

    def test_partial_rerun_same_run_id_does_not_accept_stale_base_artifact(self):
        bases = {job: self.TESTED_BASE for job in rel000.EXECUTION_EVIDENCE_JOBS}
        bases["realtime-e2e"] = self.STALE_BASE
        self.assert_reason("EXECUTION_RESULT_SHA_MISMATCH", lambda: self.load(bases, partial=True))

    # J. full rerun: attempt-one artifacts stay rejected regardless of base.
    def test_full_rerun_rejects_attempt_one_artifact_even_with_tested_base(self):
        bases = {job: self.TESTED_BASE for job in rel000.EXECUTION_EVIDENCE_JOBS}
        self.assert_reason(
            "WORKFLOW_PROVENANCE_CURRENT_ATTEMPT_MISMATCH",
            lambda: self.load(bases, attempt=2),
        )

    def test_ops_artifact_provenance_with_stale_base_is_rejected(self):
        directory = self.root / "ops001"
        directory.mkdir()
        (directory / "report.json").write_text("{}", encoding="utf-8")
        rel000.create_provenance(
            directory,
            "delivery-simulation-results",
            self.run_id,
            "1",
            self.source,
            self.tested,
            self.STALE_BASE,
            "source_head_is_ancestor_of_tested_commit",
        )
        failure = self.assert_reason(
            "ARTIFACT_SHA_MISMATCH",
            lambda: rel000.validate_artifact(
                directory,
                "delivery-simulation-results",
                "1",
                "d" * 64,
                {**self.trace, "git_relationship": "source_head_is_ancestor_of_tested_commit"},
                self.run_id,
                "1",
                lambda path: path.endswith(".json"),
            ),
        )
        self.assertEqual("base_main_sha", failure.details["field"])


class TestedBaseWorkflowGuardTests(unittest.TestCase):
    """Physical guards: Foundation never lets the event base SHA become base_main_sha."""

    PROVENANCE_JOBS = sorted(rel000.EXECUTION_EVIDENCE_JOBS | {"rel000"})
    RESOLVE_STEP = "Resolve tested provenance"

    @classmethod
    def setUpClass(cls) -> None:
        cls.path = REPOSITORY_ROOT / ".github/workflows/ci.yml"
        cls.text = cls.path.read_text(encoding="utf-8")
        import yaml

        cls.workflow = yaml.safe_load(cls.text)

    def evidence_steps(self, job_key: str) -> list[dict[str, Any]]:
        return [
            step
            for step in self.workflow["jobs"][job_key]["steps"]
            if "rel000.py collect-results" in str(step.get("run", ""))
            or "rel000.py provenance" in str(step.get("run", ""))
        ]

    def test_event_pull_request_base_sha_is_never_an_authoritative_base(self):
        for line_number, line in enumerate(self.text.splitlines(), start=1):
            if "pull_request.base.sha" not in line:
                continue
            self.assertIn("--event-pull-request-base-sha", line, f"ci.yml:{line_number}")
            self.assertNotIn("base-main-sha", line, f"ci.yml:{line_number}")
            self.assertNotIn("BASE_MAIN_SHA", line, f"ci.yml:{line_number}")
            self.assertNotIn("||", line, f"ci.yml:{line_number}")
        self.assertNotIn('--base-main-sha "${{', self.text)
        self.assertNotIn("REL000_BASE_MAIN_SHA:", self.text)
        self.assertNotIn("REL000_GIT_RELATIONSHIP:", self.text)
        for job in self.workflow["jobs"].values():
            for name in (
                "REL000_BASE_MAIN_SHA",
                "REL000_SOURCE_HEAD_SHA",
                "REL000_TESTED_GIT_SHA",
                "REL000_GIT_RELATIONSHIP",
            ):
                self.assertNotIn(name, job.get("env", {}))

    def test_every_evidence_producer_and_rel000_resolve_tested_provenance_first(self):
        for job_key in self.PROVENANCE_JOBS:
            steps = self.workflow["jobs"][job_key]["steps"]
            checkout, resolve = steps[0], steps[1]
            self.assertEqual("actions/checkout@v5", checkout["uses"], job_key)
            self.assertIn(checkout.get("with", {}).get("fetch-depth"), {0, 2}, job_key)
            self.assertEqual(self.RESOLVE_STEP, resolve.get("name"), job_key)
            run = resolve["run"]
            self.assertIn("python ./tools/rel-000/rel000.py resolve-tested-provenance", run)
            self.assertIn('--event-name "${{ github.event_name }}"', run)
            self.assertIn('--tested-git-sha "${{ github.sha }}"', run)
            self.assertIn(
                '--source-head-sha "${{ github.event.pull_request.head.sha || github.sha }}"', run
            )
            self.assertIn(
                '--event-pull-request-base-sha "${{ github.event.pull_request.base.sha }}"', run
            )
            self.assertIn('--github-env "$GITHUB_ENV"', run)
            self.assertNotIn("base-main-sha", run)
            self.assertNotIn("if", resolve)
            self.assertEqual(
                1, sum(step.get("name") == self.RESOLVE_STEP for step in steps), job_key
            )

    def test_every_structured_evidence_uses_resolved_provenance(self):
        for job_key in sorted(rel000.EXECUTION_EVIDENCE_JOBS):
            evidence = self.evidence_steps(job_key)
            self.assertTrue(evidence, job_key)
            for step in evidence:
                run = step["run"]
                self.assertIn('--source-head-sha "$REL000_SOURCE_HEAD_SHA"', run, job_key)
                self.assertIn('--tested-git-sha "$REL000_TESTED_GIT_SHA"', run, job_key)
                self.assertIn('--base-main-sha "$REL000_BASE_MAIN_SHA"', run, job_key)
                if "rel000.py provenance" in run:
                    self.assertIn('--git-relationship "$REL000_GIT_RELATIONSHIP"', run, job_key)
                self.assertNotIn("github.sha", run, job_key)
                self.assertNotIn("pull_request", run, job_key)
        rel000_steps = self.workflow["jobs"]["rel000"]["steps"]
        provenance_capture = next(
            step
            for step in rel000_steps
            if step.get("name") == "Capture and sanitize workflow provenance"
        )
        self.assertIn('--head-sha "$REL000_SOURCE_HEAD_SHA"', provenance_capture["run"])
        audits = next(
            step
            for step in rel000_steps
            if step.get("name") == "Capture and sanitize real base and branch audits"
        )
        self.assertIn('git archive "$REL000_BASE_MAIN_SHA"', audits["run"])
        validation = next(
            step
            for step in rel000_steps
            if step.get("name") == "Validate and generate redacted REL-000 evidence"
        )
        self.assertNotIn("REL000_BASE_MAIN_SHA", validation.get("env", {}))

    def test_jobs_without_structured_evidence_do_not_resolve_provenance(self):
        for job_key in ("normative", "web"):
            names = [step.get("name") for step in self.workflow["jobs"][job_key]["steps"]]
            self.assertNotIn(self.RESOLVE_STEP, names, job_key)
        self.assertEqual(13, len(self.workflow["jobs"]))
        self.assertEqual(
            len(self.PROVENANCE_JOBS),
            self.text.count(f"- name: {self.RESOLVE_STEP}"),
        )


if __name__ == "__main__":
    unittest.main(verbosity=2)
