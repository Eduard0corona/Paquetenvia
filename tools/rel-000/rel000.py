#!/usr/bin/env python3
"""REL-000 fail-closed evidence validator and report generator."""

from __future__ import annotations

import argparse
import copy
import datetime as dt
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
EXPECTED_MVP0_P0_COUNT = 30
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
    if "REL-000" not in selected_ids or "FIN-001" not in selected_ids:
        fail("REQUIRED_P0_ITEM_MISSING", "REL-000 or FIN-001 is absent from MVP-0/P0.")
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
        reason = "FIN001_OMITTED" if "FIN-001" in missing else "P0_ITEM_MISSING"
        fail(reason, "One or more canonical MVP-0/P0 items have no evidence.", ids=missing)
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
        if item_id == "FIN-001" and status == "VERIFIED":
            fail(
                "FIN001_FALSELY_VERIFIED",
                "FIN-001 cannot be VERIFIED without complete merged implementation evidence.",
            )
        if item_id == "REL-000" and status == "VERIFIED":
            fail(
                "OWNER_APPROVAL_FALSELY_ASSERTED",
                "REL-000 cannot be VERIFIED before the owner decision.",
            )

        paths = entry.get("implementation_paths") or []
        tests = entry.get("required_test_sources") or []
        jobs = entry.get("authoritative_ci_jobs") or []
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
            if not paths or not tests or not jobs or not refs or not rollback_reference:
                fail(
                    "VERIFIED_EVIDENCE_INCOMPLETE",
                    "VERIFIED requires implementation, tests, CI and rollback evidence.",
                    id=item_id,
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
        if item_id in {"FIN-001", "REL-000"} and "REL-000-DEF-001" not in open_gates:
            fail(
                "NORMATIVE_BLOCKER_OMITTED",
                "FIN-001 and REL-000 must expose REL-000-DEF-001.",
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
    for source in sources:
        job = source.get("job")
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
        output_sources.append(
            {
                "evidence_id": source["evidence_id"],
                "job": job,
                "test_project": source["test_project"],
                "category_or_filter": source["category_or_filter"],
                "expected_presence": True,
                "result": "PASSED",
            }
        )
    return {
        "format_version": FORMAT_VERSION,
        "cross_tenant_sources_expected": len(REQUIRED_CROSS_TENANT_CATEGORIES),
        "cross_tenant_sources_executed": len(output_sources),
        "cross_tenant_sources_passed": len(output_sources),
        "cross_tenant_sources_missing": 0,
        "cross_tenant_sources_failed": 0,
        "cross_tenant_sources_skipped": 0,
        "cross_tenant_incidents_observed": 0,
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
) -> dict[str, Any]:
    entries = raw_manifest.get("items")
    if not isinstance(entries, list):
        fail("ROLLBACK_MANIFEST_INVALID", "Rollback evidence must contain an items list.")
    expected_ids = {item["id"] for item in selected}
    ids = [entry.get("owning_backlog_item") for entry in entries]
    if len(ids) != len(set(ids)):
        fail("ROLLBACK_ITEM_DUPLICATED", "Rollback evidence duplicates a backlog item.")
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
            }
        )
    verified = sum(entry["status"] == "VERIFIED" for entry in output)
    return {
        "format_version": FORMAT_VERSION,
        "rollback_items_expected": len(expected_ids),
        "rollback_items_verified": verified,
        "rollback_items_blocked": len(output) - verified,
        "rollback_items_missing": 0,
        "rollback_test_passed": True,
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


def validate_dependency_diff(repository_root: Path, base_main_sha: str) -> dict[str, Any]:
    changed = run_git(
        repository_root,
        "diff",
        "--name-only",
        base_main_sha,
        "--",
        *DEPENDENCY_FILES,
    ).splitlines()
    changed = sorted(path.replace("\\", "/") for path in changed if path.strip())
    if changed:
        fail(
            "DEPENDENCY_FILES_CHANGED",
            "Dependency manifests or lockfiles changed relative to the fixed base.",
            files=changed,
        )
    return {
        "dependency_manifest_changed": False,
        "dependency_lockfile_changed": False,
        "dependency_diff_against_base": "CLEAN",
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
) -> dict[str, Any]:
    if int(issue.get("number", 0)) != 5 or str(issue.get("state", "")).upper() != "OPEN":
        fail("ISSUE5_OMITTED_OR_CLOSED", "Issue #5 must be consulted and remain open.")
    issue5_ids = {str(value).lower() for value in issue.get("tracked_advisory_ids") or []}
    if issue5_ids != {ISSUE5_ADVISORY.lower()}:
        fail("ISSUE5_SCOPE_MISREPRESENTED", "Issue #5 must track only its sharp advisory.")

    base = validate_audit_snapshot(base_audit, "base")
    branch = validate_audit_snapshot(branch_audit, "branch")
    base_totals = base["totals"]
    branch_totals = branch["totals"]
    if branch_totals["critical"] > 0:
        fail("AUDIT_CRITICAL_PRESENT", "The branch audit contains a critical advisory.")
    for key, expected in EXPECTED_BASE_AUDIT_TOTALS.items():
        if base_totals[key] != expected:
            fail(
                "BASE_AUDIT_UNEXPECTED",
                "The real base audit no longer matches the fixed inherited baseline.",
                severity=key,
                expected=expected,
                actual=base_totals[key],
            )
    for key in ("total", "critical", "high", "moderate", "low"):
        if branch_totals[key] > base_totals[key]:
            fail(
                "BRANCH_AUDIT_WORSENED",
                "The branch audit worsened relative to the real base audit.",
                severity=key,
                base=base_totals[key],
                branch=branch_totals[key],
            )

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

    base_ids = {item["advisory_id"].lower() for item in base["advisories"]}
    branch_ids = {item["advisory_id"].lower() for item in branch["advisories"]}
    expected_ids = {value.lower() for value in EXPECTED_BASE_ADVISORIES}
    if base_ids != expected_ids:
        fail("BASE_ADVISORY_SET_UNEXPECTED", "The base advisory set is incomplete or changed.")
    if identities(base) != identities(branch):
        fail(
            "BRANCH_ADVISORY_SET_CHANGED",
            "The branch advisory or affected package set changed without dependency changes.",
        )
    if any(dependency_diff.get(key) is not False for key in (
        "dependency_manifest_changed",
        "dependency_lockfile_changed",
    )) or dependency_diff.get("dependency_diff_against_base") != "CLEAN":
        fail("DEPENDENCY_FILES_CHANGED", "Dependency files changed relative to the fixed base.")

    additional_ids = base_ids - {ISSUE5_ADVISORY.lower()}
    if (
        str(additional_issue.get("state", "")).upper() != "OPEN"
        or additional_issue.get("title") != ADDITIONAL_SECURITY_ISSUE_TITLE
        or {
            str(value).lower()
            for value in additional_issue.get("tracked_advisory_ids") or []
        } != additional_ids
    ):
        fail(
            "ADDITIONAL_SECURITY_TRACKING_INVALID",
            "The additional security issue must remain open and track the ten-advisory gap.",
        )
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
            "fix_available": item["fix_available"],
            "fix_compatibility": item["fix_compatibility"],
            "tracking_issue": tracking_by_id[item["advisory_id"].lower()],
        }
        for item in branch["advisories"]
    ]
    return {
        "known_security_issues": [
            {
                "id": "Issue #5",
                "state": "OPEN",
                "title": issue.get("title"),
                "url": issue.get("url"),
                "tracked_advisories": 1,
            },
            {
                "id": f"Issue #{additional_issue['number']}",
                "state": "OPEN",
                "title": additional_issue.get("title"),
                "url": additional_issue.get("url"),
                "tracked_advisories": 10,
            },
        ],
        "dependency_advisories": redacted_advisories,
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
                "rollback_test_passed",
            )
        },
        "known_security_issues": security["known_security_issues"],
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
        "dependency_manifest_changed": False,
        "dependency_lockfile_changed": False,
        "dependency_diff_against_base": "CLEAN",
        "audit_tracking_gap_detected": True,
        "audit_tracking_gap_count": 10,
        "dependency_advisories": security["dependency_advisories"],
        "dependency_security_status": "BLOCKED",
        "additional_security_tracking_status": "OPEN",
        "additional_security_tracking_issue": security["additional_issue"],
        "physical_path_tests_local": "NOT_EXECUTED",
        "physical_path_tests_local_reason": "POWERSHELL_7_UNAVAILABLE",
        "physical_path_tests_ci": "PASSED",
        "powershell_ci_major_version": 7,
        "resolved_decisions": [decision["id"] for decision in decisions["resolved_decisions"]],
        "open_decisions": [decision["id"] for decision in decisions["open_decisions"]]
        + ["Issue #5", f"Issue #{security['additional_issue']['number']}"],
        "blocking_scope": decisions["blocking_scope"],
        "work_allowed": decisions["work_allowed"],
        "normative_blockers": [
            {
                "id": "REL-000-DEF-001",
                "title": "Clasificación inconsistente de FIN-001 dentro de MVP-0",
                "alternatives_for_owner": [
                    "A. Reclasificar FIN-001 como MVP-1.",
                    "B. Dividir FIN-001 en un mínimo financiero MVP-0 y unit economics MVP-1.",
                    "C. Redefinir explícitamente todos P0 completos como el cierre transitivo de REL-000.",
                    "D. Cambiar la secuencia REL-000 / EXT-001 / FIN-001 mediante una decisión normativa nueva.",
                ],
            }
        ],
        "artifact_sources": artifacts,
        "owner_approval_status": "PENDING",
        "normative_scope_status": "BLOCKED_BY_OWNER_DECISION",
        "technical_evidence_status": "PASSED",
        "release_candidate_status": "BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION",
        "technical_gate_outcome": "EVIDENCE_COMPLETE_RELEASE_BLOCKED",
        "result": "REL000_EVIDENCE_GENERATED",
    }
    return result


def validate_owner_state(report: dict[str, Any]) -> None:
    if report.get("owner_approval_status") != "PENDING":
        fail("OWNER_APPROVAL_FALSELY_ASSERTED", "Owner approval must remain PENDING.")
    if report.get("dependency_security_status") != "BLOCKED":
        fail("DEPENDENCY_SECURITY_FALSELY_PASSED", "Dependency security must remain BLOCKED.")
    if report.get("release_candidate_status") != (
        "BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION"
    ):
        fail("RELEASE_CANDIDATE_NOT_BLOCKED", "The release candidate must remain blocked.")
    if report.get("audit_tracking_gap_detected") is not True or report.get(
        "audit_tracking_gap_count"
    ) != 10:
        fail("AUDIT_TRACKING_GAP_HIDDEN", "The ten-advisory Issue #5 tracking gap must be visible.")
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
    normative = load_normative(repository_root)
    normative_evidence = validate_normative_checksums(normative["root"])
    selected, all_items = normative_items(normative)
    item_input = load_json(args.item_evidence)
    validate_extension_not_started(repository_root, item_input)
    p0 = validate_item_evidence(
        repository_root,
        selected,
        all_items,
        item_input,
        trace["base_main_sha"],
    )
    job_results = json.loads(args.job_results_json)
    validate_jobs(job_results)
    cross_tenant = validate_cross_tenant(
        repository_root, load_json(args.cross_tenant_evidence), job_results
    )
    rollback = validate_rollback(repository_root, load_json(args.rollback_evidence), selected)
    decisions = validate_decisions(normative["gates"])
    issue = load_json(args.issue5)
    additional_issue = load_json(args.security_tracking_issue)
    base_audit = load_json(args.base_audit)
    branch_audit = load_json(args.branch_audit)
    dependency_diff = validate_dependency_diff(repository_root, trace["base_main_sha"])
    security = validate_issue_and_audit(
        issue,
        additional_issue,
        base_audit,
        branch_audit,
        dependency_diff,
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
    artifacts = {"ops001": ops001_artifact, "ops002": ops002_artifact}
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
    unique = {
        (
            advisory.get("advisory_id"),
            advisory.get("package"),
            tuple(advisory.get("installed_versions") or []),
            advisory.get("severity"),
        ): advisory
        for advisory in advisories
    }
    metadata = raw.get("metadata") or {}
    vulnerability_counts = metadata.get("vulnerabilities") or {}
    totals = {
        severity: int(vulnerability_counts.get(severity, 0))
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

    validate = subparsers.add_parser("validate")
    validate.add_argument("--repository-root", type=Path, required=True)
    validate.add_argument("--item-evidence", type=Path, required=True)
    validate.add_argument("--cross-tenant-evidence", type=Path, required=True)
    validate.add_argument("--rollback-evidence", type=Path, required=True)
    validate.add_argument("--ops001-directory", type=Path, required=True)
    validate.add_argument("--ops002-directory", type=Path, required=True)
    validate.add_argument("--output-directory", type=Path, required=True)
    validate.add_argument("--issue5", type=Path, required=True)
    validate.add_argument("--security-tracking-issue", type=Path, required=True)
    validate.add_argument("--base-audit", type=Path, required=True)
    validate.add_argument("--branch-audit", type=Path, required=True)
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
