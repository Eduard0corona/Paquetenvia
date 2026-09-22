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
import shutil
import stat
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Iterable


FORMAT_VERSION = "paquetenvia-rel000-v1"
SOURCE_PROVENANCE_VERSION = "paquetenvia-rel000-source-v1"
TESTED_PROVENANCE_VERSION = "paquetenvia-rel000-tested-provenance-v1"
EXECUTION_EVIDENCE_VERSION = "paquetenvia-rel000-execution-v2"
WORKFLOW_PROVENANCE_VERSION = "paquetenvia-rel000-workflow-provenance-v1"
OWNER_DECISION_FORMAT = "paquetenvia-mvp0-owner-decision-v1"
OWNER_DECISION_ID = "REL-000-OWNER-001"
OWNER_DECISION_PATH = "docs/releases/mvp-0-owner-decision.json"
OWNER_DECISION_STATEMENT = "Apruebo REL-000"
OWNER_DECISION_REASON = (
    "Aprobación explícita del project owner posterior al cierre técnico y a la "
    "validación completa de la evidencia REL-000."
)
OWNER_DECISION_DATE = "2026-08-02"
APPROVED_EVIDENCE_MAIN_SHA = "3b23a26d97e31424ba023aa4ecf204142ece0445"
APPROVED_WORKFLOW_RUN_ID = "30750187893"
APPROVED_WORKFLOW_RUN_ATTEMPT = 1
APPROVED_ARTIFACT_ID = 8834236041
APPROVED_ARTIFACT_NAME = "rel000-mvp0-internal-release-evidence"
APPROVED_ARTIFACT_DIGEST = (
    "sha256:66f8465a79fd4f082cc715724087f507bb9b528fc57a31aa1241accfab676172"
)
APPROVED_ARTIFACT_ZIP_SIZE = 17147
APPROVED_ARTIFACT_CREATED_AT = "2026-08-02T13:38:42Z"
APPROVED_ARTIFACT_EXPIRES_AT = "2026-08-16T13:38:41Z"
APPROVED_SNAPSHOT_FORMAT = "paquetenvia-rel000-approved-evidence-snapshot-v1"
APPROVED_SNAPSHOT_DIRECTORY = "docs/releases/evidence/rel-000-owner-001"
APPROVED_SNAPSHOT_MANIFEST_PATH = (
    f"{APPROVED_SNAPSHOT_DIRECTORY}/approved-evidence-manifest.json"
)
APPROVED_SNAPSHOT_FILES = {
    "rel000-cross-tenant-evidence.json": {
        "size_bytes": 27804,
        "sha256": "d3a9dc7343ddb2c9f00e3484e89789c639ee555c3fd17e2aff81c047515e5bcd",
    },
    "rel000-internal-release-report.json": {
        "size_bytes": 17749,
        "sha256": "ed30fbbad93369aece753be930e1b6b9d51e12a219e5e9e7560e74082ce68a92",
    },
    "rel000-p0-evidence.json": {
        "size_bytes": 54623,
        "sha256": "5db1e0a0e8c4759198e55c1a8be6f4d9090a9ca6edb1e3cb807f8812ecad7525",
    },
    "rel000-rollback-evidence.json": {
        "size_bytes": 18565,
        "sha256": "267e01eb480349eefb50060d9ee8fe2c7c5dc78ca812a543a2e547f93af37eeb",
    },
}
APPROVED_EXT001_SOURCE_PATH = "tests/fixtures/rel-000/item-evidence.json"
APPROVED_EXT001_SOURCE_BLOB_SHA = "5bdf2c7845aceb84806f3cc0f0fdf3bcc6bbe9ae"
OWNER_APPROVAL_SCOPE = {
    "synthetic_internal_mvp0_only": True,
    "pilot_authorized": False,
    "production_authorized": False,
    "deployment_authorized": False,
    "go_live_authorized": False,
    "real_customers_authorized": False,
    "real_pii_authorized": False,
    "real_pricing_authorized": False,
    "payments_authorized": False,
    "invoicing_authorized": False,
    "external_drivers_authorized": False,
    "ext001_started": False,
}
NORMAL_RELEASE_EVIDENCE = "NORMAL_RELEASE_EVIDENCE"
SECURITY_REMEDIATION = "SECURITY_REMEDIATION"
REL000_MODES = {NORMAL_RELEASE_EVIDENCE, SECURITY_REMEDIATION}
REMEDIATION_POLICY_FORMAT = "paquetenvia-rel000-security-remediation-policy-v2"
SHARP_REMEDIATION_ID = "ISSUE-5-SHARP-035-REMEDIATION"
WEB_TRANSITIVE_REMEDIATION_ID = "SEC-2026-08-SECURITY-BASELINE"
NEXT_CRITICAL_REMEDIATION_ID = "SEC-2026-09-NEXT-CRITICAL"
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
AUTHORITATIVE_JOB_NAMES = {
    "normative": "Validate normative baseline",
    "dotnet": "Build and test .NET",
    "runtime-contracts": "Validate runtime contracts",
    "web": "Validate web workspace",
    "realtime-e2e": "Validate real SignalR reconnect",
    "outbox-signalr-delivery": "Validate outbox SignalR delivery",
    "driver-stops-pwa": "Validate driver PWA",
    "public-tracking": "Validate public tracking",
    "operations-dashboard": "Validate operations dashboard",
    "delivery-simulation": "Validate 20-delivery simulation",
    "infrastructure": "Validate local infrastructure",
    "backup-restore": "Validate backup and restore",
    "rel000": "Validate MVP-0 internal release evidence",
}
EXECUTION_ARTIFACT_NAMES = {
    "dotnet": "rel000-execution-dotnet",
    "runtime-contracts": "rel000-execution-runtime-contracts",
    "realtime-e2e": "rel000-execution-realtime-e2e",
    "outbox-signalr-delivery": "rel000-execution-outbox-signalr-delivery",
    "driver-stops-pwa": "rel000-execution-driver-stops-pwa",
    "public-tracking": "rel000-execution-public-tracking",
    "operations-dashboard": "rel000-execution-operations-dashboard",
    "delivery-simulation": "delivery-simulation-results",
    "infrastructure": "rel000-execution-infrastructure",
    "backup-restore": "ops002-backup-restore-results",
}
# PR Validation (pull_request → development) is the only other workflow in which the
# REL-000 aggregator executes. Its raw topology is exactly 17 jobs: the Foundation
# `normative` job is decomposed into three real controls, `classify` precedes every
# validation and `PR Gate` is downstream of the aggregator (topology-only: it cannot
# have executed while REL-000 sanitizes provenance and is never producer evidence).
# GitHub does not create a downstream job record until its dependencies conclude, so
# while REL-000 sanitizes provenance `PR Gate` is normally absent from the jobs API
# rather than pending. Exactly the 16 non-downstream jobs are therefore required to be
# observable; `PR Gate` is accepted when absent and validated when present.
PR_VALIDATION_JOB_NAMES = {
    "classify": "Classify PR impact",
    "secret-scan": "Secret scan",
    "normative-contracts": "Validate normative contracts",
    "azr-static": "Validate AZR-001 static guards",
    "dotnet": "Build and test .NET",
    "runtime-contracts": "Validate runtime contracts",
    "web": "Validate web workspace",
    "realtime-e2e": "Validate real SignalR reconnect",
    "outbox-signalr-delivery": "Validate outbox SignalR delivery",
    "driver-stops-pwa": "Validate driver PWA",
    "public-tracking": "Validate public tracking",
    "operations-dashboard": "Validate operations dashboard",
    "delivery-simulation": "Validate 20-delivery simulation",
    "infrastructure": "Validate local infrastructure",
    "backup-restore": "Validate backup and restore",
    "rel000": "Validate MVP-0 internal release evidence",
    "pr-gate": "PR Gate",
}
PR_VALIDATION_PRODUCER_JOBS = frozenset(PR_VALIDATION_JOB_NAMES) - {"rel000", "pr-gate"}
# GitHub job states a downstream job may legitimately report before it has started.
PENDING_JOB_STATUSES = frozenset({"queued", "waiting", "pending", "requested", "in_progress"})


@dataclass(frozen=True)
class WorkflowProvenanceProfile:
    """Exact workflow topology REL-000 accepts as provenance for one workflow."""

    name: str
    workflow_name: str
    events: frozenset[str]
    require_run_identity: bool
    job_names: dict[str, str]
    producer_jobs: frozenset[str]
    aggregator_job: str
    downstream_jobs: frozenset[str]
    artifact_names: dict[str, str]

    @property
    def observable_jobs(self) -> frozenset[str]:
        """Jobs that must be observable while the aggregator sanitizes provenance.

        Downstream jobs are excluded: GitHub only creates their job records once the
        aggregator they depend on has concluded, so requiring them would make the
        aggregator's own provenance step unsatisfiable.
        """
        return frozenset(self.job_names) - self.downstream_jobs

    @property
    def observable_job_names(self) -> frozenset[str]:
        """Raw GitHub names of the jobs that must be observable."""
        return frozenset(self.job_names[key] for key in self.observable_jobs)


WORKFLOW_PROVENANCE_PROFILE_FOUNDATION = "foundation"
WORKFLOW_PROVENANCE_PROFILE_PR_VALIDATION = "pr-validation"
DEFAULT_WORKFLOW_PROVENANCE_PROFILE = WORKFLOW_PROVENANCE_PROFILE_FOUNDATION
WORKFLOW_PROVENANCE_PROFILES: dict[str, WorkflowProvenanceProfile] = {
    WORKFLOW_PROVENANCE_PROFILE_FOUNDATION: WorkflowProvenanceProfile(
        name=WORKFLOW_PROVENANCE_PROFILE_FOUNDATION,
        workflow_name="Foundation CI",
        events=frozenset({"push", "pull_request"}),
        require_run_identity=False,
        job_names=AUTHORITATIVE_JOB_NAMES,
        producer_jobs=frozenset(REQUIRED_JOBS),
        aggregator_job="rel000",
        downstream_jobs=frozenset(),
        artifact_names=EXECUTION_ARTIFACT_NAMES,
    ),
    WORKFLOW_PROVENANCE_PROFILE_PR_VALIDATION: WorkflowProvenanceProfile(
        name=WORKFLOW_PROVENANCE_PROFILE_PR_VALIDATION,
        workflow_name="PR Validation",
        events=frozenset({"pull_request"}),
        require_run_identity=True,
        job_names=PR_VALIDATION_JOB_NAMES,
        producer_jobs=PR_VALIDATION_PRODUCER_JOBS,
        aggregator_job="rel000",
        downstream_jobs=frozenset({"pr-gate"}),
        artifact_names=EXECUTION_ARTIFACT_NAMES,
    ),
}


def resolve_workflow_provenance_profile(value: Any) -> WorkflowProvenanceProfile:
    name = value if isinstance(value, str) else ""
    profile = WORKFLOW_PROVENANCE_PROFILES.get(name)
    if profile is None:
        fail(
            "WORKFLOW_PROVENANCE_PROFILE_INVALID",
            "The workflow provenance profile is unknown.",
            profile=str(value),
            known=sorted(WORKFLOW_PROVENANCE_PROFILES),
        )
    return profile


def validate_workflow_run_identity(profile: WorkflowProvenanceProfile, run_payload: dict[str, Any], attempt: int) -> None:
    """Prove the run belongs to the profiled workflow when GitHub supplies that identity."""
    name = run_payload.get("name")
    event = run_payload.get("event")
    if profile.require_run_identity and (not isinstance(name, str) or not isinstance(event, str)):
        fail("WORKFLOW_PROVENANCE_RUN_IDENTITY_MISSING", "Workflow run identity is absent.", attempt=attempt)
    if name is not None and name != profile.workflow_name:
        fail("WORKFLOW_PROVENANCE_WORKFLOW_MISMATCH", "The run belongs to another workflow.", workflow=str(name), attempt=attempt)
    if event is not None and event not in profile.events:
        fail("WORKFLOW_PROVENANCE_EVENT_INVALID", "The run event is not valid for this workflow.", event=str(event), attempt=attempt)
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
    "Directory.Packages.props",
    "package.json",
    "package-lock.json",
    "packages.lock.json",
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
    re.compile(r"https?://", re.IGNORECASE),
    re.compile(r"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", re.IGNORECASE),
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
    if policy.get("format_version") != REMEDIATION_POLICY_FORMAT:
        fail("REMEDIATION_POLICY_FORMAT_INVALID", "The remediation policy format is unknown.")
    if policy.get("default_mode") != NORMAL_RELEASE_EVIDENCE:
        fail("REMEDIATION_POLICY_INVALID", "The remediation policy must fail closed to normal mode.")
    historical = policy.get("historical_remediations")
    active = policy.get("active_remediations")
    if not isinstance(historical, list) or not isinstance(active, list):
        fail("REMEDIATION_POLICY_INVALID", "The remediation registries must be arrays.")
    identifiers: set[str] = set()
    branches: set[str] = set()
    required = {
        "id",
        "mode",
        "authorized_base_sha",
        "authorized_source_branch",
        "expected_base_advisories",
        "allowed_dependency_files",
    }
    for authorization in [*historical, *active]:
        if not isinstance(authorization, dict) or not required.issubset(authorization):
            fail("REMEDIATION_POLICY_INVALID", "A remediation authorization is incomplete.")
        identifier = authorization.get("id")
        branch = authorization.get("authorized_source_branch")
        base = authorization.get("authorized_base_sha")
        advisories = authorization.get("expected_base_advisories")
        files = authorization.get("allowed_dependency_files")
        if (
            not isinstance(identifier, str)
            or not identifier
            or identifier in identifiers
            or not isinstance(branch, str)
            or not branch
            or branch in branches
            or not isinstance(base, str)
            or re.fullmatch(r"[0-9a-f]{40}", base) is None
            or authorization.get("mode") != SECURITY_REMEDIATION
            or not isinstance(advisories, list)
            or not advisories
            or any(not isinstance(value, str) or not value.startswith("GHSA-") for value in advisories)
            or not isinstance(files, list)
            or not files
            or any(
                not isinstance(value, str) or Path(value).name not in DEPENDENCY_FILE_NAMES
                for value in files
            )
        ):
            fail("REMEDIATION_POLICY_INVALID", "A remediation authorization is invalid.")
        identifiers.add(identifier)
        branches.add(branch)
    for authorization in active:
        totals = authorization.get("expected_base_totals")
        required_files = authorization.get("required_dependency_files")
        allowed_direct = authorization.get("allowed_direct_packages")
        allowed_non_dependency = authorization.get("allowed_non_dependency_files")
        if (
            not isinstance(totals, dict)
            or set(totals) != {"total", "critical", "high", "moderate", "low"}
            or any(not isinstance(value, int) or value < 0 for value in totals.values())
            or totals["total"] != sum(totals[key] for key in ("critical", "high", "moderate", "low"))
            or not isinstance(required_files, list)
            or not set(required_files).issubset(set(authorization["allowed_dependency_files"]))
            or not isinstance(allowed_direct, list)
            or not isinstance(allowed_non_dependency, list)
            or any(not isinstance(value, str) or not value for value in allowed_non_dependency)
            or not isinstance(authorization.get("tracked_issue"), int)
        ):
            fail("REMEDIATION_POLICY_INVALID", "An active remediation authorization is invalid.")
        if authorization.get("id") == WEB_TRANSITIVE_REMEDIATION_ID:
            branch_totals = authorization.get("expected_branch_totals")
            expected_packages = authorization.get("expected_base_packages")
            replacements = authorization.get("expected_lock_version_replacements")
            nuget_versions = authorization.get("expected_nuget_versions")
            issue_advisories = authorization.get("issue_advisories")
            reconciliation = authorization.get("nanoid_source_reconciliation")
            if (
                authorization.get("tracked_issue_title")
                != "SEC-2026-08: remediate current web transitive dependency advisories"
                or authorization.get("related_tracked_issue") != 40
                or authorization.get("related_tracked_issue_title")
                != "SEC-2026-08: remediate transitive SSH.NET advisory"
                or issue_advisories
                != {
                    "38": [
                        "GHSA-fxqj-rqcc-2cmp",
                        "GHSA-rgw5-rvv9-x895",
                        "GHSA-2v37-7h3g-55p8",
                        "GHSA-5p4m-2wfm-xmqj",
                    ],
                    "40": ["GHSA-q939-rpr3-3284"],
                }
                or not isinstance(branch_totals, dict)
                or set(branch_totals) != {"total", "critical", "high", "moderate", "low"}
                or any(not isinstance(value, int) or value < 0 for value in branch_totals.values())
                or branch_totals["total"]
                != sum(branch_totals[key] for key in ("critical", "high", "moderate", "low"))
                or not isinstance(expected_packages, dict)
                or set(expected_packages) != set(authorization["expected_base_advisories"])
                or not isinstance(replacements, dict)
                or set(replacements) != {"postcss", "brace-expansion", "nanoid", "js-yaml", "sharp"}
                or any(
                    not isinstance(value, dict)
                    or set(value) != {"from", "to"}
                    or not all(
                        isinstance(versions, list)
                        and versions
                        and all(isinstance(version, str) and version for version in versions)
                        for versions in value.values()
                    )
                    for value in replacements.values()
                )
                or not isinstance(authorization.get("expected_lock_package_count"), int)
                or authorization["expected_lock_package_count"] < 1
                or nuget_versions
                != {
                    "Testcontainers.PostgreSql": {"from": "4.13.0", "to": "4.14.0"},
                    "Testcontainers": {"from": "4.13.0", "to": "4.14.0"},
                    "SSH.NET": {"from": "2025.1.0", "to": "2026.0.0"},
                    "BouncyCastle.Cryptography": {"from": "2.6.2", "to": "2.7.0"},
                }
                or authorization.get("expected_nuget_dependency_path")
                != "Testcontainers.PostgreSql 4.13.0 -> Testcontainers 4.13.0 -> SSH.NET 2025.1.0"
                or authorization.get("expected_nuget_advisory") != "GHSA-q939-rpr3-3284"
                or authorization.get("expected_nuget_cve") != "CVE-2026-48798"
                or authorization.get("direct_ssh_net_pin_allowed") is not False
                or authorization.get("central_package_transitive_pinning_allowed") is not False
                or authorization.get("prohibited_prereleases") is not True
                or authorization.get("manual_lockfile_edits_allowed") is not False
                or not isinstance(reconciliation, dict)
                or reconciliation.get("github_advisory_id") != "GHSA-2v37-7h3g-55p8"
                or reconciliation.get("github_advisory_affected_range") != "<3.3.18"
                or reconciliation.get("github_advisory_first_patched_version") != "3.3.18"
                or reconciliation.get("pnpm_3_3_17_result") != "ONE_HIGH_ADVISORY"
                or reconciliation.get("pnpm_3_3_18_result") != "AUDIT_ZERO"
            ):
                fail(
                    "REMEDIATION_POLICY_INVALID",
                    "The web-transitive remediation authorization is incomplete or inconsistent.",
                )
        if authorization.get("id") == NEXT_CRITICAL_REMEDIATION_ID:
            issue_advisories = authorization.get("issue_advisories")
            version_changes = authorization.get("expected_direct_version_changes")
            branch_totals = authorization.get("expected_branch_totals")
            expected_ids = [
                "GHSA-p293-qw3h-jr36",
                "GHSA-2xp9-vwfh-vxw4",
                "GHSA-c83g-rgw3-j3cx",
                "GHSA-73wf-gq98-2v4g",
                "GHSA-rgj7-g3m4-5g8c",
                "GHSA-2883-xcg3-v3hh",
                "GHSA-82fw-gwwq-j7x9",
                "GHSA-w5vr-8v7q-w6rv",
            ]
            if (
                authorization.get("tracked_issue") != 48
                or authorization.get("tracked_issue_title")
                != "SEC-2026-09: remediate Next.js critical advisories"
                or issue_advisories != {"48": expected_ids}
                or authorization.get("expected_base_advisories") != expected_ids
                or authorization.get("expected_base_totals")
                != {"total": 9, "critical": 2, "high": 4, "moderate": 3, "low": 0}
                or branch_totals
                != {"total": 0, "critical": 0, "high": 0, "moderate": 0, "low": 0}
                or set(authorization.get("allowed_direct_packages") or [])
                != {"next", "eslint-config-next", "vitest"}
                or version_changes
                != {
                    "next": {"from": "16.2.11", "to": "16.3.3"},
                    "eslint-config-next": {"from": "16.2.11", "to": "16.3.3"},
                    "vitest": {"from": "4.1.10", "to": "4.1.11"},
                }
                or authorization.get("expected_cves")
                != {
                    "GHSA-p293-qw3h-jr36": "CVE-2026-75604",
                    "GHSA-2xp9-vwfh-vxw4": None,
                }
                or authorization.get("next_affected_range") != ">=16.0.0 <16.3.3"
                or authorization.get("next_first_patched_version") != "16.3.3"
                or authorization.get("allowed_overrides")
                != {
                    "postcss": "8.5.23",
                    "brace-expansion@<1.1.18": "1.1.18",
                    "brace-expansion@>=4.0.0 <5.0.9": "5.0.9",
                    "nanoid@>=3.0.0 <3.3.18": "3.3.18",
                    "js-yaml@>=4.0.0 <4.3.2": "4.3.2",
                    "browserslist@<=4.28.6": "4.28.7",
                    "baseline-browser-mapping@>=2.0.0 <2.11.0": "2.11.0",
                    "next@16.3.3>sharp": "0.35.4",
                }
                or authorization.get("expected_lock_package_count") != 475
                or re.fullmatch(
                    r"[0-9a-f]{64}", str(authorization.get("expected_lockfile_sha256", ""))
                )
                is None
                or authorization.get("target_sharp_version") != "0.35.4"
                or authorization.get("require_sharp_runtime_smoke") is not True
                or authorization.get("prohibited_prereleases") is not True
                or authorization.get("manual_lockfile_edits_allowed") is not False
            ):
                fail(
                    "REMEDIATION_POLICY_INVALID",
                    "The Next.js critical remediation authorization is incomplete or inconsistent.",
                )
    return policy


def resolve_rel000_mode(
    policy: dict[str, Any], source_branch: str, remediation_id: str = ""
) -> str:
    active = policy["active_remediations"]
    matching_branch = next(
        (item for item in active if item["authorized_source_branch"] == source_branch),
        None,
    )
    if not remediation_id:
        if matching_branch is not None:
            fail(
                "REMEDIATION_ID_REQUIRED",
                "An active remediation branch must request its exact remediation ID.",
            )
        return NORMAL_RELEASE_EVIDENCE
    authorization = next((item for item in active if item["id"] == remediation_id), None)
    if authorization is None:
        historical = {item["id"] for item in policy["historical_remediations"]}
        fail(
            "REMEDIATION_ID_NOT_ACTIVE" if remediation_id in historical else "REMEDIATION_ID_UNKNOWN",
            "The requested remediation ID is not an active authorization.",
            remediation_id=remediation_id,
        )
    if source_branch == authorization["authorized_source_branch"]:
        return SECURITY_REMEDIATION
    return NORMAL_RELEASE_EVIDENCE


def active_remediation(policy: dict[str, Any], remediation_id: str) -> dict[str, Any]:
    authorization = next(
        (item for item in policy["active_remediations"] if item["id"] == remediation_id),
        None,
    )
    if authorization is None:
        fail("REMEDIATION_ID_UNKNOWN", "The requested remediation authorization was not found.")
    return authorization


def validate_mode_authorization(
    mode: str,
    policy: dict[str, Any],
    source_branch: str,
    base_main_sha: str,
    remediation_id: str = "",
) -> dict[str, Any] | None:
    if mode not in REL000_MODES:
        fail("REL000_MODE_INVALID", "The REL-000 execution mode is invalid.", mode=mode)
    resolved = resolve_rel000_mode(policy, source_branch, remediation_id)
    if mode != resolved:
        fail(
            "REL000_MODE_NOT_AUTHORIZED",
            "The requested REL-000 mode is not authorized for this source branch.",
            requested=mode,
            authorized=resolved,
        )
    if mode == NORMAL_RELEASE_EVIDENCE:
        return None
    authorization = active_remediation(policy, remediation_id)
    if source_branch != authorization["authorized_source_branch"]:
        fail("REL000_MODE_NOT_AUTHORIZED", "The remediation branch does not match its authorization.")
    if base_main_sha != authorization["authorized_base_sha"]:
        fail(
            "SECURITY_REMEDIATION_BASE_MISMATCH",
            "Security remediation requires the exact authorized base SHA.",
            expected=authorization["authorized_base_sha"],
            actual=base_main_sha,
        )
    return authorization


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


def git_commit_exists(repository_root: Path, sha: str) -> bool:
    result = subprocess.run(
        ["git", "-C", str(repository_root), "cat-file", "-e", f"{sha}^{{commit}}"],
        check=False,
        capture_output=True,
    )
    return result.returncode == 0


def tested_commit_parents(repository_root: Path, tested_git_sha: str) -> list[str]:
    """Return the raw parent list of a commit object, independent of shallow grafts."""
    raw = run_git(repository_root, "cat-file", "-p", f"{tested_git_sha}^{{commit}}")
    parents: list[str] = []
    for line in raw.splitlines():
        if not line.strip():
            break
        if line.startswith("parent "):
            parents.append(line.split(" ", 1)[1].strip().lower())
    return parents


def resolve_tested_merge_base(
    repository_root: Path,
    tested_git_sha: str,
    source_head_sha: str,
) -> str:
    """Resolve the authoritative base of a pull_request merge ref from its topology.

    GitHub tests ``refs/pull/N/merge`` = merge(base_branch_tip, pull_request_head).
    The first parent of that merge commit is the baseline that was actually tested;
    ``github.event.pull_request.base.sha`` can lag behind it once the base branch
    advances after the pull request was created, so it is never consulted here.
    """
    tested = validate_sha(tested_git_sha, "TESTED_GIT_SHA_MALFORMED", "tested_git_sha")
    source = validate_sha(source_head_sha, "SOURCE_HEAD_SHA_MALFORMED", "source_head_sha")
    if not git_commit_exists(repository_root, tested):
        fail(
            "GIT_SHA_NOT_IN_CHECKOUT",
            "tested_git_sha is not available in the checkout.",
            field="tested_git_sha",
        )
    parents = tested_commit_parents(repository_root, tested)
    if len(parents) != 2 or len(set(parents)) != 2:
        fail(
            "TESTED_COMMIT_NOT_MERGE",
            "The tested pull_request commit is not a two-parent merge commit.",
            tested_git_sha=tested,
            parent_count=len(parents),
        )
    for parent in parents:
        if not git_commit_exists(repository_root, parent):
            fail(
                "TESTED_MERGE_PARENT_UNAVAILABLE",
                "A parent of the tested merge commit is not available in the checkout.",
                parent=parent,
            )
    base, merged_source = parents
    if merged_source != source:
        fail(
            "TESTED_MERGE_SOURCE_MISMATCH",
            "The declared source HEAD is not the second parent of the tested merge commit.",
            declared_source_head_sha=source,
            tested_merge_second_parent=merged_source,
        )
    return base


def resolve_tested_provenance(
    repository_root: Path,
    event_name: str,
    tested_git_sha: str,
    source_head_sha: str,
    event_pull_request_base_sha: str | None = None,
) -> dict[str, Any]:
    """Single authoritative resolution of the checkout that Foundation actually tested.

    ``push``: source = tested = base = the pushed commit.
    ``pull_request``: base = first parent of the tested merge commit, after proving
    the merge topology. The event base SHA is only recorded as diagnostic metadata.
    Any other event, or a topology that cannot be proven, fails closed.
    """
    tested = validate_sha(tested_git_sha, "TESTED_GIT_SHA_MALFORMED", "tested_git_sha")
    source = validate_sha(source_head_sha, "SOURCE_HEAD_SHA_MALFORMED", "source_head_sha")
    event_base: str | None = None
    if str(event_pull_request_base_sha or "").strip():
        event_base = validate_sha(
            event_pull_request_base_sha,
            "EVENT_PULL_REQUEST_BASE_SHA_MALFORMED",
            "event_pull_request_base_sha",
        )
    checkout = run_git(repository_root, "rev-parse", "HEAD").lower()
    if checkout != tested:
        fail(
            "TESTED_SHA_CHECKOUT_MISMATCH",
            "tested_git_sha does not match the checked-out commit.",
            expected=tested,
            actual=checkout,
        )
    if event_name == "push":
        if source != tested:
            fail(
                "PUSH_SOURCE_TESTED_MISMATCH",
                "A push event tests exactly the pushed commit.",
                source_head_sha=source,
                tested_git_sha=tested,
            )
        base = tested
        relationship = "same_commit"
        resolution = "push_same_commit"
    elif event_name == "pull_request":
        if source == tested:
            fail(
                "PULL_REQUEST_SOURCE_IS_TESTED",
                "A pull_request event tests a merge commit, never the source HEAD itself.",
            )
        base = resolve_tested_merge_base(repository_root, tested, source)
        relationship = "source_head_is_ancestor_of_tested_commit"
        resolution = "tested_merge_first_parent"
    else:
        fail(
            "EVENT_NAME_UNSUPPORTED",
            "Foundation provenance is only defined for push and pull_request events.",
            event_name=event_name,
        )
    return {
        "format_version": TESTED_PROVENANCE_VERSION,
        "event_name": event_name,
        "source_head_sha": source,
        "tested_git_sha": tested,
        "base_main_sha": base,
        "git_relationship": relationship,
        "base_resolution": resolution,
        "event_pull_request_base_sha": event_base,
        "event_pull_request_base_sha_authoritative": False,
        "event_pull_request_base_sha_matches_tested_base": (
            None if event_base is None else event_base == base
        ),
    }


def resolve_tested_provenance_command(args: argparse.Namespace) -> dict[str, Any]:
    resolved = resolve_tested_provenance(
        args.repository_root.resolve(),
        args.event_name,
        args.tested_git_sha,
        args.source_head_sha,
        args.event_pull_request_base_sha,
    )
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        write_json(args.output, resolved)
    if args.github_env is not None:
        lines = "".join(
            f"{name}={resolved[field]}\n"
            for name, field in (
                ("REL000_SOURCE_HEAD_SHA", "source_head_sha"),
                ("REL000_TESTED_GIT_SHA", "tested_git_sha"),
                ("REL000_BASE_MAIN_SHA", "base_main_sha"),
                ("REL000_GIT_RELATIONSHIP", "git_relationship"),
            )
        )
        with args.github_env.open("a", encoding="utf-8") as stream:
            stream.write(lines)
    print(json.dumps(resolved, ensure_ascii=False, sort_keys=True))
    return resolved


def load_owner_decision(path: Path) -> dict[str, Any]:
    if not path.is_file():
        fail("OWNER_DECISION_RECORD_MISSING", "The versioned owner decision record is missing.")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        fail("OWNER_DECISION_JSON_INVALID", "The owner decision record is not valid JSON.", error=str(exc))
    if not isinstance(value, dict):
        fail("OWNER_DECISION_JSON_INVALID", "The owner decision record must be a JSON object.")
    return value


def validate_owner_decision_record(record: dict[str, Any]) -> dict[str, Any]:
    if record.get("format_version") != OWNER_DECISION_FORMAT:
        fail("OWNER_DECISION_FORMAT_INVALID", "The owner decision format version is invalid.")
    if record.get("decision_id") != OWNER_DECISION_ID:
        fail("OWNER_DECISION_ID_INVALID", "The owner decision ID is invalid.")
    if record.get("release") != "MVP-0_INTERNAL":
        fail("OWNER_DECISION_RELEASE_INVALID", "The owner decision exceeds the internal MVP-0 release.")
    if record.get("decision") != "APPROVE":
        fail("OWNER_DECISION_VALUE_INVALID", "The owner decision must be APPROVE.")
    statement = record.get("decision_statement")
    if not isinstance(statement, str) or not statement:
        fail("OWNER_DECISION_STATEMENT_EMPTY", "The owner decision statement is empty.")
    if statement != OWNER_DECISION_STATEMENT:
        fail("OWNER_DECISION_STATEMENT_INVALID", "The owner decision statement is not exact.")
    reason = record.get("decision_reason")
    if not isinstance(reason, str) or not reason.strip():
        fail("OWNER_DECISION_REASON_EMPTY", "The owner decision reason is empty.")
    if reason != OWNER_DECISION_REASON:
        fail("OWNER_DECISION_REASON_INVALID", "The owner decision reason is not exact.")
    decided_on = record.get("decided_on")
    try:
        parsed_date = dt.date.fromisoformat(decided_on) if isinstance(decided_on, str) else None
    except ValueError:
        parsed_date = None
    if parsed_date is None or decided_on != OWNER_DECISION_DATE:
        fail("OWNER_DECISION_DATE_INVALID", "The owner decision date is invalid or unexpected.")
    if record.get("decided_by") != "project_owner":
        fail("OWNER_DECISION_ACTOR_INVALID", "The decision actor must be project_owner.")

    evidence = record.get("approved_evidence")
    if not isinstance(evidence, dict):
        fail("OWNER_DECISION_EVIDENCE_INVALID", "The approved evidence anchor is missing.")
    expected_evidence = {
        "main_sha": APPROVED_EVIDENCE_MAIN_SHA,
        "workflow_run_id": APPROVED_WORKFLOW_RUN_ID,
        "workflow_run_attempt": APPROVED_WORKFLOW_RUN_ATTEMPT,
        "artifact_id": APPROVED_ARTIFACT_ID,
        "artifact_name": APPROVED_ARTIFACT_NAME,
        "artifact_digest": APPROVED_ARTIFACT_DIGEST,
    }
    reason_codes = {
        "main_sha": "EXT001_STATE_SOURCE_MAIN_MISMATCH",
        "workflow_run_id": "OWNER_DECISION_RUN_ID_MISMATCH",
        "workflow_run_attempt": "OWNER_DECISION_RUN_ATTEMPT_MISMATCH",
        "artifact_id": "OWNER_DECISION_ARTIFACT_ID_MISMATCH",
        "artifact_name": "OWNER_DECISION_ARTIFACT_NAME_MISMATCH",
        "artifact_digest": "OWNER_DECISION_ARTIFACT_DIGEST_MISMATCH",
    }
    for key, expected in expected_evidence.items():
        if evidence.get(key) != expected:
            fail(reason_codes[key], "The owner decision evidence anchor does not match.", field=key)

    scope = record.get("scope")
    if not isinstance(scope, dict) or set(scope) != set(OWNER_APPROVAL_SCOPE):
        fail("OWNER_DECISION_SCOPE_INVALID", "The owner approval scope is incomplete or expanded.")
    for key, expected in OWNER_APPROVAL_SCOPE.items():
        value = scope.get(key)
        if not isinstance(value, bool) or value is not expected:
            reason_code = "EXT001_ALREADY_STARTED" if key == "ext001_started" and value is True else "OWNER_DECISION_SCOPE_INVALID"
            fail(reason_code, "The owner approval scope contains an unauthorized value.", field=key)

    preservation = record.get("evidence_preservation")
    expected_preservation = {
        "mode": "VERSIONED_REDACTED_SNAPSHOT",
        "manifest_path": APPROVED_SNAPSHOT_MANIFEST_PATH,
        "snapshot_directory": APPROVED_SNAPSHOT_DIRECTORY,
        "historical_artifact_live_access_required": False,
    }
    if preservation != expected_preservation:
        fail(
            "OWNER_DECISION_EVIDENCE_PRESERVATION_INVALID",
            "The owner decision must reference the canonical durable evidence snapshot.",
        )
    return copy.deepcopy(record)


def _git_file_at_commit(
    repository_root: Path,
    commit_sha: str,
    relative_path: str,
    missing_reason: str,
) -> tuple[str, str]:
    spec = f"{commit_sha}:{relative_path}"
    blob = subprocess.run(
        ["git", "-C", str(repository_root), "rev-parse", spec],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if blob.returncode != 0 or not SHA40.fullmatch(blob.stdout.strip().lower()):
        fail(missing_reason, "The required versioned source does not exist at the approved SHA.")
    content = subprocess.run(
        ["git", "-C", str(repository_root), "show", spec],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if content.returncode != 0:
        fail(missing_reason, "The required versioned source cannot be read at the approved SHA.")
    return blob.stdout.strip().lower(), content.stdout


def validate_ext001_state_document(
    approved_main_sha: str,
    blob_sha: str,
    content: str,
) -> dict[str, Any]:
    if approved_main_sha != APPROVED_EVIDENCE_MAIN_SHA:
        fail("EXT001_STATE_SOURCE_MAIN_MISMATCH", "The EXT-001 state source is anchored to another main SHA.")
    if blob_sha != APPROVED_EXT001_SOURCE_BLOB_SHA:
        fail("EXT001_STATE_SOURCE_BLOB_MISMATCH", "The EXT-001 state source blob does not match.")
    try:
        source = json.loads(content)
    except json.JSONDecodeError as exc:
        fail("EXT001_STATE_SOURCE_FORMAT_INVALID", "The EXT-001 state source is invalid JSON.", error=str(exc))
    if not isinstance(source, dict) or source.get("format_version") != "paquetenvia-rel000-item-evidence-v1":
        fail("EXT001_STATE_SOURCE_FORMAT_INVALID", "The EXT-001 state source format is invalid.")
    if "ext001_started" not in source:
        fail("EXT001_STARTED_FIELD_MISSING", "The EXT-001 state source omits ext001_started.")
    if not isinstance(source["ext001_started"], bool):
        fail("EXT001_STARTED_FIELD_TYPE_INVALID", "ext001_started must be a JSON boolean.")
    if source["ext001_started"]:
        fail("EXT001_ALREADY_STARTED", "EXT-001 already started in the approved state source.")
    blocked = [
        item.get("id")
        for item in source.get("items", [])
        if item.get("implementation_status") == "BLOCKED"
    ]
    if blocked != ["REL-000"]:
        fail("EXT001_STATE_SOURCE_FORMAT_INVALID", "The approved inventory no longer has only REL-000 blocked.")
    return {
        "path": APPROVED_EXT001_SOURCE_PATH,
        "blob_sha": blob_sha,
        "source_sha": approved_main_sha,
        "ext001_started": False,
    }


def validate_ext001_state_source(repository_root: Path, approved_main_sha: str) -> dict[str, Any]:
    if approved_main_sha != APPROVED_EVIDENCE_MAIN_SHA:
        fail("EXT001_STATE_SOURCE_MAIN_MISMATCH", "The EXT-001 state source is anchored to another main SHA.")
    blob_sha, content = _git_file_at_commit(
        repository_root,
        approved_main_sha,
        APPROVED_EXT001_SOURCE_PATH,
        "EXT001_STATE_SOURCE_MISSING",
    )
    return validate_ext001_state_document(approved_main_sha, blob_sha, content)


def _has_reparse_point(path: Path) -> bool:
    try:
        attributes = getattr(path.lstat(), "st_file_attributes", 0)
    except OSError:
        return False
    return bool(attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))


def _assert_snapshot_redacted(documents: dict[str, Any]) -> None:
    allowed_public_issue_urls = {
        "https://github.com/Eduard0corona/Paquetenvia/issues/5",
        "https://github.com/Eduard0corona/Paquetenvia/issues/30",
    }

    def sanitize_known_public_urls(value: Any) -> Any:
        if isinstance(value, dict):
            return {key: sanitize_known_public_urls(child) for key, child in value.items()}
        if isinstance(value, list):
            return [sanitize_known_public_urls(child) for child in value]
        if isinstance(value, str) and value in allowed_public_issue_urls:
            return "PUBLIC_GITHUB_ISSUE_REFERENCE"
        return value

    assert_redacted(sanitize_known_public_urls(documents))


def _validate_historical_approved_documents(documents: dict[str, Any]) -> dict[str, Any]:
    if set(documents) != OUTPUT_FILES:
        fail(
            "APPROVED_ARTIFACT_CONTENT_INVALID",
            "The approved evidence does not contain exactly four public JSON files.",
        )
    if any(not isinstance(document, dict) for document in documents.values()):
        fail("APPROVED_ARTIFACT_CONTENT_INVALID", "Every approved evidence file must be a JSON object.")
    report = documents["rel000-internal-release-report.json"]
    p0 = documents["rel000-p0-evidence.json"]
    expected_report = {
        "format_version": FORMAT_VERSION,
        "source_head_sha": APPROVED_EVIDENCE_MAIN_SHA,
        "tested_git_sha": APPROVED_EVIDENCE_MAIN_SHA,
        "base_main_sha": APPROVED_EVIDENCE_MAIN_SHA,
        "workflow_run_id": APPROVED_WORKFLOW_RUN_ID,
        "workflow_run_attempt": str(APPROVED_WORKFLOW_RUN_ATTEMPT),
        "dependency_security_status": "PASSED",
        "technical_evidence_status": "PASSED",
        "owner_approval_status": "PENDING",
        "release_candidate_status": "BLOCKED_BY_OWNER_DECISION",
    }
    if any(report.get(key) != value for key, value in expected_report.items()):
        fail("APPROVED_TECHNICAL_EVIDENCE_INVALID", "The approved technical report does not match its immutable state.")
    provenance = report.get("execution_provenance")
    jobs = provenance.get("jobs") if isinstance(provenance, dict) else None
    if (
        not isinstance(jobs, list)
        or len(jobs) != 10
        or {job.get("job") for job in jobs} != EXECUTION_EVIDENCE_JOBS
        or str(provenance.get("workflow_run_id")) != APPROVED_WORKFLOW_RUN_ID
        or provenance.get("aggregator_attempt") != 1
        or provenance.get("mixed_attempt_evidence") is not False
        or any(job.get("producer_attempt") != 1 for job in jobs)
    ):
        fail("APPROVED_ARTIFACT_PROVENANCE_INVALID", "The approved artifact provenance is invalid.")
    blocked_ids = (p0.get("ids_by_status") or {}).get("BLOCKED")
    if (
        report.get("mvp0_p0_items_expected") != 29
        or report.get("mvp0_p0_items_evaluated") != 29
        or report.get("mvp0_p0_items_verified") != 28
        or report.get("mvp0_p0_items_blocked") != 1
        or blocked_ids != ["REL-000"]
    ):
        fail("APPROVED_TECHNICAL_EVIDENCE_INVALID", "The approved historical inventory is invalid.")
    if "ext001_started" in report:
        fail(
            "APPROVED_ARTIFACT_SCHEMA_DRIFT",
            "The historical approved report must preserve the original absence of ext001_started.",
        )
    _assert_snapshot_redacted(documents)
    return {
        "artifact_id": APPROVED_ARTIFACT_ID,
        "artifact_name": APPROVED_ARTIFACT_NAME,
        "artifact_digest": APPROVED_ARTIFACT_DIGEST,
        "technical_artifact_contains_ext001_started": False,
        "dependency_security_status": "PASSED",
        "technical_evidence_status": "PASSED",
    }


def validate_approved_artifact(
    metadata: dict[str, Any],
    zip_path: Path,
    *,
    now: dt.datetime | None = None,
) -> dict[str, Any]:
    """Validate the live historical artifact only during one-time capture."""
    if not isinstance(metadata, dict):
        fail("APPROVED_ARTIFACT_METADATA_INVALID", "Approved artifact metadata is invalid.")
    expected_metadata = {
        "id": APPROVED_ARTIFACT_ID,
        "name": APPROVED_ARTIFACT_NAME,
        "size_in_bytes": APPROVED_ARTIFACT_ZIP_SIZE,
        "digest": APPROVED_ARTIFACT_DIGEST,
        "created_at": APPROVED_ARTIFACT_CREATED_AT,
        "expires_at": APPROVED_ARTIFACT_EXPIRES_AT,
        "expired": False,
    }
    reason_codes = {
        "id": "APPROVED_ARTIFACT_ID_MISMATCH",
        "name": "APPROVED_ARTIFACT_NAME_MISMATCH",
        "size_in_bytes": "APPROVED_ARTIFACT_ZIP_SIZE_MISMATCH",
        "digest": "APPROVED_ARTIFACT_DIGEST_MISMATCH",
        "created_at": "APPROVED_ARTIFACT_METADATA_INVALID",
        "expires_at": "APPROVED_ARTIFACT_METADATA_INVALID",
        "expired": "APPROVED_ARTIFACT_EXPIRED",
    }
    for key, expected in expected_metadata.items():
        if metadata.get(key) != expected:
            fail(reason_codes[key], "The approved artifact metadata changed.", field=key)
    try:
        expires = dt.datetime.fromisoformat(APPROVED_ARTIFACT_EXPIRES_AT.replace("Z", "+00:00"))
    except ValueError:
        fail("APPROVED_ARTIFACT_METADATA_INVALID", "The approved artifact expiration is invalid.")
    current = now or dt.datetime.now(dt.timezone.utc)
    if expires <= current:
        fail("APPROVED_ARTIFACT_EXPIRED", "The approved artifact has passed its expiration time.")
    workflow_run = metadata.get("workflow_run")
    if not isinstance(workflow_run, dict) or str(workflow_run.get("id")) != APPROVED_WORKFLOW_RUN_ID:
        fail("APPROVED_ARTIFACT_RUN_MISMATCH", "The approved artifact belongs to another run.")
    if workflow_run.get("head_sha") != APPROVED_EVIDENCE_MAIN_SHA:
        fail("APPROVED_ARTIFACT_SOURCE_SHA_MISMATCH", "The approved artifact belongs to another SHA.")
    if not zip_path.is_file() or zip_path.is_symlink() or _has_reparse_point(zip_path):
        fail("APPROVED_ARTIFACT_INACCESSIBLE", "The approved artifact ZIP is unavailable or linked.")
    if zip_path.stat().st_size != APPROVED_ARTIFACT_ZIP_SIZE:
        fail("APPROVED_ARTIFACT_ZIP_SIZE_MISMATCH", "The downloaded artifact ZIP size changed.")
    actual_digest = f"sha256:{sha256_file(zip_path)}"
    if actual_digest != APPROVED_ARTIFACT_DIGEST:
        fail("APPROVED_ARTIFACT_ZIP_DIGEST_MISMATCH", "The downloaded artifact digest does not match GitHub.")
    file_bytes: dict[str, bytes] = {}
    documents: dict[str, Any] = {}
    try:
        with zipfile.ZipFile(zip_path) as archive:
            entries = archive.infolist()
            if (
                len(entries) != len(APPROVED_SNAPSHOT_FILES)
                or sorted(item.filename for item in entries) != sorted(APPROVED_SNAPSHOT_FILES)
                or any(item.is_dir() for item in entries)
                or any(((item.external_attr >> 16) & 0o170000) == 0o120000 for item in entries)
            ):
                fail("APPROVED_ARTIFACT_CONTENT_INVALID", "The approved artifact does not contain exactly four regular JSON files.")
            for item in entries:
                raw = archive.read(item)
                expected = APPROVED_SNAPSHOT_FILES[item.filename]
                if len(raw) != expected["size_bytes"]:
                    fail("APPROVED_ARTIFACT_FILE_SIZE_MISMATCH", "An approved artifact file size changed.", file=item.filename)
                if hashlib.sha256(raw).hexdigest() != expected["sha256"]:
                    fail("APPROVED_ARTIFACT_FILE_HASH_MISMATCH", "An approved artifact file hash changed.", file=item.filename)
                file_bytes[item.filename] = raw
                documents[item.filename] = json.loads(raw.decode("utf-8"))
    except ValidationFailure:
        raise
    except (OSError, zipfile.BadZipFile, UnicodeDecodeError, json.JSONDecodeError) as exc:
        fail("APPROVED_ARTIFACT_CONTENT_INVALID", "The approved artifact content is invalid.", error=str(exc))
    result = _validate_historical_approved_documents(documents)
    result["file_bytes"] = file_bytes
    return result


def approved_snapshot_manifest() -> dict[str, Any]:
    return {
        "format_version": APPROVED_SNAPSHOT_FORMAT,
        "decision_id": OWNER_DECISION_ID,
        "release": "MVP-0_INTERNAL",
        "capture": {
            "source": "GITHUB_ACTIONS_ARTIFACT",
            "artifact_id": APPROVED_ARTIFACT_ID,
            "artifact_name": APPROVED_ARTIFACT_NAME,
            "artifact_zip_size_bytes": APPROVED_ARTIFACT_ZIP_SIZE,
            "artifact_zip_sha256": APPROVED_ARTIFACT_DIGEST.removeprefix("sha256:"),
            "workflow_run_id": APPROVED_WORKFLOW_RUN_ID,
            "workflow_run_attempt": APPROVED_WORKFLOW_RUN_ATTEMPT,
            "source_sha": APPROVED_EVIDENCE_MAIN_SHA,
            "created_at": APPROVED_ARTIFACT_CREATED_AT,
            "original_expires_at": APPROVED_ARTIFACT_EXPIRES_AT,
            "captured_while_live": True,
        },
        "files": [
            {"name": name, **APPROVED_SNAPSHOT_FILES[name]}
            for name in sorted(APPROVED_SNAPSHOT_FILES)
        ],
    }


def capture_approved_evidence(
    metadata_path: Path,
    zip_path: Path,
    output_directory: Path,
    *,
    now: dt.datetime | None = None,
) -> dict[str, Any]:
    if output_directory.exists() or output_directory.is_symlink() or _has_reparse_point(output_directory):
        fail("APPROVED_SNAPSHOT_OUTPUT_EXISTS", "The capture output must not preexist.")
    parent = output_directory.parent
    if not parent.is_dir() or parent.is_symlink() or _has_reparse_point(parent):
        fail("APPROVED_SNAPSHOT_OUTPUT_PATH_INVALID", "The capture output parent must be a regular directory.")
    metadata = load_json(metadata_path)
    validated = validate_approved_artifact(metadata, zip_path, now=now)
    created = False
    try:
        output_directory.mkdir(parents=False, exist_ok=False)
        created = True
        for name in sorted(APPROVED_SNAPSHOT_FILES):
            (output_directory / name).write_bytes(validated["file_bytes"][name])
        manifest = approved_snapshot_manifest()
        write_json(output_directory / "approved-evidence-manifest.json", manifest)
        return manifest
    except BaseException:
        if created and output_directory.is_dir() and not output_directory.is_symlink() and not _has_reparse_point(output_directory):
            shutil.rmtree(output_directory)
        raise


def _git_bytes_at_commit(
    repository_root: Path,
    commit_sha: str,
    relative_path: str,
) -> bytes:
    result = subprocess.run(
        ["git", "-C", str(repository_root), "show", f"{commit_sha}:{relative_path}"],
        check=False,
        capture_output=True,
    )
    if result.returncode:
        fail("APPROVED_SNAPSHOT_NOT_VERSIONED", "The approved snapshot is missing from the source HEAD.", path=relative_path)
    return result.stdout


def validate_approved_evidence_snapshot(
    repository_root: Path,
    manifest_path: Path,
    snapshot_directory: Path,
    source_head_sha: str,
    *,
    versioned_bytes_loader: Callable[[str], bytes] | None = None,
) -> dict[str, Any]:
    expected_directory = (repository_root / APPROVED_SNAPSHOT_DIRECTORY).resolve()
    expected_manifest = (repository_root / APPROVED_SNAPSHOT_MANIFEST_PATH).resolve()
    try:
        actual_directory = snapshot_directory.resolve(strict=True)
        actual_manifest = manifest_path.resolve(strict=True)
    except OSError:
        fail("APPROVED_SNAPSHOT_MISSING", "The approved evidence snapshot or manifest is missing.")
    if actual_directory != expected_directory or actual_manifest != expected_manifest:
        fail("APPROVED_SNAPSHOT_PATH_INVALID", "The approved snapshot must use its canonical versioned paths.")
    if (
        not actual_directory.is_dir()
        or actual_directory.is_symlink()
        or _has_reparse_point(actual_directory)
        or not actual_manifest.is_file()
        or actual_manifest.is_symlink()
        or _has_reparse_point(actual_manifest)
    ):
        fail("APPROVED_SNAPSHOT_LINK_REJECTED", "The approved snapshot cannot contain links or reparse points.")
    expected_names = {"approved-evidence-manifest.json", *APPROVED_SNAPSHOT_FILES}
    entries = list(actual_directory.iterdir())
    if {entry.name for entry in entries} != expected_names or len(entries) != len(expected_names):
        fail("APPROVED_SNAPSHOT_FILE_LIST_INVALID", "The approved snapshot file allowlist changed.")
    if any(not entry.is_file() or entry.is_symlink() or _has_reparse_point(entry) for entry in entries):
        fail("APPROVED_SNAPSHOT_LINK_REJECTED", "Every approved snapshot entry must be a regular file.")
    try:
        manifest = json.loads(actual_manifest.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        fail("APPROVED_SNAPSHOT_MANIFEST_INVALID", "The approved snapshot manifest is invalid JSON.", error=str(exc))
    if manifest != approved_snapshot_manifest():
        fail("APPROVED_SNAPSHOT_MANIFEST_INVALID", "The approved snapshot manifest does not match its immutable anchor.")
    documents: dict[str, Any] = {}
    snapshot_bytes: dict[str, bytes] = {}
    for name, expected in APPROVED_SNAPSHOT_FILES.items():
        path = actual_directory / name
        try:
            raw = path.read_bytes()
        except OSError as exc:
            fail("APPROVED_SNAPSHOT_FILE_MISSING", "An approved snapshot file is missing.", file=name, error=str(exc))
        if len(raw) != expected["size_bytes"]:
            fail("APPROVED_SNAPSHOT_FILE_SIZE_MISMATCH", "An approved snapshot file size changed.", file=name)
        if hashlib.sha256(raw).hexdigest() != expected["sha256"]:
            fail("APPROVED_SNAPSHOT_FILE_HASH_MISMATCH", "An approved snapshot file hash changed.", file=name)
        try:
            documents[name] = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            fail("APPROVED_SNAPSHOT_JSON_INVALID", "An approved snapshot file is invalid JSON.", file=name, error=str(exc))
        snapshot_bytes[f"{APPROVED_SNAPSHOT_DIRECTORY}/{name}"] = raw
    snapshot_bytes[APPROVED_SNAPSHOT_MANIFEST_PATH] = actual_manifest.read_bytes()
    loader = versioned_bytes_loader or (
        lambda relative: _git_bytes_at_commit(repository_root, source_head_sha, relative)
    )
    for relative, raw in snapshot_bytes.items():
        if loader(relative) != raw:
            fail("APPROVED_SNAPSHOT_NOT_VERSIONED", "The approved snapshot differs from the source HEAD.", path=relative)
    result = _validate_historical_approved_documents(documents)
    result.update(
        {
            "storage": "VERSIONED_REDACTED_SNAPSHOT",
            "manifest_path": APPROVED_SNAPSHOT_MANIFEST_PATH,
            "snapshot_file_count": 4,
            "live_artifact_required": False,
            "original_expires_at": APPROVED_ARTIFACT_EXPIRES_AT,
        }
    )
    return result


def capture_approved_evidence_command(args: argparse.Namespace) -> None:
    manifest = capture_approved_evidence(
        args.metadata,
        args.artifact_zip,
        args.output_directory,
    )
    print(json.dumps({"result": "REL000_APPROVED_EVIDENCE_CAPTURED", "manifest": manifest}, sort_keys=True))


def validate_owner_approval_issues(issue5: dict[str, Any], issue30: dict[str, Any]) -> None:
    if issue5.get("state") != "CLOSED":
        fail("OWNER_APPROVAL_ISSUE_5_OPEN", "Issue #5 must be closed before approval.")
    if issue30.get("state") != "CLOSED":
        fail("OWNER_APPROVAL_ISSUE_30_OPEN", "Issue #30 must be closed before approval.")


def validate_owner_approval(
    repository_root: Path,
    decision_record_path: Path,
    approved_evidence_manifest_path: Path,
    approved_evidence_directory: Path,
    issue5: dict[str, Any],
    issue30: dict[str, Any],
    source_head_sha: str,
    authorization: dict[str, Any] | None = None,
) -> dict[str, Any]:
    expected_path = (repository_root / OWNER_DECISION_PATH).resolve()
    try:
        actual_path = decision_record_path.resolve(strict=True)
    except OSError:
        fail("OWNER_DECISION_RECORD_MISSING", "The versioned owner decision record is missing.")
    if actual_path != expected_path:
        fail("OWNER_DECISION_PATH_INVALID", "Approval must come from the canonical versioned decision record.")
    record = validate_owner_decision_record(load_owner_decision(actual_path))
    record_blob, record_content = _git_file_at_commit(
        repository_root,
        source_head_sha,
        OWNER_DECISION_PATH,
        "OWNER_DECISION_RECORD_NOT_VERSIONED",
    )
    try:
        committed_record = json.loads(record_content)
    except json.JSONDecodeError:
        fail("OWNER_DECISION_RECORD_NOT_VERSIONED", "The committed owner decision record is invalid.")
    if committed_record != record:
        fail("OWNER_DECISION_RECORD_NOT_VERSIONED", "The owner decision record differs from the source HEAD.")
    duplicates = []
    for candidate in (repository_root / "docs/releases").glob("*.json"):
        try:
            value = json.loads(candidate.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        if isinstance(value, dict) and value.get("decision_id") == OWNER_DECISION_ID:
            duplicates.append(candidate.resolve())
    if duplicates != [expected_path]:
        fail("OWNER_DECISION_DUPLICATED", "The owner decision ID must exist in exactly one decision record.")
    if authorization is not None and isinstance(authorization.get("issue_advisories"), dict):
        if issue5.get("state") != "CLOSED":
            fail("OWNER_APPROVAL_ISSUE_5_OPEN", "Issue #5 must remain historically closed.")
        if issue30.get("state") not in {"OPEN", "CLOSED"}:
            fail("OWNER_APPROVAL_SECURITY_ISSUE_INVALID", "The remediation Issue has an invalid state.")
        related = issue30.get("related_issue") or {}
        if authorization.get("related_tracked_issue") is not None and (
            not isinstance(related, dict) or related.get("state") not in {"OPEN", "CLOSED"}
        ):
            fail("OWNER_APPROVAL_SECURITY_ISSUE_INVALID", "Issue #40 has an invalid state.")
    else:
        validate_owner_approval_issues(issue5, issue30)
    artifact = validate_approved_evidence_snapshot(
        repository_root,
        approved_evidence_manifest_path,
        approved_evidence_directory,
        source_head_sha,
    )
    ext001 = validate_ext001_state_source(repository_root, record["approved_evidence"]["main_sha"])
    if artifact["technical_artifact_contains_ext001_started"] is not False:
        fail("APPROVED_ARTIFACT_SCHEMA_DRIFT", "The historical artifact unexpectedly claims the EXT-001 state.")
    return {
        "record": record,
        "decision_record_sha": source_head_sha,
        "decision_record_blob_sha": record_blob,
        "artifact": artifact,
        "ext001": ext001,
    }


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
    if source != tested:
        # pull_request shape: the only authoritative base is the first parent of the
        # tested merge commit. A stale event base SHA must not pass as base_main_sha.
        resolved_base = resolve_tested_merge_base(repository_root, tested, source)
        if base != resolved_base:
            fail(
                "TESTED_MERGE_BASE_MISMATCH",
                "base_main_sha is not the first parent of the tested merge commit.",
                declared_base_main_sha=base,
                tested_merge_first_parent=resolved_base,
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
    owner_approval: dict[str, Any] | None = None,
    focused_tests: dict[str, Any] | None = None,
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
        if item_id == "REL-000" and status == "VERIFIED" and owner_approval is None:
            fail(
                "OWNER_APPROVAL_FALSELY_ASSERTED",
                "REL-000 cannot be VERIFIED without a valid versioned owner decision.",
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
            allowed_jobs = REQUIRED_JOBS | ({"rel000"} if item_id == "REL-000" else set())
            unknown_jobs = sorted(set(jobs) - allowed_jobs)
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
                if job != "rel000" and job_results.get(job) != "success"
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
                if item_id == "REL-000":
                    if required != {
                        "job": "rel000",
                        "source_path": "tools/rel-000/test_rel000.py",
                        "test_project": "tools/rel-000/test_rel000.py",
                        "fully_qualified_test_name": "OwnerApprovalDecisionTests",
                        "category": "REL000_OWNER_APPROVAL",
                    }:
                        fail("OWNER_APPROVAL_TEST_BINDING_INVALID", "REL-000 approval tests are not bound exactly.")
                    if not isinstance(focused_tests, dict) or any(
                        focused_tests.get(key) != focused_tests.get("focused_tests_expected")
                        for key in (
                            "focused_tests_discovered",
                            "focused_tests_executed",
                            "focused_tests_passed",
                        )
                    ) or focused_tests.get("focused_tests_failed") != 0 or focused_tests.get("focused_tests_skipped") != 0:
                        fail("OWNER_APPROVAL_TESTS_NOT_PASSED", "The focused owner-approval suite did not pass exactly.")
                    matched_tests.append(
                        {
                            **required,
                            "outcome": "PASSED",
                            "executed": True,
                            "skipped": False,
                            "evidence_source": "REL000_FOCUSED_TEST_SUITE",
                        }
                    )
                else:
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


def parse_positive_attempt(
    value: Any,
    *,
    missing_reason: str,
    invalid_reason: str,
    field: str,
) -> int:
    if value is None or value == "":
        fail(missing_reason, f"{field} is required.", field=field)
    if isinstance(value, bool):
        fail(invalid_reason, f"{field} must be a positive integer.", field=field)
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        fail(invalid_reason, f"{field} must be a positive integer.", field=field)
    if parsed <= 0 or str(parsed) != str(value).strip():
        fail(invalid_reason, f"{field} must be a positive integer.", field=field)
    return parsed


def parse_github_timestamp(value: Any, field: str) -> dt.datetime:
    if not isinstance(value, str) or not value:
        fail("WORKFLOW_PROVENANCE_METADATA_INCOMPLETE", "GitHub job metadata is incomplete.", field=field)
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        fail(
            "WORKFLOW_PROVENANCE_METADATA_INVALID",
            "GitHub job metadata contains an invalid timestamp.",
            field=field,
            error=str(exc),
        )
    if parsed.tzinfo is None:
        fail("WORKFLOW_PROVENANCE_METADATA_INVALID", "GitHub timestamps must include a timezone.", field=field)
    return parsed


def load_workflow_json(path: Path, missing_reason: str, invalid_reason: str) -> Any:
    if not path.is_file() or path.is_symlink():
        fail(missing_reason, "Required GitHub workflow metadata is absent.", path=path.name)
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        fail(invalid_reason, "GitHub workflow metadata is not parseable.", path=path.name, error=str(exc))


def sanitize_workflow_provenance(
    attempts_directory: Path,
    artifacts_path: Path,
    artifact_outputs_json: str,
    output_path: Path,
    workflow_run_id: str,
    current_attempt_value: Any,
    head_sha_value: str,
    workflow_profile: str = DEFAULT_WORKFLOW_PROVENANCE_PROFILE,
) -> dict[str, Any]:
    profile = resolve_workflow_provenance_profile(workflow_profile)
    current_attempt = parse_positive_attempt(
        current_attempt_value,
        missing_reason="WORKFLOW_PROVENANCE_CURRENT_ATTEMPT_MISSING",
        invalid_reason="WORKFLOW_PROVENANCE_CURRENT_ATTEMPT_INVALID",
        field="current_attempt",
    )
    head_sha = validate_sha(head_sha_value, "WORKFLOW_PROVENANCE_HEAD_SHA_INVALID", "head_sha")
    if not str(workflow_run_id).isdigit() or int(workflow_run_id) <= 0:
        fail("WORKFLOW_PROVENANCE_RUN_ID_INVALID", "The workflow run ID is invalid.")
    if not attempts_directory.is_dir() or attempts_directory.is_symlink():
        fail("WORKFLOW_PROVENANCE_MANIFEST_MISSING", "Workflow attempt metadata is absent.")

    names_to_keys = {name: key for key, name in profile.job_names.items()}
    actual_jobs: list[dict[str, Any]] = []
    actual_by_key: dict[str, list[dict[str, Any]]] = {
        key: [] for key in profile.job_names
    }
    observed_job_ids: set[int] = set()
    for attempt in range(1, current_attempt + 1):
        run_payload = load_workflow_json(
            attempts_directory / f"attempt-{attempt}-run.json",
            "WORKFLOW_PROVENANCE_ATTEMPT_METADATA_MISSING",
            "WORKFLOW_PROVENANCE_METADATA_INVALID",
        )
        if not isinstance(run_payload, dict):
            fail("WORKFLOW_PROVENANCE_METADATA_INVALID", "Workflow attempt metadata must be an object.")
        if str(run_payload.get("id") or "") != str(workflow_run_id):
            fail("WORKFLOW_PROVENANCE_RUN_MISMATCH", "Workflow attempt metadata belongs to another run.")
        raw_attempt = parse_positive_attempt(
            run_payload.get("run_attempt"),
            missing_reason="WORKFLOW_PROVENANCE_ATTEMPT_METADATA_INCOMPLETE",
            invalid_reason="WORKFLOW_PROVENANCE_ATTEMPT_METADATA_INVALID",
            field="run_attempt",
        )
        if raw_attempt != attempt:
            fail("WORKFLOW_PROVENANCE_ATTEMPT_METADATA_INVALID", "Workflow attempt metadata is out of sequence.")
        if str(run_payload.get("head_sha") or "") != head_sha:
            fail("WORKFLOW_PROVENANCE_HEAD_SHA_MISMATCH", "Workflow attempt metadata belongs to another SHA.")
        validate_workflow_run_identity(profile, run_payload, attempt)
        attempt_started_at = parse_github_timestamp(
            run_payload.get("run_started_at"), "run_started_at"
        )

        jobs_payload = load_workflow_json(
            attempts_directory / f"attempt-{attempt}-jobs.json",
            "WORKFLOW_PROVENANCE_JOBS_MISSING",
            "WORKFLOW_PROVENANCE_METADATA_INVALID",
        )
        jobs = jobs_payload.get("jobs") if isinstance(jobs_payload, dict) else None
        if not isinstance(jobs, list):
            fail("WORKFLOW_PROVENANCE_METADATA_INVALID", "Workflow jobs metadata must contain a jobs array.")
        if not len(profile.observable_jobs) <= len(jobs) <= len(profile.job_names):
            fail(
                "WORKFLOW_PROVENANCE_JOB_SET_INVALID",
                f"{profile.workflow_name} must contain exactly the authoritative job set.",
                expected=len(profile.observable_jobs),
                expected_maximum=len(profile.job_names),
                observed=len(jobs),
                attempt=attempt,
            )
        seen_names: set[str] = set()
        for raw_job in jobs:
            if not isinstance(raw_job, dict):
                fail("WORKFLOW_PROVENANCE_METADATA_INVALID", "A GitHub job record is malformed.")
            job_name = str(raw_job.get("name") or "")
            if job_name not in names_to_keys:
                fail("WORKFLOW_PROVENANCE_JOB_UNKNOWN", "GitHub metadata contains an unknown job.", job_name=job_name)
            if job_name in seen_names:
                fail("WORKFLOW_PROVENANCE_JOB_AMBIGUOUS", "A job name appears more than once in one attempt.", job_name=job_name)
            seen_names.add(job_name)
            raw_job_attempt = parse_positive_attempt(
                raw_job.get("run_attempt"),
                missing_reason="WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_MISSING",
                invalid_reason="WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_INVALID",
                field="run_attempt",
            )
            if raw_job_attempt != attempt:
                fail("WORKFLOW_PROVENANCE_ATTEMPT_METADATA_INVALID", "A job record belongs to another attempt.")
            job_id = raw_job.get("id")
            if not isinstance(job_id, int) or job_id <= 0:
                fail("WORKFLOW_PROVENANCE_JOB_ID_INVALID", "A GitHub job ID is invalid.", job_name=job_name)
            if job_id in observed_job_ids:
                fail("WORKFLOW_PROVENANCE_JOB_ID_REUSED", "A GitHub job ID is reused.", job_id=job_id)
            observed_job_ids.add(job_id)
            started_at_text = str(raw_job.get("started_at") or "")
            completed_at_text = str(raw_job.get("completed_at") or "")
            key = names_to_keys[job_name]
            if key in profile.downstream_jobs and not started_at_text:
                # A downstream job (after the aggregator) has not started while the
                # aggregator runs; it can only be pending in the current attempt.
                if attempt != current_attempt:
                    fail("WORKFLOW_PROVENANCE_METADATA_INCOMPLETE", "GitHub job metadata is incomplete.", field="started_at")
                if completed_at_text or str(raw_job.get("conclusion") or ""):
                    fail("WORKFLOW_PROVENANCE_METADATA_INVALID", "A pending downstream job reports completion.", job_name=job_name)
                if str(raw_job.get("status") or "") not in PENDING_JOB_STATUSES:
                    fail("WORKFLOW_PROVENANCE_METADATA_INVALID", "A downstream job without a start time is not pending.", job_name=job_name)
                started_at = None
            else:
                started_at = parse_github_timestamp(started_at_text, "started_at")
            record = {
                "job_key": key,
                "job_name": job_name,
                "job_id": job_id,
                "run_attempt": attempt,
                "status": str(raw_job.get("status") or ""),
                "conclusion": str(raw_job.get("conclusion") or ""),
                "_started_at": started_at_text,
                "_completed_at": completed_at_text,
            }
            if started_at is None or started_at >= attempt_started_at:
                actual_jobs.append(record)
                actual_by_key[key].append(record)
                continue
            retained_matches = [
                prior
                for prior in actual_by_key[key]
                if prior["_started_at"] == started_at_text
                and prior["_completed_at"] == completed_at_text
                and prior["status"] == record["status"]
                and prior["conclusion"] == record["conclusion"]
            ]
            if len(retained_matches) != 1:
                fail(
                    "WORKFLOW_PROVENANCE_RETAINED_JOB_AMBIGUOUS",
                    "A retained job cannot be tied to one earlier execution.",
                    job=key,
                    attempt=attempt,
                )
        missing_names = profile.observable_job_names - seen_names
        if missing_names:
            fail(
                "WORKFLOW_PROVENANCE_JOB_SET_INVALID",
                "The authoritative job set is incomplete.",
                jobs=sorted(missing_names),
                attempt=attempt,
            )

    latest_jobs: dict[str, dict[str, Any]] = {}
    for key, records in actual_by_key.items():
        if not records:
            if key in profile.downstream_jobs:
                # Not yet created by GitHub because the aggregator is still running.
                continue
            fail("WORKFLOW_PROVENANCE_JOB_MISSING", "An authoritative job has no executed attempt.", job=key)
        latest_jobs[key] = max(records, key=lambda item: item["run_attempt"])
    if latest_jobs[profile.aggregator_job]["run_attempt"] != current_attempt:
        fail("WORKFLOW_PROVENANCE_AGGREGATOR_ATTEMPT_MISMATCH", "The aggregator did not execute in the current attempt.")
    for key in sorted(profile.producer_jobs):
        latest = latest_jobs[key]
        if latest["status"] != "completed" or latest["conclusion"] != "success":
            fail(
                "WORKFLOW_PROVENANCE_PRODUCER_NOT_SUCCESSFUL",
                "The latest executed authoritative producer job did not succeed.",
                job=key,
                producer_attempt=latest["run_attempt"],
                status=latest["status"],
                conclusion=latest["conclusion"],
            )

    artifacts_payload = load_workflow_json(
        artifacts_path,
        "WORKFLOW_PROVENANCE_ARTIFACT_METADATA_MISSING",
        "WORKFLOW_PROVENANCE_ARTIFACT_METADATA_INVALID",
    )
    raw_artifacts = artifacts_payload.get("artifacts") if isinstance(artifacts_payload, dict) else None
    if not isinstance(raw_artifacts, list):
        fail("WORKFLOW_PROVENANCE_ARTIFACT_METADATA_INVALID", "GitHub artifact metadata is invalid.")
    try:
        outputs = json.loads(artifact_outputs_json)
    except json.JSONDecodeError as exc:
        fail("WORKFLOW_PROVENANCE_ARTIFACT_OUTPUTS_INVALID", "Artifact outputs are not parseable.", error=str(exc))
    if not isinstance(outputs, list):
        fail("WORKFLOW_PROVENANCE_ARTIFACT_OUTPUTS_INVALID", "Artifact outputs must be an array.")
    output_by_name: dict[str, dict[str, Any]] = {}
    for output in outputs:
        if not isinstance(output, dict):
            fail("WORKFLOW_PROVENANCE_ARTIFACT_OUTPUTS_INVALID", "An artifact output is malformed.")
        name = str(output.get("artifact_name") or "")
        if name in output_by_name:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_AMBIGUOUS", "Artifact outputs contain duplicate names.", artifact=name)
        output_by_name[name] = output
    if set(output_by_name) != set(profile.artifact_names.values()):
        fail("WORKFLOW_PROVENANCE_ARTIFACT_SET_INVALID", "Artifact outputs do not match the authoritative set.")

    sanitized_artifacts: list[dict[str, Any]] = []
    selected_ids: set[int] = set()
    for job_key, expected_name in profile.artifact_names.items():
        output = output_by_name[expected_name]
        output_id = str(output.get("artifact_id") or "")
        if not output_id.isdigit() or int(output_id) <= 0:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_ID_INVALID", "An artifact output ID is invalid.", job=job_key)
        artifact_id = int(output_id)
        if artifact_id in selected_ids:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_REUSED", "One artifact ID cannot satisfy two jobs.", artifact_id=artifact_id)
        selected_ids.add(artifact_id)
        output_digest = normalized_sha256(
            str(output.get("artifact_digest") or ""),
            "WORKFLOW_PROVENANCE_ARTIFACT_DIGEST_INVALID",
            "artifact_digest",
        )
        candidates = [item for item in raw_artifacts if isinstance(item, dict) and item.get("id") == artifact_id]
        if len(candidates) != 1:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_AMBIGUOUS", "Artifact metadata does not identify one candidate.", artifact_id=artifact_id)
        artifact = candidates[0]
        if str(artifact.get("name") or "") != expected_name:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_NAME_MISMATCH", "Artifact metadata has an unexpected name.", job=job_key)
        if artifact.get("expired") is True:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_EXPIRED", "An authoritative artifact is expired.", job=job_key)
        metadata_digest = normalized_sha256(
            str(artifact.get("digest") or ""),
            "WORKFLOW_PROVENANCE_ARTIFACT_DIGEST_INVALID",
            "artifact_digest",
        )
        if metadata_digest != output_digest:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_DIGEST_MISMATCH", "Artifact output and GitHub metadata digests differ.", job=job_key)
        artifact_run = artifact.get("workflow_run")
        if not isinstance(artifact_run, dict):
            fail("WORKFLOW_PROVENANCE_ARTIFACT_METADATA_INCOMPLETE", "Artifact workflow metadata is absent.", job=job_key)
        if str(artifact_run.get("id") or "") != str(workflow_run_id):
            fail("WORKFLOW_PROVENANCE_ARTIFACT_RUN_MISMATCH", "An artifact belongs to another workflow run.", job=job_key)
        if str(artifact_run.get("head_sha") or "") != head_sha:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_SHA_MISMATCH", "An artifact belongs to another source SHA.", job=job_key)
        producer = latest_jobs[job_key]
        sanitized_artifacts.append(
            {
                "job_key": job_key,
                "artifact_name": expected_name,
                "artifact_id": artifact_id,
                "artifact_digest": f"sha256:{metadata_digest}",
                "producer_attempt": producer["run_attempt"],
                "producer_job_id": producer["job_id"],
                "workflow_run_id": str(workflow_run_id),
                "head_sha": head_sha,
            }
        )

    sanitized_jobs = [
        {key: value for key, value in record.items() if not key.startswith("_")}
        for record in sorted(actual_jobs, key=lambda item: (item["run_attempt"], item["job_key"]))
    ]
    manifest = {
        "format_version": WORKFLOW_PROVENANCE_VERSION,
        "workflow_profile": profile.name,
        "workflow_run_id": str(workflow_run_id),
        "head_sha": head_sha,
        "current_attempt": current_attempt,
        "jobs": sanitized_jobs,
        "artifacts": sorted(sanitized_artifacts, key=lambda item: item["job_key"]),
    }
    write_json(output_path, manifest)
    return manifest


def validate_workflow_provenance(
    manifest_path: Path,
    trace: dict[str, str],
    workflow_run_id: str,
    current_attempt_value: Any,
) -> dict[str, Any]:
    payload = load_workflow_json(
        manifest_path,
        "WORKFLOW_PROVENANCE_MANIFEST_MISSING",
        "WORKFLOW_PROVENANCE_MANIFEST_INVALID",
    )
    if not isinstance(payload, dict) or payload.get("format_version") != WORKFLOW_PROVENANCE_VERSION:
        fail("WORKFLOW_PROVENANCE_MANIFEST_INVALID", "The workflow provenance format is invalid.")
    # Manifests written before profiles existed are Foundation manifests.
    profile = resolve_workflow_provenance_profile(
        payload.get("workflow_profile", DEFAULT_WORKFLOW_PROVENANCE_PROFILE)
    )
    current_attempt = parse_positive_attempt(
        current_attempt_value,
        missing_reason="WORKFLOW_PROVENANCE_CURRENT_ATTEMPT_MISSING",
        invalid_reason="WORKFLOW_PROVENANCE_CURRENT_ATTEMPT_INVALID",
        field="current_attempt",
    )
    manifest_attempt = parse_positive_attempt(
        payload.get("current_attempt"),
        missing_reason="WORKFLOW_PROVENANCE_CURRENT_ATTEMPT_MISSING",
        invalid_reason="WORKFLOW_PROVENANCE_CURRENT_ATTEMPT_INVALID",
        field="current_attempt",
    )
    if manifest_attempt != current_attempt:
        fail("WORKFLOW_PROVENANCE_CURRENT_ATTEMPT_MISMATCH", "The provenance manifest targets another aggregator attempt.")
    if str(payload.get("workflow_run_id") or "") != str(workflow_run_id):
        fail("WORKFLOW_PROVENANCE_RUN_MISMATCH", "The provenance manifest belongs to another run.")
    if str(payload.get("head_sha") or "") != trace["source_head_sha"]:
        fail("WORKFLOW_PROVENANCE_HEAD_SHA_MISMATCH", "The provenance manifest belongs to another source SHA.")

    jobs = payload.get("jobs")
    if not isinstance(jobs, list):
        fail("WORKFLOW_PROVENANCE_MANIFEST_INCOMPLETE", "The provenance manifest has no jobs array.")
    by_key: dict[str, list[dict[str, Any]]] = {key: [] for key in profile.job_names}
    seen_job_attempts: set[tuple[str, int]] = set()
    seen_job_ids: set[int] = set()
    for job in jobs:
        if not isinstance(job, dict):
            fail("WORKFLOW_PROVENANCE_JOB_INVALID", "A provenance job is malformed.")
        key = str(job.get("job_key") or "")
        if key not in profile.job_names:
            fail("WORKFLOW_PROVENANCE_JOB_UNKNOWN", "The provenance manifest contains an unknown job.", job=key)
        if str(job.get("job_name") or "") != profile.job_names[key]:
            fail("WORKFLOW_PROVENANCE_JOB_NAME_MISMATCH", "A job key is associated with the wrong name.", job=key)
        attempt = parse_positive_attempt(
            job.get("run_attempt"),
            missing_reason="WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_MISSING",
            invalid_reason="WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_INVALID",
            field="run_attempt",
        )
        if attempt > current_attempt:
            fail("WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_FUTURE", "A producer attempt is in the future.", job=key)
        identity = (key, attempt)
        if identity in seen_job_attempts:
            fail("WORKFLOW_PROVENANCE_JOB_AMBIGUOUS", "A job has multiple executions in one attempt.", job=key, attempt=attempt)
        seen_job_attempts.add(identity)
        job_id = job.get("job_id")
        if not isinstance(job_id, int) or job_id <= 0:
            fail("WORKFLOW_PROVENANCE_JOB_ID_INVALID", "A provenance job ID is invalid.", job=key)
        if job_id in seen_job_ids:
            fail("WORKFLOW_PROVENANCE_JOB_ID_REUSED", "A provenance job ID is reused.", job_id=job_id)
        seen_job_ids.add(job_id)
        normalized = {**job, "run_attempt": attempt}
        by_key[key].append(normalized)
    latest_jobs: dict[str, dict[str, Any]] = {}
    for key, records in by_key.items():
        if not records:
            if key in profile.downstream_jobs:
                # The aggregator sanitized provenance before GitHub created this
                # downstream job record; its absence is the expected topology.
                continue
            fail("WORKFLOW_PROVENANCE_JOB_MISSING", "An authoritative job is absent.", job=key)
        latest_jobs[key] = max(records, key=lambda item: item["run_attempt"])
    if latest_jobs[profile.aggregator_job]["run_attempt"] != current_attempt:
        fail("WORKFLOW_PROVENANCE_AGGREGATOR_ATTEMPT_MISMATCH", "The current aggregator execution is absent.")
    for key in sorted(profile.producer_jobs):
        latest = latest_jobs[key]
        if latest.get("status") != "completed" or latest.get("conclusion") != "success":
            fail(
                "WORKFLOW_PROVENANCE_PRODUCER_NOT_SUCCESSFUL",
                "The latest producer execution is not successful.",
                job=key,
                producer_attempt=latest["run_attempt"],
            )

    artifacts = payload.get("artifacts")
    if not isinstance(artifacts, list):
        fail("WORKFLOW_PROVENANCE_MANIFEST_INCOMPLETE", "The provenance manifest has no artifacts array.")
    artifacts_by_job: dict[str, dict[str, Any]] = {}
    seen_artifact_ids: set[int] = set()
    for artifact in artifacts:
        if not isinstance(artifact, dict):
            fail("WORKFLOW_PROVENANCE_ARTIFACT_INVALID", "A provenance artifact is malformed.")
        job_key = str(artifact.get("job_key") or "")
        if job_key not in profile.artifact_names:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_JOB_UNKNOWN", "An artifact references an unknown producer.", job=job_key)
        if job_key in artifacts_by_job:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_AMBIGUOUS", "A producer has multiple eligible artifacts.", job=job_key)
        expected_name = profile.artifact_names[job_key]
        if str(artifact.get("artifact_name") or "") != expected_name:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_NAME_MISMATCH", "An artifact name does not match its producer.", job=job_key)
        artifact_id = artifact.get("artifact_id")
        if not isinstance(artifact_id, int) or artifact_id <= 0:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_ID_INVALID", "An artifact ID is invalid.", job=job_key)
        if artifact_id in seen_artifact_ids:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_REUSED", "One artifact cannot satisfy two producers.", artifact_id=artifact_id)
        seen_artifact_ids.add(artifact_id)
        digest = normalized_sha256(
            str(artifact.get("artifact_digest") or ""),
            "WORKFLOW_PROVENANCE_ARTIFACT_DIGEST_INVALID",
            "artifact_digest",
        )
        producer_attempt = parse_positive_attempt(
            artifact.get("producer_attempt"),
            missing_reason="WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_MISSING",
            invalid_reason="WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_INVALID",
            field="producer_attempt",
        )
        if producer_attempt > current_attempt:
            fail("WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_FUTURE", "An artifact producer attempt is in the future.", job=job_key)
        producer_matches = [job for job in by_key[job_key] if job["run_attempt"] == producer_attempt]
        if len(producer_matches) != 1:
            fail("WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_UNKNOWN", "An artifact producer attempt is absent from job metadata.", job=job_key)
        latest = latest_jobs[job_key]
        if producer_attempt < latest["run_attempt"]:
            fail(
                "WORKFLOW_PROVENANCE_ARTIFACT_STALE_AFTER_RERUN",
                "An artifact predates the latest executed producer job.",
                job=job_key,
                producer_attempt=producer_attempt,
                latest_attempt=latest["run_attempt"],
            )
        if producer_attempt != latest["run_attempt"]:
            fail("WORKFLOW_PROVENANCE_PRODUCER_ATTEMPT_MISMATCH", "An artifact does not match the latest producer attempt.", job=job_key)
        if artifact.get("producer_job_id") != latest["job_id"]:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_JOB_MISMATCH", "An artifact is associated with the wrong producer job ID.", job=job_key)
        if str(artifact.get("workflow_run_id") or "") != str(workflow_run_id):
            fail("WORKFLOW_PROVENANCE_ARTIFACT_RUN_MISMATCH", "An artifact belongs to another workflow run.", job=job_key)
        if str(artifact.get("head_sha") or "") != trace["source_head_sha"]:
            fail("WORKFLOW_PROVENANCE_ARTIFACT_SHA_MISMATCH", "An artifact belongs to another source SHA.", job=job_key)
        artifacts_by_job[job_key] = {
            **artifact,
            "artifact_digest": digest,
            "producer_attempt": producer_attempt,
        }
    missing_artifacts = sorted(set(profile.artifact_names) - set(artifacts_by_job))
    if missing_artifacts:
        fail("WORKFLOW_PROVENANCE_ARTIFACT_MISSING", "Authoritative artifacts are missing.", jobs=missing_artifacts)

    attempts = {artifact["producer_attempt"] for artifact in artifacts_by_job.values()}
    report = {
        "workflow_profile": profile.name,
        "workflow_run_id": str(workflow_run_id),
        "aggregator_attempt": current_attempt,
        "mixed_attempt_evidence": len(attempts) > 1,
        "jobs": [
            {
                "job": key,
                "producer_attempt": artifact["producer_attempt"],
                "artifact_id": artifact["artifact_id"],
                "artifact_digest": f"sha256:{artifact['artifact_digest']}",
            }
            for key, artifact in sorted(artifacts_by_job.items())
        ],
    }
    return {
        "workflow_profile": profile.name,
        "latest_jobs": latest_jobs,
        "artifacts_by_job": artifacts_by_job,
        "report": report,
    }


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
    workflow_provenance_path: Path,
    trace: dict[str, str],
    workflow_run_id: str,
    workflow_run_attempt: str,
) -> tuple[list[dict[str, Any]], dict[str, dict[str, Any]], dict[str, Any]]:
    if not directory.is_dir():
        fail("EXECUTION_ARTIFACT_MISSING", "The execution artifact directory is absent.")
    provenance = validate_workflow_provenance(
        workflow_provenance_path,
        trace,
        workflow_run_id,
        workflow_run_attempt,
    )
    metadata_by_job = provenance["artifacts_by_job"]

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
        job = str(manifest.get("job") or "")
        artifact_name = str(manifest.get("artifact_name") or "")
        if job not in EXECUTION_EVIDENCE_JOBS or job in artifacts_by_job:
            fail("EXECUTION_JOB_INVALID", "Execution evidence has an unknown or duplicate job.", job=job)
        artifact = metadata_by_job.get(job)
        if artifact is None:
            fail(
                "EXECUTION_ARTIFACT_METADATA_MISSING",
                "Execution artifact metadata is absent.",
                job=job,
            )
        if artifact_name != EXECUTION_ARTIFACT_NAMES[job] or artifact_name != artifact["artifact_name"]:
            fail(
                "EXECUTION_ARTIFACT_NAME_MISMATCH",
                "Execution evidence declares an artifact that does not belong to its producer.",
                job=job,
            )
        manifest_attempt = parse_positive_attempt(
            manifest.get("workflow_run_attempt"),
            missing_reason="EXECUTION_PRODUCER_ATTEMPT_MISSING",
            invalid_reason="EXECUTION_PRODUCER_ATTEMPT_INVALID",
            field="workflow_run_attempt",
        )
        producer_attempt = artifact["producer_attempt"]
        if manifest_attempt < producer_attempt:
            fail(
                "EXECUTION_ARTIFACT_STALE_AFTER_RERUN",
                "Execution evidence predates the latest producer execution.",
                job=job,
                producer_attempt=manifest_attempt,
                latest_attempt=producer_attempt,
            )
        if manifest_attempt != producer_attempt:
            fail(
                "EXECUTION_PRODUCER_ATTEMPT_MISMATCH",
                "Execution evidence does not match its eligible producer attempt.",
                job=job,
                expected=producer_attempt,
                observed=manifest_attempt,
            )
        expected_fields = {
            "format_version": EXECUTION_EVIDENCE_VERSION,
            "workflow_run_id": str(workflow_run_id),
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
                "workflow_run_attempt": str(producer_attempt),
            }
            validate_execution_context(
                enriched,
                trace,
                workflow_run_id,
                str(producer_attempt),
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
            "workflow_run_attempt": str(producer_attempt),
            "producer_attempt": producer_attempt,
            "producer_job_id": artifact["producer_job_id"],
        }

    missing_jobs = sorted(EXECUTION_EVIDENCE_JOBS - set(artifacts_by_job))
    if missing_jobs:
        fail(
            "EXECUTION_ARTIFACT_MISSING",
            "Required jobs did not publish structured execution evidence.",
            jobs=missing_jobs,
        )
    return tests, artifacts_by_job, provenance["report"]


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


def _lock_package_keys(lock_text: str) -> set[str]:
    keys: set[str] = set()
    in_packages = False
    for line in lock_text.splitlines():
        if line == "packages:":
            in_packages = True
            continue
        if line == "snapshots:":
            break
        if not in_packages:
            continue
        match = re.fullmatch(r"  (\S.*):", line)
        if match:
            keys.add(match.group(1).strip("'\""))
    return keys


def _lock_package_name(key: str) -> str:
    if key.startswith("@"):
        return key.rsplit("@", 1)[0]
    return key.split("@", 1)[0]


def _lock_package_integrity(lock_text: str, package: str, version: str) -> str | None:
    match = re.search(
        rf"^  {re.escape(package)}@{re.escape(version)}:\r?\n"
        rf"\s+resolution:\s+\{{integrity:\s*([^}}]+)\}}",
        lock_text,
        re.MULTILINE,
    )
    return match.group(1).strip() if match else None


def _workspace_overrides(value: str) -> dict[str, str]:
    overrides: dict[str, str] = {}
    in_overrides = False
    for line in value.splitlines():
        if line == "overrides:":
            in_overrides = True
            continue
        if not in_overrides:
            continue
        if line and not line.startswith("  "):
            break
        match = re.fullmatch(r'  ["\']?(.+?)["\']?:\s*([^\s#]+)\s*', line)
        if match:
            overrides[match.group(1)] = match.group(2)
    return overrides


def _manifest_direct_versions(package: dict[str, Any]) -> dict[str, str]:
    dependencies = package.get("dependencies") or {}
    development = package.get("devDependencies") or {}
    return {
        "next": dependencies.get("next"),
        "eslint-config-next": development.get("eslint-config-next"),
        "react": dependencies.get("react"),
        "react-dom": dependencies.get("react-dom"),
    }


def validate_sharp_remediation_diff(
    repository_root: Path,
    base_main_sha: str,
    all_changed: list[str],
    changed_dependency_files: list[str],
    authorization: dict[str, Any],
) -> dict[str, Any]:
    allowed_all = set(authorization["allowed_dependency_files"]) | set(
        authorization["allowed_non_dependency_files"]
    )
    unexpected = sorted(set(all_changed) - allowed_all)
    if unexpected:
        fail(
            "SECURITY_REMEDIATION_FILE_SCOPE_INVALID",
            "The remediation changed a file outside its exact authorization.",
            files=unexpected,
        )
    required = set(authorization["required_dependency_files"])
    if set(changed_dependency_files) != required:
        fail(
            "SECURITY_REMEDIATION_DEPENDENCY_SCOPE_INVALID",
            "The remediation dependency files do not match the exact authorization.",
            expected=sorted(required),
            actual=changed_dependency_files,
        )

    package_path = repository_root / "apps/web/package.json"
    current_package = load_json(package_path)
    try:
        base_package = json.loads(
            run_git(repository_root, "show", f"{base_main_sha}:apps/web/package.json")
        )
    except json.JSONDecodeError as exc:
        fail("BASE_DEPENDENCY_MANIFEST_INVALID", "The base package manifest is invalid.", error=str(exc))
    direct_versions = _manifest_direct_versions(current_package)
    if any(
        "sharp" in (current_package.get(section) or {})
        for section in ("dependencies", "devDependencies", "optionalDependencies", "peerDependencies")
    ):
        fail("DIRECT_SHARP_DEPENDENCY_REJECTED", "Sharp must remain transitive and optional.")
    next_version = direct_versions["next"]
    eslint_next_version = direct_versions["eslint-config-next"]
    if _semver_tuple(str(next_version)) is None or _semver_tuple(str(eslint_next_version)) is None:
        fail("PRERELEASE_DEPENDENCY_REJECTED", "Prerelease Next.js dependencies are forbidden.")
    if next_version != eslint_next_version:
        fail(
            "NEXT_DEPENDENCY_ALIGNMENT_INVALID",
            "Next.js and eslint-config-next must remain aligned stable versions.",
        )
    if current_package != base_package:
        fail(
            "UNAUTHORIZED_DEPENDENCY_MANIFEST_CHANGE",
            "The Sharp remediation does not authorize direct package changes.",
        )
    if direct_versions != authorization["expected_direct_versions"]:
        fail(
            "DIRECT_DEPENDENCY_VERSION_INVALID",
            "The direct web dependency versions drifted from the authorization.",
            expected=authorization["expected_direct_versions"],
            actual=direct_versions,
        )

    workspace_path = repository_root / "apps/web/pnpm-workspace.yaml"
    current_workspace = workspace_path.read_text(encoding="utf-8")
    base_workspace = run_git(
        repository_root, "show", f"{base_main_sha}:apps/web/pnpm-workspace.yaml"
    )
    base_overrides = _workspace_overrides(base_workspace)
    current_overrides = _workspace_overrides(current_workspace)
    allowed_overrides = authorization.get("allowed_overrides") or {}
    if not isinstance(allowed_overrides, dict) or current_overrides != {
        **base_overrides,
        **allowed_overrides,
    }:
        fail(
            "SECURITY_REMEDIATION_OVERRIDE_INVALID",
            "Only the exact authorized Sharp dependency-edge override may be added.",
            expected={**base_overrides, **allowed_overrides},
            actual=current_overrides,
        )
    target_sharp = authorization.get("target_sharp_version")
    if _semver_tuple(str(target_sharp)) is None or _semver_tuple(str(target_sharp)) < (0, 35, 0):
        fail("SHARP_TARGET_VERSION_INVALID", "The authorized Sharp target must be stable and patched.")
    if allowed_overrides != {f"next@{next_version}>sharp": target_sharp}:
        fail(
            "SHARP_OVERRIDE_SELECTOR_INVALID",
            "The Sharp override must target only the selected Next.js dependency edge.",
        )

    lock_text = (repository_root / "apps/web/pnpm-lock.yaml").read_text(encoding="utf-8")
    sharp_versions = _lock_versions(lock_text, "sharp")
    if sharp_versions != {target_sharp}:
        fail(
            "SHARP_LOCK_VERSION_INVALID",
            "The lockfile must contain exactly one authorized patched Sharp version.",
            versions=sorted(sharp_versions),
        )
    if f"{next_version}>sharp: {target_sharp}" not in lock_text or not re.search(
        rf"^\s+sharp:\s+{re.escape(target_sharp)}(?:\(|$)", lock_text, re.MULTILINE
    ):
        fail("LOCKFILE_INCONSISTENT", "The lockfile does not apply the authorized Sharp edge.")
    if re.search(r"^\s{6}sharp:\s*$", lock_text, re.MULTILINE):
        fail("DIRECT_SHARP_DEPENDENCY_REJECTED", "Sharp appeared in the direct importer.")
    prerelease = sorted(version for version in sharp_versions if _semver_tuple(version) is None)
    if prerelease:
        fail("PRERELEASE_DEPENDENCY_REJECTED", "Prerelease Sharp versions are forbidden.")

    for path in all_changed:
        if not path.startswith("apps/web/") or path in authorization["allowed_dependency_files"]:
            continue
        added = [
            line[1:]
            for line in run_git(
                repository_root, "diff", "--unified=0", base_main_sha, "--", path
            ).splitlines()
            if line.startswith("+") and not line.startswith("+++")
        ]
        if any(re.search(r"(?:from\s+|require\()['\"]sharp['\"]", line) for line in added):
            fail("DIRECT_SHARP_USAGE_ADDED", "The remediation added direct Sharp usage.", file=path)
        if any("next/image" in line for line in added):
            fail("NEXT_IMAGE_USAGE_ADDED", "The remediation added next/image usage.", file=path)
        if any(re.search(r"['\"]use server['\"]", line) for line in added):
            fail("NEXT_SERVER_ACTION_ADDED", "The remediation added a Next.js Server Action.", file=path)

    return {
        "remediation_id": authorization["id"],
        "dependency_manifest_changed": False,
        "dependency_lockfile_changed": True,
        "dependency_workspace_changed": True,
        "dependency_diff_against_base": "AUTHORIZED_SECURITY_REMEDIATION",
        "changed_dependency_files": changed_dependency_files,
        "lockfile_consistency_verified": True,
        "vulnerable_lock_versions": [],
        "sharp_versions": sorted(sharp_versions),
        "sharp_override_selector": next(iter(allowed_overrides)),
        "sharp_override_version": target_sharp,
    }


def validate_web_transitive_remediation_diff(
    repository_root: Path,
    base_main_sha: str,
    all_changed: list[str],
    changed_dependency_files: list[str],
    authorization: dict[str, Any],
) -> dict[str, Any]:
    allowed_all = set(authorization["allowed_dependency_files"]) | set(
        authorization["allowed_non_dependency_files"]
    )
    unexpected = sorted(set(all_changed) - allowed_all)
    if unexpected:
        fail(
            "SECURITY_REMEDIATION_FILE_SCOPE_INVALID",
            "The web-transitive remediation changed a file outside its exact authorization.",
            files=unexpected,
        )
    required = set(authorization["required_dependency_files"])
    if set(changed_dependency_files) != required:
        fail(
            "SECURITY_REMEDIATION_DEPENDENCY_SCOPE_INVALID",
            "The remediation dependency files do not match the exact authorization.",
            expected=sorted(required),
            actual=changed_dependency_files,
        )

    package_path = repository_root / "apps/web/package.json"
    current_package = load_json(package_path)
    try:
        base_package = json.loads(
            run_git(repository_root, "show", f"{base_main_sha}:apps/web/package.json")
        )
    except json.JSONDecodeError as exc:
        fail("BASE_DEPENDENCY_MANIFEST_INVALID", "The base package manifest is invalid.", error=str(exc))
    if current_package != base_package:
        fail(
            "UNAUTHORIZED_DEPENDENCY_MANIFEST_CHANGE",
            "The web-transitive remediation does not authorize direct package changes.",
        )
    direct_versions = _manifest_direct_versions(current_package)
    if direct_versions != authorization["expected_direct_versions"]:
        fail(
            "DIRECT_DEPENDENCY_VERSION_INVALID",
            "The direct web dependency versions drifted from the authorization.",
            expected=authorization["expected_direct_versions"],
            actual=direct_versions,
        )
    transitive_targets = set(authorization["expected_lock_version_replacements"])
    for section in ("dependencies", "devDependencies", "optionalDependencies", "peerDependencies"):
        promoted = sorted(transitive_targets & set(current_package.get(section) or {}))
        if promoted:
            fail(
                "DIRECT_TRANSITIVE_DEPENDENCY_REJECTED",
                "An authorized transitive package was promoted to a direct dependency.",
                packages=promoted,
            )

    workspace_path = repository_root / "apps/web/pnpm-workspace.yaml"
    current_workspace = workspace_path.read_text(encoding="utf-8")
    base_workspace = run_git(
        repository_root, "show", f"{base_main_sha}:apps/web/pnpm-workspace.yaml"
    )
    current_overrides = _workspace_overrides(current_workspace)
    allowed_overrides = authorization.get("allowed_overrides") or {}
    if not isinstance(allowed_overrides, dict) or current_overrides != allowed_overrides:
        fail(
            "SECURITY_REMEDIATION_OVERRIDE_INVALID",
            "The workspace overrides differ from the exact web-transitive authorization.",
            expected=allowed_overrides,
            actual=current_overrides,
        )
    if "minimumReleaseAgeExclude:" in current_workspace:
        fail(
            "RELEASE_AGE_EXCLUSION_RETAINED",
            "The obsolete PostCSS release-age exclusion must be removed without replacement.",
        )
    if "minimumReleaseAgeExclude:" not in base_workspace or "postcss@8.5.21" not in base_workspace:
        fail(
            "AUTHORIZED_BASE_WORKSPACE_UNEXPECTED",
            "The authorized base no longer contains the expected obsolete PostCSS exclusion.",
        )

    current_lock = (repository_root / "apps/web/pnpm-lock.yaml").read_text(encoding="utf-8")
    base_lock = run_git(repository_root, "show", f"{base_main_sha}:apps/web/pnpm-lock.yaml")
    replacements = authorization["expected_lock_version_replacements"]
    for package, expected in replacements.items():
        base_versions = _lock_versions(base_lock, package)
        current_versions = _lock_versions(current_lock, package)
        if base_versions != set(expected["from"]) or current_versions != set(expected["to"]):
            fail(
                "SECURITY_REMEDIATION_LOCK_VERSION_INVALID",
                "A transitive lockfile version differs from the exact authorization.",
                package=package,
                expected_base=sorted(expected["from"]),
                actual_base=sorted(base_versions),
                expected_branch=sorted(expected["to"]),
                actual_branch=sorted(current_versions),
            )
        if authorization.get("prohibited_prereleases") is True and any(
            _semver_tuple(version) is None for version in current_versions
        ):
            fail(
                "PRERELEASE_DEPENDENCY_REJECTED",
                "Prerelease target versions are forbidden in the remediation graph.",
                package=package,
                versions=sorted(current_versions),
            )

    base_keys = _lock_package_keys(base_lock)
    current_keys = _lock_package_keys(current_lock)
    expected_count = authorization["expected_lock_package_count"]
    if len(base_keys) != expected_count or len(current_keys) != expected_count:
        fail(
            "LOCKFILE_PACKAGE_COUNT_CHANGED",
            "The remediation must preserve the exact lockfile package count.",
            expected=expected_count,
            base=len(base_keys),
            branch=len(current_keys),
        )
    expected_removed = {
        f"{package}@{version}"
        for package, values in replacements.items()
        for version in values["from"]
        if version not in values["to"]
    }
    expected_added = {
        f"{package}@{version}"
        for package, values in replacements.items()
        for version in values["to"]
        if version not in values["from"]
    }
    removed = base_keys - current_keys
    added = current_keys - base_keys
    if removed != expected_removed or added != expected_added:
        fail(
            "LOCKFILE_TRANSITIVE_SCOPE_INVALID",
            "The lockfile changed outside the five exact authorized version replacements.",
            expected_removed=sorted(expected_removed),
            actual_removed=sorted(removed),
            expected_added=sorted(expected_added),
            actual_added=sorted(added),
        )
    if {_lock_package_name(key) for key in base_keys} != {
        _lock_package_name(key) for key in current_keys
    }:
        fail(
            "LOCKFILE_PACKAGE_NAME_ADDED",
            "The remediation introduced or removed a package name.",
        )

    target_sharp = authorization["target_sharp_version"]
    base_sharp_integrity = _lock_package_integrity(base_lock, "sharp", target_sharp)
    current_sharp_integrity = _lock_package_integrity(current_lock, "sharp", target_sharp)
    if not base_sharp_integrity or current_sharp_integrity != base_sharp_integrity:
        fail(
            "SHARP_LOCK_INTEGRITY_CHANGED",
            "The remediation must preserve the authorized Sharp package integrity.",
        )

    return {
        "remediation_id": authorization["id"],
        "dependency_manifest_changed": False,
        "dependency_lockfile_changed": True,
        "dependency_workspace_changed": True,
        "dependency_diff_against_base": "AUTHORIZED_SECURITY_REMEDIATION",
        "changed_dependency_files": changed_dependency_files,
        "lockfile_consistency_verified": True,
        "vulnerable_lock_versions": [],
        "lock_package_count": len(current_keys),
        "removed_lock_package_keys": sorted(removed),
        "added_lock_package_keys": sorted(added),
        "sharp_versions": sorted(_lock_versions(current_lock, "sharp")),
        "sharp_override_selector": f"next@{direct_versions['next']}>sharp",
        "sharp_override_version": target_sharp,
    }


def validate_consolidated_security_baseline_diff(
    repository_root: Path,
    base_main_sha: str,
    all_changed: list[str],
    changed_dependency_files: list[str],
    authorization: dict[str, Any],
) -> dict[str, Any]:
    allowed_all = set(authorization["allowed_dependency_files"]) | set(
        authorization["allowed_non_dependency_files"]
    )
    if set(all_changed) != allowed_all:
        fail(
            "SECURITY_REMEDIATION_FILE_SCOPE_INVALID",
            "The consolidated remediation must contain exactly its ten authorized files.",
            expected=sorted(allowed_all),
            actual=all_changed,
        )
    required = set(authorization["required_dependency_files"])
    if set(changed_dependency_files) != required:
        fail(
            "SECURITY_REMEDIATION_DEPENDENCY_SCOPE_INVALID",
            "The consolidated dependency files do not match the exact authorization.",
            expected=sorted(required),
            actual=changed_dependency_files,
        )

    web_dependency_files = {
        "apps/web/pnpm-lock.yaml",
        "apps/web/pnpm-workspace.yaml",
    }
    web_authorization = copy.deepcopy(authorization)
    web_authorization["allowed_dependency_files"] = sorted(web_dependency_files)
    web_authorization["required_dependency_files"] = sorted(web_dependency_files)
    web_changed = sorted(
        path
        for path in all_changed
        if path in web_dependency_files
        or path in set(authorization["allowed_non_dependency_files"])
    )
    result = validate_web_transitive_remediation_diff(
        repository_root,
        base_main_sha,
        web_changed,
        sorted(web_dependency_files),
        web_authorization,
    )

    props_path = "Directory.Packages.props"
    base_props = run_git(repository_root, "show", f"{base_main_sha}:{props_path}")
    current_props = (repository_root / props_path).read_text(encoding="utf-8")
    expected_props = base_props.replace(
        '<PackageVersion Include="Testcontainers.PostgreSql" Version="4.13.0" />',
        '<PackageVersion Include="Testcontainers.PostgreSql" Version="4.14.0" />',
    )
    if expected_props == base_props or current_props.rstrip() != expected_props:
        fail(
            "NUGET_CENTRAL_VERSION_CHANGE_INVALID",
            "Only Testcontainers.PostgreSql 4.13.0 -> 4.14.0 is authorized centrally.",
        )
    forbidden_props = (
        "CentralPackageTransitivePinningEnabled",
        "NuGetAuditMode",
        "NuGetAuditLevel",
        "NuGetAuditSuppress",
        "NoWarn",
    )
    if any(value in current_props for value in forbidden_props) or "SSH.NET" in current_props:
        fail(
            "NUGET_SECURITY_POLICY_BYPASS_REJECTED",
            "Direct SSH.NET pinning, transitive pinning, and NuGet audit suppression are forbidden.",
        )

    versions = authorization["expected_nuget_versions"]
    lock_paths = (
        "tests/Paqueteria.ContractTests/packages.lock.json",
        "tests/Paqueteria.IntegrationTests/packages.lock.json",
    )
    for lock_path in lock_paths:
        try:
            base_lock = json.loads(
                run_git(repository_root, "show", f"{base_main_sha}:{lock_path}")
            )
        except json.JSONDecodeError as exc:
            fail("BASE_DEPENDENCY_LOCK_INVALID", "The base NuGet lockfile is invalid.", error=str(exc))
        current_lock = load_json(repository_root / lock_path)
        expected_lock = copy.deepcopy(base_lock)
        base_packages = base_lock.get("dependencies", {}).get("net10.0", {})
        current_packages = current_lock.get("dependencies", {}).get("net10.0", {})
        expected_packages = expected_lock.get("dependencies", {}).get("net10.0", {})
        if not all(
            package in base_packages and package in current_packages
            for package in versions
        ):
            fail("NUGET_LOCK_GRAPH_INVALID", "An authorized NuGet package is missing from a lockfile.", file=lock_path)
        for package, replacement in versions.items():
            base_node = base_packages[package]
            current_node = current_packages[package]
            if (
                base_node.get("resolved") != replacement["from"]
                or current_node.get("resolved") != replacement["to"]
                or not isinstance(current_node.get("contentHash"), str)
                or not current_node["contentHash"]
                or current_node.get("contentHash") == base_node.get("contentHash")
                or _semver_tuple(replacement["to"]) is None
            ):
                fail(
                    "NUGET_LOCK_VERSION_INVALID",
                    "A NuGet package does not match its exact stable before/after authorization.",
                    file=lock_path,
                    package=package,
                )
            expected_node = copy.deepcopy(base_node)
            expected_node["resolved"] = replacement["to"]
            expected_node["contentHash"] = current_node["contentHash"]
            if package == "Testcontainers.PostgreSql":
                expected_node["requested"] = "[4.14.0, )"
                expected_node["dependencies"]["Testcontainers"] = "4.14.0"
            elif package == "Testcontainers":
                expected_node["dependencies"]["SSH.NET"] = "2026.0.0"
            elif package == "SSH.NET":
                expected_node["dependencies"]["BouncyCastle.Cryptography"] = "2.7.0"
            if current_node != expected_node:
                fail(
                    "NUGET_LOCK_GRAPH_INVALID",
                    "The generated NuGet lock graph changed outside the four authorized packages.",
                    file=lock_path,
                    package=package,
                )
            expected_packages[package] = expected_node
        if current_lock != expected_lock:
            fail(
                "NUGET_LOCK_SCOPE_INVALID",
                "The NuGet lockfile changed outside the exact generated four-package graph.",
                file=lock_path,
            )

    for project in repository_root.rglob("*.csproj"):
        if re.search(r"PackageReference[^>]+(?:Include|Update)=['\"]SSH\.NET['\"]", project.read_text(encoding="utf-8"), re.IGNORECASE):
            fail(
                "DIRECT_SSH_NET_DEPENDENCY_REJECTED",
                "SSH.NET must remain transitive and must not be pinned directly.",
                file=project.relative_to(repository_root).as_posix(),
            )

    result.update(
        {
            "remediation_id": authorization["id"],
            "changed_dependency_files": changed_dependency_files,
            "nuget_lockfiles_regenerated": list(lock_paths),
            "nuget_versions": {
                package: replacement["to"] for package, replacement in versions.items()
            },
            "nuget_dependency_path": authorization["expected_nuget_dependency_path"],
        }
    )
    return result


def _nuget_package_nodes(lock: dict[str, Any]) -> dict[tuple[str, str], dict[str, Any]]:
    dependencies = lock.get("dependencies")
    if not isinstance(dependencies, dict):
        fail("NUGET_LOCK_INVALID", "A NuGet lockfile has no dependency graph.")
    packages: dict[tuple[str, str], dict[str, Any]] = {}
    for framework, nodes in dependencies.items():
        if not isinstance(framework, str) or not isinstance(nodes, dict):
            fail("NUGET_LOCK_INVALID", "A NuGet lockfile dependency graph is invalid.")
        for package, node in nodes.items():
            if not isinstance(package, str) or not isinstance(node, dict):
                fail("NUGET_LOCK_INVALID", "A NuGet lockfile package node is invalid.")
            if node.get("type") != "Project":
                packages[(framework, package)] = node
    return packages


def _base_nuget_package_catalog(
    repository_root: Path,
    base_main_sha: str,
) -> dict[tuple[str, str], list[dict[str, Any]]]:
    catalog: dict[tuple[str, str], list[dict[str, Any]]] = {}
    paths = run_git(repository_root, "ls-tree", "-r", "--name-only", base_main_sha)
    for relative in paths.splitlines():
        if Path(relative).name != "packages.lock.json":
            continue
        raw = run_git(repository_root, "show", f"{base_main_sha}:{relative}")
        try:
            lock = json.loads(raw)
        except json.JSONDecodeError as exc:
            fail(
                "BASE_DEPENDENCY_LOCK_INVALID",
                "A baseline NuGet lockfile is invalid.",
                file=relative,
                error=str(exc),
            )
        for key, node in _nuget_package_nodes(lock).items():
            catalog.setdefault(key, []).append(node)
    return catalog


def _xml_local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _baseline_central_package_versions(
    repository_root: Path,
    base_main_sha: str,
    all_changed: set[str],
) -> dict[str, tuple[str, str]]:
    props_path = "Directory.Packages.props"
    if props_path in all_changed:
        fail(
            "BASELINE_CENTRAL_PACKAGES_CHANGED",
            "Directory.Packages.props must remain byte-identical to the baseline.",
        )
    baseline_raw = run_git(repository_root, "show", f"{base_main_sha}:{props_path}")
    current_path = repository_root / props_path
    if not current_path.is_file() or current_path.read_text(encoding="utf-8").strip() != baseline_raw:
        fail(
            "BASELINE_CENTRAL_PACKAGES_CHANGED",
            "Directory.Packages.props must remain byte-identical to the baseline.",
        )
    try:
        root = ET.fromstring(baseline_raw)
    except ET.ParseError as exc:
        fail(
            "BASE_DEPENDENCY_MANIFEST_INVALID",
            "The baseline central package manifest is invalid XML.",
            error=str(exc),
        )
    central_enabled = any(
        _xml_local_name(element.tag).casefold() == "managepackageversionscentrally"
        and (element.text or "").strip().casefold() == "true"
        for element in root.iter()
    )
    if not central_enabled:
        fail(
            "BASE_DEPENDENCY_MANIFEST_INVALID",
            "The baseline does not enable Central Package Management.",
        )
    versions: dict[str, tuple[str, str]] = {}
    for element in root.iter():
        if _xml_local_name(element.tag).casefold() != "packageversion":
            continue
        package = element.attrib.get("Include")
        version = element.attrib.get("Version")
        if not package or not version:
            fail(
                "BASE_DEPENDENCY_MANIFEST_INVALID",
                "A baseline PackageVersion must use explicit Include and Version attributes.",
            )
        key = package.casefold()
        if key in versions:
            fail(
                "BASE_DEPENDENCY_MANIFEST_INVALID",
                "The baseline central package manifest contains a duplicate package ID.",
                package=package,
            )
        versions[key] = (package, version)
    return versions


def _new_project_package_references(
    project_path: Path,
    relative: str,
) -> dict[str, str]:
    try:
        root = ET.parse(project_path).getroot()
    except (ET.ParseError, OSError) as exc:
        fail(
            "NUGET_NEW_PROJECT_INVALID",
            "A new project associated with a NuGet lockfile is invalid XML.",
            file=relative,
            error=str(exc),
        )
    forbidden_properties = {
        "restoreadditionalprojectsources",
        "restoreconfigfile",
        "restorefallbackfolders",
        "restoresources",
        "directorypackagespropspath",
        "centralpackagetransitivepinningenabled",
        "centralpackageversionoverrideenabled",
    }
    package_references: dict[str, str] = {}
    for element in root.iter():
        name = _xml_local_name(element.tag).casefold()
        if name == "import" or name in forbidden_properties or (
            "source" in name and ("restore" in name or "package" in name)
        ):
            fail(
                "NUGET_NEW_PROJECT_SOURCE_OVERRIDE",
                "A new project must not override package sources or import dependency settings.",
                file=relative,
            )
        if name in {"packageversion", "packagedownload", "globalpackagereference"}:
            fail(
                "NUGET_NEW_PROJECT_LOCAL_PACKAGE_DECLARATION",
                "A new project must use baseline Central Package Management only.",
                file=relative,
            )
        if name == "managepackageversionscentrally" and (
            element.text or ""
        ).strip().casefold() != "true":
            fail(
                "NUGET_NEW_PROJECT_CPM_DISABLED",
                "A new project must not disable Central Package Management.",
                file=relative,
            )
        if name != "packagereference":
            continue
        attribute_names = {key.casefold() for key in element.attrib}
        if "condition" in attribute_names or "update" in attribute_names:
            fail(
                "NUGET_NEW_PROJECT_PACKAGE_REFERENCE_INVALID",
                "A new project PackageReference must be unconditional and use Include.",
                file=relative,
            )
        package = element.attrib.get("Include")
        if not package or "$" in package:
            fail(
                "NUGET_NEW_PROJECT_PACKAGE_REFERENCE_INVALID",
                "A new project PackageReference must use a literal package ID.",
                file=relative,
            )
        local_version = any(
            key.casefold() in {"version", "versionoverride"}
            for key in element.attrib
        ) or any(
            _xml_local_name(child.tag).casefold() in {"version", "versionoverride"}
            for child in element
        )
        if local_version:
            fail(
                "NUGET_NEW_PROJECT_LOCAL_VERSION",
                "A new project PackageReference must not contain Version or VersionOverride.",
                file=relative,
                package=package,
            )
        key = package.casefold()
        if key in package_references:
            fail(
                "NUGET_NEW_PROJECT_PACKAGE_REFERENCE_INVALID",
                "A new project contains a duplicate PackageReference.",
                file=relative,
                package=package,
            )
        package_references[key] = package
    return package_references


def _requested_version_matches_central(requested: Any, version: str) -> bool:
    return isinstance(requested, str) and requested == f"[{version}, )"


def validate_baseline_package_graph_lockfile_diff(
    repository_root: Path,
    base_main_sha: str,
    all_changed: list[str],
) -> list[str]:
    """Accept only lock drift whose full package graph exists in the baseline.

    Existing locks may change only Project nodes. New locks must have one
    corresponding changed project, use baseline Central Package Management,
    and contain exact baseline package metadata for every non-Project node.
    """

    lockfiles = sorted(
        path for path in all_changed if Path(path).name == "packages.lock.json"
    )
    if not lockfiles:
        return []
    base_catalog: dict[tuple[str, str], list[dict[str, Any]]] | None = None
    baseline_graph_only: list[str] = []
    changed_set = set(all_changed)
    for relative in lockfiles:
        current = load_json(repository_root / relative)
        if not isinstance(current, dict):
            fail("NUGET_LOCK_INVALID", "A NuGet lockfile must be a JSON object.", file=relative)
        current_packages = _nuget_package_nodes(current)
        base_raw = run_git(
            repository_root,
            "show",
            f"{base_main_sha}:{relative}",
            allow_failure=True,
        )
        if base_raw:
            try:
                base = json.loads(base_raw)
            except json.JSONDecodeError as exc:
                fail(
                    "BASE_DEPENDENCY_LOCK_INVALID",
                    "A baseline NuGet lockfile is invalid.",
                    file=relative,
                    error=str(exc),
                )
            if current.get("version") != base.get("version") or current_packages != _nuget_package_nodes(base):
                continue
            baseline_graph_only.append(relative)
            continue

        project_files = [
            path
            for path in changed_set
            if Path(path).parent.as_posix() == Path(relative).parent.as_posix()
            and Path(path).suffix.lower() == ".csproj"
        ]
        if len(project_files) != 1:
            continue
        central_versions = _baseline_central_package_versions(
            repository_root,
            base_main_sha,
            changed_set,
        )
        project_relative = project_files[0]
        direct_references = _new_project_package_references(
            repository_root / project_relative,
            project_relative,
        )
        for key, package in direct_references.items():
            if key not in central_versions:
                fail(
                    "NUGET_NEW_PROJECT_DIRECT_PACKAGE_NOT_BASELINE_CENTRAL",
                    "A new project directly references a package not approved centrally in the baseline.",
                    file=project_relative,
                    package=package,
                )
        lock_direct: dict[str, list[tuple[str, dict[str, Any]]]] = {}
        for (framework, package), node in current_packages.items():
            if node.get("type") == "Direct":
                lock_direct.setdefault(package.casefold(), []).append((framework, node))
        if set(lock_direct) != set(direct_references):
            fail(
                "NUGET_NEW_PROJECT_DIRECT_GRAPH_MISMATCH",
                "The new project PackageReferences do not match the direct lockfile graph.",
                file=relative,
            )
        for key, package in direct_references.items():
            _, central_version = central_versions[key]
            for framework, node in lock_direct[key]:
                if (
                    node.get("resolved") != central_version
                    or not _requested_version_matches_central(
                        node.get("requested"), central_version
                    )
                ):
                    fail(
                        "NUGET_NEW_PROJECT_CENTRAL_VERSION_MISMATCH",
                        "A new project lockfile did not resolve its exact baseline central version.",
                        file=relative,
                        framework=framework,
                        package=package,
                    )
        if base_catalog is None:
            base_catalog = _base_nuget_package_catalog(repository_root, base_main_sha)
        if all(node in base_catalog.get(key, []) for key, node in current_packages.items()):
            baseline_graph_only.append(relative)
    return baseline_graph_only


def validate_next_critical_remediation_diff(
    repository_root: Path,
    base_main_sha: str,
    all_changed: list[str],
    changed_dependency_files: list[str],
    authorization: dict[str, Any],
) -> dict[str, Any]:
    allowed_all = set(authorization["allowed_dependency_files"]) | set(
        authorization["allowed_non_dependency_files"]
    )
    unexpected = sorted(set(all_changed) - allowed_all)
    if unexpected:
        fail(
            "SECURITY_REMEDIATION_FILE_SCOPE_INVALID",
            "The Next.js remediation changed a file outside its exact authorization.",
            files=unexpected,
        )
    required = set(authorization["required_dependency_files"])
    if set(changed_dependency_files) != required:
        fail(
            "SECURITY_REMEDIATION_DEPENDENCY_SCOPE_INVALID",
            "The Next.js remediation dependency files do not match the exact authorization.",
            expected=sorted(required),
            actual=changed_dependency_files,
        )

    package_path = repository_root / "apps/web/package.json"
    current_package = load_json(package_path)
    try:
        base_package = json.loads(
            run_git(repository_root, "show", f"{base_main_sha}:apps/web/package.json")
        )
    except json.JSONDecodeError as exc:
        fail("BASE_DEPENDENCY_MANIFEST_INVALID", "The base package manifest is invalid.", error=str(exc))
    version_changes = authorization["expected_direct_version_changes"]
    if set(version_changes) != set(authorization["allowed_direct_packages"]):
        fail(
            "SECURITY_REMEDIATION_DIRECT_SCOPE_INVALID",
            "The direct dependency authorization is inconsistent.",
        )
    next_version = current_package.get("dependencies", {}).get("next")
    eslint_next_version = current_package.get("devDependencies", {}).get(
        "eslint-config-next"
    )
    if eslint_next_version != next_version:
        fail(
            "NEXT_DEPENDENCY_ALIGNMENT_INVALID",
            "Next.js and eslint-config-next must remain aligned stable versions.",
        )
    expected_package = copy.deepcopy(base_package)
    for package, replacement in version_changes.items():
        section = "dependencies" if package == "next" else "devDependencies"
        actual_version = current_package.get(section, {}).get(package)
        if _semver_tuple(str(actual_version)) is None:
            fail(
                "PRERELEASE_DEPENDENCY_REJECTED",
                "Prerelease dependency versions are forbidden.",
                package=package,
            )
        if base_package.get(section, {}).get(package) != replacement["from"]:
            fail(
                "SECURITY_REMEDIATION_VERSION_INVALID",
                "A base direct dependency differs from its exact authorization.",
                package=package,
            )
        if actual_version != replacement["to"]:
            fail(
                "SECURITY_REMEDIATION_VERSION_INVALID",
                "A direct dependency differs from its exact minimum patched target.",
                package=package,
            )
        expected_package[section][package] = replacement["to"]
    if current_package != expected_package:
        fail(
            "UNAUTHORIZED_DEPENDENCY_MANIFEST_CHANGE",
            "Only the three exact authorized direct dependency updates are permitted.",
        )
    if next_version != authorization["next_first_patched_version"]:
        fail(
            "SECURITY_REMEDIATION_VERSION_INVALID",
            "Next.js must use the exact minimum patched version.",
        )

    if any(
        "sharp" in (current_package.get(section) or {})
        for section in ("dependencies", "devDependencies", "optionalDependencies", "peerDependencies")
    ):
        fail("DIRECT_SHARP_DEPENDENCY_REJECTED", "Sharp must remain transitive and optional.")
    current_workspace = (repository_root / "apps/web/pnpm-workspace.yaml").read_text(
        encoding="utf-8"
    )
    current_overrides = _workspace_overrides(current_workspace)
    if current_overrides != authorization["allowed_overrides"]:
        fail(
            "SECURITY_REMEDIATION_OVERRIDE_INVALID",
            "The workspace overrides differ from the exact Next.js remediation authorization.",
            expected=authorization["allowed_overrides"],
            actual=current_overrides,
        )

    lock_path = repository_root / "apps/web/pnpm-lock.yaml"
    lock_bytes = lock_path.read_bytes()
    lock_text = lock_bytes.decode("utf-8")
    lock_hash = hashlib.sha256(lock_bytes).hexdigest()
    if lock_hash != authorization["expected_lockfile_sha256"]:
        fail(
            "LOCKFILE_GENERATED_GRAPH_INVALID",
            "The pnpm lockfile differs from the exact authorized generated graph.",
            expected=authorization["expected_lockfile_sha256"],
            actual=lock_hash,
        )
    lock_keys = _lock_package_keys(lock_text)
    if len(lock_keys) != authorization["expected_lock_package_count"]:
        fail(
            "LOCKFILE_PACKAGE_COUNT_CHANGED",
            "The remediation lockfile package count changed.",
            expected=authorization["expected_lock_package_count"],
            actual=len(lock_keys),
        )
    exact_versions = {
        "next": "16.3.3",
        "eslint-config-next": "16.3.3",
        "vitest": "4.1.11",
        "sharp": "0.35.4",
        "browserslist": "4.28.7",
        "baseline-browser-mapping": "2.11.0",
        "js-yaml": "4.3.2",
    }
    for package, expected_version in exact_versions.items():
        versions = {
            version for version in _lock_versions(lock_text, package) if ">" not in version
        }
        if versions != {expected_version}:
            fail(
                "SECURITY_REMEDIATION_LOCK_VERSION_INVALID",
                "A security target differs from its exact authorized lockfile version.",
                package=package,
                expected=expected_version,
                actual=sorted(versions),
            )
    if f"next@{next_version}>sharp: {authorization['target_sharp_version']}" not in lock_text:
        fail("LOCKFILE_INCONSISTENT", "The lockfile does not apply the authorized Sharp edge.")

    return {
        "remediation_id": authorization["id"],
        "dependency_manifest_changed": True,
        "dependency_lockfile_changed": True,
        "dependency_workspace_changed": True,
        "dependency_diff_against_base": "AUTHORIZED_SECURITY_REMEDIATION",
        "changed_dependency_files": changed_dependency_files,
        "lockfile_consistency_verified": True,
        "lockfile_sha256": lock_hash,
        "lock_package_count": len(lock_keys),
        "vulnerable_lock_versions": [],
        "sharp_versions": [authorization["target_sharp_version"]],
        "sharp_override_selector": f"next@{next_version}>sharp",
        "sharp_override_version": authorization["target_sharp_version"],
    }


def validate_dependency_diff(
    repository_root: Path,
    base_main_sha: str,
    mode: str = NORMAL_RELEASE_EVIDENCE,
    authorization: dict[str, Any] | None = None,
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
    baseline_package_graph_lockfiles = validate_baseline_package_graph_lockfile_diff(
        repository_root,
        base_main_sha,
        all_changed,
    )
    allowed_dependency_files = (
        set(authorization["allowed_dependency_files"])
        if authorization is not None
        else SECURITY_REMEDIATION_DEPENDENCY_FILES
    )
    changed = sorted(path for path in all_changed if path in allowed_dependency_files)
    unexpected_dependency_files = sorted(
        path
        for path in all_changed
        if Path(path).name in DEPENDENCY_FILE_NAMES
        and path not in baseline_package_graph_lockfiles
        and path not in allowed_dependency_files
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
            "dependency_lockfile_changed": bool(baseline_package_graph_lockfiles),
            "dependency_workspace_changed": False,
            "dependency_diff_against_base": "CLEAN",
            "changed_dependency_files": baseline_package_graph_lockfiles,
            "baseline_package_graph_lockfiles": baseline_package_graph_lockfiles,
            "lockfile_consistency_verified": True,
            "vulnerable_lock_versions": [],
        }
    if mode != SECURITY_REMEDIATION:
        fail("REL000_MODE_INVALID", "The dependency validator received an invalid mode.")
    if authorization is not None and authorization.get("id") == SHARP_REMEDIATION_ID:
        return validate_sharp_remediation_diff(
            repository_root,
            base_main_sha,
            all_changed,
            changed,
            authorization,
        )
    if authorization is not None and authorization.get("id") == WEB_TRANSITIVE_REMEDIATION_ID:
        return validate_consolidated_security_baseline_diff(
            repository_root,
            base_main_sha,
            all_changed,
            changed,
            authorization,
        )
    if authorization is not None and authorization.get("id") == NEXT_CRITICAL_REMEDIATION_ID:
        return validate_next_critical_remediation_diff(
            repository_root,
            base_main_sha,
            all_changed,
            changed,
            authorization,
        )
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


def validate_sharp_runtime_smoke(
    smoke: dict[str, Any] | None,
    mode: str,
    authorization: dict[str, Any] | None,
) -> dict[str, Any]:
    required = (
        mode == SECURITY_REMEDIATION
        and authorization is not None
        and authorization.get("require_sharp_runtime_smoke") is True
    )
    if not required:
        return {"executed": False, "status": "NOT_REQUIRED"}
    if not isinstance(smoke, dict):
        fail("SHARP_RUNTIME_SMOKE_NOT_EXECUTED", "The Sharp runtime smoke evidence is missing.")
    if smoke.get("result") != "SHARP_RUNTIME_SMOKE_PASSED":
        fail("SHARP_RUNTIME_SMOKE_FAILED", "The Sharp runtime smoke did not pass.")
    expected_version = authorization.get("target_sharp_version")
    if smoke.get("sharp_version") != expected_version:
        fail(
            "SHARP_RUNTIME_SMOKE_VERSION_INVALID",
            "The Sharp runtime smoke executed a different dependency version.",
            expected=expected_version,
            actual=smoke.get("sharp_version"),
        )
    if (
        smoke.get("dependency_parent") != "next"
        or smoke.get("node_version") != "24.13.0"
        or smoke.get("platform") not in {"linux", "win32"}
        or smoke.get("output_format") != "png"
        or not isinstance(smoke.get("output_bytes"), int)
        or smoke["output_bytes"] <= 8
    ):
        fail("SHARP_RUNTIME_SMOKE_INVALID", "The Sharp runtime smoke evidence is invalid.")
    return {
        "executed": True,
        "status": "PASSED",
        "sharp_version": smoke["sharp_version"],
        "dependency_parent": "next",
        "node_version": smoke["node_version"],
        "platform": smoke["platform"],
        "architecture": smoke.get("architecture"),
        "output_format": "png",
        "output_nonempty": True,
    }


def validate_issue_and_audit(
    issue: dict[str, Any],
    additional_issue: dict[str, Any],
    base_audit: dict[str, Any],
    branch_audit: dict[str, Any],
    dependency_diff: dict[str, Any],
    mode: str = NORMAL_RELEASE_EVIDENCE,
    authorization: dict[str, Any] | None = None,
) -> dict[str, Any]:
    if int(issue.get("number", 0)) != 5:
        fail("ISSUE5_OMITTED_OR_CLOSED", "Issue #5 must be consulted.")
    issue5_ids = {str(value).lower() for value in issue.get("tracked_advisory_ids") or []}
    if issue5_ids != {ISSUE5_ADVISORY.lower()}:
        fail("ISSUE5_SCOPE_MISREPRESENTED", "Issue #5 must track only its sharp advisory.")
    issue5_state = str(issue.get("state", "")).upper()
    additional_state = str(additional_issue.get("state", "")).upper()
    related_issue = additional_issue.get("related_issue")
    if related_issue is None:
        related_issue = {}
    if not isinstance(related_issue, dict):
        fail("ADDITIONAL_SECURITY_TRACKING_INVALID", "The related security Issue is invalid.")
    related_state = str(related_issue.get("state", "")).upper()
    policy_tracks_additional = authorization is not None and isinstance(
        authorization.get("issue_advisories"), dict
    )
    consolidated_tracking = policy_tracks_additional or (
        int(additional_issue.get("number", 0)) == 38
        and int(related_issue.get("number", 0)) == 40
    )
    expected_additional_number = (
        int(authorization["tracked_issue"])
        if policy_tracks_additional
        else 38
        if consolidated_tracking
        else 30
    )
    expected_additional_title = (
        authorization["tracked_issue_title"]
        if policy_tracks_additional
        else "SEC-2026-08: remediate current web transitive dependency advisories"
        if consolidated_tracking
        else ADDITIONAL_SECURITY_ISSUE_TITLE
    )
    expected_additional_ids = {
        value.lower()
        for value in (
            authorization["issue_advisories"][str(authorization["tracked_issue"])]
            if policy_tracks_additional
            else [
                "GHSA-fxqj-rqcc-2cmp",
                "GHSA-rgw5-rvv9-x895",
                "GHSA-2v37-7h3g-55p8",
                "GHSA-5p4m-2wfm-xmqj",
            ]
            if consolidated_tracking
            else ISSUE30_ADVISORIES
        )
    }
    if (
        int(additional_issue.get("number", 0)) != expected_additional_number
        or additional_state not in {"OPEN", "CLOSED"}
        or additional_issue.get("title") != expected_additional_title
        or {
            str(value).lower()
            for value in additional_issue.get("tracked_advisory_ids") or []
        }
        != expected_additional_ids
    ):
        fail(
            "ADDITIONAL_SECURITY_TRACKING_INVALID",
            "The additional security Issue does not match the exact remediation tracking contract.",
        )
    related_tracking_required = (
        policy_tracks_additional and authorization.get("related_tracked_issue") is not None
    ) or (not policy_tracks_additional and consolidated_tracking)
    expected_related_number = (
        int(authorization["related_tracked_issue"])
        if policy_tracks_additional and authorization.get("related_tracked_issue") is not None
        else 40
    )
    expected_related_ids = (
        {
            value.lower()
            for value in authorization["issue_advisories"].get(
                str(expected_related_number), []
            )
        }
        if policy_tracks_additional
        else {"ghsa-q939-rpr3-3284"}
        if consolidated_tracking
        else set()
    )
    expected_related_title = (
        authorization.get("related_tracked_issue_title")
        if policy_tracks_additional
        else "SEC-2026-08: remediate transitive SSH.NET advisory"
    )
    if related_tracking_required and (
        int(related_issue.get("number", 0)) != expected_related_number
        or related_state not in {"OPEN", "CLOSED"}
        or related_issue.get("title") != expected_related_title
        or {
            str(value).lower()
            for value in related_issue.get("tracked_advisory_ids") or []
        }
        != expected_related_ids
    ):
        fail(
            "RELATED_SECURITY_TRACKING_INVALID",
            "The related Issue does not match the exact remediation tracking contract.",
        )
    if issue5_state not in {"OPEN", "CLOSED"}:
        fail("ISSUE5_OMITTED_OR_CLOSED", "Issue #5 has an invalid state.")
    if authorization is not None:
        tracked_number = int(
            (additional_issue if policy_tracks_additional else issue).get("number", 0)
        )
        if authorization.get("tracked_issue") != tracked_number:
            fail(
                "REMEDIATION_TRACKED_ISSUE_INVALID",
                "The remediation authorization targets a different issue.",
            )

    base = validate_audit_snapshot(base_audit, "base")
    branch = validate_audit_snapshot(branch_audit, "branch")
    base_totals = base["totals"]
    branch_totals = branch["totals"]
    if branch_totals["critical"] > 0:
        fail("AUDIT_CRITICAL_PRESENT", "The branch audit contains a critical advisory.")
    base_ids = {item["advisory_id"].lower() for item in base["advisories"]}
    branch_ids = {item["advisory_id"].lower() for item in branch["advisories"]}
    expected_base_advisories = (
        set(authorization["expected_base_advisories"])
        if authorization is not None
        else EXPECTED_BASE_ADVISORIES
    )
    expected_base_totals = (
        authorization["expected_base_totals"]
        if authorization is not None
        else EXPECTED_BASE_AUDIT_TOTALS
    )
    expected_ids = {value.lower() for value in expected_base_advisories}
    if mode == SECURITY_REMEDIATION:
        if policy_tracks_additional and additional_state != "OPEN":
            fail(
                "ADDITIONAL_SECURITY_ISSUE_STATE_INVALID",
                "The active remediation tracking Issue must remain open until merge.",
                issue=additional_issue["number"],
            )
        if related_tracking_required and related_state != "OPEN":
            fail(
                "RELATED_SECURITY_ISSUE_STATE_INVALID",
                "The related security Issue must remain open until the remediation merges.",
                issue=expected_related_number,
            )
        for key, expected in expected_base_totals.items():
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
        expected_packages = authorization.get("expected_base_packages") if authorization else None
        if expected_packages is not None:
            actual_packages = {
                item["advisory_id"].lower(): item["package"] for item in base["advisories"]
            }
            normalized_expected_packages = {
                advisory_id.lower(): package
                for advisory_id, package in expected_packages.items()
            }
            if actual_packages != normalized_expected_packages:
                fail(
                    "BASE_AFFECTED_PACKAGES_UNEXPECTED",
                    "The base advisory-to-package mapping differs from the authorization.",
                    expected=normalized_expected_packages,
                    actual=actual_packages,
                )
        expected_branch_totals = (
            authorization.get("expected_branch_totals") if authorization else None
        )
        if expected_branch_totals is not None and branch_totals != expected_branch_totals:
            fail(
                "BRANCH_AUDIT_NOT_ZERO",
                "The remediation branch audit does not match the required zero-advisory result.",
                expected=expected_branch_totals,
                actual=branch_totals,
            )
        if dependency_diff.get("dependency_diff_against_base") != (
            "AUTHORIZED_SECURITY_REMEDIATION"
        ):
            fail("DEPENDENCY_FILES_CHANGED", "Security remediation dependency evidence is missing.")
        if (
            authorization is not None
            and authorization.get("tracked_issue") == 5
            and additional_state != "CLOSED"
        ):
            fail(
                "ADDITIONAL_SECURITY_ISSUE_STATE_INVALID",
                "Issue #30 must remain closed during the Issue #5 remediation.",
            )
    elif mode == NORMAL_RELEASE_EVIDENCE:
        if dependency_diff.get("dependency_diff_against_base") != "CLEAN":
            fail("DEPENDENCY_FILES_CHANGED", "Normal release evidence requires a clean dependency diff.")
    else:
        fail("REL000_MODE_INVALID", "The audit validator received an invalid mode.")

    untracked_base = base_ids - expected_ids
    if untracked_base:
        fail(
            "UNTRACKED_DEPENDENCY_ADVISORY",
            "The base audit contains an advisory outside the exact tracking contract.",
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
    additional_present = branch_ids & expected_additional_ids
    related_present = branch_ids & expected_related_ids
    if issue5_present and issue5_state == "CLOSED":
        fail(
            "SECURITY_ISSUE_CLOSED_WHILE_ADVISORY_PRESENT",
            "Issue #5 is closed while its advisory remains present.",
            issue=5,
        )
    if additional_present and additional_state == "CLOSED":
        fail(
            "SECURITY_ISSUE_CLOSED_WHILE_ADVISORY_PRESENT",
            "The tracking Issue is closed while one or more advisories remain present.",
            issue=additional_issue["number"],
        )
    if related_present and related_state == "CLOSED":
        fail(
            "SECURITY_ISSUE_CLOSED_WHILE_ADVISORY_PRESENT",
            "Issue #40 is closed while its SSH.NET advisory remains present.",
            issue=40,
        )
    issue5_status = (
        "UNRESOLVED"
        if issue5_present
        else "REMEDIATED_PENDING_MERGE"
        if issue5_state == "OPEN"
        else "REMEDIATED"
    )
    if additional_present:
        additional_status = (
            "PARTIALLY_REMEDIATED"
            if additional_present != expected_additional_ids
            else "UNRESOLVED"
        )
    else:
        additional_status = (
            "REMEDIATED_PENDING_MERGE" if additional_state == "OPEN" else "REMEDIATED"
        )
    related_status = (
        "UNRESOLVED"
        if related_present
        else "REMEDIATED_PENDING_MERGE"
        if related_state == "OPEN"
        else "REMEDIATED"
        if related_tracking_required
        else "NOT_APPLICABLE"
    )
    removed_ids = base_ids - branch_ids
    if branch_ids:
        dependency_security_status = "BLOCKED"
        release_candidate_status = "BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION"
    elif issue5_state == "OPEN" or additional_state == "OPEN" or (
        related_tracking_required and related_state == "OPEN"
    ):
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
            else f"Issue #{expected_related_number}"
            if advisory_id in expected_related_ids
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
                "state": additional_state,
                "title": additional_issue.get("title"),
                "url": additional_issue.get("url"),
                "tracked_advisories": len(expected_additional_ids),
                "remediation_status": additional_status,
            },
            *(
                [
                    {
                        "id": f"Issue #{expected_related_number}",
                        "state": related_state,
                        "title": related_issue.get("title"),
                        "url": related_issue.get("url"),
                        "tracked_advisories": 1,
                        "remediation_status": related_status,
                    }
                ]
                if related_tracking_required
                else []
            ),
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
                    and additional_state == "OPEN"
                )
                or (
                    tracking_by_id[advisory_id] == f"Issue #{expected_related_number}"
                    and related_state == "OPEN"
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
        "related_security_issue": (
            {"number": expected_related_number, "url": related_issue.get("url")}
            if related_tracking_required
            else None
        ),
        "issue_5_remediation_status": issue5_status,
        "issue_30_remediation_status": (
            additional_status
            if int(additional_issue["number"]) == 30
            else "HISTORICAL_REMEDIATED"
        ),
        "additional_security_remediation_status": additional_status,
        "related_security_remediation_status": related_status,
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
    execution_provenance: dict[str, Any],
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
        "security_remediation_id": security["remediation_id"],
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
        "sharp_runtime_smoke": security["sharp_runtime_smoke"],
        "sharp_override_selector": security["dependency_diff"].get(
            "sharp_override_selector"
        ),
        "sharp_override_version": security["dependency_diff"].get(
            "sharp_override_version"
        ),
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
        "execution_provenance": execution_provenance,
        "blocked_ids": p0["ids_by_status"]["BLOCKED"],
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


def apply_owner_approval(report: dict[str, Any], approval: dict[str, Any], mode: str) -> dict[str, Any]:
    if mode == NORMAL_RELEASE_EVIDENCE:
        expected_dependency_status = "PASSED"
        transition = {
            "release_candidate_status": "APPROVED_FOR_MVP0_INTERNAL",
            "technical_gate_outcome": "OWNER_APPROVED_INTERNAL_RELEASE",
            "result": "REL000_OWNER_APPROVED",
        }
    elif mode == SECURITY_REMEDIATION:
        expected_dependency_status = "REMEDIATED_PENDING_MERGE"
        transition = {
            "release_candidate_status": "BLOCKED_BY_SECURITY_REMEDIATION_MERGE",
            "technical_gate_outcome": "SECURITY_REMEDIATION_READY_FOR_MERGE",
            "result": "REL000_SECURITY_REMEDIATION_READY_FOR_MERGE",
        }
    else:
        fail("REL000_MODE_INVALID", "REL-000 owner approval requires an explicit supported mode.", mode=mode)
    if report.get("dependency_security_status") != expected_dependency_status:
        fail("OWNER_APPROVAL_DEPENDENCY_SECURITY_NOT_PASSED", "Dependency security must pass before owner approval.")
    if report.get("technical_evidence_status") != "PASSED":
        fail("OWNER_APPROVAL_TECHNICAL_EVIDENCE_NOT_PASSED", "Technical evidence must pass before owner approval.")
    if (
        report.get("mvp0_p0_items_expected") != 29
        or report.get("mvp0_p0_items_evaluated") != 29
        or report.get("mvp0_p0_items_verified") != 29
        or report.get("mvp0_p0_items_blocked") != 0
        or report.get("blocked_ids") != []
    ):
        fail("OWNER_APPROVAL_INVENTORY_INVALID", "Owner approval requires the exact 29/29/0 inventory.")
    record = approval["record"]
    artifact = approval["artifact"]
    ext001 = approval["ext001"]
    approved = copy.deepcopy(report)
    for issue in approved.get("known_security_issues") or []:
        issue.pop("url", None)
    additional_issue = approved.get("additional_security_tracking_issue")
    if isinstance(additional_issue, dict):
        additional_issue.pop("url", None)
    approved.update(
        {
            "approved_evidence_sha": APPROVED_EVIDENCE_MAIN_SHA,
            "approved_workflow_run_id": APPROVED_WORKFLOW_RUN_ID,
            "approved_workflow_run_attempt": APPROVED_WORKFLOW_RUN_ATTEMPT,
            "approved_artifact_id": APPROVED_ARTIFACT_ID,
            "approved_artifact_name": APPROVED_ARTIFACT_NAME,
            "approved_artifact_digest": APPROVED_ARTIFACT_DIGEST,
            "approved_evidence_storage": artifact["storage"],
            "approved_evidence_snapshot_manifest_path": artifact["manifest_path"],
            "approved_evidence_snapshot_file_count": artifact["snapshot_file_count"],
            "approved_evidence_live_artifact_required": artifact["live_artifact_required"],
            "approved_artifact_original_id": APPROVED_ARTIFACT_ID,
            "approved_artifact_original_digest": APPROVED_ARTIFACT_DIGEST,
            "approved_artifact_original_expires_at": artifact["original_expires_at"],
            "approved_evidence_ext001_source_path": ext001["path"],
            "approved_evidence_ext001_source_blob_sha": ext001["blob_sha"],
            "approved_evidence_ext001_started": False,
            "technical_artifact_contains_ext001_started": artifact[
                "technical_artifact_contains_ext001_started"
            ],
            "ext001_state_source": "VERSIONED_ITEM_EVIDENCE",
            "ext001_state_source_sha": ext001["source_sha"],
            "ext001_started": False,
            "decision_record_sha": approval["decision_record_sha"],
            "decision_record_blob_sha": approval["decision_record_blob_sha"],
            "decision_id": record["decision_id"],
            "decision_statement": record["decision_statement"],
            "decision_reason": record["decision_reason"],
            "decided_by": record["decided_by"],
            "decided_on": record["decided_on"],
            "release_scope": "MVP-0_INTERNAL",
            "synthetic_data_only": True,
            "pilot_authorized": False,
            "production_authorized": False,
            "deployment_authorized": False,
            "go_live_authorized": False,
            "real_customers_authorized": False,
            "real_pii_authorized": False,
            "real_pricing_authorized": False,
            "payments_authorized": False,
            "invoicing_authorized": False,
            "external_drivers_authorized": False,
            "owner_approval_status": "APPROVED",
            "rel000_status": "VERIFIED",
            "mvp0_approved": True,
            **transition,
        }
    )
    return approved


def validate_approved_owner_state(report: dict[str, Any]) -> None:
    expected = {
        "owner_approval_status": "APPROVED",
        "release_candidate_status": "APPROVED_FOR_MVP0_INTERNAL",
        "technical_gate_outcome": "OWNER_APPROVED_INTERNAL_RELEASE",
        "result": "REL000_OWNER_APPROVED",
        "dependency_security_status": "PASSED",
        "technical_evidence_status": "PASSED",
        "approved_evidence_storage": "VERSIONED_REDACTED_SNAPSHOT",
        "approved_evidence_snapshot_manifest_path": APPROVED_SNAPSHOT_MANIFEST_PATH,
        "approved_evidence_snapshot_file_count": 4,
        "approved_evidence_live_artifact_required": False,
        "approved_artifact_original_id": APPROVED_ARTIFACT_ID,
        "approved_artifact_original_digest": APPROVED_ARTIFACT_DIGEST,
        "approved_artifact_original_expires_at": APPROVED_ARTIFACT_EXPIRES_AT,
        "release_scope": "MVP-0_INTERNAL",
        "synthetic_data_only": True,
        "pilot_authorized": False,
        "production_authorized": False,
        "ext001_started": False,
        "rel000_status": "VERIFIED",
        "mvp0_approved": True,
        "mvp0_p0_items_expected": 29,
        "mvp0_p0_items_evaluated": 29,
        "mvp0_p0_items_verified": 29,
        "mvp0_p0_items_blocked": 0,
        "blocked_ids": [],
    }
    if any(report.get(key) != value for key, value in expected.items()):
        fail("OWNER_APPROVAL_STATE_INVALID", "The approved internal release state is inconsistent.")
    for key in (
        "deployment_authorized",
        "go_live_authorized",
        "real_customers_authorized",
        "real_pii_authorized",
        "real_pricing_authorized",
        "payments_authorized",
        "invoicing_authorized",
        "external_drivers_authorized",
    ):
        if report.get(key) is not False:
            fail("OWNER_APPROVAL_SCOPE_INVALID", "The approved report expands internal MVP-0 scope.", field=key)


def validate_security_remediation_owner_state(report: dict[str, Any]) -> None:
    expected = {
        "owner_approval_status": "APPROVED",
        "release_candidate_status": "BLOCKED_BY_SECURITY_REMEDIATION_MERGE",
        "technical_gate_outcome": "SECURITY_REMEDIATION_READY_FOR_MERGE",
        "result": "REL000_SECURITY_REMEDIATION_READY_FOR_MERGE",
        "dependency_security_status": "REMEDIATED_PENDING_MERGE",
        "technical_evidence_status": "PASSED",
        "approved_evidence_storage": "VERSIONED_REDACTED_SNAPSHOT",
        "approved_evidence_snapshot_manifest_path": APPROVED_SNAPSHOT_MANIFEST_PATH,
        "approved_evidence_snapshot_file_count": 4,
        "approved_evidence_live_artifact_required": False,
        "approved_artifact_original_id": APPROVED_ARTIFACT_ID,
        "approved_artifact_original_digest": APPROVED_ARTIFACT_DIGEST,
        "approved_artifact_original_expires_at": APPROVED_ARTIFACT_EXPIRES_AT,
        "release_scope": "MVP-0_INTERNAL",
        "synthetic_data_only": True,
        "pilot_authorized": False,
        "production_authorized": False,
        "ext001_started": False,
        "rel000_status": "VERIFIED",
        "mvp0_approved": True,
        "mvp0_p0_items_expected": 29,
        "mvp0_p0_items_evaluated": 29,
        "mvp0_p0_items_verified": 29,
        "mvp0_p0_items_blocked": 0,
        "blocked_ids": [],
    }
    if any(report.get(key) != value for key, value in expected.items()):
        fail(
            "SECURITY_REMEDIATION_OWNER_STATE_INVALID",
            "The approved security remediation state is inconsistent.",
        )
    for key in (
        "deployment_authorized",
        "go_live_authorized",
        "real_customers_authorized",
        "real_pii_authorized",
        "real_pricing_authorized",
        "payments_authorized",
        "invoicing_authorized",
        "external_drivers_authorized",
    ):
        if report.get(key) is not False:
            fail(
                "OWNER_APPROVAL_SCOPE_INVALID",
                "The approved report expands internal MVP-0 scope.",
                field=key,
            )


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


def validate_mvp0_extension_state(raw_item_evidence: dict[str, Any]) -> None:
    if "ext001_started" not in raw_item_evidence:
        fail("EXT001_STARTED_FIELD_MISSING", "The approved MVP-0 item evidence omits ext001_started.")
    if not isinstance(raw_item_evidence["ext001_started"], bool):
        fail("EXT001_STARTED_FIELD_TYPE_INVALID", "ext001_started must be a JSON boolean.")
    if raw_item_evidence["ext001_started"]:
        fail("EXT001_ALREADY_STARTED", "The approved MVP-0 evidence indicates EXT-001 had already started.")


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
    authorization = validate_mode_authorization(
        args.mode,
        policy,
        args.source_branch,
        trace["base_main_sha"],
        args.remediation_id,
    )
    normative = load_normative(repository_root)
    normative_evidence = validate_normative_checksums(normative["root"])
    selected, all_items = normative_items(normative)
    item_input = load_json(args.item_evidence)
    validate_mvp0_extension_state(item_input)
    issue = load_json(args.issue5)
    additional_issue = load_json(args.security_tracking_issue)
    job_results = json.loads(args.job_results_json)
    validate_jobs(job_results)
    execution_results, execution_artifacts, execution_provenance = load_execution_evidence(
        args.execution_results_directory,
        args.workflow_provenance,
        trace,
        args.workflow_run_id,
        args.workflow_run_attempt,
    )
    focused_tests = validate_focused_test_results(
        load_json(args.python_test_results),
        load_json(args.physical_test_results),
    )
    owner_approval = validate_owner_approval(
        repository_root,
        args.decision_record,
        args.approved_evidence_manifest,
        args.approved_evidence_directory,
        issue,
        additional_issue,
        trace["source_head_sha"],
        authorization,
    )
    p0 = validate_item_evidence(
        repository_root,
        selected,
        all_items,
        item_input,
        trace["base_main_sha"],
        job_results,
        execution_results,
        owner_approval,
        focused_tests,
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
    decisions = validate_decisions(normative["gates"])
    base_audit = load_json(args.base_audit)
    branch_audit = load_json(args.branch_audit)
    dependency_diff = validate_dependency_diff(
        repository_root,
        trace["base_main_sha"],
        args.mode,
        authorization,
    )
    security = validate_issue_and_audit(
        issue,
        additional_issue,
        base_audit,
        branch_audit,
        dependency_diff,
        args.mode,
        authorization,
    )
    security["sharp_runtime_smoke"] = validate_sharp_runtime_smoke(
        load_json(args.sharp_runtime_smoke) if args.sharp_runtime_smoke else None,
        args.mode,
        authorization,
    )
    security["remediation_id"] = authorization["id"] if authorization else None

    ops001_artifact = validate_artifact(
        args.ops001_directory,
        args.ops001_artifact_name,
        args.ops001_artifact_id,
        args.ops001_artifact_digest,
        trace,
        args.workflow_run_id,
        str(execution_artifacts["delivery-simulation"]["producer_attempt"]),
        lambda path: path.endswith(".json") or path.endswith(".trx"),
    )
    ops002_artifact = validate_artifact(
        args.ops002_directory,
        args.ops002_artifact_name,
        args.ops002_artifact_id,
        args.ops002_artifact_digest,
        trace,
        args.workflow_run_id,
        str(execution_artifacts["backup-restore"]["producer_attempt"]),
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
        execution_provenance,
        focused_tests,
    )
    report = apply_owner_approval(report, owner_approval, args.mode)
    if args.mode == NORMAL_RELEASE_EVIDENCE:
        validate_approved_owner_state(report)
    elif args.mode == SECURITY_REMEDIATION:
        validate_security_remediation_owner_state(report)
    else:
        fail("REL000_MODE_INVALID", "REL-000 validation requires an explicit supported mode.", mode=args.mode)
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


def replay_execution_provenance(args: argparse.Namespace) -> dict[str, Any]:
    trace = {
        "source_head_sha": validate_sha(
            args.source_head_sha, "SOURCE_HEAD_SHA_MALFORMED", "source_head_sha"
        ),
        "tested_git_sha": validate_sha(
            args.tested_git_sha, "TESTED_GIT_SHA_MALFORMED", "tested_git_sha"
        ),
        "base_main_sha": validate_sha(
            args.base_main_sha, "BASE_MAIN_SHA_MALFORMED", "base_main_sha"
        ),
    }
    tests, artifacts, provenance = load_execution_evidence(
        args.execution_results_directory,
        args.workflow_provenance,
        trace,
        args.workflow_run_id,
        args.workflow_run_attempt,
    )
    result = {
        "result": "HISTORICAL_PARTIAL_RERUN_PROVENANCE_REPLAY_PASSED",
        "workflow_run_id": str(args.workflow_run_id),
        "aggregator_attempt": provenance["aggregator_attempt"],
        "mixed_attempt_evidence": provenance["mixed_attempt_evidence"],
        "artifacts_validated": len(artifacts),
        "tests_validated": len(tests),
    }
    print(json.dumps(result, sort_keys=True))
    return result


def sanitize_audit(
    input_path: Path,
    output_path: Path,
    command_executed: bool,
    command_exit_code: int,
    nuget_input_paths: list[Path] | None = None,
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
    for nuget_path in nuget_input_paths or []:
        nuget = load_json(nuget_path)
        projects = nuget.get("projects") if isinstance(nuget, dict) else None
        if nuget.get("version") != 1 or not isinstance(projects, list):
            fail("AUDIT_PARSE_FAILED", "The NuGet audit JSON is invalid.", file=nuget_path.name)
        for project in projects:
            for framework in project.get("frameworks") or []:
                for collection, dependency_kind in (
                    ("topLevelPackages", "direct"),
                    ("transitivePackages", "transitive"),
                ):
                    for package in framework.get(collection) or []:
                        package_id = str(package.get("id") or "")
                        installed_version = str(package.get("resolvedVersion") or "")
                        for vulnerability in package.get("vulnerabilities") or []:
                            url = str(vulnerability.get("advisoryurl") or "")
                            match = re.search(r"GHSA-[0-9a-z-]+", url, re.IGNORECASE)
                            if not match or not package_id or not installed_version:
                                fail(
                                    "AUDIT_PARSE_FAILED",
                                    "A NuGet vulnerability entry is incomplete.",
                                    file=nuget_path.name,
                                )
                            advisory_id = match.group(0).upper()
                            patched_range = (
                                ">=2026.0.0"
                                if advisory_id == "GHSA-Q939-RPR3-3284"
                                else "consult-advisory"
                            )
                            advisories.append(
                                {
                                    "advisory_id": advisory_id,
                                    "package": package_id,
                                    "installed_versions": [installed_version],
                                    "severity": str(vulnerability.get("severity") or "").lower(),
                                    "affected_range": installed_version,
                                    "patched_range": patched_range,
                                    "direct_or_transitive": dependency_kind,
                                    "dependency_path_count": 1,
                                    "fix_available": advisory_id == "GHSA-Q939-RPR3-3284",
                                    "fix_compatibility": (
                                        "compatible_patch_available"
                                        if advisory_id == "GHSA-Q939-RPR3-3284"
                                        else "requires_compatibility_assessment"
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


def sanitize_issue(
    input_path: Path,
    output_path: Path,
    related_input_path: Path | None = None,
) -> None:
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
    if related_input_path is not None:
        related_output = output_path.with_name(f"{output_path.stem}-related.json")
        sanitize_issue(related_input_path, related_output)
        sanitized["related_issue"] = load_json(related_output)
        related_output.unlink()
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
    audit.add_argument("--nuget-input", type=Path, action="append")

    issue = subparsers.add_parser("sanitize-issue")
    issue.add_argument("--input", type=Path, required=True)
    issue.add_argument("--output", type=Path, required=True)
    issue.add_argument("--related-input", type=Path)

    resolve_mode = subparsers.add_parser("resolve-mode")
    resolve_mode.add_argument("--policy", type=Path, required=True)
    resolve_mode.add_argument("--source-branch", required=True)
    resolve_mode.add_argument("--remediation-id", default="")

    resolve_provenance = subparsers.add_parser("resolve-tested-provenance")
    resolve_provenance.add_argument("--repository-root", type=Path, default=Path("."))
    resolve_provenance.add_argument("--event-name", required=True)
    resolve_provenance.add_argument("--tested-git-sha", required=True)
    resolve_provenance.add_argument("--source-head-sha", required=True)
    resolve_provenance.add_argument("--event-pull-request-base-sha", default="")
    resolve_provenance.add_argument("--output", type=Path)
    resolve_provenance.add_argument("--github-env", type=Path)

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

    workflow_provenance = subparsers.add_parser("sanitize-workflow-provenance")
    workflow_provenance.add_argument("--attempts-directory", type=Path, required=True)
    workflow_provenance.add_argument("--artifacts", type=Path, required=True)
    artifact_outputs_source = workflow_provenance.add_mutually_exclusive_group(required=True)
    artifact_outputs_source.add_argument("--artifact-outputs-json")
    artifact_outputs_source.add_argument("--artifact-outputs", type=Path)
    workflow_provenance.add_argument("--output", type=Path, required=True)
    workflow_provenance.add_argument("--workflow-run-id", required=True)
    workflow_provenance.add_argument("--current-attempt", required=True)
    workflow_provenance.add_argument("--head-sha", required=True)
    workflow_provenance.add_argument("--workflow-profile", default=DEFAULT_WORKFLOW_PROVENANCE_PROFILE)

    replay = subparsers.add_parser("replay-execution-provenance")
    replay.add_argument("--execution-results-directory", type=Path, required=True)
    replay.add_argument("--workflow-provenance", type=Path, required=True)
    replay.add_argument("--workflow-run-id", required=True)
    replay.add_argument("--workflow-run-attempt", required=True)
    replay.add_argument("--source-head-sha", required=True)
    replay.add_argument("--tested-git-sha", required=True)
    replay.add_argument("--base-main-sha", required=True)

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

    capture = subparsers.add_parser("capture-approved-evidence")
    capture.add_argument("--metadata", type=Path, required=True)
    capture.add_argument("--artifact-zip", type=Path, required=True)
    capture.add_argument("--output-directory", type=Path, required=True)

    validate = subparsers.add_parser("validate")
    validate.add_argument("--repository-root", type=Path, required=True)
    validate.add_argument("--decision-record", type=Path, required=True)
    validate.add_argument("--approved-evidence-manifest", type=Path, required=True)
    validate.add_argument("--approved-evidence-directory", type=Path, required=True)
    validate.add_argument("--item-evidence", type=Path, required=True)
    validate.add_argument("--cross-tenant-evidence", type=Path, required=True)
    validate.add_argument("--rollback-evidence", type=Path, required=True)
    validate.add_argument("--rollback-execution", type=Path, required=True)
    validate.add_argument("--python-test-results", type=Path, required=True)
    validate.add_argument("--physical-test-results", type=Path, required=True)
    validate.add_argument("--execution-results-directory", type=Path, required=True)
    validate.add_argument(
        "--workflow-provenance",
        "--execution-artifacts",
        dest="workflow_provenance",
        type=Path,
        required=True,
    )
    validate.add_argument("--ops001-directory", type=Path, required=True)
    validate.add_argument("--ops002-directory", type=Path, required=True)
    validate.add_argument("--output-directory", type=Path, required=True)
    validate.add_argument("--issue5", type=Path, required=True)
    validate.add_argument("--security-tracking-issue", type=Path, required=True)
    validate.add_argument("--base-audit", type=Path, required=True)
    validate.add_argument("--branch-audit", type=Path, required=True)
    validate.add_argument("--sharp-runtime-smoke", type=Path)
    validate.add_argument("--mode", choices=sorted(REL000_MODES), required=True)
    validate.add_argument("--source-branch", required=True)
    validate.add_argument("--remediation-id", default="")
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
                args.nuget_input,
            )
            return 0
        if args.command == "sanitize-issue":
            sanitize_issue(args.input, args.output, args.related_input)
            return 0
        if args.command == "resolve-mode":
            print(
                resolve_rel000_mode(
                    load_remediation_policy(args.policy),
                    args.source_branch,
                    args.remediation_id,
                )
            )
            return 0
        if args.command == "resolve-tested-provenance":
            resolve_tested_provenance_command(args)
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
        if args.command == "sanitize-workflow-provenance":
            artifact_outputs_json = (
                args.artifact_outputs.read_text(encoding="utf-8-sig")
                if args.artifact_outputs is not None
                else args.artifact_outputs_json
            )
            sanitize_workflow_provenance(
                args.attempts_directory,
                args.artifacts,
                artifact_outputs_json,
                args.output,
                args.workflow_run_id,
                args.current_attempt,
                args.head_sha,
                args.workflow_profile,
            )
            return 0
        if args.command == "replay-execution-provenance":
            replay_execution_provenance(args)
            return 0
        if args.command == "synthetic-generate":
            synthetic_generation(args)
            return 0
        if args.command == "capture-approved-evidence":
            capture_approved_evidence_command(args)
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
