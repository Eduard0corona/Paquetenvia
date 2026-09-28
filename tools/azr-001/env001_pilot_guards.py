#!/usr/bin/env python3
"""ENV-001 pilot (PILOT-REAL-PEOPLE) static guards and deployment helpers.

The pilot is a separate environment from AZR-001 DEV_SYNTHETIC: its templates live in
`deploy/azure/pilot/` (outside the AZR-001 `deploy/azure/*.bicep` glob) and it is deployed by
`.github/workflows/deploy-azure-pilot.yml`. These guards never weaken or replace the AZR-001 §28
guards; they check the pilot's own owner decisions against the compiled ARM output
(`bicep build`) and the workflow. They never contact Azure.

Commands:
  check            evaluate every pilot guard (P00..P20)
  settings-check   validate deploy/azure/pilot/apps.settings.json (fails while an owner value is missing)
  scram-verifier   read a password on stdin, print its PostgreSQL SCRAM-SHA-256 verifier
  gate-decision    require a decision-log row that resolves or scopes GATE-007 / GATE-012
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

# Key Vault secrets each workload may reference (least privilege, §17-style classes).
# PILOT-KEYVAULT-PRIVATE-APP-READ: each workload reads exactly these Key Vault secrets itself (ADP-001
# `KeyVaultSecrets__Mappings__<n>__SecretName` -> `__ConfigurationKey`), never through Container Apps.
# ConfigurationKey -> SecretName. One secret may feed several keys (read once); a key appears only once.
REQUIRED_SECRET_MAPPINGS = {
    API_APP: {
        "ConnectionStrings:Paqueteria": "pg-api-runtime-connection",
        "AuthCenter:ClientSecret": "authcenter-paquetenvia-client-secret",
        "EmailLookup:Keys:1": "paquetenvia-email-lookup-key-1",
    },
    WORKER_APP: {
        "ConnectionStrings:PaqueteriaWorker": "pg-worker-runtime-connection",
        "ConnectionStrings:Paqueteria": "pg-worker-runtime-connection",
    },
    WEB_APP: {},
    MIGRATE_JOB: {"PAQUETERIA_MIGRATION_CONNECTION": "pg-migrate-connection"},
    LOGINS_JOB: {
        "PAQUETERIA_MIGRATION_CONNECTION": "pg-migrate-connection",
        "PAQUETERIA_API_LOGIN_VERIFIER": "pg-api-login-verifier",
        "PAQUETERIA_WORKER_LOGIN_VERIFIER": "pg-worker-login-verifier",
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
    r"^(ConnectionStrings__.*|.*Secret|EmailLookup__Keys__.*|PAQUETERIA_MIGRATION_CONNECTION|.*_VERIFIER|.*Password.*|.*ApiKey.*|.*Token)$")

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
    }
)
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
    "Urls",
)
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
    expected = {"security", "platform", "jobs", "apps"}
    failures = [] if set(ctx.templates) == expected else [f"pilot templates must be exactly {sorted(expected)}, found {sorted(ctx.templates)}"]
    return _result(0, "pilot templates compiled", failures, f"{len(ctx.templates)} templates")


def guard_01_resource_set(ctx: Context) -> GuardResult:
    failures = []
    for template, resource in ctx.resources():
        resource_type = str(resource.get("type", ""))
        if resource_type not in AUTHORIZED_RESOURCE_TYPES:
            failures.append(f"{template} declares unauthorized {resource_type}")
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


def guard_12_storage(ctx: Context) -> GuardResult:
    failures = []
    accounts = ctx.resources("Microsoft.Storage/storageAccounts")
    if len(accounts) != 1:
        return _result(12, "proof storage", [f"expected one storage account, found {len(accounts)}"], "")
    properties = accounts[0][1].get("properties", {}) or {}
    for key, expected in (("allowBlobPublicAccess", False), ("allowSharedKeyAccess", False), ("supportsHttpsTrafficOnly", True), ("isHnsEnabled", False), ("minimumTlsVersion", "TLS1_2")):
        if properties.get(key) != expected:
            failures.append(f"storage {key} must be {expected}")
    for _, service in ctx.resources("Microsoft.Storage/storageAccounts/blobServices"):
        if (service.get("properties", {}) or {}).get("isVersioningEnabled") is not False:
            failures.append("blob versioning must be off (ADP-001)")
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
    return _result(12, "proof storage private, Entra-only, malware scanned", failures, "no anonymous or shared-key access, Defender on upload")


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


GUARD_TOOLS = ("env001_pilot_guards.py", "azr001_static_guards.py")
PYYAML_INSTALL = re.compile(r"pip install\s+PyYAML==6\.0\.3\b")


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


def validate_settings(entries: list[Any], *, allow_sentinel: bool) -> list[str]:
    failures = []
    seen: set[str] = set()
    for entry in entries:
        if not isinstance(entry, dict) or set(entry) != {"name", "value"} or not all(isinstance(entry[k], str) for k in entry):
            failures.append(f"setting must be exactly {{name, value}} strings: {entry!r}")
            continue
        name, value = entry["name"], entry["value"]
        if not SETTING_NAME.match(name):
            failures.append(f"setting name {name!r} is not a .NET double-underscore key")
        if name.startswith(PLATFORM_MANAGED_PREFIXES):
            failures.append(f"setting {name} is platform-managed by apps.bicep and cannot be overridden")
        if name in seen:
            failures.append(f"setting {name} declared twice")
        seen.add(name)
        if is_forbidden_value(value):
            failures.append(f"setting {name}={value} is a mock/synthetic value")
        if is_owner_sentinel(value) and not allow_sentinel:
            failures.append(f"setting {name} still awaits an owner decision ({OWNER_SENTINEL})")
        if re.search(r"(?i)(password|secret|token|key)", name):
            failures.append(f"setting {name} looks like a secret; secrets belong in Key Vault")
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
    gate = sub.add_parser("gate-decision", help="require a GATE-007/012 resolution or scoping row")
    gate.add_argument("--gate", required=True, choices=("007", "012"))
    gate.add_argument("--decision-id", default="", help="decision-log ID (from the environment variable)")
    gate.add_argument("--decision-log", default=str(DECISION_LOG))
    gate.set_defaults(func=command_gate_decision)
    scram = sub.add_parser("scram-verifier", help="password on stdin -> SCRAM-SHA-256 verifier on stdout")
    scram.set_defaults(func=command_scram_verifier)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
