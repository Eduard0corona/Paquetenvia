#!/usr/bin/env python3
"""Fail-closed terminal gate for future ``pull_request`` → ``development`` validation.

The gate receives the classifier plan and the ``needs`` context of the terminal job and
decides whether every validation the plan required actually succeeded. It never relies
on GitHub treating a skipped job as passing: a required job that was skipped fails the
gate, and a job that ran although the plan did not require it fails the gate too,
because that means the workflow's job conditions disagree with the plan.

Contract (``needs`` JSON as produced by ``toJSON(needs)``):

* ``classify`` must be present with ``result == success`` and a parsable plan;
* every job listed in the configuration must be present, no other job may be present;
* ``secret-scan`` must be ``success``;
* every required job must be ``success``;
* every non-required job must be ``skipped``;
* a plan touching the ``DEPS`` domain is rejected unless the classification is a
  certified ``MAIN_BACKSYNC`` (Owner dependency policy: ordinary and security
  dependency remediations take the exceptional ``→ main`` route, never
  ``development``; only content already certified on ``main`` may carry
  dependency drift into ``development``). A NuGet lockfile whose content the
  classifier proved to change only the internal ``type: Project`` graph is carried by
  ``NUGET_PROJECT_GRAPH`` instead of ``DEPS``; an unproven lockfile stays ``DEPS``.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))

import classify_changes as classifier  # noqa: E402

CLASSIFY_JOB = "classify"
SECRET_SCAN_JOB = "secret-scan"
RESULT_SUCCESS = "success"
RESULT_SKIPPED = "skipped"
KNOWN_RESULTS = {RESULT_SUCCESS, RESULT_SKIPPED, "failure", "cancelled"}

REASON_CLASSIFICATION_UNTRUSTED = "PR_GATE_CLASSIFICATION_UNTRUSTED"
REASON_SECRET_SCAN_FAILED = "PR_GATE_SECRET_SCAN_FAILED"
REASON_REQUIRED_JOB_SKIPPED = "PR_GATE_REQUIRED_JOB_SKIPPED"
REASON_REQUIRED_JOB_FAILED = "PR_GATE_REQUIRED_JOB_FAILED"
REASON_UNEXPECTED_JOB_RESULT = "PR_GATE_UNEXPECTED_JOB_RESULT"
REASON_DEPENDENCY_CHANGE_NOT_ALLOWED = "PR_GATE_DEPENDENCY_CHANGE_NOT_ALLOWED"
DEPENDENCY_DOMAIN = "DEPS"


def _result_of(needs: dict[str, Any], job: str) -> str | None:
    entry = needs.get(job)
    if not isinstance(entry, dict):
        return None
    result = entry.get("result")
    return result if isinstance(result, str) else None


def evaluate(plan_json: str | None, needs: Any, config: dict[str, Any]) -> dict[str, Any]:
    """Evaluate the gate. Returns a verdict dict; never raises for contract violations."""
    failures: list[dict[str, Any]] = []
    matrix: list[dict[str, Any]] = []

    def failure(reason: str, message: str, **details: Any) -> None:
        failures.append({"reason": reason, "message": message, **details})

    if not isinstance(needs, dict):
        failure(REASON_UNEXPECTED_JOB_RESULT, "needs must be a JSON object.")
        return {"verdict": "FAIL", "failures": failures, "matrix": matrix, "plan": None}

    classify_result = _result_of(needs, CLASSIFY_JOB)
    if classify_result != RESULT_SUCCESS:
        failure(REASON_CLASSIFICATION_UNTRUSTED, "The classifier did not succeed.", result=classify_result)
        return {"verdict": "FAIL", "failures": failures, "matrix": matrix, "plan": None}

    if plan_json is None:
        outputs = needs[CLASSIFY_JOB].get("outputs")
        plan_json = outputs.get("plan") if isinstance(outputs, dict) else None
    plan: dict[str, Any] | None = None
    try:
        if not isinstance(plan_json, str) or not plan_json.strip():
            raise classifier.ClassifyError("PLAN_MISSING", "The classifier produced no plan output.")
        parsed = json.loads(plan_json)
        plan = classifier.validate_plan(parsed, config)
    except (ValueError, classifier.ClassifyError) as error:
        failure(REASON_CLASSIFICATION_UNTRUSTED, "The plan cannot be trusted.", cause=str(error))
        return {"verdict": "FAIL", "failures": failures, "matrix": matrix, "plan": None}

    # Dependency policy: dependency drift never enters `development` through a feature
    # or security-remediation PR, whatever its classification or branch name. Only a
    # certified MAIN_BACKSYNC (source already 13/13 on push/main) may carry it.
    if DEPENDENCY_DOMAIN in plan["domains"] and plan["classification"] != classifier.CLASSIFICATION_MAIN_BACKSYNC:
        failure(
            REASON_DEPENDENCY_CHANGE_NOT_ALLOWED,
            "Dependency changes are not accepted into development; use the authorized dependency route to main.",
            classification=plan["classification"],
        )

    expected_jobs = list(config["jobs"])
    present = set(needs)
    unexpected = sorted(present - set(expected_jobs) - {CLASSIFY_JOB})
    missing = sorted(set(expected_jobs) - present)
    if unexpected:
        failure(REASON_UNEXPECTED_JOB_RESULT, "needs contains jobs the gate does not know.", jobs=unexpected)
    if missing:
        failure(REASON_UNEXPECTED_JOB_RESULT, "needs is missing jobs the gate expects.", jobs=missing)

    required = set(plan["required_jobs"])
    for job in expected_jobs:
        if job in missing:
            continue
        result = _result_of(needs, job)
        is_required = job in required
        verdict = "OK"
        if result not in KNOWN_RESULTS:
            verdict = REASON_UNEXPECTED_JOB_RESULT
            failure(REASON_UNEXPECTED_JOB_RESULT, "Job reported an unknown result.", job=job, result=result)
        elif job == SECRET_SCAN_JOB and result != RESULT_SUCCESS:
            verdict = REASON_SECRET_SCAN_FAILED
            failure(REASON_SECRET_SCAN_FAILED, "The secret scan must succeed on every pull request.", result=result)
        elif is_required and result == RESULT_SKIPPED:
            verdict = REASON_REQUIRED_JOB_SKIPPED
            failure(REASON_REQUIRED_JOB_SKIPPED, "A required job was skipped.", job=job)
        elif is_required and result != RESULT_SUCCESS:
            verdict = REASON_REQUIRED_JOB_FAILED
            failure(REASON_REQUIRED_JOB_FAILED, "A required job did not succeed.", job=job, result=result)
        elif not is_required and result != RESULT_SKIPPED:
            verdict = REASON_UNEXPECTED_JOB_RESULT
            failure(
                REASON_UNEXPECTED_JOB_RESULT,
                "A job the plan did not require ran; the workflow conditions disagree with the plan.",
                job=job,
                result=result,
            )
        matrix.append({"job": job, "required": is_required, "result": result, "verdict": verdict})

    return {
        "verdict": "FAIL" if failures else "PASS",
        "failures": failures,
        "matrix": matrix,
        "plan": plan,
    }


def format_matrix(evaluation: dict[str, Any]) -> str:
    plan = evaluation.get("plan")
    lines = []
    if plan is not None:
        lines.append(f"classification: {plan['classification']}")
        lines.append(f"domains: {', '.join(plan['domains']) or '-'}")
        lines.append(f"reasons: {', '.join(plan['reasons']) or '-'}")
        if plan["unmatched_paths"]:
            lines.append(f"unmatched_paths: {', '.join(plan['unmatched_paths'])}")
    header = f"{'job':<26}{'required':<10}{'result':<11}verdict"
    lines.append(header)
    lines.append("-" * len(header))
    for row in evaluation["matrix"]:
        lines.append(
            f"{row['job']:<26}{('yes' if row['required'] else 'no'):<10}{str(row['result']):<11}{row['verdict']}"
        )
    for item in evaluation["failures"]:
        details = {key: value for key, value in item.items() if key not in {"reason", "message"}}
        suffix = f" {json.dumps(details, sort_keys=True)}" if details else ""
        lines.append(f"FAIL {item['reason']}: {item['message']}{suffix}")
    lines.append(f"PR Gate: {evaluation['verdict']}")
    return "\n".join(lines)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--config", default=str(classifier.DEFAULT_CONFIG_PATH))
    parser.add_argument("--needs-json", required=True, help="toJSON(needs) of the gate job")
    parser.add_argument("--plan-json", default=None, help="classifier plan; defaults to needs.classify.outputs.plan")
    parser.add_argument("--summary", default=None, help="append the matrix to this file (e.g. $GITHUB_STEP_SUMMARY)")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        config = classifier.load_config(Path(args.config))
    except classifier.ClassifyError as error:
        print(f"FAIL {REASON_CLASSIFICATION_UNTRUSTED}: {error}", file=sys.stderr)
        return 1
    try:
        needs = json.loads(args.needs_json)
    except ValueError as error:
        print(f"FAIL {REASON_UNEXPECTED_JOB_RESULT}: needs JSON is unreadable: {error}", file=sys.stderr)
        return 1
    evaluation = evaluate(args.plan_json, needs, config)
    text = format_matrix(evaluation)
    print(text)
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as handle:
            handle.write("```\n" + text + "\n```\n")
    return 0 if evaluation["verdict"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
