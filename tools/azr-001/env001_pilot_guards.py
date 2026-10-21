#!/usr/bin/env python3
"""ENV-001 pilot (PILOT-REAL-PEOPLE) static guards and deployment helpers.

The pilot is a separate environment from AZR-001 DEV_SYNTHETIC: its templates live in
`deploy/azure/pilot/` (outside the AZR-001 `deploy/azure/*.bicep` glob) and it is deployed by
`.github/workflows/deploy-azure-pilot.yml`. These guards never weaken or replace the AZR-001 §28
guards; they check the pilot's own owner decisions against the compiled ARM output
(`bicep build`) and the workflow. They never contact Azure.

Commands:
  check            evaluate every pilot guard (P00..P26)
  settings-check   validate deploy/azure/pilot/apps.settings.json (fails while an owner value is missing)
  observability-check  validate deploy/azure/pilot/observability.parameters.json (OBS-002 alert e-mail)
  web-check        validate deploy/azure/pilot/web.parameters.json (UI-001 accepted terms/privacy versions)
  scram-verifier   read a password on stdin, print its PostgreSQL SCRAM-SHA-256 verifier
  gate-decision    require a decision-log row that resolves or scopes GATE-007 / GATE-012
  postgres-server-name     validate PILOT_POSTGRES_SERVER_NAME (OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20)
  connection-host          read a connection string on stdin, print only its PostgreSQL host
  postgres-adoption-check  refuse a named PostgreSQL server that platform.bicep cannot update in place
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import re
import secrets
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

import yaml

PILOT_DIR = Path("deploy/azure/pilot")
PILOT_WORKFLOW = ".github/workflows/deploy-azure-pilot.yml"
PILOT_ENVIRONMENT = "azure-pilot"
PILOT_REGION = "mexicocentral"
SETTINGS_FILE = PILOT_DIR / "apps.settings.json"
OBSERVABILITY_PARAMETERS_FILE = PILOT_DIR / "observability.parameters.json"
WEB_PARAMETERS_FILE = PILOT_DIR / "web.parameters.json"
OWNER_SENTINEL = "OWNER_DECISION_REQUIRED"
FORBIDDEN_TRIGGERS = ("push", "pull_request", "pull_request_target", "schedule", "repository_dispatch", "workflow_run")

API_APP = "ca-pv-pilot-api"
WORKER_APP = "ca-pv-pilot-worker"
WEB_APP = "ca-pv-pilot-web"
MIGRATE_JOB = "job-pv-pilot-migrate"
LOGINS_JOB = "job-pv-pilot-logins"
VERIFY_JOB = "job-pv-pilot-verify"
AUTHORIZED_WORKLOADS = {API_APP, WORKER_APP, WEB_APP, MIGRATE_JOB, LOGINS_JOB, VERIFY_JOB}
DOTNET_WORKLOADS = {API_APP, WORKER_APP, MIGRATE_JOB, LOGINS_JOB, VERIFY_JOB}
API_PREFIXES = ("/api", "/hubs", "/auth")
API_EXACT_PATHS = ("/signin-authcenter",)

# OBS-003-TENANT-PSEUDONYM-2026-10-12: the one configured version of the organization pseudonym key. Rotating it is a
# reviewed change of this constant, of apps.bicep (secret names, mappings, TelemetryPseudonym__CurrentKeyVersion) and of
# the has_secret check in the pilot workflow, with their tests (docs/development/obs-003-appinsights-export.md).
TELEMETRY_PSEUDONYM_KEY_VERSION = "1"
TELEMETRY_PSEUDONYM_SECRET = f"paquetenvia-telemetry-pseudonym-key-{TELEMETRY_PSEUDONYM_KEY_VERSION}"
TELEMETRY_PSEUDONYM_CONFIGURATION_KEY = f"TelemetryPseudonym:Keys:{TELEMETRY_PSEUDONYM_KEY_VERSION}"

# Key Vault secrets each workload may reference (least privilege, §17-style classes).
# PILOT-KEYVAULT-PRIVATE-APP-READ: each workload reads exactly these Key Vault secrets itself (ADP-001
# `KeyVaultSecrets__Mappings__<n>__SecretName` -> `__ConfigurationKey`), never through Container Apps.
# ConfigurationKey -> SecretName. One secret may feed several keys (read once); a key appears only once.
REQUIRED_SECRET_MAPPINGS = {
    API_APP: {
        "ConnectionStrings:Paqueteria": "pg-api-runtime-connection",
        "AuthCenter:ClientSecret": "authcenter-paquetenvia-client-secret",
        "EmailLookup:Keys:1": "paquetenvia-email-lookup-key-1",
        # GATE-003-PROVIDER-GOOGLE: owner-written Google Maps Platform key for the Locations adapter.
        "Locations:GoogleMaps:ApiKey": "google-maps-api-key",
        # TRK-002-AUTO-LINK: owner-written HMAC key public tracking links are derived with.
        "PublicTracking:LinkKeys:1": "public-tracking-link-key",
        # RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the in-process realtime dispatchers' own least-privilege login.
        "ConnectionStrings:PaqueteriaRealtime": "pg-realtime-runtime-connection",
        # OBS-003-TENANT-PSEUDONYM-2026-10-12: owner-written HMAC key of the organization pseudonym (logs and traces).
        TELEMETRY_PSEUDONYM_CONFIGURATION_KEY: TELEMETRY_PSEUDONYM_SECRET,
    },
    WORKER_APP: {
        "ConnectionStrings:PaqueteriaWorker": "pg-worker-runtime-connection",
        "ConnectionStrings:Paqueteria": "pg-worker-runtime-connection",
        TELEMETRY_PSEUDONYM_CONFIGURATION_KEY: TELEMETRY_PSEUDONYM_SECRET,
    },
    WEB_APP: {},
    MIGRATE_JOB: {"PAQUETERIA_MIGRATION_CONNECTION": "pg-migrate-connection"},
    LOGINS_JOB: {
        "PAQUETERIA_MIGRATION_CONNECTION": "pg-migrate-connection",
        "PAQUETERIA_API_LOGIN_VERIFIER": "pg-api-login-verifier",
        "PAQUETERIA_WORKER_LOGIN_VERIFIER": "pg-worker-login-verifier",
        "PAQUETERIA_REALTIME_LOGIN_VERIFIER": "pg-realtime-login-verifier",
    },
    VERIFY_JOB: {"PAQUETERIA_MIGRATION_CONNECTION": "pg-migrate-connection"},
}
ALLOWED_SECRETS = {name: set(mapping.values()) for name, mapping in REQUIRED_SECRET_MAPPINGS.items()}
# Template variable holding the secrets that get a per-secret Key Vault Secrets User assignment for the
# identity of each workload; it must equal the workload's mapped secret set.
SECRET_RBAC_VARIABLE = {
    API_APP: ("apps", "apiSecretNames"),
    WORKER_APP: ("apps", "workerSecretNames"),
    MIGRATE_JOB: ("jobs", "migrateSecretNames"),
    VERIFY_JOB: ("jobs", "migrateSecretNames"),
    LOGINS_JOB: ("jobs", "loginsSecretNames"),
}
MAPPING_KEY = re.compile(r"^KeyVaultSecrets__Mappings__(\d+)__(SecretName|ConfigurationKey)$")
SENSITIVE_SETTINGS = re.compile(
    r"^(ConnectionStrings__.*|.*Secret|EmailLookup__Keys__.*|PublicTracking__LinkKeys__.*|TelemetryPseudonym__Keys__.*|PAQUETERIA_MIGRATION_CONNECTION|.*_VERIFIER|.*Password.*|.*ApiKey.*|.*Token)$")

AUTHORIZED_RESOURCE_TYPES = frozenset(
    {
        "Microsoft.ManagedIdentity/userAssignedIdentities",
        "Microsoft.KeyVault/vaults",
        "Microsoft.KeyVault/vaults/keys",
        "Microsoft.Network/virtualNetworks",
        "Microsoft.Network/privateDnsZones",
        "Microsoft.Network/privateDnsZones/virtualNetworkLinks",
        "Microsoft.OperationalInsights/workspaces",
        "Microsoft.ContainerRegistry/registries",
        "Microsoft.Storage/storageAccounts",
        "Microsoft.Storage/storageAccounts/blobServices",
        "Microsoft.Storage/storageAccounts/blobServices/containers",
        "Microsoft.Security/defenderForStorageSettings",
        "Microsoft.DBforPostgreSQL/flexibleServers",
        "Microsoft.DBforPostgreSQL/flexibleServers/databases",
        "Microsoft.DBforPostgreSQL/flexibleServers/configurations",
        "Microsoft.App/managedEnvironments",
        "Microsoft.App/managedEnvironments/httpRouteConfigs",
        "Microsoft.App/managedEnvironments/managedCertificates",
        "Microsoft.App/containerApps",
        "Microsoft.App/jobs",
        "Microsoft.Consumption/budgets",
        "Microsoft.Authorization/roleAssignments",
        # OBS-002 (observability.bicep): log search alerts, one e-mail action group and a workbook.
        "Microsoft.Insights/scheduledQueryRules",
        "Microsoft.Insights/actionGroups",
        "Microsoft.Insights/workbooks",
        # OBS-003 (platform.bicep): one workspace-based Application Insights on the pilot workspace (checked by P21).
        "Microsoft.Insights/components",
        # SCL-007-POSTGRES-CONNECTION-ALERT-2026-10-14 and SCL-007-POSTGRES-RESOURCE-ALERTS-2026-10-20
        # (observability.bicep only, RESOURCE_TYPE_TEMPLATES): the two PostgreSQL connection alerts and the four
        # PostgreSQL resource alerts, pinned by P21; every other metric alert is refused.
        "Microsoft.Insights/metricAlerts",
    }
)
# Authorized types that only one template may declare.
RESOURCE_TYPE_TEMPLATES = {
    "Microsoft.Insights/metricAlerts": "observability",
}
PROHIBITED_TYPE_PREFIXES = (
    "Microsoft.Cache/",  # GATE-013: no Redis
    "Microsoft.SignalRService/",  # GATE-013: no Azure SignalR Service
    "Microsoft.DBforPostgreSQL/flexibleServers/firewallRules",  # private access only
    "Microsoft.Cdn/",  # Front Door breaks the budget (see README)
)

# Built-in roles the pilot may assign. Owner, Contributor, User Access Administrator, Storage Blob Data
# Owner and every role that can write blob index tags are excluded on purpose.
ALLOWED_ROLE_IDS = {
    "7f951dda-4ed3-4680-a7ca-43fe172d538d": "AcrPull",
    "ba92f5b4-2d11-453d-a403-e96b0029c9fe": "Storage Blob Data Contributor",
    "db58b8e5-c6ad-4a2a-8342-4190687cbf4a": "Storage Blob Delegator",
    "4633458b-17de-408a-b874-0445c86b69e6": "Key Vault Secrets User",
    "b86a8fe4-44ce-4948-aee5-eccb2c155cd7": "Key Vault Secrets Officer",
    "e147488a-f6f5-4113-8e2d-b22465e65bf6": "Key Vault Crypto Service Encryption User",
}

# Values that must never reach a pilot workload.
FORBIDDEN_ENV_VALUES = frozenset({"mock", "synthetic", "devsynthetic", "dev_synthetic", "development", "testing", "s3compatible", "memory"})


def is_forbidden_value(value: str) -> bool:
    """Case-insensitive: .NET binds enum settings ignoring case, so `mock` selects the Mock provider."""
    return value.strip().casefold() in FORBIDDEN_ENV_VALUES


def is_owner_sentinel(value: str) -> bool:
    return value.strip().casefold() == OWNER_SENTINEL.casefold()
PLATFORM_MANAGED_PREFIXES = (
    "ASPNETCORE_",
    "DOTNET_",
    "PAQUETERIA_",
    "AZURE_",
    "ConnectionStrings__",
    "Authentication__",
    "AuthCenter__",
    "EmailLookup__",
    "IdentityBootstrap__",
    "Tenancy__",
    "Realtime__",
    "Http__",
    "DataProtection__",
    "ProofStorage__",
    "PiiProtection__",
    "Locations__",
    "Incidents__",
    "OutboxRetention__",
    "OperationalCleanup__",
    # VOICE-001-PROVIDER-TWILIO-2026-10-11: the masked call bridge stays Disabled in the pilot until the owner
    # delivers the Twilio account, number and Key Vault secrets and GATE-007 is resolved; enabling it is an
    # apps.bicep change under review, never a free-form setting.
    "Voice__",
    # OBS-003: the export switch, its connection string and the pseudonym key version are reviewed apps.bicep values
    # (P21); apps.settings.json can neither turn the export on nor send telemetry elsewhere.
    "TelemetryExport__",
    "TelemetryPseudonym__",
    "Urls",
)
# Settings that no longer exist. The API refuses to start while one is configured, so the pilot refuses
# them before deploying. PRC-POLICY-VERSION-PER-ORG: each organization versions its own pricing policy on
# its tariff rules; there is no global pricing policy version. POLICY-VERSIONS-PER-ORG-2026-10-02: each
# organization versions its own assignment and driver eligibility policies; there is no global version.
REMOVED_SETTINGS = {
    "Pricing__PricingPolicyVersion": "PRC-POLICY-VERSION-PER-ORG (the version comes from each organization's tariff rules)",
    "Dispatch__AssignmentPolicyVersion": "POLICY-VERSIONS-PER-ORG-2026-10-02 (the version comes from each organization)",
    "Drivers__Eligibility__PolicyVersion": "POLICY-VERSIONS-PER-ORG-2026-10-02 (the version comes from the driver's organization)",
}
SETTING_NAME = re.compile(r"^[A-Za-z][A-Za-z0-9]*(__[A-Za-z0-9]+)+$")
DIGEST_REGEX_LITERAL = r"^sha256:[0-9a-f]{64}$"
USES_PINNED = re.compile(r"^[^@\s]+@[0-9a-f]{40}$")

CREATE_OBJECT = re.compile(r"createObject\('name', '([^']+)', '(value|secretRef)', ('([^']*)'|[^)]*\)+)")
VARIABLE_REF = re.compile(r"variables\('([A-Za-z0-9_]+)'\)")
SECRET_NAME = re.compile(r"secrets/([A-Za-z0-9-]+)")


class GuardError(Exception):
    """A guard could not be evaluated (BLOCKED, never PASS)."""


@dataclass
class GuardResult:
    number: int
    title: str
    status: str
    detail: str = ""

    @property
    def passed(self) -> bool:
        return self.status == "PASS"


@dataclass
class Workload:
    template: str
    resource_type: str
    name: str
    resource: dict[str, Any]
    env: dict[str, tuple[str, str]] = field(default_factory=dict)
    env_includes_business_settings: bool = False

    @property
    def properties(self) -> dict[str, Any]:
        return self.resource.get("properties", {}) or {}

    @property
    def configuration(self) -> dict[str, Any]:
        value = self.properties.get("configuration", {})
        if not isinstance(value, dict):
            raise GuardError(f"{self.name}: configuration is an expression; keep it literal so it can be verified")
        return value

    @property
    def containers(self) -> list[dict[str, Any]]:
        return [c for c in ((self.properties.get("template", {}) or {}).get("containers", []) or []) if isinstance(c, dict)]

    @property
    def secret_names(self) -> list[str]:
        names = []
        for secret in self.configuration.get("secrets", []) or []:
            match = SECRET_NAME.search(str(secret.get("keyVaultUrl", "")))
            names.append(match.group(1) if match else f"inline:{secret.get('name')}")
        return names

    def value(self, key: str) -> str | None:
        entry = self.env.get(key)
        return None if entry is None or entry[0] != "value" else entry[1]


@dataclass
class Context:
    repo_root: Path
    arm_dir: Path
    templates: dict[str, dict[str, Any]] = field(default_factory=dict)
    workflow: dict[str, Any] = field(default_factory=dict)
    workflow_text: str = ""

    def resources(self, resource_type: str | None = None) -> list[tuple[str, dict[str, Any]]]:
        found = []
        for name, document in self.templates.items():
            raw = document.get("resources", [])
            items = raw.values() if isinstance(raw, dict) else raw
            for resource in items:
                if not isinstance(resource, dict) or resource.get("existing"):
                    continue
                if resource_type is None or resource.get("type") == resource_type:
                    found.append((name, resource))
        return found

    @property
    def workloads(self) -> list[Workload]:
        found = []
        for template, resource in self.resources():
            if resource.get("type") not in ("Microsoft.App/containerApps", "Microsoft.App/jobs"):
                continue
            workload = Workload(template, resource["type"], str(resource.get("name", "")), resource)
            variables = self.templates[template].get("variables", {}) or {}
            for container in workload.containers:
                _collect_env(container.get("env", []), variables, workload, depth=0)
            found.append(workload)
        return found

    def workload(self, name: str) -> Workload:
        matches = [w for w in self.workloads if w.name == name]
        if len(matches) != 1:
            raise GuardError(f"expected exactly one workload {name}, found {len(matches)}")
        return matches[0]


def _collect_env(raw: Any, variables: dict[str, Any], workload: Workload, depth: int) -> None:
    """Resolve a container `env` (literal list or ARM concat expression) into name -> (kind, value)."""
    if depth > 8:
        raise GuardError(f"{workload.name}: env variable nesting too deep")
    if isinstance(raw, list):
        for entry in raw:
            if isinstance(entry, dict) and "name" in entry:
                kind = "secretRef" if "secretRef" in entry else "value"
                _add_env(workload, str(entry["name"]), kind, str(entry.get(kind, "")))
        return
    if not isinstance(raw, str):
        if depth == 0:
            raise GuardError(f"{workload.name}: unsupported env shape {type(raw).__name__}")
        return
    if "parameters('businessSettings')" in raw:
        workload.env_includes_business_settings = True
    for reference in VARIABLE_REF.findall(raw):
        if reference not in variables:
            raise GuardError(f"{workload.name}: env references unknown variable {reference}")
        _collect_env(variables[reference], variables, workload, depth + 1)
    for name, kind, whole, literal in CREATE_OBJECT.findall(raw):
        _add_env(workload, name, kind, literal if whole.startswith("'") else "<expression>")


def _add_env(workload: Workload, name: str, kind: str, value: str) -> None:
    if name in workload.env and workload.env[name] != (kind, value):
        raise GuardError(f"{workload.name}: env {name} declared twice with different values")
    workload.env[name] = (kind, value)


def load_yaml(path: Path) -> dict[str, Any]:
    document = yaml.safe_load(path.read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        raise GuardError(f"{path} is not a YAML mapping")
    if True in document and "on" not in document:
        document["on"] = document.pop(True)
    return document


def load_context(repo_root: Path, arm_dir: Path) -> Context:
    ctx = Context(repo_root=repo_root, arm_dir=arm_dir)
    templates = sorted((repo_root / PILOT_DIR).glob("*.bicep"))
    if not templates:
        raise GuardError(f"no {PILOT_DIR}/*.bicep templates found")
    for bicep in templates:
        compiled = arm_dir / f"{bicep.stem}.json"
        if not compiled.is_file():
            raise GuardError(f"missing compiled ARM output for {bicep.name}: {compiled}")
        ctx.templates[bicep.stem] = json.loads(compiled.read_text(encoding="utf-8"))
    workflow = repo_root / PILOT_WORKFLOW
    if not workflow.is_file():
        raise GuardError(f"missing {PILOT_WORKFLOW}")
    ctx.workflow_text = workflow.read_text(encoding="utf-8")
    ctx.workflow = load_yaml(workflow)
    return ctx


def _result(number: int, title: str, failures: list[str], ok: str) -> GuardResult:
    return GuardResult(number, title, "FAIL", "; ".join(failures)) if failures else GuardResult(number, title, "PASS", ok)


# ---------------------------------------------------------------------------------------------- guards
def guard_00_templates(ctx: Context) -> GuardResult:
    expected = {"security", "platform", "jobs", "apps", "observability"}
    failures = [] if set(ctx.templates) == expected else [f"pilot templates must be exactly {sorted(expected)}, found {sorted(ctx.templates)}"]
    return _result(0, "pilot templates compiled", failures, f"{len(ctx.templates)} templates")


def guard_01_resource_set(ctx: Context) -> GuardResult:
    failures = []
    for template, resource in ctx.resources():
        resource_type = str(resource.get("type", ""))
        if resource_type not in AUTHORIZED_RESOURCE_TYPES:
            failures.append(f"{template} declares unauthorized {resource_type}")
        elif RESOURCE_TYPE_TEMPLATES.get(resource_type, template) != template:
            failures.append(f"{template} declares {resource_type}, allowed only in {RESOURCE_TYPE_TEMPLATES[resource_type]}.bicep")
        if any(resource_type.startswith(prefix) for prefix in PROHIBITED_TYPE_PREFIXES):
            failures.append(f"{template} declares prohibited {resource_type}")
    names = sorted(w.name for w in ctx.workloads)
    if set(names) != AUTHORIZED_WORKLOADS or len(names) != len(set(names)):
        failures.append(f"workloads must be exactly {sorted(AUTHORIZED_WORKLOADS)}, found {names}")
    return _result(1, "authorized resource and workload set", failures, f"{len(ctx.resources())} resources, {len(names)} workloads")


def guard_02_dispatch_only(ctx: Context) -> GuardResult:
    failures = []
    triggers = ctx.workflow.get("on")
    keys = set(triggers) if isinstance(triggers, (dict, list)) else ({triggers} if isinstance(triggers, str) else set())
    if keys != {"workflow_dispatch"}:
        failures.append(f"triggers must be exactly workflow_dispatch, found {sorted(map(str, keys))}")
    for forbidden in FORBIDDEN_TRIGGERS:
        if re.search(rf"^\s+{forbidden}:", ctx.workflow_text, re.MULTILINE):
            failures.append(f"forbidden trigger key {forbidden}")
    if ctx.workflow.get("permissions") != {}:
        failures.append("top-level permissions must be {}")
    return _result(2, "pilot workflow is workflow_dispatch only", failures, "single manual trigger, no default permissions")


def guard_03_environment(ctx: Context) -> GuardResult:
    failures = []
    concurrency = ctx.workflow.get("concurrency")
    if not isinstance(concurrency, dict) or concurrency.get("group") != PILOT_ENVIRONMENT or concurrency.get("cancel-in-progress") is not False:
        failures.append(f"concurrency must be group={PILOT_ENVIRONMENT} with cancel-in-progress=false")
    azure_jobs = 0
    for name, job in (ctx.workflow.get("jobs", {}) or {}).items():
        steps = [s for s in job.get("steps", []) or [] if isinstance(s, dict)]
        permissions = job.get("permissions", {}) or {}
        touches_azure = permissions.get("id-token") == "write" or any(
            "azure/login" in str(s.get("uses", "")) or re.search(r"(^|\s)az\s", str(s.get("run", ""))) for s in steps
        )
        if touches_azure:
            azure_jobs += 1
            environment = job.get("environment")
            env_name = environment.get("name") if isinstance(environment, dict) else environment
            if env_name != PILOT_ENVIRONMENT:
                failures.append(f"job {name} touches Azure without environment {PILOT_ENVIRONMENT}")
        for step in steps:
            uses = str(step.get("uses", ""))
            if uses and not uses.startswith(("actions/", "./")) and not USES_PINNED.match(uses):
                failures.append(f"job {name}: third-party action not pinned by commit SHA: {uses}")
            if "${{" in str(step.get("run", "")):
                failures.append(f"job {name}: step '{step.get('name')}' interpolates an expression inside run")
        if "timeout-minutes" not in job:
            failures.append(f"job {name} declares no timeout-minutes")
    if azure_jobs == 0:
        failures.append("workflow declares no Azure job")
    if "azure-dev" in ctx.workflow_text:
        failures.append("pilot workflow must not reference the DEV_SYNTHETIC azure-dev environment")
    if f"PILOT_REGION: {PILOT_REGION}" not in ctx.workflow_text:
        failures.append(f"workflow must pin PILOT_REGION: {PILOT_REGION}")
    return _result(3, "azure-pilot environment, OIDC and hygiene", failures, f"{azure_jobs} Azure job(s) bound to {PILOT_ENVIRONMENT}")


def guard_04_provenance(ctx: Context) -> GuardResult:
    failures = []
    for needle in ("azr001_static_guards.py deploy-gate", "--foundation-jobs-json", "^[0-9a-f]{40}$", "^[0-9]+$", DIGEST_REGEX_LITERAL,
                   "PILOT_GATE_007_DECISION", "PILOT_GATE_012_DECISION",
                   "gate-decision --gate 007", "gate-decision --gate 012"):
        if needle not in ctx.workflow_text:
            failures.append(f"workflow lacks provenance/digest control: {needle}")
    for name, job in (ctx.workflow.get("jobs", {}) or {}).items():
        needs = job.get("needs") or []
        needs = [needs] if isinstance(needs, str) else list(needs)
        if (job.get("permissions", {}) or {}).get("id-token") == "write" and "provenance" not in needs:
            failures.append(f"Azure job {name} does not depend on the provenance job")
    if re.search(r":latest\b", ctx.workflow_text):
        failures.append("workflow references a :latest tag")
    return _result(4, "13/13 provenance gate and digest-only images", failures, "deploy-gate + sha256 digests enforced")


def guard_05_images(ctx: Context) -> GuardResult:
    failures = []
    for workload in ctx.workloads:
        for container in workload.containers:
            image = str(container.get("image", ""))
            if not image.startswith("[parameters(") and "@sha256:" not in image:
                failures.append(f"{workload.name} image is neither a parameter nor digest-pinned: {image}")
        registries = workload.configuration.get("registries", []) or []
        if not registries or any(not r.get("identity") or r.get("username") or r.get("passwordSecretRef") for r in registries):
            failures.append(f"{workload.name} must pull with its managed identity only")
        if (workload.resource.get("identity", {}) or {}).get("type") != "UserAssigned":
            failures.append(f"{workload.name} must use a user-assigned identity")
    for _, registry in ctx.resources("Microsoft.ContainerRegistry/registries"):
        properties = registry.get("properties", {}) or {}
        if properties.get("adminUserEnabled") is not False or properties.get("anonymousPullEnabled") is True:
            failures.append("registry must disable the admin user and anonymous pull")
    return _result(5, "managed-identity pulls of immutable images", failures, "no registry credentials, digests only")


def secret_mappings(workload: Workload) -> dict[str, str] | str:
    """`ConfigurationKey -> SecretName` from the workload's ADP-001 mapping env, or an error text.
    A secret may feed several configuration keys; a configuration key may appear only once."""
    indexed: dict[int, dict[str, str]] = {}
    for key, (kind, value) in workload.env.items():
        match = MAPPING_KEY.match(key)
        if not match:
            continue
        if kind != "value" or value == "<expression>":
            return f"{key} must be a literal value"
        indexed.setdefault(int(match.group(1)), {})[match.group(2)] = value
    if sorted(indexed) != list(range(len(indexed))):
        return f"mapping indexes must be 0..n-1, found {sorted(indexed)}"
    mappings: dict[str, str] = {}
    for index in sorted(indexed):
        pair = indexed[index]
        if set(pair) != {"SecretName", "ConfigurationKey"}:
            return f"mapping {index} must have SecretName and ConfigurationKey"
        if pair["ConfigurationKey"] in mappings:
            return f"mapping {index} duplicates configuration key {pair['ConfigurationKey']}"
        mappings[pair["ConfigurationKey"]] = pair["SecretName"]
    return mappings


def _copy_count_variable(resource: dict[str, Any]) -> str | None:
    count = str(((resource.get("copy") or {}).get("count")) or "")
    match = re.fullmatch(r"\[length\(variables\('([A-Za-z0-9_]+)'\)\)\]", count)
    return match.group(1) if match else None


def guard_06_secrets(ctx: Context) -> GuardResult:
    """PILOT-KEYVAULT-PRIVATE-APP-READ: no Container Apps secrets at all; each workload reads exactly its
    mapped Key Vault secrets itself, and its identity holds Key Vault Secrets User on exactly those secrets."""
    failures = []
    reader_role = "4633458b-17de-408a-b874-0445c86b69e6"
    for workload in ctx.workloads:
        if workload.configuration.get("secrets"):
            failures.append(f"{workload.name} declares Container Apps secrets; Key Vault secrets are read by the application")
        for key, (kind, _) in workload.env.items():
            if kind == "secretRef":
                failures.append(f"{workload.name} uses secretRef for {key}")
            if SENSITIVE_SETTINGS.match(key):
                failures.append(f"{workload.name} sets sensitive setting {key} in its environment; map it from Key Vault instead")
        expected = REQUIRED_SECRET_MAPPINGS.get(workload.name, {})
        mappings = secret_mappings(workload)
        if isinstance(mappings, str):
            failures.append(f"{workload.name}: {mappings}")
            continue
        if mappings != expected:
            failures.append(f"{workload.name} Key Vault mappings {mappings} differ from the least-privilege set {expected}")
        if expected:
            if workload.env.get("KeyVaultSecrets__VaultUri") is None:
                failures.append(f"{workload.name} maps Key Vault secrets without KeyVaultSecrets__VaultUri")
            if workload.env.get("AZURE_CLIENT_ID") is None:
                failures.append(f"{workload.name} must name its user-assigned identity in AZURE_CLIENT_ID")
            template, variable = SECRET_RBAC_VARIABLE[workload.name]
            granted = ctx.templates[template].get("variables", {}).get(variable)
            if not isinstance(granted, list) or len(granted) != len(set(granted)) or set(granted) != set(expected.values()):
                failures.append(f"{workload.name}: {template}.{variable} (per-secret Key Vault Secrets User) is {granted}, expected {sorted(set(expected.values()))}")
            assignments = [r for _, r in ctx.resources("Microsoft.Authorization/roleAssignments")
                           if _copy_count_variable(r) == variable and reader_role in json.dumps(ctx.templates[template].get("variables", {}))]
            if not assignments:
                failures.append(f"{workload.name}: no per-secret Key Vault Secrets User assignment loops over {variable}")
    return _result(6, "Key Vault secrets read by the application, least privilege", failures,
                   "no Container Apps secrets; mappings match per-secret RBAC")


def guard_07_production_classification(ctx: Context) -> GuardResult:
    failures = []
    for workload in ctx.workloads:
        for key, (kind, value) in workload.env.items():
            if kind == "value" and is_forbidden_value(value):
                failures.append(f"{workload.name} sets {key}={value}")
            if "TESTING" in key.upper():
                failures.append(f"{workload.name} declares testing variable {key}")
        if workload.name in DOTNET_WORKLOADS:
            for key, expected in (("ASPNETCORE_ENVIRONMENT", "Production"), ("DOTNET_ENVIRONMENT", "Production"), ("PAQUETERIA_DEPLOYMENT_CLASS", "PILOT_REAL_PEOPLE")):
                if workload.value(key) != expected:
                    failures.append(f"{workload.name} must set {key}={expected} (found {workload.value(key)})")
    web = ctx.workload(WEB_APP)
    if web.value("NODE_ENV") != "production":
        failures.append("web must set NODE_ENV=production")
    api = ctx.workload(API_APP)
    for key, expected in (("Authentication__Provider", "AuthCenter"), ("AuthCenter__SessionStore", "PostgreSql"), ("DataProtection__Provider", "PostgreSql")):
        if api.value(key) != expected:
            failures.append(f"api must set {key}={expected}")
    return _result(7, "Production + PILOT_REAL_PEOPLE, no mock/synthetic", failures, "all workloads classified for real people")


def guard_08_single_replica_inprocess(ctx: Context) -> GuardResult:
    failures = []
    for name in (API_APP, WORKER_APP):
        scale = ((ctx.workload(name).properties.get("template", {}) or {}).get("scale", {}) or {})
        if scale.get("maxReplicas") != 1 or scale.get("minReplicas") != 1:
            failures.append(f"{name} must run exactly one replica (min=max=1), found {scale}")
    api = ctx.workload(API_APP)
    if api.value("Realtime__Backplane") != "InProcess" or api.value("Realtime__Provider") != "SignalR":
        failures.append("api must use SignalR with the InProcess backplane (GATE-013)")
    if re.search(r"redis|Azure__SignalR", json.dumps(ctx.templates), re.IGNORECASE):
        failures.append("templates reference Redis or Azure SignalR")
    return _result(8, "GATE-013 single API replica, SignalR InProcess", failures, "API and Worker pinned to one replica")


def guard_09_migrations(ctx: Context) -> GuardResult:
    failures = []
    for workload in ctx.workloads:
        invocation = " ".join(str(p) for c in workload.containers for p in (c.get("command", []) or []) + (c.get("args", []) or []))
        if workload.resource_type == "Microsoft.App/containerApps" and "migrator" in invocation.lower():
            failures.append(f"{workload.name} runs the migrator at startup")
    migrate = ctx.workload(MIGRATE_JOB)
    args = [str(a) for c in migrate.containers for a in (c.get("args", []) or [])]
    if args[:1] != ["apply"] or "--confirm-initial-baseline" not in args or "--azure-ownership-bridge" not in args:
        failures.append(f"{MIGRATE_JOB} must run `apply --confirm-initial-baseline --azure-ownership-bridge`, found {args}")
    logins = ctx.workload(LOGINS_JOB)
    if [str(a) for c in logins.containers for a in (c.get("args", []) or [])][:1] != ["runtime-logins"]:
        failures.append(f"{LOGINS_JOB} must run `runtime-logins`")
    verify = ctx.workload(VERIFY_JOB)
    if [str(a) for c in verify.containers for a in (c.get("args", []) or [])][:1] != ["assert"]:
        failures.append(f"{VERIFY_JOB} must run the read-only `assert`")
    for job in (migrate, logins, verify):
        invocation = " ".join(str(p) for c in job.containers for p in c.get("command", []) or [])
        if "Paqueteria.DatabaseMigrator" not in invocation:
            failures.append(f"{job.name} does not invoke the canonical DatabaseMigrator")
        if job.value("PAQUETERIA_DB_DEPLOYMENT_PROVIDER") != "AZURE_POSTGRESQL_FLEXIBLE_SERVER":
            failures.append(f"{job.name} must declare the Azure Flexible Server provider")
        if (job.configuration.get("triggerType") != "Manual" or job.configuration.get("replicaRetryLimit") != 0):
            failures.append(f"{job.name} must be a manual job without retries")
    for dockerfile in ("Dockerfile.api", "Dockerfile.worker", "Dockerfile.web"):
        text = (ctx.repo_root / "deploy" / "azure" / dockerfile).read_text(encoding="utf-8")
        if "Paqueteria.DatabaseMigrator" in text or "DevSeed" in text:
            failures.append(f"{dockerfile} packages migration or seed tooling")
    pilot_dbops = (ctx.repo_root / PILOT_DIR / "Dockerfile.db-ops").read_text(encoding="utf-8")
    if "DevSeed" in "\n".join(line for line in pilot_dbops.splitlines() if not line.lstrip().startswith("#")):
        failures.append("pilot db-ops image must not package the synthetic DevSeed tool")
    return _result(9, "migrations only through the canonical migrator job", failures, "apply, runtime-logins and assert jobs, no startup migration")


def guard_10_same_origin_routing(ctx: Context) -> GuardResult:
    failures = []
    routes = ctx.resources("Microsoft.App/managedEnvironments/httpRouteConfigs")
    if len(routes) != 1:
        return _result(10, "PILOT-SAME-ORIGIN-ROUTING", [f"expected exactly one httpRouteConfigs, found {len(routes)}"], "")
    rules = (routes[0][1].get("properties", {}) or {}).get("rules", []) or []
    if len(rules) != 2:
        failures.append(f"expected two ordered rules (API, then Web), found {len(rules)}")
    else:
        api_rule, web_rule = rules
        if [t.get("containerApp") for t in api_rule.get("targets", [])] != [API_APP]:
            failures.append("first rule must target the API only")
        matches = [r.get("match", {}) for r in api_rule.get("routes", [])]
        prefixes = sorted(m["pathSeparatedPrefix"] for m in matches if "pathSeparatedPrefix" in m)
        exact = sorted(m["path"] for m in matches if "path" in m)
        if prefixes != sorted(API_PREFIXES) or exact != sorted(API_EXACT_PATHS) or len(matches) != len(API_PREFIXES) + len(API_EXACT_PATHS):
            failures.append(f"API rule must match exactly {API_PREFIXES} (path-separated) and {API_EXACT_PATHS}, found {matches}")
        if any("action" in r for r in api_rule.get("routes", [])):
            failures.append("API routes must not rewrite the path")
        if [t.get("containerApp") for t in web_rule.get("targets", [])] != [WEB_APP] or [r.get("match") for r in web_rule.get("routes", [])] != [{"prefix": "/"}]:
            failures.append("second rule must send every other path (prefix /) to Web")
    for name in (API_APP, WEB_APP):
        ingress = ctx.workload(name).configuration.get("ingress", {}) or {}
        if ingress.get("external") is not False or ingress.get("allowInsecure") is not False:
            failures.append(f"{name} ingress must be internal (reachable only through the route) and HTTPS-only")
        if ingress.get("transport") not in ("auto", "http"):
            failures.append(f"{name} ingress transport must support WebSockets (auto/http)")
    if ctx.workload(WORKER_APP).configuration.get("ingress"):
        failures.append("worker must not expose ingress")
    host = ((ctx.templates["apps"].get("parameters", {}) or {}).get("publicHost", {}) or {}).get("defaultValue")
    if host != "paquetenvia.com" or "AuthCenter__PublicOrigin" not in ctx.workload(API_APP).env:
        failures.append("the pilot must serve the apex paquetenvia.com and pass it to AuthCenter__PublicOrigin")
    return _result(10, "PILOT-SAME-ORIGIN-ROUTING", failures, "API prefixes first, Web catch-all, apps internal")


def guard_11_private_postgres(ctx: Context) -> GuardResult:
    failures = []
    servers = ctx.resources("Microsoft.DBforPostgreSQL/flexibleServers")
    if len(servers) != 1:
        return _result(11, "private PostgreSQL", [f"expected one PostgreSQL server, found {len(servers)}"], "")
    properties = servers[0][1].get("properties", {}) or {}
    network = properties.get("network", {}) or {}
    if network.get("publicNetworkAccess") != "Disabled" or not network.get("delegatedSubnetResourceId") or not network.get("privateDnsZoneArmResourceId"):
        failures.append("PostgreSQL must use private access (delegated subnet + private DNS zone, public access Disabled)")
    if str(properties.get("version")) != "18":
        failures.append("PostgreSQL major version must be 18")
    if int((properties.get("backup", {}) or {}).get("backupRetentionDays", 0)) < 7:
        failures.append("backup retention must be at least 7 days")
    if (servers[0][1].get("sku", {}) or {}).get("tier") != "Burstable":
        failures.append("PostgreSQL tier must stay Burstable within the budget")
    configs = {str(r.get("name", "")).split("'")[-2] if "'" in str(r.get("name", "")) else str(r.get("name", "")): (r.get("properties", {}) or {}).get("value")
               for _, r in ctx.resources("Microsoft.DBforPostgreSQL/flexibleServers/configurations")}
    if configs.get("require_secure_transport") != "ON":
        failures.append("require_secure_transport must be ON")
    if configs.get("azure.extensions") != "POSTGIS,PGCRYPTO":
        failures.append("azure.extensions must be exactly POSTGIS,PGCRYPTO")
    for _, environment in ctx.resources("Microsoft.App/managedEnvironments"):
        env_properties = environment.get("properties", {}) or {}
        if not (env_properties.get("vnetConfiguration", {}) or {}).get("infrastructureSubnetId"):
            failures.append("Container Apps environment must be injected into the VNet")
        profiles = env_properties.get("workloadProfiles", []) or []
        if not profiles or any(p.get("workloadProfileType") != "Consumption" for p in profiles):
            failures.append("Container Apps environment must use only the Consumption workload profile")
    return _result(11, "private PostgreSQL 18 in the VNet", failures, "no public endpoint, secure transport, >=7 day backups")


# ADP-001-PHOTO-SOFT-DELETE-30D-2026-10-20 (owner: "Borrado suave más largo (Recommended)"): a deleted or overwritten
# proof photo stays recoverable for 30 days. With versioning off (ADP-001) an overwrite keeps the prior state as a
# soft-deleted snapshot (learn.microsoft.com/en-us/azure/storage/blobs/soft-delete-blob-overview). The container gets
# the same 30 days, so deleting `proofs` is not a shorter way to lose the photos. Exactly 30: soft-deleted data is
# billed as stored data, and a different period needs a new owner decision.
PROOF_SOFT_DELETE_DAYS = 30
PROOF_SOFT_DELETE_POLICIES = ("deleteRetentionPolicy", "containerDeleteRetentionPolicy")


def _proof_soft_delete_failures(service_properties: dict[str, Any]) -> list[str]:
    failures = []
    for policy in PROOF_SOFT_DELETE_POLICIES:
        retention = service_properties.get(policy) or {}
        days = retention.get("days")
        if retention.get("enabled") is not True or type(days) is not int or days != PROOF_SOFT_DELETE_DAYS:
            failures.append(f"blob service {policy} must be enabled for exactly {PROOF_SOFT_DELETE_DAYS} days "
                            f"(ADP-001-PHOTO-SOFT-DELETE-30D-2026-10-20), found enabled={retention.get('enabled')} days={days}")
        if retention.get("allowPermanentDelete") not in (None, False):
            failures.append(f"blob service {policy} must not allow permanent delete, which ends the 30 days early")
    return failures


def guard_12_storage(ctx: Context) -> GuardResult:
    failures = []
    accounts = ctx.resources("Microsoft.Storage/storageAccounts")
    if len(accounts) != 1:
        return _result(12, "proof storage", [f"expected one storage account, found {len(accounts)}"], "")
    properties = accounts[0][1].get("properties", {}) or {}
    for key, expected in (("allowBlobPublicAccess", False), ("allowSharedKeyAccess", False), ("supportsHttpsTrafficOnly", True), ("isHnsEnabled", False), ("minimumTlsVersion", "TLS1_2")):
        if properties.get(key) != expected:
            failures.append(f"storage {key} must be {expected}")
    services = ctx.resources("Microsoft.Storage/storageAccounts/blobServices")
    if len(services) != 1:
        failures.append(f"expected one blob service, found {len(services)}")
    for _, service in services:
        service_properties = service.get("properties", {}) or {}
        if service_properties.get("isVersioningEnabled") is not False:
            failures.append("blob versioning must be off (ADP-001)")
        failures.extend(_proof_soft_delete_failures(service_properties))
    containers = ctx.resources("Microsoft.Storage/storageAccounts/blobServices/containers")
    if not containers or any((c.get("properties", {}) or {}).get("publicAccess") != "None" for _, c in containers):
        failures.append("proof container must be private")
    defenders = ctx.resources("Microsoft.Security/defenderForStorageSettings")
    if len(defenders) != 1:
        failures.append("Defender for Storage settings must be declared once")
    else:
        settings = defenders[0][1].get("properties", {}) or {}
        if settings.get("isEnabled") is not True or ((settings.get("malwareScanning", {}) or {}).get("onUpload", {}) or {}).get("isEnabled") is not True:
            failures.append("Defender for Storage on-upload malware scanning must be enabled")
    return _result(12, "proof storage private, Entra-only, malware scanned, 30-day soft delete", failures,
                   f"no anonymous or shared-key access, Defender on upload, blob and container soft delete {PROOF_SOFT_DELETE_DAYS} days")


def guard_13_key_vault(ctx: Context) -> GuardResult:
    failures = []
    for _, vault in ctx.resources("Microsoft.KeyVault/vaults"):
        properties = vault.get("properties", {}) or {}
        if properties.get("enableRbacAuthorization") is not True or properties.get("enablePurgeProtection") is not True:
            failures.append("Key Vault must use RBAC and purge protection")
    keys = {str(r.get("name")): r for _, r in ctx.resources("Microsoft.KeyVault/vaults/keys")}
    for required in ("pii-kek", "dataprotection-kek"):
        if not any(required in name for name in keys):
            failures.append(f"missing Key Vault key {required}")
    for name, key in keys.items():
        properties = key.get("properties", {}) or {}
        if properties.get("kty") not in ("RSA", "RSA-HSM") or int(properties.get("keySize", 0)) < 3072 or sorted(properties.get("keyOps", [])) != ["unwrapKey", "wrapKey"]:
            failures.append(f"key {name} must be RSA >= 3072 limited to wrapKey/unwrapKey")
    if ctx.resources("Microsoft.KeyVault/vaults/secrets"):
        failures.append("templates must not create secret values (the workflow writes generated secrets)")
    # PILOT-KEYVAULT-PRIVATE-APP-READ: deny by default, no trusted-service bypass, no standing IP rule,
    # only the Container Apps subnet admitted.
    for _, vault in ctx.resources("Microsoft.KeyVault/vaults"):
        acls = (vault.get("properties", {}) or {}).get("networkAcls", {}) or {}
        if acls.get("defaultAction") != "Deny":
            failures.append(f"Key Vault networkAcls.defaultAction must be Deny, found {acls.get('defaultAction')}")
        if acls.get("bypass") != "None":
            failures.append(f"Key Vault networkAcls.bypass must be None, found {acls.get('bypass')}")
        if acls.get("ipRules"):
            failures.append("Key Vault must not declare standing ipRules (the workflow opens a temporary rule)")
        rules = acls.get("virtualNetworkRules", []) or []
        if len(rules) != 1 or "snet-containerapps" not in json.dumps(rules):
            failures.append("Key Vault must admit exactly the Container Apps subnet")
    if "kv-firewall.sh open" not in ctx.workflow_text or "kv-firewall.sh close" not in ctx.workflow_text:
        failures.append("workflow must open and close the temporary Key Vault firewall rule around its secret access")
    return _result(13, "Key Vault deny-by-default, RBAC, purge protection, wrap-only keys", failures, f"{len(keys)} key(s)")


def guard_14_rbac(ctx: Context) -> GuardResult:
    failures = []
    count = 0
    for template, assignment in ctx.resources("Microsoft.Authorization/roleAssignments"):
        count += 1
        definition = str((assignment.get("properties", {}) or {}).get("roleDefinitionId", ""))
        if "parameters('blobTagReaderRoleDefinitionId')" in definition:
            continue
        role_id = _resolve_role_id(definition, ctx.templates[template].get("variables", {}) or {})
        if role_id not in ALLOWED_ROLE_IDS:
            failures.append(f"{template} assigns a role outside the pilot allowlist: {definition}")
    return _result(14, "least-privilege RBAC only", failures, f"{count} role assignment(s) within the allowlist")


def _resolve_role_id(definition: str, variables: dict[str, Any]) -> str | None:
    """Role id of `subscriptionResourceId(..., <literal> | variables('x') | variables('x').member)`."""
    literal = re.search(r"roleDefinitions', '([0-9a-f-]{36})'\)", definition)
    if literal:
        return literal.group(1)
    reference = re.search(r"roleDefinitions', variables\('([A-Za-z0-9_]+)'\)(?:\.([A-Za-z0-9_]+))?\)", definition)
    if not reference:
        return None
    value = variables.get(reference.group(1))
    if reference.group(2):
        value = value.get(reference.group(2)) if isinstance(value, dict) else None
    return value if isinstance(value, str) else None


def guard_15_budget_and_logs(ctx: Context) -> GuardResult:
    failures = []
    budgets = ctx.resources("Microsoft.Consumption/budgets")
    if len(budgets) != 1:
        failures.append("exactly one Cost Management budget is required")
    else:
        properties = budgets[0][1].get("properties", {}) or {}
        notifications = properties.get("notifications", {}) or {}
        if properties.get("timeGrain") != "Monthly" or not any(int(n.get("threshold", 0)) == 100 for n in notifications.values()):
            failures.append("budget must be monthly with an alert at 100%")
        platform_parameters = ctx.templates["platform"].get("parameters", {}) or {}
        amount = platform_parameters.get("budgetAmount", {})
        if amount.get("defaultValue") != 100 or amount.get("maxValue") != 100:
            failures.append("budgetAmount must default to and never exceed 100 USD")
    workspaces = ctx.resources("Microsoft.OperationalInsights/workspaces")
    if len(workspaces) != 1 or not ((workspaces[0][1].get("properties", {}) or {}).get("workspaceCapping", {}) or {}).get("dailyQuotaGb"):
        failures.append("Log Analytics must declare a daily ingestion cap")
    return _result(15, "PILOT-BUDGET-100USD guard rails", failures, "100 USD budget alert and capped log ingestion")


def guard_16_adp_contract(ctx: Context) -> GuardResult:
    failures = []
    required = {
        API_APP: {
            "AZURE_CLIENT_ID": None,
            "Locations__PiiProtector": "AzureKeyVault",
            "Incidents__PiiProtector": "AzureKeyVault",
            "PiiProtection__AzureKeyVault__KeyId": None,
            "ProofStorage__Provider": "AzureBlob",
            "ProofStorage__ThreatScanner": "DefenderForStorage",
            "ProofStorage__AzureBlob__ServiceUri": None,
            "DataProtection__KeyEncryption__Provider": "AzureKeyVault",
            "DataProtection__KeyEncryption__AzureKeyVault__KeyId": None,
        },
        WORKER_APP: {
            "AZURE_CLIENT_ID": None,
            "ProofStorage__Provider": "AzureBlob",
            "ProofStorage__ThreatScanner": "DefenderForStorage",
            "ProofStorage__AzureBlob__ServiceUri": None,
            "DataProtection__Provider": "PostgreSql",
            "DataProtection__KeyEncryption__Provider": "AzureKeyVault",
            "DataProtection__KeyEncryption__AzureKeyVault__KeyId": None,
        },
    }
    for name, expected in required.items():
        workload = ctx.workload(name)
        for key, value in expected.items():
            if key not in workload.env:
                failures.append(f"{name} lacks {key}")
            elif value is not None and workload.value(key) != value:
                failures.append(f"{name} must set {key}={value}")
    if "PiiProtection__AzureKeyVault__KeyId" in ctx.workload(WORKER_APP).env:
        failures.append("worker needs no PII key (ADP-001 RBAC table)")
    return _result(16, "ADP-001 configuration contract wired", failures, "PII, proof storage and Data Protection KEK settings present")


def guard_19_cleanups_enabled(ctx: Context) -> GuardResult:
    """PILOT-CLEANUPS-ENABLED: OPS-004 retention and OPS-003 cleanup run on the Worker with the contract values."""
    failures = []
    worker = ctx.workload(WORKER_APP)
    expected = {
        "OutboxRetention__Enabled": "true",
        "OutboxRetention__DryRun": "false",
        "OperationalCleanup__IdempotencyKeys__Enabled": "true",
        "OperationalCleanup__IdempotencyKeys__DryRun": "false",
        "OperationalCleanup__ProofUploadSessions__Enabled": "true",
        "OperationalCleanup__BffSessions__Enabled": "true",
    }
    for key, value in expected.items():
        if worker.value(key) != value:
            failures.append(f"worker must set {key}={value} (found {worker.value(key)})")
    for workload in ctx.workloads:
        for key in workload.env:
            if key.startswith(("OutboxRetention__", "OperationalCleanup__")) and key not in expected:
                failures.append(f"{workload.name} overrides contract cleanup value {key}; keep the OPS-003/OPS-004 defaults")
            if workload.name != WORKER_APP and key in expected:
                failures.append(f"{workload.name} must not run cleanup jobs ({key}); they belong to the Worker")
    return _result(19, "PILOT-CLEANUPS-ENABLED with contract values", failures, "OPS-004 retention and OPS-003 cleanup enabled on the Worker")


def guard_17_business_settings(ctx: Context) -> GuardResult:
    failures = []
    for name in (API_APP, WORKER_APP):
        if not ctx.workload(name).env_includes_business_settings:
            failures.append(f"{name} does not receive the reviewed business settings")
    try:
        failures.extend(validate_settings(load_settings(ctx.repo_root / SETTINGS_FILE), allow_sentinel=True))
    except GuardError as error:
        failures.append(str(error))
    return _result(17, "business settings cannot override the platform", failures, "settings file well-formed")


def guard_18_bff_web_image(ctx: Context) -> GuardResult:
    failures = []
    dockerfile = (ctx.repo_root / PILOT_DIR / "Dockerfile.web").read_text(encoding="utf-8")
    instructions = [line for line in dockerfile.splitlines() if not line.lstrip().startswith("#")]
    declared = set(re.findall(r"^(?:ARG|ENV)\s+([A-Z0-9_]+)", "\n".join(instructions), re.MULTILINE))
    if "NEXT_PUBLIC_API_BASE_URL" in declared:
        failures.append("BFF web image must never define NEXT_PUBLIC_API_BASE_URL (same origin)")
    if not re.search(r"^ENV NEXT_PUBLIC_AUTH_MODE=bff$", "\n".join(instructions), re.MULTILINE):
        failures.append("BFF web image must set NEXT_PUBLIC_AUTH_MODE=bff")
    used: set[str] = set()
    for root in ("apps/web/src", "apps/web/next.config.ts"):
        path = ctx.repo_root / root
        files = [path] if path.is_file() else [p for p in path.rglob("*") if p.suffix in {".ts", ".tsx", ".js", ".mjs"} and "node_modules" not in p.parts]
        for file in files:
            used.update(re.findall(r"NEXT_PUBLIC_[A-Z0-9_]+", file.read_text(encoding="utf-8", errors="replace")))
    missing = sorted(used - declared - {"NEXT_PUBLIC_API_BASE_URL"})
    if missing:
        failures.append(f"BFF web image manifest misses build inputs {missing}")
    if "deploy/azure/pilot/Dockerfile.web" not in ctx.workflow_text:
        failures.append("workflow must build the web image from deploy/azure/pilot/Dockerfile.web")
    if ctx.workload(WEB_APP).env.get("PAQUETERIA_CSP_CONNECT_SOURCES") is None:
        failures.append("web must allow the proof storage origin in CSP connect-src")
    return _result(18, "AUTH-001 BFF web image (same origin)", failures, "API base URL unset, auth mode bff, manifest complete")


# ---------------------------------------------------------------------------------------------- OPS-002 server name
# OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20 (owner J1: "Que el despliegue lo siga (Recommended)"): the PostgreSQL server
# is one setting, the azure-pilot variable PILOT_POSTGRES_SERVER_NAME (empty = the default server). The workflow passes
# it to platform.bicep, whose postgresName and postgresHost outputs feed every other consumer: the migration connection,
# the runtime credentials, the jobs, the SCL-007 alerts and restore-drill.sh. This prefix is the only place in the
# guards that spells the server name; the two template defaults and the drill script's format check are the others.
POSTGRES_SERVER_PREFIX = "pg-pv-pilot-"
POSTGRES_SERVER_VARIABLE = "PILOT_POSTGRES_SERVER_NAME"
POSTGRES_SERVER_PARAMETER = "postgresServerName"
POSTGRES_SERVER_TYPE = "Microsoft.DBforPostgreSQL/flexibleServers"
# The prefix, the 13-character uniqueString(subscription().id, resourceGroup().id) suffix (the default server), then
# optional '-' parts of lowercase letters and digits (restore-drill.sh names `-r<UTC minute>` and `-drill-<UTC minute>`),
# at most 63 characters in all (the flexible server name rule). Group 1 is the base name, the default server's.
POSTGRES_SERVER_NAME_PATTERN = rf"^({POSTGRES_SERVER_PREFIX}[a-z0-9]{{13}})(-[a-z0-9]+)*$"
POSTGRES_SERVER_NAME = re.compile(POSTGRES_SERVER_NAME_PATTERN)
POSTGRES_SERVER_NAME_MAX = 63
POSTGRES_SERVER_DEFAULT = f"[format('{POSTGRES_SERVER_PREFIX}{{0}}', uniqueString(subscription().id, resourceGroup().id))]"
POSTGRES_SERVER_NAME_EXPRESSION = f"[parameters('{POSTGRES_SERVER_PARAMETER}')]"
POSTGRES_SERVER_ID = f"[resourceId('{POSTGRES_SERVER_TYPE}', parameters('{POSTGRES_SERVER_PARAMETER}'))]"
POSTGRES_HOST_OUTPUT = (f"[reference(resourceId('{POSTGRES_SERVER_TYPE}', parameters('{POSTGRES_SERVER_PARAMETER}')), "
                        "'2024-08-01').fullyQualifiedDomainName]")
POSTGRES_HOST = re.compile(r"^[a-z0-9-]+\.postgres\.database\.azure\.com$")


def validate_postgres_server_name(value: Any) -> list[str]:
    """PILOT_POSTGRES_SERVER_NAME: empty (the default server) or a pilot server name. A malformed value is never echoed."""
    if value == "":
        return []
    if not isinstance(value, str) or len(value) > POSTGRES_SERVER_NAME_MAX or not POSTGRES_SERVER_NAME.fullmatch(value):
        return [f"{POSTGRES_SERVER_VARIABLE} must be empty (the default server) or a pilot PostgreSQL server name: "
                f"{POSTGRES_SERVER_PREFIX}, the 13-character resource-group suffix, then optional '-' parts of lowercase "
                f"letters and digits, at most {POSTGRES_SERVER_NAME_MAX} characters (README section 6.4, real restore)"]
    return []


def connection_host(text: str) -> str:
    """The single Host (or its Npgsql alias Server) of a connection string, which must be a PostgreSQL flexible server
    FQDN. Raises GuardError without ever quoting the input, which holds a password."""
    hosts = []
    for part in text.strip().split(";"):
        key, separator, value = part.partition("=")
        if separator and key.strip().lower() in ("host", "server"):
            hosts.append(value.strip())
    if len(hosts) != 1 or not POSTGRES_HOST.fullmatch(hosts[0]):
        raise GuardError("the connection string has no single PostgreSQL flexible server Host (value not shown)")
    return hosts[0]


def _arm_resource_id(expression: Any, subscription_id: str, resource_group: str) -> str | None:
    """Resolve a compiled `[resourceId('<namespace>/<type>[/<child>]', '<name>'[, '<child name>'])]` literal."""
    match = re.fullmatch(r"\[resourceId\('([^']+)'((?:, '[^']+')+)\)\]", str(expression))
    if not match:
        return None
    namespace, *types = match.group(1).split("/")
    names = re.findall(r"'([^']+)'", match.group(2))
    if len(types) != len(names):
        return None
    path = "/".join(f"{kind}/{name}" for kind, name in zip(types, names))
    return f"/subscriptions/{subscription_id}/resourceGroups/{resource_group}/providers/{namespace}/{path}"


def postgres_adoption_failures(name: str, server: Any, platform: Any, *, subscription_id: str, resource_group: str,
                               region: str) -> list[str]:
    """Before stage 2 adopts PILOT_POSTGRES_SERVER_NAME: the server exists (the ARM GET of `az resource show`), is
    Ready and standalone, and already has every value platform.bicep sets that an existing server cannot change
    (administrator login, major version, region, private network with the template's delegated subnet and private DNS
    zone, geo-redundant backup) and a disk no larger than the template's (a disk never shrinks), so the deployment
    updates it in place and never creates a second server."""
    if not isinstance(server, dict) or not isinstance(server.get("properties"), dict):
        return [f"{name}: the server description is not an ARM resource"]
    resources = platform.get("resources", []) if isinstance(platform, dict) else []
    template = [r for r in (resources.values() if isinstance(resources, dict) else resources)
                if isinstance(r, dict) and r.get("type") == POSTGRES_SERVER_TYPE and not r.get("existing")]
    if len(template) != 1:
        return ["platform.json must declare exactly one PostgreSQL server"]
    expected, found = template[0].get("properties", {}) or {}, server["properties"]
    variables = platform.get("variables", {}) or {}
    failures = []
    if str(server.get("name", "")) != name or str(server.get("type", "")).lower() != POSTGRES_SERVER_TYPE.lower():
        failures.append(f"{name}: the description is of {server.get('type')} {server.get('name')}")
    if "".join(str(server.get("location", "")).split()).lower() != region:
        failures.append(f"{name} is in {server.get('location')}, not {region}")
    if found.get("state") != "Ready":
        failures.append(f"{name} is {found.get('state')}, not Ready; start it or wait for the restore to finish")
    if found.get("replicationRole") in ("AsyncReplica", "GeoAsyncReplica"):
        failures.append(f"{name} is a read replica, not a restored standalone server")
    login = expected.get("administratorLogin")
    login_variable = re.fullmatch(r"\[variables\('([A-Za-z0-9_]+)'\)\]", str(login))
    login = variables.get(login_variable.group(1)) if login_variable else login
    if found.get("administratorLogin") != login:
        failures.append(f"{name} has another administrator login; it can never change, so the template cannot adopt it")
    if str(found.get("version")) != str(expected.get("version")):
        failures.append(f"{name} runs PostgreSQL {found.get('version')}, the template {expected.get('version')}")
    network, expected_network = found.get("network", {}) or {}, expected.get("network", {}) or {}
    if network.get("publicNetworkAccess") != expected_network.get("publicNetworkAccess"):
        failures.append(f"{name} must use private access like the template (publicNetworkAccess "
                        f"{expected_network.get('publicNetworkAccess')})")
    for key, label in (("delegatedSubnetResourceId", "delegated subnet"), ("privateDnsZoneArmResourceId", "private DNS zone")):
        wanted = _arm_resource_id(expected_network.get(key), subscription_id, resource_group)
        if wanted is None:
            failures.append(f"platform.json {key} is not a literal resourceId")
        elif str(network.get(key, "")).lower() != wanted.lower():
            failures.append(f"{name} is not in the template's {label}; restore it without --vnet, --subnet or "
                            "--private-dns-zone (README section 6.4)")
    geo = (found.get("backup", {}) or {}).get("geoRedundantBackup")
    if geo != (expected.get("backup", {}) or {}).get("geoRedundantBackup"):
        failures.append(f"{name} has geo-redundant backup {geo}; it is fixed at creation and the template sets "
                        f"{(expected.get('backup', {}) or {}).get('geoRedundantBackup')}")
    size, template_size = (found.get("storage", {}) or {}).get("storageSizeGB"), (expected.get("storage", {}) or {}).get("storageSizeGB")
    if not isinstance(size, int) or not isinstance(template_size, int) or size > template_size:
        failures.append(f"{name} has a {size} GiB disk and the template {template_size} GiB; a disk never shrinks, so "
                        "raising storageSizeGB (and the SCL-007 storage alert base) needs its own owner decision")
    return failures


# ---------------------------------------------------------------------------------------------- OBS-002
# Log search alert price per rule and month by evaluation frequency (Azure Retail Prices API,
# mexicocentral, 2026-09-28). Frequencies outside 5-15 minutes are not allowed for the pilot.
ALERT_MONTHLY_USD = {"PT5M": 1.65, "PT10M": 1.10, "PT15M": 0.55}
ALERT_BUDGET_USD = 5.0
REQUIRED_ALERTS = {
    "sqr-pv-pilot-outbox-lag",
    "sqr-pv-pilot-outbox-dead",
    "sqr-pv-pilot-job-failure",
    "sqr-pv-pilot-readiness",
    "sqr-pv-pilot-api-5xx",
}
# Tables and structured-log properties the OBS-002 queries may read. The properties mirror
# Paqueteria.Infrastructure.Observability.TelemetryDimensions.Allowed (ObservabilityArchitectureTests).
OBSERVABILITY_TABLES = {"ContainerAppConsoleLogs_CL", "ContainerAppSystemLogs_CL"}
OBSERVABILITY_STATE_PROPERTIES = {
    "Lane", "Claimed", "Processed", "Retry", "Dead", "LoopFailures", "MaxClaimAgeMs", "WindowSeconds",
    "Job", "Outcome", "DurationMs", "Total", "Status2xx", "Status3xx", "Status4xx", "Status5xx",
}
ACTION_GROUP_RECEIVER_KINDS = (
    "smsReceivers", "webhookReceivers", "itsmReceivers", "azureAppPushReceivers", "automationRunbookReceivers",
    "voiceReceivers", "logicAppReceivers", "azureFunctionReceivers", "armRoleReceivers", "eventHubReceivers",
    "incidentReceivers",
)
EMAIL_ADDRESS = re.compile(r"^[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9-]{1,63}(\.[A-Za-z0-9-]{1,63})+$")
ISO_MINUTES = re.compile(r"^PT(?:(\d+)H)?(?:(\d+)M)?$")


def _iso_minutes(value: Any) -> int | None:
    match = ISO_MINUTES.match(str(value))
    if not match or not any(match.groups()):
        return None
    return int(match.group(1) or 0) * 60 + int(match.group(2) or 0)


# SCL-007-POSTGRES-CONNECTION-ALERT-2026-10-14: the connection alerts, with the four resource alerts below the only
# metric alerts of the pilot. Owner answers: "Sí, 80 % y 95 %
# (Recommended)" (28 and 33 of the 35 B1ms user connections, AI-15 connection_pool_utilization 0.8 and 0.95) and
# "Máximo en 5 min, cada minuto (Recommended)". Each watches one time series (one server, no dimension); metric alerts
# are billed per series whatever the frequency, 10 free and then about 0.10 USD per series and month
# (azure.microsoft.com/en-us/pricing/details/monitor), counted here at that worst case.
METRIC_ALERT_TYPE = "Microsoft.Insights/metricAlerts"
METRIC_ALERT_SERIES_MONTHLY_USD = 0.10
POSTGRES_SKU = "Standard_B1ms"
POSTGRES_USER_CONNECTIONS = 35
PILOT_ACTION_GROUP_ID = "[resourceId('Microsoft.Insights/actionGroups', 'ag-pv-pilot-ops')]"
# name -> (severity, threshold): 28 = 80 % and 33 = 95 % (rounded down) of the 35 user connections.
REQUIRED_CONNECTION_ALERTS = {
    "mar-pv-pilot-pg-connections-warning": (2, 28),
    "mar-pv-pilot-pg-connections-critical": (1, 33),
}
METRIC_ALERT_PROPERTIES = {
    "description", "severity", "enabled", "scopes", "evaluationFrequency", "windowSize", "autoMitigate", "criteria", "actions",
}
CONNECTION_CRITERION = {
    "criterionType": "StaticThresholdCriterion",
    "name": "active-connections",
    "metricNamespace": "Microsoft.DBforPostgreSQL/flexibleServers",
    "metricName": "active_connections",
    "timeAggregation": "Maximum",
    "operator": "GreaterThanOrEqual",
}
# SCL-007-POSTGRES-RESOURCE-ALERTS-2026-10-20: four more metric alerts on the same server, one level each (the
# warning, severity 2), every minute, through the same action group. Owner answers: "CPU, memoria y disco
# (Recommended)", "Un nivel, de aviso (Recommended)", "CPU 70, memoria 80, disco 70 (Recommended)", "Sí, agregarla
# (Recommended)" (CPU credits) and "Menos de 72 (25 %) (Recommended)", whose option text reads "Avisa con 72 créditos o
# menos (mínimo en 5 min, cada minuto)", so the credits rule is LessThanOrEqual 72. CPU takes the AI-15
# db_cpu_utilization warning (0.7, window 30m); storage has no window in the answer and takes the connection alerts'
# Maximum over 5 minutes. Metrics, units, aggregations and dimensions: learn.microsoft.com/en-us/azure/azure-monitor/
# reference/supported-metrics/microsoft-dbforpostgresql-flexibleservers-metrics (each Average/Maximum/Minimum, PT1M;
# cpu_credits_remaining is a Count with the dimension LogicalServerName, the other three are Percent with ServerName).
# No dimension filter: one server, one time series per rule, so the pilot has 6 series, inside the 10 free.
RESOURCE_ALERT_SEVERITY = 2
POSTGRES_STORAGE_GB = 32
POSTGRES_MAX_CPU_CREDITS = 288  # B1ms (Bv1-series VM page: 288 banked at most, 12 per hour); 72 = 25 %.
# name -> (criterion name, metric, aggregation, operator, threshold, window).
REQUIRED_RESOURCE_ALERTS = {
    "mar-pv-pilot-pg-cpu-warning": ("cpu-percent", "cpu_percent", "Average", "GreaterThanOrEqual", 70, "PT30M"),
    "mar-pv-pilot-pg-memory-warning": ("memory-percent", "memory_percent", "Maximum", "GreaterThanOrEqual", 80, "PT5M"),
    "mar-pv-pilot-pg-storage-warning": ("storage-percent", "storage_percent", "Maximum", "GreaterThanOrEqual", 70, "PT5M"),
    "mar-pv-pilot-pg-cpu-credits-warning": (
        "cpu-credits-remaining", "cpu_credits_remaining", "Minimum", "LessThanOrEqual", 72, "PT5M"),
}
METRIC_ALERT_OPERATOR_SYMBOLS = {"GreaterThanOrEqual": ">=", "LessThanOrEqual": "<="}
EMAIL_LITERAL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+")


def _required_metric_alerts() -> dict[str, tuple[int, str, dict[str, Any]]]:
    """name -> (severity, windowSize, criterion) for the six PostgreSQL metric alerts; all are evaluated every minute."""
    required: dict[str, tuple[int, str, dict[str, Any]]] = {
        name: (severity, "PT5M", dict(CONNECTION_CRITERION, threshold=threshold))
        for name, (severity, threshold) in REQUIRED_CONNECTION_ALERTS.items()
    }
    for name, (criterion_name, metric, aggregation, operator, threshold, window) in REQUIRED_RESOURCE_ALERTS.items():
        required[name] = (RESOURCE_ALERT_SEVERITY, window, {
            "criterionType": "StaticThresholdCriterion",
            "name": criterion_name,
            "metricNamespace": "Microsoft.DBforPostgreSQL/flexibleServers",
            "metricName": metric,
            "timeAggregation": aggregation,
            "operator": operator,
            "threshold": threshold,
        })
    return required


REQUIRED_METRIC_ALERTS = _required_metric_alerts()


def _metric_alert_failures(ctx: Context) -> tuple[list[str], int]:
    """SCL-007: exactly the six PostgreSQL metric alerts (the two connection alerts and the four resource alerts), on
    the pilot server, through the pilot action group; returns the failures and the number of monitored time series."""
    failures: list[str] = []
    alerts = ctx.resources(METRIC_ALERT_TYPE)
    names = sorted(str(resource.get("name")) for _, resource in alerts)
    if names != sorted(REQUIRED_METRIC_ALERTS):
        failures.append(f"metric alerts must be exactly {sorted(REQUIRED_METRIC_ALERTS)}, found {names}")
    for template, resource in alerts:
        name = str(resource.get("name"))
        expected = REQUIRED_METRIC_ALERTS.get(name)
        if expected is None:
            continue
        severity, window, criterion = expected
        properties = resource.get("properties", {}) or {}
        if template != "observability" or resource.get("copy") or resource.get("location") != "global":
            failures.append(f"{name} must be one literal global resource of observability.bicep")
        extra = sorted(set(properties) - METRIC_ALERT_PROPERTIES)
        if extra:
            failures.append(f"{name} sets unexpected properties {extra}")
        if properties.get("enabled") is not True or properties.get("autoMitigate") is not True:
            failures.append(f"{name} must be enabled and stateful (autoMitigate)")
        if properties.get("severity") != severity:
            failures.append(f"{name} must have severity {severity}, found {properties.get('severity')}")
        if properties.get("evaluationFrequency") != "PT1M" or properties.get("windowSize") != window:
            failures.append(f"{name} must be evaluated every minute (PT1M) over {_iso_minutes(window)} minutes ({window})")
        if properties.get("scopes") != [POSTGRES_SERVER_ID]:
            failures.append(f"{name} must watch only the pilot PostgreSQL server (parameter {POSTGRES_SERVER_PARAMETER}), "
                            f"found {properties.get('scopes')}")
        criteria = properties.get("criteria", {}) or {}
        found = criteria.get("allOf")
        if (criteria.get("odata.type") != "Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria" or set(criteria) != {"odata.type", "allOf"}
                or not isinstance(found, list) or len(found) != 1 or found[0] != criterion or type(found[0].get("threshold")) is not int):
            failures.append(f"{name} must alert on the {criterion['timeAggregation']} of {criterion['metricName']} "
                            f"{METRIC_ALERT_OPERATOR_SYMBOLS[criterion['operator']]} {criterion['threshold']}, with no dimension or other criterion")
        if properties.get("actions") != [{"actionGroupId": PILOT_ACTION_GROUP_ID}]:
            failures.append(f"{name} must notify only through the pilot action group ag-pv-pilot-ops")
        if EMAIL_LITERAL.search(json.dumps(resource)):
            failures.append(f"{name} must not contain an e-mail address")
    servers = ctx.resources("Microsoft.DBforPostgreSQL/flexibleServers")
    server = servers[0][1] if len(servers) == 1 else {}
    sku = (server.get("sku", {}) or {}).get("name")
    if sku != POSTGRES_SKU:
        failures.append(f"the PostgreSQL connection alerts (28 and 33) are 80 % and 95 % of the {POSTGRES_USER_CONNECTIONS} user "
                        f"connections of {POSTGRES_SKU} and the CPU credits alert (72) is 25 % of its {POSTGRES_MAX_CPU_CREDITS} "
                        f"credits; revisit them with the server SKU, found {sku}")
    storage = ((server.get("properties", {}) or {}).get("storage", {}) or {}).get("storageSizeGB")
    if storage != POSTGRES_STORAGE_GB:
        failures.append(f"the PostgreSQL storage alert (70 %, 22.4 GiB) is a share of the {POSTGRES_STORAGE_GB} GiB disk; revisit it "
                        f"with the disk size, found {storage}")
    suffix = ctx.templates.get("observability", {}).get("variables", {}).get("suffix")
    if suffix is None or suffix != ctx.templates.get("platform", {}).get("variables", {}).get("suffix"):
        failures.append("observability.bicep must name the workspace with the same suffix as platform.bicep")
    return failures, len(alerts)


def _alert_rules(template: dict[str, Any], resource: dict[str, Any]) -> list[dict[str, Any]] | str:
    """The rule list behind the scheduledQueryRules copy loop (`for rule in rules`), kept literal."""
    variable = _copy_count_variable(resource)
    rules = (template.get("variables", {}) or {}).get(variable) if variable else None
    if not isinstance(rules, list) or not all(isinstance(r, dict) for r in rules):
        return "scheduledQueryRules must loop over a literal `rules` variable so every rule can be verified"
    return rules


# OBS-003 (OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12): one workspace-based Application Insights; the export settings
# of the API and the Worker; the tables and names no alert or workbook query may read.
APP_INSIGHTS_TYPE = "Microsoft.Insights/components"
TELEMETRY_WORKLOADS = (API_APP, WORKER_APP)
# The managed component: platform.bicep names it, and the API and the Worker read its connection string by that name.
APP_INSIGHTS_NAME = "format('appi-pv-pilot-{0}', variables('suffix'))"
APP_INSIGHTS_CONNECTION_STRING = re.compile(
    r"createObject\('name', 'TelemetryExport__ConnectionString', 'value', "
    + re.escape(f"reference(resourceId('{APP_INSIGHTS_TYPE}', {APP_INSIGHTS_NAME}), ")
    + r"'\d{4}-\d{2}-\d{2}(?:-preview)?'\)\.ConnectionString\)")
TELEMETRY_ENV_PREFIXES = ("TelemetryExport__", "TelemetryPseudonym__", "APPLICATIONINSIGHTS_", "OTEL_")
APPLICATION_INSIGHTS_TABLES = (
    "AppRequests", "AppDependencies", "AppTraces", "AppMetrics", "AppExceptions", "AppEvents", "AppPageViews",
    "AppAvailabilityResults", "AppBrowserTimings", "AppPerformanceCounters", "AppSystemEvents", "customMetrics",
    "customDimensions", "customEvents",
)
# OBS-003-TENANT-PSEUDONYM-2026-10-12: never an alert or workbook dimension.
PSEUDONYM_AND_ROLE_NAMES = (
    "OrganizationPseudonym", "ActorRoleKind", "paqueteria.organization.pseudonym", "paqueteria.actor.role_kind",
    "pseudonym", "role_kind",
)


def _observability_obs003_failures(ctx: Context) -> list[str]:
    """OBS-003: the Application Insights component, the export switch of the API and the Worker, and the queries."""
    failures: list[str] = []
    components = ctx.resources(APP_INSIGHTS_TYPE)
    if len(components) != 1 or components[0][0] != "platform":
        failures.append(f"exactly one Application Insights component is allowed, in platform.bicep, found {[t for t, _ in components]}")
    else:
        component = components[0][1]
        properties = component.get("properties", {}) or {}
        if component.get("name") != f"[{APP_INSIGHTS_NAME}]":
            failures.append(f"Application Insights must be the managed appi-pv-pilot-<suffix> component, found {component.get('name')}")
        if component.get("kind") != "web" or properties.get("Application_Type") != "web":
            failures.append("Application Insights must be kind and Application_Type web")
        if component.get("location") != "[parameters('location')]":
            failures.append(f"Application Insights must be in the pilot resource group's region, found {component.get('location')}")
        workspace = str(properties.get("WorkspaceResourceId", ""))
        if properties.get("IngestionMode") != "LogAnalytics" or not workspace.startswith("[resourceId('Microsoft.OperationalInsights/workspaces'") or "law-pv-pilot-" not in workspace:
            failures.append("Application Insights must be workspace-based (IngestionMode LogAnalytics) on the pilot Log Analytics workspace, inside its daily cap")
        if properties.get("DisableIpMasking") is not False:
            failures.append("Application Insights must keep IP masking on (DisableIpMasking false)")
        for forbidden in ("SamplingPercentage", "RetentionInDays", "ImmediatePurgeDataOn30Days"):
            if forbidden in properties:
                failures.append(f"Application Insights must not set {forbidden} (sampling and retention follow OBS-003 and the workspace)")
    for workload in ctx.workloads:
        names = [key for key in workload.env if key.startswith(TELEMETRY_ENV_PREFIXES)]
        if workload.name not in TELEMETRY_WORKLOADS:
            if names:
                failures.append(f"{workload.name} must not receive telemetry settings {sorted(names)}")
            continue
        enabled = workload.env.get("TelemetryExport__Enabled")
        if enabled is None or enabled[0] != "value" or enabled[1] not in ("true", "false"):
            failures.append(f"{workload.name} must set TelemetryExport__Enabled to the literal 'true' or 'false', found {enabled}")
        if workload.env.get("TelemetryPseudonym__CurrentKeyVersion") != ("value", TELEMETRY_PSEUDONYM_KEY_VERSION):
            failures.append(f"{workload.name} must set TelemetryPseudonym__CurrentKeyVersion to '{TELEMETRY_PSEUDONYM_KEY_VERSION}', the version of {TELEMETRY_PSEUDONYM_SECRET}")
        raw = json.dumps(workload.resource)
        if not APP_INSIGHTS_CONNECTION_STRING.search(raw):
            failures.append(f"{workload.name} must take TelemetryExport__ConnectionString from the pilot Application Insights component appi-pv-pilot-<suffix>")
        unexpected = sorted(set(names) - {"TelemetryExport__Enabled", "TelemetryExport__ConnectionString", "TelemetryPseudonym__CurrentKeyVersion"})
        if unexpected:
            failures.append(f"{workload.name} sets unexpected telemetry settings {unexpected} (the sampling and exporter features are fixed in code)")
    text = json.dumps(ctx.templates.get("observability", {}).get("variables", {}))
    for table in APPLICATION_INSIGHTS_TABLES:
        if re.search(rf"\b{table}\b", text):
            failures.append(f"alert and workbook queries must not read the Application Insights table {table}")
    for name in PSEUDONYM_AND_ROLE_NAMES:
        if name in text:
            failures.append(f"alert and workbook queries must not read {name} (OBS-003-TENANT-PSEUDONYM-2026-10-12)")
    apps_suffix = ctx.templates.get("apps", {}).get("variables", {}).get("suffix")
    if apps_suffix is None or apps_suffix != ctx.templates.get("platform", {}).get("variables", {}).get("suffix"):
        failures.append("apps.bicep must derive the component name from the same suffix as platform.bicep")
    if f"has_secret {TELEMETRY_PSEUDONYM_SECRET}" not in ctx.workflow_text:
        failures.append(f"workflow must stop before deploying when Key Vault lacks {TELEMETRY_PSEUDONYM_SECRET}")
    return failures


def guard_21_observability(ctx: Context) -> GuardResult:
    """OBS-002: the pilot alerts exist, run every 5-15 minutes within their cost bound, notify only the
    owner's e-mail (a parameter without default) and read only the PII-free structured events. OBS-003: one
    workspace-based Application Insights on the pilot workspace, the export switch of the API and the Worker is a
    reviewed literal, and no query reads Application Insights tables or the organization pseudonym. SCL-007: exactly
    the six PostgreSQL metric alerts on the pilot server, every minute, through the same action group, counted in the
    cost bound: the two connection alerts (28 and 33, Maximum over 5 minutes) and the four resource alerts (CPU
    Average over 30 minutes >= 70, memory Maximum over 5 minutes >= 80, storage Maximum over 5 minutes >= 70, CPU
    credits Minimum over 5 minutes <= 72, severity 2); no e-mail address in the template."""
    failures: list[str] = _observability_obs003_failures(ctx)
    template = ctx.templates.get("observability", {})
    parameter = (template.get("parameters", {}) or {}).get("alertEmailAddress")
    if not isinstance(parameter, dict) or parameter.get("type") != "string" or "defaultValue" in parameter:
        failures.append("observability.bicep must take alertEmailAddress as a string parameter without a default")

    groups = ctx.resources("Microsoft.Insights/actionGroups")
    if len(groups) != 1:
        failures.append(f"expected exactly one action group, found {len(groups)}")
    else:
        properties = groups[0][1].get("properties", {}) or {}
        emails = properties.get("emailReceivers", []) or []
        if properties.get("enabled") is not True:
            failures.append("the action group must be enabled")
        if len(emails) != 1 or emails[0].get("emailAddress") != "[parameters('alertEmailAddress')]":
            failures.append("the action group must have exactly one e-mail receiver bound to parameters('alertEmailAddress')")
        extra = [kind for kind in ACTION_GROUP_RECEIVER_KINDS if properties.get(kind)]
        if extra:
            failures.append(f"the action group may notify only by e-mail, found {extra}")

    rules: list[dict[str, Any]] = []
    for _, resource in ctx.resources("Microsoft.Insights/scheduledQueryRules"):
        found = _alert_rules(template, resource) if resource.get("copy") else [resource]
        if isinstance(found, str):
            failures.append(found)
            continue
        properties = resource.get("properties", {}) or {}
        if properties.get("enabled") is not True or properties.get("autoMitigate") is not True:
            failures.append("alert rules must be enabled and stateful (autoMitigate)")
        if "Microsoft.Insights/actionGroups" not in json.dumps((properties.get("actions", {}) or {}).get("actionGroups", [])):
            failures.append("alert rules must notify through the pilot action group")
        if "Microsoft.OperationalInsights/workspaces" not in json.dumps(properties.get("scopes", [])):
            failures.append("alert rules must query the pilot Log Analytics workspace")
        rules.extend(found)

    names = sorted(str(r.get("name")) for r in rules)
    if set(names) != REQUIRED_ALERTS or len(names) != len(REQUIRED_ALERTS):
        failures.append(f"alert rules must be exactly {sorted(REQUIRED_ALERTS)}, found {names}")
    metric_failures, series = _metric_alert_failures(ctx)
    failures.extend(metric_failures)
    if EMAIL_LITERAL.search(json.dumps(template)):
        failures.append("observability.bicep must not contain an e-mail address; the owner's goes in observability.parameters.json")
    monthly = series * METRIC_ALERT_SERIES_MONTHLY_USD
    for rule in rules:
        frequency, window = rule.get("frequency"), rule.get("window")
        if frequency not in ALERT_MONTHLY_USD:
            failures.append(f"{rule.get('name')} evaluationFrequency {frequency} must be one of {sorted(ALERT_MONTHLY_USD)}")
            continue
        monthly += ALERT_MONTHLY_USD[frequency]
        window_minutes = _iso_minutes(window)
        if window_minutes is None or window_minutes < (_iso_minutes(frequency) or 0) or window_minutes > 24 * 60:
            failures.append(f"{rule.get('name')} windowSize {window} must be at least its frequency and at most one day")
    if monthly > ALERT_BUDGET_USD:
        failures.append(f"alert rules cost {monthly:.2f} USD per month, above the {ALERT_BUDGET_USD:.2f} USD OBS-002 bound (PILOT-BUDGET-100USD)")

    text = json.dumps(template.get("variables", {}))
    tables = set(re.findall(r"\b([A-Za-z]+_CL)\b", text))
    if not tables or not tables <= OBSERVABILITY_TABLES:
        failures.append(f"queries may read only {sorted(OBSERVABILITY_TABLES)}, found {sorted(tables)}")
    properties_read = set(re.findall(r"\be\.State\.([A-Za-z0-9_]+)", text))
    if not properties_read or not properties_read <= OBSERVABILITY_STATE_PROPERTIES:
        failures.append(f"queries read structured-log properties outside the OBS-002 allowlist: {sorted(properties_read - OBSERVABILITY_STATE_PROPERTIES)}")
    if "observability-check" not in ctx.workflow_text or "observability.parameters.json" not in ctx.workflow_text:
        failures.append("workflow must check and deploy observability.parameters.json")
    return _result(21, "OBS-002 alerts, e-mail action group and cost bound; OBS-003 Application Insights; SCL-007 PostgreSQL metric alerts",
                   failures, f"{len(rules)} log alert rules and {series} PostgreSQL metric alerts (connections 28 Sev 2 and 33 Sev 1; "
                   f"CPU 70, memory 80, storage 70 and CPU credits 72, Sev 2) at most {monthly:.2f} USD/month, e-mail only; "
                   "workspace-based Application Insights")


# ---------------------------------------------------------------------------------------------- UI-001 web
# Accepted terms/privacy notice versions on the web container: apps.bicep parameter -> web env variable.
# The values are owner decisions (they come with the approved privacy notice, GATE-007) kept in
# web.parameters.json; the format is AI-05 CreateOrderRequest.acceptance terms_version/privacy_version.
WEB_VERSION_PARAMETERS = {
    "webTermsVersion": "PAQUETERIA_TERMS_VERSION",
    "webPrivacyVersion": "PAQUETERIA_PRIVACY_VERSION",
}
ACCEPTANCE_VERSION = re.compile(r"^[A-Za-z0-9._-]{1,64}\Z")


def guard_22_web_acceptance_versions(ctx: Context) -> GuardResult:
    """UI-001: the web container receives both versions from apps.bicep parameters without a default
    (1-64 chars), only the web receives them, and the workflow checks and deploys web.parameters.json."""
    failures: list[str] = []
    parameters = ctx.templates.get("apps", {}).get("parameters", {}) or {}
    web = ctx.workload(WEB_APP)
    for parameter, variable in WEB_VERSION_PARAMETERS.items():
        definition = parameters.get(parameter)
        if (not isinstance(definition, dict) or definition.get("type") != "string" or "defaultValue" in definition
                or definition.get("minLength") != 1 or definition.get("maxLength") != 64):
            failures.append(f"apps.bicep must take {parameter} as a string parameter (minLength 1, maxLength 64) without a default")
        if web.env.get(variable) != ("value", f"[parameters('{parameter}')]"):
            failures.append(f"web must set {variable} from parameters('{parameter}'), found {web.env.get(variable)}")
        for workload in ctx.workloads:
            if workload.name != WEB_APP and variable in workload.env:
                failures.append(f"{workload.name} must not receive {variable} (web only)")
    try:
        document = json.loads((ctx.repo_root / WEB_PARAMETERS_FILE).read_text(encoding="utf-8"))
        failures.extend(validate_web_parameters(document, allow_sentinel=True))
    except (OSError, ValueError) as error:
        failures.append(f"{WEB_PARAMETERS_FILE}: {error}")
    for needle in ("web-check --file tested/deploy/azure/pilot/web.parameters.json",
                   "web-check --file deploy/azure/pilot/web.parameters.json",
                   '"@deploy/azure/pilot/web.parameters.json"'):
        if needle not in ctx.workflow_text:
            failures.append(f"workflow must check and deploy web.parameters.json ({needle})")
    return _result(22, "UI-001 accepted terms/privacy versions on the web", failures,
                   "web versions bound to owner parameters without defaults")


GUARD_TOOLS = ("env001_pilot_guards.py", "azr001_static_guards.py")
PYYAML_INSTALL = re.compile(r"pip install\s+PyYAML==6\.0\.3\b")


# RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12 ("Rol propio y mínimo"): the API hosts the realtime outbox dispatchers
# in process (RTM-002-INPROCESS-DISPATCHER-COLOCATION) with their own least-privilege login, never the Worker's.
REALTIME_CONNECTION_KEY = "ConnectionStrings:PaqueteriaRealtime"
REALTIME_SECRET = "pg-realtime-runtime-connection"
REALTIME_LOGIN_ROLE = "realtime"
RUNTIME_LOGIN_LOOP = re.compile(r"^\s*for role in ([a-z ]+); do\s*$", re.MULTILINE)


def guard_23_realtime_dispatcher_credential(ctx: Context) -> GuardResult:
    """The API's realtime outbox dispatchers read their own Key Vault connection (a login that is a member of
    paqueteria_realtime only), the API never maps the Worker connection or any Worker secret, and the workflow
    generates the realtime login and its verifier like the API and Worker logins."""
    failures = []
    api = ctx.workload(API_APP)
    mappings = secret_mappings(api)
    if isinstance(mappings, str):
        return _result(23, "API realtime dispatcher credential", [f"{API_APP}: {mappings}"], "")
    dispatcher = api.value("Realtime__OutboxDispatcher__Provider")
    if dispatcher not in ("PostgreSql", "Disabled"):
        failures.append(f"api must set Realtime__OutboxDispatcher__Provider to PostgreSql or Disabled, found {dispatcher}")
    if dispatcher == "PostgreSql" and mappings.get(REALTIME_CONNECTION_KEY) != REALTIME_SECRET:
        failures.append(
            f"api runs the realtime outbox dispatchers but does not map {REALTIME_CONNECTION_KEY} from {REALTIME_SECRET}")
    if "ConnectionStrings:PaqueteriaWorker" in mappings:
        failures.append("api maps ConnectionStrings:PaqueteriaWorker; the Worker credential must never reach the API")
    # OBS-003-TENANT-PSEUDONYM-2026-10-12: both hosts read the organization pseudonym key (not a credential; both must
    # derive the same pseudonyms). Every other Worker secret stays out of the API.
    worker_secrets = set(REQUIRED_SECRET_MAPPINGS[WORKER_APP].values()) - {TELEMETRY_PSEUDONYM_SECRET}
    shared = sorted(worker_secrets & set(mappings.values()))
    if shared:
        failures.append(f"api maps Worker secrets {shared}")
    for workload in ctx.workloads:
        other = secret_mappings(workload)
        if workload.name != API_APP and isinstance(other, dict) and REALTIME_SECRET in other.values():
            failures.append(f"{workload.name} maps {REALTIME_SECRET}; only the API's realtime dispatchers read it")
    loops = RUNTIME_LOGIN_LOOP.findall(ctx.workflow_text)
    if len(loops) != 1 or set(loops[0].split()) != {"api", "worker", REALTIME_LOGIN_ROLE}:
        failures.append(f"the workflow must generate exactly the api, worker and realtime runtime logins, found {loops}")
    if '"pg-${role}-runtime-connection"' not in ctx.workflow_text or '"pg-${role}-login-verifier"' not in ctx.workflow_text:
        failures.append("the workflow no longer writes pg-<role>-runtime-connection and pg-<role>-login-verifier")
    return _result(23, "API realtime dispatcher credential (RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12)", failures,
                   "own Key Vault connection, no Worker credential, login generated by the workflow")


# ---------------------------------------------------------------------------------------------- AC-064
# AZR-001 v0.8 AZR-001-AC-064 (image hardening): "Non-root cuando soporte runtime. Root requiere waiver." No waiver is
# recorded, so the final stage of every image the pilot workflow builds must switch to a non-root USER.
PILOT_DOCKERFILES = (
    "deploy/azure/Dockerfile.api",
    "deploy/azure/Dockerfile.worker",
    "deploy/azure/pilot/Dockerfile.db-ops",
    "deploy/azure/pilot/Dockerfile.web",
)
WORKFLOW_IMAGE_BUILD = re.compile(r"\$\(build\s+[a-z0-9-]+\s+([^\s)]+)")
# The official .NET images define APP_UID as the uid of their non-root `app` user. It is accepted only on such a base
# and only while no ARG or ENV in the Dockerfile could redefine it.
DOTNET_IMAGE_PREFIX = "mcr.microsoft.com/dotnet/"
DOTNET_APP_UID_REFERENCES = ("$APP_UID", "${APP_UID}")
APP_UID_DECLARATION = re.compile(r"(?:^|\s)APP_UID(?:=|\s|$)")
ESCAPE_DIRECTIVE = re.compile(r"^#\s*escape\s*=", re.IGNORECASE)


def dockerfile_instructions(text: str) -> list[tuple[str, str]]:
    """`(INSTRUCTION, arguments)` per Dockerfile instruction: comments and blank lines skipped (also inside a
    continuation, as Docker does) and backslash continuations joined."""
    instructions: list[tuple[str, str]] = []
    pending = ""
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.endswith("\\"):
            pending += line[:-1].rstrip() + " "
            continue
        instructions.append(_instruction(pending + line))
        pending = ""
    if pending.strip():
        instructions.append(_instruction(pending))
    return instructions


def _instruction(line: str) -> tuple[str, str]:
    parts = line.split(None, 1)
    return parts[0].upper(), (parts[1].strip() if len(parts) > 1 else "")


def dockerfile_non_root_failure(name: str, text: str) -> str | None:
    """Why the final stage of this Dockerfile would run as root, or None when its last USER is non-root."""
    if any(ESCAPE_DIRECTIVE.match(line.strip()) for line in text.splitlines()):
        return f"{name} changes the escape character; the guard cannot read it"
    instructions = dockerfile_instructions(text)
    starts = [index for index, (keyword, _) in enumerate(instructions) if keyword == "FROM"]
    if not starts:
        return f"{name} has no FROM"
    stage = instructions[starts[-1]:]
    base = next((part for part in stage[0][1].split() if not part.startswith("--")), "")
    users = [arguments for keyword, arguments in stage if keyword == "USER"]
    if not users:
        return f"{name} final stage has no USER, so it runs as root"
    user = users[-1].split(":", 1)[0].strip()
    if user in DOTNET_APP_UID_REFERENCES:
        if not base.startswith(DOTNET_IMAGE_PREFIX):
            return f"{name} uses USER {user} on {base or 'an unknown base'}, which is not an official .NET image"
        if any(keyword in ("ARG", "ENV") and APP_UID_DECLARATION.search(arguments) for keyword, arguments in instructions):
            return f"{name} redeclares APP_UID, so USER {user} could resolve to root"
        return None
    if not user or "$" in user:
        return f"{name} final USER '{user}' cannot be resolved statically"
    if user.lower() == "root" or (user.isdigit() and int(user) == 0):
        return f"{name} final stage switches back to root (USER {users[-1]})"
    return None


def pilot_image_user_failures(repo_root: Path, workflow_text: str) -> list[str]:
    failures = []
    built = WORKFLOW_IMAGE_BUILD.findall(workflow_text)
    if sorted(built) != sorted(PILOT_DOCKERFILES):
        failures.append(f"the workflow must build exactly the images {list(PILOT_DOCKERFILES)}, found {built}")
    for name in PILOT_DOCKERFILES:
        path = repo_root / name
        if not path.is_file():
            failures.append(f"{name} is missing")
            continue
        failure = dockerfile_non_root_failure(name, path.read_text(encoding="utf-8"))
        if failure:
            failures.append(failure)
    return failures


def guard_24_non_root_images(ctx: Context) -> GuardResult:
    return _result(24, "non-root images (AZR-001-AC-064)", pilot_image_user_failures(ctx.repo_root, ctx.workflow_text),
                   "the final stage of each pilot image runs as a non-root USER")


# ---------------------------------------------------------------------------------------------- OPS-002 (P25)
RESTORE_DRILL_SCRIPT = PILOT_DIR / "restore-drill.sh"
POSTGRES_SERVER_VARIABLE_ENV = f"${{{{ vars.{POSTGRES_SERVER_VARIABLE} }}}}"
POSTGRES_NAME_OUTPUT_ENV = "${{ steps.platform.outputs.postgresName }}"
POSTGRES_HOST_OUTPUT_ENV = "${{ steps.platform.outputs.postgresHost }}"
RUNTIME_LOGINS_CHANGED_ENV = "${{ steps.connections.outputs.runtime_logins_changed }}"


def _step_index(steps: list[dict[str, Any]], predicate: Callable[[dict[str, Any]], bool]) -> list[int]:
    return [i for i, step in enumerate(steps) if predicate(step)]


def guard_25_postgres_server_setting(ctx: Context) -> GuardResult:
    """OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20: one setting names the PostgreSQL server and everything follows it. Both
    templates take `postgresServerName` with exactly the first server's name as default; platform.bicep names its only
    server and its outputs from it; observability.bicep watches it; no template spells the name elsewhere. The deploy job
    reads PILOT_POSTGRES_SERVER_NAME once, validates it before Azure, refuses a named server that does not exist or
    cannot be adopted before stage 2, passes it to platform.bicep and the platform output to observability.bicep,
    regenerates a kept runtime credential whose host is not the current one, and restarts the apps on a changed
    credential. restore-drill.sh restores the server in service and checks its name with the same pattern."""
    failures: list[str] = []
    for name in ("platform", "observability"):
        template = ctx.templates.get(name, {})
        parameter = (template.get("parameters", {}) or {}).get(POSTGRES_SERVER_PARAMETER)
        if not isinstance(parameter, dict) or parameter.get("type") != "string" or parameter.get("defaultValue") != POSTGRES_SERVER_DEFAULT \
                or parameter.get("minLength") != 3 or parameter.get("maxLength") != POSTGRES_SERVER_NAME_MAX:
            failures.append(f"{name}.bicep must take {POSTGRES_SERVER_PARAMETER} as a 3-63 character string whose default is "
                            f"exactly the first server, {POSTGRES_SERVER_DEFAULT}")
    # The compiled ARM keeps only declared resources; observability.bicep's `existing` server shows up in the scopes of
    # its metric alerts, which P21 pins to POSTGRES_SERVER_ID.
    servers = [r for _, r in ctx.resources(POSTGRES_SERVER_TYPE)]
    if len(servers) != 1 or servers[0].get("name") != POSTGRES_SERVER_NAME_EXPRESSION \
            or [t for t, _ in ctx.resources(POSTGRES_SERVER_TYPE)] != ["platform"]:
        failures.append(f"platform.bicep must declare the only PostgreSQL server, named {POSTGRES_SERVER_NAME_EXPRESSION}")
    for name, template in sorted(ctx.templates.items()):
        count = json.dumps(template).count(POSTGRES_SERVER_PREFIX)
        if count != (1 if name in ("platform", "observability") else 0):
            failures.append(f"{name}.bicep spells {POSTGRES_SERVER_PREFIX}... {count} time(s); only the "
                            f"{POSTGRES_SERVER_PARAMETER} default of platform.bicep and observability.bicep may")
    outputs = ctx.templates.get("platform", {}).get("outputs", {}) or {}
    if (outputs.get("postgresName", {}) or {}).get("value") != POSTGRES_SERVER_NAME_EXPRESSION \
            or (outputs.get("postgresHost", {}) or {}).get("value") != POSTGRES_HOST_OUTPUT:
        failures.append(f"platform.bicep must output postgresName and postgresHost of the server {POSTGRES_SERVER_NAME_EXPRESSION}")

    deploy = (ctx.workflow.get("jobs", {}) or {}).get("deploy", {}) or {}
    steps = [s for s in deploy.get("steps", []) or [] if isinstance(s, dict)]
    run = lambda step: str(step.get("run", ""))  # noqa: E731
    env = lambda step: step.get("env", {}) or {}  # noqa: E731
    if (deploy.get("env", {}) or {}).get(POSTGRES_SERVER_VARIABLE) != POSTGRES_SERVER_VARIABLE_ENV \
            or ctx.workflow_text.count(f"vars.{POSTGRES_SERVER_VARIABLE}") != 1:
        failures.append(f"the deploy job must read {POSTGRES_SERVER_VARIABLE} once, into its env, from {POSTGRES_SERVER_VARIABLE_ENV}")
    login = _step_index(steps, lambda s: "azure/login" in str(s.get("uses", "")))
    validate = _step_index(steps, lambda s: f'postgres-server-name --value "${{{POSTGRES_SERVER_VARIABLE}:-}}"' in run(s))
    adopt = _step_index(steps, lambda s: "postgres-adoption-check" in run(s) and "az resource show" in run(s))
    platform = _step_index(steps, lambda s: '"${RUNNER_TEMP}/arm/platform.json"' in run(s) and "az deployment group create" in run(s))
    prepare = _step_index(steps, lambda s: "platform.parameters.json" in run(s) and "postgresAdministratorPassword" in run(s))
    if len(validate) != 1 or not login or validate[0] > login[0]:
        failures.append(f"the deploy job must validate {POSTGRES_SERVER_VARIABLE} with postgres-server-name before azure/login")
    if len(platform) != 1 or len(adopt) != 1 or adopt[0] > platform[0]:
        failures.append("the deploy job must check the named server with az resource show and postgres-adoption-check before stage 2")
    else:
        server = next((r for r in ctx.templates.get("platform", {}).get("resources", []) or []
                       if isinstance(r, dict) and r.get("type") == POSTGRES_SERVER_TYPE), {})
        if f"--api-version {server.get('apiVersion')}" not in run(steps[adopt[0]]):
            failures.append(f"the adoption check must read the server with the template's API version {server.get('apiVersion')}")
        if f'"{POSTGRES_SERVER_PARAMETER}"' not in (run(steps[prepare[0]]) if len(prepare) == 1 else "") \
                or POSTGRES_SERVER_VARIABLE not in (run(steps[prepare[0]]) if len(prepare) == 1 else ""):
            failures.append(f"the platform parameters must carry {POSTGRES_SERVER_PARAMETER} from {POSTGRES_SERVER_VARIABLE}")
        if 'postgresName' not in run(steps[platform[0]]) or '"${name}.postgres.database.azure.com"' not in run(steps[platform[0]]):
            failures.append("stage 2 must export postgresName and check that postgresHost belongs to it")
    observability = _step_index(steps, lambda s: '"${RUNNER_TEMP}/arm/observability.json"' in run(s))
    if len(observability) != 1 or env(steps[observability[0]]).get("POSTGRES_NAME") != POSTGRES_NAME_OUTPUT_ENV \
            or f'"{POSTGRES_SERVER_PARAMETER}=${{POSTGRES_NAME}}"' not in run(steps[observability[0]]):
        failures.append(f"stage 5 must pass {POSTGRES_SERVER_PARAMETER}=<platform postgresName output> to observability.bicep")
    connections = [s for s in steps if s.get("id") == "connections"]
    if len(connections) != 1 or env(connections[0]).get("PGHOST_FQDN") != POSTGRES_HOST_OUTPUT_ENV \
            or "connection-host" not in run(connections[0]) or 'runtime_logins_changed=' not in run(connections[0]) \
            or '"${kept_host}" == "${PGHOST_FQDN}"' not in run(connections[0]):
        failures.append("the connections step (id connections) must keep a runtime credential only while its host is "
                        "the platform postgresHost output, and report runtime_logins_changed")
    restart = _step_index(steps, lambda s: "az containerapp revision restart" in run(s))
    jobs = _step_index(steps, lambda s: "job-pv-pilot-logins" in run(s) and '"${RUNNER_TEMP}/arm/jobs.json"' in run(s))
    apps = _step_index(steps, lambda s: '"${RUNNER_TEMP}/arm/apps.json"' in run(s))
    if len(restart) != 1 or env(steps[restart[0]]).get("RUNTIME_LOGINS_CHANGED") != RUNTIME_LOGINS_CHANGED_ENV \
            or "az containerapp revision activate" not in run(steps[restart[0]]):
        failures.append("one step must start a stopped API or Worker revision and restart a running one when "
                        "runtime_logins_changed")
    elif len(jobs) != 1 or len(apps) != 1 or not jobs[0] < restart[0] < apps[0]:
        failures.append("the start-or-restart step must run after the logins job (stage 3) and before stage 4")
    try:
        drill = (ctx.repo_root / RESTORE_DRILL_SCRIPT).read_text(encoding="utf-8")
    except OSError:
        drill = ""
    if 'SOURCE="$(jq -r .postgresName.value <<<"${platform}")"' not in drill or POSTGRES_SERVER_NAME_PATTERN not in drill:
        failures.append(f"{RESTORE_DRILL_SCRIPT} must restore the platform postgresName output and check it with {POSTGRES_SERVER_NAME_PATTERN}")
    return _result(25, f"PostgreSQL server follows {POSTGRES_SERVER_VARIABLE} (OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20)", failures,
                   "one parameter with the first server as default; outputs, alerts, credentials, jobs and drills follow it")


# ---------------------------------------------------------------------------------------------- ENV-001 F-06 (P26)
REVISION_IMAGE_ENV = {
    "API_IMAGE": "${{ steps.images.outputs.api }}",
    "WORKER_IMAGE": "${{ steps.images.outputs.worker }}",
    "WEB_IMAGE": "${{ steps.images.outputs.web }}",
}
REVISION_CHECK_CALLS = (
    f'check_revision api_revision {API_APP} "${{API_IMAGE}}"',
    f'check_revision worker_revision {WORKER_APP} "${{WORKER_IMAGE}}"',
    f'check_revision web_revision {WEB_APP} "${{WEB_IMAGE}}"',
)
# A ready revision on another image is not yet this run's revision: it is polled again within the same budget.
REVISION_IMAGE_MISMATCH = 'if [[ "${images}" != "${image}" ]]; then'
REVISION_IMAGE_NOT_YET = "status=\"revision ${ready} is ready but runs '${images}', not this run's image (${image})\""
REVISION_HEALTHY = 'elif [[ "${active}" == "true" && "${health}" == "Healthy" ]]; then'
# What the check reads and asserts; a bounded poll, and the system-log hint on every failure.
REVISION_CHECK_FRAGMENTS = (
    "{latest: properties.latestRevisionName, ready: properties.latestReadyRevisionName}",
    "{active: properties.active, health: properties.healthState, images: properties.template.containers[].image}",
    '[[ -n "${latest}" && "${latest}" == "${ready}" ]]',
    REVISION_IMAGE_MISMATCH,
    REVISION_IMAGE_NOT_YET,
    REVISION_HEALTHY,
    "polls_left=40\n",
    "(( polls_left == 0 ))",
    "sleep 15",
    "az containerapp logs show --name $1 --resource-group ${PILOT_RESOURCE_GROUP} --type system",
    '"${image##*@}" =~ ^sha256:[0-9a-f]{64}$',
)
SUMMARY_ENV = {
    **REVISION_IMAGE_ENV,
    "DBOPS_IMAGE": "${{ steps.images.outputs.dbops }}",
    "API_REVISION": "${{ steps.revisions.outputs.api_revision }}",
    "WORKER_REVISION": "${{ steps.revisions.outputs.worker_revision }}",
    "WEB_REVISION": "${{ steps.revisions.outputs.web_revision }}",
}


def guard_26_revision_check(ctx: Context) -> GuardResult:
    """ENV-001 F-06: in single revision mode Container Apps keeps the previous revision serving while a new one never
    becomes ready, so a green stage 4 and smoke test can hide old code. After stage 4 and before the smoke test, one
    unconditional step (id revisions) must check, for the API, the Worker and the Web, that the latest revision is ready,
    runs this run's digest, and is active and Healthy, with a bounded poll and the system-log hint on failure. A ready
    revision on another image is polled again within the same budget (right after stage 4 the control plane can still
    report the previous revision as the latest one), never failed at once. The always-run summary lists the four
    digests (F-05) and the serving revisions."""
    failures: list[str] = []
    deploy = (ctx.workflow.get("jobs", {}) or {}).get("deploy", {}) or {}
    steps = [s for s in deploy.get("steps", []) or [] if isinstance(s, dict)]
    run = lambda step: str(step.get("run", ""))  # noqa: E731
    env = lambda step: step.get("env", {}) or {}  # noqa: E731
    checks = _step_index(steps, lambda s: s.get("id") == "revisions")
    apps = _step_index(steps, lambda s: '"${RUNNER_TEMP}/arm/apps.json"' in run(s))
    smoke = _step_index(steps, lambda s: "Same-origin routing smoke test passed" in run(s))
    if len(checks) != 1:
        failures.append("one deploy step (id revisions) must check the new API, Worker and Web revisions")
    else:
        step = steps[checks[0]]
        text = run(step)
        if env(step) != REVISION_IMAGE_ENV:
            failures.append(f"the revision check must read exactly this run's images {REVISION_IMAGE_ENV}")
        missing = [fragment for fragment in REVISION_CHECK_FRAGMENTS if fragment not in text]
        if missing:
            failures.append(f"the revision check must poll, assert and hint with {missing}")
        elif text.count(REVISION_IMAGE_MISMATCH) != 1 or text.count(REVISION_HEALTHY) != 1 or \
                re.search(r"\breturn\b|\bfail\b|\bexit\b",
                          text[text.index(REVISION_IMAGE_MISMATCH):text.index(REVISION_HEALTHY)]):
            failures.append("a ready revision on another image must be polled again, not failed at once")
        lines = [line.strip() for line in text.splitlines()]
        if [line for line in lines if line.startswith("check_revision ")] != list(REVISION_CHECK_CALLS):
            failures.append(f"the revision check must end with {list(REVISION_CHECK_CALLS)}")
        if "if" in step or "continue-on-error" in step or not text.startswith("set -euo pipefail\n"):
            failures.append("the revision check must always run, with set -euo pipefail, and fail the run")
        if len(apps) != 1 or len(smoke) != 1 or not apps[0] < checks[0] < smoke[0]:
            failures.append("the revision check must run after stage 4 and before the smoke test")
    summary = [s for s in steps if "GITHUB_STEP_SUMMARY" in run(s)]
    if len(summary) != 1 or summary[0].get("if") != "always()" \
            or any(env(summary[0]).get(name) != value for name, value in SUMMARY_ENV.items()) \
            or any(f"${{{name}:-" not in run(summary[0]) for name in SUMMARY_ENV):
        failures.append("the always-run summary must list the four image digests and the serving revisions")
    return _result(26, "new revisions serve this run's digests (ENV-001 F-06)", failures,
                   "after stage 4 the API, Worker and Web revisions are ready, active, Healthy and on this run's digests")


def guard_20_guard_tool_dependencies(ctx: Context) -> GuardResult:
    """Every job that runs a guard tool (both import PyYAML at module scope) installs the pinned PyYAML
    with actions/setup-python first, in the same job."""
    failures = []
    invoking_jobs = 0
    for name, job in (ctx.workflow.get("jobs", {}) or {}).items():
        steps = [s for s in job.get("steps", []) or [] if isinstance(s, dict)]
        python_ready = False
        yaml_ready = False
        uses_tool = False
        for step in steps:
            uses = str(step.get("uses", ""))
            run = str(step.get("run", ""))
            if uses.startswith("actions/setup-python@"):
                python_ready = True
            if PYYAML_INSTALL.search(run):
                yaml_ready = python_ready
            if any(tool in run for tool in GUARD_TOOLS):
                uses_tool = True
                if not yaml_ready:
                    failures.append(f"job {name} step '{step.get('name')}' runs a guard tool before actions/setup-python + pip install PyYAML==6.0.3")
                    break
        invoking_jobs += uses_tool
    if invoking_jobs == 0:
        failures.append("no job runs the pilot guard tooling")
    return _result(20, "guard tooling dependencies installed per job", failures, f"{invoking_jobs} job(s) install PyYAML before running guard tools")


GUARDS: tuple[Callable[[Context], GuardResult], ...] = (
    guard_00_templates,
    guard_01_resource_set,
    guard_02_dispatch_only,
    guard_03_environment,
    guard_04_provenance,
    guard_05_images,
    guard_06_secrets,
    guard_07_production_classification,
    guard_08_single_replica_inprocess,
    guard_09_migrations,
    guard_10_same_origin_routing,
    guard_11_private_postgres,
    guard_12_storage,
    guard_13_key_vault,
    guard_14_rbac,
    guard_15_budget_and_logs,
    guard_16_adp_contract,
    guard_17_business_settings,
    guard_18_bff_web_image,
    guard_19_cleanups_enabled,
    guard_20_guard_tool_dependencies,
    guard_21_observability,
    guard_22_web_acceptance_versions,
    guard_23_realtime_dispatcher_credential,
    guard_24_non_root_images,
    guard_25_postgres_server_setting,
    guard_26_revision_check,
)


def run_guards(ctx: Context) -> list[GuardResult]:
    results = []
    for guard in GUARDS:
        try:
            results.append(guard(ctx))
        except GuardError as error:
            results.append(GuardResult(-1, guard.__name__, "BLOCKED", str(error)))
        except Exception as error:  # noqa: BLE001 - an evaluation failure is BLOCKED, never a silent PASS
            results.append(GuardResult(-1, guard.__name__, "BLOCKED", f"{type(error).__name__}: {error}"))
    return results


# ---------------------------------------------------------------------------------------------- settings
def load_settings(path: Path) -> list[dict[str, Any]]:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise GuardError(f"{path}: {error}") from error
    if not isinstance(document, list):
        raise GuardError(f"{path} must be a JSON array of {{name, value}} objects")
    return document


def setting_label(position: int, entry: Any) -> str:
    """How a failure names a setting without echoing anything unverified (DRV-DISPATCH-CONTACT-2026-10-12): the name
    only when it is a well-formed .NET key that cannot hold a value (a dispatch contact name only in its exact
    canonical form, whose index has at most three digits); otherwise the entry's 1-based position in the file. A
    value is never part of it."""
    name = entry.get("name") if isinstance(entry, dict) else None
    if (isinstance(name, str) and SETTING_NAME.match(name) and
            (not is_dispatch_contact_name(name) or DISPATCH_CONTACT_SETTING.match(name))):
        return f"setting {name}"
    return f"setting #{position + 1}"


def validate_settings(entries: list[Any], *, allow_sentinel: bool) -> list[str]:
    failures = []
    seen: set[str] = set()
    platform_prefixes = tuple(prefix.casefold() for prefix in PLATFORM_MANAGED_PREFIXES)
    removed = {name.casefold(): reason for name, reason in REMOVED_SETTINGS.items()}
    for position, entry in enumerate(entries):
        label = setting_label(position, entry)
        if not isinstance(entry, dict) or set(entry) != {"name", "value"} or not all(isinstance(entry[k], str) for k in entry):
            # Never the entry itself: a mistyped value (a JSON number, an extra member) could be a phone number.
            failures.append(f"{label} must be exactly {{name, value}} strings")
            continue
        name, value = entry["name"], entry["value"]
        # .NET configuration keys are case-insensitive, so every name rule compares names case-insensitively.
        folded = name.casefold()
        if not SETTING_NAME.match(name):
            failures.append(f"{label} name is not a .NET double-underscore key")
        if folded.startswith(platform_prefixes):
            failures.append(f"{label} is platform-managed by apps.bicep and cannot be overridden")
        if folded in removed:
            failures.append(f"{label} was removed by {removed[folded]}")
        if folded in seen:
            failures.append(f"{label} declared twice (configuration names ignore case)")
        seen.add(folded)
        if is_forbidden_value(value):
            failures.append(f"{label} has a mock/synthetic value ({value.strip()})")
        if is_owner_sentinel(value) and not allow_sentinel:
            failures.append(f"{label} still awaits an owner decision ({OWNER_SENTINEL})")
        if re.search(r"(?i)(password|secret|token|key)", name):
            failures.append(f"{label} looks like a secret; secrets belong in Key Vault")
    failures.extend(validate_dispatch_contacts(entries))
    failures.extend(validate_driver_eligibility(entries, allow_missing=allow_sentinel))
    return failures


# DRV-DISPATCH-CONTACT-2026-10-12 ("Sí, configurable (Recommended)"): the dispatch number of an organization is an
# owner business setting, empty by default (no entry: the driver app shows no "Llamar a despacho"). The API refuses
# to start with an invalid entry, so the pilot checks the same rules before deploying. A failure never echoes a value.
DISPATCH_CONTACT_PREFIX = "Drivers__DispatchContacts"
DISPATCH_CONTACT_SETTING = re.compile(r"^Drivers__DispatchContacts__(0|[1-9][0-9]{0,2})__(OrganizationId|PhoneE164)\Z")
# The same names in any casing, as .NET binds them; only the exact canonical spelling above is accepted.
DISPATCH_CONTACT_SETTING_ANY_CASE = re.compile(
    r"^Drivers__DispatchContacts__(0|[1-9][0-9]{0,2})__(OrganizationId|PhoneE164)\Z", re.IGNORECASE)
DISPATCH_CONTACT_MEMBERS = {"organizationid": "OrganizationId", "phonee164": "PhoneE164"}
CANONICAL_UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\Z")
DISPATCH_PHONE_E164 = re.compile(r"^\+52[2-9][0-9]{9}\Z")


def is_dispatch_contact_name(name: str) -> bool:
    """Whether .NET would bind the name under Drivers:DispatchContacts (configuration keys ignore case)."""
    folded, prefix = name.casefold(), DISPATCH_CONTACT_PREFIX.casefold()
    return folded == prefix or folded.startswith(prefix + "__")


def validate_dispatch_contacts(entries: list[Any]) -> list[str]:
    """Drivers__DispatchContacts__<n>__OrganizationId and __PhoneE164, spelled exactly so: both for every n,
    n = 0, 1, 2... with no gap, a canonical lowercase UUID (never the nil one), +52 and ten digits starting with 2-9,
    each organization once. A name in another casing is refused (and still checked), since .NET binds it anyway. No
    failure echoes a value or an unverified name."""
    contacts: dict[int, dict[str, str | None]] = {}
    failures: list[str] = []
    for position, entry in enumerate(entries):
        if not isinstance(entry, dict) or not isinstance(entry.get("name"), str):
            continue
        name = entry["name"]
        if not is_dispatch_contact_name(name):
            continue
        label = setting_label(position, entry)
        match = DISPATCH_CONTACT_SETTING_ANY_CASE.match(name)
        if match is None:
            failures.append(f"{label} is not Drivers__DispatchContacts__<n>__OrganizationId or __PhoneE164")
            continue
        if DISPATCH_CONTACT_SETTING.match(name) is None:
            failures.append(
                f"{label} must be spelled exactly Drivers__DispatchContacts__<n>__OrganizationId or __PhoneE164 "
                "(configuration names ignore case, so another casing still reaches the API)")
        value = entry.get("value")
        # A value that is not a string is reported by the shape check; here it simply counts as missing.
        member = DISPATCH_CONTACT_MEMBERS[match.group(2).casefold()]
        contacts.setdefault(int(match.group(1)), {})[member] = value if isinstance(value, str) else None
    if sorted(contacts) != list(range(len(contacts))):
        failures.append("Drivers__DispatchContacts entries must be numbered 0, 1, 2... without gaps")
    organizations: set[str] = set()
    for index in sorted(contacts):
        contact = contacts[index]
        organization = contact.get("OrganizationId")
        if organization is None or not CANONICAL_UUID.match(organization) or set(organization) <= {"0", "-"}:
            failures.append(f"Drivers__DispatchContacts__{index}__OrganizationId must be a canonical lowercase UUID")
        elif organization in organizations:
            failures.append(f"Drivers__DispatchContacts__{index}__OrganizationId names an organization twice")
        else:
            organizations.add(organization)
        if contact.get("PhoneE164") is None or not DISPATCH_PHONE_E164.match(contact["PhoneE164"]):
            failures.append(
                f"Drivers__DispatchContacts__{index}__PhoneE164 must be +52 and ten digits, the first one 2-9")
    return failures


# Driver eligibility rules. apps.bicep runs the pilot API with Drivers__Provider=PostgreSql and
# Routing__Provider=PostgreSql, so the API refuses to start unless Drivers:Eligibility gives every vehicle type at
# least one required document type and a valid capacity (IsComplete in Drivers.Infrastructure and
# Routing.Infrastructure DependencyInjection; PilotApiStartupConfigurationTests pins both failures without them). They
# are owner business rules with no default (the file carries the owner's since
# GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20): settings-check stops while any is missing, naming the vehicle type
# and each missing setting, and holds every entry present to the API's rules. The static guard (P17, allow_missing)
# tolerates the missing ones, like OWNER_DECISION_REQUIRED, but not an invalid one.
ELIGIBILITY_PREFIX = "Drivers__Eligibility"
ELIGIBILITY_DOCUMENTS = "RequiredDocumentTypesByVehicleType"
ELIGIBILITY_NON_EXPIRING = "NonExpiringDocumentTypes"
ELIGIBILITY_CAPACITY = "VehicleCapacity"
# The C# validators' VehicleTypes and DocumentTypes (test_env001_pilot_guards.py reads both validators to compare).
ELIGIBILITY_VEHICLE_TYPES = ("MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER")
ELIGIBILITY_DOCUMENT_TYPES = ("IDENTITY", "DRIVER_LICENSE", "VEHICLE_CARD", "INSURANCE", "BACKGROUND_CHECK", "OTHER")
# VehicleCapacityOptions: each one required, above zero and within its .NET type (int or long); the single package
# weight may not exceed the total weight.
ELIGIBILITY_CAPACITY_FIELDS = {
    "MaximumPackageCount": 2**31 - 1,
    "MaximumTotalWeightGrams": 2**63 - 1,
    "MaximumSinglePackageWeightGrams": 2**63 - 1,
    "MaximumLengthMillimeters": 2**31 - 1,
    "MaximumWidthMillimeters": 2**31 - 1,
    "MaximumHeightMillimeters": 2**31 - 1,
}
# Optional bool (false when absent): refuse a package without dimensions for that vehicle type.
ELIGIBILITY_REQUIRE_DIMENSIONS = "RequireDimensions"
ELIGIBILITY_LIST_INDEX = re.compile(r"^(0|[1-9][0-9]{0,2})\Z")
ELIGIBILITY_POSITIVE_INTEGER = re.compile(r"^[1-9][0-9]*\Z")


def is_eligibility_name(name: str) -> bool:
    """Whether .NET would bind the name under Drivers:Eligibility (configuration keys ignore case)."""
    folded, prefix = name.casefold(), ELIGIBILITY_PREFIX.casefold()
    return folded == prefix or folded.startswith(prefix + "__")


def _member(segment: str, members: Any) -> str | None:
    """The member a name segment binds to (.NET property names ignore case), or None."""
    return next((member for member in members if member.casefold() == segment.casefold()), None)


def validate_driver_eligibility(entries: list[Any], *, allow_missing: bool) -> list[str]:
    """Drivers__Eligibility__RequiredDocumentTypesByVehicleType__<TYPE>__<n> and the optional
    Drivers__Eligibility__NonExpiringDocumentTypes__<n> (each a document type), and
    Drivers__Eligibility__VehicleCapacity__<TYPE>__<field>, spelled exactly so, for the five vehicle types only.
    Unless allow_missing, every vehicle type needs at least one document type and the six capacity numbers. Numbers
    are plain decimal digits. No failure echoes a value."""
    failures: list[str] = []
    removed = {name.casefold() for name in REMOVED_SETTINGS}
    documents: dict[str, int] = {}
    capacity: dict[str, dict[str, int | None]] = {}
    segment_counts = {ELIGIBILITY_DOCUMENTS: 5, ELIGIBILITY_NON_EXPIRING: 4, ELIGIBILITY_CAPACITY: 5}
    capacity_members = (*ELIGIBILITY_CAPACITY_FIELDS, ELIGIBILITY_REQUIRE_DIMENSIONS)
    document_types = ", ".join(ELIGIBILITY_DOCUMENT_TYPES)
    for position, entry in enumerate(entries):
        if not isinstance(entry, dict) or not isinstance(entry.get("name"), str):
            continue
        name = entry["name"]
        if not is_eligibility_name(name) or name.casefold() in removed:
            continue
        label = setting_label(position, entry)
        segments = name.split("__")
        section = _member(segments[2], segment_counts) if len(segments) > 2 else None
        if len(segments) != segment_counts.get(section):
            failures.append(
                f"{label} is not a driver eligibility setting the API reads ({ELIGIBILITY_PREFIX}__{ELIGIBILITY_DOCUMENTS}"
                f"__<TYPE>__<n>, {ELIGIBILITY_PREFIX}__{ELIGIBILITY_NON_EXPIRING}__<n> or "
                f"{ELIGIBILITY_PREFIX}__{ELIGIBILITY_CAPACITY}__<TYPE>__<field>)")
            continue
        vehicle = None if section == ELIGIBILITY_NON_EXPIRING else segments[3]
        if vehicle is not None and vehicle not in ELIGIBILITY_VEHICLE_TYPES:
            # The API keeps the vehicle type as written and accepts only these five, compared exactly.
            failures.append(f"{label} names no vehicle type the API knows ({', '.join(ELIGIBILITY_VEHICLE_TYPES)}, "
                            "in capitals)")
            continue
        member = _member(segments[-1], capacity_members) if section == ELIGIBILITY_CAPACITY else segments[-1]
        if member is None:
            failures.append(f"{label} is not a capacity setting the API reads ({', '.join(capacity_members)})")
            continue
        if section != ELIGIBILITY_CAPACITY and not ELIGIBILITY_LIST_INDEX.match(member):
            failures.append(f"{label} must end in a list index (0, 1, 2...)")
            continue
        canonical = "__".join(part for part in (ELIGIBILITY_PREFIX, section, vehicle, member) if part is not None)
        if name != canonical:
            failures.append(f"{label} must be spelled exactly {canonical} "
                            "(configuration names ignore case, so another casing still reaches the API)")
        value = entry.get("value")
        # A value that is not a string is reported by the shape check and the sentinel by its own rule; either way
        # this rule is still missing.
        if not isinstance(value, str) or is_owner_sentinel(value):
            continue
        if section != ELIGIBILITY_CAPACITY:
            if section == ELIGIBILITY_DOCUMENTS:
                documents[vehicle] = documents.get(vehicle, 0) + 1
            if value not in ELIGIBILITY_DOCUMENT_TYPES:
                failures.append(f"{label} must be one of the document types {document_types}")
        elif member == ELIGIBILITY_REQUIRE_DIMENSIONS:
            if value not in ("true", "false"):
                failures.append(f"{label} must be true or false")
        else:
            maximum = ELIGIBILITY_CAPACITY_FIELDS[member]
            valid = ELIGIBILITY_POSITIVE_INTEGER.match(value) is not None and int(value) <= maximum
            capacity.setdefault(vehicle, {})[member] = int(value) if valid else None
            if not valid:
                failures.append(f"{label} must be a whole number from 1 to {maximum}, in plain digits")
    for vehicle in ELIGIBILITY_VEHICLE_TYPES:
        limits = capacity.get(vehicle, {})
        single, total = limits.get("MaximumSinglePackageWeightGrams"), limits.get("MaximumTotalWeightGrams")
        if single is not None and total is not None and single > total:
            failures.append(f"{ELIGIBILITY_PREFIX}__{ELIGIBILITY_CAPACITY}__{vehicle}__MaximumSinglePackageWeightGrams "
                            "may not exceed MaximumTotalWeightGrams")
        if allow_missing:
            continue
        missing = [] if documents.get(vehicle) else [
            f"{ELIGIBILITY_PREFIX}__{ELIGIBILITY_DOCUMENTS}__{vehicle}__0 (at least one required document type)"]
        missing += [f"{ELIGIBILITY_PREFIX}__{ELIGIBILITY_CAPACITY}__{vehicle}__{field}"
                    for field in ELIGIBILITY_CAPACITY_FIELDS if field not in limits]
        if missing:
            failures.append(f"driver eligibility rules for vehicle type {vehicle} are missing: {', '.join(missing)}. "
                            "The pilot API refuses to start without them (Drivers__Provider and Routing__Provider are "
                            "PostgreSql); the owner chooses them (README section 4, \"Driver eligibility rules\")")
    return failures


OBSERVABILITY_PARAMETERS = {
    "alertEmailAddress": str,
    "outboxLagThresholdSeconds": int,
    "http5xxMinimumCount": int,
    "http5xxPercentThreshold": int,
    "readinessEventThreshold": int,
}


def validate_observability_parameters(document: Any, *, allow_sentinel: bool) -> list[str]:
    """observability.parameters.json: an ARM parameters file with the owner's alert e-mail and, optionally,
    the documented thresholds; nothing else."""
    if not isinstance(document, dict) or not isinstance(document.get("parameters"), dict):
        return ["observability parameters must be an ARM parameters file with a `parameters` object"]
    failures = []
    parameters = document["parameters"]
    for name, entry in parameters.items():
        expected = OBSERVABILITY_PARAMETERS.get(name)
        if expected is None:
            failures.append(f"parameter {name} is not an OBS-002 parameter")
        elif not isinstance(entry, dict) or set(entry) != {"value"} or type(entry["value"]) is not expected:
            failures.append(f"parameter {name} must be {{\"value\": <{expected.__name__}>}}")
    entry = parameters.get("alertEmailAddress")
    email = entry.get("value") if isinstance(entry, dict) else None
    if not isinstance(email, str):
        failures.append("alertEmailAddress is required")
    elif is_owner_sentinel(email):
        if not allow_sentinel:
            failures.append(f"alertEmailAddress still awaits an owner decision ({OWNER_SENTINEL})")
    elif not EMAIL_ADDRESS.match(email):
        failures.append("alertEmailAddress is not a single e-mail address")
    return failures


def validate_web_parameters(document: Any, *, allow_sentinel: bool) -> list[str]:
    """web.parameters.json: an ARM parameters file with exactly the two accepted versions, each matching
    ^[A-Za-z0-9._-]{1,64}$. There is no default and no empty value; OWNER_DECISION_REQUIRED passes only with
    allow_sentinel (the static guard), never for the deploy."""
    if not isinstance(document, dict) or not isinstance(document.get("parameters"), dict):
        return ["web parameters must be an ARM parameters file with a `parameters` object"]
    failures = []
    parameters = document["parameters"]
    for name in parameters:
        if name not in WEB_VERSION_PARAMETERS:
            failures.append(f"parameter {name} is not a UI-001 web parameter (allowed: {sorted(WEB_VERSION_PARAMETERS)})")
    for name, variable in WEB_VERSION_PARAMETERS.items():
        entry = parameters.get(name)
        label = f"{name} ({variable})"
        if not isinstance(entry, dict) or set(entry) != {"value"} or not isinstance(entry["value"], str):
            failures.append(f"{label} is required as {{\"value\": <string>}}")
            continue
        value = entry["value"]
        if is_owner_sentinel(value):
            if not allow_sentinel:
                failures.append(f"{label} still awaits an owner decision ({OWNER_SENTINEL}); it comes with the approved "
                                "privacy notice (GATE-007)")
        elif not ACCEPTANCE_VERSION.match(value):
            failures.append(f"{label} must match ^[A-Za-z0-9._-]{{1,64}}$ (AI-05 terms_version/privacy_version), found {value!r}")
    return failures


# ---------------------------------------------------------------------------------------------- gate decisions
DECISION_LOG = Path("docs/normative/v0.6/decision-log.md")
DECISION_LOG_HEADER = ["Date", "ID", "Type", "Decision", "Impacted files", "Approved by"]
GATE_DECISION_TYPES = frozenset({"Gate resolution", "Gate scoping"})
DECISION_ID = re.compile(r"^[A-Z0-9][A-Z0-9-]{2,80}$")


def split_markdown_row(line: str) -> list[str] | None:
    """Cells of a `| a | b |` table row, splitting only on unescaped `|` (`\\|` is a literal pipe)."""
    stripped = line.strip()
    if len(stripped) < 2 or not stripped.startswith("|") or not stripped.endswith("|") or stripped.endswith("\\|"):
        return None
    cells: list[str] = []
    current: list[str] = []
    escaped = False
    for char in stripped[1:-1]:
        if escaped:
            if char != "|":
                current.append("\\")
            current.append(char)
            escaped = False
        elif char == "\\":
            escaped = True
        elif char == "|":
            cells.append("".join(current).strip())
            current = []
        else:
            current.append(char)
    if escaped:
        current.append("\\")
    cells.append("".join(current).strip())
    return cells


def decision_log_rows(text: str) -> list[list[str]]:
    """Rows of the decision table: every table row after the exact DECISION_LOG_HEADER, across blank lines,
    until the first non-blank line that is not a table row (a heading or prose ends the table)."""
    rows: list[list[str]] = []
    in_table = False
    for line in text.splitlines():
        cells = split_markdown_row(line)
        if cells == DECISION_LOG_HEADER:
            in_table = True
            continue
        if not in_table:
            continue
        if cells is None:
            if line.strip():
                in_table = False
            continue
        if all(re.fullmatch(r":?-{3,}:?", cell) for cell in cells):
            continue
        rows.append(cells)
    return rows


def validate_gate_decision(text: str, gate: str, decision_id: str) -> list[str]:
    """ENV-001: the id must name exactly one row `GATE-<gate>-...` whose Type is a gate resolution or scoping."""
    if gate not in ("007", "012"):
        return [f"unsupported gate GATE-{gate}"]
    if not decision_id or not DECISION_ID.match(decision_id):
        return [f"GATE-{gate} decision id is missing or malformed"]
    if not decision_id.startswith(f"GATE-{gate}-"):
        return [f"decision {decision_id} is not a GATE-{gate} decision (id must start with GATE-{gate}-)"]
    rows = decision_log_rows(text)
    if not rows:
        return ["decision-log table not found"]
    # Date, ID and Type come first, so an unescaped `|` later in the Decision text cannot shift them.
    matches = [r for r in rows if len(r) >= len(DECISION_LOG_HEADER) and r[1] == decision_id]
    if not matches:
        return [f"decision {decision_id} is not recorded in the decision-log table"]
    if len(matches) > 1:
        return [f"decision {decision_id} appears {len(matches)} times in the decision-log table"]
    row_type = matches[0][2]
    if row_type not in GATE_DECISION_TYPES:
        return [f"decision {decision_id} has Type '{row_type}', not one of {sorted(GATE_DECISION_TYPES)}"]
    return []


# ---------------------------------------------------------------------------------------------- SCRAM
def scram_sha256_verifier(password: str, *, salt: bytes | None = None, iterations: int = 4096) -> str:
    """PostgreSQL stored SCRAM-SHA-256 verifier (RFC 5802 / RFC 7677)."""
    salt = secrets.token_bytes(16) if salt is None else salt
    salted = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, iterations)
    stored_key = hashlib.sha256(hmac.new(salted, b"Client Key", hashlib.sha256).digest()).digest()
    server_key = hmac.new(salted, b"Server Key", hashlib.sha256).digest()
    b64 = lambda raw: base64.b64encode(raw).decode("ascii")  # noqa: E731
    return f"SCRAM-SHA-256${iterations}:{b64(salt)}${b64(stored_key)}:{b64(server_key)}"


# ---------------------------------------------------------------------------------------------- CLI
def command_check(args: argparse.Namespace) -> int:
    try:
        ctx = load_context(Path(args.repo_root).resolve(), Path(args.arm_dir).resolve())
    except GuardError as error:
        print(f"ENV001_PILOT_GUARDS=BLOCKED {error}")
        return 2
    results = run_guards(ctx)
    for result in results:
        print(f"P{result.number:02d}  {result.status:<8} {result.title} :: {result.detail}")
    failed = [r for r in results if not r.passed]
    if failed:
        print(f"ENV001_PILOT_GUARDS=FAIL ({len(failed)} failing)")
        return 1
    print(f"ENV001_PILOT_GUARDS=PASS ({len(results)} guards)")
    return 0


def command_settings_check(args: argparse.Namespace) -> int:
    try:
        failures = validate_settings(load_settings(Path(args.file)), allow_sentinel=args.allow_owner_sentinel)
    except GuardError as error:
        failures = [str(error)]
    for failure in failures:
        print(f"STOP_FOR_OWNER_DECISION: {failure}")
    print("ENV001_SETTINGS=" + ("FAIL" if failures else "PASS"))
    return 1 if failures else 0


def command_observability_check(args: argparse.Namespace) -> int:
    try:
        document = json.loads(Path(args.file).read_text(encoding="utf-8"))
        failures = validate_observability_parameters(document, allow_sentinel=args.allow_owner_sentinel)
    except (OSError, ValueError) as error:
        failures = [f"{args.file}: {error}"]
    for failure in failures:
        print(f"STOP_FOR_OWNER_DECISION: {failure}")
    print("ENV001_OBSERVABILITY=" + ("FAIL" if failures else "PASS"))
    return 1 if failures else 0


def command_web_check(args: argparse.Namespace) -> int:
    try:
        document = json.loads(Path(args.file).read_text(encoding="utf-8"))
        failures = validate_web_parameters(document, allow_sentinel=args.allow_owner_sentinel)
    except (OSError, ValueError) as error:
        failures = [f"{args.file}: {error}"]
    for failure in failures:
        print(f"STOP_FOR_OWNER_DECISION: {failure}")
    print("ENV001_WEB_VERSIONS=" + ("FAIL" if failures else "PASS"))
    return 1 if failures else 0


def command_gate_decision(args: argparse.Namespace) -> int:
    try:
        text = Path(args.decision_log).read_text(encoding="utf-8")
    except OSError as error:
        print(f"STOP_FOR_OWNER_DECISION: {error}")
        return 1
    failures = validate_gate_decision(text, args.gate, (args.decision_id or "").strip())
    for failure in failures:
        print(f"STOP_FOR_OWNER_DECISION: {failure}")
    print(f"ENV001_GATE_{args.gate}=" + ("FAIL" if failures else f"PASS {args.decision_id}"))
    return 1 if failures else 0


def command_postgres_server_name(args: argparse.Namespace) -> int:
    failures = validate_postgres_server_name(args.value)
    for failure in failures:
        print(f"STOP_FOR_OWNER_DECISION: {failure}")
    if failures:
        print("ENV001_POSTGRES_SERVER=FAIL")
        return 1
    print("ENV001_POSTGRES_SERVER=" + (f"PASS {args.value}" if args.value else "PASS default (platform.bicep postgresServerName)"))
    return 0


def command_connection_host(_: argparse.Namespace) -> int:
    """Connection string on stdin -> its host on stdout. The input holds a password and is never printed."""
    try:
        print(connection_host(sys.stdin.read()))
    except GuardError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 2
    return 0


def command_postgres_adoption_check(args: argparse.Namespace) -> int:
    failures = validate_postgres_server_name(args.name) or ([] if args.name else [f"{POSTGRES_SERVER_VARIABLE} is empty"])
    if not failures:
        try:
            server = json.loads(Path(args.server_json).read_text(encoding="utf-8"))
            platform = json.loads(Path(args.platform_arm).read_text(encoding="utf-8"))
            failures = postgres_adoption_failures(args.name, server, platform, subscription_id=args.subscription_id,
                                                  resource_group=args.resource_group, region=args.region)
        except (OSError, ValueError) as error:
            failures = [f"cannot read the server or template description: {error}"]
    for failure in failures:
        print(f"STOP_FOR_OWNER_DECISION: {failure}")
    print("ENV001_POSTGRES_ADOPTION=" + ("FAIL" if failures else f"PASS {args.name}"))
    return 1 if failures else 0


def command_scram_verifier(_: argparse.Namespace) -> int:
    password = sys.stdin.read().rstrip("\n")
    if len(password) < 32:
        print("password on stdin must be at least 32 characters", file=sys.stderr)
        return 2
    print(scram_sha256_verifier(password))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    check = sub.add_parser("check", help="evaluate the pilot guards")
    check.add_argument("--repo-root", default=".")
    check.add_argument("--arm-dir", required=True)
    check.set_defaults(func=command_check)
    settings = sub.add_parser("settings-check", help="validate the reviewed business settings")
    settings.add_argument("--file", default=str(SETTINGS_FILE))
    settings.add_argument("--allow-owner-sentinel", action="store_true")
    settings.set_defaults(func=command_settings_check)
    observability = sub.add_parser("observability-check", help="validate the OBS-002 alert parameters (owner e-mail)")
    observability.add_argument("--file", default=str(OBSERVABILITY_PARAMETERS_FILE))
    observability.add_argument("--allow-owner-sentinel", action="store_true")
    observability.set_defaults(func=command_observability_check)
    web = sub.add_parser("web-check", help="validate the UI-001 accepted terms/privacy versions (owner values)")
    web.add_argument("--file", default=str(WEB_PARAMETERS_FILE))
    web.add_argument("--allow-owner-sentinel", action="store_true")
    web.set_defaults(func=command_web_check)
    gate = sub.add_parser("gate-decision", help="require a GATE-007/012 resolution or scoping row")
    gate.add_argument("--gate", required=True, choices=("007", "012"))
    gate.add_argument("--decision-id", default="", help="decision-log ID (from the environment variable)")
    gate.add_argument("--decision-log", default=str(DECISION_LOG))
    gate.set_defaults(func=command_gate_decision)
    scram = sub.add_parser("scram-verifier", help="password on stdin -> SCRAM-SHA-256 verifier on stdout")
    scram.set_defaults(func=command_scram_verifier)
    server_name = sub.add_parser("postgres-server-name", help=f"validate {POSTGRES_SERVER_VARIABLE} (empty = the default server)")
    server_name.add_argument("--value", default="", help="the environment variable's value")
    server_name.set_defaults(func=command_postgres_server_name)
    host = sub.add_parser("connection-host", help="connection string on stdin -> its PostgreSQL host on stdout (never echoed)")
    host.set_defaults(func=command_connection_host)
    adoption = sub.add_parser("postgres-adoption-check", help="refuse a named server platform.bicep cannot update in place")
    adoption.add_argument("--name", required=True)
    adoption.add_argument("--server-json", required=True, help="az resource show output (ARM GET) of the server")
    adoption.add_argument("--platform-arm", required=True, help="compiled platform.json")
    adoption.add_argument("--subscription-id", required=True)
    adoption.add_argument("--resource-group", required=True)
    adoption.add_argument("--region", default=PILOT_REGION)
    adoption.set_defaults(func=command_postgres_adoption_check)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
