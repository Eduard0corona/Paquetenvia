"""Unit tests for the AZR-001 §28 static guards and §27 deployment gate."""

from __future__ import annotations

import copy
import json
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import azr001_static_guards as guards  # noqa: E402

BASELINE_SHA = "524a5c5735f83707cbdf3d675add31622f409a4f"
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
            run: ./deploy/azure/deploy-core.ps1 -SubscriptionId x
    """
)

FOUNDATION_WORKFLOW = "name: Foundation CI\non:\n  push:\njobs:\n" + "".join(f"  job{i}:\n    runs-on: self-hosted\n" for i in range(13))

DOCKERFILE_WEB = "ARG NEXT_PUBLIC_API_BASE_URL\nARG NEXT_PUBLIC_TRACKING_BRAND_NAME\nARG NEXT_PUBLIC_TRACKING_SUPPORT_URL\nENV NODE_ENV=production\n"
DOCKERFILE_DOTNET = "FROM base\nENTRYPOINT [\"dotnet\", \"Paqueteria.Api.dll\"]\n"
DEPLOY_CORE = f"param()\n$expectedMain = '{BASELINE_SHA}'\n"
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


class DeployGateTests(unittest.TestCase):
    def run_json(self, **overrides) -> dict:
        run = {"id": 35528319093, "name": "Foundation CI", "head_sha": BASELINE_SHA, "event": "push", "head_branch": "main", "status": "completed", "conclusion": "success"}
        run.update(overrides)
        return run

    def gate(self, run: dict, *, tested: str = BASELINE_SHA, dispatch: str = BASELINE_SHA, run_id: str | None = "35528319093") -> list[str]:
        return guards.evaluate_deploy_gate(tested_git_sha=tested, dispatch_sha=dispatch, frozen_baseline=BASELINE_SHA, foundation_run=run, expected_run_id=run_id)

    def test_exact_baseline_passes(self) -> None:
        self.assertEqual(self.gate(self.run_json()), [])

    def test_any_other_sha_stops(self) -> None:
        self.assertTrue(any("frozen AZR-001 baseline" in f for f in self.gate(self.run_json(head_sha=OTHER_SHA), tested=OTHER_SHA, dispatch=OTHER_SHA)))

    def test_dispatch_ref_must_match(self) -> None:
        self.assertTrue(any("dispatched ref" in f for f in self.gate(self.run_json(), dispatch=OTHER_SHA)))

    def test_foundation_run_must_certify_sha(self) -> None:
        self.assertTrue(self.gate(self.run_json(head_sha=OTHER_SHA)))
        self.assertTrue(self.gate(self.run_json(conclusion="failure")))
        self.assertTrue(self.gate(self.run_json(event="pull_request")))
        self.assertTrue(self.gate(self.run_json(head_branch="feature/x")))
        self.assertTrue(self.gate(self.run_json(name="Driver PWA cold probe")))
        self.assertTrue(self.gate(self.run_json(id=1)))

    def test_frozen_baseline_is_read_from_deploy_core(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            script = Path(tmp) / "deploy-core.ps1"
            script.write_text(DEPLOY_CORE, encoding="utf-8")
            self.assertEqual(guards.read_frozen_baseline(script), BASELINE_SHA)
            script.write_text("$expectedMain = (git rev-parse origin/main)\n", encoding="utf-8")
            with self.assertRaises(guards.GuardError):
                guards.read_frozen_baseline(script)

    def test_cli_rejects_non_digest_images(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "deploy" / "azure").mkdir(parents=True)
            (root / "deploy" / "azure" / "deploy-core.ps1").write_text(DEPLOY_CORE, encoding="utf-8")
            run_file = root / "run.json"
            run_file.write_text(json.dumps(self.run_json()), encoding="utf-8")
            base = ["deploy-gate", "--repo-root", str(root), "--tested-git-sha", BASELINE_SHA, "--dispatch-sha", BASELINE_SHA, "--foundation-run-id", "35528319093", "--foundation-run-json", str(run_file)]
            self.assertEqual(guards.main(base + ["--image-digest", DIGEST]), 0)
            self.assertEqual(guards.main(base + ["--image-digest", "x.azurecr.io/db-ops:latest"]), 1)
            self.assertEqual(guards.main(base[:-1] + [str(root / "missing.json")]), 2)


if __name__ == "__main__":
    unittest.main()
