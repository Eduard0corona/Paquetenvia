"""Unit tests for the change classifier and its impact configuration."""

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

import classify_changes as classifier  # noqa: E402

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = "Eduard0corona/Paquetenvia"
CERTIFIED = "c" * 40
OTHER_SHA = "d" * 40
CONFIG = classifier.load_config(classifier.DEFAULT_CONFIG_PATH)
ALL_JOBS = list(CONFIG["jobs"])
BACKEND = CONFIG["job_sets"]["BACKEND"]
WEB_ALL = CONFIG["job_sets"]["WEB_ALL"]


def classify(
    paths,
    head_ref="feature/x",
    labels=None,
    head_repo=REPOSITORY,
    repository=REPOSITORY,
    source_head_sha=None,
    certified_main_sha=None,
):
    return classifier.classify_paths(
        CONFIG,
        list(paths),
        head_ref=head_ref,
        head_repo=head_repo,
        repository=repository,
        labels=labels,
        source_head_sha=source_head_sha,
        certified_main_sha=certified_main_sha,
    )


def backsync(paths, labels=None, source_head_sha=CERTIFIED, certified_main_sha=CERTIFIED, **kwargs):
    return classify(
        paths,
        head_ref="main",
        labels=labels,
        source_head_sha=source_head_sha,
        certified_main_sha=certified_main_sha,
        **kwargs,
    )


def jobs(plan):
    return set(plan["required_jobs"])


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
    completed = subprocess.run(
        ["git", "-C", str(root), *args], check=True, capture_output=True, text=True, env=env
    )
    return completed.stdout.strip()


def write(root: Path, relative: str, text: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def make_merge_repo(base_files: dict[str, str], source_files: dict[str, str]) -> dict[str, str]:
    """Create base → source → merge (tested) topology and return the SHAs."""
    root = Path(tempfile.mkdtemp(prefix="pv-classify-"))
    git(root, "init", "-q", "-b", "main")
    git(root, "config", "commit.gpgsign", "false")
    for relative, text in base_files.items():
        write(root, relative, text)
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "base")
    base = git(root, "rev-parse", "HEAD")
    git(root, "checkout", "-q", "-b", "feature/x")
    for relative, text in source_files.items():
        write(root, relative, text)
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "source", "--allow-empty")
    source = git(root, "rev-parse", "HEAD")
    git(root, "checkout", "-q", "main")
    git(root, "merge", "-q", "--no-ff", "-m", "tested merge", "feature/x")
    tested = git(root, "rev-parse", "HEAD")
    return {"root": str(root), "base": base, "source": source, "tested": tested}


# --------------------------------------------------------------------------- impact model


class ImpactModelTests(unittest.TestCase):
    def test_docs_only_requires_universal_controls_only(self):
        plan = classify(["docs/adr/0001.md", "README.md", ".gitignore", ".local/.gitignore", "docs/development/guide.md"])
        self.assertEqual("SELECTIVE", plan["classification"])
        self.assertEqual({"secret-scan"}, jobs(plan))
        self.assertEqual(["DOCS"], plan["domains"])
        self.assertEqual([], plan["unmatched_paths"])

    def test_normative_docs_are_rel000_inputs(self):
        plan = classify(["docs/normative/v0.6/contracts/AI-01.md"])
        self.assertEqual("FULL", plan["classification"])
        self.assertEqual(["DOCS", "NORMATIVE", "REL000_INPUT"], plan["domains"])
        self.assertIn("FULL:REL000_INPUT", plan["reasons"])
        self.assertIn("rel000", jobs(plan))

    def test_driver_ui(self):
        plan = classify(["apps/web/src/driver/stops.tsx", "apps/web/src/app/driver/page.tsx"])
        self.assertEqual({"secret-scan", "web", "driver-stops-pwa", "azr-static"}, jobs(plan))
        self.assertEqual(["WEB_DRIVER"], plan["domains"])

    def test_tracking_ui(self):
        plan = classify(["apps/web/src/tracking/view.tsx", "apps/web/src/app/track/page.tsx"])
        self.assertEqual({"secret-scan", "web", "public-tracking", "azr-static"}, jobs(plan))

    def test_operations_ui(self):
        plan = classify(["apps/web/src/operations/board.tsx", "apps/web/src/app/ops/page.tsx"])
        self.assertEqual({"secret-scan", "web", "operations-dashboard", "azr-static"}, jobs(plan))

    def test_realtime_shared_fans_out_to_realtime_dependents_only(self):
        plan = classify(["apps/web/src/realtime/hub.ts"])
        self.assertEqual({"secret-scan", "azr-static", *WEB_ALL}, jobs(plan))
        self.assertNotIn("outbox-signalr-delivery", jobs(plan))
        self.assertNotIn("dotnet", jobs(plan))

    def test_web_dev_portal(self):
        plan = classify(["apps/web/src/dev/dev-portal-policy.ts", "apps/web/src/app/dev/page.tsx"])
        self.assertEqual({"secret-scan", "web", "dotnet", "azr-static"}, jobs(plan))

    def test_web_shared(self):
        plan = classify(["apps/web/src/app/layout.tsx", "apps/web/public/sw.js", "apps/web/src/components/x.tsx"])
        self.assertEqual({"secret-scan", "azr-static", *WEB_ALL}, jobs(plan))
        self.assertNotIn("dotnet", jobs(plan))

    def test_next_config_adds_dotnet(self):
        plan = classify(["apps/web/next.config.ts"])
        self.assertEqual({"secret-scan", "azr-static", "dotnet", *WEB_ALL}, jobs(plan))

    def test_web_shared_does_not_trigger_backend_jobs(self):
        plan = classify(["apps/web/src/lib/health.ts"])
        for job in ("outbox-signalr-delivery", "delivery-simulation", "infrastructure", "backup-restore", "runtime-contracts"):
            self.assertNotIn(job, jobs(plan))

    def test_azure_bicep(self):
        plan = classify(["deploy/azure/core.bicep", "deploy/azure/Dockerfile.web", "deploy/azure/deploy-core.ps1"])
        self.assertEqual({"secret-scan", "azr-static"}, jobs(plan))
        self.assertEqual("SELECTIVE", plan["classification"])

    def test_deploy_workflow(self):
        plan = classify([".github/workflows/deploy-azure-dev.yml"])
        self.assertEqual({"secret-scan", "azr-static"}, jobs(plan))
        self.assertEqual(["DEPLOY_WORKFLOW"], plan["domains"])
        self.assertEqual("SELECTIVE", plan["classification"])

    def test_claude_automation_workflows_need_only_universal_controls(self):
        for path in (".github/workflows/claude.yml", ".github/workflows/claude-code-review.yml"):
            with self.subTest(path=path):
                plan = classify([path])
                self.assertEqual("SELECTIVE", plan["classification"])
                self.assertEqual(["GITHUB_AUTOMATION"], plan["domains"])
                self.assertEqual({"secret-scan"}, jobs(plan))
                self.assertEqual([], plan["unmatched_paths"])

    def test_both_claude_automation_workflows_together_stay_selective(self):
        plan = classify([".github/workflows/claude.yml", ".github/workflows/claude-code-review.yml"])
        self.assertEqual("SELECTIVE", plan["classification"])
        self.assertEqual(["GITHUB_AUTOMATION"], plan["domains"])
        self.assertEqual({"secret-scan"}, jobs(plan))

    def test_unknown_workflow_file_remains_full(self):
        for path in (
            ".github/workflows/unknown.yml",
            ".github/workflows/new-unclassified-workflow.yml",
            ".github/workflows/claude-extra.yml",
            ".github/dependabot.yml",
        ):
            with self.subTest(path=path):
                plan = classify([path])
                self.assertEqual("FULL", plan["classification"])
                self.assertEqual([path], plan["unmatched_paths"])
                self.assertIn("FULL:UNMATCHED_PATHS", plan["reasons"])

    def test_claude_automation_does_not_alter_control_workflow_semantics(self):
        for path in (".github/workflows/ci.yml", ".github/workflows/pr-validation.yml"):
            with self.subTest(path=path):
                plan = classify([path])
                self.assertEqual("FULL", plan["classification"])
                self.assertEqual(["CI_SELF"], plan["domains"])
                self.assertEqual(set(ALL_JOBS), jobs(plan))
        plan = classify([".github/workflows/deploy-azure-dev.yml", ".github/workflows/claude.yml"])
        self.assertEqual("SELECTIVE", plan["classification"])
        self.assertEqual(["DEPLOY_WORKFLOW", "GITHUB_AUTOMATION"], plan["domains"])
        self.assertEqual({"secret-scan", "azr-static"}, jobs(plan))
        plan = classify([".github/workflows/ci.yml", ".github/workflows/claude.yml"])
        self.assertEqual("FULL", plan["classification"])

    def test_no_domain_matches_every_workflow_file(self):
        for domain in CONFIG["domains"]:
            self.assertFalse(
                any(pattern.match(".github/workflows/unknown.yml") for pattern in domain["compiled"]),
                f"{domain['name']} matches arbitrary workflow files",
            )

    def test_backend_shared(self):
        plan = classify(["src/BuildingBlocks/Outbox/Publisher.cs"])
        self.assertEqual({"secret-scan", *BACKEND}, jobs(plan))
        self.assertNotIn("web", jobs(plan))
        self.assertNotIn("rel000", jobs(plan))
        self.assertEqual("SELECTIVE", plan["classification"])

    def test_migration_is_backend(self):
        plan = classify(["src/Modules/Orders/Orders.Infrastructure/Persistence/Migrations/20260921_X.cs"])
        self.assertEqual({"secret-scan", *BACKEND}, jobs(plan))

    def test_backend_infrastructure_paths(self):
        plan = classify(["deploy/docker-compose.yml", "tools/dev-platform.ps1", "database/migrations/v0.6-baseline.json"])
        self.assertEqual({"secret-scan", *BACKEND}, jobs(plan))

    def test_dotnet_only_test_projects(self):
        plan = classify(
            [
                "tests/Paqueteria.UnitTests/OrdersTests.cs",
                "tests/Paqueteria.ArchitectureTests/X.cs",
                "tests/Paqueteria.ArchitectureFixtures/Y.cs",
            ]
        )
        self.assertEqual({"secret-scan", "dotnet"}, jobs(plan))

    def test_contract_tests(self):
        plan = classify(["tests/Paqueteria.ContractTests/OrdersContractTests.cs"])
        self.assertEqual({"secret-scan", "dotnet", "runtime-contracts"}, jobs(plan))

    def test_realtime_test_host(self):
        plan = classify(["tests/Paqueteria.RealtimeTestHost/Program.cs"])
        self.assertEqual({"secret-scan", "dotnet", "realtime-e2e"}, jobs(plan))

    def test_integration_tests_are_backend(self):
        plan = classify(["tests/Paqueteria.IntegrationTests/Driver/StopsTests.cs"])
        self.assertEqual({"secret-scan", *BACKEND}, jobs(plan))

    def test_mixed_independent_domains_union(self):
        plan = classify(["apps/web/src/driver/a.tsx", "apps/web/src/tracking/b.tsx", "docs/adr/c.md"])
        self.assertEqual({"secret-scan", "web", "driver-stops-pwa", "public-tracking", "azr-static"}, jobs(plan))
        self.assertEqual(["DOCS", "WEB_DRIVER", "WEB_TRACKING"], plan["domains"])
        self.assertEqual("SELECTIVE", plan["classification"])

    def test_web_plus_backend_union(self):
        plan = classify(["apps/web/src/driver/a.tsx", "src/Paqueteria.Api/Program.cs"])
        self.assertEqual({"secret-scan", "web", "azr-static", *BACKEND}, jobs(plan))


class FullTriggerTests(unittest.TestCase):
    def assert_full(self, plan, reason):
        self.assertEqual("FULL", plan["classification"])
        self.assertEqual(set(ALL_JOBS), jobs(plan))
        self.assertIn("rel000", jobs(plan))
        self.assertIn(reason, plan["reasons"])

    def test_dependency_files_force_full(self):
        for path in (
            "apps/web/package.json",
            "apps/web/pnpm-lock.yaml",
            "apps/web/pnpm-workspace.yaml",
            "Directory.Packages.props",
            "tests/Paqueteria.UnitTests/packages.lock.json",
            "global.json",
            ".nvmrc",
            ".config/dotnet-tools.json",
        ):
            with self.subTest(path=path):
                self.assert_full(classify([path]), "FULL:DEPS")

    def test_ci_self_forces_full(self):
        for path in (
            ".github/workflows/ci.yml",
            ".github/workflows/pr-validation.yml",
            "tools/ci/classify_changes.py",
            "tools/ci/change-domains.json",
            "tools/rel-000/rel000.py",
            "tools/azr-001/azr001_static_guards.py",
            "tools/security/sharp-runtime-smoke.mjs",
            "tests/fixtures/rel-000/item-evidence.json",
        ):
            with self.subTest(path=path):
                self.assert_full(classify([path]), "FULL:CI_SELF")

    def test_rel000_direct_inputs_force_full(self):
        for path in (
            "docs/releases/mvp-0-owner-decision.json",
            "docs/releases/evidence/rel-000-owner-001/approved-evidence-manifest.json",
            "docs/releases/evidence/rel-000-owner-001/rel000-p0-evidence.json",
            "docs/releases/mvp-0-internal-release-report.md",
            "docs/normative/v0.6/specs/AI-08_BACKLOG.yaml",
            "docs/normative/v0.6/specs/AI-10_DECISIONS_AND_GATES.yaml",
            "docs/normative/v0.6/CHECKSUMS_SHA256.txt",
            "tools/test-rel-000-internal-release.ps1",
            "tools/backup-restore.common.ps1",
        ):
            with self.subTest(path=path):
                plan = classify([path])
                self.assert_full(plan, "FULL:REL000_INPUT")
                self.assertIn("REL000_INPUT", plan["domains"])

    def test_ordinary_docs_and_tools_are_not_rel000_inputs(self):
        for path in ("docs/adr/example.md", "docs/development/guide.md", "README.md"):
            with self.subTest(path=path):
                plan = classify([path])
                self.assertEqual("SELECTIVE", plan["classification"])
                self.assertEqual({"secret-scan"}, jobs(plan))
        plan = classify(["tools/dev-platform.ps1"])
        self.assertEqual("SELECTIVE", plan["classification"])
        self.assertNotIn("rel000", jobs(plan))

    def test_gitleaks_config_is_a_security_control(self):
        plan = classify([".gitleaks.toml"])
        self.assert_full(plan, "FULL:SECURITY_CONTROL")
        self.assertEqual(["SECURITY_CONTROL"], plan["domains"])
        docs = next(domain for domain in CONFIG["domains"] if domain["name"] == "DOCS")
        self.assertFalse(any(pattern.match(".gitleaks.toml") for pattern in docs["compiled"]))

    def test_unknown_path_forces_full_and_is_reported(self):
        plan = classify(["apps/web/src/newarea/x.ts", "docs/adr/y.md"])
        self.assert_full(plan, "FULL:UNMATCHED_PATHS")
        self.assertEqual(["apps/web/src/newarea/x.ts"], plan["unmatched_paths"])

    def test_new_top_level_directory_is_unknown(self):
        plan = classify(["services/new/thing.cs"])
        self.assert_full(plan, "FULL:UNMATCHED_PATHS")

    def test_empty_diff_forces_full(self):
        self.assert_full(classify([]), "FULL:EMPTY_DIFF")

    def test_full_ci_label_forces_full(self):
        plan = classify(["docs/adr/x.md"], labels=["full-ci"])
        self.assert_full(plan, "FULL:LABEL_full-ci")

    def test_other_labels_do_not_change_plan(self):
        self.assertEqual(classify(["docs/adr/x.md"]), classify(["docs/adr/x.md"], labels=["skip-ci", "docs"]))

    def test_full_cannot_be_downgraded_by_selective_paths(self):
        plan = classify(["apps/web/package.json", "docs/adr/x.md", "apps/web/src/driver/a.tsx"])
        self.assert_full(plan, "FULL:DEPS")
        self.assertEqual(set(ALL_JOBS), jobs(plan))

    def test_union_never_reduces_a_job(self):
        single = jobs(classify(["src/Paqueteria.Api/Program.cs"]))
        combined = jobs(classify(["src/Paqueteria.Api/Program.cs", "docs/adr/x.md", "tests/Paqueteria.UnitTests/a.cs"]))
        self.assertTrue(single <= combined)


class MainBacksyncTests(unittest.TestCase):
    def assert_backsync(self, plan):
        self.assertEqual("MAIN_BACKSYNC", plan["classification"])
        self.assertEqual(set(ALL_JOBS) - {"rel000"}, jobs(plan))
        self.assertIn("MAIN_BACKSYNC:CERTIFIED_MAIN_HEAD", plan["reasons"])
        self.assertFalse([r for r in plan["reasons"] if r.startswith("FULL:")])

    def assert_uncertified_full(self, plan):
        self.assertEqual("FULL", plan["classification"])
        self.assertEqual(set(ALL_JOBS), jobs(plan))
        self.assertIn("rel000", jobs(plan))
        self.assertIn("FULL:MAIN_BACKSYNC_UNCERTIFIED", plan["reasons"])
        self.assertNotIn("MAIN_BACKSYNC:CERTIFIED_MAIN_HEAD", plan["reasons"])

    def test_certified_main_head_runs_everything_except_rel000(self):
        self.assert_backsync(backsync(["apps/web/package.json", "docs/adr/x.md"]))

    def test_certified_main_head_docs_only_still_runs_every_development_validation(self):
        self.assert_backsync(backsync(["docs/adr/x.md"]))

    def test_path_derived_full_triggers_do_not_summon_rel000_on_certified_backsync(self):
        for path in ("services/new.cs", "tools/ci/classify_changes.py", "apps/web/pnpm-lock.yaml", ".gitleaks.toml"):
            with self.subTest(path=path):
                self.assert_backsync(backsync([path]))
        self.assertEqual(["services/new.cs"], backsync(["services/new.cs"])["unmatched_paths"])

    def test_certification_missing_fails_closed_to_full(self):
        self.assert_uncertified_full(backsync(["docs/adr/x.md"], certified_main_sha=None))
        self.assert_uncertified_full(backsync(["docs/adr/x.md"], certified_main_sha=""))
        self.assert_uncertified_full(backsync(["docs/adr/x.md"], certified_main_sha="   "))

    def test_certification_mismatch_fails_closed_to_full(self):
        self.assert_uncertified_full(backsync(["docs/adr/x.md"], certified_main_sha=OTHER_SHA))
        self.assert_uncertified_full(
            backsync(["apps/web/pnpm-lock.yaml"], source_head_sha=OTHER_SHA, certified_main_sha=CERTIFIED)
        )

    def test_source_head_missing_fails_closed_to_full(self):
        self.assert_uncertified_full(backsync(["docs/adr/x.md"], source_head_sha=None))

    def test_certification_malformed_fails_closed_nonzero(self):
        for bad in ("C" * 40, "abc", "c" * 39, CERTIFIED + " 1"):
            with self.subTest(bad=bad), self.assertRaises(classifier.ClassifyError) as ctx:
                backsync(["docs/adr/x.md"], certified_main_sha=bad)
            self.assertEqual("CLASSIFY_SHA_MALFORMED", ctx.exception.code)
        with self.assertRaises(classifier.ClassifyError) as ctx:
            backsync(["docs/adr/x.md"], source_head_sha="nope")
        self.assertEqual("CLASSIFY_SHA_MALFORMED", ctx.exception.code)

    def test_branch_identity_alone_never_certifies(self):
        """head_ref == main with certification for a different SHA is not a back-sync."""
        self.assert_uncertified_full(backsync(["docs/adr/x.md"], source_head_sha=OTHER_SHA, certified_main_sha=CERTIFIED))

    def test_uncertified_main_never_bypasses_rel000(self):
        for path in ("docs/adr/x.md", "apps/web/pnpm-lock.yaml", "services/new.cs"):
            for certified in (None, OTHER_SHA):
                with self.subTest(path=path, certified=certified):
                    self.assertIn("rel000", jobs(backsync([path], certified_main_sha=certified)))

    def test_certification_evidence_is_irrelevant_for_non_main_heads(self):
        plan = classify(["docs/adr/x.md"], head_ref="feature/x", source_head_sha=CERTIFIED, certified_main_sha=CERTIFIED)
        self.assertEqual("SELECTIVE", plan["classification"])
        self.assertEqual({"secret-scan"}, jobs(plan))

    def test_full_ci_label_expands_certified_backsync_to_full(self):
        plan = backsync(["docs/adr/x.md"], labels=["full-ci"])
        self.assertEqual("FULL", plan["classification"])
        self.assertEqual(set(ALL_JOBS), jobs(plan))
        self.assertIn("FULL:LABEL_full-ci", plan["reasons"])

    def test_fork_main_head_is_rejected_even_when_certified(self):
        with self.assertRaises(classifier.ClassifyError) as ctx:
            backsync(["docs/adr/x.md"], head_repo="someone/Paquetenvia")
        self.assertEqual("CLASSIFY_FORK_HEAD_FORBIDDEN", ctx.exception.code)


class InputSafetyTests(unittest.TestCase):
    def test_fork_head_fails_closed(self):
        with self.assertRaises(classifier.ClassifyError) as ctx:
            classify(["docs/adr/x.md"], head_repo="fork/Paquetenvia")
        self.assertEqual("CLASSIFY_FORK_HEAD_FORBIDDEN", ctx.exception.code)

    def test_missing_head_ref_fails(self):
        with self.assertRaises(classifier.ClassifyError) as ctx:
            classify(["docs/adr/x.md"], head_ref="")
        self.assertEqual("CLASSIFY_HEAD_REF_MISSING", ctx.exception.code)

    def test_invalid_path_fails(self):
        for path in ("/abs/path", "a\\b", "../x"):
            with self.subTest(path=path), self.assertRaises(classifier.ClassifyError) as ctx:
                classify([path])
            self.assertEqual("CLASSIFY_PATH_INVALID", ctx.exception.code)

    def test_plan_is_deterministic(self):
        paths = ["src/b.cs", "docs/adr/a.md", "apps/web/src/driver/z.tsx", "src/a.cs"]
        first = classifier.serialize_plan(classify(paths))
        second = classifier.serialize_plan(classify(list(reversed(paths))))
        self.assertEqual(first, second)
        self.assertEqual(first, json.dumps(json.loads(first), sort_keys=True, separators=(",", ":")))

    def test_required_jobs_use_canonical_order(self):
        plan = classify(["apps/web/src/realtime/x.ts", "src/a.cs"])
        expected = [job for job in ALL_JOBS if job in jobs(plan)]
        self.assertEqual(expected, plan["required_jobs"])

    def test_plan_validates_against_config(self):
        classifier.validate_plan(classify(["docs/adr/x.md"]), CONFIG)
        classifier.validate_plan(classify(["global.json"]), CONFIG)
        classifier.validate_plan(backsync(["docs/adr/x.md"]), CONFIG)
        classifier.validate_plan(backsync(["docs/adr/x.md"], certified_main_sha=None), CONFIG)


class ConfigValidationTests(unittest.TestCase):
    def raw(self):
        return json.loads(classifier.DEFAULT_CONFIG_PATH.read_text(encoding="utf-8"))

    def assert_rejected(self, raw, code):
        with self.assertRaises(classifier.ClassifyError) as ctx:
            classifier.validate_config(raw)
        self.assertEqual(code, ctx.exception.code)

    def test_committed_config_is_valid(self):
        classifier.validate_config(self.raw())

    def test_format_required(self):
        raw = self.raw()
        raw["format"] = "other"
        self.assert_rejected(raw, "CONFIG_FORMAT_INVALID")

    def test_missing_key(self):
        raw = self.raw()
        del raw["domains"]
        self.assert_rejected(raw, "CONFIG_KEY_MISSING")

    def test_unknown_job_in_domain(self):
        raw = self.raw()
        raw["domains"][0]["jobs"] = ["nonexistent-job"]
        self.assert_rejected(raw, "CONFIG_UNKNOWN_JOB")

    def test_empty_pattern(self):
        raw = self.raw()
        raw["domains"][0]["patterns"] = [""]
        self.assert_rejected(raw, "CONFIG_PATTERN_INVALID")

    def test_selective_domain_cannot_require_rel000(self):
        raw = self.raw()
        raw["domains"][1]["jobs"] = ["rel000"]
        self.assert_rejected(raw, "CONFIG_DOMAINS_INVALID")

    def test_full_domain_cannot_list_jobs(self):
        raw = self.raw()
        full = next(d for d in raw["domains"] if d.get("full"))
        full["jobs"] = ["web"]
        self.assert_rejected(raw, "CONFIG_DOMAINS_INVALID")

    def test_duplicate_domain_name(self):
        raw = self.raw()
        raw["domains"].append(copy.deepcopy(raw["domains"][0]))
        self.assert_rejected(raw, "CONFIG_DOMAINS_INVALID")

    def test_full_job_set_is_complete_future_validation(self):
        expected = {
            "secret-scan",
            "normative-contracts",
            "azr-static",
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
            "rel000",
        }
        self.assertEqual(expected, set(ALL_JOBS))
        self.assertEqual(["rel000"], CONFIG["full_only_jobs"])
        self.assertEqual(["secret-scan"], CONFIG["universal_jobs"])

    def test_universal_and_full_only_disjoint(self):
        raw = self.raw()
        raw["universal_jobs"] = ["rel000"]
        self.assert_rejected(raw, "CONFIG_INVALID")


class PatternTests(unittest.TestCase):
    def test_double_star_prefix_matches_root_and_nested(self):
        pattern = classifier.compile_pattern("**/*.md")
        self.assertTrue(pattern.match("README.md"))
        self.assertTrue(pattern.match("docs/a/b.md"))
        self.assertFalse(pattern.match("docs/a/b.mdx"))

    def test_single_star_stays_in_segment(self):
        pattern = classifier.compile_pattern("apps/web/src/app/*")
        self.assertTrue(pattern.match("apps/web/src/app/layout.tsx"))
        self.assertFalse(pattern.match("apps/web/src/app/driver/page.tsx"))

    def test_trailing_double_star(self):
        pattern = classifier.compile_pattern("src/**")
        self.assertTrue(pattern.match("src/a/b/c.cs"))
        self.assertFalse(pattern.match("srcx/a.cs"))


# --------------------------------------------------------------------------- git provenance


class GitProvenanceTests(unittest.TestCase):
    def test_changed_paths_from_tested_merge_ref(self):
        repo = make_merge_repo({"README.md": "a\n", "src/a.cs": "1\n"}, {"src/a.cs": "2\n", "docs/adr/x.md": "n\n"})
        paths = classifier.resolve_changed_paths(Path(repo["root"]), repo["tested"], repo["source"])
        self.assertEqual(["docs/adr/x.md", "src/a.cs"], paths)

    def test_cli_end_to_end_emits_plan(self):
        repo = make_merge_repo({"README.md": "a\n"}, {"apps/web/src/driver/a.tsx": "x\n"})
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp) / "plan.json"
            github_output = Path(tmp) / "out.txt"
            with contextlib.redirect_stdout(io.StringIO()):
                code = classifier.main(
                    [
                        "--repo-root", repo["root"],
                        "--tested-git-sha", repo["tested"],
                        "--source-head-sha", repo["source"],
                        "--head-ref", "feature/x",
                        "--head-repo", REPOSITORY,
                        "--repository", REPOSITORY,
                        "--output", str(output),
                        "--github-output", str(github_output),
                    ]
                )
            self.assertEqual(0, code)
            plan = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual({"secret-scan", "web", "driver-stops-pwa", "azr-static"}, set(plan["required_jobs"]))
            self.assertTrue(github_output.read_text(encoding="utf-8").startswith("plan={"))

    def test_cli_backsync_requires_certified_main_sha(self):
        repo = make_merge_repo({"README.md": "a\n"}, {"docs/adr/x.md": "x\n"})
        base_args = [
            "--repo-root", repo["root"],
            "--tested-git-sha", repo["tested"],
            "--source-head-sha", repo["source"],
            "--head-ref", "main",
            "--head-repo", REPOSITORY,
            "--repository", REPOSITORY,
        ]
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp) / "plan.json"

            def run(extra):
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    code = classifier.main(base_args + extra + ["--output", str(output)])
                return code, (json.loads(output.read_text(encoding="utf-8")) if code == 0 else None)

            code, plan = run([])
            self.assertEqual((0, "FULL"), (code, plan["classification"]))
            code, plan = run(["--certified-main-sha", "e" * 40])
            self.assertEqual((0, "FULL"), (code, plan["classification"]))
            self.assertIn("FULL:MAIN_BACKSYNC_UNCERTIFIED", plan["reasons"])
            code, plan = run(["--certified-main-sha", repo["source"]])
            self.assertEqual((0, "MAIN_BACKSYNC"), (code, plan["classification"]))
            self.assertNotIn("rel000", plan["required_jobs"])
            code, _ = run(["--certified-main-sha", "not-a-sha"])
            self.assertEqual(1, code)

    def test_source_second_parent_mismatch_fails(self):
        repo = make_merge_repo({"README.md": "a\n"}, {"README.md": "b\n"})
        with self.assertRaises(classifier.ClassifyError) as ctx:
            classifier.resolve_changed_paths(Path(repo["root"]), repo["tested"], repo["base"])
        self.assertEqual("CLASSIFY_SOURCE_HEAD_MISMATCH", ctx.exception.code)

    def test_non_merge_tested_commit_fails(self):
        repo = make_merge_repo({"README.md": "a\n"}, {"README.md": "b\n"})
        with self.assertRaises(classifier.ClassifyError) as ctx:
            classifier.resolve_changed_paths(Path(repo["root"]), repo["source"], repo["base"])
        self.assertEqual("CLASSIFY_MERGE_TOPOLOGY_INVALID", ctx.exception.code)

    def test_source_equal_tested_fails(self):
        repo = make_merge_repo({"README.md": "a\n"}, {"README.md": "b\n"})
        with self.assertRaises(classifier.ClassifyError) as ctx:
            classifier.resolve_changed_paths(Path(repo["root"]), repo["tested"], repo["tested"])
        self.assertEqual("CLASSIFY_SOURCE_IS_TESTED", ctx.exception.code)

    def test_missing_git_object_fails(self):
        repo = make_merge_repo({"README.md": "a\n"}, {"README.md": "b\n"})
        with self.assertRaises(classifier.ClassifyError) as ctx:
            classifier.resolve_changed_paths(Path(repo["root"]), "f" * 40, repo["source"])
        self.assertEqual("CLASSIFY_GIT_FAILED", ctx.exception.code)

    def test_malformed_sha_fails(self):
        with self.assertRaises(classifier.ClassifyError) as ctx:
            classifier.resolve_changed_paths(REPOSITORY_ROOT, "ABC", "d" * 40)
        self.assertEqual("CLASSIFY_SHA_MALFORMED", ctx.exception.code)

    def test_cli_failure_exits_nonzero_without_plan(self):
        repo = make_merge_repo({"README.md": "a\n"}, {"README.md": "b\n"})
        with contextlib.redirect_stderr(io.StringIO()) as captured:
            code = classifier.main(
                [
                    "--repo-root", repo["root"],
                    "--tested-git-sha", repo["tested"],
                    "--source-head-sha", repo["base"],
                    "--head-ref", "feature/x",
                    "--head-repo", REPOSITORY,
                    "--repository", REPOSITORY,
                ]
            )
        self.assertEqual(1, code)
        self.assertIn("CLASSIFY_SOURCE_HEAD_MISMATCH", captured.getvalue())


# --------------------------------------------------------------------------- repository coverage


class TrackedPathCoverageTests(unittest.TestCase):
    def test_every_tracked_path_matches_a_rule(self):
        output = subprocess.run(
            ["git", "-C", str(REPOSITORY_ROOT), "ls-files", "-z"], check=True, capture_output=True, text=True
        ).stdout
        tracked = sorted(path for path in output.split("\0") if path)
        self.assertGreater(len(tracked), 500)
        unmatched = [path for path in tracked if not classifier.match_domains(CONFIG, path)]
        self.assertEqual([], unmatched, f"{len(unmatched)} tracked paths are unclassified")

    def test_no_domain_uses_a_catch_all_pattern(self):
        for domain in CONFIG["domains"]:
            for pattern in domain["patterns"]:
                self.assertNotIn(pattern, ("**", "*", "**/*"), f"{domain['name']} uses a catch-all pattern")


# --------------------------------------------------------------------------- REL-000 synchronisation


class Rel000SynchronisationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(REPOSITORY_ROOT / "tools" / "rel-000"))
        import rel000  # noqa: WPS433

        cls.rel000 = rel000
        cls.deps = next(domain for domain in CONFIG["domains"] if domain["name"] == "DEPS")

    def test_every_rel000_dependency_file_name_forces_full_anywhere(self):
        for name in sorted(self.rel000.DEPENDENCY_FILE_NAMES):
            with self.subTest(name=name):
                self.assertIn(f"**/{name}", self.deps["patterns"])
                for path in (name, f"apps/web/{name}", f"tests/Paqueteria.UnitTests/{name}"):
                    self.assertEqual("FULL", classify([path])["classification"], path)

    def test_rel000_web_dependency_files_force_full(self):
        for path in self.rel000.DEPENDENCY_FILES:
            with self.subTest(path=path):
                plan = classify([path])
                self.assertEqual("FULL", plan["classification"])
                self.assertIn("rel000", plan["required_jobs"])
        self.assertEqual(set(self.rel000.DEPENDENCY_FILES), set(self.rel000.SECURITY_REMEDIATION_DEPENDENCY_FILES))

    def test_rel000_repository_inputs_are_full(self):
        """Paths rel000.py, the rel000 job and its runner script read from the tree stay FULL."""
        for path in (
            "docs/releases/mvp-0-owner-decision.json",
            "docs/releases/evidence/rel-000-owner-001/approved-evidence-manifest.json",
            "docs/normative/v0.6/specs/AI-08_BACKLOG.yaml",
            "tools/test-rel-000-internal-release.ps1",
            "tools/backup-restore.common.ps1",
            "tools/rel-000/security-remediation-policy.json",
            "tools/security/sharp-runtime-smoke.mjs",
            "tests/fixtures/rel-000/security-tracking.json",
        ):
            with self.subTest(path=path):
                plan = classify([path])
                self.assertEqual("FULL", plan["classification"])
                self.assertIn("rel000", plan["required_jobs"])

    def test_rel000_tooling_and_fixtures_force_full(self):
        for path in ("tools/rel-000/rel000.py", "tools/rel-000/security-remediation-policy.json", "tests/fixtures/rel-000/rollback-evidence.json"):
            self.assertEqual("FULL", classify([path])["classification"], path)


if __name__ == "__main__":
    unittest.main()
