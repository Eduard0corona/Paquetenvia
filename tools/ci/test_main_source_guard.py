"""Unit tests for the main source-branch guard."""

from __future__ import annotations

import contextlib
import copy
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import main_source_guard as guard  # noqa: E402

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = "Eduard0corona/Paquetenvia"
POLICY_PATH = "tools/rel-000/security-remediation-policy.json"
COMMITTED_POLICY = json.loads((REPOSITORY_ROOT / POLICY_PATH).read_text(encoding="utf-8"))
AUTHORIZED_BRANCH = "fix/security-2026-09-next-critical"


def minimal_policy(active=None, historical=None):
    return {
        "format_version": guard.REMEDIATION_POLICY_FORMAT,
        "default_mode": guard.NORMAL_RELEASE_EVIDENCE,
        "historical_remediations": historical or [],
        "active_remediations": active or [],
    }


def remediation(branch, remediation_id="SEC-TEST", **extra):
    entry = {"id": remediation_id, "mode": guard.SECURITY_REMEDIATION, "authorized_source_branch": branch}
    entry.update(extra)
    return entry


def evaluate(head_ref, policy=None, base_ref="main", head_repo=REPOSITORY, repository=REPOSITORY, today="2026-09-21"):
    def loader():
        if isinstance(policy, Exception):
            raise policy
        return guard.validate_policy(copy.deepcopy(policy if policy is not None else minimal_policy()))

    return guard.evaluate(
        base_ref=base_ref, head_ref=head_ref, head_repo=head_repo, repository=repository, policy_loader=loader, today=today
    )


# --------------------------------------------------------------------------- git fixtures


def git(root: Path, *args: str) -> str:
    env = dict(os.environ)
    env.update(
        {
            "GIT_AUTHOR_NAME": "ci",
            "GIT_AUTHOR_EMAIL": "ci@example.invalid",
            "GIT_COMMITTER_NAME": "ci",
            "GIT_COMMITTER_EMAIL": "ci@example.invalid",
            "GIT_CONFIG_GLOBAL": os.devnull,
            "GIT_CONFIG_NOSYSTEM": "1",
        }
    )
    return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True, text=True, env=env).stdout.strip()


def make_pr_repo(base_policy, branch: str, pr_policy=None) -> dict[str, str]:
    """main (base_policy) ← merge ← branch (optionally rewriting the policy in the PR)."""
    root = Path(tempfile.mkdtemp(prefix="pv-guard-"))
    git(root, "init", "-q", "-b", "main")
    git(root, "config", "commit.gpgsign", "false")
    policy_file = root / POLICY_PATH
    policy_file.parent.mkdir(parents=True, exist_ok=True)
    if base_policy is not None:
        policy_file.write_text(json.dumps(base_policy, indent=2) + "\n", encoding="utf-8")
    (root / "README.md").write_text("base\n", encoding="utf-8")
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "base")
    git(root, "checkout", "-q", "-b", branch)
    if pr_policy is not None:
        policy_file.write_text(json.dumps(pr_policy, indent=2) + "\n", encoding="utf-8")
    (root / "change.txt").write_text("pr\n", encoding="utf-8")
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "pr")
    source = git(root, "rev-parse", "HEAD")
    git(root, "checkout", "-q", "main")
    git(root, "merge", "-q", "--no-ff", "-m", "tested merge", branch)
    tested = git(root, "rev-parse", "HEAD")
    git(root, "checkout", "-q", "--detach", tested)
    return {"root": str(root), "source": source, "tested": tested}


def run_cli(repo, head_ref, base_ref="main", head_repo=REPOSITORY):
    with contextlib.redirect_stdout(io.StringIO()) as out:
        code = guard.main(
            [
                "--repo-root", repo["root"],
                "--base-ref", base_ref,
                "--head-ref", head_ref,
                "--head-repo", head_repo,
                "--repository", REPOSITORY,
                "--tested-git-sha", repo["tested"],
            ]
        )
    return code, json.loads(out.getvalue())


# --------------------------------------------------------------------------- decision logic


class RouteTests(unittest.TestCase):
    def test_development_to_main_passes(self):
        verdict = evaluate("development")
        self.assertEqual(("PASS", guard.REASON_DEVELOPMENT), (verdict["result"], verdict["reason"]))

    def test_development_passes_even_when_policy_is_unavailable(self):
        verdict = evaluate("development", policy=guard.GuardError(guard.REASON_POLICY_UNAVAILABLE, "boom"))
        self.assertEqual("PASS", verdict["result"])

    def test_ordinary_branches_fail_without_base_policy(self):
        for head in ("feature/foo", "fix/bug", "chore/x", "governance/y", "validation/z", "diagnostic/w", "fix/security-unlisted"):
            with self.subTest(head=head):
                verdict = evaluate(head)
                self.assertEqual(("FAIL", guard.REASON_NOT_DEVELOPMENT), (verdict["result"], verdict["reason"]))

    def test_authorized_security_branch_passes(self):
        verdict = evaluate("fix/security-x", minimal_policy(active=[remediation("fix/security-x", "SEC-X")]))
        self.assertEqual(("PASS", guard.REASON_AUTHORIZED_REMEDIATION), (verdict["result"], verdict["reason"]))
        self.assertEqual("SEC-X", verdict["details"]["remediation_id"])

    def test_branch_match_is_exact(self):
        policy = minimal_policy(active=[remediation("fix/security-x")])
        for head in ("fix/security-x2", "fix/security-X", "fix/security-x/", "refs/heads/fix/security-x"):
            with self.subTest(head=head):
                self.assertEqual("FAIL", evaluate(head, policy)["result"])

    def test_historical_remediation_does_not_authorize(self):
        policy = minimal_policy(historical=[remediation("fix/security-old", "SEC-OLD", status="MERGED")])
        self.assertEqual(guard.REASON_NOT_DEVELOPMENT, evaluate("fix/security-old", policy)["reason"])

    def test_inactive_status_does_not_authorize(self):
        for status in ("MERGED", "REVOKED", "PENDING", ""):
            with self.subTest(status=status):
                policy = minimal_policy(active=[remediation("fix/security-x", status=status)])
                self.assertEqual("FAIL", evaluate("fix/security-x", policy)["result"])
        policy = minimal_policy(active=[remediation("fix/security-x", status="ACTIVE")])
        self.assertEqual("PASS", evaluate("fix/security-x", policy)["result"])

    def test_expired_authorization_does_not_authorize(self):
        policy = minimal_policy(active=[remediation("fix/security-x", expires="2026-09-20")])
        self.assertEqual("FAIL", evaluate("fix/security-x", policy, today="2026-09-21")["result"])
        self.assertEqual("PASS", evaluate("fix/security-x", policy, today="2026-09-20")["result"])
        malformed = minimal_policy(active=[remediation("fix/security-x", expires="soon")])
        self.assertEqual("FAIL", evaluate("fix/security-x", malformed)["result"])

    def test_non_security_mode_does_not_authorize(self):
        policy = minimal_policy(active=[remediation("fix/security-x", mode=guard.NORMAL_RELEASE_EVIDENCE)])
        self.assertEqual("FAIL", evaluate("fix/security-x", policy)["result"])

    def test_fork_fails_closed_before_any_route(self):
        for head in ("development", AUTHORIZED_BRANCH, "feature/foo"):
            with self.subTest(head=head):
                verdict = evaluate(head, COMMITTED_POLICY, head_repo="someone/Paquetenvia")
                self.assertEqual(("FAIL", guard.REASON_FORK_FORBIDDEN), (verdict["result"], verdict["reason"]))
        self.assertEqual(guard.REASON_FORK_FORBIDDEN, evaluate("development", head_repo="")["reason"])

    def test_non_main_base_is_a_no_op_pass(self):
        for base in ("development", "feature/other"):
            with self.subTest(base=base):
                verdict = evaluate("feature/foo", base_ref=base, head_repo="someone/fork")
                self.assertEqual(("PASS", guard.REASON_BASE_NOT_MAIN), (verdict["result"], verdict["reason"]))

    def test_missing_inputs_fail(self):
        for kwargs in ({"base_ref": ""}, {"repository": ""}):
            with self.subTest(kwargs=kwargs):
                self.assertEqual(guard.REASON_INPUT_INVALID, evaluate("development", **kwargs)["reason"])
        self.assertEqual(guard.REASON_INPUT_INVALID, evaluate("")["reason"])

    def test_policy_errors_fail_closed(self):
        verdict = evaluate("fix/security-x", policy=guard.GuardError(guard.REASON_POLICY_UNAVAILABLE, "missing"))
        self.assertEqual(("FAIL", guard.REASON_POLICY_UNAVAILABLE), (verdict["result"], verdict["reason"]))


class PolicyValidationTests(unittest.TestCase):
    def assert_invalid(self, policy):
        with self.assertRaises(guard.GuardError) as ctx:
            guard.validate_policy(policy)
        self.assertEqual(guard.REASON_POLICY_UNAVAILABLE, ctx.exception.reason)

    def test_committed_policy_is_valid(self):
        guard.validate_policy(copy.deepcopy(COMMITTED_POLICY))

    def test_malformed_policies(self):
        self.assert_invalid([])
        self.assert_invalid({**minimal_policy(), "format_version": "v1"})
        self.assert_invalid({**minimal_policy(), "default_mode": guard.SECURITY_REMEDIATION})
        self.assert_invalid({**minimal_policy(), "active_remediations": {}})
        self.assert_invalid(minimal_policy(active=["x"]))
        self.assert_invalid(minimal_policy(active=[{"id": "X", "mode": guard.SECURITY_REMEDIATION}]))
        self.assert_invalid(minimal_policy(active=[{"mode": guard.SECURITY_REMEDIATION, "authorized_source_branch": "b"}]))


# --------------------------------------------------------------------------- policy from base (git)


class BasePolicyTests(unittest.TestCase):
    def test_authorization_on_base_passes(self):
        repo = make_pr_repo(minimal_policy(active=[remediation("fix/security-x")]), "fix/security-x")
        code, verdict = run_cli(repo, "fix/security-x")
        self.assertEqual((0, guard.REASON_AUTHORIZED_REMEDIATION), (code, verdict["reason"]))

    def test_pr_cannot_authorize_itself(self):
        """The PR rewrites the policy to authorize its own branch; the base does not."""
        repo = make_pr_repo(minimal_policy(), "feature/foo", pr_policy=minimal_policy(active=[remediation("feature/foo")]))
        # Sanity: the tested tree really contains the self-authorization.
        tested_policy = json.loads(git(Path(repo["root"]), "show", f"{repo['tested']}:{POLICY_PATH}"))
        self.assertEqual("feature/foo", tested_policy["active_remediations"][0]["authorized_source_branch"])
        code, verdict = run_cli(repo, "feature/foo")
        self.assertEqual((1, guard.REASON_NOT_DEVELOPMENT), (code, verdict["reason"]))

    def test_pr_cannot_reactivate_or_extend_an_authorization(self):
        base = minimal_policy(active=[remediation("fix/security-x", status="MERGED")])
        pr = minimal_policy(active=[remediation("fix/security-x")])
        repo = make_pr_repo(base, "fix/security-x", pr_policy=pr)
        code, verdict = run_cli(repo, "fix/security-x")
        self.assertEqual((1, guard.REASON_NOT_DEVELOPMENT), (code, verdict["reason"]))

    def test_development_passes_without_reading_policy(self):
        repo = make_pr_repo(None, "development")
        code, verdict = run_cli(repo, "development")
        self.assertEqual((0, guard.REASON_DEVELOPMENT), (code, verdict["reason"]))

    def test_missing_base_policy_fails_closed(self):
        repo = make_pr_repo(None, "fix/security-x")
        code, verdict = run_cli(repo, "fix/security-x")
        self.assertEqual((1, guard.REASON_POLICY_UNAVAILABLE), (code, verdict["reason"]))

    def test_malformed_base_policy_fails_closed(self):
        repo = make_pr_repo({"format_version": "other"}, "fix/security-x")
        code, verdict = run_cli(repo, "fix/security-x")
        self.assertEqual((1, guard.REASON_POLICY_UNAVAILABLE), (code, verdict["reason"]))

    def test_non_merge_tested_sha_fails_closed(self):
        repo = make_pr_repo(minimal_policy(active=[remediation("fix/security-x")]), "fix/security-x")
        repo = {**repo, "tested": repo["source"]}
        code, verdict = run_cli(repo, "fix/security-x")
        self.assertEqual((1, guard.REASON_POLICY_UNAVAILABLE), (code, verdict["reason"]))

    def test_malformed_tested_sha_fails_closed(self):
        repo = make_pr_repo(minimal_policy(active=[remediation("fix/security-x")]), "fix/security-x")
        repo = {**repo, "tested": "ABC"}
        code, verdict = run_cli(repo, "fix/security-x")
        self.assertEqual((1, guard.REASON_INPUT_INVALID), (code, verdict["reason"]))

    def test_fork_cli(self):
        repo = make_pr_repo(minimal_policy(), "development")
        code, verdict = run_cli(repo, "development", head_repo="someone/Paquetenvia")
        self.assertEqual((1, guard.REASON_FORK_FORBIDDEN), (code, verdict["reason"]))


# --------------------------------------------------------------------------- REL-000 synchronisation


class Rel000SynchronisationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(REPOSITORY_ROOT / "tools" / "rel-000"))
        import rel000  # noqa: WPS433

        cls.rel000 = rel000

    def test_policy_constants_match_rel000(self):
        self.assertEqual(self.rel000.REMEDIATION_POLICY_FORMAT, guard.REMEDIATION_POLICY_FORMAT)
        self.assertEqual(self.rel000.SECURITY_REMEDIATION, guard.SECURITY_REMEDIATION)
        self.assertEqual(self.rel000.NORMAL_RELEASE_EVIDENCE, guard.NORMAL_RELEASE_EVIDENCE)

    def test_guard_agrees_with_rel000_mode_resolution_for_committed_policy(self):
        """Every branch the guard authorizes is one REL-000 resolves to SECURITY_REMEDIATION and vice versa."""
        authorized = guard.authorized_branches(guard.validate_policy(copy.deepcopy(COMMITTED_POLICY)), today="2026-09-21")
        self.assertTrue(authorized)
        for entry in COMMITTED_POLICY["active_remediations"]:
            branch = entry["authorized_source_branch"]
            mode = self.rel000.resolve_rel000_mode(COMMITTED_POLICY, branch, entry["id"])
            self.assertEqual(self.rel000.SECURITY_REMEDIATION, mode, branch)
            self.assertEqual(entry["id"], authorized.get(branch), branch)
        for entry in COMMITTED_POLICY["historical_remediations"]:
            self.assertNotIn(entry["authorized_source_branch"], authorized)
        self.assertEqual(self.rel000.NORMAL_RELEASE_EVIDENCE, self.rel000.resolve_rel000_mode(COMMITTED_POLICY, "development"))


if __name__ == "__main__":
    unittest.main()
