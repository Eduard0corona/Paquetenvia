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


EXPECTED_IDS = [
    "FND-001",
    "ARC-001",
    "ARC-002",
    "FND-002",
    "SEC-001",
    "TEN-001",
    "AUD-001",
    "GEO-001",
    "PRC-001",
    "PRC-002",
    "ORD-001",
    "ORD-002",
    "DSP-001",
    "DSP-002",
    "RTM-001",
    "DRV-003",
    "RTM-002",
    "POD-001",
    "DRV-001",
    "DRV-002",
    "TRK-001",
    "OBS-001",
    "OPS-001",
    "OPS-002",
    "REL-000",
    "FIN-001",
    "SEC-002",
    "TEN-002",
    "DBA-001",
    "TEN-003",
]


class Rel000FocusedTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel000-tests-")
        self.root = Path(self.temp.name)
        self.selected = [
            {
                "id": item_id,
                "title": f"Title {item_id}",
                "release": "MVP-0",
                "priority": "P0",
                "depends_on": [],
            }
            for item_id in EXPECTED_IDS
        ]
        self.by_id = {item["id"]: item for item in self.selected}
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
                    "open_gates": (
                        ["REL-000-DEF-001"] if item_id in {"REL-000", "FIN-001"} else []
                    ),
                }
                for item_id in EXPECTED_IDS
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

    # 1-8: exact P0 inventory and evidence.
    def test_01_missing_p0_item(self):
        evidence = copy.deepcopy(self.item_evidence)
        evidence["items"] = [item for item in evidence["items"] if item["id"] != "FND-001"]
        self.assert_reason("P0_ITEM_MISSING", lambda: self.validate_items(evidence))

    def test_02_duplicate_p0_item(self):
        evidence = copy.deepcopy(self.item_evidence)
        evidence["items"].append(copy.deepcopy(evidence["items"][0]))
        self.assert_reason("P0_ITEM_DUPLICATED", lambda: self.validate_items(evidence))

    def test_03_unknown_p0_item(self):
        evidence = copy.deepcopy(self.item_evidence)
        evidence["items"][0]["id"] = "UNKNOWN-001"
        self.assert_reason("P0_ITEM_UNKNOWN", lambda: self.validate_items(evidence))

    def test_04_fin001_omitted(self):
        evidence = copy.deepcopy(self.item_evidence)
        evidence["items"] = [item for item in evidence["items"] if item["id"] != "FIN-001"]
        self.assert_reason("FIN001_OMITTED", lambda: self.validate_items(evidence))

    def test_05_fin001_falsely_verified(self):
        evidence = copy.deepcopy(self.item_evidence)
        next(item for item in evidence["items"] if item["id"] == "FIN-001")[
            "implementation_status"
        ] = "VERIFIED"
        self.assert_reason("FIN001_FALSELY_VERIFIED", lambda: self.validate_items(evidence))

    def test_06_dependency_missing(self):
        normative = {"backlog": {"items": copy.deepcopy(self.selected)}}
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
    def test_30_owner_approval_falsely_marked_approved(self):
        report = {
            "owner_approval_status": "APPROVED",
            "release_candidate_status": "BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION",
            "dependency_security_status": "BLOCKED",
            "audit_tracking_gap_detected": True,
            "audit_tracking_gap_count": 10,
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
            "audit_tracking_gap_detected": True,
            "audit_tracking_gap_count": 10,
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

    def test_34_issue5_omitted(self):
        issue = {"number": 5, "state": "CLOSED"}
        _, additional, base, branch, clean = self.make_security_inputs()
        self.assert_reason(
            "ISSUE5_OMITTED_OR_CLOSED",
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
            "BRANCH_AUDIT_WORSENED",
            lambda: rel000.validate_issue_and_audit(issue5, additional, base, branch, clean),
        )

    def test_38_branch_high_increases(self):
        issue5, additional, base, branch, clean = self.make_security_inputs()
        moderate = next(item for item in branch["advisories"] if item["severity"] == "moderate")
        moderate["severity"] = "high"
        branch["totals"]["high"] += 1
        branch["totals"]["moderate"] -= 1
        self.assert_reason(
            "BRANCH_AUDIT_WORSENED",
            lambda: rel000.validate_issue_and_audit(issue5, additional, base, branch, clean),
        )

    def test_39_branch_moderate_increases(self):
        issue5, additional, base, branch, clean = self.make_security_inputs()
        high = next(item for item in branch["advisories"] if item["severity"] == "high")
        high["severity"] = "moderate"
        branch["totals"]["high"] -= 1
        branch["totals"]["moderate"] += 1
        self.assert_reason(
            "BRANCH_AUDIT_WORSENED",
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
            "BRANCH_ADVISORY_SET_CHANGED",
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

    def test_45_security_falsely_passed(self):
        report = {
            "owner_approval_status": "PENDING",
            "dependency_security_status": "PASSED",
            "release_candidate_status": "BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION",
            "audit_tracking_gap_detected": True,
            "audit_tracking_gap_count": 10,
            "technical_evidence_status": "PASSED",
            "result": "REL000_EVIDENCE_GENERATED",
        }
        self.assert_reason(
            "DEPENDENCY_SECURITY_FALSELY_PASSED",
            lambda: rel000.validate_owner_state(report),
        )

    def test_46_release_candidate_unblocked(self):
        report = {
            "owner_approval_status": "PENDING",
            "dependency_security_status": "BLOCKED",
            "release_candidate_status": "READY_FOR_RELEASE",
            "audit_tracking_gap_detected": True,
            "audit_tracking_gap_count": 10,
            "technical_evidence_status": "PASSED",
            "result": "REL000_EVIDENCE_GENERATED",
        }
        self.assert_reason(
            "RELEASE_CANDIDATE_NOT_BLOCKED",
            lambda: rel000.validate_owner_state(report),
        )

    def test_47_tracking_gap_hidden(self):
        report = {
            "owner_approval_status": "PENDING",
            "dependency_security_status": "BLOCKED",
            "release_candidate_status": "BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION",
            "audit_tracking_gap_detected": False,
            "audit_tracking_gap_count": 0,
            "technical_evidence_status": "PASSED",
            "result": "REL000_EVIDENCE_GENERATED",
        }
        self.assert_reason(
            "AUDIT_TRACKING_GAP_HIDDEN",
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


if __name__ == "__main__":
    unittest.main(verbosity=2)
