#!/usr/bin/env python3
"""Main source-branch guard (implemented ahead of enforcement).

For a ``pull_request`` whose base is ``main`` the normal route is ``development``. The
only other admissible sources are security remediation branches and ACTIVE dependency
admission branches (GOV-DEPENDENCY-ADMISSION-001) that the REL-000 policy already
authorizes **on the tested base commit** (``tested_git_sha^1``). The policy is read with
``git show <base>:<path>``, never from the pull request's own tree, so a branch can never
authorize itself by editing the policy in the same pull request.

Outcomes (JSON on stdout, exit 0 on PASS, exit 1 on FAIL):

* ``MAIN_SOURCE_BASE_NOT_MAIN``            PASS  base is not main → guard does not apply
* ``MAIN_SOURCE_DEVELOPMENT``              PASS  head is ``development``
* ``MAIN_SOURCE_AUTHORIZED_REMEDIATION``   PASS  head is an active authorized remediation branch
* ``MAIN_SOURCE_AUTHORIZED_DEPENDENCY_ADMISSION``
                                         PASS  head is an ACTIVE, unexpired dependency admission branch
* ``MAIN_SOURCE_FORK_FORBIDDEN``           FAIL  head repository differs from the repository
* ``MAIN_SOURCE_NOT_DEVELOPMENT``          FAIL  any other head
* ``MAIN_SOURCE_POLICY_UNAVAILABLE``       FAIL  base policy missing/unreadable/malformed
* ``MAIN_SOURCE_INPUT_INVALID``            FAIL  malformed inputs

This module is not wired into Foundation yet; enforcement is a later slice.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any

MAIN_BRANCH = "main"
DEVELOPMENT_BRANCH = "development"
DEFAULT_POLICY_PATH = "tools/rel-000/security-remediation-policy.json"
# Mirrors tools/rel-000/rel000.py REMEDIATION_POLICY_FORMAT / modes (checked by tests).
REMEDIATION_POLICY_FORMAT = "paquetenvia-rel000-security-remediation-policy-v3"
# A v2 base (before dependency admissions existed) is still readable; it admits nothing.
LEGACY_REMEDIATION_POLICY_FORMATS = frozenset({"paquetenvia-rel000-security-remediation-policy-v2"})
SECURITY_REMEDIATION = "SECURITY_REMEDIATION"
DEPENDENCY_ADMISSION = "DEPENDENCY_ADMISSION"
NORMAL_RELEASE_EVIDENCE = "NORMAL_RELEASE_EVIDENCE"
ACTIVE_STATUSES = {"ACTIVE"}
SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")

REASON_BASE_NOT_MAIN = "MAIN_SOURCE_BASE_NOT_MAIN"
REASON_DEVELOPMENT = "MAIN_SOURCE_DEVELOPMENT"
REASON_AUTHORIZED_REMEDIATION = "MAIN_SOURCE_AUTHORIZED_REMEDIATION"
REASON_AUTHORIZED_DEPENDENCY_ADMISSION = "MAIN_SOURCE_AUTHORIZED_DEPENDENCY_ADMISSION"
REASON_FORK_FORBIDDEN = "MAIN_SOURCE_FORK_FORBIDDEN"
REASON_NOT_DEVELOPMENT = "MAIN_SOURCE_NOT_DEVELOPMENT"
REASON_POLICY_UNAVAILABLE = "MAIN_SOURCE_POLICY_UNAVAILABLE"
REASON_INPUT_INVALID = "MAIN_SOURCE_INPUT_INVALID"


class GuardError(Exception):
    def __init__(self, reason: str, message: str, **details: Any) -> None:
        super().__init__(f"{reason}: {message}")
        self.reason = reason
        self.message = message
        self.details = details


def _verdict(result: str, reason: str, message: str, **details: Any) -> dict[str, Any]:
    return {"result": result, "reason": reason, "message": message, "details": details}


def _run_git(repo_root: Path, *args: str) -> str:
    completed = subprocess.run(
        ["git", "-C", str(repo_root), *args],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if completed.returncode != 0:
        raise GuardError(
            REASON_POLICY_UNAVAILABLE,
            "A git command required to read the base policy failed.",
            command=["git", *args],
            stderr=completed.stderr.strip(),
        )
    return completed.stdout


def read_base_policy(repo_root: Path, tested_git_sha: str, policy_path: str) -> dict[str, Any]:
    """Read the policy exactly as committed on the tested base (first parent)."""
    tested = str(tested_git_sha or "").strip()
    if not SHA_PATTERN.match(tested):
        raise GuardError(REASON_INPUT_INVALID, "tested_git_sha must be a lowercase 40-hex commit SHA.")
    parents = _run_git(repo_root, "rev-list", "--parents", "-n", "1", tested).split()
    if len(parents) != 3 or parents[0] != tested:
        raise GuardError(
            REASON_POLICY_UNAVAILABLE,
            "The tested commit is not a two-parent merge; its base cannot be determined.",
            parents=parents[1:],
        )
    base = parents[1]
    text = _run_git(repo_root, "show", f"{base}:{policy_path}")
    try:
        policy = json.loads(text)
    except ValueError as error:
        raise GuardError(REASON_POLICY_UNAVAILABLE, "The base policy is not valid JSON.", cause=str(error))
    return validate_policy(policy)


def validate_policy(policy: Any) -> dict[str, Any]:
    if not isinstance(policy, dict):
        raise GuardError(REASON_POLICY_UNAVAILABLE, "The policy must be a JSON object.")
    format_version = policy.get("format_version")
    if format_version != REMEDIATION_POLICY_FORMAT and format_version not in LEGACY_REMEDIATION_POLICY_FORMATS:
        raise GuardError(REASON_POLICY_UNAVAILABLE, "Unknown policy format.", format_version=format_version)
    if policy.get("default_mode") != NORMAL_RELEASE_EVIDENCE:
        raise GuardError(REASON_POLICY_UNAVAILABLE, "The policy must fail closed to normal mode.")
    active = policy.get("active_remediations")
    if not isinstance(active, list):
        raise GuardError(REASON_POLICY_UNAVAILABLE, "active_remediations must be an array.")
    for entry in active:
        if not isinstance(entry, dict):
            raise GuardError(REASON_POLICY_UNAVAILABLE, "Each active remediation must be an object.")
        branch = entry.get("authorized_source_branch")
        if not isinstance(entry.get("id"), str) or not entry["id"].strip():
            raise GuardError(REASON_POLICY_UNAVAILABLE, "An active remediation lacks an id.")
        if not isinstance(branch, str) or not branch.strip():
            raise GuardError(REASON_POLICY_UNAVAILABLE, "An active remediation lacks an authorized source branch.", id=entry.get("id"))
        if "status" in entry and not isinstance(entry["status"], str):
            raise GuardError(REASON_POLICY_UNAVAILABLE, "A remediation status must be a string.", id=entry["id"])
        if "expires" in entry and not isinstance(entry["expires"], str):
            raise GuardError(REASON_POLICY_UNAVAILABLE, "A remediation expiry must be a string.", id=entry["id"])
    admissions = policy.get("dependency_admissions")
    if format_version == REMEDIATION_POLICY_FORMAT and not isinstance(admissions, list):
        raise GuardError(REASON_POLICY_UNAVAILABLE, "dependency_admissions must be an array.")
    if format_version != REMEDIATION_POLICY_FORMAT and admissions is not None:
        raise GuardError(REASON_POLICY_UNAVAILABLE, "A v2 policy cannot declare dependency admissions.")
    for entry in admissions or []:
        if not isinstance(entry, dict):
            raise GuardError(REASON_POLICY_UNAVAILABLE, "Each dependency admission must be an object.")
        if not isinstance(entry.get("id"), str) or not entry["id"].strip():
            raise GuardError(REASON_POLICY_UNAVAILABLE, "A dependency admission lacks an id.")
        branch = entry.get("authorized_source_branch")
        if not isinstance(branch, str) or not branch.strip():
            raise GuardError(REASON_POLICY_UNAVAILABLE, "A dependency admission lacks an authorized source branch.", id=entry["id"])
        if not isinstance(entry.get("status"), str):
            raise GuardError(REASON_POLICY_UNAVAILABLE, "A dependency admission status must be a string.", id=entry["id"])
        if "expires" in entry and not isinstance(entry["expires"], str):
            raise GuardError(REASON_POLICY_UNAVAILABLE, "A dependency admission expiry must be a string.", id=entry["id"])
    return policy


def _not_expired(entry: dict[str, Any], today: str | None) -> bool:
    """An entry without ``expires`` never expires; a malformed or past date fails closed."""
    expires = entry.get("expires")
    if expires is None:
        return True
    return today is not None and re.match(r"^\d{4}-\d{2}-\d{2}$", expires) is not None and expires >= today


def authorized_branches(policy: dict[str, Any], today: str | None = None) -> dict[str, str]:
    """Map authorized_source_branch → remediation id for entries that authorize now.

    Only ``active_remediations`` authorize. An entry with a ``status`` other than
    ``ACTIVE`` or a past ``expires`` date (ISO ``YYYY-MM-DD``) does not authorize.
    Historical remediations never authorize.
    """
    authorized: dict[str, str] = {}
    for entry in policy["active_remediations"]:
        if entry.get("mode") != SECURITY_REMEDIATION:
            continue
        status = entry.get("status")
        if status is not None and status not in ACTIVE_STATUSES:
            continue
        if not _not_expired(entry, today):
            continue
        authorized[entry["authorized_source_branch"]] = entry["id"]
    return authorized


def admitted_branches(policy: dict[str, Any], today: str | None = None) -> dict[str, str]:
    """Map authorized_source_branch → admission id for dependency admissions that authorize now.

    Only ``mode == DEPENDENCY_ADMISSION`` entries with ``status == "ACTIVE"`` (required,
    exact) and no past ``expires`` date authorize. ``MERGED`` admissions keep their
    packages admitted in REL-000 but never open another pull request into main.
    """
    admitted: dict[str, str] = {}
    for entry in policy.get("dependency_admissions") or []:
        if entry.get("mode") != DEPENDENCY_ADMISSION or entry.get("status") != "ACTIVE":
            continue
        if not _not_expired(entry, today):
            continue
        admitted[entry["authorized_source_branch"]] = entry["id"]
    return admitted


def evaluate(
    *,
    base_ref: str,
    head_ref: str,
    head_repo: str,
    repository: str,
    policy_loader,
    today: str | None = None,
) -> dict[str, Any]:
    base_ref = str(base_ref or "").strip()
    head_ref = str(head_ref or "").strip()
    head_repo = str(head_repo or "").strip()
    repository = str(repository or "").strip()
    if not base_ref or not head_ref or not repository:
        return _verdict("FAIL", REASON_INPUT_INVALID, "base_ref, head_ref and repository are required.")
    if base_ref != MAIN_BRANCH:
        return _verdict("PASS", REASON_BASE_NOT_MAIN, "The guard only applies to pull requests into main.", base_ref=base_ref)
    if not head_repo or head_repo != repository:
        return _verdict("FAIL", REASON_FORK_FORBIDDEN, "Pull requests into main must originate from this repository.", head_repo=head_repo, repository=repository)
    if head_ref == DEVELOPMENT_BRANCH:
        return _verdict("PASS", REASON_DEVELOPMENT, "Normal integration route development → main.")
    try:
        policy = policy_loader()
        authorized = authorized_branches(policy, today)
        admitted = admitted_branches(policy, today)
    except GuardError as error:
        return _verdict("FAIL", error.reason, error.message, **error.details)
    remediation_id = authorized.get(head_ref)
    if remediation_id is not None:
        return _verdict(
            "PASS",
            REASON_AUTHORIZED_REMEDIATION,
            "Head is an active security remediation branch authorized on the tested base.",
            remediation_id=remediation_id,
            head_ref=head_ref,
        )
    admission_id = admitted.get(head_ref)
    if admission_id is not None:
        return _verdict(
            "PASS",
            REASON_AUTHORIZED_DEPENDENCY_ADMISSION,
            "Head is an ACTIVE dependency admission branch authorized on the tested base.",
            admission_id=admission_id,
            head_ref=head_ref,
        )
    return _verdict(
        "FAIL",
        REASON_NOT_DEVELOPMENT,
        "Pull requests into main must come from development or from a base-authorized remediation or dependency admission branch.",
        head_ref=head_ref,
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--repo-root", default=".")
    parser.add_argument("--base-ref", required=True, help="github.base_ref")
    parser.add_argument("--head-ref", required=True, help="github.head_ref")
    parser.add_argument("--head-repo", required=True, help="github.event.pull_request.head.repo.full_name")
    parser.add_argument("--repository", required=True, help="github.repository")
    parser.add_argument("--tested-git-sha", required=True, help="github.sha (tested merge ref)")
    parser.add_argument("--policy-path", default=DEFAULT_POLICY_PATH)
    parser.add_argument("--today", default=None, help="ISO date used for expiry evaluation (tests)")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    repo_root = Path(args.repo_root).resolve()
    verdict = evaluate(
        base_ref=args.base_ref,
        head_ref=args.head_ref,
        head_repo=args.head_repo,
        repository=args.repository,
        policy_loader=lambda: read_base_policy(repo_root, args.tested_git_sha, args.policy_path),
        today=args.today or dt.date.today().isoformat(),
    )
    print(json.dumps(verdict, sort_keys=True))
    return 0 if verdict["result"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
