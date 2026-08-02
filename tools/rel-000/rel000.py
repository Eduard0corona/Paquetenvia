#!/usr/bin/env python3
"""REL-000 fail-closed evidence validator and report generator."""

from __future__ import annotations

import argparse
import copy
import datetime as dt
import glob
import hashlib
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Iterable


FORMAT_VERSION = "paquetenvia-rel000-v1"
SOURCE_PROVENANCE_VERSION = "paquetenvia-rel000-source-v1"
EXECUTION_EVIDENCE_VERSION = "paquetenvia-rel000-execution-v2"
NORMAL_RELEASE_EVIDENCE = "NORMAL_RELEASE_EVIDENCE"
SECURITY_REMEDIATION = "SECURITY_REMEDIATION"
REL000_MODES = {NORMAL_RELEASE_EVIDENCE, SECURITY_REMEDIATION}
SECURITY_REMEDIATION_BASE_SHA = "1ac8054026b3e4cb06612001f2be053d351fd2cf"
SECURITY_REMEDIATION_BRANCH = "fix/security-next-sharp-brace-expansion"
EXPECTED_MVP0_P0_COUNT = 29
FIN001_EXPECTED_DEPENDENCIES = {"DSP-002", "EXT-001", "RTE-001"}
ITEM_STATUSES = {"VERIFIED", "PARTIAL", "NOT_STARTED", "BLOCKED", "NOT_APPLICABLE"}
REQUIRED_JOBS = {
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
}
EXECUTION_EVIDENCE_JOBS = {
    "dotnet",
    "runtime-contracts",
    "realtime-e2e",
    "outbox-signalr-delivery",
    "driver-stops-pwa",
    "public-tracking",
    "operations-dashboard",
    "delivery-simulation",
    "infrastructure",
    "backup-restore",
}
REQUIRED_CROSS_TENANT_CATEGORIES = {
    "identity_resolution",
    "active_organization",
    "transaction_local_rls",
    "pooling_retry",
    "provisioning",
    "organizations_memberships",
    "quotes",
    "orders",
    "order_acceptances",
    "state_transitions",
    "driver_profiles_documents",
    "assignments",
    "driver_stops",
    "driver_location",
    "business_outbox",
    "location_outbox",
    "signalr_operations_hub",
    "signalr_driver_hub",
    "public_tracking",
    "operations_dashboard",
    "pod_upload_sessions",
    "proofs",
    "audit",
    "backup_restore",
    "ops001_decoy_tenant",
}
REQUIRED_OPEN_DECISIONS = {
    "GATE-001",
    "GATE-003",
    "GATE-004",
    "GATE-005",
    "GATE-006",
    "GATE-007",
    "GATE-008",
    "GATE-009",
    "GATE-010",
    "GATE-011",
    "GATE-012",
    "GATE-013",
    "GATE-014",
    "GATE-015",
    "GATE-016",
    "GATE-017",
    "RTM-001-CUSTOMER-SUPPORT-ROLE",
}
OUTPUT_FILES = {
    "rel000-internal-release-report.json",
    "rel000-p0-evidence.json",
    "rel000-cross-tenant-evidence.json",
    "rel000-rollback-evidence.json",
}
DEPENDENCY_FILES = (
    "apps/web/package.json",
    "apps/web/pnpm-lock.yaml",
    "apps/web/pnpm-workspace.yaml",
)
SECURITY_REMEDIATION_DEPENDENCY_FILES = {
    "apps/web/package.json",
    "apps/web/pnpm-lock.yaml",
    "apps/web/pnpm-workspace.yaml",
}
DEPENDENCY_FILE_NAMES = {
    "package.json",
    "package-lock.json",
    "pnpm-lock.yaml",
    "pnpm-workspace.yaml",
    "yarn.lock",
}
ISSUE5_ADVISORY = "GHSA-f88m-g3jw-g9cj"
ADDITIONAL_SECURITY_ISSUE_TITLE = (
    "Security: remediate inherited Next.js and brace-expansion advisories "
    "before release approval"
)
EXPECTED_BASE_AUDIT_TOTALS = {
    "critical": 0,
    "high": 6,
    "moderate": 5,
    "low": 0,
    "total": 11,
}
EXPECTED_BASE_ADVISORIES = {
    "GHSA-4633-3j49-mh5q",
    "GHSA-4c39-4ccg-62r3",
    "GHSA-68g3-v927-f742",
    "GHSA-6gpp-xcg3-4w24",
    "GHSA-89xv-2m56-2m9x",
    "GHSA-955p-x3mx-jcvp",
    "GHSA-f88m-g3jw-g9cj",
    "GHSA-m99w-x7hq-7vfj",
    "GHSA-mh99-v99m-4gvg",
    "GHSA-p9j2-gv94-2wf4",
    "GHSA-q8wf-6r8g-63ch",
}
ISSUE30_ADVISORIES = EXPECTED_BASE_ADVISORIES - {ISSUE5_ADVISORY}
SEVERITY_RANK = {"low": 0, "moderate": 1, "high": 2, "critical": 3}
SHA40 = re.compile(r"^[0-9a-f]{40}$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")
FORBIDDEN_RESULT_WORDS = {
    "APPROVED",
    "RELEASED",
    "PRODUCTION_READY",
    "PILOT_READY",
    "GO_LIVE",
    "DONE",
}
FORBIDDEN_REDACTED_KEYS = {
    "identity",
    "connection_string",
    "password",
    "access_key",
    "secret",
    "token",
    "signed_url",
    "object_key",
    "payload",
    "user_id",
    "organization_id",
    "order_id",
    "driver_id",
}
FORBIDDEN_TEXT_PATTERNS = (
    re.compile(r"AGE-SECRET-KEY-", re.IGNORECASE),
    re.compile(r"postgres(?:ql)?://", re.IGNORECASE),
    re.compile(r"AKIA[0-9A-Z]{16}"),
    re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
    re.compile(
        r"[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-"
        r"[89ab][0-9a-f]{3}-[0-9a-f]{12}",
        re.IGNORECASE,
    ),
)


@dataclass(frozen=True)
class ValidationFailure(Exception):
    reason_code: str
    message: str
    details: dict[str, Any] | None = None

    def __str__(self) -> str:
        return f"{self.reason_code}: {self.message}"

    def as_dict(self) -> dict[str, Any]:
        return {
            "format_version": FORMAT_VERSION,
            "result": "REL000_TECHNICAL_VALIDATION_FAILED",
            "technical_evidence_status": "FAILED",
            "reason_code": self.reason_code,
            "message": self.message,
            "details": self.details or {},
        }


def fail(reason_code: str, message: str, **details: Any) -> None:
    raise ValidationFailure(reason_code, message, details or None)


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        fail("JSON_INPUT_INVALID", f"Unable to load JSON input {path.name}.", error=str(exc))


def load_remediation_policy(path: Path) -> dict[str, Any]:
    policy = load_json(path)
    if not isinstance(policy, dict):
        fail("REMEDIATION_POLICY_INVALID", "The remediation policy must be a JSON object.")
    if policy.get("default_mode") != NORMAL_RELEASE_EVIDENCE:
        fail("REMEDIATION_POLICY_INVALID", "The remediation policy must fail closed to normal mode.")
    remediation = policy.get("security_remediation")
    if not isinstance(remediation, dict):
        fail("REMEDIATION_POLICY_INVALID", "The security remediation authorization is missing.")
    if (
        remediation.get("mode") != SECURITY_REMEDIATION
        or remediation.get("authorized_base_sha") != SECURITY_REMEDIATION_BASE_SHA
        or remediation.get("authorized_source_branch") != SECURITY_REMEDIATION_BRANCH
        or set(remediation.get("allowed_dependency_files") or [])
        != SECURITY_REMEDIATION_DEPENDENCY_FILES
    ):
        fail("REMEDIATION_POLICY_INVALID", "The security remediation authorization drifted.")
    return policy


def resolve_rel000_mode(policy: dict[str, Any], source_branch: str) -> str:
    remediation = policy["security_remediation"]
    if source_branch == remediation["authorized_source_branch"]:
        return SECURITY_REMEDIATION
    return NORMAL_RELEASE_EVIDENCE


def validate_mode_authorization(
    mode: str,
    policy: dict[str, Any],
    source_branch: str,
    base_main_sha: str,
) -> None:
    if mode not in REL000_MODES:
        fail("REL000_MODE_INVALID", "The REL-000 execution mode is invalid.", mode=mode)
    resolved = resolve_rel000_mode(policy, source_branch)
    if mode != resolved:
        fail(
            "REL000_MODE_NOT_AUTHORIZED",
            "The requested REL-000 mode is not authorized for this source branch.",
            requested=mode,
            authorized=resolved,
        )
    if mode == SECURITY_REMEDIATION and base_main_sha != SECURITY_REMEDIATION_BASE_SHA:
        fail(
            "SECURITY_REMEDIATION_BASE_MISMATCH",
            "Security remediation requires the exact authorized base SHA.",
            expected=SECURITY_REMEDIATION_BASE_SHA,
            actual=base_main_sha,
        )


def write_json(path: Path, value: Any) -> None:
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2, sort_keys=False) + "\n",
        encoding="utf-8",
    )


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def normalized_sha256(value: str, reason_code: str, field: str) -> str:
    normalized = str(value or "").strip().lower()
    if normalized.startswith("sha256:"):
        normalized = normalized[7:]
    if not SHA256.fullmatch(normalized):
        fail(reason_code, f"{field} must be a SHA-256 digest.", field=field)
    return normalized


def _xml_local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_trx_results(
    path: Path,
    job: str,
    project: str,
    category_override: str | None = None,
) -> list[dict[str, Any]]:
    if not path.is_file() or path.stat().st_size == 0:
        fail("EXECUTION_TRX_EMPTY", "A required TRX is absent or empty.", path=str(path))
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as exc:
        fail("EXECUTION_TRX_MALFORMED", "A required TRX is malformed.", error=str(exc))

    definitions: dict[str, tuple[str, list[str]]] = {}
    for unit_test in root.iter():
        if _xml_local_name(unit_test.tag) != "UnitTest":
            continue
        test_id = unit_test.attrib.get("id", "")
        method = next(
            (
                node
                for node in unit_test.iter()
                if _xml_local_name(node.tag) == "TestMethod"
            ),
            None,
        )
        if method is None:
            continue
        class_name = method.attrib.get("className", "").split(",", 1)[0]
        method_name = method.attrib.get("name", "")
        fully_qualified = ".".join(part for part in (class_name, method_name) if part)
        categories = sorted(
            {
                node.attrib.get("TestCategory", "")
                for node in unit_test.iter()
                if _xml_local_name(node.tag) == "TestCategoryItem"
                and node.attrib.get("TestCategory")
            }
        )
        definitions[test_id] = (fully_qualified, categories)

    digest = sha256_file(path)
    results: list[dict[str, Any]] = []
    for node in root.iter():
        if _xml_local_name(node.tag) != "UnitTestResult":
            continue
        outcome_raw = node.attrib.get("outcome", "")
        outcome = {
            "Passed": "PASSED",
            "Failed": "FAILED",
            "NotExecuted": "NOT_EXECUTED",
        }.get(outcome_raw, outcome_raw.upper() or "UNKNOWN")
        test_id = node.attrib.get("testId", "")
        defined_name, categories = definitions.get(test_id, ("", []))
        fully_qualified = defined_name or node.attrib.get("testName", "")
        if not fully_qualified:
            fail("EXECUTION_TEST_IDENTITY_MISSING", "A TRX result has no stable test identity.")
        resolved_project = project
        if project == "auto":
            project_by_namespace = {
                "Paqueteria.ArchitectureTests.": "tests/Paqueteria.ArchitectureTests/Paqueteria.ArchitectureTests.csproj",
                "Paqueteria.ContractTests.": "tests/Paqueteria.ContractTests/Paqueteria.ContractTests.csproj",
                "Paqueteria.EndToEndTests.": "tests/Paqueteria.EndToEndTests/Paqueteria.EndToEndTests.csproj",
                "Paqueteria.IntegrationTests.": "tests/Paqueteria.IntegrationTests/Paqueteria.IntegrationTests.csproj",
                "Paqueteria.UnitTests.": "tests/Paqueteria.UnitTests/Paqueteria.UnitTests.csproj",
            }
            resolved_project = next(
                (
                    mapped
                    for prefix, mapped in project_by_namespace.items()
                    if fully_qualified.startswith(prefix)
                ),
                "",
            )
            if not resolved_project:
                fail(
                    "EXECUTION_TEST_PROJECT_UNKNOWN",
                    "A TRX test could not be mapped to its project.",
                    test=fully_qualified,
                )
        duration = node.attrib.get("duration")
        for category in ([category_override] if category_override else categories or ["Uncategorized"]):
            results.append(
                {
                    "job": job,
                    "test_project": resolved_project,
                    "fully_qualified_test_name": fully_qualified,
                    "category": category,
                    "outcome": outcome,
                    "executed": outcome not in {"NOT_EXECUTED", "SKIPPED"},
                    "skipped": outcome in {"NOT_EXECUTED", "SKIPPED"},
                    "duration": duration,
                    "trx_or_result_digest": digest,
                }
            )
    if not results:
        fail("EXECUTION_TRX_EMPTY", "A required TRX contains no test results.", path=str(path))
    return results


def parse_junit_results(
    path: Path,
    job: str,
    project: str,
    category_override: str | None = None,
) -> list[dict[str, Any]]:
    if not path.is_file() or path.stat().st_size == 0:
        fail("EXECUTION_RESULT_EMPTY", "A required JUnit result is absent or empty.")
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as exc:
        fail("EXECUTION_RESULT_MALFORMED", "A required JUnit result is malformed.", error=str(exc))
    digest = sha256_file(path)
    results: list[dict[str, Any]] = []
    for node in root.iter():
        if _xml_local_name(node.tag) != "testcase":
            continue
        class_name = node.attrib.get("classname", "")
        name = node.attrib.get("name", "")
        identity = "::".join(part for part in (class_name, name) if part)
        if not identity:
            fail("EXECUTION_TEST_IDENTITY_MISSING", "A JUnit result has no stable test identity.")
        children = {_xml_local_name(child.tag) for child in node}
        skipped = "skipped" in children
        failed = bool(children & {"failure", "error"})
        outcome = "FAILED" if failed else "SKIPPED" if skipped else "PASSED"
        results.append(
            {
                "job": job,
                "test_project": project,
                "fully_qualified_test_name": identity,
                "category": category_override or "RealtimeE2E",
                "outcome": outcome,
                "executed": not skipped,
                "skipped": skipped,
                "duration": node.attrib.get("time"),
                "trx_or_result_digest": digest,
            }
        )
    if not results:
        fail("EXECUTION_RESULT_EMPTY", "A required JUnit file contains no test cases.")
    return results


def parse_structured_results(
    path: Path,
    job: str,
    project: str,
    category_override: str | None = None,
) -> list[dict[str, Any]]:
    payload = load_json(path)
    raw_tests = payload.get("tests") if isinstance(payload, dict) else None
    if not isinstance(raw_tests, list) or not raw_tests:
        fail("EXECUTION_RESULT_EMPTY", "A structured execution result contains no tests.")
    digest = sha256_file(path)
    results: list[dict[str, Any]] = []
    for raw in raw_tests:
        if not isinstance(raw, dict):
            fail("EXECUTION_RESULT_MALFORMED", "A structured test result is malformed.")
        outcome = str(raw.get("outcome") or "").upper()
        identity = str(raw.get("fully_qualified_test_name") or "")
        category = str(raw.get("category") or "")
        if not identity or not category or outcome not in {
            "PASSED",
            "FAILED",
            "SKIPPED",
            "NOT_EXECUTED",
        }:
            fail("EXECUTION_RESULT_MALFORMED", "A structured test result is incomplete.")
        skipped = outcome in {"SKIPPED", "NOT_EXECUTED"}
        results.append(
            {
                "job": job,
                "test_project": project,
                "fully_qualified_test_name": identity,
                "category": category,
                "outcome": outcome,
                "executed": not skipped,
                "skipped": skipped,
                "duration": raw.get("duration"),
                "trx_or_result_digest": digest,
            }
        )
    return results


def run_git(repository_root: Path, *arguments: str, allow_failure: bool = False) -> str:
    result = subprocess.run(
        ["git", "-C", str(repository_root), *arguments],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if result.returncode and not allow_failure:
        fail(
            "GIT_COMMAND_FAILED",
            "A required Git traceability command failed.",
            arguments=list(arguments),
            stderr=result.stderr.strip(),
        )
    return result.stdout.strip()


def git_is_ancestor(repository_root: Path, ancestor: str, descendant: str) -> bool:
    result = subprocess.run(
        ["git", "-C", str(repository_root), "merge-base", "--is-ancestor", ancestor, descendant],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    return result.returncode == 0


def validate_sha(value: str, reason_code: str, field: str) -> str:
    normalized = str(value or "").strip().lower()
    if not SHA40.fullmatch(normalized):
        fail(reason_code, f"{field} must be one full 40-character Git SHA.", field=field)
    return normalized


def validate_traceability(
    repository_root: Path,
    source_head_sha: str,
    tested_git_sha: str,
    base_main_sha: str,
    git_relationship: str,
) -> dict[str, str]:
    source = validate_sha(source_head_sha, "SOURCE_HEAD_SHA_MALFORMED", "source_head_sha")
    tested = validate_sha(tested_git_sha, "TESTED_GIT_SHA_MALFORMED", "tested_git_sha")
    base = validate_sha(base_main_sha, "BASE_MAIN_SHA_MALFORMED", "base_main_sha")
    checkout = run_git(repository_root, "rev-parse", "HEAD").lower()
    if checkout != tested:
        fail(
            "TESTED_SHA_CHECKOUT_MISMATCH",
            "tested_git_sha does not match the checked-out commit.",
            expected=tested,
            actual=checkout,
        )
    for field, value in (
        ("source_head_sha", source),
        ("tested_git_sha", tested),
        ("base_main_sha", base),
    ):
        exists = subprocess.run(
            ["git", "-C", str(repository_root), "cat-file", "-e", f"{value}^{{commit}}"],
            check=False,
            capture_output=True,
        )
        if exists.returncode:
            fail("GIT_SHA_NOT_IN_CHECKOUT", f"{field} is not available in the checkout.", field=field)
    if not git_is_ancestor(repository_root, base, tested):
        fail("BASE_MAIN_NOT_ANCESTOR", "base_main_sha is not an ancestor of tested_git_sha.")
    expected_relationship = (
        "same_commit" if source == tested else "source_head_is_ancestor_of_tested_commit"
    )
    if git_relationship != expected_relationship:
        fail(
            "GIT_RELATIONSHIP_INVALID",
            "The declared Git relationship does not match the SHAs.",
            expected=expected_relationship,
            actual=git_relationship,
        )
    if source != tested and not git_is_ancestor(repository_root, source, tested):
        fail(
            "SOURCE_HEAD_NOT_ANCESTOR",
            "source_head_sha is not an ancestor of tested_git_sha.",
        )
    return {
        "source_head_sha": source,
        "tested_git_sha": tested,
        "base_main_sha": base,
        "git_relationship": expected_relationship,
    }


def load_normative(repository_root: Path) -> dict[str, Any]:
    try:
        import yaml
    except ImportError as exc:
        fail("YAML_PARSER_UNAVAILABLE", "PyYAML is required for normative validation.", error=str(exc))
    normative_root = repository_root / "docs/normative/v0.6"
    backlog_path = normative_root / "specs/AI-08_BACKLOG.yaml"
    gates_path = normative_root / "specs/AI-10_DECISIONS_AND_GATES.yaml"
    try:
        backlog = yaml.safe_load(backlog_path.read_text(encoding="utf-8"))
        gates = yaml.safe_load(gates_path.read_text(encoding="utf-8"))
    except (OSError, yaml.YAMLError) as exc:
        fail("NORMATIVE_YAML_INVALID", "A normative YAML source could not be parsed.", error=str(exc))
    return {
        "root": normative_root,
        "backlog": backlog,
        "gates": gates,
    }


def validate_normative_checksums(normative_root: Path) -> dict[str, Any]:
    checksum_path = normative_root / "CHECKSUMS_SHA256.txt"
    try:
        lines = [
            line
            for line in checksum_path.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
    except OSError as exc:
        fail("NORMATIVE_CHECKSUM_SOURCE_MISSING", "The normative checksum file is unavailable.", error=str(exc))
    modified: list[str] = []
    for line in lines:
        try:
            expected, relative = line.split("  ", 1)
        except ValueError:
            fail("NORMATIVE_CHECKSUM_SOURCE_INVALID", "A checksum entry is malformed.")
        target = normative_root / relative
        if not target.is_file() or sha256_file(target) != expected:
            modified.append(relative)
    if modified:
        fail(
            "NORMATIVE_MODIFIED",
            "The canonical v0.6 checksum baseline has changed.",
            files=modified,
        )
    return {
        "normative_version": "0.6",
        "normative_checksum_valid": True,
        "normative_files_modified": [],
        "files_checked": len(lines),
    }


def normative_items(normative: dict[str, Any]) -> tuple[list[dict[str, Any]], dict[str, dict[str, Any]]]:
    all_items = normative["backlog"].get("items")
    if not isinstance(all_items, list):
        fail("BACKLOG_ITEMS_INVALID", "AI-08 does not contain an items list.")
    by_id: dict[str, dict[str, Any]] = {}
    for item in all_items:
        item_id = item.get("id")
        if not item_id or item_id in by_id:
            fail("BACKLOG_ID_INVALID", "AI-08 contains a missing or duplicate item ID.")
        by_id[item_id] = item
    for item in all_items:
        for dependency in item.get("depends_on") or []:
            if dependency not in by_id:
                fail(
                    "DEPENDENCY_NOT_FOUND",
                    "AI-08 contains a dependency that does not exist.",
                    item=item["id"],
                    dependency=dependency,
                )
    fin001 = by_id.get("FIN-001")
    if not fin001 or fin001.get("release") != "MVP-1":
        fail(
            "FIN001_RELEASE_CLASSIFICATION_INVALID",
            "FIN-001 must exist globally and belong completely to MVP-1.",
            actual=None if not fin001 else fin001.get("release"),
        )
    if fin001.get("priority") != "P0":
        fail(
            "FIN001_PRIORITY_INVALID",
            "FIN-001 must retain priority P0.",
            actual=fin001.get("priority"),
        )
    fin001_dependencies = fin001.get("depends_on") or []
    if (
        len(fin001_dependencies) != len(FIN001_EXPECTED_DEPENDENCIES)
        or set(fin001_dependencies) != FIN001_EXPECTED_DEPENDENCIES
    ):
        fail(
            "FIN001_DEPENDENCY_SET_INVALID",
            "FIN-001 must retain exactly DSP-002, EXT-001 and RTE-001.",
            expected=sorted(FIN001_EXPECTED_DEPENDENCIES),
            actual=sorted(fin001_dependencies),
        )
    selected = [
        item
        for item in all_items
        if item.get("release") == "MVP-0" and item.get("priority") == "P0"
    ]
    if len(selected) != EXPECTED_MVP0_P0_COUNT:
        fail(
            "P0_COUNT_UNEXPECTED",
            "The canonical MVP-0/P0 count changed unexpectedly.",
            expected=EXPECTED_MVP0_P0_COUNT,
            actual=len(selected),
        )
    selected_ids = {item["id"] for item in selected}
    if "REL-000" not in selected_ids:
        fail("REQUIRED_P0_ITEM_MISSING", "REL-000 is absent from MVP-0/P0.")
    return selected, by_id


def require_path(repository_root: Path, relative: str, reason_code: str) -> Path:
    if not relative or Path(relative).is_absolute() or ".." in Path(relative).parts:
        fail(reason_code, "Evidence paths must be repository-relative.", path=relative)
    path = repository_root / relative
    if not path.exists():
        fail(reason_code, "An evidence path does not exist.", path=relative)
    return path


def validate_item_evidence(
    repository_root: Path,
    selected: list[dict[str, Any]],
    all_items: dict[str, dict[str, Any]],
    raw_evidence: dict[str, Any],
    base_main_sha: str,
    job_results: dict[str, str],
    execution_results: list[dict[str, Any]],
    ancestor_checker: Callable[[Path, str, str], bool] = git_is_ancestor,
) -> dict[str, Any]:
    entries = raw_evidence.get("items")
    if not isinstance(entries, list):
        fail("P0_EVIDENCE_INVALID", "The P0 evidence input must contain an items list.")
    ids = [entry.get("id") for entry in entries]
    duplicates = sorted({item_id for item_id in ids if ids.count(item_id) > 1})
    if duplicates:
        fail("P0_ITEM_DUPLICATED", "A P0 item appears more than once.", ids=duplicates)
    expected_ids = {item["id"] for item in selected}
    actual_ids = set(ids)
    unknown = sorted(actual_ids - expected_ids)
    if unknown:
        fail("P0_ITEM_UNKNOWN", "The evidence contains unknown MVP-0/P0 items.", ids=unknown)
    missing = sorted(expected_ids - actual_ids)
    if missing:
        fail("P0_ITEM_MISSING", "One or more canonical MVP-0/P0 items have no evidence.", ids=missing)
    if len(entries) != EXPECTED_MVP0_P0_COUNT:
        fail(
            "P0_COUNT_UNEXPECTED",
            "The evaluated evidence count does not match the canonical count.",
            expected=EXPECTED_MVP0_P0_COUNT,
            actual=len(entries),
        )

    output_items: list[dict[str, Any]] = []
    evidence_owners: dict[str, tuple[str, str]] = {}
    for entry in entries:
        item_id = entry["id"]
        normative_item = all_items[item_id]
        status = entry.get("implementation_status")
        if status not in ITEM_STATUSES:
            fail("ITEM_STATUS_INVALID", "An item uses a status outside the closed vocabulary.", id=item_id)
        if status == "NOT_APPLICABLE" and not entry.get("normative_justification"):
            fail(
                "NOT_APPLICABLE_UNJUSTIFIED",
                "NOT_APPLICABLE requires an explicit normative justification.",
                id=item_id,
            )
        if item_id == "REL-000" and status == "VERIFIED":
            fail(
                "OWNER_APPROVAL_FALSELY_ASSERTED",
                "REL-000 cannot be VERIFIED before the owner decision.",
            )

        paths = entry.get("implementation_paths") or []
        tests = entry.get("required_test_sources") or []
        jobs = entry.get("authoritative_ci_jobs") or []
        required_tests = entry.get("required_tests") or []
        refs = entry.get("implementation_commits_or_prs") or []
        rollback_reference = entry.get("rollback_reference")
        for relative in paths:
            require_path(repository_root, relative, "EVIDENCE_PATH_NOT_FOUND")
        for relative in tests:
            require_path(repository_root, relative, "TEST_SOURCE_NOT_FOUND")
        if rollback_reference:
            rollback_path = str(rollback_reference).split("#", 1)[0]
            require_path(repository_root, rollback_path, "ROLLBACK_REFERENCE_NOT_FOUND")

        if status == "VERIFIED":
            if not paths or not tests or not jobs or not required_tests or not refs or not rollback_reference:
                fail(
                    "VERIFIED_EVIDENCE_INCOMPLETE",
                    "VERIFIED requires implementation, executable tests, CI and rollback evidence.",
                    id=item_id,
                )
            unknown_jobs = sorted(set(jobs) - REQUIRED_JOBS)
            if unknown_jobs:
                fail(
                    "AUTHORITATIVE_JOB_UNKNOWN",
                    "A VERIFIED item cites a job outside the closed vocabulary.",
                    id=item_id,
                    jobs=unknown_jobs,
                )
            unsuccessful = {
                job: job_results.get(job)
                for job in jobs
                if job_results.get(job) != "success"
            }
            if unsuccessful:
                fail(
                    "AUTHORITATIVE_ITEM_JOB_FAILED",
                    "A VERIFIED item cites a job that did not succeed.",
                    id=item_id,
                    jobs=unsuccessful,
                )
            required_sources = [required.get("source_path") for required in required_tests]
            if sorted(required_sources) != sorted(tests):
                fail(
                    "REQUIRED_TEST_SOURCE_UNBOUND",
                    "Every required test source must have one executable identity.",
                    id=item_id,
                )
            required_jobs = {required.get("job") for required in required_tests}
            unrelated_jobs = sorted(set(jobs) - required_jobs)
            if unrelated_jobs:
                fail(
                    "AUTHORITATIVE_JOB_UNRELATED",
                    "Every authoritative job must execute evidence required by the item.",
                    id=item_id,
                    jobs=unrelated_jobs,
                )
            matched_tests = []
            for required in required_tests:
                if required.get("job") not in jobs:
                    fail(
                        "REQUIRED_TEST_JOB_MISMATCH",
                        "A required test belongs to a job not cited by the item.",
                        id=item_id,
                    )
                require_path(
                    repository_root,
                    str(required.get("source_path") or ""),
                    "TEST_SOURCE_NOT_FOUND",
                )
                matched = match_execution_result(
                    execution_results,
                    required,
                    context=f"item:{item_id}",
                )
                matched_tests.append(
                    {
                        "job": matched["job"],
                        "test_project": matched["test_project"],
                        "fully_qualified_test_name": matched[
                            "fully_qualified_test_name"
                        ],
                        "category": matched["category"],
                        "outcome": matched["outcome"],
                        "executed": matched["executed"],
                        "skipped": matched["skipped"],
                        "trx_or_result_digest": matched["trx_or_result_digest"],
                        "artifact_id": matched["artifact_id"],
                        "artifact_digest": matched["artifact_digest"],
                        "source_head_sha": matched["source_head_sha"],
                        "tested_git_sha": matched["tested_git_sha"],
                        "base_main_sha": matched["base_main_sha"],
                        "workflow_run_id": matched["workflow_run_id"],
                        "workflow_run_attempt": matched["workflow_run_attempt"],
                    }
                )
            for ref in refs:
                normalized_ref = validate_sha(
                    ref, "IMPLEMENTATION_REF_MALFORMED", "implementation_commits_or_prs"
                )
                if not ancestor_checker(repository_root, normalized_ref, base_main_sha):
                    fail(
                        "IMPLEMENTATION_NOT_MERGED",
                        "A VERIFIED implementation reference is not in base main.",
                        id=item_id,
                        ref=normalized_ref,
                    )

        open_gates = entry.get("open_gates") or []
        if "REL-000-DEF-001" in open_gates:
            fail(
                "RESOLVED_NORMATIVE_DECISION_REOPENED",
                "REL-000-DEF-001 is resolved and cannot remain an open item gate.",
                id=item_id,
            )

        for evidence_kind, values in (("path", paths), ("test", tests)):
            for value in values:
                key = f"{evidence_kind}:{value}"
                if key in evidence_owners:
                    previous_id, previous_justification = evidence_owners[key]
                    current_justification = entry.get("shared_evidence_justification")
                    if not previous_justification or not current_justification:
                        fail(
                            "EVIDENCE_REUSED_WITHOUT_JUSTIFICATION",
                            "Evidence cannot cover two items without explicit justification.",
                            evidence=value,
                            items=[previous_id, item_id],
                        )
                else:
                    evidence_owners[key] = (
                        item_id,
                        entry.get("shared_evidence_justification") or "",
                    )

        output_items.append(
            {
                "id": item_id,
                "title": normative_item["title"],
                "release": normative_item["release"],
                "priority": normative_item["priority"],
                "dependencies": normative_item.get("depends_on") or [],
                "implementation_status": status,
                "implementation_commits_or_prs": refs,
                "implementation_paths": paths,
                "required_test_sources": tests,
                "required_tests": matched_tests if status == "VERIFIED" else [],
                "authoritative_ci_jobs": jobs,
                "rollback_reference": rollback_reference,
                "known_limitations": entry.get("known_limitations") or [],
                "open_gates": open_gates,
            }
        )

    output_items.sort(key=lambda item: item["id"])
    ids_by_status = {
        status: [item["id"] for item in output_items if item["implementation_status"] == status]
        for status in ITEM_STATUSES
    }
    return {
        "format_version": FORMAT_VERSION,
        "inventory_source": "docs/normative/v0.6/specs/AI-08_BACKLOG.yaml",
        "mvp0_p0_items_expected": len(selected),
        "mvp0_p0_items_evaluated": len(output_items),
        "mvp0_p0_items_verified": len(ids_by_status["VERIFIED"]),
        "mvp0_p0_items_partial": len(ids_by_status["PARTIAL"]),
        "mvp0_p0_items_not_started": len(ids_by_status["NOT_STARTED"]),
        "mvp0_p0_items_blocked": len(ids_by_status["BLOCKED"]),
        "mvp0_p0_items_missing": 0,
        "mvp0_p0_items_duplicated": 0,
        "mvp0_p0_items_unknown": 0,
        "ids_by_status": ids_by_status,
        "items": output_items,
    }


def validate_jobs(job_results: dict[str, str]) -> None:
    missing = sorted(REQUIRED_JOBS - set(job_results))
    if missing:
        fail("AUTHORITATIVE_JOB_MISSING", "An authoritative CI job result is absent.", jobs=missing)
    failed = {
        job: result for job, result in job_results.items() if job in REQUIRED_JOBS and result != "success"
    }
    if failed:
        fail(
            "AUTHORITATIVE_JOB_FAILED",
            "Every authoritative upstream job must finish successfully.",
            jobs=failed,
        )


def validate_cross_tenant(
    repository_root: Path,
    raw_manifest: dict[str, Any],
    job_results: dict[str, str],
    execution_results: list[dict[str, Any]],
) -> dict[str, Any]:
    sources = raw_manifest.get("sources")
    if not isinstance(sources, list):
        fail("CROSS_TENANT_MANIFEST_INVALID", "Cross-tenant evidence must contain sources.")
    ids = [source.get("evidence_id") for source in sources]
    if len(ids) != len(set(ids)):
        fail("CROSS_TENANT_SOURCE_DUPLICATED", "A cross-tenant evidence source is duplicated.")
    actual = set(ids)
    missing = sorted(REQUIRED_CROSS_TENANT_CATEGORIES - actual)
    if missing:
        fail(
            "CROSS_TENANT_CATEGORY_MISSING",
            "A required cross-tenant category is absent.",
            categories=missing,
        )
    unknown = sorted(actual - REQUIRED_CROSS_TENANT_CATEGORIES)
    if unknown:
        fail(
            "CROSS_TENANT_CATEGORY_UNKNOWN",
            "The closed cross-tenant manifest contains an unknown category.",
            categories=unknown,
        )
    output_sources = []
    result_owners: dict[tuple[str, str, str, str], tuple[str, str]] = {}
    for source in sources:
        job = source.get("job")
        if job not in REQUIRED_JOBS:
            fail(
                "CROSS_TENANT_JOB_UNKNOWN",
                "A cross-tenant source cites a job outside the closed vocabulary.",
                evidence_id=source["evidence_id"],
            )
        result = job_results.get(job)
        if result == "skipped":
            fail(
                "CROSS_TENANT_REQUIRED_SKIPPED",
                "A required cross-tenant source was skipped.",
                evidence_id=source["evidence_id"],
            )
        if result != "success":
            fail(
                "CROSS_TENANT_SOURCE_FAILED",
                "A required cross-tenant source did not pass.",
                evidence_id=source["evidence_id"],
                result=result,
            )
        source_path = require_path(
            repository_root,
            source.get("source_path") or "",
            "CROSS_TENANT_SOURCE_NOT_FOUND",
        )
        source_match = source.get("source_match")
        if not source_match or source_match not in source_path.read_text(encoding="utf-8"):
            fail(
                "CROSS_TENANT_EXPECTED_TEST_NOT_FOUND",
                "The expected test/category marker is absent from its source.",
                evidence_id=source["evidence_id"],
            )
        if source.get("expected_presence") is not True:
            fail(
                "CROSS_TENANT_EXPECTATION_INVALID",
                "Every required cross-tenant source must require presence.",
                evidence_id=source["evidence_id"],
            )
        required = {
            "job": job,
            "test_project": source.get("test_project"),
            "fully_qualified_test_name": source.get("fully_qualified_test_name"),
            "category": source.get("category"),
        }
        matched = match_execution_result(
            execution_results,
            required,
            context=f"cross_tenant:{source['evidence_id']}",
        )
        result_key = tuple(required[field] for field in (
            "job",
            "test_project",
            "fully_qualified_test_name",
            "category",
        ))
        justification = str(source.get("shared_execution_justification") or "")
        if result_key in result_owners:
            previous_id, previous_justification = result_owners[result_key]
            if not justification or not previous_justification:
                fail(
                    "CROSS_TENANT_RESULT_REUSED",
                    "One execution result cannot compensate for two sources without justification.",
                    evidence_ids=[previous_id, source["evidence_id"]],
                )
        else:
            result_owners[result_key] = (source["evidence_id"], justification)
        output_sources.append(
            {
                "evidence_id": source["evidence_id"],
                "job": job,
                "test_project": matched["test_project"],
                "fully_qualified_test_name": matched["fully_qualified_test_name"],
                "category": matched["category"],
                "outcome": matched["outcome"],
                "executed": matched["executed"],
                "skipped": matched["skipped"],
                "duration": matched.get("duration"),
                "trx_or_result_digest": matched["trx_or_result_digest"],
                "artifact_id": matched["artifact_id"],
                "artifact_digest": matched["artifact_digest"],
                "content_digest": matched["content_digest"],
                "source_head_sha": matched["source_head_sha"],
                "tested_git_sha": matched["tested_git_sha"],
                "base_main_sha": matched["base_main_sha"],
                "workflow_run_id": matched["workflow_run_id"],
                "workflow_run_attempt": matched["workflow_run_attempt"],
            }
        )
    executed = sum(source["executed"] is True for source in output_sources)
    passed = sum(source["outcome"] == "PASSED" for source in output_sources)
    failed = sum(source["outcome"] == "FAILED" for source in output_sources)
    skipped = sum(source["skipped"] is True for source in output_sources)
    return {
        "format_version": FORMAT_VERSION,
        "cross_tenant_sources_expected": len(REQUIRED_CROSS_TENANT_CATEGORIES),
        "cross_tenant_sources_executed": executed,
        "cross_tenant_sources_passed": passed,
        "cross_tenant_sources_missing": len(REQUIRED_CROSS_TENANT_CATEGORIES) - len(output_sources),
        "cross_tenant_sources_failed": failed,
        "cross_tenant_sources_skipped": skipped,
        "cross_tenant_incidents_observed": failed,
        "sources": sorted(output_sources, key=lambda source: source["evidence_id"]),
    }


def safe_artifact_relative_path(value: str) -> Path:
    path = Path(value)
    if path.is_absolute() or ".." in path.parts or value in {"", "."}:
        fail("ARTIFACT_PATH_INVALID", "Artifact provenance contains an unsafe relative path.")
    return path


def canonical_content_digest(files: Iterable[dict[str, Any]]) -> str:
    lines = [
        f"{entry['sha256']}  {entry['path']}  {entry['bytes']}"
        for entry in sorted(files, key=lambda item: item["path"])
    ]
    return hashlib.sha256(("\n".join(lines) + "\n").encode("utf-8")).hexdigest()


def create_provenance(
    artifact_directory: Path,
    artifact_name: str,
    workflow_run_id: str,
    workflow_run_attempt: str,
    source_head_sha: str,
    tested_git_sha: str,
    base_main_sha: str,
    git_relationship: str,
) -> dict[str, Any]:
    files: list[dict[str, Any]] = []
    for path in sorted(artifact_directory.rglob("*")):
        if not path.is_file() or path.name == "rel000-source-provenance.json":
            continue
        relative = path.relative_to(artifact_directory).as_posix()
        files.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha256_file(path)})
    if not files:
        fail("ARTIFACT_CONTENT_MISSING", "No files are available for source provenance.")
    provenance = {
        "format_version": SOURCE_PROVENANCE_VERSION,
        "artifact_name": artifact_name,
        "workflow_run_id": str(workflow_run_id),
        "workflow_run_attempt": str(workflow_run_attempt),
        "source_head_sha": source_head_sha,
        "tested_git_sha": tested_git_sha,
        "base_main_sha": base_main_sha,
        "git_relationship": git_relationship,
        "files": files,
        "content_digest": canonical_content_digest(files),
    }
    write_json(artifact_directory / "rel000-source-provenance.json", provenance)
    return provenance


def validate_artifact(
    artifact_directory: Path,
    artifact_name: str,
    artifact_id: str,
    artifact_digest: str,
    trace: dict[str, str],
    workflow_run_id: str,
    workflow_run_attempt: str,
    allowed: Callable[[str], bool],
) -> dict[str, Any]:
    if not artifact_directory.is_dir():
        fail("ARTIFACT_MISSING", f"Artifact {artifact_name} is absent.", artifact=artifact_name)
    if not str(artifact_id).isdigit() or int(artifact_id) <= 0:
        fail("ARTIFACT_ID_INVALID", "The artifact ID is missing or invalid.", artifact=artifact_name)
    digest = str(artifact_digest).lower()
    if not SHA256.fullmatch(digest):
        fail("ARTIFACT_DIGEST_INVALID", "The upload artifact digest is invalid.", artifact=artifact_name)
    provenance_path = artifact_directory / "rel000-source-provenance.json"
    if not provenance_path.is_file():
        fail("ARTIFACT_PROVENANCE_MISSING", "Artifact provenance is absent.", artifact=artifact_name)
    provenance = load_json(provenance_path)
    required_matches = {
        "format_version": SOURCE_PROVENANCE_VERSION,
        "artifact_name": artifact_name,
        "workflow_run_id": str(workflow_run_id),
        "workflow_run_attempt": str(workflow_run_attempt),
        **trace,
    }
    for field, expected in required_matches.items():
        if str(provenance.get(field)) != str(expected):
            reason = "ARTIFACT_RUN_MISMATCH" if field.startswith("workflow_run") else "ARTIFACT_SHA_MISMATCH"
            fail(
                reason,
                "Artifact provenance does not match the current run and checkout.",
                artifact=artifact_name,
                field=field,
            )
    files = provenance.get("files")
    if not isinstance(files, list) or not files:
        fail("ARTIFACT_CONTENT_MISSING", "Artifact provenance has no file inventory.")
    seen: set[str] = set()
    for entry in files:
        relative = str(entry.get("path") or "")
        safe_relative = safe_artifact_relative_path(relative)
        if relative in seen:
            fail("ARTIFACT_FILE_DUPLICATED", "Artifact provenance duplicates a file.", path=relative)
        seen.add(relative)
        if not allowed(relative):
            fail(
                "ARTIFACT_FORMAT_FORBIDDEN",
                "Artifact provenance lists a forbidden file format.",
                artifact=artifact_name,
                path=relative,
            )
        target = artifact_directory / safe_relative
        if not target.is_file():
            fail("ARTIFACT_FILE_MISSING", "A declared artifact file is absent.", path=relative)
        if target.stat().st_size != int(entry.get("bytes", -1)):
            fail("ARTIFACT_FILE_SIZE_MISMATCH", "An artifact file size does not match provenance.")
        if sha256_file(target) != entry.get("sha256"):
            fail("ARTIFACT_FILE_DIGEST_MISMATCH", "An artifact file digest does not match provenance.")
    undeclared = {
        path.relative_to(artifact_directory).as_posix()
        for path in artifact_directory.rglob("*")
        if path.is_file() and path.name != "rel000-source-provenance.json"
    } - seen
    if undeclared:
        fail(
            "ARTIFACT_UNDECLARED_FILE",
            "Artifact content contains files absent from provenance.",
            files=sorted(undeclared),
        )
    expected_content_digest = canonical_content_digest(files)
    if provenance.get("content_digest") != expected_content_digest:
        fail("ARTIFACT_CONTENT_DIGEST_MISMATCH", "The artifact content digest is invalid.")
    return {
        "artifact_id": int(artifact_id),
        "artifact_name": artifact_name,
        "artifact_digest": digest,
        "content_digest": expected_content_digest,
        "files": sorted(seen),
    }


def _parse_result_spec(value: str) -> tuple[str, str | None, str]:
    identity, separator, raw_path = str(value).partition("=")
    project, category_separator, category = identity.partition("|")
    if not separator or not project or not raw_path or (category_separator and not category):
        fail(
            "EXECUTION_RESULT_SPEC_INVALID",
            "Execution result inputs must use project=path.",
        )
    return project, category if category_separator else None, raw_path


def collect_execution_results(args: argparse.Namespace) -> dict[str, Any]:
    output = args.output.resolve()
    artifact_root = output.parent
    artifact_root.mkdir(parents=True, exist_ok=True)
    if output.exists():
        fail("EXECUTION_MANIFEST_EXISTS", "Execution evidence cannot overwrite a manifest.")

    tests: list[dict[str, Any]] = []
    result_files: list[dict[str, Any]] = []
    parsers = (
        (args.trx or [], parse_trx_results),
        (args.junit or [], parse_junit_results),
        (args.structured or [], parse_structured_results),
    )
    for specs, parser in parsers:
        for spec in specs:
            project, category_override, raw_pattern = _parse_result_spec(spec)
            matched_paths = [Path(value).resolve() for value in glob.glob(raw_pattern)]
            if not matched_paths:
                fail(
                    "EXECUTION_RESULT_PATH_INVALID",
                    "An execution result pattern matched no files.",
                    pattern=raw_pattern,
                )
            for source in matched_paths:
                try:
                    relative = source.relative_to(artifact_root).as_posix()
                except ValueError:
                    fail(
                        "EXECUTION_RESULT_PATH_INVALID",
                        "Execution results must be inside the uploaded artifact root.",
                    )
                safe_artifact_relative_path(relative)
                if source.is_symlink() or not source.is_file():
                    fail(
                        "EXECUTION_RESULT_PATH_INVALID",
                        "Execution result inputs must be regular files.",
                        path=relative,
                    )
                tests.extend(parser(source, args.job, project, category_override))
                result_files.append(
                    {
                        "path": relative,
                        "bytes": source.stat().st_size,
                        "sha256": sha256_file(source),
                    }
                )
    if not result_files or not tests:
        fail("EXECUTION_RESULT_EMPTY", "No structured execution evidence was collected.")

    seen_outcomes: dict[tuple[str, str, str, str], str] = {}
    for result in tests:
        key = (
            result["job"],
            result["test_project"],
            result["fully_qualified_test_name"],
            result["category"],
        )
        previous = seen_outcomes.get(key)
        if previous is not None and previous != result["outcome"]:
            fail(
                "EXECUTION_RESULT_CONTRADICTORY",
                "Duplicate test results have contradictory outcomes.",
                job=result["job"],
                test=result["fully_qualified_test_name"],
            )
        seen_outcomes[key] = result["outcome"]

    manifest = {
        "format_version": EXECUTION_EVIDENCE_VERSION,
        "artifact_name": args.artifact_name,
        "job": args.job,
        "workflow_run_id": str(args.workflow_run_id),
        "workflow_run_attempt": str(args.workflow_run_attempt),
        "source_head_sha": validate_sha(
            args.source_head_sha, "SOURCE_HEAD_SHA_MALFORMED", "source_head_sha"
        ),
        "tested_git_sha": validate_sha(
            args.tested_git_sha, "TESTED_GIT_SHA_MALFORMED", "tested_git_sha"
        ),
        "base_main_sha": validate_sha(
            args.base_main_sha, "BASE_MAIN_SHA_MALFORMED", "base_main_sha"
        ),
        "result_files": sorted(result_files, key=lambda entry: entry["path"]),
        "content_digest": canonical_content_digest(result_files),
        "tests": tests,
    }
    write_json(output, manifest)
    return manifest


def sanitize_execution_artifacts(
    input_path: Path,
    output_path: Path,
    workflow_run_id: str,
) -> None:
    payload = load_json(input_path)
    artifacts = payload.get("artifacts") if isinstance(payload, dict) else None
    if not isinstance(artifacts, list):
        fail("EXECUTION_ARTIFACT_METADATA_INVALID", "GitHub artifact metadata is invalid.")
    sanitized: list[dict[str, Any]] = []
    for artifact in artifacts:
        name = str(artifact.get("name") or "")
        if not (
            name.startswith("rel000-execution-")
            or name in {"delivery-simulation-results", "ops002-backup-restore-results"}
        ):
            continue
        digest = normalized_sha256(
            str(artifact.get("digest") or ""),
            "EXECUTION_ARTIFACT_DIGEST_INVALID",
            "artifact_digest",
        )
        artifact_id = artifact.get("id")
        if not isinstance(artifact_id, int) or artifact_id <= 0:
            fail("EXECUTION_ARTIFACT_ID_INVALID", "An execution artifact ID is invalid.")
        if artifact.get("expired") is True:
            fail("EXECUTION_ARTIFACT_EXPIRED", "A current execution artifact is expired.")
        run = artifact.get("workflow_run") or {}
        if str(run.get("id") or "") != str(workflow_run_id):
            fail("EXECUTION_ARTIFACT_RUN_MISMATCH", "Execution artifact metadata is stale.")
        sanitized.append(
            {
                "artifact_id": artifact_id,
                "artifact_name": name,
                "artifact_digest": digest,
                "workflow_run_id": str(workflow_run_id),
            }
        )
    names = [entry["artifact_name"] for entry in sanitized]
    if len(names) != len(set(names)):
        fail("EXECUTION_ARTIFACT_DUPLICATED", "Execution artifact names are duplicated.")
    write_json(output_path, {"artifacts": sorted(sanitized, key=lambda entry: entry["artifact_name"])})


def materialize_execution_artifacts(
    raw_json: str,
    output_path: Path,
    workflow_run_id: str,
) -> None:
    try:
        entries = json.loads(raw_json)
    except json.JSONDecodeError as exc:
        fail("EXECUTION_ARTIFACT_METADATA_INVALID", "Artifact output JSON is invalid.", error=str(exc))
    if not isinstance(entries, list) or not entries:
        fail("EXECUTION_ARTIFACT_METADATA_INVALID", "Artifact output JSON must be a list.")
    sanitized = []
    for entry in entries:
        if not isinstance(entry, dict):
            fail("EXECUTION_ARTIFACT_METADATA_INVALID", "An artifact output is malformed.")
        artifact_id = str(entry.get("artifact_id") or "")
        if not artifact_id.isdigit() or int(artifact_id) <= 0:
            fail("EXECUTION_ARTIFACT_ID_INVALID", "An execution artifact ID is invalid.")
        artifact_name = str(entry.get("artifact_name") or "")
        if not artifact_name:
            fail("EXECUTION_ARTIFACT_METADATA_INVALID", "An execution artifact name is absent.")
        sanitized.append(
            {
                "artifact_id": int(artifact_id),
                "artifact_name": artifact_name,
                "artifact_digest": normalized_sha256(
                    str(entry.get("artifact_digest") or ""),
                    "EXECUTION_ARTIFACT_DIGEST_INVALID",
                    "artifact_digest",
                ),
                "workflow_run_id": str(workflow_run_id),
            }
        )
    names = [entry["artifact_name"] for entry in sanitized]
    if len(names) != len(set(names)):
        fail("EXECUTION_ARTIFACT_DUPLICATED", "Execution artifact names are duplicated.")
    write_json(output_path, {"artifacts": sorted(sanitized, key=lambda item: item["artifact_name"])})


def synthetic_generation(args: argparse.Namespace) -> None:
    if os.environ.get("REL000_TEST_MODE") != "true":
        fail("SYNTHETIC_MODE_FORBIDDEN", "Synthetic generation is restricted to REL-000 tests.")
    scenario = args.scenario
    if scenario == "artifact-other-sha":
        fail("EXECUTION_RESULT_SHA_MISMATCH", "Synthetic artifact belongs to another SHA.")
    if scenario == "artifact-other-run":
        fail("EXECUTION_RESULT_RUN_MISMATCH", "Synthetic artifact belongs to another run.")
    output = args.output.resolve()
    assert_output_is_fresh(output)
    output.mkdir(parents=False, exist_ok=False)
    names = sorted(OUTPUT_FILES)
    for index, name in enumerate(names):
        write_json(
            output / name,
            {
                "format_version": FORMAT_VERSION,
                "synthetic": True,
                "sequence": index,
            },
        )
        if scenario == "cancel-after-first-json" and index == 0:
            fail("GENERATION_CANCELLED", "Synthetic generation cancelled after the first JSON.")
    if scenario == "manifest-incomplete":
        (output / names[-1]).unlink()


def load_execution_evidence(
    directory: Path,
    artifact_metadata_path: Path,
    trace: dict[str, str],
    workflow_run_id: str,
    workflow_run_attempt: str,
) -> tuple[list[dict[str, Any]], dict[str, dict[str, Any]]]:
    if not directory.is_dir():
        fail("EXECUTION_ARTIFACT_MISSING", "The execution artifact directory is absent.")
    metadata_payload = load_json(artifact_metadata_path)
    metadata_entries = metadata_payload.get("artifacts") if isinstance(metadata_payload, dict) else None
    if not isinstance(metadata_entries, list):
        fail("EXECUTION_ARTIFACT_METADATA_INVALID", "Execution artifact metadata is invalid.")
    metadata = {
        str(entry.get("artifact_name")): entry
        for entry in metadata_entries
        if isinstance(entry, dict)
    }

    manifests = list(directory.rglob("rel000-execution-results.json"))
    if not manifests:
        fail("EXECUTION_ARTIFACT_MISSING", "No execution manifests were downloaded.")
    tests: list[dict[str, Any]] = []
    artifacts_by_job: dict[str, dict[str, Any]] = {}
    observed_outcomes: dict[tuple[str, str, str, str], str] = {}
    for manifest_path in manifests:
        if manifest_path.is_symlink():
            fail("EXECUTION_RESULT_PATH_INVALID", "Execution manifests cannot be links.")
        manifest = load_json(manifest_path)
        expected_fields = {
            "format_version": EXECUTION_EVIDENCE_VERSION,
            "workflow_run_id": str(workflow_run_id),
            "workflow_run_attempt": str(workflow_run_attempt),
            "source_head_sha": trace["source_head_sha"],
            "tested_git_sha": trace["tested_git_sha"],
            "base_main_sha": trace["base_main_sha"],
        }
        for field, expected in expected_fields.items():
            if str(manifest.get(field)) != str(expected):
                reason = (
                    "EXECUTION_RESULT_RUN_MISMATCH"
                    if field.startswith("workflow_run")
                    else "EXECUTION_RESULT_SHA_MISMATCH"
                )
                fail(reason, "Execution evidence is not from the current run.", field=field)
        job = str(manifest.get("job") or "")
        artifact_name = str(manifest.get("artifact_name") or "")
        if job not in EXECUTION_EVIDENCE_JOBS or job in artifacts_by_job:
            fail("EXECUTION_JOB_INVALID", "Execution evidence has an unknown or duplicate job.", job=job)
        artifact = metadata.get(artifact_name)
        if artifact is None:
            fail(
                "EXECUTION_ARTIFACT_METADATA_MISSING",
                "Execution artifact metadata is absent.",
                artifact=artifact_name,
            )
        if str(artifact.get("workflow_run_id")) != str(workflow_run_id):
            fail("EXECUTION_ARTIFACT_RUN_MISMATCH", "Execution artifact metadata is stale.")
        artifact_id = artifact.get("artifact_id")
        if not isinstance(artifact_id, int) or artifact_id <= 0:
            fail("EXECUTION_ARTIFACT_ID_INVALID", "Execution artifact ID is invalid.")
        artifact_digest = normalized_sha256(
            str(artifact.get("artifact_digest") or ""),
            "EXECUTION_ARTIFACT_DIGEST_INVALID",
            "artifact_digest",
        )

        files = manifest.get("result_files")
        if not isinstance(files, list) or not files:
            fail("EXECUTION_RESULT_EMPTY", "Execution evidence has no result files.")
        artifact_root = manifest_path.parent
        for entry in files:
            relative = str(entry.get("path") or "")
            target = artifact_root / safe_artifact_relative_path(relative)
            if target.is_symlink() or not target.is_file():
                fail("EXECUTION_RESULT_FILE_MISSING", "An execution result file is absent.")
            if target.stat().st_size != int(entry.get("bytes", -1)):
                fail("EXECUTION_RESULT_FILE_SIZE_MISMATCH", "Execution result size changed.")
            if sha256_file(target) != entry.get("sha256"):
                fail("EXECUTION_RESULT_FILE_DIGEST_MISMATCH", "Execution result digest changed.")
        content_digest = canonical_content_digest(files)
        if manifest.get("content_digest") != content_digest:
            fail("EXECUTION_CONTENT_DIGEST_MISMATCH", "Execution content digest is invalid.")

        raw_tests = manifest.get("tests")
        if not isinstance(raw_tests, list) or not raw_tests:
            fail("EXECUTION_RESULT_EMPTY", "Execution evidence contains no tests.")
        for result in raw_tests:
            required = (
                "job",
                "test_project",
                "fully_qualified_test_name",
                "category",
                "outcome",
                "executed",
                "skipped",
                "trx_or_result_digest",
            )
            if not isinstance(result, dict) or any(key not in result for key in required):
                fail("EXECUTION_RESULT_MALFORMED", "An execution result is incomplete.")
            if result["job"] != job:
                fail("EXECUTION_JOB_INVALID", "A test result belongs to another job.")
            normalized_result_digest = normalized_sha256(
                str(result["trx_or_result_digest"]),
                "EXECUTION_RESULT_DIGEST_INVALID",
                "trx_or_result_digest",
            )
            if normalized_result_digest not in {entry.get("sha256") for entry in files}:
                fail(
                    "EXECUTION_RESULT_DIGEST_MISMATCH",
                    "A test result is not backed by a declared result file.",
                )
            enriched = {
                **result,
                "trx_or_result_digest": normalized_result_digest,
                "artifact_id": artifact_id,
                "artifact_name": artifact_name,
                "artifact_digest": artifact_digest,
                "content_digest": content_digest,
                "source_head_sha": trace["source_head_sha"],
                "tested_git_sha": trace["tested_git_sha"],
                "base_main_sha": trace["base_main_sha"],
                "workflow_run_id": str(workflow_run_id),
                "workflow_run_attempt": str(workflow_run_attempt),
            }
            validate_execution_context(
                enriched,
                trace,
                workflow_run_id,
                workflow_run_attempt,
            )
            key = (
                enriched["job"],
                enriched["test_project"],
                enriched["fully_qualified_test_name"],
                enriched["category"],
            )
            prior = observed_outcomes.get(key)
            if prior is not None and prior != enriched["outcome"]:
                fail(
                    "EXECUTION_RESULT_CONTRADICTORY",
                    "Duplicate execution results have contradictory outcomes.",
                    job=job,
                    test=enriched["fully_qualified_test_name"],
                )
            observed_outcomes[key] = enriched["outcome"]
            tests.append(enriched)
        artifacts_by_job[job] = {
            "artifact_id": artifact_id,
            "artifact_name": artifact_name,
            "artifact_digest": artifact_digest,
            "content_digest": content_digest,
            "source_head_sha": trace["source_head_sha"],
            "tested_git_sha": trace["tested_git_sha"],
            "base_main_sha": trace["base_main_sha"],
            "workflow_run_id": str(workflow_run_id),
            "workflow_run_attempt": str(workflow_run_attempt),
        }

    missing_jobs = sorted(EXECUTION_EVIDENCE_JOBS - set(artifacts_by_job))
    if missing_jobs:
        fail(
            "EXECUTION_ARTIFACT_MISSING",
            "Required jobs did not publish structured execution evidence.",
            jobs=missing_jobs,
        )
    return tests, artifacts_by_job


def match_execution_result(
    execution_results: list[dict[str, Any]],
    required: dict[str, Any],
    *,
    context: str,
) -> dict[str, Any]:
    identity_fields = (
        "job",
        "test_project",
        "fully_qualified_test_name",
        "category",
    )
    if any(not required.get(field) for field in identity_fields):
        fail("REQUIRED_TEST_IDENTITY_INVALID", "Required test identity is incomplete.", context=context)
    matches = [
        result
        for result in execution_results
        if all(result.get(field) == required.get(field) for field in identity_fields)
    ]
    if not matches:
        fail(
            "REQUIRED_TEST_RESULT_MISSING",
            "A required test did not execute in the declared job and project.",
            context=context,
            test=required.get("fully_qualified_test_name"),
        )
    outcomes = {result.get("outcome") for result in matches}
    if len(outcomes) != 1:
        fail(
            "EXECUTION_RESULT_CONTRADICTORY",
            "A required test has contradictory outcomes.",
            context=context,
        )
    result = matches[0]
    if result.get("skipped") is True or result.get("executed") is not True:
        fail("REQUIRED_TEST_SKIPPED", "A required test was skipped or not executed.", context=context)
    if result.get("outcome") != "PASSED":
        fail("REQUIRED_TEST_FAILED", "A required test did not pass.", context=context)
    return result


def validate_execution_context(
    result: dict[str, Any],
    trace: dict[str, str],
    workflow_run_id: str,
    workflow_run_attempt: str,
) -> None:
    expected = {
        "source_head_sha": trace["source_head_sha"],
        "tested_git_sha": trace["tested_git_sha"],
        **(
            {"base_main_sha": trace["base_main_sha"]}
            if "base_main_sha" in trace
            else {}
        ),
        "workflow_run_id": str(workflow_run_id),
        "workflow_run_attempt": str(workflow_run_attempt),
    }
    for field, value in expected.items():
        if str(result.get(field)) != str(value):
            reason = (
                "EXECUTION_RESULT_RUN_MISMATCH"
                if field.startswith("workflow_run")
                else "EXECUTION_RESULT_SHA_MISMATCH"
            )
            fail(reason, "A test result is stale.", field=field)


def find_one(directory: Path, pattern: str, reason_code: str) -> Path:
    matches = list(directory.rglob(pattern))
    if len(matches) != 1:
        fail(reason_code, "Expected exactly one artifact evidence file.", pattern=pattern, count=len(matches))
    return matches[0]


def assert_metric(data: dict[str, Any], key: str, expected: Any, reason: str) -> None:
    if key not in data:
        fail("OPS_METRIC_MISSING", "A required OPS evidence metric is missing.", metric=key)
    if data[key] != expected:
        fail(reason, "An OPS evidence metric does not meet the exact contract.", metric=key, actual=data[key])


def parse_trx_successes(path: Path) -> set[str]:
    try:
        tree = ET.parse(path)
    except (OSError, ET.ParseError) as exc:
        fail("OPS001_TRX_INVALID", "The OPS-001 TRX cannot be parsed.", error=str(exc))
    successes: set[str] = set()
    failures: list[str] = []
    for node in tree.getroot().iter():
        if node.tag.endswith("UnitTestResult"):
            name = node.attrib.get("testName", "")
            outcome = node.attrib.get("outcome", "")
            if outcome == "Passed":
                successes.add(name)
            elif outcome in {"Failed", "NotExecuted"}:
                failures.append(name)
    if failures:
        fail("OPS001_TRX_REQUIRED_TEST_FAILED", "OPS-001 TRX contains a failed or skipped test.", tests=failures)
    return successes


def validate_ops001(directory: Path) -> dict[str, Any]:
    report_path = find_one(directory, "ops001-delivery-simulation.json", "OPS001_REPORT_MISSING")
    report = load_json(report_path)
    exact_metrics = {
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
        "audits_expected": 340,
        "audits_exactly_matched": 340,
        "audits_missing": 0,
        "audits_duplicated": 0,
        "audits_mismatched": 0,
        "secondary_tenant_rows": 0,
        "outbox_dead_expected": 1,
        "outbox_dead_actual": 1,
        "newer_message_processed_after_poison": True,
    }
    for key, expected in exact_metrics.items():
        reason = "OPS001_METRIC_INVALID"
        if key == "realtime_events_missing":
            reason = "OPS001_REALTIME_MISSING"
        elif key == "realtime_events_unexpected":
            reason = "OPS001_REALTIME_UNEXPECTED"
        elif key == "audits_missing":
            reason = "OPS001_AUDIT_MISSING"
        elif key == "audits_duplicated":
            reason = "OPS001_AUDIT_DUPLICATED"
        assert_metric(report, key, expected, reason)
    for key in ("stale_recoveries", "stale_lease_rejections"):
        if key not in report:
            fail("OPS_METRIC_MISSING", "A required OPS-001 metric is missing.", metric=key)
        if int(report[key]) < 1:
            fail("OPS001_METRIC_INVALID", "OPS-001 recovery evidence is insufficient.", metric=key)
    if "realtime_observation_percent" not in report:
        fail("OPS_METRIC_MISSING", "Realtime observation percentage is absent.")
    if float(report["realtime_observation_percent"]) < 98:
        fail("OPS001_OBSERVATION_INSUFFICIENT", "Realtime observation is below 98 percent.")
    trx_path = find_one(directory, "*.trx", "OPS001_TRX_MISSING")
    successes = parse_trx_successes(trx_path)
    required_test_names = {
        "Two_consecutive_runs_deliver_twenty_orders_with_worker_recovery",
        "Cancellation_of_real_runner_releases_owned_resources_and_allows_next_run",
    }
    missing_tests = sorted(
        test_name
        for test_name in required_test_names
        if not any(test_name in actual for actual in successes)
    )
    if missing_tests:
        fail(
            "OPS001_REQUIRED_TEST_MISSING",
            "OPS-001 did not execute every required runner lifecycle test.",
            tests=missing_tests,
        )
    return report


def validate_append_only_evidence(report: dict[str, Any], prefix: str = "") -> None:
    expected = {
        "tables_expected": 4,
        "permission_checks_verified": 4,
        "triggers_verified": 4,
        "update_guards_verified": 4,
        "delete_guards_verified": 4,
        "trigger_failures_verified": 8,
        "permission_failures": 0,
        "rows_intact_verified": 8,
    }
    for suffix, value in expected.items():
        key = f"{prefix}{suffix}"
        assert_metric(report, key, value, "OPS002_APPEND_ONLY_INVALID")
    assert_metric(report, f"{prefix}contract_sqlstate", "42501", "OPS002_APPEND_ONLY_INVALID")
    assert_metric(
        report,
        f"{prefix}contract_message",
        "qualified_table_is_append_only",
        "OPS002_APPEND_ONLY_INVALID",
    )


def validate_ops002(directory: Path) -> tuple[dict[str, Any], dict[str, Any]]:
    report_path = find_one(directory, "ops002-restore-drill-report.json", "OPS002_REPORT_MISSING")
    negative_path = find_one(directory, "ops002-negative-tests.json", "OPS002_NEGATIVE_REPORT_MISSING")
    report = load_json(report_path)
    negative = load_json(negative_path)
    expected_report = {
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
    }
    for key, expected in expected_report.items():
        reason = "OPS002_REPORT_TAMPERED"
        if key == "plaintext_residue_detected" and report.get(key) is not False:
            reason = "OPS002_PLAINTEXT_RESIDUE"
        assert_metric(report, key, expected, reason)
    assert_metric(negative, "required_checks", 70, "OPS002_NEGATIVE_TESTS_INVALID")
    assert_metric(negative, "passed_checks", 70, "OPS002_NEGATIVE_TESTS_INVALID")
    checks = negative.get("checks")
    if not isinstance(checks, list) or len(checks) != 70 or any(
        check.get("passed") is not True for check in checks
    ):
        fail("OPS002_NEGATIVE_TESTS_INVALID", "OPS-002 negative/security evidence is incomplete.")
    validate_append_only_evidence(report, "append_only_")
    for stage in ("append_only_before_restart", "append_only_after_restart"):
        nested = report.get(stage)
        if not isinstance(nested, dict):
            fail("OPS002_RESTART_EVIDENCE_MISSING", "Append-only restart evidence is missing.", stage=stage)
        validate_append_only_evidence(nested)
    allowed_files = [
        path
        for path in directory.rglob("*")
        if path.is_file() and path.name != "rel000-source-provenance.json"
    ]
    if not allowed_files or any(
        not (path.name.endswith(".json") or path.name.endswith(".tar.gz.age"))
        for path in allowed_files
    ):
        fail("OPS002_ARTIFACT_FORMAT_FORBIDDEN", "OPS-002 contains a forbidden artifact format.")
    return report, negative


def validate_rollback(
    repository_root: Path,
    raw_manifest: dict[str, Any],
    selected: list[dict[str, Any]],
    rollback_execution: dict[str, Any],
) -> dict[str, Any]:
    required_execution_fields = (
        "rollback_scenarios_expected",
        "rollback_scenarios_executed",
        "rollback_scenarios_passed",
        "rollback_scenarios_failed",
        "rollback_cleanup_verified",
        "foreign_targets_preserved",
        "successful_report_after_failure",
    )
    if any(field not in rollback_execution for field in required_execution_fields):
        fail("ROLLBACK_EXECUTION_INCOMPLETE", "Rollback execution evidence is incomplete.")
    expected_scenarios = int(rollback_execution["rollback_scenarios_expected"])
    executed_scenarios = int(rollback_execution["rollback_scenarios_executed"])
    passed_scenarios = int(rollback_execution["rollback_scenarios_passed"])
    failed_scenarios = int(rollback_execution["rollback_scenarios_failed"])
    rollback_execution_passed = (
        expected_scenarios > 0
        and executed_scenarios == expected_scenarios
        and passed_scenarios == expected_scenarios
        and failed_scenarios == 0
        and rollback_execution["rollback_cleanup_verified"] is True
        and rollback_execution["foreign_targets_preserved"] is True
        and rollback_execution["successful_report_after_failure"] is False
    )
    if not rollback_execution_passed:
        fail("ROLLBACK_EXECUTION_FAILED", "The end-to-end rollback contract did not pass.")

    entries = raw_manifest.get("items")
    if not isinstance(entries, list):
        fail("ROLLBACK_MANIFEST_INVALID", "Rollback evidence must contain an items list.")
    expected_ids = {item["id"] for item in selected}
    ids = [entry.get("owning_backlog_item") for entry in entries]
    if len(ids) != len(set(ids)):
        fail("ROLLBACK_ITEM_DUPLICATED", "Rollback evidence duplicates a backlog item.")
    unknown = sorted(set(ids) - expected_ids)
    if unknown:
        fail(
            "ROLLBACK_ITEM_UNKNOWN",
            "Rollback evidence contains an item outside MVP-0/P0.",
            ids=unknown,
        )
    missing = sorted(expected_ids - set(ids))
    if missing:
        fail("ROLLBACK_MISSING", "A required MVP-0 rollback entry is absent.", ids=missing)
    output = []
    for entry in entries:
        reference = entry.get("rollback_reference")
        if not reference:
            fail("ROLLBACK_MISSING", "A rollback reference is absent.", id=entry.get("owning_backlog_item"))
        require_path(
            repository_root,
            str(reference).split("#", 1)[0],
            "ROLLBACK_REFERENCE_NOT_FOUND",
        )
        command = entry.get("verification_command")
        test_evidence = entry.get("test_evidence")
        if not command or not test_evidence:
            fail("ROLLBACK_EVIDENCE_INCOMPLETE", "Rollback verification evidence is incomplete.")
        require_path(repository_root, test_evidence, "ROLLBACK_TEST_SOURCE_NOT_FOUND")
        if entry.get("destructive_actions_required") is True:
            fail("ROLLBACK_DESTRUCTIVE", "REL-000 rollback evidence cannot require destructive actions.")
        if entry.get("data_preservation") is not True:
            fail("ROLLBACK_DATA_PRESERVATION_MISSING", "Rollback must preserve data.")
        status = entry.get("status")
        if status not in {"VERIFIED", "BLOCKED"}:
            fail("ROLLBACK_STATUS_INVALID", "Rollback status is outside the closed release vocabulary.")
        output.append(
            {
                "component": entry["component"],
                "owning_backlog_item": entry["owning_backlog_item"],
                "rollback_reference": reference,
                "rollback_type": entry["rollback_type"],
                "destructive_actions_required": False,
                "data_preservation": True,
                "verification_command": command,
                "test_evidence": test_evidence,
                "status": status,
                "rollback_reference_verified": True,
                "rollback_execution_verified": (
                    entry["owning_backlog_item"] == "REL-000"
                    and rollback_execution_passed
                ),
            }
        )
    verified = sum(entry["status"] == "VERIFIED" for entry in output)
    return {
        "format_version": FORMAT_VERSION,
        "rollback_items_expected": len(expected_ids),
        "rollback_items_verified": verified,
        "rollback_items_blocked": len(output) - verified,
        "rollback_items_missing": 0,
        "rollback_items_unknown": 0,
        "rollback_items_duplicated": 0,
        "rollback_scenarios_expected": expected_scenarios,
        "rollback_scenarios_executed": executed_scenarios,
        "rollback_scenarios_passed": passed_scenarios,
        "rollback_scenarios_failed": failed_scenarios,
        "rollback_cleanup_verified": rollback_execution["rollback_cleanup_verified"],
        "foreign_targets_preserved": rollback_execution["foreign_targets_preserved"],
        "successful_report_after_failure": rollback_execution[
            "successful_report_after_failure"
        ],
        "rollback_test_passed": rollback_execution_passed,
        "items": sorted(output, key=lambda item: item["owning_backlog_item"]),
    }


def validate_decisions(gates: dict[str, Any]) -> dict[str, Any]:
    open_decisions = gates.get("open_decisions")
    resolved_decisions = gates.get("resolved_decisions")
    if not isinstance(open_decisions, list) or not isinstance(resolved_decisions, list):
        fail("GATES_SOURCE_INVALID", "AI-10 decision collections are invalid.")
    open_ids = {decision.get("id") for decision in open_decisions}
    missing = sorted(REQUIRED_OPEN_DECISIONS - open_ids)
    if missing:
        fail("OPEN_GATE_OMITTED", "AI-10 is missing a required open decision.", gates=missing)
    resolved_ids = {decision.get("id") for decision in resolved_decisions}
    if "GATE-002" not in resolved_ids:
        fail("GATE002_NOT_RESOLVED", "GATE-002 must be present in resolved decisions.")
    if "REL-000-DEF-001" in open_ids:
        fail(
            "REL000_DEF001_NOT_RESOLVED",
            "REL-000-DEF-001 cannot remain in open decisions.",
        )
    rel000_decision = next(
        (decision for decision in resolved_decisions if decision.get("id") == "REL-000-DEF-001"),
        None,
    )
    expected_decision = {
        "topic": "fin001_release_classification",
        "resolved_on": "2026-07-31",
        "resolved_by": "project_owner",
        "decision": "Move FIN-001 completely from MVP-0 to MVP-1.",
        "fin001_priority": "P0",
        "fin001_depends_on": ["DSP-002", "EXT-001", "RTE-001"],
        "fin001_mvp0_variant": "none",
        "mvp0_p0_inventory_before": 30,
        "mvp0_p0_inventory_after": 29,
        "rel000_depends_on_fin001": False,
        "ext001_depends_on_rel000": True,
        "ext001_authorized": False,
        "rel000_approved": False,
        "mvp0_internal_approved": False,
    }
    if not rel000_decision or any(
        (
            sorted(rel000_decision.get(key) or []) != sorted(value)
            if key == "fin001_depends_on"
            else str(rel000_decision.get(key)) != value
            if key == "resolved_on"
            else rel000_decision.get(key) != value
        )
        for key, value in expected_decision.items()
    ):
        fail(
            "REL000_DEF001_DECISION_INVALID",
            "The resolved FIN-001 release-scope decision is missing or inconsistent.",
        )
    return {
        "resolved_decisions": resolved_decisions,
        "open_decisions": open_decisions,
        "blocking_scope": {
            decision["id"]: decision.get("severity") for decision in open_decisions
        },
        "work_allowed": {
            decision["id"]: decision.get("work_allowed")
            for decision in open_decisions
            if decision.get("work_allowed") is not None
        },
    }


def _semver_tuple(value: str) -> tuple[int, int, int] | None:
    match = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)", value)
    if not match:
        return None
    return tuple(int(part) for part in match.groups())


def _lock_versions(lock_text: str, package: str) -> set[str]:
    pattern = re.compile(rf"^  {re.escape(package)}@([0-9][^:() ]*):", re.MULTILINE)
    return {match.group(1) for match in pattern.finditer(lock_text)}


def validate_dependency_diff(
    repository_root: Path,
    base_main_sha: str,
    mode: str = NORMAL_RELEASE_EVIDENCE,
) -> dict[str, Any]:
    all_changed = sorted(
        {
            path.replace("\\", "/")
            for output in (
                run_git(repository_root, "diff", "--name-only", base_main_sha),
                run_git(repository_root, "ls-files", "--others", "--exclude-standard"),
            )
            for path in output.splitlines()
            if path.strip()
        }
    )
    changed = sorted(path for path in all_changed if path in SECURITY_REMEDIATION_DEPENDENCY_FILES)
    unexpected_dependency_files = sorted(
        path
        for path in all_changed
        if Path(path).name in DEPENDENCY_FILE_NAMES
        and path not in SECURITY_REMEDIATION_DEPENDENCY_FILES
    )
    if unexpected_dependency_files:
        fail(
            "UNAUTHORIZED_DEPENDENCY_FILE_CHANGED",
            "A dependency file outside the REL-000 allowlist changed.",
            files=unexpected_dependency_files,
        )
    if mode == NORMAL_RELEASE_EVIDENCE:
        if changed:
            fail(
                "DEPENDENCY_FILES_CHANGED",
                "Dependency manifests or lockfiles changed in normal release-evidence mode.",
                files=changed,
            )
        return {
            "dependency_manifest_changed": False,
            "dependency_lockfile_changed": False,
            "dependency_workspace_changed": False,
            "dependency_diff_against_base": "CLEAN",
            "changed_dependency_files": [],
            "lockfile_consistency_verified": True,
            "vulnerable_lock_versions": [],
        }
    if mode != SECURITY_REMEDIATION:
        fail("REL000_MODE_INVALID", "The dependency validator received an invalid mode.")
    required = {"apps/web/package.json", "apps/web/pnpm-lock.yaml"}
    if not required.issubset(changed) or not set(changed).issubset(
        SECURITY_REMEDIATION_DEPENDENCY_FILES
    ):
        fail(
            "SECURITY_REMEDIATION_DEPENDENCY_SCOPE_INVALID",
            "Security remediation changed an unexpected dependency scope.",
            files=changed,
        )

    package_path = repository_root / "apps/web/package.json"
    current_package = load_json(package_path)
    try:
        base_package = json.loads(
            run_git(repository_root, "show", f"{base_main_sha}:apps/web/package.json")
        )
    except json.JSONDecodeError as exc:
        fail("BASE_DEPENDENCY_MANIFEST_INVALID", "The base package manifest is invalid.", error=str(exc))
    expected_package = copy.deepcopy(base_package)
    expected_package["dependencies"]["next"] = current_package["dependencies"].get("next")
    expected_package["devDependencies"]["eslint-config-next"] = current_package[
        "devDependencies"
    ].get("eslint-config-next")
    if current_package != expected_package:
        fail(
            "UNAUTHORIZED_DEPENDENCY_MANIFEST_CHANGE",
            "Only Next.js and eslint-config-next may change in the web manifest.",
        )
    next_version = current_package["dependencies"].get("next")
    eslint_next_version = current_package["devDependencies"].get("eslint-config-next")
    if next_version != "16.2.11" or eslint_next_version != next_version:
        fail(
            "SECURITY_REMEDIATION_VERSION_INVALID",
            "Next.js dependencies must use the aligned minimum patched stable version.",
            next=next_version,
            eslint_config_next=eslint_next_version,
        )
    if _semver_tuple(str(next_version)) is None:
        fail("PRERELEASE_DEPENDENCY_REJECTED", "Prerelease dependency versions are forbidden.")

    lock_text = (repository_root / "apps/web/pnpm-lock.yaml").read_text(encoding="utf-8")
    if (
        f"specifier: {next_version}" not in lock_text
        or not re.search(rf"^  next@{re.escape(next_version)}:", lock_text, re.MULTILINE)
        or not re.search(
            rf"^  eslint-config-next@{re.escape(eslint_next_version)}:",
            lock_text,
            re.MULTILINE,
        )
    ):
        fail("LOCKFILE_INCONSISTENT", "The lockfile does not match the dependency manifest.")
    brace_versions = _lock_versions(lock_text, "brace-expansion")
    sharp_versions = _lock_versions(lock_text, "sharp")
    prerelease = sorted(
        version
        for version in brace_versions | sharp_versions
        if _semver_tuple(version) is None
    )
    if prerelease:
        fail(
            "PRERELEASE_DEPENDENCY_REJECTED",
            "Prerelease dependency versions are forbidden in the remediation graph.",
            versions=prerelease,
        )
    vulnerable: list[str] = []
    for version in brace_versions:
        parsed = _semver_tuple(version)
        if parsed is not None and (parsed < (1, 1, 17) or (4, 0, 0) <= parsed < (5, 0, 8)):
            vulnerable.append(f"brace-expansion@{version}")
    for version in sharp_versions:
        parsed = _semver_tuple(version)
        if parsed is not None and parsed < (0, 35, 0):
            vulnerable.append(f"sharp@{version}")

    workspace_text = (repository_root / "apps/web/pnpm-workspace.yaml").read_text(
        encoding="utf-8"
    )
    brace_overrides = {
        (match.group(1), match.group(2))
        for match in re.finditer(
            r'^\s*["\']?(brace-expansion@[^"\']+)["\']?:\s*([^\s#]+)',
            workspace_text,
            re.MULTILINE,
        )
    }
    expected_overrides = {
        ("brace-expansion@<1.1.17", "1.1.17"),
        ("brace-expansion@>=4.0.0 <5.0.8", "5.0.8"),
    }
    if brace_overrides != expected_overrides:
        fail(
            "INCOMPATIBLE_DEPENDENCY_OVERRIDE",
            "Brace-expansion overrides must stay same-major and satisfy every consumer range.",
            overrides=sorted(brace_overrides),
        )
    if brace_versions != {"1.1.17", "5.0.8"}:
        fail(
            "VULNERABLE_VERSION_RETAINED",
            "The lockfile must contain only the patched brace-expansion branches.",
            versions=sorted(brace_versions),
        )
    for path in all_changed:
        if path in SECURITY_REMEDIATION_DEPENDENCY_FILES or not path.startswith("apps/web/"):
            continue
        added = [
            line[1:]
            for line in run_git(repository_root, "diff", "--unified=0", base_main_sha, "--", path).splitlines()
            if line.startswith("+") and not line.startswith("+++")
        ]
        if any(re.search(r"(?:from\s+|require\()['\"]sharp['\"]", line) for line in added):
            fail("DIRECT_SHARP_USAGE_ADDED", "The remediation added direct Sharp usage.", file=path)
        if any("next/image" in line for line in added):
            fail("NEXT_IMAGE_USAGE_ADDED", "The remediation added next/image usage.", file=path)
    return {
        "dependency_manifest_changed": True,
        "dependency_lockfile_changed": True,
        "dependency_workspace_changed": "apps/web/pnpm-workspace.yaml" in changed,
        "dependency_diff_against_base": "AUTHORIZED_SECURITY_REMEDIATION",
        "changed_dependency_files": changed,
        "lockfile_consistency_verified": True,
        "vulnerable_lock_versions": sorted(vulnerable),
        "brace_expansion_versions": sorted(brace_versions),
        "sharp_versions": sorted(sharp_versions),
    }


def validate_audit_snapshot(audit: dict[str, Any], label: str) -> dict[str, Any]:
    if audit.get("command_executed") is not True:
        fail("AUDIT_COMMAND_NOT_EXECUTED", f"The {label} dependency audit was not executed.")
    if audit.get("parse_succeeded") is not True:
        fail("AUDIT_PARSE_FAILED", f"The {label} dependency audit was not parsed.")
    if audit.get("command_exit_code") not in (0, 1):
        fail(
            "AUDIT_COMMAND_FAILED",
            f"The {label} dependency audit returned an unexpected exit code.",
            exit_code=audit.get("command_exit_code"),
        )
    totals = audit.get("totals")
    advisories = audit.get("advisories")
    if not isinstance(totals, dict) or not isinstance(advisories, list):
        fail("AUDIT_RESULT_INVALID", f"The sanitized {label} audit is invalid.")
    normalized_totals: dict[str, int] = {}
    for severity in ("critical", "high", "moderate", "low"):
        value = totals.get(severity)
        if not isinstance(value, int) or isinstance(value, bool) or value < 0:
            fail("AUDIT_RESULT_INVALID", f"The {label} audit severity counts are invalid.")
        normalized_totals[severity] = value
    total = totals.get("total")
    if not isinstance(total, int) or isinstance(total, bool) or total < 0:
        fail("AUDIT_RESULT_INVALID", f"The {label} audit total is invalid.")
    normalized_totals["total"] = total
    if sum(normalized_totals[key] for key in ("critical", "high", "moderate", "low")) != total:
        fail("AUDIT_COUNT_MISMATCH", f"The {label} audit severity counts do not equal total.")
    if len(advisories) != total:
        fail("AUDIT_ADVISORY_OMITTED", f"The {label} audit advisory collection is incomplete.")

    identities: set[tuple[str, str, str]] = set()
    normalized: list[dict[str, Any]] = []
    for advisory in advisories:
        advisory_id = advisory.get("advisory_id")
        package = advisory.get("package")
        severity = advisory.get("severity")
        versions = advisory.get("installed_versions")
        if (
            not isinstance(advisory_id, str)
            or not re.fullmatch(r"GHSA-[0-9a-z-]+", advisory_id, re.IGNORECASE)
            or not isinstance(package, str)
            or not package
            or severity not in {"critical", "high", "moderate", "low"}
            or not isinstance(versions, list)
            or not versions
            or not all(isinstance(version, str) and version for version in versions)
            or not isinstance(advisory.get("affected_range"), str)
            or not advisory.get("affected_range")
            or not isinstance(advisory.get("patched_range"), str)
            or not advisory.get("patched_range")
            or advisory.get("direct_or_transitive") not in {"direct", "transitive"}
            or not isinstance(advisory.get("dependency_path_count"), int)
            or advisory.get("dependency_path_count") < 1
            or not isinstance(advisory.get("fix_available"), bool)
            or advisory.get("fix_compatibility") not in {
                "compatible_patch_available",
                "requires_compatibility_assessment",
                "no_fix_available",
            }
        ):
            fail(
                "AUDIT_ADVISORY_INVALID",
                f"The {label} audit contains an incomplete advisory.",
                advisory_id=advisory_id,
            )
        identity = (advisory_id.lower(), package, severity)
        if identity in identities:
            fail("AUDIT_ADVISORY_DUPLICATED", f"The {label} audit duplicates an advisory.")
        identities.add(identity)
        normalized.append(advisory)
    observed_counts = {
        severity: sum(item["severity"] == severity for item in normalized)
        for severity in ("critical", "high", "moderate", "low")
    }
    if any(observed_counts[key] != normalized_totals[key] for key in observed_counts):
        fail("AUDIT_COUNT_MISMATCH", f"The {label} advisory severities do not match metadata.")
    return {"totals": normalized_totals, "advisories": normalized}


def validate_issue_and_audit(
    issue: dict[str, Any],
    additional_issue: dict[str, Any],
    base_audit: dict[str, Any],
    branch_audit: dict[str, Any],
    dependency_diff: dict[str, Any],
    mode: str = NORMAL_RELEASE_EVIDENCE,
) -> dict[str, Any]:
    if int(issue.get("number", 0)) != 5:
        fail("ISSUE5_OMITTED_OR_CLOSED", "Issue #5 must be consulted.")
    issue5_ids = {str(value).lower() for value in issue.get("tracked_advisory_ids") or []}
    if issue5_ids != {ISSUE5_ADVISORY.lower()}:
        fail("ISSUE5_SCOPE_MISREPRESENTED", "Issue #5 must track only its sharp advisory.")
    issue5_state = str(issue.get("state", "")).upper()
    issue30_state = str(additional_issue.get("state", "")).upper()
    expected_issue30_ids = {value.lower() for value in ISSUE30_ADVISORIES}
    if (
        int(additional_issue.get("number", 0)) != 30
        or issue30_state not in {"OPEN", "CLOSED"}
        or additional_issue.get("title") != ADDITIONAL_SECURITY_ISSUE_TITLE
        or {
            str(value).lower()
            for value in additional_issue.get("tracked_advisory_ids") or []
        }
        != expected_issue30_ids
    ):
        fail(
            "ADDITIONAL_SECURITY_TRACKING_INVALID",
            "Issue #30 must track exactly its ten registered advisories.",
        )
    if issue5_state not in {"OPEN", "CLOSED"}:
        fail("ISSUE5_OMITTED_OR_CLOSED", "Issue #5 has an invalid state.")

    base = validate_audit_snapshot(base_audit, "base")
    branch = validate_audit_snapshot(branch_audit, "branch")
    base_totals = base["totals"]
    branch_totals = branch["totals"]
    if branch_totals["critical"] > 0:
        fail("AUDIT_CRITICAL_PRESENT", "The branch audit contains a critical advisory.")
    base_ids = {item["advisory_id"].lower() for item in base["advisories"]}
    branch_ids = {item["advisory_id"].lower() for item in branch["advisories"]}
    expected_ids = {value.lower() for value in EXPECTED_BASE_ADVISORIES}
    if mode == SECURITY_REMEDIATION:
        for key, expected in EXPECTED_BASE_AUDIT_TOTALS.items():
            if base_totals[key] != expected:
                fail(
                    "BASE_AUDIT_UNEXPECTED",
                    "The real base audit no longer matches the authorized inherited baseline.",
                    severity=key,
                    expected=expected,
                    actual=base_totals[key],
                )
        if base_ids != expected_ids:
            fail("BASE_ADVISORY_SET_UNEXPECTED", "The base advisory set is incomplete or changed.")
        if dependency_diff.get("dependency_diff_against_base") != (
            "AUTHORIZED_SECURITY_REMEDIATION"
        ):
            fail("DEPENDENCY_FILES_CHANGED", "Security remediation dependency evidence is missing.")
    elif mode == NORMAL_RELEASE_EVIDENCE:
        if dependency_diff.get("dependency_diff_against_base") != "CLEAN":
            fail("DEPENDENCY_FILES_CHANGED", "Normal release evidence requires a clean dependency diff.")
    else:
        fail("REL000_MODE_INVALID", "The audit validator received an invalid mode.")

    untracked_base = base_ids - expected_ids
    if untracked_base:
        fail(
            "UNTRACKED_DEPENDENCY_ADVISORY",
            "The base audit contains an advisory not tracked by Issue #5 or Issue #30.",
            advisories=sorted(untracked_base),
        )
    base_by_id = {item["advisory_id"].lower(): item for item in base["advisories"]}
    branch_by_id = {item["advisory_id"].lower(): item for item in branch["advisories"]}
    new_ids = branch_ids - base_ids
    if new_ids:
        fail(
            "NEW_DEPENDENCY_ADVISORY",
            "The branch audit introduced a new advisory or changed an advisory identifier.",
            advisories=sorted(new_ids),
        )
    base_packages = {item["package"] for item in base["advisories"]}
    new_packages = {item["package"] for item in branch["advisories"]} - base_packages
    if new_packages:
        fail(
            "NEW_AFFECTED_DEPENDENCY_PACKAGE",
            "The branch audit introduced a newly affected package.",
            packages=sorted(new_packages),
        )
    for advisory_id in branch_ids:
        previous = base_by_id[advisory_id]
        current = branch_by_id[advisory_id]
        if current["package"] != previous["package"]:
            fail(
                "NEW_AFFECTED_DEPENDENCY_PACKAGE",
                "An advisory moved to a different affected package.",
                advisory=advisory_id,
            )
        if SEVERITY_RANK[current["severity"]] > SEVERITY_RANK[previous["severity"]]:
            fail(
                "DEPENDENCY_ADVISORY_SEVERITY_INCREASED",
                "An advisory severity increased on the branch.",
                advisory=advisory_id,
                base=previous["severity"],
                branch=current["severity"],
            )
    if mode == NORMAL_RELEASE_EVIDENCE:
        def identities(snapshot: dict[str, Any]) -> set[tuple[str, str, str, tuple[str, ...]]]:
            return {
                (
                    item["advisory_id"].lower(),
                    item["package"],
                    item["severity"],
                    tuple(item["installed_versions"]),
                )
                for item in snapshot["advisories"]
            }

        if identities(base) != identities(branch):
            fail(
                "BRANCH_ADVISORY_SET_CHANGED",
                "The branch advisory set changed in normal release-evidence mode.",
            )

    for vulnerable in dependency_diff.get("vulnerable_lock_versions") or []:
        if vulnerable.startswith("sharp@") and ISSUE5_ADVISORY.lower() not in branch_ids:
            fail(
                "VULNERABLE_VERSION_RETAINED",
                "The lockfile retained vulnerable Sharp while the audit claimed remediation.",
            )
        if vulnerable.startswith("brace-expansion@") and (
            "GHSA-mh99-v99m-4gvg".lower() not in branch_ids
        ):
            fail(
                "VULNERABLE_VERSION_RETAINED",
                "The lockfile retained vulnerable brace-expansion while the audit claimed remediation.",
            )

    issue5_present = bool(branch_ids & issue5_ids)
    issue30_present = branch_ids & expected_issue30_ids
    if issue5_present and issue5_state == "CLOSED":
        fail(
            "SECURITY_ISSUE_CLOSED_WHILE_ADVISORY_PRESENT",
            "Issue #5 is closed while its advisory remains present.",
            issue=5,
        )
    if issue30_present and issue30_state == "CLOSED":
        fail(
            "SECURITY_ISSUE_CLOSED_WHILE_ADVISORY_PRESENT",
            "Issue #30 is closed while one or more advisories remain present.",
            issue=30,
        )
    issue5_status = (
        "UNRESOLVED"
        if issue5_present
        else "REMEDIATED_PENDING_MERGE"
        if issue5_state == "OPEN"
        else "REMEDIATED"
    )
    if issue30_present:
        issue30_status = (
            "PARTIALLY_REMEDIATED"
            if issue30_present != expected_issue30_ids
            else "UNRESOLVED"
        )
    else:
        issue30_status = (
            "REMEDIATED_PENDING_MERGE" if issue30_state == "OPEN" else "REMEDIATED"
        )
    removed_ids = base_ids - branch_ids
    if branch_ids:
        dependency_security_status = "BLOCKED"
        release_candidate_status = "BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION"
    elif issue5_state == "OPEN" or issue30_state == "OPEN":
        dependency_security_status = "REMEDIATED_PENDING_MERGE"
        release_candidate_status = (
            "BLOCKED_BY_SECURITY_REMEDIATION_MERGE_AND_OWNER_DECISION"
        )
    else:
        dependency_security_status = "PASSED"
        release_candidate_status = "BLOCKED_BY_OWNER_DECISION"

    tracking_by_id = {
        advisory_id: (
            "Issue #5"
            if advisory_id == ISSUE5_ADVISORY.lower()
            else f"Issue #{additional_issue['number']}"
        )
        for advisory_id in base_ids
    }
    redacted_advisories = [
        {
            "advisory_id": item["advisory_id"],
            "package": item["package"],
            "severity": item["severity"],
            "affected_range": item["affected_range"],
            "patched_range": item["patched_range"],
            "direct_or_transitive": item["direct_or_transitive"],
            "installed_versions": item["installed_versions"],
            "dependency_path_count": item["dependency_path_count"],
            "fix_available": item["fix_available"],
            "fix_compatibility": item["fix_compatibility"],
            "tracking_issue": tracking_by_id[item["advisory_id"].lower()],
        }
        for item in branch["advisories"]
    ]
    return {
        "mode": mode,
        "known_security_issues": [
            {
                "id": "Issue #5",
                "state": issue5_state,
                "title": issue.get("title"),
                "url": issue.get("url"),
                "tracked_advisories": 1,
                "remediation_status": issue5_status,
            },
            {
                "id": f"Issue #{additional_issue['number']}",
                "state": issue30_state,
                "title": additional_issue.get("title"),
                "url": additional_issue.get("url"),
                "tracked_advisories": 10,
                "remediation_status": issue30_status,
            },
        ],
        "dependency_advisories": redacted_advisories,
        "remediated_advisories": [
            {
                "advisory_id": base_by_id[advisory_id]["advisory_id"],
                "package": base_by_id[advisory_id]["package"],
                "severity": base_by_id[advisory_id]["severity"],
                "tracking_issue": tracking_by_id[advisory_id],
                "remediation_status": "REMEDIATED_PENDING_MERGE"
                if (
                    tracking_by_id[advisory_id] == "Issue #5" and issue5_state == "OPEN"
                )
                or (
                    tracking_by_id[advisory_id] == f"Issue #{additional_issue['number']}"
                    and issue30_state == "OPEN"
                )
                else "REMEDIATED",
            }
            for advisory_id in sorted(removed_ids)
        ],
        "removed_advisory_ids": sorted(base_by_id[value]["advisory_id"] for value in removed_ids),
        "remaining_advisory_ids": sorted(
            branch_by_id[value]["advisory_id"] for value in branch_ids
        ),
        "base_totals": base_totals,
        "branch_totals": branch_totals,
        "deltas": {
            key: branch_totals[key] - base_totals[key]
            for key in ("total", "critical", "high", "moderate", "low")
        },
        "additional_issue": {
            "number": additional_issue["number"],
            "url": additional_issue["url"],
        },
        "issue_5_remediation_status": issue5_status,
        "issue_30_remediation_status": issue30_status,
        "sharp_remediation_status": (
            "BLOCKED_BY_UPSTREAM_COMPATIBILITY" if issue5_present else issue5_status
        ),
        "brace_expansion_remediation_status": (
            "BLOCKED_BY_UPSTREAM_DEPENDENCY_GRAPH"
            if "GHSA-mh99-v99m-4gvg".lower() in branch_ids
            else "REMEDIATED"
        ),
        "dependency_security_status": dependency_security_status,
        "release_candidate_status": release_candidate_status,
        "dependency_diff": dependency_diff,
    }


def assert_redacted(value: Any) -> None:
    text = json.dumps(value, ensure_ascii=False)
    lowered_keys: list[str] = []

    def visit(node: Any) -> None:
        if isinstance(node, dict):
            for key, child in node.items():
                lowered_keys.append(str(key).lower())
                visit(child)
        elif isinstance(node, list):
            for child in node:
                visit(child)

    visit(value)
    forbidden = sorted(set(lowered_keys) & FORBIDDEN_REDACTED_KEYS)
    if forbidden:
        fail("REDACTION_FORBIDDEN_FIELD", "A report contains a forbidden field.", fields=forbidden)
    for pattern in FORBIDDEN_TEXT_PATTERNS:
        if pattern.search(text):
            fail("REDACTION_FORBIDDEN_VALUE", "A report contains secret or identifier-shaped data.")


def validate_focused_test_results(
    python_results: dict[str, Any],
    physical_results: dict[str, Any],
) -> dict[str, int]:
    python_fields = (
        "python_tests_expected",
        "python_tests_discovered",
        "python_tests_executed",
        "python_tests_passed",
        "python_tests_failed",
        "python_tests_skipped",
    )
    physical_fields = (
        "physical_tests_expected",
        "physical_tests_discovered",
        "physical_tests_executed",
        "physical_tests_passed",
        "physical_tests_failed",
        "physical_tests_skipped",
    )
    if any(field not in python_results for field in python_fields) or any(
        field not in physical_results for field in physical_fields
    ):
        fail("REL000_FOCUSED_TEST_RESULT_INCOMPLETE", "Focused test results are incomplete.")
    values = {
        **{field: int(python_results[field]) for field in python_fields},
        **{field: int(physical_results[field]) for field in physical_fields},
    }
    if (
        values["python_tests_expected"] != values["python_tests_discovered"]
        or values["python_tests_discovered"] != values["python_tests_executed"]
        or values["python_tests_executed"] != values["python_tests_passed"]
        or values["python_tests_failed"] != 0
        or values["python_tests_skipped"] != 0
        or values["physical_tests_expected"] != values["physical_tests_discovered"]
        or values["physical_tests_discovered"] != values["physical_tests_executed"]
        or values["physical_tests_executed"] != values["physical_tests_passed"]
        or values["physical_tests_failed"] != 0
        or values["physical_tests_skipped"] != 0
    ):
        fail("REL000_FOCUSED_TESTS_FAILED", "Focused test counts do not prove a complete pass.")
    return {
        "focused_tests_expected": (
            values["python_tests_expected"] + values["physical_tests_expected"]
        ),
        "focused_tests_discovered": (
            values["python_tests_discovered"] + values["physical_tests_discovered"]
        ),
        "focused_tests_executed": (
            values["python_tests_executed"] + values["physical_tests_executed"]
        ),
        "focused_tests_passed": (
            values["python_tests_passed"] + values["physical_tests_passed"]
        ),
        "focused_tests_failed": (
            values["python_tests_failed"] + values["physical_tests_failed"]
        ),
        "focused_tests_skipped": (
            values["python_tests_skipped"] + values["physical_tests_skipped"]
        ),
    }


def release_report(
    trace: dict[str, str],
    workflow_run_id: str,
    workflow_run_attempt: str,
    normative_evidence: dict[str, Any],
    p0: dict[str, Any],
    cross_tenant: dict[str, Any],
    ops001: dict[str, Any],
    ops002: dict[str, Any],
    rollback: dict[str, Any],
    decisions: dict[str, Any],
    security: dict[str, Any],
    artifacts: dict[str, dict[str, Any]],
    focused_tests: dict[str, Any],
) -> dict[str, Any]:
    base_totals = security["base_totals"]
    branch_totals = security["branch_totals"]
    deltas = security["deltas"]
    result = {
        "format_version": FORMAT_VERSION,
        "release": "MVP-0_INTERNAL",
        **trace,
        "workflow_run_id": str(workflow_run_id),
        "workflow_run_attempt": str(workflow_run_attempt),
        "generated_at_utc": dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
        **{
            key: focused_tests[key]
            for key in (
                "focused_tests_expected",
                "focused_tests_discovered",
                "focused_tests_executed",
                "focused_tests_passed",
                "focused_tests_failed",
                "focused_tests_skipped",
            )
        },
        **{
            key: normative_evidence[key]
            for key in (
                "normative_version",
                "normative_checksum_valid",
                "normative_files_modified",
            )
        },
        **{
            key: p0[key]
            for key in (
                "mvp0_p0_items_expected",
                "mvp0_p0_items_evaluated",
                "mvp0_p0_items_verified",
                "mvp0_p0_items_partial",
                "mvp0_p0_items_not_started",
                "mvp0_p0_items_blocked",
                "mvp0_p0_items_missing",
                "mvp0_p0_items_duplicated",
                "mvp0_p0_items_unknown",
            )
        },
        **{
            key: cross_tenant[key]
            for key in (
                "cross_tenant_sources_expected",
                "cross_tenant_sources_executed",
                "cross_tenant_sources_passed",
                "cross_tenant_sources_missing",
                "cross_tenant_sources_failed",
                "cross_tenant_sources_skipped",
                "cross_tenant_incidents_observed",
            )
        },
        "ops001_evidence_valid": True,
        "ops001_orders_planned": ops001["orders_planned"],
        "ops001_orders_delivered": ops001["orders_delivered"],
        "ops001_realtime_expected": ops001["realtime_events_expected"],
        "ops001_realtime_matched": ops001["realtime_events_matched"],
        "ops001_realtime_missing": ops001["realtime_events_missing"],
        "ops001_audits_expected": ops001["audits_expected"],
        "ops001_audits_exactly_matched": ops001["audits_exactly_matched"],
        "ops001_audits_missing": ops001["audits_missing"],
        "ops001_secondary_tenant_rows": ops001["secondary_tenant_rows"],
        "ops002_evidence_valid": True,
        "ops002_restore_drill_passed": ops002["result"] == "RESTORE_DRILL_PASSED",
        "ops002_negative_tests_expected": 70,
        "ops002_negative_tests_passed": 70,
        "ops002_observed_data_loss": ops002["observed_data_loss"],
        "ops002_guaranteed_rpo": ops002["current_guaranteed_rpo"],
        "ops002_plaintext_residue_detected": ops002["plaintext_residue_detected"],
        **{
            key: rollback[key]
            for key in (
                "rollback_items_expected",
                "rollback_items_verified",
                "rollback_items_missing",
                "rollback_items_unknown",
                "rollback_items_duplicated",
                "rollback_test_passed",
                "rollback_scenarios_expected",
                "rollback_scenarios_executed",
                "rollback_scenarios_passed",
                "rollback_scenarios_failed",
                "rollback_cleanup_verified",
            )
        },
        "known_security_issues": security["known_security_issues"],
        "rel000_mode": security["mode"],
        "dependency_audit_command_executed": True,
        "dependency_audit_parse_succeeded": True,
        "tracked_issue_5_advisories": 1,
        "tracked_issue_5_high": 1,
        "tracked_issue_5_moderate": 0,
        "tracked_issue_5_packages": ["sharp"],
        **{f"base_audit_{key}": base_totals[key] for key in (
            "total", "critical", "high", "moderate", "low"
        )},
        **{f"branch_audit_{key}": branch_totals[key] for key in (
            "total", "critical", "high", "moderate", "low"
        )},
        **{f"dependency_audit_delta_{key}": deltas[key] for key in (
            "total", "critical", "high", "moderate", "low"
        )},
        "dependency_manifest_changed": security["dependency_diff"][
            "dependency_manifest_changed"
        ],
        "dependency_lockfile_changed": security["dependency_diff"][
            "dependency_lockfile_changed"
        ],
        "dependency_workspace_changed": security["dependency_diff"][
            "dependency_workspace_changed"
        ],
        "dependency_diff_against_base": security["dependency_diff"][
            "dependency_diff_against_base"
        ],
        "changed_dependency_files": security["dependency_diff"][
            "changed_dependency_files"
        ],
        "lockfile_consistency_verified": security["dependency_diff"][
            "lockfile_consistency_verified"
        ],
        "audit_tracking_gap_detected": False,
        "audit_tracking_gap_count": 0,
        "dependency_advisories": security["dependency_advisories"],
        "remediated_advisories": security["remediated_advisories"],
        "removed_advisory_ids": security["removed_advisory_ids"],
        "remaining_advisory_ids": security["remaining_advisory_ids"],
        "dependency_security_status": security["dependency_security_status"],
        "issue_5_remediation_status": security["issue_5_remediation_status"],
        "issue_30_remediation_status": security["issue_30_remediation_status"],
        "sharp_remediation_status": security["sharp_remediation_status"],
        "brace_expansion_remediation_status": security[
            "brace_expansion_remediation_status"
        ],
        "additional_security_tracking_status": security["known_security_issues"][1][
            "state"
        ],
        "additional_security_tracking_issue": security["additional_issue"],
        "physical_path_tests_local": "NOT_EXECUTED",
        "physical_path_tests_local_reason": "POWERSHELL_7_UNAVAILABLE",
        "physical_path_tests_ci": "PASSED",
        "powershell_ci_major_version": 7,
        "resolved_decisions": [decision["id"] for decision in decisions["resolved_decisions"]],
        "open_decisions": [decision["id"] for decision in decisions["open_decisions"]]
        + [
            issue["id"]
            for issue in security["known_security_issues"]
            if issue["state"] == "OPEN"
        ],
        "blocking_scope": decisions["blocking_scope"],
        "work_allowed": decisions["work_allowed"],
        "normative_blockers": [],
        "artifact_sources": artifacts,
        "owner_approval_status": "PENDING",
        "rel000_def_001_status": "RESOLVED",
        "normative_scope_status": "RESOLVED",
        "normative_scope_decision": "FIN001_MOVED_TO_MVP1",
        "technical_evidence_status": "PASSED",
        "release_candidate_status": security["release_candidate_status"],
        "technical_gate_outcome": "EVIDENCE_COMPLETE_RELEASE_BLOCKED",
        "result": "REL000_EVIDENCE_GENERATED",
    }
    return result


def validate_owner_state(report: dict[str, Any]) -> None:
    if report.get("owner_approval_status") != "PENDING":
        fail("OWNER_APPROVAL_FALSELY_ASSERTED", "Owner approval must remain PENDING.")
    expected_release_status = {
        "BLOCKED": "BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION",
        "REMEDIATED_PENDING_MERGE": (
            "BLOCKED_BY_SECURITY_REMEDIATION_MERGE_AND_OWNER_DECISION"
        ),
        "PASSED": "BLOCKED_BY_OWNER_DECISION",
    }.get(report.get("dependency_security_status"))
    if expected_release_status is None:
        fail("DEPENDENCY_SECURITY_STATUS_INVALID", "Dependency security has an invalid state.")
    if report.get("release_candidate_status") != expected_release_status:
        fail("RELEASE_CANDIDATE_NOT_BLOCKED", "The release candidate must remain blocked.")
    if (
        "mvp0_p0_items_verified" in report
        or "mvp0_p0_items_blocked" in report
    ) and (
        report.get("mvp0_p0_items_verified") != 28
        or report.get("mvp0_p0_items_blocked") != 1
    ):
        fail("REL000_FALSELY_VERIFIED", "REL-000 must remain the single blocked MVP-0 item.")
    if (
        report.get("rel000_def_001_status") != "RESOLVED"
        or report.get("normative_scope_status") != "RESOLVED"
        or report.get("normative_scope_decision") != "FIN001_MOVED_TO_MVP1"
    ):
        fail(
            "REL000_DEF001_RESOLUTION_INVALID",
            "The approved FIN-001 normative resolution must remain explicit.",
        )
    if report.get("audit_tracking_gap_detected") is not False or report.get(
        "audit_tracking_gap_count"
    ) != 0:
        fail("AUDIT_TRACKING_GAP_INVALID", "Every advisory must be mapped to its owning issue.")
    if report.get("result") == "REL000_EVIDENCE_GENERATED" and report.get(
        "technical_evidence_status"
    ) != "PASSED":
        fail(
            "SUCCESS_WITH_TECHNICAL_FAILURE",
            "REL000_EVIDENCE_GENERATED cannot be emitted with a technical failure.",
        )
    serialized = json.dumps(report, ensure_ascii=False)
    for word in FORBIDDEN_RESULT_WORDS:
        if re.search(rf'"(?:owner_approval_status|release_candidate_status|result)"\s*:\s*"{word}"', serialized):
            fail("FORBIDDEN_RELEASE_STATUS", "The report asserts a forbidden release status.", status=word)


def validate_extension_not_started(repository_root: Path, raw_item_evidence: dict[str, Any]) -> None:
    if raw_item_evidence.get("ext001_started") is True:
        fail("EXT001_STARTED", "EXT-001 must not start before owner approval.")
    tracked = run_git(repository_root, "ls-files").splitlines()
    forbidden = [
        path
        for path in tracked
        if re.search(r"(^|[/_-])ext-?001([/_.-]|$)", path, re.IGNORECASE)
        and path != "docs/normative/v0.6/specs/AI-08_BACKLOG.yaml"
    ]
    if forbidden:
        fail("EXT001_STARTED", "Tracked implementation evidence indicates EXT-001 started.", files=forbidden)


def assert_output_is_fresh(output_directory: Path) -> None:
    if output_directory.exists():
        fail(
            "STALE_REPORT_REUSED",
            "The output directory must not exist before generation.",
        )


def assert_generation_not_cancelled() -> None:
    if (
        os.environ.get("REL000_TEST_MODE") == "true"
        and os.environ.get("REL000_TEST_CANCEL_BEFORE_PUBLISH") == "true"
    ):
        fail("GENERATION_CANCELLED", "Generation was cancelled before publication.")


def generate(args: argparse.Namespace) -> dict[str, Any]:
    repository_root = args.repository_root.resolve()
    assert_output_is_fresh(args.output_directory)
    trace = validate_traceability(
        repository_root,
        args.source_head_sha,
        args.tested_git_sha,
        args.base_main_sha,
        args.git_relationship,
    )
    policy = load_remediation_policy(args.remediation_policy)
    validate_mode_authorization(
        args.mode,
        policy,
        args.source_branch,
        trace["base_main_sha"],
    )
    normative = load_normative(repository_root)
    normative_evidence = validate_normative_checksums(normative["root"])
    selected, all_items = normative_items(normative)
    item_input = load_json(args.item_evidence)
    validate_extension_not_started(repository_root, item_input)
    job_results = json.loads(args.job_results_json)
    validate_jobs(job_results)
    execution_results, execution_artifacts = load_execution_evidence(
        args.execution_results_directory,
        args.execution_artifacts,
        trace,
        args.workflow_run_id,
        args.workflow_run_attempt,
    )
    p0 = validate_item_evidence(
        repository_root,
        selected,
        all_items,
        item_input,
        trace["base_main_sha"],
        job_results,
        execution_results,
    )
    cross_tenant = validate_cross_tenant(
        repository_root,
        load_json(args.cross_tenant_evidence),
        job_results,
        execution_results,
    )
    rollback = validate_rollback(
        repository_root,
        load_json(args.rollback_evidence),
        selected,
        load_json(args.rollback_execution),
    )
    focused_tests = validate_focused_test_results(
        load_json(args.python_test_results),
        load_json(args.physical_test_results),
    )
    decisions = validate_decisions(normative["gates"])
    issue = load_json(args.issue5)
    additional_issue = load_json(args.security_tracking_issue)
    base_audit = load_json(args.base_audit)
    branch_audit = load_json(args.branch_audit)
    dependency_diff = validate_dependency_diff(
        repository_root,
        trace["base_main_sha"],
        args.mode,
    )
    security = validate_issue_and_audit(
        issue,
        additional_issue,
        base_audit,
        branch_audit,
        dependency_diff,
        args.mode,
    )

    ops001_artifact = validate_artifact(
        args.ops001_directory,
        args.ops001_artifact_name,
        args.ops001_artifact_id,
        args.ops001_artifact_digest,
        trace,
        args.workflow_run_id,
        args.workflow_run_attempt,
        lambda path: path.endswith(".json") or path.endswith(".trx"),
    )
    ops002_artifact = validate_artifact(
        args.ops002_directory,
        args.ops002_artifact_name,
        args.ops002_artifact_id,
        args.ops002_artifact_digest,
        trace,
        args.workflow_run_id,
        args.workflow_run_attempt,
        lambda path: path.endswith(".json") or path.endswith(".tar.gz.age"),
    )
    ops001 = validate_ops001(args.ops001_directory)
    ops002, _ = validate_ops002(args.ops002_directory)
    artifacts = {
        "ops001": ops001_artifact,
        "ops002": ops002_artifact,
        "execution": execution_artifacts,
    }
    report = release_report(
        trace,
        args.workflow_run_id,
        args.workflow_run_attempt,
        normative_evidence,
        p0,
        cross_tenant,
        ops001,
        ops002,
        rollback,
        decisions,
        security,
        artifacts,
        focused_tests,
    )
    validate_owner_state(report)
    for document in (p0, cross_tenant, rollback, report):
        assert_redacted(document)

    assert_generation_not_cancelled()
    args.output_directory.mkdir(parents=False, exist_ok=False)
    write_json(args.output_directory / "rel000-p0-evidence.json", p0)
    write_json(args.output_directory / "rel000-cross-tenant-evidence.json", cross_tenant)
    write_json(args.output_directory / "rel000-rollback-evidence.json", rollback)
    write_json(args.output_directory / "rel000-internal-release-report.json", report)
    actual_files = {path.name for path in args.output_directory.iterdir() if path.is_file()}
    if actual_files != OUTPUT_FILES:
        fail("OUTPUT_ALLOWLIST_VIOLATION", "REL-000 output differs from the publication allowlist.")
    return report


def provenance_command(args: argparse.Namespace) -> None:
    artifact_directory = args.artifact_directory.resolve()
    if not artifact_directory.is_dir():
        fail("ARTIFACT_MISSING", "The provenance source directory does not exist.")
    create_provenance(
        artifact_directory,
        args.artifact_name,
        args.workflow_run_id,
        args.workflow_run_attempt,
        validate_sha(args.source_head_sha, "SOURCE_HEAD_SHA_MALFORMED", "source_head_sha"),
        validate_sha(args.tested_git_sha, "TESTED_GIT_SHA_MALFORMED", "tested_git_sha"),
        validate_sha(args.base_main_sha, "BASE_MAIN_SHA_MALFORMED", "base_main_sha"),
        args.git_relationship,
    )


def sanitize_audit(
    input_path: Path,
    output_path: Path,
    command_executed: bool,
    command_exit_code: int,
) -> None:
    raw = load_json(input_path)
    if not isinstance(raw, dict):
        fail("AUDIT_PARSE_FAILED", "The dependency audit JSON root must be an object.")
    advisories: list[dict[str, Any]] = []
    legacy = raw.get("advisories") if isinstance(raw, dict) else None
    if isinstance(legacy, dict):
        for advisory in legacy.values():
            findings = advisory.get("findings") or []
            versions = sorted(
                {
                    str(finding.get("version"))
                    for finding in findings
                    if finding.get("version")
                }
            )
            dependency_paths: list[str] = []
            for finding in findings:
                raw_paths = finding.get("paths") or []
                if isinstance(raw_paths, str):
                    dependency_paths.extend(path for path in raw_paths.split() if path)
                elif isinstance(raw_paths, list):
                    dependency_paths.extend(
                        str(path) for path in raw_paths if isinstance(path, str) and path
                    )
            package = str(advisory.get("module_name") or "")
            is_direct = any(path in {f".>{package}", package} for path in dependency_paths)
            patched_range = str(advisory.get("patched_versions") or "")
            advisories.append(
                {
                    "advisory_id": str(
                        advisory.get("github_advisory_id") or advisory.get("id") or ""
                    ),
                    "package": package,
                    "installed_versions": versions,
                    "severity": str(advisory.get("severity") or "").lower(),
                    "affected_range": str(advisory.get("vulnerable_versions") or ""),
                    "patched_range": patched_range,
                    "direct_or_transitive": "direct" if is_direct else "transitive",
                    "dependency_path_count": len(dependency_paths),
                    "fix_available": bool(patched_range and patched_range != "<0.0.0"),
                    "fix_compatibility": (
                        "compatible_patch_available"
                        if package == "next" and patched_range == ">=16.2.11"
                        else "requires_compatibility_assessment"
                    ),
                }
            )
    modern = raw.get("vulnerabilities") if isinstance(raw, dict) else None
    if isinstance(modern, dict):
        for package, vulnerability in modern.items():
            via = vulnerability.get("via") or []
            advisory_ids = {
                match.group(0)
                for entry in via
                if isinstance(entry, dict)
                for text in (str(entry.get("url") or ""), str(entry.get("title") or ""))
                for match in re.finditer(r"GHSA-[0-9a-z-]+", text, re.IGNORECASE)
            }
            versions = {
                match.group(1)
                for node in vulnerability.get("nodes") or []
                for match in [
                    re.search(
                        rf"(?:^|[/\\]){re.escape(package)}@([0-9][^/\\]*)(?:[/\\]|$)",
                        str(node),
                    )
                ]
                if match
            }
            for advisory_id in sorted(advisory_ids):
                advisories.append(
                    {
                        "advisory_id": advisory_id,
                        "package": package,
                        "installed_versions": sorted(versions),
                        "severity": str(vulnerability.get("severity") or "").lower(),
                        "affected_range": str(vulnerability.get("range") or ""),
                        "patched_range": str(vulnerability.get("fixAvailable") or ""),
                        "direct_or_transitive": "transitive",
                        "dependency_path_count": len(vulnerability.get("nodes") or []),
                        "fix_available": bool(vulnerability.get("fixAvailable")),
                        "fix_compatibility": (
                            "requires_compatibility_assessment"
                            if vulnerability.get("fixAvailable")
                            else "no_fix_available"
                        ),
                    }
                )
    unique: dict[tuple[str, str, str], dict[str, Any]] = {}
    for advisory in advisories:
        key = (
            str(advisory.get("advisory_id")),
            str(advisory.get("package")),
            str(advisory.get("severity")),
        )
        existing = unique.get(key)
        if existing is None:
            unique[key] = copy.deepcopy(advisory)
            continue
        existing["installed_versions"] = sorted(
            set(existing["installed_versions"]) | set(advisory["installed_versions"])
        )
        existing["affected_range"] = " || ".join(
            sorted(set(existing["affected_range"].split(" || ")) | {advisory["affected_range"]})
        )
        existing["patched_range"] = " || ".join(
            sorted(set(existing["patched_range"].split(" || ")) | {advisory["patched_range"]})
        )
        existing["direct_or_transitive"] = (
            "direct"
            if "direct" in {
                existing["direct_or_transitive"],
                advisory["direct_or_transitive"],
            }
            else "transitive"
        )
        existing["dependency_path_count"] += advisory["dependency_path_count"]
        existing["fix_available"] = bool(
            existing["fix_available"] and advisory["fix_available"]
        )
        if "requires_compatibility_assessment" in {
            existing["fix_compatibility"],
            advisory["fix_compatibility"],
        }:
            existing["fix_compatibility"] = "requires_compatibility_assessment"
    totals = {
        severity: sum(item["severity"] == severity for item in unique.values())
        for severity in ("critical", "high", "moderate", "low")
    }
    totals["total"] = sum(totals.values())
    sanitized = {
        "format_version": "paquetenvia-rel000-audit-v1",
        "command_executed": command_executed,
        "command_exit_code": command_exit_code,
        "parse_succeeded": True,
        "totals": totals,
        "affected_packages": sorted(
            {str(advisory.get("package")) for advisory in unique.values()}
        ),
        "advisories": sorted(
            unique.values(),
            key=lambda item: (
                str(item.get("advisory_id")),
                str(item.get("package")),
            ),
        ),
    }
    validate_audit_snapshot(sanitized, "sanitized")
    write_json(output_path, sanitized)


def sanitize_issue(input_path: Path, output_path: Path) -> None:
    raw = load_json(input_path)
    html_url = raw.get("html_url") or raw.get("url")
    if not isinstance(html_url, str) or not html_url.startswith("https://github.com/"):
        fail("ISSUE5_SOURCE_INVALID", "The GitHub issue response has an invalid URL.")
    body = str(raw.get("body") or "")
    title = str(raw.get("title") or "")
    advisory_source = body
    if title == ADDITIONAL_SECURITY_ISSUE_TITLE:
        match = re.search(
            r"## Findings to remediate\s+(.*?)(?=\n## |\Z)",
            body,
            re.DOTALL,
        )
        if not match:
            fail(
                "SECURITY_ISSUE_SOURCE_INVALID",
                "The additional issue does not contain its findings section.",
            )
        advisory_source = match.group(1)
    sanitized = {
        "number": raw.get("number"),
        "state": str(raw.get("state", "")).upper(),
        "title": title,
        "url": html_url,
        "updated_at": raw.get("updated_at"),
        "tracked_advisory_ids": sorted(
            {
                match.group(0).upper()
                for match in re.finditer(
                    r"GHSA-[0-9a-z-]+",
                    advisory_source,
                    re.IGNORECASE,
                )
            }
        ),
    }
    write_json(output_path, sanitized)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    provenance = subparsers.add_parser("provenance")
    provenance.add_argument("--artifact-directory", type=Path, required=True)
    provenance.add_argument("--artifact-name", required=True)
    provenance.add_argument("--workflow-run-id", required=True)
    provenance.add_argument("--workflow-run-attempt", required=True)
    provenance.add_argument("--source-head-sha", required=True)
    provenance.add_argument("--tested-git-sha", required=True)
    provenance.add_argument("--base-main-sha", required=True)
    provenance.add_argument("--git-relationship", required=True)

    audit = subparsers.add_parser("sanitize-audit")
    audit.add_argument("--input", type=Path, required=True)
    audit.add_argument("--output", type=Path, required=True)
    audit.add_argument("--command-executed", action="store_true", required=True)
    audit.add_argument("--command-exit-code", type=int, required=True)

    issue = subparsers.add_parser("sanitize-issue")
    issue.add_argument("--input", type=Path, required=True)
    issue.add_argument("--output", type=Path, required=True)

    resolve_mode = subparsers.add_parser("resolve-mode")
    resolve_mode.add_argument("--policy", type=Path, required=True)
    resolve_mode.add_argument("--source-branch", required=True)

    collect = subparsers.add_parser("collect-results")
    collect.add_argument("--job", choices=sorted(EXECUTION_EVIDENCE_JOBS), required=True)
    collect.add_argument("--artifact-name", required=True)
    collect.add_argument("--workflow-run-id", required=True)
    collect.add_argument("--workflow-run-attempt", required=True)
    collect.add_argument("--source-head-sha", required=True)
    collect.add_argument("--tested-git-sha", required=True)
    collect.add_argument("--base-main-sha", required=True)
    collect.add_argument("--trx", action="append")
    collect.add_argument("--junit", action="append")
    collect.add_argument("--structured", action="append")
    collect.add_argument("--output", type=Path, required=True)

    execution_artifacts = subparsers.add_parser("sanitize-execution-artifacts")
    execution_artifacts.add_argument("--input", type=Path, required=True)
    execution_artifacts.add_argument("--output", type=Path, required=True)
    execution_artifacts.add_argument("--workflow-run-id", required=True)

    artifact_outputs = subparsers.add_parser("materialize-execution-artifacts")
    artifact_outputs.add_argument("--artifacts-json", required=True)
    artifact_outputs.add_argument("--output", type=Path, required=True)
    artifact_outputs.add_argument("--workflow-run-id", required=True)

    synthetic = subparsers.add_parser("synthetic-generate")
    synthetic.add_argument(
        "--scenario",
        choices=(
            "success",
            "cancel-after-first-json",
            "artifact-other-sha",
            "artifact-other-run",
            "manifest-incomplete",
        ),
        required=True,
    )
    synthetic.add_argument("--output", type=Path, required=True)

    validate = subparsers.add_parser("validate")
    validate.add_argument("--repository-root", type=Path, required=True)
    validate.add_argument("--item-evidence", type=Path, required=True)
    validate.add_argument("--cross-tenant-evidence", type=Path, required=True)
    validate.add_argument("--rollback-evidence", type=Path, required=True)
    validate.add_argument("--rollback-execution", type=Path, required=True)
    validate.add_argument("--python-test-results", type=Path, required=True)
    validate.add_argument("--physical-test-results", type=Path, required=True)
    validate.add_argument("--execution-results-directory", type=Path, required=True)
    validate.add_argument("--execution-artifacts", type=Path, required=True)
    validate.add_argument("--ops001-directory", type=Path, required=True)
    validate.add_argument("--ops002-directory", type=Path, required=True)
    validate.add_argument("--output-directory", type=Path, required=True)
    validate.add_argument("--issue5", type=Path, required=True)
    validate.add_argument("--security-tracking-issue", type=Path, required=True)
    validate.add_argument("--base-audit", type=Path, required=True)
    validate.add_argument("--branch-audit", type=Path, required=True)
    validate.add_argument("--mode", choices=sorted(REL000_MODES), required=True)
    validate.add_argument("--source-branch", required=True)
    validate.add_argument("--remediation-policy", type=Path, required=True)
    validate.add_argument("--source-head-sha", required=True)
    validate.add_argument("--tested-git-sha", required=True)
    validate.add_argument("--base-main-sha", required=True)
    validate.add_argument("--git-relationship", required=True)
    validate.add_argument("--workflow-run-id", required=True)
    validate.add_argument("--workflow-run-attempt", required=True)
    validate.add_argument("--job-results-json", required=True)
    validate.add_argument("--ops001-artifact-name", required=True)
    validate.add_argument("--ops001-artifact-id", required=True)
    validate.add_argument("--ops001-artifact-digest", required=True)
    validate.add_argument("--ops002-artifact-name", required=True)
    validate.add_argument("--ops002-artifact-id", required=True)
    validate.add_argument("--ops002-artifact-digest", required=True)
    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    try:
        if args.command == "provenance":
            provenance_command(args)
            return 0
        if args.command == "sanitize-audit":
            sanitize_audit(
                args.input,
                args.output,
                args.command_executed,
                args.command_exit_code,
            )
            return 0
        if args.command == "sanitize-issue":
            sanitize_issue(args.input, args.output)
            return 0
        if args.command == "resolve-mode":
            print(resolve_rel000_mode(load_remediation_policy(args.policy), args.source_branch))
            return 0
        if args.command == "collect-results":
            collect_execution_results(args)
            return 0
        if args.command == "sanitize-execution-artifacts":
            sanitize_execution_artifacts(
                args.input,
                args.output,
                args.workflow_run_id,
            )
            return 0
        if args.command == "materialize-execution-artifacts":
            materialize_execution_artifacts(
                args.artifacts_json,
                args.output,
                args.workflow_run_id,
            )
            return 0
        if args.command == "synthetic-generate":
            synthetic_generation(args)
            return 0
        report = generate(args)
        print(
            json.dumps(
                {
                    "result": report["result"],
                    "technical_gate_outcome": report["technical_gate_outcome"],
                    "owner_approval_status": report["owner_approval_status"],
                },
                sort_keys=True,
            )
        )
        return 0
    except ValidationFailure as exc:
        print(json.dumps(exc.as_dict(), ensure_ascii=False, sort_keys=True), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
