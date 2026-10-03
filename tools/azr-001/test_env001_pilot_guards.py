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
    def test_repository_settings_are_well_formed_and_await_no_owner_decision(self):
        # POLICY-VERSIONS-PER-ORG-2026-10-02 removed the last two OWNER_DECISION_REQUIRED settings (the
        # global assignment and eligibility policy versions); the sentinel itself is still refused below.
        entries = guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE)
        self.assertEqual([], guards.validate_settings(entries, allow_sentinel=True))
        self.assertEqual([], guards.validate_settings(entries, allow_sentinel=False))
        self.assertTrue(guards.validate_settings([{"name": "Finance__OperationalTimeZone", "value": guards.OWNER_SENTINEL}], allow_sentinel=False))

    def test_platform_managed_mock_and_secret_settings_are_rejected(self):
        cases = {
            "Authentication__Provider": "AuthCenter",
            "ConnectionStrings__Paqueteria": "Host=x",
            "Locations__PiiProtector": "AzureKeyVault",
            "Realtime__Backplane": "Redis",
            "Pricing__ApiKey": "abc",
            "Pricing__Mode": "Mock",
            "Pricing__Engine": "mock",
            "Pricing__Source": " SYNTHETIC ",
        }
        for name, value in cases.items():
            with self.subTest(name=name):
                self.assertTrue(guards.validate_settings([{"name": name, "value": value}], allow_sentinel=False))
        self.assertTrue(guards.validate_settings([{"name": "Finance__OperationalTimeZone", "value": "America/Mazatlan", "extra": 1}], allow_sentinel=False))
        self.assertTrue(guards.validate_settings([{"name": "A__B", "value": "1"}, {"name": "A__B", "value": "2"}], allow_sentinel=False))
        self.assertEqual([], guards.validate_settings([{"name": "Finance__OperationalTimeZone", "value": "America/Mazatlan"}], allow_sentinel=False))
        self.assertTrue(guards.validate_settings([{"name": "Finance__OperationalTimeZone", "value": "owner_decision_required"}], allow_sentinel=False))

    def test_removed_global_pricing_policy_version_is_rejected(self):
        # PRC-POLICY-VERSION-PER-ORG: the version comes from each organization's tariff rules.
        failures = guards.validate_settings([{"name": "Pricing__PricingPolicyVersion", "value": "PRC-PILOT-v1"}], allow_sentinel=False)
        self.assertTrue(any("PRC-POLICY-VERSION-PER-ORG" in failure for failure in failures))
        entries = guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE)
        self.assertNotIn("Pricing__PricingPolicyVersion", {entry["name"] for entry in entries})

    def test_removed_global_assignment_and_eligibility_policy_versions_are_rejected(self):
        # POLICY-VERSIONS-PER-ORG-2026-10-02: each organization versions its own assignment and driver
        # eligibility policies, so neither global setting may come back, with any value.
        entries = guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE)
        for name in ("Dispatch__AssignmentPolicyVersion", "Drivers__Eligibility__PolicyVersion"):
            for value in ("piloto-2026-10-v1", guards.OWNER_SENTINEL):
                with self.subTest(name=name, value=value):
                    failures = guards.validate_settings([{"name": name, "value": value}], allow_sentinel=True)
                    self.assertTrue(any("POLICY-VERSIONS-PER-ORG-2026-10-02" in failure for failure in failures))
            self.assertNotIn(name, {entry["name"] for entry in entries})


class ObservabilityParametersTests(unittest.TestCase):
    """OBS-002: the alert e-mail is an owner value; the file carries nothing else."""

    @staticmethod
    def document(**parameters):
        return {"$schema": "x", "contentVersion": "1.0.0.0", "parameters": {k: {"value": v} for k, v in parameters.items()}}

    def test_repository_parameters_are_well_formed_but_await_the_owner(self):
        document = json.loads((REPO_ROOT / guards.OBSERVABILITY_PARAMETERS_FILE).read_text(encoding="utf-8"))
        self.assertEqual(guards.OWNER_SENTINEL, document["parameters"]["alertEmailAddress"]["value"])
        self.assertEqual([], guards.validate_observability_parameters(document, allow_sentinel=True))
        failures = guards.validate_observability_parameters(document, allow_sentinel=False)
        self.assertTrue(any(guards.OWNER_SENTINEL in f for f in failures), failures)

    def test_a_single_email_and_documented_thresholds_pass(self):
        self.assertEqual([], guards.validate_observability_parameters(
            self.document(alertEmailAddress="ops@paquetenvia.com", outboxLagThresholdSeconds=600), allow_sentinel=False))

    def test_malformed_or_unexpected_parameters_fail(self):
        cases = {
            "not an address": self.document(alertEmailAddress="ops"),
            "two addresses": self.document(alertEmailAddress="a@x.com,b@x.com"),
            "padded": self.document(alertEmailAddress=" ops@x.com"),
            "missing": self.document(outboxLagThresholdSeconds=300),
            "unknown parameter": self.document(alertEmailAddress="ops@x.com", webhookUrl="https://x"),
            "wrong type": self.document(alertEmailAddress="ops@x.com", outboxLagThresholdSeconds="300"),
            "not a parameters file": {"alertEmailAddress": "ops@x.com"},
        }
        for name, document in cases.items():
            with self.subTest(name=name):
                self.assertTrue(guards.validate_observability_parameters(document, allow_sentinel=True))

    def test_cli_stops_for_the_owner_decision(self):
        result = subprocess.run([sys.executable, guards.__file__, "observability-check", "--file",
                                 str(REPO_ROOT / guards.OBSERVABILITY_PARAMETERS_FILE)], capture_output=True, text=True)
        self.assertEqual(1, result.returncode)
        self.assertIn("STOP_FOR_OWNER_DECISION", result.stdout)
        allowed = subprocess.run([sys.executable, guards.__file__, "observability-check", "--allow-owner-sentinel", "--file",
                                  str(REPO_ROOT / guards.OBSERVABILITY_PARAMETERS_FILE)], capture_output=True, text=True)
        self.assertEqual(0, allowed.returncode, allowed.stdout)


SAMPLE_LOG = """# Decision log

| Date | ID | Type | Decision | Impacted files | Approved by |
|---|---|---|---|---|---|
| 2026-09-27 | GATE-007-PRIVACY-DRAFT | Legal process decision | Claude drafts the notice; resolves nothing (GATE-007 stays open) | GATE-007 | Owner |
| 2026-09-27 | PILOT-REAL-PEOPLE | Release decision | Real PII still waits for GATE-007 approval; resolves no gate | ENV-001 | Owner |
| 2026-08-04 | NTF-001-OWNER-001 | Product decision | Mentions GATE-004 and GATE-007 in passing | NTF-001 | Owner |
| 2026-09-28 | GATE-012-PILOT-SCOPE | Gate scoping | Mexico Central only \\| 14-day PITR | GATE-012 | Owner |
| 2026-09-29 | GATE-007-DUPLICATED | Gate resolution | first | GATE-007 | Owner |
| 2026-09-29 | GATE-007-DUPLICATED | Gate resolution | second | GATE-007 | Owner |
| 2026-09-29 | GATE-007-WRONG-TYPE | Gate decision | not a resolution type | GATE-007 | Owner |
| 2026-09-30 | GATE-007-APPROVED | Gate resolution | Approved with an unescaped | pipe later in the text | GATE-007 | Owner |
"""


class GateDecisionTests(unittest.TestCase):
    def check(self, gate, decision_id, text=SAMPLE_LOG):
        return guards.validate_gate_decision(text, gate, decision_id)

    def test_scoping_and_resolution_rows_are_accepted(self):
        self.assertEqual([], self.check("012", "GATE-012-PILOT-SCOPE"))
        self.assertEqual([], self.check("007", "GATE-007-APPROVED"))

    def test_rows_that_do_not_resolve_or_scope_the_gate_are_rejected(self):
        cases = {
            "GATE-007-PRIVACY-DRAFT": "Type 'Legal process decision'",
            "PILOT-REAL-PEOPLE": "not a GATE-007 decision",
            "NTF-001-OWNER-001": "not a GATE-007 decision",
            "": "missing or malformed",
            "GATE-007-UNKNOWN": "not recorded",
            "GATE-007-DUPLICATED": "appears 2 times",
            "GATE-007-WRONG-TYPE": "Type 'Gate decision'",
            "GATE-012-PILOT-SCOPE": "not a GATE-007 decision",
            "gate-007-approved": "missing or malformed",
        }
        for decision_id, fragment in cases.items():
            with self.subTest(decision_id=decision_id):
                failures = self.check("007", decision_id)
                self.assertEqual(1, len(failures), failures)
                self.assertIn(fragment, failures[0])

    def test_escaped_pipes_and_foreign_tables_do_not_shift_columns(self):
        self.assertEqual(["| a", "b |"], [c for c in guards.split_markdown_row("| \\| a | b \\| |")])
        other_table = "| ID | Type |\n|---|---|\n| GATE-012-PILOT-SCOPE | Gate scoping |\n"
        self.assertIn("table not found", self.check("012", "GATE-012-PILOT-SCOPE", other_table)[0])

    def test_real_decision_log_scopes_gate_012_for_the_pilot(self):
        text = (REPO_ROOT / guards.DECISION_LOG).read_text(encoding="utf-8")
        self.assertEqual([], guards.validate_gate_decision(text, "012", "GATE-012-PILOT-SCOPE"))
        self.assertTrue(guards.validate_gate_decision(text, "007", "GATE-007-PRIVACY-DRAFT"))
        self.assertTrue(guards.validate_gate_decision(text, "007", "PILOT-REAL-PEOPLE"))

    def test_cli_exit_codes(self):
        ok = subprocess.run([sys.executable, guards.__file__, "gate-decision", "--gate", "012", "--decision-id", "GATE-012-PILOT-SCOPE",
                             "--decision-log", str(REPO_ROOT / guards.DECISION_LOG)], capture_output=True, text=True)
        self.assertEqual(0, ok.returncode, ok.stdout)
        bad = subprocess.run([sys.executable, guards.__file__, "gate-decision", "--gate", "007", "--decision-id", "GATE-007-PRIVACY-DRAFT",
                              "--decision-log", str(REPO_ROOT / guards.DECISION_LOG)], capture_output=True, text=True)
        self.assertEqual(1, bad.returncode)
        self.assertIn("STOP_FOR_OWNER_DECISION", bad.stdout)


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
        for value in ("Mock", "mock", "Synthetic"):
            def mutate(t, value=value):
                t["apps"]["variables"]["productionEnv"].append({"name": "Notifications__Channel", "value": value})
            with self.subTest(value=value):
                self.assert_fails(7, self.context(mutate), value)

    def test_container_apps_secrets_and_secret_refs_are_forbidden(self):
        def kv_reference(t):
            api = self.resource(t, "apps", "Microsoft.App/containerApps", "ca-pv-pilot-api")
            api["properties"]["configuration"]["secrets"] = [
                {"name": "pg-api-conn", "keyVaultUrl": "https://kv/secrets/pg-api-runtime-connection", "identity": "x"}]
        self.assert_fails(6, self.context(kv_reference), "declares Container Apps secrets")

        def secret_ref(t):
            t["apps"]["variables"]["productionEnv"].append({"name": "Some__Setting", "secretRef": "x"})
        self.assert_fails(6, self.context(secret_ref), "uses secretRef")

        def plain_connection(t):
            t["apps"]["variables"]["productionEnv"].append({"name": "ConnectionStrings__Paqueteria", "value": "Host=x"})
        self.assert_fails(6, self.context(plain_connection), "sensitive setting ConnectionStrings__Paqueteria")

    @staticmethod
    def edit(t, template: str, old: str, new: str, count: int = 1):
        """Edit the compiled ARM text (Bicep inlines env arrays that use reference() into expressions)."""
        raw = json.dumps(t[template])
        assert raw.count(old) >= 1, old
        t[template] = json.loads(raw.replace(old, new, count))

    def test_key_vault_mappings_must_match_least_privilege_and_rbac(self):
        def foreign_secret(t):
            self.edit(t, "apps", "'value', 'paquetenvia-email-lookup-key-1'", "'value', 'pg-migrate-connection'")
        self.assert_fails(6, self.context(foreign_secret), "differ from the least-privilege set")

        def rbac_drift(t):
            t["apps"]["variables"]["apiSecretNames"] = [n for n in t["apps"]["variables"]["apiSecretNames"]
                                                        if n != "paquetenvia-email-lookup-key-1"]
        self.assert_fails(6, self.context(rbac_drift), "apps.apiSecretNames")

        def logins_on_migrate_identity(t):
            t["jobs"]["variables"]["migrateSecretNames"] = list(t["jobs"]["variables"]["loginsSecretNames"])
        self.assert_fails(6, self.context(logins_on_migrate_identity), "jobs.migrateSecretNames")

        def duplicate_key(t):
            self.edit(t, "apps", "'KeyVaultSecrets__Mappings__1__ConfigurationKey', 'value', 'ConnectionStrings:Paqueteria')",
                      "'KeyVaultSecrets__Mappings__1__ConfigurationKey', 'value', 'ConnectionStrings:PaqueteriaWorker')")
        self.assert_fails(6, self.context(duplicate_key), "duplicates")

        def worker_second_secret(t):
            self.edit(t, "apps", "'KeyVaultSecrets__Mappings__1__SecretName', 'value', 'pg-worker-runtime-connection')",
                      "'KeyVaultSecrets__Mappings__1__SecretName', 'value', 'pg-worker-custody-connection')")
        self.assert_fails(6, self.context(worker_second_secret), "differ from the least-privilege set")

        def duplicated_grant(t):
            t["apps"]["variables"]["workerSecretNames"] = ["pg-worker-runtime-connection", "pg-worker-runtime-connection"]
        self.assert_fails(6, self.context(duplicated_grant), "apps.workerSecretNames")

        def no_vault_uri(t):
            raw = json.dumps(t["jobs"])
            start = raw.index("createObject('name', 'KeyVaultSecrets__VaultUri'")
            end = raw.index("createObject('name', 'KeyVaultSecrets__Mappings__0__SecretName'", start)
            t["jobs"] = json.loads(raw[:start] + raw[end:])
        self.assert_fails(6, self.context(no_vault_uri), "without KeyVaultSecrets__VaultUri")

    def test_key_vault_must_deny_public_traffic(self):
        def allow(t):
            self.resource(t, "security", "Microsoft.KeyVault/vaults")["properties"]["networkAcls"]["defaultAction"] = "Allow"
        self.assert_fails(13, self.context(allow), "defaultAction must be Deny")

        def trusted_services(t):
            self.resource(t, "security", "Microsoft.KeyVault/vaults")["properties"]["networkAcls"]["bypass"] = "AzureServices"
        self.assert_fails(13, self.context(trusted_services), "bypass must be None")

        def standing_ip(t):
            self.resource(t, "security", "Microsoft.KeyVault/vaults")["properties"]["networkAcls"]["ipRules"] = [{"value": "1.2.3.4/32"}]
        self.assert_fails(13, self.context(standing_ip), "standing ipRules")

        def no_subnet(t):
            self.resource(t, "security", "Microsoft.KeyVault/vaults")["properties"]["networkAcls"]["virtualNetworkRules"] = []
        self.assert_fails(13, self.context(no_subnet), "Container Apps subnet")

        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        self.assert_fails(13, self.context(workflow_text=text.replace("kv-firewall.sh close", "true")), "temporary Key Vault firewall")

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
        without_yaml = text.replace("      - name: Install guard dependencies\n        run: python -m pip install PyYAML==6.0.3\n\n      # ENV-001 acceptance", "      # ENV-001 acceptance", 1)
        self.assertNotEqual(text, without_yaml)
        self.assert_fails(20, self.context(workflow_text=without_yaml), "job deploy step")
        pinned = "azure/login@7184910d9eb2b1c5e48f7073824a90609bb9b6d6"
        self.assertIn(pinned, text)
        self.assert_fails(3, self.context(workflow_text=text.replace(pinned, "azure/login@v2", 1)), "pinned")

    def test_obs002_alerts_fail_closed(self):
        def rules(t):
            return t["observability"]["variables"]["rules"]

        def default_email(t):
            t["observability"]["parameters"]["alertEmailAddress"]["defaultValue"] = "ops@example.com"
        self.assert_fails(21, self.context(default_email), "without a default")

        def webhook(t):
            group = self.resource(t, "observability", "Microsoft.Insights/actionGroups")
            group["properties"]["webhookReceivers"] = [{"name": "hook", "serviceUri": "https://example.com"}]
        self.assert_fails(21, self.context(webhook), "only by e-mail")

        def one_minute(t):
            rules(t)[0]["frequency"] = "PT1M"
        self.assert_fails(21, self.context(one_minute), "evaluationFrequency PT1M")

        def all_five_minutes(t):
            for rule in rules(t):
                rule["frequency"] = "PT5M"
                rule["window"] = "PT15M"
        self.assert_fails(21, self.context(all_five_minutes), "above the 5.00 USD")

        def window_shorter_than_frequency(t):
            rules(t)[0]["window"] = "PT5M"
        self.assert_fails(21, self.context(window_shorter_than_frequency), "windowSize PT5M")

        def dropped_rule(t):
            t["observability"]["variables"]["rules"] = rules(t)[1:]
        self.assert_fails(21, self.context(dropped_rule), "must be exactly")

        def payload_property(t):
            self.edit(t, "observability", "e.State.Dead", "e.State.PayloadJson")
        self.assert_fails(21, self.context(payload_property), "PayloadJson")

        def foreign_table(t):
            self.edit(t, "observability", "ContainerAppSystemLogs_CL", "AppRequests_CL")
        self.assert_fails(21, self.context(foreign_table), "AppRequests_CL")

        def stateless(t):
            self.resource(t, "observability", "Microsoft.Insights/scheduledQueryRules")["properties"]["autoMitigate"] = False
        self.assert_fails(21, self.context(stateless), "stateful")

        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        self.assert_fails(21, self.context(workflow_text=text.replace("observability-check", "true")), "observability.parameters.json")

    def test_observability_template_is_required(self):
        def drop(t):
            del t["observability"]
        self.assert_fails(0, self.context(drop), "observability")

        def unknown_insights_type(t):
            t["observability"]["resources"].append({"type": "Microsoft.Insights/diagnosticSettings", "apiVersion": "2021-05-01-preview", "name": "d"})
        self.assert_fails(1, self.context(unknown_insights_type), "Microsoft.Insights/diagnosticSettings")


if __name__ == "__main__":
    unittest.main()
