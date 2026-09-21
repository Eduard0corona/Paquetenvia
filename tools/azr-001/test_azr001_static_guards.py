"""Unit tests for the AZR-001 §28 static guards and §27 deployment gate."""

from __future__ import annotations

import copy
import json
import os
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import azr001_static_guards as guards  # noqa: E402

TESTED_SHA = "a" * 40
CONTROL_PLANE_SHA = "b" * 40
OTHER_SHA = "0" * 40
DIGEST = "sha256:" + "a" * 64

DEPLOY_WORKFLOW = textwrap.dedent(
    """
    name: Deploy Azure DEV-SYNTHETIC
    on:
      workflow_dispatch:
        inputs:
          tested_git_sha:
            required: true
            type: string
          db_ops_digest:
            required: false
            type: string
    permissions: {}
    concurrency:
      group: azure-dev
      cancel-in-progress: false
    jobs:
      provenance:
        runs-on: ubuntu-latest
        permissions:
          contents: read
        steps:
          - run: |
              [[ "$DIGEST" =~ ^sha256:[0-9a-f]{64}$ ]]
      deploy-core:
        needs: provenance
        runs-on: ubuntu-latest
        environment: azure-dev
        permissions:
          id-token: write
          contents: read
        steps:
          - uses: azure/login@v2
          - shell: pwsh
            run: ./deploy/azure/deploy-core.ps1 -SubscriptionId x -TestedGitSha y
    """
)

FOUNDATION_WORKFLOW = "name: Foundation CI\non:\n  push:\njobs:\n" + "".join(f"  job{i}:\n    runs-on: self-hosted\n" for i in range(13))

DOCKERFILE_WEB = "ARG NEXT_PUBLIC_API_BASE_URL\nARG NEXT_PUBLIC_TRACKING_BRAND_NAME\nARG NEXT_PUBLIC_TRACKING_SUPPORT_URL\nENV NODE_ENV=production\n"
DOCKERFILE_DOTNET = "FROM base\nENTRYPOINT [\"dotnet\", \"Paqueteria.Api.dll\"]\n"
DEPLOY_CORE = "param([string]$TestedGitSha)\nif ((git rev-parse HEAD) -cne $TestedGitSha) { throw 'STOP_FOR_CONTRACT_REVIEW' }\n"
DEPLOY_MIGRATION = "if ($DbOpsDigest -cnotmatch '^sha256:[0-9a-f]{64}$') { throw 'digest' }\n$c = \"Host=h;Database=paqueteria;Username=u;Password=p;SSL Mode=Require;Maximum Pool Size=4;Minimum Pool Size=0\"\n"


def dotnet_env(extra: list[dict] | None = None) -> list[dict]:
    env = [
        {"name": "DOTNET_ENVIRONMENT", "value": "DevSynthetic"},
        {"name": "ASPNETCORE_ENVIRONMENT", "value": "DevSynthetic"},
        {"name": "PAQUETERIA_DEPLOYMENT_CLASS", "value": "DEV_SYNTHETIC"},
    ]
    return env + (extra or [])


def workload(resource_type: str, name: str, *, image: str = "[parameters('image')]", env: list[dict] | None = None, secrets: list[dict] | None = None, command: list[str] | None = None, ingress: dict | None = None, max_replicas: int | None = 1) -> dict:
    properties: dict = {
        "configuration": {
            "registries": [{"server": "x.azurecr.io", "identity": "[resourceId('mi')]"}],
            "secrets": secrets or [],
        },
        "template": {"containers": [{"name": "c", "image": image, "env": env or dotnet_env(), "command": command or []}]},
    }
    if ingress is not None:
        properties["configuration"]["ingress"] = ingress
    if resource_type == "Microsoft.App/containerApps" and max_replicas is not None:
        properties["template"]["scale"] = {"maxReplicas": max_replicas}
    return {"type": resource_type, "name": name, "identity": {"type": "UserAssigned"}, "properties": properties}


def kv_secret(name: str) -> dict:
    return {"name": name, "keyVaultUrl": f"[format('{{0}}secrets/{{1}}', reference(x).vaultUri, '{name}')]", "identity": "[resourceId('mi')]"}


def core_template() -> dict:
    return {
        "variables": {"identityNames": sorted(guards.NORMATIVE_IDENTITIES)},
        "resources": [
            {"type": "Microsoft.OperationalInsights/workspaces", "name": "law"},
            {"type": "Microsoft.App/managedEnvironments", "name": "cae", "properties": {"workloadProfiles": [{"name": "Consumption", "workloadProfileType": "Consumption"}]}},
            {"type": "Microsoft.ContainerRegistry/registries", "name": "acr", "sku": {"name": "Basic"}, "properties": {"adminUserEnabled": False}},
            {"type": "Microsoft.DBforPostgreSQL/flexibleServers", "name": "pg", "properties": {"version": "18"}},
            {"type": "Microsoft.ManagedIdentity/userAssignedIdentities", "name": "[variables('identityNames')[copyIndex()]]"},
            {"type": "Microsoft.Authorization/roleAssignments", "name": "ra"},
        ],
    }


def jobs_template() -> dict:
    return {
        "resources": [
            workload("Microsoft.App/jobs", "job-pv-azrdev-migrate", secrets=[kv_secret("pg-migrate-connection")], command=["dotnet", "/app/migrator/Paqueteria.DatabaseMigrator.dll"]),
        ]
    }


class GuardFixture:
    def __init__(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.arm = self.root / "arm"
        self.arm.mkdir()
        (self.root / "deploy" / "azure").mkdir(parents=True)
        (self.root / ".github" / "workflows").mkdir(parents=True)
        (self.root / "apps" / "web" / "src").mkdir(parents=True)
        (self.root / "apps" / "web" / "src" / "a.ts").write_text("process.env.NEXT_PUBLIC_API_BASE_URL; process.env.NEXT_PUBLIC_TRACKING_SUPPORT_URL;", encoding="utf-8")
        self.write_text("deploy/azure/Dockerfile.web", DOCKERFILE_WEB)
        self.write_text("deploy/azure/Dockerfile.api", DOCKERFILE_DOTNET)
        self.write_text("deploy/azure/Dockerfile.worker", DOCKERFILE_DOTNET)
        self.write_text("deploy/azure/deploy-core.ps1", DEPLOY_CORE)
        self.write_text("deploy/azure/deploy-migration-job.ps1", DEPLOY_MIGRATION)
        self.write_text(guards.DEPLOY_WORKFLOW, DEPLOY_WORKFLOW)
        self.write_text(guards.FOUNDATION_WORKFLOW, FOUNDATION_WORKFLOW)
        self.templates = {"core": core_template(), "migration-job": jobs_template()}

    def write_text(self, relative: str, text: str) -> None:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def context(self) -> guards.Context:
        for name, document in self.templates.items():
            (self.root / "deploy" / "azure" / f"{name}.bicep").write_text("// synthetic", encoding="utf-8")
            (self.arm / f"{name}.json").write_text(json.dumps(document), encoding="utf-8")
        return guards.load_context(self.root, self.arm)

    def close(self) -> None:
        self.tmp.cleanup()


def results_by_number(ctx: guards.Context) -> dict[int, guards.GuardResult]:
    return {r.number: r for r in guards.run_guards(ctx)}


class StaticGuardTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = GuardFixture()
        self.addCleanup(self.fixture.close)

    def test_conformant_fixture_passes_every_guard(self) -> None:
        results = guards.run_guards(self.fixture.context())
        failing = [f"{r.number}:{r.detail}" for r in results if not r.passed]
        self.assertEqual(failing, [])
        self.assertEqual(sorted(r.number for r in results), list(range(0, 31)))

    def test_guard_numbers_follow_section_28(self) -> None:
        numbers = [guard(self.fixture.context()).number for guard in guards.GUARDS]
        self.assertEqual(numbers, list(range(0, 31)))

    def test_foundation_job_count_is_enforced(self) -> None:
        self.fixture.write_text(guards.FOUNDATION_WORKFLOW, FOUNDATION_WORKFLOW + "  extra:\n    runs-on: self-hosted\n")
        self.assertEqual(results_by_number(self.fixture.context())[0].status, "FAIL")

    def test_missing_compiled_output_blocks(self) -> None:
        self.fixture.context()
        (self.fixture.arm / "core.json").unlink()
        with self.assertRaises(guards.GuardError):
            guards.load_context(self.fixture.root, self.fixture.arm)

    def test_deploy_workflow_must_be_dispatch_only(self) -> None:
        self.fixture.write_text(guards.DEPLOY_WORKFLOW, DEPLOY_WORKFLOW.replace("on:\n  workflow_dispatch:", "on:\n  push:\n  workflow_dispatch:"))
        result = results_by_number(self.fixture.context())[2]
        self.assertEqual(result.status, "FAIL")
        self.assertIn("push", result.detail)

    def test_azure_job_requires_environment_and_concurrency(self) -> None:
        self.fixture.write_text(guards.DEPLOY_WORKFLOW, DEPLOY_WORKFLOW.replace("environment: azure-dev", "timeout-minutes: 5"))
        self.assertEqual(results_by_number(self.fixture.context())[3].status, "FAIL")
        self.fixture.write_text(guards.DEPLOY_WORKFLOW, DEPLOY_WORKFLOW.replace("cancel-in-progress: false", "cancel-in-progress: true"))
        self.assertEqual(results_by_number(self.fixture.context())[3].status, "FAIL")

    def test_floating_tag_fails(self) -> None:
        self.fixture.templates["migration-job"]["resources"][0]["properties"]["template"]["containers"][0]["image"] = "x.azurecr.io/db-ops:latest"
        self.assertEqual(results_by_number(self.fixture.context())[4].status, "FAIL")
        self.fixture.templates["migration-job"]["resources"][0]["properties"]["template"]["containers"][0]["image"] = f"x.azurecr.io/db-ops@{DIGEST}"
        self.assertTrue(results_by_number(self.fixture.context())[4].passed)

    def test_digest_regex_required_in_scripts_and_workflow(self) -> None:
        self.fixture.write_text("deploy/azure/deploy-migration-job.ps1", DEPLOY_MIGRATION.replace("^sha256:[0-9a-f]{64}$", "^sha256:"))
        self.assertEqual(results_by_number(self.fixture.context())[5].status, "FAIL")
        self.fixture.write_text("deploy/azure/deploy-migration-job.ps1", DEPLOY_MIGRATION)
        self.fixture.write_text(guards.DEPLOY_WORKFLOW, DEPLOY_WORKFLOW.replace("^sha256:[0-9a-f]{64}$", "^sha256:"))
        self.assertEqual(results_by_number(self.fixture.context())[5].status, "FAIL")

    def test_next_public_manifest_must_cover_source_and_minimum(self) -> None:
        self.fixture.write_text("deploy/azure/Dockerfile.web", "ARG NEXT_PUBLIC_API_BASE_URL\nENV NODE_ENV=production\n")
        result = results_by_number(self.fixture.context())[6]
        self.assertEqual(result.status, "FAIL")
        self.assertIn("NEXT_PUBLIC_TRACKING_SUPPORT_URL", result.detail)
        self.assertIn("NEXT_PUBLIC_TRACKING_BRAND_NAME", result.detail)

    def test_development_environment_fails(self) -> None:
        env = self.fixture.templates["migration-job"]["resources"][0]["properties"]["template"]["containers"][0]["env"]
        env[0]["value"] = "Development"
        self.assertEqual(results_by_number(self.fixture.context())[7].status, "FAIL")

    def test_missing_deployment_class_fails_sec003(self) -> None:
        env = self.fixture.templates["migration-job"]["resources"][0]["properties"]["template"]["containers"][0]["env"]
        del env[2]
        self.assertEqual(results_by_number(self.fixture.context())[8].status, "FAIL")

    def test_testing_variables_fail(self) -> None:
        env = self.fixture.templates["migration-job"]["resources"][0]["properties"]["template"]["containers"][0]["env"]
        env.append({"name": "PAQUETERIA_TESTING_PROBES", "value": "true"})
        self.assertEqual(results_by_number(self.fixture.context())[9].status, "FAIL")

    def test_mock_ingress_requires_cidr_allowlist(self) -> None:
        api = workload("Microsoft.App/containerApps", "ca-pv-azrdev-api", env=dotnet_env([{"name": "Authentication__Provider", "value": "Mock"}, {"name": "ProofStorage__Provider", "value": "Disabled"}, {"name": "ProofStorage__ThreatScanner", "value": "Disabled"}]), ingress={"external": True, "ipSecurityRestrictions": []})
        self.fixture.templates["apps"] = {"resources": [api]}
        self.assertEqual(results_by_number(self.fixture.context())[10].status, "FAIL")
        api["properties"]["configuration"]["ingress"]["ipSecurityRestrictions"] = [{"action": "Allow", "ipAddressRange": "0.0.0.0/0"}]
        self.assertEqual(results_by_number(self.fixture.context())[10].status, "FAIL")
        api["properties"]["configuration"]["ingress"]["ipSecurityRestrictions"] = [{"action": "Allow", "ipAddressRange": "203.0.113.10/32"}]
        self.assertTrue(results_by_number(self.fixture.context())[10].passed)

    def test_api_and_worker_single_replica_and_proof_storage(self) -> None:
        api = workload("Microsoft.App/containerApps", "ca-pv-azrdev-api", env=dotnet_env([{"name": "ProofStorage__Provider", "value": "Disabled"}, {"name": "ProofStorage__ThreatScanner", "value": "Disabled"}]), max_replicas=2)
        worker = workload("Microsoft.App/containerApps", "ca-pv-azrdev-worker", env=dotnet_env([{"name": "ProofStorage__Provider", "value": "Disabled"}]), max_replicas=1)
        self.fixture.templates["apps"] = {"resources": [api, worker]}
        results = results_by_number(self.fixture.context())
        self.assertEqual(results[11].status, "FAIL")
        self.assertTrue(results[12].passed)
        self.assertTrue(results[14].passed)
        self.assertEqual(results[15].status, "FAIL")

    def test_connection_string_without_pool_limits_fails(self) -> None:
        self.fixture.write_text("deploy/azure/deploy-migration-job.ps1", DEPLOY_MIGRATION.replace(";Maximum Pool Size=4;Minimum Pool Size=0", ""))
        self.assertEqual(results_by_number(self.fixture.context())[13].status, "FAIL")

    def test_prohibited_resources_fail(self) -> None:
        cases = {
            16: {"type": "Microsoft.Cache/redis", "name": "r"},
            17: {"type": "Microsoft.SignalRService/signalR", "name": "s"},
            19: {"type": "Microsoft.Network/privateEndpoints", "name": "pe"},
            21: {"type": "Microsoft.Maintenance/maintenanceConfigurations", "name": "m"},
        }
        for number, resource in cases.items():
            with self.subTest(guard=number):
                fixture = GuardFixture()
                try:
                    fixture.templates["core"]["resources"].append(resource)
                    results = results_by_number(fixture.context())
                    self.assertEqual(results[number].status, "FAIL")
                    self.assertEqual(results[26].status, "FAIL")
                finally:
                    fixture.close()

    def test_dedicated_profile_and_premium_ingress_fail(self) -> None:
        environment = self.fixture.templates["core"]["resources"][1]
        environment["properties"]["workloadProfiles"].append({"name": "D4", "workloadProfileType": "D4"})
        self.assertEqual(results_by_number(self.fixture.context())[18].status, "FAIL")
        environment["properties"]["workloadProfiles"].pop()
        environment["properties"]["ingressConfiguration"] = {"workloadProfileName": "ingress"}
        self.assertEqual(results_by_number(self.fixture.context())[20].status, "FAIL")

    def test_custom_maintenance_window_fails(self) -> None:
        self.fixture.templates["core"]["resources"][3]["properties"]["maintenanceWindow"] = {"customWindow": "Enabled"}
        self.assertEqual(results_by_number(self.fixture.context())[21].status, "FAIL")

    def test_startup_migration_fails(self) -> None:
        api = workload("Microsoft.App/containerApps", "ca-pv-azrdev-api", env=dotnet_env([{"name": "ProofStorage__Provider", "value": "Disabled"}, {"name": "ProofStorage__ThreatScanner", "value": "Disabled"}]), command=["dotnet", "Paqueteria.DatabaseMigrator.dll", "apply"])
        self.fixture.templates["apps"] = {"resources": [api]}
        self.assertEqual(results_by_number(self.fixture.context())[22].status, "FAIL")
        del self.fixture.templates["apps"]
        self.fixture.write_text("deploy/azure/Dockerfile.api", DOCKERFILE_DOTNET + "COPY Paqueteria.DatabaseMigrator /app\n")
        self.assertEqual(results_by_number(self.fixture.context())[22].status, "FAIL")

    def test_acr_admin_and_registry_credentials_fail(self) -> None:
        self.fixture.templates["core"]["resources"][2]["properties"]["adminUserEnabled"] = True
        self.assertEqual(results_by_number(self.fixture.context())[23].status, "FAIL")
        self.fixture.templates["core"]["resources"][2]["properties"]["adminUserEnabled"] = False
        self.fixture.templates["migration-job"]["resources"][0]["properties"]["configuration"]["registries"][0]["passwordSecretRef"] = "acr-pw"
        self.assertEqual(results_by_number(self.fixture.context())[24].status, "FAIL")

    def test_managed_identity_required_for_pull_and_secrets(self) -> None:
        job = self.fixture.templates["migration-job"]["resources"][0]
        del job["properties"]["configuration"]["registries"][0]["identity"]
        self.assertEqual(results_by_number(self.fixture.context())[25].status, "FAIL")
        job["properties"]["configuration"]["registries"][0]["identity"] = "[resourceId('mi')]"
        job["properties"]["configuration"]["secrets"].append({"name": "inline", "value": "x"})
        self.assertEqual(results_by_number(self.fixture.context())[25].status, "FAIL")

    def test_identity_drift_fails_authorized_set(self) -> None:
        self.fixture.templates["core"]["variables"]["identityNames"] = sorted(guards.NORMATIVE_IDENTITIES - {"mi-azr-dev-db-migrate"}) + ["mi-azr-dev-migrate"]
        result = results_by_number(self.fixture.context())[26]
        self.assertEqual(result.status, "FAIL")
        self.assertIn("mi-azr-dev-migrate", result.detail)
        self.assertIn("mi-azr-dev-db-migrate", result.detail)

    def test_job_secret_classes(self) -> None:
        acceptance = workload("Microsoft.App/jobs", "job-pv-azrdev-acceptance", secrets=[kv_secret("pg-api-runtime-connection")])
        rls = workload("Microsoft.App/jobs", "job-pv-azrdev-rls", secrets=[kv_secret("pg-api-runtime-connection"), kv_secret("pg-worker-runtime-connection")])
        self.fixture.templates["jobs"] = {"resources": [acceptance, rls]}
        results = results_by_number(self.fixture.context())
        self.assertEqual(results[27].status, "FAIL")
        self.assertTrue(results[28].passed)
        self.assertTrue(results[29].passed)
        acceptance["properties"]["configuration"]["secrets"] = []
        rls["properties"]["configuration"]["secrets"].append(kv_secret("pg-admin-password"))
        results = results_by_number(self.fixture.context())
        self.assertTrue(results[27].passed)
        self.assertEqual(results[28].status, "FAIL")
        self.assertEqual(results[29].status, "FAIL")

    def test_unknown_workload_fails_manifest(self) -> None:
        self.fixture.templates["extra"] = {"resources": [workload("Microsoft.App/containerApps", "ca-debug")]}
        result = results_by_number(self.fixture.context())[30]
        self.assertEqual(result.status, "FAIL")
        self.assertIn("ca-debug", result.detail)

    def test_vacuous_guards_are_labelled(self) -> None:
        results = results_by_number(self.fixture.context())
        for number in (10, 11, 12, 14, 15, 27, 28, 29):
            self.assertEqual(results[number].status, "PASS (vacuous)", number)


def git(root: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(root), "-c", "user.name=t", "-c", "user.email=t@example.invalid", *args], check=True, capture_output=True, text=True).stdout.strip()


def make_history(root: Path) -> tuple[str, str, str]:
    """Create M → N on main plus an orphan commit unrelated to both."""
    git(root, "init", "-q", "-b", "main")
    git(root, "commit", "-q", "--allow-empty", "-m", "M")
    m = git(root, "rev-parse", "HEAD")
    git(root, "commit", "-q", "--allow-empty", "-m", "N")
    n = git(root, "rev-parse", "HEAD")
    orphan = git(root, "commit-tree", git(root, "write-tree"), "-m", "unrelated")
    return m, n, orphan


def foundation_jobs(run_id: int = 35528193093, head_sha: str = TESTED_SHA, count: int = 13, first_job: dict | None = None) -> dict:
    jobs = [{"id": 100 + i, "run_id": run_id, "name": f"job{i}", "head_sha": head_sha, "status": "completed", "conclusion": "success"} for i in range(count)]
    if first_job:
        jobs[0].update(first_job)
    return {"total_count": len(jobs), "jobs": jobs}


class DeployGateTests(unittest.TestCase):
    """§27 provenance gate under the canonical mapping (no in-tree SHA pin, no control-plane equality)."""

    def run_json(self, **overrides) -> dict:
        run = {"id": 35528193093, "run_attempt": 1, "name": "Foundation CI", "head_sha": TESTED_SHA, "event": "push", "head_branch": "main", "status": "completed", "conclusion": "success"}
        run.update(overrides)
        return run

    def gate(self, run: dict | None = None, *, tested: str = TESTED_SHA, ref: str = "refs/heads/main", dispatch: str = CONTROL_PLANE_SHA, ancestor: bool | None = True, jobs: dict | None = None, run_id: str | None = "35528193093") -> list[str]:
        run = run or self.run_json()
        return guards.evaluate_deploy_gate(tested_git_sha=tested, dispatch_ref=ref, dispatch_sha=dispatch, tested_is_ancestor=ancestor, foundation_run=run, foundation_jobs=jobs if jobs is not None else foundation_jobs(run["id"]), expected_run_id=run_id)

    def test_same_commit_passes(self) -> None:
        self.assertEqual(self.gate(dispatch=TESTED_SHA), [])

    def test_tested_ancestor_of_control_plane_passes(self) -> None:
        self.assertEqual(self.gate(dispatch=CONTROL_PLANE_SHA, ancestor=True), [])

    def test_dispatch_ref_must_be_main(self) -> None:
        for ref in ("refs/heads/feature/x", "refs/tags/v1", "main", ""):
            self.assertTrue(any("refs/heads/main" in f for f in self.gate(ref=ref)), ref)

    def test_tested_not_ancestor_fails(self) -> None:
        self.assertTrue(any("not an ancestor" in f for f in self.gate(ancestor=False)))

    def test_unresolvable_ancestry_fails_closed(self) -> None:
        self.assertTrue(any("could not be resolved" in f for f in self.gate(ancestor=None)))

    def test_malformed_shas_fail(self) -> None:
        self.assertTrue(self.gate(tested="abc"))
        self.assertTrue(self.gate(dispatch="abc"))

    def test_foundation_run_must_certify_tested_sha(self) -> None:
        self.assertTrue(any("head_sha" in f for f in self.gate(self.run_json(head_sha=OTHER_SHA))))
        self.assertTrue(any("push run on main" in f for f in self.gate(self.run_json(event="pull_request"))))
        self.assertTrue(any("push run on main" in f for f in self.gate(self.run_json(head_branch="feature/x"))))
        self.assertTrue(any("conclusion" in f for f in self.gate(self.run_json(conclusion="failure"))))
        self.assertTrue(any("conclusion" in f for f in self.gate(self.run_json(status="in_progress", conclusion=None))))
        self.assertTrue(any("Foundation CI" in f for f in self.gate(self.run_json(name="Driver PWA cold probe"))))
        self.assertTrue(any("run id mismatch" in f for f in self.gate(self.run_json(id=1))))

    def test_foundation_job_set_must_be_13_of_13(self) -> None:
        run = self.run_json()
        self.assertTrue(any("exactly 13 jobs" in f for f in self.gate(run, jobs=foundation_jobs(run["id"], count=12))))
        self.assertTrue(any("exactly 13 jobs" in f for f in self.gate(run, jobs=foundation_jobs(run["id"], count=14))))
        self.assertTrue(any("concluded failure" in f for f in self.gate(run, jobs=foundation_jobs(run["id"], first_job={"conclusion": "failure"}))))
        self.assertTrue(any("concluded" in f for f in self.gate(run, jobs=foundation_jobs(run["id"], first_job={"status": "in_progress", "conclusion": None}))))
        self.assertTrue(any("belongs to run" in f for f in self.gate(run, jobs=foundation_jobs(run["id"], first_job={"run_id": 1}))))
        self.assertTrue(any("head_sha" in f for f in self.gate(run, jobs=foundation_jobs(run["id"], first_job={"head_sha": OTHER_SHA}))))
        self.assertTrue(any("not a job list" in f for f in self.gate(run, jobs={"jobs": None})))
        self.assertEqual(self.gate(run, jobs=foundation_jobs(run["id"])["jobs"]), [])

    def test_no_frozen_baseline_concept_remains(self) -> None:
        self.assertFalse(hasattr(guards, "read_frozen_baseline"))
        self.assertFalse(hasattr(guards, "FROZEN_BASELINE_PATTERN"))
        script = (Path(__file__).resolve().parents[2] / "deploy" / "azure" / "deploy-core.ps1").read_text(encoding="utf-8")
        self.assertNotIn("expectedMain", script)
        self.assertNotIn("origin/main", script)


class GitAncestryTests(unittest.TestCase):
    """FIX 4: ancestry comes from `git merge-base --is-ancestor`, never from timestamps."""

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.m, self.n, self.orphan = make_history(self.root)

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def test_equal_and_ancestor_pass(self) -> None:
        self.assertIs(guards.git_is_ancestor(self.root, self.m, self.m), True)
        self.assertIs(guards.git_is_ancestor(self.root, self.m, self.n), True)

    def test_descendant_and_unrelated_fail(self) -> None:
        self.assertIs(guards.git_is_ancestor(self.root, self.n, self.m), False)
        self.assertIs(guards.git_is_ancestor(self.root, self.orphan, self.n), False)

    def test_missing_commit_is_unresolvable(self) -> None:
        self.assertIsNone(guards.git_is_ancestor(self.root, OTHER_SHA, self.n))
        self.assertIsNone(guards.git_is_ancestor(self.root, self.m, OTHER_SHA))

    def test_cli_end_to_end(self) -> None:
        run = {"id": 7, "run_attempt": 1, "name": "Foundation CI", "head_sha": self.m, "event": "push", "head_branch": "main", "status": "completed", "conclusion": "success"}
        run_file = self.root / "run.json"
        jobs_file = self.root / "jobs.json"
        run_file.write_text(json.dumps(run), encoding="utf-8")
        jobs_file.write_text(json.dumps(foundation_jobs(7, head_sha=self.m)), encoding="utf-8")

        def cli(tested: str, dispatch: str, ref: str = "refs/heads/main", *extra: str) -> int:
            return guards.main(["deploy-gate", "--repo-root", str(self.root), "--tested-git-sha", tested, "--dispatch-ref", ref, "--dispatch-sha", dispatch, "--foundation-run-id", "7", "--foundation-run-json", str(run_file), "--foundation-jobs-json", str(jobs_file), *extra])

        self.assertEqual(cli(self.m, self.m), 0, "post-merge: main = M, tested = M")
        self.assertEqual(cli(self.m, self.n), 0, "later control plane N deploying certified M")
        self.assertEqual(cli(self.m, self.n, "refs/heads/feature/x"), 1)
        self.assertEqual(cli(self.n, self.m), 1, "tested descendant of control plane")
        self.assertEqual(cli(self.orphan, self.n), 1, "unrelated tested commit (Foundation head mismatch too)")
        self.assertEqual(cli(OTHER_SHA, self.n), 1, "missing tested commit fails closed")
        self.assertEqual(cli(self.m, self.m, "refs/heads/main", "--image-digest", DIGEST), 0)
        self.assertEqual(cli(self.m, self.m, "refs/heads/main", "--image-digest", "x.azurecr.io/db-ops:latest"), 1)
        self.assertEqual(guards.main(["deploy-gate", "--repo-root", str(self.root), "--tested-git-sha", self.m, "--dispatch-ref", "refs/heads/main", "--dispatch-sha", self.m, "--foundation-run-json", str(run_file), "--foundation-jobs-json", str(self.root / "missing.json")]), 2)


class DeployCoreProvenanceTests(unittest.TestCase):
    """FIX 2: deploy-core.ps1 stops before any `az` call unless HEAD == TestedGitSha."""

    SCRIPT = Path(__file__).resolve().parents[2] / "deploy" / "azure" / "deploy-core.ps1"

    def setUp(self) -> None:
        self.shell = shutil.which("pwsh") or shutil.which("powershell")
        if self.shell is None:
            self.skipTest("no PowerShell host available")
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / "deploy" / "azure").mkdir(parents=True)
        shutil.copy(self.SCRIPT, self.root / "deploy" / "azure" / "deploy-core.ps1")
        git(self.root, "init", "-q", "-b", "main")
        git(self.root, "add", ".")
        git(self.root, "commit", "-q", "-m", "M")
        self.head = git(self.root, "rev-parse", "HEAD")
        # Stub `az`: records that it was reached, then fails so nothing beyond the first call runs.
        self.stub_dir = self.root / "stub"
        self.stub_dir.mkdir()
        self.marker = self.root / "az-was-called"
        if os.name == "nt":
            (self.stub_dir / "az.cmd").write_text("@echo off\r\necho called> \"%AZ_MARKER%\"\r\nexit /b 1\r\n", encoding="ascii")
        else:
            stub = self.stub_dir / "az"
            stub.write_text("#!/bin/sh\necho called > \"$AZ_MARKER\"\nexit 1\n", encoding="ascii")
            stub.chmod(0o755)

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def run_script(self, tested: str) -> subprocess.CompletedProcess:
        env = dict(os.environ, PATH=str(self.stub_dir) + os.pathsep + os.environ.get("PATH", ""), AZ_MARKER=str(self.marker))
        return subprocess.run([self.shell, "-NoProfile", "-NonInteractive", "-File", str(self.root / "deploy" / "azure" / "deploy-core.ps1"), "-SubscriptionId", "00000000-0000-0000-0000-000000000000", "-TestedGitSha", tested], capture_output=True, text=True, env=env, cwd=str(self.root))

    def test_head_equals_tested_sha_reaches_next_stage(self) -> None:
        result = self.run_script(self.head)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(f"deployed_git_sha={self.head}", result.stdout)
        self.assertNotIn("STOP_FOR_CONTRACT_REVIEW", result.stdout + result.stderr)
        self.assertTrue(self.marker.exists(), "provenance passed, so the next stage (stub az) must have been reached")

    def test_head_differs_from_tested_sha_stops_before_azure(self) -> None:
        result = self.run_script(OTHER_SHA)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("checked-out deployment source does not equal tested_git_sha", result.stdout + result.stderr)
        self.assertFalse(self.marker.exists(), "az must never be reached when provenance fails")

    def test_malformed_tested_sha_stops_before_azure(self) -> None:
        result = self.run_script("HEAD")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("STOP_FOR_CONTRACT_REVIEW", result.stdout + result.stderr)
        self.assertFalse(self.marker.exists())


if __name__ == "__main__":
    unittest.main()
