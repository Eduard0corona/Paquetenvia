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
import pr_gate as gate  # noqa: E402

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
        plan = classify(["apps/web/src/dev/dev-portal-policy.ts", "apps/web/src/app/dev/page.dev.tsx"])
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
        for path in (
            ".github/workflows/ci.yml",
            ".github/workflows/pr-validation.yml",
            ".github/workflows/development-push.yml",
        ):
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


AI05_OPENAPI = "docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"
NORMATIVE_MANIFEST = "docs/normative/v0.6/MANIFEST.json"
NORMATIVE_CHECKSUMS = "docs/normative/v0.6/CHECKSUMS_SHA256.txt"
AI05_TRIPLET = (AI05_OPENAPI, NORMATIVE_MANIFEST, NORMATIVE_CHECKSUMS)
AI05_CONSUMERS = {"normative-contracts", "dotnet", "web"}
AI08_BACKLOG = "docs/normative/v0.6/specs/AI-08_BACKLOG.yaml"
AI10_GATES = "docs/normative/v0.6/specs/AI-10_DECISIONS_AND_GATES.yaml"


class Rel000InputNarrowingTests(unittest.TestCase):
    """Only the AI-05 triplet leaves REL000_INPUT; every other REL-000 input stays FULL."""

    def assert_selective_openapi(self, plan):
        self.assertEqual("SELECTIVE", plan["classification"])
        self.assertNotIn("REL000_INPUT", plan["domains"])
        self.assertNotIn("FULL:REL000_INPUT", plan["reasons"])
        self.assertFalse([reason for reason in plan["reasons"] if reason.startswith("FULL:")])
        self.assertIn("NORMATIVE_OPENAPI", plan["domains"])
        self.assertIn("DOMAIN:NORMATIVE_OPENAPI", plan["reasons"])
        self.assertTrue(AI05_CONSUMERS <= jobs(plan))
        self.assertNotIn("rel000", jobs(plan))
        classifier.validate_plan(plan, CONFIG)
        self.assertEqual(("PASS", []), gate_verdict(plan))

    def assert_full_rel000(self, plan):
        self.assertEqual("FULL", plan["classification"])
        self.assertIn("REL000_INPUT", plan["domains"])
        self.assertIn("FULL:REL000_INPUT", plan["reasons"])
        self.assertEqual(set(ALL_JOBS), jobs(plan))

    # ---- the AI-05 triplet is selective and requires its real consumers

    def test_each_ai05_path_no_longer_forces_full(self):
        for path in AI05_TRIPLET:
            with self.subTest(path=path):
                plan = classify([path])
                self.assert_selective_openapi(plan)
                self.assertEqual(["DOCS", "NORMATIVE", "NORMATIVE_OPENAPI"], plan["domains"])
                self.assertEqual({"secret-scan", *AI05_CONSUMERS}, jobs(plan))
                self.assertNotIn("azr-static", jobs(plan))

    def test_ai05_triplet_together_requires_normative_dotnet_and_web(self):
        plan = classify(AI05_TRIPLET)
        self.assert_selective_openapi(plan)
        self.assertEqual(["DOCS", "NORMATIVE", "NORMATIVE_OPENAPI"], plan["domains"])
        self.assertEqual(["secret-scan", "normative-contracts", "dotnet", "web"], plan["required_jobs"])

    def test_product_change_with_ai05_triplet_is_selective(self):
        """FIN-001/INC-001/CSV-001 shape: AI-05 triplet plus backend code and tests."""
        plan = classify(
            [
                *AI05_TRIPLET,
                "src/Modules/Incidents/Incidents.Api/IncidentEndpoints.cs",
                "tests/Paqueteria.IntegrationTests/Incidents/IncidentTests.cs",
                "tests/Paqueteria.ContractTests/IncidentContractTests.cs",
            ]
        )
        self.assert_selective_openapi(plan)
        self.assertEqual({"secret-scan", "normative-contracts", "web", *BACKEND}, jobs(plan))
        self.assertEqual({"azr-static", "rel000"}, set(ALL_JOBS) - jobs(plan))

    def test_openapi_domain_covers_exactly_the_excluded_triplet(self):
        domains = {domain["name"]: domain for domain in CONFIG["domains"]}
        self.assertEqual(list(AI05_TRIPLET), domains["NORMATIVE_OPENAPI"]["patterns"])
        self.assertEqual(["normative-contracts", "dotnet", "web"], domains["NORMATIVE_OPENAPI"]["jobs"])
        self.assertFalse(domains["NORMATIVE_OPENAPI"]["full"])
        self.assertEqual(list(AI05_TRIPLET), domains["REL000_INPUT"]["exclude"])
        others = [name for name, domain in domains.items() if domain["exclude"] and name != "REL000_INPUT"]
        self.assertEqual([], others)

    # ---- the exclusion is domain-local, never a global suppression

    def test_excluded_path_is_still_matched_by_every_other_applicable_domain(self):
        rel000_input = next(domain for domain in CONFIG["domains"] if domain["name"] == "REL000_INPUT")
        for path in AI05_TRIPLET:
            with self.subTest(path=path):
                self.assertTrue(any(pattern.match(path) for pattern in rel000_input["compiled"]))
                self.assertEqual(["DOCS", "NORMATIVE", "NORMATIVE_OPENAPI"], classifier.match_domains(CONFIG, path))
                self.assertEqual([], classify([path])["unmatched_paths"])

    def test_excluded_path_still_forces_full_through_another_full_domain(self):
        raw = json.loads(classifier.DEFAULT_CONFIG_PATH.read_text(encoding="utf-8"))
        raw["domains"].append({"name": "SYNTHETIC_FULL", "patterns": [NORMATIVE_MANIFEST], "full": True})
        config = classifier.validate_config(raw)
        plan = classifier.classify_paths(
            config, [NORMATIVE_MANIFEST], head_ref="feature/x", head_repo=REPOSITORY, repository=REPOSITORY
        )
        self.assertEqual("FULL", plan["classification"])
        self.assertIn("FULL:SYNTHETIC_FULL", plan["reasons"])
        self.assertNotIn("FULL:REL000_INPUT", plan["reasons"])

    def test_without_the_exclusion_the_triplet_would_be_rel000_input(self):
        raw = json.loads(classifier.DEFAULT_CONFIG_PATH.read_text(encoding="utf-8"))
        next(domain for domain in raw["domains"] if domain["name"] == "REL000_INPUT").pop("exclude")
        config = classifier.validate_config(raw)
        for path in AI05_TRIPLET:
            with self.subTest(path=path):
                self.assertIn("REL000_INPUT", classifier.match_domains(config, path))

    # ---- every other REL-000 input stays FULL

    def test_ai08_and_ai10_remain_full(self):
        for path in (AI08_BACKLOG, AI10_GATES):
            with self.subTest(path=path):
                self.assert_full_rel000(classify([path]))
                self.assertNotIn("NORMATIVE_OPENAPI", classify([path])["domains"])

    def test_ai05_together_with_ai08_or_ai10_is_full(self):
        for paths in (
            [AI05_OPENAPI, AI08_BACKLOG],
            [*AI05_TRIPLET, AI08_BACKLOG],
            [*AI05_TRIPLET, AI10_GATES],
            [NORMATIVE_CHECKSUMS, AI10_GATES],
        ):
            with self.subTest(paths=paths):
                plan = classify(paths)
                self.assert_full_rel000(plan)
                self.assertIn("NORMATIVE_OPENAPI", plan["domains"])

    def test_normative_tooling_remains_full(self):
        for path in (
            "docs/normative/v0.6/tools/validate_contracts.py",
            "docs/normative/v0.6/tools/new_checker.py",
            "docs/normative/v0.6/tools/CHECKSUMS_SHA256.txt",
        ):
            with self.subTest(path=path):
                self.assert_full_rel000(classify([path]))

    def test_every_other_tracked_v06_normative_file_remains_full(self):
        tracked = git(REPOSITORY_ROOT, "ls-files", "docs/normative/v0.6").splitlines()
        others = sorted(set(tracked) - set(AI05_TRIPLET))
        self.assertEqual(sorted(AI05_TRIPLET), sorted(set(tracked) & set(AI05_TRIPLET)))
        self.assertGreater(len(others), 50)
        self.assertIn("docs/normative/v0.6/contracts/AI-12_SIGNALR_CONTRACT.yaml", others)
        for path in others:
            with self.subTest(path=path):
                plan = classify([path])
                self.assert_full_rel000(plan)
                self.assertNotIn("NORMATIVE_OPENAPI", plan["domains"])

    def test_future_and_near_miss_v06_files_remain_full(self):
        for path in (
            "docs/normative/v0.6/contracts/AI-30_FUTURE_CONTRACT.yaml",
            "docs/normative/v0.6/NEW_INDEX.json",
            "docs/normative/v0.6/future/area/file.md",
            "docs/normative/v0.6/contracts/AI-05_OPENAPI.yml",
            "docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml.orig",
            "docs/normative/v0.6/contracts/AI-05_OPENAPI_V2.yaml",
            "docs/normative/v0.6/contracts/ai-05_openapi.yaml",
            "docs/normative/v0.6/contracts/nested/AI-05_OPENAPI.yaml",
            "docs/normative/v0.6/specs/MANIFEST.json",
            "docs/normative/v0.6/MANIFEST.json.bak",
            "docs/normative/v0.6/CHECKSUMS_SHA256.txt.new",
        ):
            with self.subTest(path=path):
                plan = classify([path])
                self.assert_full_rel000(plan)
                self.assertNotIn("NORMATIVE_OPENAPI", plan["domains"])

    def test_release_docs_and_rel000_scripts_remain_full_beside_the_triplet(self):
        for path in (
            "docs/releases/mvp-0-owner-decision.json",
            "docs/releases/evidence/rel-000-owner-001/approved-evidence-manifest.json",
            "docs/releases/future-release-note.md",
            "tools/test-rel-000-internal-release.ps1",
            "tools/backup-restore.common.ps1",
        ):
            with self.subTest(path=path):
                self.assert_full_rel000(classify([path]))
                self.assert_full_rel000(classify([*AI05_TRIPLET, path]))


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

    def proven_domain(self, raw):
        return next(item for item in raw["domains"] if item["name"] == "NUGET_PROJECT_GRAPH")

    def test_unknown_content_proof_rejected(self):
        raw = self.raw()
        self.proven_domain(raw)["content_proof"] = "trust-me"
        self.assert_rejected(raw, "CONFIG_DOMAINS_INVALID")

    def test_content_proven_domain_must_be_selective(self):
        raw = self.raw()
        domain = self.proven_domain(raw)
        domain.pop("job_sets")
        domain["full"] = True
        self.assert_rejected(raw, "CONFIG_DOMAINS_INVALID")

    def test_supersedes_requires_content_proof(self):
        raw = self.raw()
        backend = next(item for item in raw["domains"] if item["name"] == "BACKEND")
        backend["supersedes"] = ["DEPS"]
        self.assert_rejected(raw, "CONFIG_DOMAINS_INVALID")

    def test_supersedes_must_name_other_existing_domains(self):
        for supersedes in (["NOPE"], ["NUGET_PROJECT_GRAPH"], [], "DEPS"):
            with self.subTest(supersedes=supersedes):
                raw = self.raw()
                self.proven_domain(raw)["supersedes"] = supersedes
                self.assert_rejected(raw, "CONFIG_DOMAINS_INVALID")

    def test_content_proof_requires_supersedes(self):
        raw = self.raw()
        self.proven_domain(raw).pop("supersedes")
        self.assert_rejected(raw, "CONFIG_DOMAINS_INVALID")

    def test_only_nuget_lockfiles_can_leave_deps_by_proof(self):
        config = classifier.validate_config(self.raw())
        proven = [domain for domain in config["domains"] if domain["content_proof"] is not None]
        self.assertEqual(["NUGET_PROJECT_GRAPH"], [domain["name"] for domain in proven])
        self.assertEqual(["**/packages.lock.json"], proven[0]["patterns"])
        self.assertEqual(["DEPS"], proven[0]["supersedes"])

    def domain(self, raw, name):
        return next(item for item in raw["domains"] if item["name"] == name)

    def test_exclude_rejects_wildcards(self):
        for entry in (
            "docs/normative/v0.6/**",
            "docs/normative/v0.6/*.json",
            "docs/normative/v0.6/contracts/AI-05_*.yaml",
            "docs/normative/v0.6/**/MANIFEST.json",
            "docs/normative/v0.6/MANIFEST.jso?",
            "docs/normative/v0.6/[M]ANIFEST.json",
            "docs/normative/v0.6/{MANIFEST,CHECKSUMS_SHA256}.json",
        ):
            with self.subTest(entry=entry):
                raw = self.raw()
                self.domain(raw, "REL000_INPUT")["exclude"] = [entry]
                self.assert_rejected(raw, "CONFIG_EXCLUDE_INVALID")

    def test_exclude_rejects_non_literal_or_malformed_entries(self):
        for exclude in (
            [],
            [""],
            "docs/normative/v0.6/MANIFEST.json",
            [NORMATIVE_MANIFEST, NORMATIVE_MANIFEST],
            ["/docs/normative/v0.6/MANIFEST.json"],
            ["docs\\normative\\v0.6\\MANIFEST.json"],
            ["docs/normative/v0.6/../v0.6/MANIFEST.json"],
            ["docs/normative/v0.6/./MANIFEST.json"],
            ["docs/normative/v0.6//MANIFEST.json"],
            ["docs/normative/v0.6/contracts/"],
            [" docs/normative/v0.6/MANIFEST.json"],
        ):
            with self.subTest(exclude=exclude):
                raw = self.raw()
                self.domain(raw, "REL000_INPUT")["exclude"] = exclude
                self.assert_rejected(raw, "CONFIG_EXCLUDE_INVALID")

    def test_exclude_must_be_covered_by_the_domains_own_patterns(self):
        for entry in (
            "docs/adr/x.md",
            "docs/normative/v0.5/MANIFEST.json",
            "docs/normativex/v0.6/MANIFEST.json",
            "tools/ci/classify_changes.py",
            "apps/web/package.json",
            "src/Paqueteria.Api/Program.cs",
        ):
            with self.subTest(entry=entry):
                raw = self.raw()
                self.domain(raw, "REL000_INPUT")["exclude"] = [entry]
                self.assert_rejected(raw, "CONFIG_EXCLUDE_INVALID")

    def test_exclude_is_forbidden_for_control_domains(self):
        for name, covered in (
            ("SECURITY_CONTROL", ".gitleaks.toml"),
            ("CI_SELF", "tools/ci/classify_changes.py"),
            ("DEPS", "global.json"),
        ):
            with self.subTest(name=name):
                raw = self.raw()
                self.domain(raw, name)["exclude"] = [covered]
                self.assert_rejected(raw, "CONFIG_EXCLUDE_FORBIDDEN")

    def test_exclude_is_forbidden_for_every_domain_but_rel000_input(self):
        for name in [item["name"] for item in self.raw()["domains"] if item["name"] != "REL000_INPUT"]:
            with self.subTest(name=name):
                raw = self.raw()
                self.domain(raw, name)["exclude"] = [NORMATIVE_MANIFEST]
                self.assert_rejected(raw, "CONFIG_EXCLUDE_FORBIDDEN")
        raw = self.raw()
        raw["domains"].append({"name": "REL000_INPUT_EXTRA", "patterns": ["docs/x/**"], "exclude": ["docs/x/a.md"], "full": True})
        self.assert_rejected(raw, "CONFIG_EXCLUDE_FORBIDDEN")

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


# --------------------------------------------------------------------------- NuGet Project-graph proof

NET = "net10.0"
API_LOCK = "src/Paqueteria.Api/packages.lock.json"
ORDERS_LOCK = "src/Modules/Orders/Orders.Infrastructure/packages.lock.json"
NEW_MODULE_LOCK = "src/Modules/Incidents/Incidents.Infrastructure/packages.lock.json"
NEW_MODULE_CSPROJ = "src/Modules/Incidents/Incidents.Infrastructure/Incidents.Infrastructure.csproj"
API_CSPROJ = "src/Paqueteria.Api/Paqueteria.Api.csproj"
NPGSQL = {
    "type": "Direct",
    "requested": "[10.0.3, )",
    "resolved": "10.0.3",
    "contentHash": "IPGrrZnRkuW7OlHDhUESZz4G5DLkW7Nej==",
    "dependencies": {"Npgsql": "10.0.3"},
}
NPGSQL_CORE = {"type": "Transitive", "resolved": "10.0.3", "contentHash": "c29tZS1ucGdzcWwtaGFzaA=="}
OPENAPI = {"type": "Direct", "requested": "[10.0.10, )", "resolved": "10.0.10", "contentHash": "b3BlbmFwaQ=="}
SERILOG = {"type": "Transitive", "resolved": "4.1.0", "contentHash": "c2VyaWxvZw=="}


def lock(nodes, version=2, framework=NET):
    return json.dumps({"version": version, "dependencies": {framework: nodes}}, indent=2) + "\n"


def project(*deps):
    return {"type": "Project", "dependencies": {dep: "[1.0.0, )" for dep in deps}} if deps else {"type": "Project"}


def api_nodes(extra=None, **overrides):
    """A host lockfile shaped like src/Paqueteria.Api/packages.lock.json."""
    nodes = {
        "Microsoft.AspNetCore.OpenApi": copy.deepcopy(OPENAPI),
        "Npgsql": copy.deepcopy(NPGSQL_CORE),
        "Npgsql.EntityFrameworkCore.PostgreSQL": copy.deepcopy(NPGSQL),
        "orders.infrastructure": {
            "type": "Project",
            "dependencies": {
                "Npgsql.EntityFrameworkCore.PostgreSQL": "[10.0.3, )",
                "Paqueteria.Domain": "[1.0.0, )",
            },
        },
        "paqueteria.domain": project(),
    }
    nodes.update(copy.deepcopy(extra or {}))
    for name, node in overrides.items():
        if node is None:
            nodes.pop(name, None)
        else:
            nodes[name] = node
    return nodes


def orders_nodes():
    return {
        "Npgsql": copy.deepcopy(NPGSQL_CORE),
        "Npgsql.EntityFrameworkCore.PostgreSQL": copy.deepcopy(NPGSQL),
        "paqueteria.domain": project(),
    }


# The Project entries INC-001/FIN-001 add to every existing host/test lockfile.
INCIDENTS_PROJECTS = {
    "incidents.domain": project("Paqueteria.Domain"),
    "incidents.infrastructure": {
        "type": "Project",
        "dependencies": {
            "Incidents.Domain": "[1.0.0, )",
            "Npgsql.EntityFrameworkCore.PostgreSQL": "[10.0.3, )",
            "Paqueteria.Domain": "[1.0.0, )",
        },
    },
}
BASE_FILES = {
    API_CSPROJ: "<Project />\n",
    API_LOCK: lock(api_nodes()),
    ORDERS_LOCK: lock(orders_nodes()),
}


def new_module_files(api_lock_nodes=None, module_lock_nodes=None):
    """The INC-001/FIN-001 shape: a new module lockfile plus Project entries in the host lock."""
    module = {
        "Npgsql": copy.deepcopy(NPGSQL_CORE),
        "Npgsql.EntityFrameworkCore.PostgreSQL": copy.deepcopy(NPGSQL),
        "incidents.domain": project("Paqueteria.Domain"),
        "paqueteria.domain": project(),
    }
    return {
        API_CSPROJ: '<Project><ProjectReference Include="Incidents.Infrastructure.csproj" /></Project>\n',
        API_LOCK: lock(api_lock_nodes if api_lock_nodes is not None else api_nodes(INCIDENTS_PROJECTS)),
        NEW_MODULE_CSPROJ: "<Project />\n",
        NEW_MODULE_LOCK: lock(module_lock_nodes if module_lock_nodes is not None else module),
    }


def classify_tested(root, tested, source, head_ref="feature/x", certified=False, labels=None):
    base, tested, changed = classifier.resolve_tested_diff(root, tested, source)
    proofs = classifier.resolve_content_proofs(root, base, tested, changed)
    plan = classifier.classify_paths(
        CONFIG,
        changed,
        head_ref=head_ref,
        head_repo=REPOSITORY,
        repository=REPOSITORY,
        labels=labels,
        source_head_sha=source,
        certified_main_sha=source if certified else None,
        content_proofs=proofs,
    )
    classifier.validate_plan(plan, CONFIG)
    return plan, proofs[classifier.CONTENT_PROOF_NUGET_PROJECT_GRAPH]


def classify_repo(source_files, base_files=None, **kwargs):
    repo = make_merge_repo(dict(BASE_FILES if base_files is None else base_files), source_files)
    return classify_tested(Path(repo["root"]), repo["tested"], repo["source"], **kwargs)


def gate_verdict(plan):
    required = set(plan["required_jobs"])
    needs = {"classify": {"result": "success", "outputs": {"plan": classifier.serialize_plan(plan)}}}
    for job in ALL_JOBS:
        needs[job] = {"result": "success" if job in required else "skipped", "outputs": {}}
    evaluation = gate.evaluate(None, needs, CONFIG)
    return evaluation["verdict"], [item["reason"] for item in evaluation["failures"]]


class NuGetProjectGraphProofTests(unittest.TestCase):
    """Internal ProjectReference graph evolution may enter development; external drift may not."""

    def assert_allowed(self, plan, proven, expected_proven):
        self.assertEqual(sorted(expected_proven), proven)
        self.assertNotIn("DEPS", plan["domains"])
        self.assertIn("NUGET_PROJECT_GRAPH", plan["domains"])
        self.assertNotIn("FULL:DEPS", plan["reasons"])
        self.assertTrue(set(BACKEND) <= jobs(plan), "proven lockfiles still require backend validation")
        self.assertEqual(("PASS", []), gate_verdict(plan))

    def assert_rejected(self, plan):
        self.assertEqual("FULL", plan["classification"])
        self.assertIn("DEPS", plan["domains"])
        self.assertIn("FULL:DEPS", plan["reasons"])
        self.assertEqual(("FAIL", [gate.REASON_DEPENDENCY_CHANGE_NOT_ALLOWED]), gate_verdict(plan))

    def assert_unproven_and_rejected(self, source_files, **kwargs):
        plan, proven = classify_repo(source_files, **kwargs)
        self.assertEqual([], proven)
        self.assert_rejected(plan)

    # ---- allowed toward development

    def test_new_internal_project_reference_only_is_allowed_toward_development(self):
        plan, proven = classify_repo(new_module_files())
        self.assert_allowed(plan, proven, [API_LOCK, NEW_MODULE_LOCK])
        self.assertEqual("SELECTIVE", plan["classification"])
        self.assertNotIn("rel000", jobs(plan))

    def test_project_reference_added_to_existing_project_node_is_allowed(self):
        orders = api_nodes()["orders.infrastructure"]
        orders["dependencies"]["Incidents.Domain"] = "[1.0.0, )"
        plan, proven = classify_repo({API_LOCK: lock(api_nodes(INCIDENTS_PROJECTS, **{"orders.infrastructure": orders}))})
        self.assert_allowed(plan, proven, [API_LOCK])

    def test_project_reference_removal_only_is_allowed(self):
        plan, proven = classify_repo({API_LOCK: lock(api_nodes(**{"orders.infrastructure": None}))})
        self.assert_allowed(plan, proven, [API_LOCK])

    def test_project_node_internal_dependency_change_only_is_allowed(self):
        orders = api_nodes()["orders.infrastructure"]
        del orders["dependencies"]["Paqueteria.Domain"]
        plan, proven = classify_repo({API_LOCK: lock(api_nodes(**{"orders.infrastructure": orders}))})
        self.assert_allowed(plan, proven, [API_LOCK])

    def test_formatting_only_lockfile_change_is_allowed(self):
        plan, proven = classify_repo({ORDERS_LOCK: json.dumps(json.loads(BASE_FILES[ORDERS_LOCK])) + "\n"})
        self.assert_allowed(plan, proven, [ORDERS_LOCK])

    # ---- external drift stays DEPS

    def test_external_nuget_package_added_is_rejected(self):
        self.assert_unproven_and_rejected({API_LOCK: lock(api_nodes(Serilog=copy.deepcopy(SERILOG)))})

    def test_external_nuget_package_removed_is_rejected(self):
        self.assert_unproven_and_rejected({API_LOCK: lock(api_nodes(**{"Microsoft.AspNetCore.OpenApi": None}))})

    def test_external_package_version_changed_is_rejected(self):
        bumped = dict(NPGSQL, resolved="10.0.4")
        self.assert_unproven_and_rejected({API_LOCK: lock(api_nodes(**{"Npgsql.EntityFrameworkCore.PostgreSQL": bumped}))})

    def test_external_content_hash_changed_is_rejected(self):
        self.assert_unproven_and_rejected({API_LOCK: lock(api_nodes(Npgsql=dict(NPGSQL_CORE, contentHash="b3RoZXI=")))})

    def test_nuget_requested_range_changed_is_rejected(self):
        reranged = dict(NPGSQL, requested="[10.0.4, )")
        self.assert_unproven_and_rejected({API_LOCK: lock(api_nodes(**{"Npgsql.EntityFrameworkCore.PostgreSQL": reranged}))})

    def test_external_package_transitive_edge_changed_is_rejected(self):
        changed = copy.deepcopy(NPGSQL)
        changed["dependencies"]["Npgsql"] = "10.0.4"
        self.assert_unproven_and_rejected({API_LOCK: lock(api_nodes(**{"Npgsql.EntityFrameworkCore.PostgreSQL": changed}))})

    def test_external_package_type_changed_is_rejected(self):
        promoted = dict(NPGSQL_CORE, type="Direct", requested="[10.0.3, )")
        self.assert_unproven_and_rejected({API_LOCK: lock(api_nodes(Npgsql=promoted))})

    def test_project_node_reaching_a_new_package_range_is_rejected(self):
        orders = api_nodes()["orders.infrastructure"]
        orders["dependencies"]["Npgsql.EntityFrameworkCore.PostgreSQL"] = "[10.0.4, )"
        self.assert_unproven_and_rejected({API_LOCK: lock(api_nodes(**{"orders.infrastructure": orders}))})

    def test_project_node_reaching_a_package_absent_from_the_graph_is_rejected(self):
        orders = api_nodes()["orders.infrastructure"]
        orders["dependencies"]["Serilog"] = "[4.1.0, )"
        self.assert_unproven_and_rejected({API_LOCK: lock(api_nodes(**{"orders.infrastructure": orders}))})

    def test_project_turned_into_package_is_rejected(self):
        as_package = {"type": "Direct", "requested": "[1.0.0, )", "resolved": "1.0.0", "contentHash": "eA=="}
        self.assert_unproven_and_rejected({API_LOCK: lock(api_nodes(**{"paqueteria.domain": as_package}))})

    def test_mixed_project_reference_and_external_package_change_is_rejected(self):
        plan, proven = classify_repo(
            new_module_files(api_lock_nodes=api_nodes(INCIDENTS_PROJECTS, Serilog=copy.deepcopy(SERILOG)))
        )
        self.assertEqual([NEW_MODULE_LOCK], proven)
        self.assert_rejected(plan)

    def test_new_lockfile_with_package_absent_from_base_is_rejected(self):
        module = {
            "Serilog": dict(SERILOG, type="Direct", requested="[4.1.0, )"),
            "incidents.domain": project(),
        }
        plan, proven = classify_repo(new_module_files(module_lock_nodes=module))
        self.assertEqual([API_LOCK], proven)
        self.assert_rejected(plan)

    def test_new_lockfile_with_other_version_of_a_base_package_is_rejected(self):
        module = {"Npgsql": dict(NPGSQL_CORE, resolved="10.0.4"), "incidents.domain": project()}
        plan, proven = classify_repo(new_module_files(module_lock_nodes=module))
        self.assertEqual([API_LOCK], proven)
        self.assert_rejected(plan)

    def test_new_lockfile_with_new_target_framework_is_rejected(self):
        files = new_module_files()
        files[NEW_MODULE_LOCK] = lock({"incidents.domain": project()}, framework="net11.0")
        plan, proven = classify_repo(files)
        self.assertEqual([API_LOCK], proven)
        self.assert_rejected(plan)

    def test_lockfile_deletion_is_rejected(self):
        repo = make_merge_repo(dict(BASE_FILES), {})
        root = Path(repo["root"])
        git(root, "checkout", "-q", "feature/x")
        git(root, "rm", "-q", ORDERS_LOCK)
        git(root, "commit", "-q", "-m", "drop lock")
        source = git(root, "rev-parse", "HEAD")
        git(root, "checkout", "-q", "main")
        git(root, "merge", "-q", "--no-ff", "-m", "tested", "feature/x")
        plan, proven = classify_tested(root, git(root, "rev-parse", "HEAD"), source)
        self.assertEqual([], proven)
        self.assert_rejected(plan)

    def test_other_dependency_files_keep_deps_beside_proven_lockfiles(self):
        for path in ("Directory.Packages.props", "global.json", ".config/dotnet-tools.json", "apps/web/pnpm-lock.yaml"):
            with self.subTest(path=path):
                files = new_module_files()
                files[path] = "changed\n"
                plan, proven = classify_repo(files)
                self.assertEqual(sorted([API_LOCK, NEW_MODULE_LOCK]), proven)
                self.assert_rejected(plan)

    # ---- fail closed on anything unparseable or ambiguous

    def test_malformed_lockfile_fails_closed(self):
        for text in (
            BASE_FILES[API_LOCK][:-40],
            "",
            "[]\n",
            json.dumps({"version": 2}) + "\n",
            json.dumps({"version": 2, "dependencies": {}}) + "\n",
            json.dumps({"version": 2, "dependencies": {NET: []}}) + "\n",
            json.dumps({"version": "2", "dependencies": {NET: {}}}) + "\n",
            json.dumps({"version": 2, "dependencies": {NET: {}}, "extra": 1}) + "\n",
        ):
            with self.subTest(text=text[:40]):
                self.assert_unproven_and_rejected({API_LOCK: text})

    def test_lock_version_change_fails_closed(self):
        self.assert_unproven_and_rejected({ORDERS_LOCK: lock(orders_nodes(), version=1)})

    def test_unknown_lock_version_fails_closed(self):
        v3 = dict(BASE_FILES, **{ORDERS_LOCK: lock(orders_nodes(), version=3)})
        self.assert_unproven_and_rejected({ORDERS_LOCK: lock(orders_nodes(), version=3) + "\n"}, base_files=v3)

    def test_unknown_node_type_or_key_fails_closed(self):
        for name, node in (
            ("incidents.domain", {"type": "Package"}),
            ("incidents.domain", {"type": "Project", "path": "../x"}),
            ("incidents.domain", {"type": "Project", "dependencies": {"Paqueteria.Domain": 1}}),
            ("Serilog", {"type": "Transitive", "resolved": "4.1.0"}),
            ("Npgsql", dict(NPGSQL_CORE, sha512="x")),
        ):
            with self.subTest(name=name, node=node):
                self.assert_unproven_and_rejected({API_LOCK: lock(api_nodes(**{name: node}))})

    def test_duplicate_json_keys_fail_closed(self):
        text = BASE_FILES[API_LOCK].replace(
            '"paqueteria.domain": {', '"paqueteria.domain": {"type": "Project"},\n      "paqueteria.domain": {', 1
        )
        self.assertNotEqual(BASE_FILES[API_LOCK], text)
        self.assert_unproven_and_rejected({API_LOCK: text})

    def test_case_colliding_project_and_package_names_fail_closed(self):
        self.assert_unproven_and_rejected({API_LOCK: lock(api_nodes(npgsql=project()))})

    def test_malformed_base_catalog_fails_closed_for_new_lockfiles(self):
        base = dict(BASE_FILES, **{"tools/Broken/packages.lock.json": "{"})
        plan, proven = classify_repo(new_module_files(), base_files=base)
        self.assertEqual([], proven)
        self.assert_rejected(plan)

    # ---- the proof cannot be forged or widened

    def test_lockfiles_without_proof_remain_deps(self):
        for path in (API_LOCK, NEW_MODULE_LOCK, "tests/Paqueteria.UnitTests/packages.lock.json"):
            with self.subTest(path=path):
                plan = classify([path])
                self.assertIn("DEPS", plan["domains"])
                self.assertNotIn("NUGET_PROJECT_GRAPH", plan["domains"])
                self.assertEqual(("FAIL", [gate.REASON_DEPENDENCY_CHANGE_NOT_ALLOWED]), gate_verdict(plan))

    def test_proof_for_a_non_lockfile_path_is_ignored(self):
        paths = ["apps/web/package.json", "global.json"]
        plan = classifier.classify_paths(
            CONFIG,
            paths,
            head_ref="feature/x",
            head_repo=REPOSITORY,
            repository=REPOSITORY,
            content_proofs={classifier.CONTENT_PROOF_NUGET_PROJECT_GRAPH: paths},
        )
        self.assertEqual(["DEPS"], plan["domains"])
        self.assertEqual(("FAIL", [gate.REASON_DEPENDENCY_CHANGE_NOT_ALLOWED]), gate_verdict(plan))

    def test_full_ci_label_on_proven_lockfiles_is_full_without_dependency_rejection(self):
        plan, _ = classify_repo(new_module_files(), labels=["full-ci"])
        self.assertEqual("FULL", plan["classification"])
        self.assertNotIn("DEPS", plan["domains"])
        self.assertEqual(("PASS", []), gate_verdict(plan))

    # ---- MAIN_BACKSYNC is unchanged

    def test_certified_main_backsync_is_unchanged_with_proven_lockfiles(self):
        plan, _ = classify_repo(new_module_files(), head_ref="main", certified=True)
        self.assertEqual("MAIN_BACKSYNC", plan["classification"])
        self.assertEqual(set(ALL_JOBS) - {"rel000"}, jobs(plan))
        self.assertEqual(("PASS", []), gate_verdict(plan))

    def test_certified_main_backsync_still_carries_external_drift(self):
        bumped = dict(NPGSQL, resolved="10.0.4")
        plan, _ = classify_repo(
            {API_LOCK: lock(api_nodes(**{"Npgsql.EntityFrameworkCore.PostgreSQL": bumped}))}, head_ref="main", certified=True
        )
        self.assertEqual("MAIN_BACKSYNC", plan["classification"])
        self.assertIn("DEPS", plan["domains"])
        self.assertEqual(set(ALL_JOBS) - {"rel000"}, jobs(plan))
        self.assertEqual(("PASS", []), gate_verdict(plan))

    def test_uncertified_main_with_external_drift_is_still_rejected(self):
        bumped = dict(NPGSQL, resolved="10.0.4")
        plan, _ = classify_repo({API_LOCK: lock(api_nodes(**{"Npgsql.EntityFrameworkCore.PostgreSQL": bumped}))}, head_ref="main")
        self.assertIn("FULL:MAIN_BACKSYNC_UNCERTIFIED", plan["reasons"])
        self.assert_rejected(plan)

    # ---- CLI and the committed repository

    def test_cli_applies_the_proof_end_to_end(self):
        repo = make_merge_repo(dict(BASE_FILES), new_module_files())
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp) / "plan.json"
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
                    ]
                )
            self.assertEqual(0, code)
            plan = json.loads(output.read_text(encoding="utf-8"))
        self.assertNotIn("DEPS", plan["domains"])
        self.assertEqual(("PASS", []), gate_verdict(plan))

    def test_every_committed_lockfile_is_understood_by_the_strict_parser(self):
        """Real Project-only evolution of any committed lockfile must be provable."""
        head = git(REPOSITORY_ROOT, "rev-parse", "HEAD")
        tracked = sorted(git(REPOSITORY_ROOT, "ls-files", "*packages.lock.json").splitlines())
        self.assertGreater(len(tracked), 10)
        self.assertEqual(tracked, classifier.prove_nuget_project_graph_only(REPOSITORY_ROOT, head, head, tracked))


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
