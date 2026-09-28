"""Tests for the ENV-001 pilot guards (tools/azr-001/env001_pilot_guards.py).

Pure tests (SCRAM known answer, settings validation) always run. The template tests compile the real
`deploy/azure/pilot/*.bicep` with the pinned Bicep CLI when it is available (`BICEP_BIN`, or
`$RUNNER_TEMP/bicep` as installed by the Foundation/PR Validation azr-static steps) and then prove
that each owner decision is enforced by mutating the compiled ARM output.
"""

from __future__ import annotations

import base64
import copy
import hashlib
import hmac
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import env001_pilot_guards as guards  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[2]


def _bicep() -> str | None:
    candidates = [os.environ.get("BICEP_BIN"), os.path.join(os.environ.get("RUNNER_TEMP", ""), "bicep"), shutil.which("bicep")]
    return next((c for c in candidates if c and os.path.isfile(c) and os.access(c, os.X_OK)), None)


class ScramVerifierTests(unittest.TestCase):
    def test_rfc7677_known_answer(self):
        # RFC 7677 section 3: user "user", password "pencil".
        verifier = guards.scram_sha256_verifier("pencil", salt=base64.b64decode("W22ZaJ0SNY7soEsUEjb6gQ=="))
        self.assertEqual(
            "SCRAM-SHA-256$4096:W22ZaJ0SNY7soEsUEjb6gQ==$WG5d8oPm3OtcPnkdi4Uo7BkeZkBFzpcXkuLmtbsT4qY=:"
            "wfPLwcE6nTWhTAmQ7tl2KeoiWGPlZqQxSrmfPwDl2dU=",
            verifier,
        )
        stored, server = (base64.b64decode(k) for k in verifier.rsplit("$", 1)[1].split(":"))
        auth_message = (
            b"n=user,r=rOprNGfwEbeRWgbNEkqO,"
            b"r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096,"
            b"c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0"
        )
        self.assertEqual(b"6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=", base64.b64encode(hmac.new(server, auth_message, hashlib.sha256).digest()))
        proof = base64.b64decode("dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=")
        client_key = bytes(a ^ b for a, b in zip(proof, hmac.new(stored, auth_message, hashlib.sha256).digest()))
        self.assertEqual(stored, hashlib.sha256(client_key).digest())

    def test_random_salt_and_migrator_format(self):
        first = guards.scram_sha256_verifier("x" * 64)
        second = guards.scram_sha256_verifier("x" * 64)
        self.assertNotEqual(first, second)
        pattern = r"^SCRAM-SHA-256\$4096:[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}=$"
        self.assertRegex(first, pattern)

    def test_cli_rejects_short_passwords(self):
        result = subprocess.run([sys.executable, guards.__file__, "scram-verifier"], input="short", capture_output=True, text=True)
        self.assertEqual(2, result.returncode)
        self.assertEqual("", result.stdout)


class SettingsTests(unittest.TestCase):
    def test_repository_settings_are_well_formed_but_await_the_owner(self):
        entries = guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE)
        self.assertEqual([], guards.validate_settings(entries, allow_sentinel=True))
        self.assertTrue(any(guards.OWNER_SENTINEL in f for f in guards.validate_settings(entries, allow_sentinel=False)))

    def test_platform_managed_mock_and_secret_settings_are_rejected(self):
        cases = {
            "Authentication__Provider": "AuthCenter",
            "ConnectionStrings__Paqueteria": "Host=x",
            "Locations__PiiProtector": "AzureKeyVault",
            "Realtime__Backplane": "Redis",
            "Pricing__ApiKey": "abc",
            "Pricing__Mode": "Mock",
        }
        for name, value in cases.items():
            with self.subTest(name=name):
                self.assertTrue(guards.validate_settings([{"name": name, "value": value}], allow_sentinel=False))
        self.assertTrue(guards.validate_settings([{"name": "Pricing__PricingPolicyVersion", "value": "v1", "extra": 1}], allow_sentinel=False))
        self.assertTrue(guards.validate_settings([{"name": "A__B", "value": "1"}, {"name": "A__B", "value": "2"}], allow_sentinel=False))
        self.assertEqual([], guards.validate_settings([{"name": "Pricing__PricingPolicyVersion", "value": "PRC-PILOT-v1"}], allow_sentinel=False))


@unittest.skipUnless(_bicep(), "pinned Bicep CLI not available (set BICEP_BIN); the azr-static CI job installs it")
class TemplateGuardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory(prefix="env001-guards-")
        cls.arm = Path(cls.tmp.name) / "arm"
        cls.arm.mkdir()
        for template in sorted((REPO_ROOT / guards.PILOT_DIR).glob("*.bicep")):
            subprocess.run([_bicep(), "build", str(template), "--outdir", str(cls.arm)], check=True, capture_output=True)
        cls.compiled = {p.stem: json.loads(p.read_text(encoding="utf-8")) for p in cls.arm.glob("*.json")}

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def context(self, mutate=None, workflow_text=None) -> guards.Context:
        ctx = guards.load_context(REPO_ROOT, self.arm)
        ctx.templates = copy.deepcopy(self.compiled)
        if mutate:
            mutate(ctx.templates)
        if workflow_text is not None:
            ctx.workflow_text = workflow_text
            ctx.workflow = guards.yaml.safe_load(workflow_text)
            if True in ctx.workflow:
                ctx.workflow["on"] = ctx.workflow.pop(True)
        return ctx

    def results(self, ctx: guards.Context) -> dict[int, guards.GuardResult]:
        return {r.number: r for r in guards.run_guards(ctx)}

    def assert_fails(self, number: int, ctx: guards.Context, fragment: str):
        results = guards.run_guards(ctx)
        failing = [r for r in results if not r.passed]
        self.assertTrue(any(r.number == number and fragment in r.detail for r in failing), [(r.number, r.status, r.detail) for r in failing])

    @staticmethod
    def resource(templates, template: str, resource_type: str, name_fragment: str = ""):
        raw = templates[template]["resources"]
        items = raw.values() if isinstance(raw, dict) else raw
        return next(r for r in items if r.get("type") == resource_type and name_fragment in str(r.get("name")) and not r.get("existing"))

    def test_repository_templates_pass_every_guard(self):
        results = guards.run_guards(self.context())
        self.assertEqual([], [(r.number, r.status, r.detail) for r in results if not r.passed])
        self.assertEqual(len(guards.GUARDS), len(results))

    def test_public_postgres_fails(self):
        def mutate(t):
            self.resource(t, "platform", "Microsoft.DBforPostgreSQL/flexibleServers")["properties"]["network"]["publicNetworkAccess"] = "Enabled"
        self.assert_fails(11, self.context(mutate), "private access")

    def test_second_api_replica_fails(self):
        def mutate(t):
            self.resource(t, "apps", "Microsoft.App/containerApps", "ca-pv-pilot-api")["properties"]["template"]["scale"]["maxReplicas"] = 2
        self.assert_fails(8, self.context(mutate), "exactly one replica")

    def test_route_drift_fails(self):
        def drop_hubs(t):
            rules = self.resource(t, "apps", "Microsoft.App/managedEnvironments/httpRouteConfigs")["properties"]["rules"]
            rules[0]["routes"] = [r for r in rules[0]["routes"] if r["match"].get("pathSeparatedPrefix") != "/hubs"]
        self.assert_fails(10, self.context(drop_hubs), "API rule must match")

        def web_first(t):
            rules = self.resource(t, "apps", "Microsoft.App/managedEnvironments/httpRouteConfigs")["properties"]["rules"]
            rules.reverse()
        self.assert_fails(10, self.context(web_first), "first rule must target the API")

        def external_api(t):
            self.resource(t, "apps", "Microsoft.App/containerApps", "ca-pv-pilot-api")["properties"]["configuration"]["ingress"]["external"] = True
        self.assert_fails(10, self.context(external_api), "must be internal")

    def test_mock_or_synthetic_value_fails(self):
        def mutate(t):
            t["apps"]["variables"]["productionEnv"].append({"name": "Notifications__Channel", "value": "Mock"})
        self.assert_fails(7, self.context(mutate), "Mock")

    def test_inline_or_foreign_secret_fails(self):
        def inline(t):
            api = self.resource(t, "apps", "Microsoft.App/containerApps", "ca-pv-pilot-api")
            api["properties"]["configuration"]["secrets"].append({"name": "x", "value": "plaintext"})
        self.assert_fails(6, self.context(inline), "Key Vault reference")

        def admin_secret(t):
            api = self.resource(t, "apps", "Microsoft.App/containerApps", "ca-pv-pilot-api")
            secret = copy.deepcopy(api["properties"]["configuration"]["secrets"][0])
            secret["name"] = "admin"
            secret["keyVaultUrl"] = secret["keyVaultUrl"].replace("pg-api-runtime-connection", "pg-migrate-connection")
            api["properties"]["configuration"]["secrets"].append(secret)
        self.assert_fails(6, self.context(admin_secret), "pg-migrate-connection")

    def test_storage_and_defender_fail_closed(self):
        def shared_key(t):
            self.resource(t, "platform", "Microsoft.Storage/storageAccounts")["properties"]["allowSharedKeyAccess"] = True
        self.assert_fails(12, self.context(shared_key), "allowSharedKeyAccess")

        def no_scan(t):
            self.resource(t, "platform", "Microsoft.Security/defenderForStorageSettings")["properties"]["malwareScanning"]["onUpload"]["isEnabled"] = False
        self.assert_fails(12, self.context(no_scan), "malware scanning")

    def test_forbidden_resources_and_roles_fail(self):
        def redis(t):
            t["platform"]["resources"].append({"type": "Microsoft.Cache/redis", "apiVersion": "2024-03-01", "name": "r"})
        self.assert_fails(1, self.context(redis), "Microsoft.Cache/redis")

        def owner(t):
            assignment = copy.deepcopy(self.resource(t, "platform", "Microsoft.Authorization/roleAssignments"))
            assignment["properties"]["roleDefinitionId"] = "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8e3af657-a8ff-443c-a75c-2fe8c4bcb635')]"
            t["platform"]["resources"].append(assignment)
        self.assert_fails(14, self.context(owner), "allowlist")

    def test_migration_job_must_use_the_bridge_and_apps_never_migrate(self):
        def no_bridge(t):
            job = self.resource(t, "jobs", "Microsoft.App/jobs", "job-pv-pilot-migrate")
            job["properties"]["template"]["containers"][0]["args"].remove("--azure-ownership-bridge")
        self.assert_fails(9, self.context(no_bridge), "--azure-ownership-bridge")

        def startup_migration(t):
            api = self.resource(t, "apps", "Microsoft.App/containerApps", "ca-pv-pilot-api")
            api["properties"]["template"]["containers"][0]["command"] = ["dotnet", "/app/migrator/Paqueteria.DatabaseMigrator.dll"]
        self.assert_fails(9, self.context(startup_migration), "runs the migrator at startup")

    def test_budget_and_log_cap_are_required(self):
        def no_cap(t):
            del self.resource(t, "platform", "Microsoft.OperationalInsights/workspaces")["properties"]["workspaceCapping"]
        self.assert_fails(15, self.context(no_cap), "daily ingestion cap")

        def raise_budget(t):
            t["platform"]["parameters"]["budgetAmount"]["maxValue"] = 500
        self.assert_fails(15, self.context(raise_budget), "100 USD")

    def test_cleanups_must_stay_enabled_with_contract_values(self):
        def disable(t):
            raw = json.dumps(t["apps"])
            raw = raw.replace("createObject('name', 'OutboxRetention__DryRun', 'value', 'false')", "createObject('name', 'OutboxRetention__DryRun', 'value', 'true')")
            t["apps"] = json.loads(raw)
        self.assert_fails(19, self.context(disable), "OutboxRetention__DryRun=false")

        def shorten(t):
            raw = json.dumps(t["apps"])
            raw = raw.replace("createObject('name', 'OutboxRetention__Enabled', 'value', 'true')",
                              "createObject('name', 'OutboxRetention__Enabled', 'value', 'true'), createObject('name', 'OutboxRetention__Business__DeadRetention', 'value', '1.00:00:00')")
            t["apps"] = json.loads(raw)
        self.assert_fails(19, self.context(shorten), "OutboxRetention__Business__DeadRetention")
        self.assertTrue(guards.validate_settings([{"name": "OutboxRetention__DryRun", "value": "true"}], allow_sentinel=False))

    def test_adp_contract_is_wired(self):
        def drop(t):
            t["apps"]["variables"]["proofStorageEnv"] = [e for e in t["apps"]["variables"]["proofStorageEnv"] if e["name"] != "ProofStorage__ThreatScanner"]
        self.assert_fails(16, self.context(drop), "ProofStorage__ThreatScanner")

    def test_workflow_triggers_environment_and_provenance(self):
        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        self.assert_fails(2, self.context(workflow_text=text.replace("on:\n  workflow_dispatch:", "on:\n  push:\n  workflow_dispatch:", 1)), "workflow_dispatch")
        self.assert_fails(3, self.context(workflow_text=text.replace("environment: azure-pilot", "environment: azure-dev", 1)), "azure-pilot")
        self.assert_fails(4, self.context(workflow_text=text.replace("azr001_static_guards.py deploy-gate", "true", 1)), "deploy-gate")
        self.assert_fails(18, self.context(workflow_text=text.replace("deploy/azure/pilot/Dockerfile.web", "deploy/azure/Dockerfile.web")), "pilot/Dockerfile.web")
        pinned = "azure/login@7184910d9eb2b1c5e48f7073824a90609bb9b6d6"
        self.assertIn(pinned, text)
        self.assert_fails(3, self.context(workflow_text=text.replace(pinned, "azure/login@v2", 1)), "pinned")


if __name__ == "__main__":
    unittest.main()
