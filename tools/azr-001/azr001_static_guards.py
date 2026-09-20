#!/usr/bin/env python3
"""AZR-001 v0.8 §28 static CI guards and §27 deployment provenance gate.

`check` evaluates the thirty §28 guards against the repository tree, the
compiled ARM output of every `deploy/azure/*.bicep` template (`bicep build`)
and the deployment workflow. It never contacts Azure. Guard numbering follows
§28 exactly; guards whose subject workload does not exist yet report
`PASS (vacuous)` so the missing asset remains a Definition-of-Done gap rather
than a false static failure.

`deploy-gate` is the fail-closed provenance check executed by
`.github/workflows/deploy-azure-dev.yml` before any Azure control-plane
action: the dispatched ref must equal the frozen AZR-001 baseline recorded in
`deploy/azure/deploy-core.ps1` and must be the exact head of a successful
Foundation CI run on `main`.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Iterable

import yaml

SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")
DIGEST_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")
FROZEN_BASELINE_PATTERN = re.compile(r"^\$expectedMain = '([0-9a-f]{40})'$", re.MULTILINE)
DIGEST_REGEX_LITERAL = r"^sha256:[0-9a-f]{64}$"
FOUNDATION_WORKFLOW_NAME = "Foundation CI"
FOUNDATION_JOB_COUNT = 13

DEPLOY_WORKFLOW = ".github/workflows/deploy-azure-dev.yml"
FOUNDATION_WORKFLOW = ".github/workflows/ci.yml"
DEPLOY_ENVIRONMENT = "azure-dev"
DEPLOY_CONCURRENCY_GROUP = "azure-dev"
FORBIDDEN_DEPLOY_TRIGGERS = ("push", "pull_request", "schedule", "repository_dispatch", "workflow_run")

NORMATIVE_IDENTITIES = frozenset(
    {
        "mi-azr-dev-db-migrate",
        "mi-azr-dev-prereq-seed",
        "mi-azr-dev-api",
        "mi-azr-dev-worker",
        "mi-azr-dev-web",
        "mi-azr-dev-acceptance",
        "mi-azr-dev-rls",
    }
)

# §5 authorized resource set, expressed as ARM resource types.
AUTHORIZED_RESOURCE_TYPES = frozenset(
    {
        "Microsoft.ContainerRegistry/registries",
        "Microsoft.App/managedEnvironments",
        "Microsoft.App/containerApps",
        "Microsoft.App/jobs",
        "Microsoft.DBforPostgreSQL/flexibleServers",
        "Microsoft.DBforPostgreSQL/flexibleServers/databases",
        "Microsoft.DBforPostgreSQL/flexibleServers/firewallRules",
        "Microsoft.DBforPostgreSQL/flexibleServers/configurations",
        "Microsoft.KeyVault/vaults",
        "Microsoft.KeyVault/vaults/secrets",
        "Microsoft.OperationalInsights/workspaces",
        "Microsoft.Insights/diagnosticSettings",
        "Microsoft.Insights/metricAlerts",
        "Microsoft.Insights/actionGroups",
        "Microsoft.Consumption/budgets",
        "Microsoft.ManagedIdentity/userAssignedIdentities",
        "Microsoft.Authorization/roleAssignments",
    }
)

# §5 / §12 persistent workload inventory. Keys are ARM resource names.
AUTHORIZED_CONTAINER_APPS = {
    "ca-pv-azrdev-web": "web",
    "ca-pv-azrdev-api": "api",
    "ca-pv-azrdev-worker": "worker",
}
AUTHORIZED_JOBS = {
    "job-pv-azrdev-migrate": "migrate",
    "job-pv-azrdev-prereq-seed": "prereq-seed",
    "job-pv-azrdev-acceptance": "acceptance",
    "job-pv-azrdev-rls": "rls",
}
DOTNET_WORKLOAD_ROLES = frozenset({"api", "worker", "migrate", "prereq-seed", "acceptance", "rls"})
SINGLE_REPLICA_ROLES = ("api", "worker")

# §17 secret classes by Key Vault secret name (the `keyVaultUrl` tail).
PRIVILEGED_SECRET_PATTERN = re.compile(r"(admin|migrate|bootstrap|prereq)", re.IGNORECASE)
RUNTIME_SECRET_PATTERN = re.compile(r"^pg-(api|worker)-runtime-connection$")
DATABASE_SECRET_PATTERN = re.compile(r"^pg-", re.IGNORECASE)

# §26 canonical Web build-input manifest minimum.
WEB_MANIFEST_MINIMUM = ("NODE_ENV", "NEXT_PUBLIC_API_BASE_URL", "NEXT_PUBLIC_TRACKING_BRAND_NAME", "NEXT_PUBLIC_TRACKING_SUPPORT_URL")
NEXT_PUBLIC_PATTERN = re.compile(r"NEXT_PUBLIC_[A-Z0-9_]+")
WEB_SOURCE_ROOTS = ("apps/web/src", "apps/web/next.config.ts")

FORBIDDEN_ENVIRONMENT_VALUES = frozenset({"Development", "Testing"})
ENVIRONMENT_KEYS = ("DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT")
DEPLOYMENT_CLASS_KEY = "PAQUETERIA_DEPLOYMENT_CLASS"
DEPLOYMENT_CLASS_VALUE = "DEV_SYNTHETIC"
MOCK_AUTH_KEY = "Authentication__Provider"
MOCK_AUTH_VALUE = "Mock"
PROOF_STORAGE_SETTINGS = {"ProofStorage__Provider": "Disabled", "ProofStorage__ThreatScanner": "Disabled"}
MIGRATOR_MARKER = "Paqueteria.DatabaseMigrator"
UNRESTRICTED_CIDRS = frozenset({"0.0.0.0/0", "::/0"})

# Tokens whose presence anywhere in the deployment tree indicates a prohibited resource or feature.
PROHIBITED_TYPE_PREFIXES = {
    "redis": ("Microsoft.Cache/",),
    "signalr": ("Microsoft.SignalRService/",),
    "private-endpoint": ("Microsoft.Network/privateEndpoints", "Microsoft.Network/privateDnsZones", "Microsoft.Network/virtualNetworks"),
    "maintenance": ("Microsoft.Maintenance/",),
}


class GuardError(Exception):
    """A guard could not be evaluated (BLOCKED semantics, distinct from FAIL)."""


@dataclass
class Template:
    name: str
    document: dict[str, Any]

    @property
    def resources(self) -> list[dict[str, Any]]:
        raw = self.document.get("resources", [])
        if isinstance(raw, dict):
            return [entry for entry in raw.values() if isinstance(entry, dict)]
        return [entry for entry in raw if isinstance(entry, dict)]

    @property
    def variables(self) -> dict[str, Any]:
        return self.document.get("variables", {}) or {}


@dataclass
class Workload:
    template: str
    resource_type: str
    name: str
    role: str | None
    properties: dict[str, Any]

    @property
    def configuration(self) -> dict[str, Any]:
        return self.properties.get("configuration", {}) or {}

    @property
    def containers(self) -> list[dict[str, Any]]:
        template = self.properties.get("template", {}) or {}
        return [c for c in (template.get("containers", []) or []) if isinstance(c, dict)]

    @property
    def env(self) -> dict[str, dict[str, Any]]:
        merged: dict[str, dict[str, Any]] = {}
        for container in self.containers:
            for entry in container.get("env", []) or []:
                if isinstance(entry, dict) and "name" in entry:
                    merged[str(entry["name"])] = entry
        return merged

    def env_value(self, key: str) -> str | None:
        entry = self.env.get(key)
        return None if entry is None else entry.get("value")

    @property
    def secret_names(self) -> list[str]:
        names: list[str] = []
        for secret in self.configuration.get("secrets", []) or []:
            url = str(secret.get("keyVaultUrl", ""))
            # Either a literal `.../secrets/<name>` or an ARM `format(...)` expression whose last argument is the name.
            match = re.search(r"secrets/([A-Za-z0-9-]+)$", url) or re.search(r"'([A-Za-z0-9-]+)'\)\]$", url)
            if match:
                names.append(match.group(1))
            elif "value" in secret:
                names.append(f"inline:{secret.get('name')}")
            else:
                names.append(str(secret.get("name")))
        return names


@dataclass
class GuardResult:
    number: int
    title: str
    status: str
    detail: str = ""

    @property
    def passed(self) -> bool:
        return self.status.startswith("PASS")


@dataclass
class Context:
    repo_root: Path
    arm_dir: Path
    templates: list[Template] = field(default_factory=list)
    bicep_files: list[Path] = field(default_factory=list)
    deploy_workflow: dict[str, Any] = field(default_factory=dict)
    deploy_workflow_text: str = ""
    foundation_workflow: dict[str, Any] = field(default_factory=dict)

    @property
    def workloads(self) -> list[Workload]:
        found: list[Workload] = []
        for template in self.templates:
            for resource in template.resources:
                resource_type = str(resource.get("type", ""))
                if resource_type not in ("Microsoft.App/containerApps", "Microsoft.App/jobs"):
                    continue
                name = str(resource.get("name", ""))
                catalog = AUTHORIZED_CONTAINER_APPS if resource_type == "Microsoft.App/containerApps" else AUTHORIZED_JOBS
                found.append(Workload(template.name, resource_type, name, catalog.get(name), resource.get("properties", {}) or {}))
        return found

    def workloads_by_role(self, role: str) -> list[Workload]:
        return [w for w in self.workloads if w.role == role]

    def resources(self, resource_type: str) -> list[dict[str, Any]]:
        return [r for t in self.templates for r in t.resources if r.get("type") == resource_type and not r.get("existing")]

    def deploy_tree_text(self) -> str:
        parts = []
        for path in sorted((self.repo_root / "deploy" / "azure").glob("*")):
            if path.is_file():
                parts.append(path.read_text(encoding="utf-8", errors="replace"))
        return "\n".join(parts)


def load_workflow_yaml(path: Path) -> dict[str, Any]:
    document = yaml.safe_load(path.read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        raise GuardError(f"{path} is not a YAML mapping")
    # PyYAML parses the bare `on:` key as boolean True.
    if True in document and "on" not in document:
        document["on"] = document.pop(True)
    return document


def load_context(repo_root: Path, arm_dir: Path) -> Context:
    context = Context(repo_root=repo_root, arm_dir=arm_dir)
    context.bicep_files = sorted((repo_root / "deploy" / "azure").glob("*.bicep"))
    if not context.bicep_files:
        raise GuardError("no deploy/azure/*.bicep templates found")
    for bicep in context.bicep_files:
        compiled = arm_dir / f"{bicep.stem}.json"
        if not compiled.is_file():
            raise GuardError(f"missing compiled ARM output for {bicep.name}: {compiled}")
        context.templates.append(Template(bicep.stem, json.loads(compiled.read_text(encoding="utf-8"))))
    deploy_path = repo_root / DEPLOY_WORKFLOW
    if not deploy_path.is_file():
        raise GuardError(f"missing {DEPLOY_WORKFLOW}")
    context.deploy_workflow_text = deploy_path.read_text(encoding="utf-8")
    context.deploy_workflow = load_workflow_yaml(deploy_path)
    context.foundation_workflow = load_workflow_yaml(repo_root / FOUNDATION_WORKFLOW)
    return context


def read_frozen_baseline(deploy_core: Path) -> str:
    matches = FROZEN_BASELINE_PATTERN.findall(deploy_core.read_text(encoding="utf-8"))
    if len(matches) != 1:
        raise GuardError(f"{deploy_core} must declare exactly one frozen $expectedMain SHA (found {len(matches)})")
    return matches[0]


def _vacuous(number: int, title: str, subject: str) -> GuardResult:
    return GuardResult(number, title, "PASS (vacuous)", f"{subject} not yet defined in deploy/azure")


def _result(number: int, title: str, failures: list[str], detail_ok: str = "") -> GuardResult:
    if failures:
        return GuardResult(number, title, "FAIL", "; ".join(failures))
    return GuardResult(number, title, "PASS", detail_ok)


def guard_01_bicep_build(ctx: Context) -> GuardResult:
    failures = []
    for bicep in ctx.bicep_files:
        compiled = ctx.arm_dir / f"{bicep.stem}.json"
        try:
            json.loads(compiled.read_text(encoding="utf-8"))
        except (OSError, ValueError) as error:
            failures.append(f"{bicep.name}: {error}")
    return _result(1, "Bicep build/lint", failures, f"{len(ctx.bicep_files)} templates compiled")


def guard_02_dispatch_only(ctx: Context) -> GuardResult:
    triggers = ctx.deploy_workflow.get("on")
    failures = []
    if isinstance(triggers, dict):
        keys = set(triggers)
    elif isinstance(triggers, list):
        keys = set(triggers)
    elif isinstance(triggers, str):
        keys = {triggers}
    else:
        keys = set()
    if keys != {"workflow_dispatch"}:
        failures.append(f"deploy workflow triggers must be exactly {{workflow_dispatch}}, found {sorted(keys)}")
    for forbidden in FORBIDDEN_DEPLOY_TRIGGERS:
        if re.search(rf"^\s+{forbidden}:", ctx.deploy_workflow_text, re.MULTILINE):
            failures.append(f"forbidden trigger key present in text: {forbidden}")
    return _result(2, "deployment workflow_dispatch only", failures, "workflow_dispatch is the single trigger")


def guard_03_azure_dev_environment(ctx: Context) -> GuardResult:
    failures = []
    concurrency = ctx.deploy_workflow.get("concurrency")
    if not isinstance(concurrency, dict) or concurrency.get("group") != DEPLOY_CONCURRENCY_GROUP or concurrency.get("cancel-in-progress") is not False:
        failures.append("workflow concurrency must be group=azure-dev with cancel-in-progress=false")
    jobs = ctx.deploy_workflow.get("jobs", {}) or {}
    azure_jobs = 0
    for job_name, job in jobs.items():
        if not isinstance(job, dict):
            continue
        permissions = job.get("permissions", {}) or {}
        touches_azure = (isinstance(permissions, dict) and permissions.get("id-token") == "write") or any(
            "azure/login" in str(step.get("uses", "")) or re.search(r"\baz\b|deploy-core\.ps1|deploy-migration-job\.ps1", str(step.get("run", "")))
            for step in job.get("steps", []) or []
            if isinstance(step, dict)
        )
        if touches_azure:
            azure_jobs += 1
            environment = job.get("environment")
            env_name = environment.get("name") if isinstance(environment, dict) else environment
            if env_name != DEPLOY_ENVIRONMENT:
                failures.append(f"job '{job_name}' touches Azure without environment {DEPLOY_ENVIRONMENT}")
    if azure_jobs == 0:
        failures.append("deploy workflow declares no Azure control-plane job")
    return _result(3, "azure-dev GitHub Environment", failures, f"{azure_jobs} Azure job(s) bound to {DEPLOY_ENVIRONMENT}")


def _image_references(ctx: Context) -> list[tuple[str, str]]:
    refs = []
    for workload in ctx.workloads:
        for container in workload.containers:
            refs.append((f"{workload.template}:{workload.name}", str(container.get("image", ""))))
    return refs


def guard_04_no_floating_tags(ctx: Context) -> GuardResult:
    failures = []
    for source, image in _image_references(ctx):
        if image.startswith("[parameters("):
            continue
        if "@sha256:" not in image:
            failures.append(f"{source} image is not digest-pinned: {image}")
    for match in re.finditer(r"image:\s*(\S+)", ctx.deploy_workflow_text):
        if "@sha256:" not in match.group(1):
            failures.append(f"deploy workflow image reference without digest: {match.group(1)}")
    if re.search(r":latest\b", ctx.deploy_workflow_text):
        failures.append("deploy workflow references a :latest tag")
    return _result(4, "no floating deployment tags", failures, "all workload images are parameters or digest literals")


def guard_05_deploy_by_digest(ctx: Context) -> GuardResult:
    failures = []
    scripts = sorted((ctx.repo_root / "deploy" / "azure").glob("deploy-*.ps1"))
    for script in scripts:
        text = script.read_text(encoding="utf-8")
        if "Digest" in text and DIGEST_REGEX_LITERAL not in text:
            failures.append(f"{script.name} accepts a digest parameter without the strict sha256 regex")
    inputs = ((ctx.deploy_workflow.get("on") or {}).get("workflow_dispatch") or {}).get("inputs", {}) or {}
    digest_inputs = [name for name in inputs if name.endswith("_digest")]
    for name in digest_inputs:
        if DIGEST_REGEX_LITERAL not in ctx.deploy_workflow_text:
            failures.append(f"workflow input {name} is not validated with {DIGEST_REGEX_LITERAL}")
            break
    return _result(5, "deployment by digest", failures, f"{len(scripts)} deploy script(s), {len(digest_inputs)} digest input(s) validated")


def guard_06_next_public_manifest(ctx: Context) -> GuardResult:
    dockerfile = ctx.repo_root / "deploy" / "azure" / "Dockerfile.web"
    text = dockerfile.read_text(encoding="utf-8")
    declared = set(re.findall(r"^(?:ARG|ENV)\s+([A-Z0-9_]+)", text, re.MULTILINE))
    used: set[str] = set()
    for root in WEB_SOURCE_ROOTS:
        path = ctx.repo_root / root
        files = [path] if path.is_file() else [p for p in path.rglob("*") if p.suffix in {".ts", ".tsx", ".js", ".mjs"} and "node_modules" not in p.parts]
        for file in files:
            used.update(NEXT_PUBLIC_PATTERN.findall(file.read_text(encoding="utf-8", errors="replace")))
    required = set(WEB_MANIFEST_MINIMUM) | used
    missing = sorted(required - declared)
    failures = [f"Dockerfile.web manifest missing {missing}"] if missing else []
    return _result(6, "NEXT_PUBLIC manifest complete", failures, f"manifest covers {len(required)} build inputs")


def guard_07_dev_synthetic_environment(ctx: Context) -> GuardResult:
    failures = []
    for workload in ctx.workloads:
        for key in ENVIRONMENT_KEYS + ("NODE_ENV",):
            value = workload.env_value(key)
            if value in FORBIDDEN_ENVIRONMENT_VALUES or (value and value.lower() in ("development", "testing")):
                failures.append(f"{workload.name} sets {key}={value}")
        if workload.role in DOTNET_WORKLOAD_ROLES:
            for key in ENVIRONMENT_KEYS:
                if workload.env_value(key) != "DevSynthetic":
                    failures.append(f"{workload.name} must set {key}=DevSynthetic")
        if workload.role == "web" and workload.env_value("NODE_ENV") != "production":
            failures.append(f"{workload.name} must set NODE_ENV=production")
    for forbidden in ("Development", "Testing"):
        if re.search(rf"(DOTNET_ENVIRONMENT|ASPNETCORE_ENVIRONMENT)\s*[=:]\s*['\"]?{forbidden}\b", ctx.deploy_tree_text()):
            failures.append(f"deploy tree references {forbidden} environment")
    if not ctx.workloads:
        return _vacuous(7, "DevSynthetic; no Development/Testing", "workloads")
    return _result(7, "DevSynthetic; no Development/Testing", failures, f"{len(ctx.workloads)} workload(s) pinned to DevSynthetic")


def guard_08_sec003_fail_closed(ctx: Context) -> GuardResult:
    failures = [f"{w.name} must set {DEPLOYMENT_CLASS_KEY}={DEPLOYMENT_CLASS_VALUE}" for w in ctx.workloads if w.env_value(DEPLOYMENT_CLASS_KEY) != DEPLOYMENT_CLASS_VALUE]
    if not ctx.workloads:
        return _vacuous(8, "SEC-003 fail-closed", "workloads")
    return _result(8, "SEC-003 fail-closed", failures, "every workload declares DEV_SYNTHETIC deployment class")


def guard_09_testing_probes(ctx: Context) -> GuardResult:
    failures = []
    for workload in ctx.workloads:
        for key, entry in workload.env.items():
            if "TESTING" in key.upper():
                failures.append(f"{workload.name} declares testing variable {key}")
            if str(entry.get("value", "")) == "Testing":
                failures.append(f"{workload.name} sets {key}=Testing")
    if re.search(r"PAQUETERIA_TESTING|Environments\.Testing", ctx.deploy_tree_text()):
        failures.append("deploy tree references Testing probes")
    return _result(9, "Testing probes Testing-only", failures, "no Testing environment or testing probe variables in deployment")


def guard_10_mock_ingress(ctx: Context) -> GuardResult:
    failures = []
    subjects = 0
    for workload in ctx.workloads:
        if workload.resource_type != "Microsoft.App/containerApps":
            continue
        ingress = workload.configuration.get("ingress", {}) or {}
        if workload.env_value(MOCK_AUTH_KEY) == MOCK_AUTH_VALUE and ingress.get("external") is True:
            subjects += 1
            restrictions = ingress.get("ipSecurityRestrictions", []) or []
            allows = [r for r in restrictions if str(r.get("action", "")).lower() == "allow"]
            if not allows:
                failures.append(f"{workload.name}: Mock authentication with external ingress requires a non-empty CIDR allowlist")
            for rule in allows:
                if str(rule.get("ipAddressRange", "")) in UNRESTRICTED_CIDRS:
                    failures.append(f"{workload.name}: unrestricted CIDR {rule.get('ipAddressRange')} allowlisted")
    if subjects == 0 and not failures:
        return _vacuous(10, "Mock + unrestricted ingress prohibited", "Mock-authenticated external ingress")
    return _result(10, "Mock + unrestricted ingress prohibited", failures, f"{subjects} Mock ingress boundary(ies) restricted")


def _max_replicas_guard(number: int, ctx: Context, role: str) -> GuardResult:
    title = f"{role.upper() if role == 'api' else role.capitalize()} maxReplicas=1"
    workloads = ctx.workloads_by_role(role)
    if not workloads:
        return _vacuous(number, title, f"{role} Container App")
    failures = []
    for workload in workloads:
        scale = (workload.properties.get("template", {}) or {}).get("scale", {}) or {}
        if scale.get("maxReplicas") != 1:
            failures.append(f"{workload.name} maxReplicas={scale.get('maxReplicas')}")
    return _result(number, title, failures, f"{len(workloads)} workload(s) at maxReplicas=1")


def guard_11_api_max_replicas(ctx: Context) -> GuardResult:
    return _max_replicas_guard(11, ctx, "api")


def guard_12_worker_max_replicas(ctx: Context) -> GuardResult:
    return _max_replicas_guard(12, ctx, "worker")


def guard_13_npgsql_pool_limits(ctx: Context) -> GuardResult:
    failures = []
    subjects = 0
    for path in sorted((ctx.repo_root / "deploy" / "azure").glob("*")):
        if not path.is_file():
            continue
        for line_number, line in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
            if re.search(r"Host=.*;Database=", line):
                subjects += 1
                if not re.search(r"Maximum Pool Size=\d+", line) or not re.search(r"Minimum Pool Size=0", line):
                    failures.append(f"{path.name}:{line_number} connection string without explicit Npgsql pool limits")
    return _result(13, "Npgsql pool limits explicit", failures, f"{subjects} connection string template(s) carry explicit pool limits")


def _proof_storage_guard(number: int, title: str, key: str, ctx: Context) -> GuardResult:
    workloads = [w for w in ctx.workloads if w.role in ("api", "worker")]
    if not workloads:
        return _vacuous(number, title, "API/Worker Container Apps")
    failures = [f"{w.name} must set {key}=Disabled (found {w.env_value(key)})" for w in workloads if w.env_value(key) != PROOF_STORAGE_SETTINGS[key]]
    return _result(number, title, failures, f"{len(workloads)} workload(s) disabled")


def guard_14_proof_storage_disabled(ctx: Context) -> GuardResult:
    return _proof_storage_guard(14, "ProofStorage disabled", "ProofStorage__Provider", ctx)


def guard_15_threat_scanner_disabled(ctx: Context) -> GuardResult:
    return _proof_storage_guard(15, "ThreatScanner disabled", "ProofStorage__ThreatScanner", ctx)


def _prohibited_types_guard(number: int, title: str, key: str, extra_pattern: str | None, ctx: Context) -> GuardResult:
    failures = []
    prefixes = PROHIBITED_TYPE_PREFIXES[key]
    for template in ctx.templates:
        for resource in template.resources:
            resource_type = str(resource.get("type", ""))
            if any(resource_type.startswith(prefix) for prefix in prefixes):
                failures.append(f"{template.name} declares {resource_type}")
    if extra_pattern and re.search(extra_pattern, ctx.deploy_tree_text(), re.IGNORECASE):
        failures.append(f"deploy tree references {key}")
    return _result(number, title, failures, "absent from deployment tree")


def guard_16_no_redis(ctx: Context) -> GuardResult:
    return _prohibited_types_guard(16, "no Redis", "redis", r"\bredis\b", ctx)


def guard_17_no_azure_signalr(ctx: Context) -> GuardResult:
    return _prohibited_types_guard(17, "no Azure SignalR Service", "signalr", r"Azure__SignalR|SignalRService|signalr\.net", ctx)


def guard_18_no_dedicated_profile(ctx: Context) -> GuardResult:
    failures = []
    for environment in ctx.resources("Microsoft.App/managedEnvironments"):
        profiles = (environment.get("properties", {}) or {}).get("workloadProfiles", []) or []
        if not profiles:
            failures.append("managed environment declares no workload profiles (Workload Profiles v2 required)")
        for profile in profiles:
            if str(profile.get("workloadProfileType", "")) != "Consumption":
                failures.append(f"non-Consumption workload profile {profile.get('workloadProfileType')}")
        if (environment.get("properties", {}) or {}).get("vnetConfiguration"):
            failures.append("managed environment declares a custom VNet")
    for workload in ctx.workloads:
        profile = workload.properties.get("workloadProfileName")
        if profile not in (None, "Consumption"):
            failures.append(f"{workload.name} bound to workload profile {profile}")
    return _result(18, "no Dedicated profile", failures, "Consumption only")


def guard_19_no_private_endpoint(ctx: Context) -> GuardResult:
    return _prohibited_types_guard(19, "no Private Endpoint", "private-endpoint", r"privateEndpoint|privateLink", ctx)


def guard_20_no_premium_ingress(ctx: Context) -> GuardResult:
    failures = []
    for environment in ctx.resources("Microsoft.App/managedEnvironments"):
        if (environment.get("properties", {}) or {}).get("ingressConfiguration"):
            failures.append("managed environment declares premium ingressConfiguration")
    if re.search(r"ingressConfiguration|premiumIngress", ctx.deploy_tree_text()):
        failures.append("deploy tree references premium ingress")
    return _result(20, "no Premium Ingress", failures, "no premium ingress configuration")


def guard_21_no_planned_maintenance(ctx: Context) -> GuardResult:
    failures = []
    for server in ctx.resources("Microsoft.DBforPostgreSQL/flexibleServers"):
        window = (server.get("properties", {}) or {}).get("maintenanceWindow", {}) or {}
        if str(window.get("customWindow", "Disabled")).lower() == "enabled":
            failures.append("PostgreSQL declares a custom maintenance window")
    prohibited = _prohibited_types_guard(21, "no planned maintenance", "maintenance", None, ctx)
    if not prohibited.passed:
        failures.append(prohibited.detail)
    return _result(21, "no planned maintenance", failures, "no maintenance configuration")


def guard_22_no_startup_migration(ctx: Context) -> GuardResult:
    failures = []
    for workload in ctx.workloads:
        if workload.resource_type != "Microsoft.App/containerApps":
            continue
        for container in workload.containers:
            invocation = " ".join(str(part) for part in (container.get("command", []) or []) + (container.get("args", []) or []))
            if MIGRATOR_MARKER in invocation or "migrator" in invocation.lower():
                failures.append(f"{workload.name} runs the migrator at startup")
    for name in ("Dockerfile.api", "Dockerfile.worker", "Dockerfile.web"):
        text = (ctx.repo_root / "deploy" / "azure" / name).read_text(encoding="utf-8")
        if MIGRATOR_MARKER in text or "DevSeed" in text:
            failures.append(f"{name} packages migration or seed tooling")
    for workload in ctx.workloads:
        if workload.resource_type == "Microsoft.App/jobs" and workload.role == "migrate":
            invocation = " ".join(str(p) for c in workload.containers for p in (c.get("command", []) or []) + (c.get("args", []) or []))
            if MIGRATOR_MARKER not in invocation:
                failures.append(f"{workload.name} does not invoke the DatabaseMigrator")
    return _result(22, "no startup migration", failures, "migrations run only in the migration job")


def guard_23_acr_admin_disabled(ctx: Context) -> GuardResult:
    registries = ctx.resources("Microsoft.ContainerRegistry/registries")
    failures = []
    if not registries:
        failures.append("no Azure Container Registry declared")
    for registry in registries:
        properties = registry.get("properties", {}) or {}
        if properties.get("adminUserEnabled") is not False:
            failures.append("registry adminUserEnabled must be false")
        if properties.get("anonymousPullEnabled") is True:
            failures.append("registry anonymousPullEnabled must not be true")
        if str((registry.get("sku", {}) or {}).get("name", "")) != "Basic":
            failures.append("registry SKU must be Basic")
    return _result(23, "ACR admin disabled", failures, "adminUserEnabled=false, no anonymous pull, Basic")


def guard_24_no_registry_credentials(ctx: Context) -> GuardResult:
    failures = []
    for workload in ctx.workloads:
        for registry in workload.configuration.get("registries", []) or []:
            if registry.get("username") or registry.get("passwordSecretRef"):
                failures.append(f"{workload.name} uses registry username/password")
    if re.search(r"acr\s+credential|--username|--password|passwordSecretRef|docker login", ctx.deploy_tree_text() + ctx.deploy_workflow_text, re.IGNORECASE):
        failures.append("deploy tree or workflow references registry credentials")
    return _result(24, "no registry username/password", failures, "no registry credentials")


def guard_25_managed_identity_pulls(ctx: Context) -> GuardResult:
    failures = []
    for workload in ctx.workloads:
        registries = workload.configuration.get("registries", []) or []
        if not registries:
            failures.append(f"{workload.name} declares no registry binding")
        for registry in registries:
            if not registry.get("identity"):
                failures.append(f"{workload.name} registry binding without managed identity")
        for secret in workload.configuration.get("secrets", []) or []:
            if secret.get("keyVaultUrl") and not secret.get("identity"):
                failures.append(f"{workload.name} Key Vault secret {secret.get('name')} without managed identity")
            if "value" in secret:
                failures.append(f"{workload.name} declares inline secret {secret.get('name')}")
    for template in ctx.templates:
        for resource in template.resources:
            if resource.get("type") in ("Microsoft.App/containerApps", "Microsoft.App/jobs"):
                identity = resource.get("identity", {}) or {}
                if identity.get("type") != "UserAssigned":
                    failures.append(f"{resource.get('name')} must use a user-assigned managed identity")
    if not ctx.workloads:
        return _vacuous(25, "private workloads use managed identity", "workloads")
    return _result(25, "private workloads use managed identity", failures, f"{len(ctx.workloads)} workload(s) pull and read secrets via managed identity")


def guard_26_authorized_resource_set(ctx: Context) -> GuardResult:
    failures = []
    for template in ctx.templates:
        for resource in template.resources:
            if resource.get("existing"):
                continue
            resource_type = str(resource.get("type", ""))
            if resource_type not in AUTHORIZED_RESOURCE_TYPES:
                failures.append(f"{template.name} declares unauthorized {resource_type}")
        identity_names = template.variables.get("identityNames")
        if isinstance(identity_names, list):
            unexpected = sorted(set(map(str, identity_names)) - NORMATIVE_IDENTITIES)
            missing = sorted(NORMATIVE_IDENTITIES - set(map(str, identity_names)))
            if unexpected or missing:
                failures.append(f"{template.name} identityNames drift: unexpected={unexpected} missing={missing}")
    for match in re.finditer(r"mi-azr-dev-[a-z-]+", ctx.deploy_tree_text()):
        if match.group(0) not in NORMATIVE_IDENTITIES:
            failures.append(f"non-normative identity reference {match.group(0)}")
    return _result(26, "authorized resource set only", failures, "resource types and identities within s.5/s.17")


def _job_secret_guard(number: int, title: str, role: str, predicate: Callable[[str], bool], message: str, ctx: Context) -> GuardResult:
    workloads = ctx.workloads_by_role(role)
    if not workloads:
        return _vacuous(number, title, f"{role} job")
    failures = [f"{w.name} references secret {s} ({message})" for w in workloads for s in w.secret_names if predicate(s)]
    return _result(number, title, failures, f"{len(workloads)} job(s) conform")


def guard_27_acceptance_no_db_secret(ctx: Context) -> GuardResult:
    return _job_secret_guard(27, "Acceptance Job references no DB secret", "acceptance", lambda s: bool(DATABASE_SECRET_PATTERN.search(s)) or s.startswith("inline:"), "database secret", ctx)


def guard_28_rls_runtime_secrets_only(ctx: Context) -> GuardResult:
    return _job_secret_guard(28, "RLS Job references runtime DB secrets only", "rls", lambda s: bool(DATABASE_SECRET_PATTERN.search(s)) and not RUNTIME_SECRET_PATTERN.match(s), "not a runtime LOGIN secret", ctx)


def guard_29_rls_no_privileged_secret(ctx: Context) -> GuardResult:
    return _job_secret_guard(29, "RLS Job references no privileged DB secret", "rls", lambda s: bool(PRIVILEGED_SECRET_PATTERN.search(s)), "privileged secret", ctx)


def guard_30_workload_manifest(ctx: Context) -> GuardResult:
    failures = []
    for workload in ctx.workloads:
        if workload.role is None:
            failures.append(f"{workload.template} declares unauthorized workload {workload.resource_type} '{workload.name}'")
    duplicates = [n for n in {w.name for w in ctx.workloads} if sum(1 for w in ctx.workloads if w.name == n) > 1]
    if duplicates:
        failures.append(f"duplicate workload declarations {duplicates}")
    return _result(30, "workload manifest contains only authorized workloads", failures, f"{len(ctx.workloads)} workload(s) within the s.5 inventory")


def guard_foundation_job_count(ctx: Context) -> GuardResult:
    jobs = ctx.foundation_workflow.get("jobs", {}) or {}
    failures = [] if len(jobs) == FOUNDATION_JOB_COUNT else [f"Foundation must keep exactly {FOUNDATION_JOB_COUNT} jobs, found {len(jobs)}"]
    if ctx.foundation_workflow.get("name") != FOUNDATION_WORKFLOW_NAME:
        failures.append(f"Foundation workflow name must be '{FOUNDATION_WORKFLOW_NAME}'")
    return _result(0, "Foundation keeps exactly 13 jobs (s.28 preamble)", failures, f"{len(jobs)} jobs")


GUARDS: tuple[Callable[[Context], GuardResult], ...] = (
    guard_foundation_job_count,
    guard_01_bicep_build,
    guard_02_dispatch_only,
    guard_03_azure_dev_environment,
    guard_04_no_floating_tags,
    guard_05_deploy_by_digest,
    guard_06_next_public_manifest,
    guard_07_dev_synthetic_environment,
    guard_08_sec003_fail_closed,
    guard_09_testing_probes,
    guard_10_mock_ingress,
    guard_11_api_max_replicas,
    guard_12_worker_max_replicas,
    guard_13_npgsql_pool_limits,
    guard_14_proof_storage_disabled,
    guard_15_threat_scanner_disabled,
    guard_16_no_redis,
    guard_17_no_azure_signalr,
    guard_18_no_dedicated_profile,
    guard_19_no_private_endpoint,
    guard_20_no_premium_ingress,
    guard_21_no_planned_maintenance,
    guard_22_no_startup_migration,
    guard_23_acr_admin_disabled,
    guard_24_no_registry_credentials,
    guard_25_managed_identity_pulls,
    guard_26_authorized_resource_set,
    guard_27_acceptance_no_db_secret,
    guard_28_rls_runtime_secrets_only,
    guard_29_rls_no_privileged_secret,
    guard_30_workload_manifest,
)


def run_guards(ctx: Context) -> list[GuardResult]:
    results = []
    for guard in GUARDS:
        try:
            results.append(guard(ctx))
        except GuardError as error:
            results.append(GuardResult(-1, guard.__name__, "BLOCKED", str(error)))
        except Exception as error:  # noqa: BLE001 - any evaluation failure is BLOCKED, never silently PASS
            results.append(GuardResult(-1, guard.__name__, "BLOCKED", f"{type(error).__name__}: {error}"))
    return results


def format_results(results: Iterable[GuardResult]) -> str:
    lines = []
    for result in results:
        label = "PRE" if result.number == 0 else f"G{result.number:02d}"
        lines.append(f"{label:<4} {result.status:<15} {result.title} :: {result.detail}")
    return "\n".join(lines)


def command_check(args: argparse.Namespace) -> int:
    repo_root = Path(args.repo_root).resolve()
    ctx = load_context(repo_root, Path(args.arm_dir).resolve())
    results = run_guards(ctx)
    print(format_results(results))
    failed = [r for r in results if not r.passed]
    passed = sum(1 for r in results if r.passed)
    if failed:
        print(f"AZR001_STATIC_GUARDS=FAIL ({len(failed)} failing, {passed} passing)")
        return 1
    print(f"AZR001_STATIC_GUARDS=PASS ({passed} guards)")
    return 0


def evaluate_deploy_gate(*, tested_git_sha: str, dispatch_sha: str, frozen_baseline: str, foundation_run: dict[str, Any], expected_run_id: str | None) -> list[str]:
    failures = []
    if not SHA_PATTERN.match(tested_git_sha):
        failures.append("tested_git_sha must be a full 40-hex commit SHA")
    if tested_git_sha != frozen_baseline:
        failures.append(f"tested_git_sha {tested_git_sha} is not the frozen AZR-001 baseline {frozen_baseline}")
    if dispatch_sha != tested_git_sha:
        failures.append(f"dispatched ref {dispatch_sha} differs from tested_git_sha {tested_git_sha}")
    if expected_run_id is not None and str(foundation_run.get("id")) != str(expected_run_id):
        failures.append(f"Foundation run id mismatch: {foundation_run.get('id')} != {expected_run_id}")
    if foundation_run.get("name") != FOUNDATION_WORKFLOW_NAME:
        failures.append(f"run {foundation_run.get('id')} is not {FOUNDATION_WORKFLOW_NAME}")
    if foundation_run.get("head_sha") != tested_git_sha:
        failures.append(f"Foundation run head_sha {foundation_run.get('head_sha')} != tested_git_sha")
    if foundation_run.get("event") != "push" or foundation_run.get("head_branch") != "main":
        failures.append("Foundation run must be a push run on main")
    if foundation_run.get("status") != "completed" or foundation_run.get("conclusion") != "success":
        failures.append(f"Foundation run conclusion is {foundation_run.get('conclusion')} ({foundation_run.get('status')})")
    return failures


def command_deploy_gate(args: argparse.Namespace) -> int:
    repo_root = Path(args.repo_root).resolve()
    try:
        frozen = read_frozen_baseline(repo_root / "deploy" / "azure" / "deploy-core.ps1")
        run = json.loads(Path(args.foundation_run_json).read_text(encoding="utf-8"))
    except (GuardError, OSError, ValueError) as error:
        print(f"AZR001_DEPLOY_GATE=BLOCKED {error}")
        return 2
    failures = evaluate_deploy_gate(
        tested_git_sha=args.tested_git_sha,
        dispatch_sha=args.dispatch_sha,
        frozen_baseline=frozen,
        foundation_run=run,
        expected_run_id=args.foundation_run_id,
    )
    for digest in args.image_digest or []:
        if not DIGEST_PATTERN.match(digest):
            failures.append(f"image reference is not an immutable sha256 digest: {digest}")
    if failures:
        for failure in failures:
            print(f"STOP_FOR_CONTRACT_REVIEW: {failure}")
        print("AZR001_DEPLOY_GATE=FAIL")
        return 1
    print(f"frozen_baseline={frozen}")
    print(f"tested_git_sha={args.tested_git_sha}")
    print(f"foundation_run_id={run.get('id')}")
    print("AZR001_DEPLOY_GATE=PASS")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    check = sub.add_parser("check", help="evaluate the §28 static guards")
    check.add_argument("--repo-root", default=".")
    check.add_argument("--arm-dir", required=True, help="directory holding `bicep build` JSON output, one file per template")
    check.set_defaults(func=command_check)
    gate = sub.add_parser("deploy-gate", help="fail-closed §27 provenance gate")
    gate.add_argument("--repo-root", default=".")
    gate.add_argument("--tested-git-sha", required=True)
    gate.add_argument("--dispatch-sha", required=True)
    gate.add_argument("--foundation-run-id", default=None)
    gate.add_argument("--foundation-run-json", required=True, help="path to the GitHub API JSON of the Foundation run")
    gate.add_argument("--image-digest", action="append", help="image digest input to validate (repeatable)")
    gate.set_defaults(func=command_deploy_gate)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
