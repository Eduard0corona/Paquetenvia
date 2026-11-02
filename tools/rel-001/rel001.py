#!/usr/bin/env python3
"""REL-001 pilot readiness evidence, entry phase (REL-001-PILOT-READINESS-2026-11-02).

AI-08 REL-001 "Puerta piloto operativo" closes in two phases (Z1). This tool builds the **entry** report that
authorizes the start of the pilot; the closing report and its read-only count function in the pilot database (Z6,
second half) are a later security-reviewed change and are only named here as pending.

The report is fail-closed, like REL-000:

* every input has a closed shape; anything unknown, malformed, stale or from another run fails;
* evidence that does not exist yet (the synthetic rehearsal, the restore drill row counts) is reported as MISSING and
  never passes;
* the output is an allowlist of redacted JSON files and a Spanish Markdown summary; it never carries a personal datum,
  a secret, a token, an identifier of an entity or a row value, and it never touches the pilot's data.

Inputs: the tested tree (``tested_git_sha``), the control-plane tree (``main`` head, which holds the owner's decision
record), the certified Foundation run and job records, the run's REL-000 and execution artifacts, the rehearsal
evidence (optional), the restore drill ``EVIDENCE`` line (optional) and the decision record (optional). Definitions and
thresholds come from AI-24 ``pilot_readiness`` and are checked against AI-02 and AI-15.

Commands:
  report   build the entry report (exit 0: ready or approved; 1: report written, not ready; 2: invalid input)
"""

from __future__ import annotations

import argparse
import datetime as dt
import importlib.util
import json
import math
import os
import re
import subprocess
import sys
from pathlib import Path
from typing import Any

import yaml

TOOLS_ROOT = Path(__file__).resolve().parents[1]
FORMAT_VERSION = "paquetenvia-rel001-v1"
RELEASE = "MVP-1_PILOT"
ITEM_ID = "REL-001"
DECISION = "REL-001-PILOT-READINESS-2026-11-02"
PHASE = "ENTRY"

AI02 = Path("docs/normative/v0.6/specs/AI-02_PRODUCT_CONTRACT.yaml")
AI07 = Path("docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml")
AI08 = Path("docs/normative/v0.6/specs/AI-08_BACKLOG.yaml")
AI10 = Path("docs/normative/v0.6/specs/AI-10_DECISIONS_AND_GATES.yaml")
AI15 = Path("docs/normative/v0.6/specs/AI-15_SCALABILITY_CONTRACT.yaml")
AI24 = Path("docs/normative/v0.6/specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml")
AI09 = Path("docs/normative/v0.6/tests/AI-09_ACCEPTANCE_TESTS.feature")
DECISION_LOG = Path("docs/normative/v0.6/decision-log.md")
BROWSER_MANIFEST = Path("tests/fixtures/rel-001/browser-suites.json")
AI09_MAP = Path("tests/fixtures/rel-001/ai09-scenario-map.json")
DECISION_RECORD = Path("docs/releases/mvp-1-pilot-decision.json")

BROWSER_MANIFEST_FORMAT = "paquetenvia-rel001-browser-suites-v1"
AI09_MAP_FORMAT = "paquetenvia-rel001-ai09-map-v1"
REHEARSAL_FORMAT = "paquetenvia-rel001-rehearsal-v1"
DECISION_FORMAT = "paquetenvia-mvp1-pilot-decision-v1"
REL000_REPORT_ARTIFACT = "rel000-mvp0-internal-release-evidence"
REL000_REPORT_FILE = "rel000-internal-release-report.json"
EXECUTION_RESULTS_FILE = "rel000-execution-results.json"
REHEARSAL_FILE = "rel001-rehearsal-evidence.json"

# Z1: the entry report covers plan, rehearsal, restore and regression; the closing report is pending.
CLOSING_PHASE_PENDING = (
    "Closing report: orders_with_complete_events_and_pod >= 0.98 measured on the 50-100 real deliveries through a "
    "read-only count function in the pilot database (Z6, second half), a later security-reviewed change "
    "(AI-06/AI-18, migration with up/down, job). Not built."
)
# Z2 and Z3 are done in their own tasks; REL-001 only lists them.
LISTED_DECISIONS = (
    {"decision": "Z2", "summary": "browser suites land in their own UI-001, AUTH-001 or EXT-001 task; the regression lists them"},
    {"decision": "Z3", "summary": "infrastructure failures answer 503 with a redacted log", "pull_request": 328},
)

# AI-24 pilot_readiness, checked field by field against the normative tree (drift fails).
EXPECTED_READINESS = {
    "decision": DECISION,
    "completeness_threshold": "0.98",
    "minimum_delivered_orders": 50,
    "critical_screens": ["/ops/inbox", "/ops/orders/:id", "/driver/stops", "/driver/stops/:id", "/track/:token"],
    "critical_screen_p95_ms_below": 2000,
    "order_creation_p95_ms_below": 3000,
    "minimum_samples_per_blocking_metric": 100,
    "percentile_method": "nearest-rank",
    "restore_max_days_before_start": 7,
    "restore_pitr_days": 14,
    "delivery_plan_minimum": 50,
    "delivery_plan_maximum": 100,
}
REPORTED_TECHNICAL_METRICS = ("http_p95_ms", "event_delivery_p95_seconds", "signed_url_generation_p95_ms")
APPROVAL_AREAS = ("operations", "finance", "legal")
LEGAL_REVIEW_ROLES = ("OWNER_LAWYER", "PROJECT_OWNER")
DECISION_STATEMENT = "Apruebo el arranque del piloto (REL-001, fase de arranque)"
DECISION_SCOPE = {
    "pilot_start_authorized": True,
    "closing_phase_approved": False,
    "production_authorized": False,
    "recipient_notices_enabled": False,
}

# AI-24 recipient_notices.switches (the AI-08 REL-001 NTF line). Each switch is off when the setting is absent or has
# its off value, its code default is the off value, and no pilot template sets it.
RECIPIENT_NOTICE_SWITCHES = (
    {
        "key": "Notifications:RecipientNotices:Enabled",
        "off": False,
        "source": "src/Modules/Notifications/Notifications.Infrastructure/RecipientNotices/RecipientNoticesOptions.cs",
        "default": r"public sealed class RecipientNoticesOptions\b[^}]*?\bpublic bool Enabled \{ get; set; \}\n",
    },
    {
        "key": "Messaging:WhatsApp:Provider",
        "off": "Disabled",
        "source": "src/BuildingBlocks/Paqueteria.Infrastructure/Messaging/MessagingOptions.cs",
        "default": r"public enum WhatsAppProviderKind\s*\{\s*Disabled,[\s\S]*?public sealed class WhatsAppChannelOptions\s*\{\s*"
                   r"public WhatsAppProviderKind Provider \{ get; set; \}\n",
    },
    {
        "key": "Messaging:Email:Provider",
        "off": "Disabled",
        "source": "src/BuildingBlocks/Paqueteria.Infrastructure/Messaging/MessagingOptions.cs",
        "default": r"public enum EmailProviderKind\s*\{\s*Disabled,[\s\S]*?public sealed class EmailChannelOptions\s*\{\s*"
                   r"public EmailProviderKind Provider \{ get; set; \}\n",
    },
    {
        "key": "Messaging:WhatsApp:Webhook:Enabled",
        "off": False,
        "source": "src/Modules/Notifications/Notifications.Infrastructure/RecipientNotices/Webhooks/RecipientNoticeWebhookOptions.cs",
        "default": r"public sealed class WhatsAppWebhookOptions\s*\{\s*public const string SectionName = "
                   r"\"Messaging:WhatsApp:Webhook\";\s*public bool Enabled \{ get; set; \}\n",
    },
    {
        "key": "Messaging:Email:Events:Enabled",
        "off": False,
        "source": "src/Modules/Notifications/Notifications.Infrastructure/RecipientNotices/Webhooks/RecipientNoticeWebhookOptions.cs",
        "default": r"public sealed class EmailEventsOptions\s*\{\s*public const string SectionName = "
                   r"\"Messaging:Email:Events\";\s*public bool Enabled \{ get; set; \}\n",
    },
    {
        "key": "Locations:NoticeKeyProtector",
        "off": "Disabled",
        "source": "src/Modules/Locations/Locations.Infrastructure/LocationsOptions.cs",
        "default": r"public enum LocationNoticeKeyProtectorKind\s*\{\s*Disabled,[\s\S]*?public sealed class LocationsOptions\b"
                   r"[\s\S]*?public LocationNoticeKeyProtectorKind NoticeKeyProtector \{ get; set; \}\n",
    },
)
APPSETTINGS_HOSTS = ("src/Paqueteria.Api", "src/Paqueteria.Worker")
# Only Development and Testing may name synthetic providers; every other appsettings file reaches the pilot.
NON_PILOT_APPSETTINGS = ("appsettings.Development.json", "appsettings.Testing.json")
PILOT_TEMPLATE_DIR = Path("deploy/azure/pilot")
NOTICE_GATES = ("GATE-004", "GATE-007")

SHA = re.compile(r"^[0-9a-f]{40}$")
RUN_ID = re.compile(r"^[1-9][0-9]{0,19}$")
DATE = re.compile(r"^20[0-9]{2}-[01][0-9]-[0-3][0-9]$")
INSTANT = re.compile(r"^20[0-9]{2}-[01][0-9]-[0-3][0-9]T[0-2][0-9]:[0-5][0-9]:[0-5][0-9]Z$")
DECISION_ID = re.compile(r"^REL-001-ENTRY-[0-9]{3}$")
GATE_ROW = re.compile(r"^(GATE-[0-9]{3})(?:-[A-Z0-9-]+)?$")
# deploy/azure/pilot/restore-drill.sh, last line. The execution name is never published.
RESTORE_LINE = re.compile(
    r"^EVIDENCE restore_point=(?P<restore_point>\S+) started=(?P<started>\S+) restored=(?P<restored>\S+) "
    r"verified=(?P<verified>\S+) verify_status=(?P<verify_status>[A-Za-z]{1,32}) execution=(?P<execution>[A-Za-z0-9-]{1,80})$"
)
# Output redaction: nothing shaped like a personal datum, a secret or an entity identifier may be published.
REDACTION_PATTERNS = (
    ("EMAIL", re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")),
    ("PHONE", re.compile(r"\+[0-9]{8,15}")),
    ("UUID", re.compile(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")),
    ("SECRET", re.compile(r"(?i)(bearer\s+[a-z0-9._-]{8,}|sig=|token=|password=|secret=|accountkey=)")),
    ("URL", re.compile(r"https?://")),
    ("EXECUTION", re.compile(r"(?i)execution=|job-pv-pilot-verify-")),
)
OUTPUT_FILES = (
    "rel001-pilot-readiness-report.json",
    "rel001-regression-evidence.json",
    "rel001-data-completeness.json",
    "rel001-latency-evidence.json",
    "rel001-restore-evidence.json",
    "rel001-delivery-plan.json",
    "rel001-pilot-readiness-report.md",
)

PASSED, FAILED, MISSING, PENDING = "PASSED", "FAILED", "MISSING", "PENDING_OWNER_INPUT"


class Rel001Error(Exception):
    """An input the report cannot be built from. Never carries a value from the input."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(f"{code}: {message}")
        self.code = code


def fail(code: str, message: str) -> None:
    raise Rel001Error(code, message)


def _load_module(name: str, path: Path) -> Any:
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        fail("TOOL_IMPORT_FAILED", f"cannot load {path.name}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


AZR001 = _load_module("rel001_azr001_static_guards", TOOLS_ROOT / "azr-001" / "azr001_static_guards.py")
ENV001 = _load_module("rel001_env001_pilot_guards", TOOLS_ROOT / "azr-001" / "env001_pilot_guards.py")
REL000 = _load_module("rel001_rel000", TOOLS_ROOT / "rel-000" / "rel000.py")


# ------------------------------------------------------------------------------------------------ small helpers
def read_text(root: Path, relative: Path) -> str:
    path = root / relative
    if path.is_symlink() or not path.is_file():
        fail("INPUT_FILE_MISSING", f"{relative.as_posix()} is missing or not a regular file")
    return path.read_text(encoding="utf-8")


def read_yaml(root: Path, relative: Path) -> Any:
    try:
        return yaml.safe_load(read_text(root, relative))
    except yaml.YAMLError:
        fail("INPUT_YAML_INVALID", f"{relative.as_posix()} is not valid YAML")


def read_json_file(path: Path, label: str) -> Any:
    if path.is_symlink() or not path.is_file():
        fail("INPUT_FILE_MISSING", f"{label} is missing or not a regular file")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (UnicodeDecodeError, ValueError):
        fail("INPUT_JSON_INVALID", f"{label} is not valid JSON")


def closed_keys(value: Any, expected: set[str], label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail("INPUT_SHAPE_INVALID", f"{label} must be an object")
    keys = set(value)
    if keys != expected:
        missing, extra = sorted(expected - keys), sorted(keys - expected)
        fail("INPUT_SHAPE_INVALID", f"{label} keys differ (missing {missing}, unexpected {extra})")
    return value


def is_int(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def parse_instant(value: str, label: str) -> dt.datetime:
    if not isinstance(value, str) or not INSTANT.match(value):
        fail("INPUT_INSTANT_INVALID", f"{label} must be YYYY-MM-DDTHH:MM:SSZ")
    try:
        return dt.datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=dt.timezone.utc)
    except ValueError:
        fail("INPUT_INSTANT_INVALID", f"{label} is not a real instant")


def parse_date(value: Any, label: str) -> dt.date:
    if not isinstance(value, str) or not DATE.match(value):
        fail("INPUT_DATE_INVALID", f"{label} must be YYYY-MM-DD")
    try:
        return dt.date.fromisoformat(value)
    except ValueError:
        fail("INPUT_DATE_INVALID", f"{label} is not a real date")


def criterion(status: str, reasons: list[str] | None = None, **fields: Any) -> dict[str, Any]:
    return {"status": status, "reasons": sorted(set(reasons or [])), **fields}


def worst(statuses: list[str]) -> str:
    if FAILED in statuses:
        return FAILED
    if MISSING in statuses:
        return MISSING
    if PENDING in statuses:
        return PENDING
    return PASSED


# ------------------------------------------------------------------------------------------------ normative inputs
def load_normative(tested_root: Path) -> dict[str, Any]:
    backlog = read_yaml(tested_root, AI08)
    entries = backlog.get("items") if isinstance(backlog, dict) else None
    items = [item for item in entries if isinstance(item, dict) and item.get("id") == ITEM_ID] if isinstance(entries, list) else []
    if len(items) != 1:
        fail("NORMATIVE_ITEM_INVALID", "AI-08 must hold exactly one REL-001 item")
    item = items[0]
    if item.get("release") != "MVP-1" or item.get("required_tests") != ["full regression"] \
            or item.get("approval_gate") != "operations_finance_legal":
        fail("NORMATIVE_ITEM_INVALID", "AI-08 REL-001 release, required test or approval gate drifted")
    criteria = item.get("acceptance_criteria") or []
    for literal in ("50-100 entregas reales planificadas", "datos completos >=98%", "restore vigente"):
        if literal not in criteria:
            fail("NORMATIVE_ITEM_INVALID", f"AI-08 REL-001 lost the criterion {literal!r}")
    if not any(isinstance(c, str) and c.startswith("NTF-RECIPIENT-NOTICES-2026-10-20") for c in criteria):
        fail("NORMATIVE_ITEM_INVALID", "AI-08 REL-001 lost its recipient-notice criterion")
    if not any(isinstance(c, str) and c.startswith(f"DECIDED {DECISION}") for c in criteria):
        fail("NORMATIVE_ITEM_INVALID", f"AI-08 REL-001 has no DECIDED {DECISION} criterion")

    product = read_yaml(tested_root, AI02)
    metrics = product.get("mvp_success_metrics") if isinstance(product, dict) else None
    if not isinstance(metrics, dict) or metrics.get("orders_with_complete_events_and_pod") != ">=0.98" \
            or str(metrics.get("critical_screen_p95_seconds")) != "<2" \
            or metrics.get("order_creation_p95_seconds") != "<3 excluding external providers":
        fail("NORMATIVE_DRIFT", "AI-02 mvp_success_metrics no longer hold the REL-001 thresholds")

    scalability = read_yaml(tested_root, AI15)
    signals = scalability.get("scaling_signals") if isinstance(scalability, dict) else None
    technical: dict[str, dict[str, Any]] = {}
    if isinstance(signals, dict):
        for group in signals.values():
            for entry in group if isinstance(group, list) else []:
                if isinstance(entry, dict) and entry.get("metric") in REPORTED_TECHNICAL_METRICS:
                    technical[entry["metric"]] = {"warning": entry.get("warning"), "critical": entry.get("critical")}
    if sorted(technical) != sorted(REPORTED_TECHNICAL_METRICS) or not all(
            is_int(v["warning"]) and is_int(v["critical"]) for v in technical.values()):
        fail("NORMATIVE_DRIFT", "AI-15 scaling_signals lost a reported technical metric")

    runtime = read_yaml(tested_root, AI24)
    readiness = runtime.get("pilot_readiness") if isinstance(runtime, dict) else None
    if not isinstance(readiness, dict):
        fail("NORMATIVE_DRIFT", "AI-24 has no pilot_readiness section")
    observed = {
        "decision": readiness.get("decision"),
        "completeness_threshold": str((readiness.get("data_completeness") or {}).get("threshold")),
        "minimum_delivered_orders": (readiness.get("data_completeness") or {}).get("minimum_delivered_orders"),
        "critical_screens": (readiness.get("latency") or {}).get("critical_screens"),
        "critical_screen_p95_ms_below": (readiness.get("latency") or {}).get("critical_screen_p95_ms_below"),
        "order_creation_p95_ms_below": (readiness.get("latency") or {}).get("order_creation_p95_ms_below"),
        "minimum_samples_per_blocking_metric": (readiness.get("latency") or {}).get("minimum_samples_per_blocking_metric"),
        "percentile_method": (readiness.get("latency") or {}).get("percentile_method"),
        "restore_max_days_before_start": (readiness.get("restore") or {}).get("max_days_before_start"),
        "restore_pitr_days": (readiness.get("restore") or {}).get("pitr_days"),
        "delivery_plan_minimum": (readiness.get("delivery_plan") or {}).get("minimum"),
        "delivery_plan_maximum": (readiness.get("delivery_plan") or {}).get("maximum"),
    }
    drift = sorted(key for key, value in EXPECTED_READINESS.items() if observed.get(key) != value)
    if drift:
        fail("NORMATIVE_DRIFT", f"AI-24 pilot_readiness drifted: {drift}")

    gates = read_yaml(tested_root, AI10)
    pilot_gates = ((gates or {}).get("release_gates") or {}).get(RELEASE) if isinstance(gates, dict) else None
    if not isinstance(pilot_gates, list) or not pilot_gates or not all(isinstance(g, str) and re.fullmatch(r"GATE-[0-9]{3}", g) for g in pilot_gates):
        fail("NORMATIVE_DRIFT", "AI-10 release_gates.MVP-1_PILOT is not a list of gates")
    severities = {d.get("id"): d.get("severity") for d in (gates.get("open_decisions") or []) if isinstance(d, dict)}
    return {
        "depends_on": list(item.get("depends_on") or []),
        "technical_thresholds": technical,
        "release_gates": pilot_gates,
        "gate_severity": {gate: severities.get(gate) for gate in pilot_gates},
    }


# ------------------------------------------------------------------------------------------------ gates
def gate_states(decision_log_text: str, gates: list[str]) -> dict[str, dict[str, Any]]:
    """A gate is RESOLVED by a `GATE-NNN-...` row of Type "Gate resolution", SCOPED by one of Type "Gate scoping"
    (the ENV-001 gate-decision parser) and OPEN otherwise; other rows naming the gate are listed as related."""
    rows = ENV001.decision_log_rows(decision_log_text)
    if not rows:
        fail("DECISION_LOG_INVALID", "decision-log table not found")
    states: dict[str, dict[str, Any]] = {}
    for gate in gates:
        resolved, scoped, related = [], [], []
        for row in rows:
            if len(row) < len(ENV001.DECISION_LOG_HEADER):
                continue
            match = GATE_ROW.match(row[1])
            if not match or match.group(1) != gate:
                continue
            if row[2] == "Gate resolution":
                resolved.append(row[1])
            elif row[2] == "Gate scoping":
                scoped.append(row[1])
            else:
                related.append(row[1])
        status = "RESOLVED" if resolved else "SCOPED" if scoped else "OPEN"
        states[gate] = {"status": status, "resolution_rows": resolved, "scoping_rows": scoped, "related_rows": related}
    return states


def release_gates_criterion(states: dict[str, dict[str, Any]], severity: dict[str, Any]) -> dict[str, Any]:
    open_gates = sorted(gate for gate, state in states.items() if state["status"] == "OPEN")
    gates = [{"gate": gate, "severity": severity.get(gate), **states[gate]} for gate in sorted(states)]
    return criterion(FAILED if open_gates else PASSED, [f"GATE_OPEN:{gate}" for gate in open_gates],
                     source="AI-10 release_gates.MVP-1_PILOT", open_gates=open_gates, gates=gates)


# ------------------------------------------------------------------------------------------------ recipient notices
def strip_bicep_comments(text: str) -> str:
    """Remove `//` and `/* */` comments outside single-quoted strings (Bicep has no other string form)."""
    out: list[str] = []
    i, quoted = 0, False
    while i < len(text):
        char = text[i]
        if quoted:
            out.append(char)
            if char == "\\" and i + 1 < len(text):
                out.append(text[i + 1])
                i += 2
                continue
            if char == "'":
                quoted = False
            i += 1
            continue
        if char == "'":
            quoted = True
            out.append(char)
            i += 1
        elif text.startswith("//", i):
            end = text.find("\n", i)
            i = len(text) if end < 0 else end
        elif text.startswith("/*", i):
            end = text.find("*/", i + 2)
            i = len(text) if end < 0 else end + 2
        else:
            out.append(char)
            i += 1
    return "".join(out)


def _config_value(document: Any, key: str) -> tuple[bool, Any]:
    """Case-insensitive .NET configuration lookup of `A:B:C` in a JSON object."""
    node = document
    for part in key.split(":"):
        if not isinstance(node, dict):
            return False, None
        matches = [k for k in node if isinstance(k, str) and k.lower() == part.lower()]
        if len(matches) != 1:
            if len(matches) > 1:
                fail("APPSETTINGS_AMBIGUOUS", f"{key} appears more than once with different casing")
            return False, None
        node = node[matches[0]]
    return True, node


def _is_off(value: Any, off: Any) -> bool:
    if off is False:
        return value is False or (isinstance(value, str) and value.strip().lower() == "false")
    return isinstance(value, str) and value.strip().lower() == str(off).lower()


def recipient_notice_switches(tested_root: Path) -> list[dict[str, Any]]:
    settings: list[tuple[str, Any]] = []
    for host in APPSETTINGS_HOSTS:
        directory = tested_root / host
        if not directory.is_dir():
            fail("INPUT_FILE_MISSING", f"{host} is missing")
        for path in sorted(directory.glob("appsettings*.json")):
            if path.name in NON_PILOT_APPSETTINGS:
                continue
            settings.append((f"{host}/{path.name}", read_json_file(path, f"{host}/{path.name}")))
    templates: list[tuple[str, str]] = []
    pilot_dir = tested_root / PILOT_TEMPLATE_DIR
    for path in sorted([*pilot_dir.glob("*.bicep"), *pilot_dir.glob("*.json")]):
        text = path.read_text(encoding="utf-8")
        templates.append((f"{PILOT_TEMPLATE_DIR.as_posix()}/{path.name}",
                          strip_bicep_comments(text) if path.suffix == ".bicep" else text))
    results = []
    for switch in RECIPIENT_NOTICE_SWITCHES:
        findings: list[str] = []
        for name, document in settings:
            present, value = _config_value(document, switch["key"])
            if present and not _is_off(value, switch["off"]):
                findings.append(f"ON_IN:{name}")
        env_name = switch["key"].replace(":", "__").lower()
        for name, text in templates:
            if env_name in text.lower():
                findings.append(f"SET_IN:{name}")
        source = tested_root / switch["source"]
        code = source.read_text(encoding="utf-8") if source.is_file() else ""
        if not re.search(switch["default"], code):
            findings.append(f"DEFAULT_NOT_OFF:{switch['source']}")
        results.append({"switch": switch["key"], "off": not findings, "findings": sorted(findings)})
    return results


def recipient_notice_criterion(states: dict[str, dict[str, Any]], switches: list[dict[str, Any]]) -> dict[str, Any]:
    gates_resolved = all(states.get(gate, {}).get("status") == "RESOLVED" for gate in NOTICE_GATES)
    on = [s["switch"] for s in switches if not s["off"]]
    status = PASSED if gates_resolved or not on else FAILED
    return criterion(status, [f"SWITCH_ON_WHILE_GATES_OPEN:{name}" for name in on] if status == FAILED else [],
                     source="AI-08 REL-001 NTF line; AI-24 recipient_notices.switches",
                     gates={gate: states.get(gate, {}).get("status", "OPEN") for gate in NOTICE_GATES},
                     gates_resolved=gates_resolved, all_switches_off=not on, switches=switches)


# ------------------------------------------------------------------------------------------------ Foundation run
def foundation_provenance(args: argparse.Namespace, control_root: Path) -> dict[str, Any]:
    run = read_json_file(Path(args.foundation_run_json), "Foundation run record")
    jobs = read_json_file(Path(args.foundation_jobs_json), "Foundation jobs record")
    tested, dispatch = args.tested_git_sha, args.dispatch_sha
    if not SHA.match(tested) or not SHA.match(dispatch) or not RUN_ID.match(args.foundation_run_id):
        fail("INPUT_PROVENANCE_INVALID", "tested_git_sha, dispatch sha or foundation_run_id is malformed")
    ancestry = AZR001.git_is_ancestor(control_root, tested, dispatch)
    failures = AZR001.evaluate_deploy_gate(
        tested_git_sha=tested, dispatch_ref=args.dispatch_ref, dispatch_sha=dispatch, tested_is_ancestor=ancestry,
        foundation_run=run, foundation_jobs=jobs, expected_run_id=args.foundation_run_id)
    if failures:
        fail("FOUNDATION_NOT_CERTIFIED", f"{len(failures)} provenance check(s) failed; run the AZR-001 deploy-gate for details")
    job_list = jobs.get("jobs") if isinstance(jobs, dict) else jobs
    return {
        "workflow": "Foundation CI",
        "event": "push",
        "branch": "main",
        "foundation_run_id": str(run.get("id")),
        "foundation_run_attempt": run.get("run_attempt"),
        "tested_git_sha": tested,
        "control_plane_sha": dispatch,
        "git_relationship": "same_commit" if tested == dispatch else "tested_is_ancestor_of_control_plane",
        "jobs_successful": len(job_list),
        "jobs_expected": AZR001.FOUNDATION_JOB_COUNT,
        "job_names": sorted(job.get("name") for job in job_list),
    }


def load_execution_manifests(artifacts_dir: Path, run_id: str, tested: str) -> tuple[dict[str, list[dict[str, Any]]], list[str]]:
    manifests: dict[str, list[dict[str, Any]]] = {}
    problems: list[str] = []
    for job, artifact in sorted(REL000.EXECUTION_ARTIFACT_NAMES.items()):
        directory = artifacts_dir / artifact
        if directory.is_symlink() or not directory.is_dir():
            problems.append(f"ARTIFACT_MISSING:{artifact}")
            continue
        found = [p for p in directory.rglob(EXECUTION_RESULTS_FILE) if p.is_file() and not p.is_symlink()]
        if len(found) != 1:
            problems.append(f"ARTIFACT_RESULTS_NOT_UNIQUE:{artifact}")
            continue
        manifest = read_json_file(found[0], f"{artifact}/{EXECUTION_RESULTS_FILE}")
        if not isinstance(manifest, dict) or manifest.get("format_version") != REL000.EXECUTION_EVIDENCE_VERSION \
                or manifest.get("job") != job or manifest.get("artifact_name") != artifact:
            problems.append(f"ARTIFACT_FORMAT_INVALID:{artifact}")
            continue
        if str(manifest.get("workflow_run_id")) != run_id or manifest.get("tested_git_sha") != tested:
            problems.append(f"ARTIFACT_FROM_ANOTHER_RUN:{artifact}")
            continue
        tests = manifest.get("tests")
        if not isinstance(tests, list) or not tests or not all(
                isinstance(t, dict) and isinstance(t.get("fully_qualified_test_name"), str) and t.get("job") == job
                for t in tests):
            problems.append(f"ARTIFACT_TESTS_INVALID:{artifact}")
            continue
        manifests[job] = tests
    return manifests, problems


def rel000_report_check(artifacts_dir: Path, run_id: str, tested: str) -> tuple[dict[str, Any], list[str]]:
    path = artifacts_dir / REL000_REPORT_ARTIFACT / REL000_REPORT_FILE
    if path.is_symlink() or not path.is_file():
        return {"present": False}, [f"ARTIFACT_MISSING:{REL000_REPORT_ARTIFACT}"]
    report = read_json_file(path, REL000_REPORT_FILE)
    problems = []
    if not isinstance(report, dict) or report.get("format_version") != "paquetenvia-rel000-v1":
        return {"present": True}, ["REL000_REPORT_FORMAT_INVALID"]
    if str(report.get("workflow_run_id")) != run_id or report.get("tested_git_sha") != tested:
        problems.append("REL000_REPORT_FROM_ANOTHER_RUN")
    expected = {"technical_evidence_status": "PASSED", "dependency_security_status": "PASSED",
                "ops001_evidence_valid": True, "ops002_restore_drill_passed": True,
                "cross_tenant_incidents_observed": 0}
    for key, value in expected.items():
        if report.get(key) != value:
            problems.append(f"REL000_{key.upper()}_NOT_PASSED")
    summary = {"present": True, **{key: report.get(key) for key in expected}, "result": report.get("result")}
    return summary, problems


def _identity_results(identity: str, tests: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """An identity is a full test name or a class name (every test of the class)."""
    return [t for t in tests if t["fully_qualified_test_name"] == identity
            or t["fully_qualified_test_name"].startswith(identity + ".")]


def verify_identity(job: str, identity: str, manifests: dict[str, list[dict[str, Any]]]) -> dict[str, Any]:
    if job not in manifests:
        return {"job": job, "test": identity, "status": MISSING, "reason": "JOB_EVIDENCE_MISSING", "executed": 0}
    results = _identity_results(identity, manifests[job])
    executed = [r for r in results if r.get("executed") is True and r.get("skipped") is False]
    passed = [r for r in executed if r.get("outcome") == "PASSED"]
    if not results:
        return {"job": job, "test": identity, "status": MISSING, "reason": "NOT_EXECUTED_IN_RUN", "executed": 0}
    if len(passed) != len(results):
        return {"job": job, "test": identity, "status": FAILED, "reason": "FAILED_OR_SKIPPED", "executed": len(executed)}
    return {"job": job, "test": identity, "status": PASSED, "executed": len(passed)}


def load_browser_manifest(tested_root: Path) -> list[dict[str, Any]]:
    document = read_json_file(tested_root / BROWSER_MANIFEST, BROWSER_MANIFEST.as_posix())
    closed_keys(document, {"format_version", "source", "screens"}, "browser-suite manifest")
    if document["format_version"] != BROWSER_MANIFEST_FORMAT or document["source"] != AI07.as_posix():
        fail("MANIFEST_INVALID", "browser-suite manifest format or source drifted")
    ui = read_yaml(tested_root, AI07)
    expected = [route for route, value in (ui.get("routes") or {}).items()
                if isinstance(value, dict) and value.get("release") in ("MVP-0", "MVP-1")]
    expected += [f"screen:{name}" for name, value in (ui.get("screen_contracts") or {}).items()
                 if isinstance(value, dict) and value.get("release") == "MVP-1" and "route" not in value]
    screens = document["screens"]
    names = [s.get("screen") for s in screens if isinstance(s, dict)]
    if len(names) != len(screens) or len(set(names)) != len(names) or set(names) != set(expected):
        fail("MANIFEST_NOT_CLOSED", "browser-suite manifest is not exactly the AI-07 MVP-0/MVP-1 screens")
    for entry in screens:
        _check_manifest_entry(entry, {"screen", "status", "suites"}, "suites", {"COVERED", "GAP"}, "owner_task")
    return screens


def ai09_scenarios(feature_text: str) -> list[tuple[str, str]]:
    scenarios, current = [], None
    for line in feature_text.splitlines():
        stripped = line.strip()
        match = re.match(r"^(Característica|Feature):\s*(.+)$", stripped)
        if match:
            current = match.group(2).strip()
            continue
        match = re.match(r"^(Escenario|Esquema del escenario|Scenario):\s*(.+)$", stripped)
        if match:
            if current is None:
                fail("AI09_INVALID", "a scenario appears before any feature")
            scenarios.append((current, match.group(2).strip()))
    return scenarios


def load_ai09_map(tested_root: Path) -> list[dict[str, Any]]:
    document = read_json_file(tested_root / AI09_MAP, AI09_MAP.as_posix())
    closed_keys(document, {"format_version", "source", "scenarios"}, "AI-09 map")
    if document["format_version"] != AI09_MAP_FORMAT or document["source"] != AI09.as_posix():
        fail("MANIFEST_INVALID", "AI-09 map format or source drifted")
    expected = ai09_scenarios(read_text(tested_root, AI09))
    entries = document["scenarios"]
    keys = [(e.get("feature"), e.get("scenario")) for e in entries if isinstance(e, dict)]
    if len(keys) != len(entries) or len(set(keys)) != len(keys) or set(keys) != set(expected) or len(set(expected)) != len(expected):
        fail("MANIFEST_NOT_CLOSED", "AI-09 map is not exactly the AI-09 scenarios")
    for entry in entries:
        _check_manifest_entry(entry, {"feature", "scenario", "status", "tests"}, "tests",
                              {"COVERED", "GAP", "NOT_APPLICABLE"}, None)
    return entries


def _check_manifest_entry(entry: dict[str, Any], base: set[str], tests_key: str, statuses: set[str], gap_key: str | None) -> None:
    status = entry.get("status")
    if status not in statuses:
        fail("MANIFEST_INVALID", "manifest entry has an unknown status")
    expected = set(base)
    if status in ("GAP", "NOT_APPLICABLE"):
        expected.add("reason")
        if gap_key and status == "GAP":
            expected.add(gap_key)
    closed_keys(entry, expected, "manifest entry")
    if "reason" in entry and (not isinstance(entry["reason"], str) or not entry["reason"].strip()):
        fail("MANIFEST_INVALID", "a GAP or NOT_APPLICABLE entry needs a reason")
    tests = entry[tests_key]
    if not isinstance(tests, list):
        fail("MANIFEST_INVALID", "manifest tests must be a list")
    if status == "COVERED" and not tests:
        fail("MANIFEST_INVALID", "a COVERED entry needs at least one test")
    if status == "NOT_APPLICABLE" and tests:
        fail("MANIFEST_INVALID", "a NOT_APPLICABLE entry lists no test")
    for test in tests:
        closed_keys(test, {"job", "test"}, "manifest test")
        if test["job"] not in REL000.EXECUTION_ARTIFACT_NAMES or not isinstance(test["test"], str) or not test["test"]:
            fail("MANIFEST_INVALID", "manifest test names an unknown job or an empty identity")


def regression_criterion(provenance: dict[str, Any], rel000_summary: dict[str, Any], rel000_problems: list[str],
                         manifests: dict[str, list[dict[str, Any]]], artifact_problems: list[str],
                         screens: list[dict[str, Any]], scenarios: list[dict[str, Any]]) -> dict[str, Any]:
    failed: list[str] = []
    missing: list[str] = []
    for problem in [*rel000_problems, *artifact_problems]:
        (missing if problem.startswith("ARTIFACT_MISSING:") else failed).append(problem)
    unclean = sorted(job for job, tests in manifests.items()
                     if any(t.get("outcome") != "PASSED" or t.get("skipped") is not False for t in tests))
    failed += [f"RUN_HAS_FAILED_OR_SKIPPED_TESTS:{job}" for job in unclean]

    def evaluate(entries: list[dict[str, Any]], key: str, tests_key: str, label: str) -> list[dict[str, Any]]:
        out = []
        for entry in entries:
            checks = [verify_identity(t["job"], t["test"], manifests) for t in entry[tests_key]]
            if entry["status"] == "COVERED":
                status = worst([c["status"] for c in checks])
                for check in checks:
                    if check["status"] == FAILED:
                        failed.append(f"{label}_TEST_FAILED:{entry[key]}")
                    elif check["status"] == MISSING:
                        missing.append(f"{label}_TEST_MISSING:{entry[key]}")
            elif entry["status"] == "GAP":
                status = FAILED
                failed.append(f"{label}_GAP:{entry[key]}")
            else:
                status = "NOT_APPLICABLE"
            row = {key: entry[key], "manifest_status": entry["status"], "status": status, "tests": checks}
            if "feature" in entry:
                row["feature"] = entry["feature"]
            for extra in ("reason", "owner_task"):
                if extra in entry:
                    row[extra] = entry[extra]
            out.append(row)
        return out

    def counts(rows: list[dict[str, Any]], field: str) -> dict[str, int]:
        return {value: sum(1 for row in rows if row[field] == value) for value in sorted({row[field] for row in rows})}

    browser = evaluate(screens, "screen", "suites", "BROWSER_SUITE")
    ai09 = evaluate(scenarios, "scenario", "tests", "AI09_SCENARIO")
    status = FAILED if failed else MISSING if missing else PASSED
    return criterion(
        status, failed + missing, source="AI-08 REL-001 required test: full regression",
        foundation=provenance, rel000=rel000_summary,
        execution_jobs={job: len(tests) for job, tests in sorted(manifests.items())},
        browser_suites={"source": AI07.as_posix(), "manifest_counts": counts(browser, "manifest_status"),
                        "status_counts": counts(browser, "status"), "screens": browser},
        ai09_scenarios={"source": AI09.as_posix(), "manifest_counts": counts(ai09, "manifest_status"),
                        "status_counts": counts(ai09, "status"), "scenarios": ai09},
        listed_decisions=list(LISTED_DECISIONS))


# ------------------------------------------------------------------------------------------------ rehearsal (PR-B)
def nearest_rank_p95_is_valid(sample: dict[str, Any]) -> bool:
    return all(is_int(sample.get(k)) and sample[k] >= 0 for k in ("samples", "p50_ms", "p95_ms", "p99_ms")) \
        and sample["p50_ms"] <= sample["p95_ms"] <= sample["p99_ms"]


def rehearsal_criteria(rehearsal_dir: Path | None, tested: str, workflow_run_id: str | None,
                       thresholds: dict[str, dict[str, Any]]) -> tuple[dict[str, Any], dict[str, Any]]:
    note = "CI runners do not represent the pilot's burstable database server; the latencies are not a pilot measurement."
    if rehearsal_dir is None:
        reason = ["REHEARSAL_EVIDENCE_MISSING"]
        return (criterion(MISSING, reason, source="AI-02 orders_with_complete_events_and_pod; Z6 entry half"),
                criterion(MISSING, reason, source="AI-02 critical_screen_p95_seconds and order_creation_p95_seconds; Z7, Z8",
                          environment_note=note))
    document = read_json_file(rehearsal_dir / REHEARSAL_FILE, "rehearsal evidence")
    closed_keys(document, {"format_version", "tested_git_sha", "workflow_run_id", "workflow_run_attempt",
                           "environment", "journey", "completeness", "latency"}, "rehearsal evidence")
    if document["format_version"] != REHEARSAL_FORMAT:
        fail("REHEARSAL_INVALID", "rehearsal evidence format drifted")
    if document["tested_git_sha"] != tested or str(document["workflow_run_id"]) != str(workflow_run_id or ""):
        fail("REHEARSAL_FROM_ANOTHER_RUN", "rehearsal evidence names another commit or run")
    environment = closed_keys(document["environment"], {"kind", "azure", "proof_storage", "proof_scanner",
                                                        "representative_of_pilot_database"}, "rehearsal environment")
    if environment != {"kind": "LOCAL_CI_SYNTHETIC", "azure": False, "proof_storage": "S3_COMPATIBLE_DOUBLE",
                       "proof_scanner": "SYNTHETIC", "representative_of_pilot_database": False}:
        fail("REHEARSAL_INVALID", "the rehearsal must be the Z4 local/CI synthetic stack")
    journey = closed_keys(document["journey"], {"cod_recorded_reconciled_closed", "failed_attempt_decided_redelivered",
                                                "csv_import_created_orders", "settlement_calculated_approved_paid_exported"},
                          "rehearsal journey")
    journey_failed = sorted(step for step, value in journey.items() if value != PASSED)

    completeness = closed_keys(document["completeness"], {"delivered_orders", "complete_orders", "missing"},
                               "rehearsal completeness")
    missing = closed_keys(completeness["missing"], {"event_version_gaps", "unaudited_status_changes",
                                                    "missing_pickup_photo", "missing_delivery_photo"}, "completeness missing")
    numbers = [completeness["delivered_orders"], completeness["complete_orders"], *missing.values()]
    if not all(is_int(n) and n >= 0 for n in numbers):
        fail("REHEARSAL_INVALID", "completeness counts must be non-negative integers")
    delivered, complete = completeness["delivered_orders"], completeness["complete_orders"]
    incomplete = delivered - complete
    if complete > delivered or any(v > incomplete for v in missing.values()) or (incomplete > 0 and sum(missing.values()) < incomplete):
        fail("REHEARSAL_INVALID", "completeness counts are inconsistent")
    reasons = [f"JOURNEY_STEP_NOT_PASSED:{step}" for step in journey_failed]
    if delivered < EXPECTED_READINESS["minimum_delivered_orders"]:
        reasons.append("TOO_FEW_DELIVERED_ORDERS")
    if delivered == 0 or complete * 100 < delivered * 98:
        reasons.append("COMPLETENESS_BELOW_0_98")
    data = criterion(FAILED if reasons else PASSED, reasons,
                     source="AI-02 orders_with_complete_events_and_pod; Z6 entry half", measured_in="SYNTHETIC_REHEARSAL",
                     threshold=">=0.98", delivered_orders=delivered, complete_orders=complete,
                     ratio=round(complete / delivered, 4) if delivered else None, missing=missing, journey=journey)

    latency = closed_keys(document["latency"], {"critical_screens", "order_creation", "technical"}, "rehearsal latency")
    screens = latency["critical_screens"]
    if not isinstance(screens, dict) or sorted(screens) != sorted(EXPECTED_READINESS["critical_screens"]):
        fail("REHEARSAL_INVALID", "latency must name exactly the Z8 critical screens")
    blocking, lat_reasons = [], []
    minimum = EXPECTED_READINESS["minimum_samples_per_blocking_metric"]
    for name, sample, limit in [*[(s, screens[s], EXPECTED_READINESS["critical_screen_p95_ms_below"]) for s in sorted(screens)],
                                ("order_creation", latency["order_creation"], EXPECTED_READINESS["order_creation_p95_ms_below"])]:
        closed_keys(sample, {"samples", "p50_ms", "p95_ms", "p99_ms"}, f"latency {name}")
        if not nearest_rank_p95_is_valid(sample):
            fail("REHEARSAL_INVALID", f"latency {name} is not a valid percentile summary")
        status = PASSED
        if sample["samples"] < minimum:
            status = FAILED
            lat_reasons.append(f"TOO_FEW_SAMPLES:{name}")
        if sample["p95_ms"] >= limit:
            status = FAILED
            lat_reasons.append(f"P95_AT_OR_ABOVE_LIMIT:{name}")
        blocking.append({"metric": name, "limit_p95_ms_below": limit, "status": status, **sample})
    technical = latency["technical"]
    if not isinstance(technical, dict) or not set(technical) <= set(REPORTED_TECHNICAL_METRICS):
        fail("REHEARSAL_INVALID", "technical latencies name an unknown metric")
    reported = []
    for metric in REPORTED_TECHNICAL_METRICS:
        value = technical.get(metric)
        level = thresholds[metric]
        if value is None:
            reported.append({"metric": metric, "level": "NOT_MEASURED", **level})
            continue
        if not isinstance(value, (int, float)) or isinstance(value, bool) or value < 0 or not math.isfinite(value):
            fail("REHEARSAL_INVALID", f"technical latency {metric} is not a non-negative number")
        tier = "CRITICAL" if value >= level["critical"] else "WARNING" if value >= level["warning"] else "OK"
        reported.append({"metric": metric, "p95": value, "level": tier, **level})
    lat = criterion(FAILED if lat_reasons else PASSED, lat_reasons,
                    source="AI-02 critical_screen_p95_seconds and order_creation_p95_seconds; Z7, Z8",
                    measured_in="SYNTHETIC_REHEARSAL", percentile_method=EXPECTED_READINESS["percentile_method"],
                    blocking=blocking, reported_only=reported, environment_note=note)
    return data, lat


# ------------------------------------------------------------------------------------------------ restore (PR-C)
def restore_criterion(line: str | None, planned_start: dt.date | None, generated_at: dt.datetime,
                      rel000_summary: dict[str, Any], manifests: dict[str, list[dict[str, Any]]]) -> dict[str, Any]:
    ci_drill_ok = rel000_summary.get("ops002_restore_drill_passed") is True and "backup-restore" in manifests
    ci = {"ops002_restore_drill_passed": ci_drill_ok, "row_count_comparison": MISSING}
    reasons = ["CI_DRILL_ROW_COUNTS_MISSING"]
    if not ci_drill_ok:
        reasons.append("CI_DRILL_NOT_PASSED")
    if not line:
        reasons.append("PILOT_DRILL_EVIDENCE_MISSING")
        return criterion(FAILED if not ci_drill_ok else MISSING, reasons, source="AI-08 REL-001 restore vigente; Z9",
                         pilot_drill={"present": False}, ci_drill=ci)
    match = RESTORE_LINE.match(line.strip())
    if not match:
        fail("RESTORE_EVIDENCE_INVALID", "the restore drill line is not the EVIDENCE line of restore-drill.sh")
    times = {k: parse_instant(match.group(k), f"restore {k}") for k in ("restore_point", "started", "restored", "verified")}
    drill: dict[str, Any] = {"present": True, "verify_status": match.group("verify_status"),
                             **{k: v.strftime("%Y-%m-%dT%H:%M:%SZ") for k, v in times.items()},
                             "execution_recorded": True, "row_count_comparison": MISSING, "asserted_git_sha": MISSING}
    status = MISSING
    if match.group("verify_status") != "Succeeded":
        reasons.append("PILOT_DRILL_NOT_SUCCEEDED")
        status = FAILED
    if not times["restore_point"] <= times["started"] <= times["restored"] <= times["verified"] <= generated_at:
        reasons.append("PILOT_DRILL_TIMES_OUT_OF_ORDER")
        status = FAILED
    if times["started"] - times["restore_point"] > dt.timedelta(days=EXPECTED_READINESS["restore_pitr_days"]):
        reasons.append("RESTORE_POINT_OUTSIDE_PITR_WINDOW")
        status = FAILED
    basis = dt.datetime.combine(planned_start, dt.time(0, 0), dt.timezone.utc) if planned_start else generated_at
    age = basis - times["verified"]
    drill["freshness_basis"] = "PLANNED_START" if planned_start else "REPORT_TIME"
    drill["age_hours"] = int(age.total_seconds() // 3600)
    if age < dt.timedelta(0) or age > dt.timedelta(days=EXPECTED_READINESS["restore_max_days_before_start"]):
        reasons.append("PILOT_DRILL_NOT_CURRENT")
        status = FAILED
    # deploy/azure/pilot/restore-drill.sh does not yet compare row counts nor name the commit its assert ran: the
    # ENV-001 follow-up (Z9) adds both. Until then the criterion cannot pass.
    reasons += ["PILOT_DRILL_ROW_COUNTS_MISSING", "PILOT_DRILL_COMMIT_MISSING"]
    if not ci_drill_ok:
        status = FAILED
    return criterion(status, reasons, source="AI-08 REL-001 restore vigente; Z9", pilot_drill=drill, ci_drill=ci)


# ------------------------------------------------------------------------------------------------ decision record
def load_decision_record(path: Path | None) -> tuple[dict[str, Any] | None, list[str]]:
    if path is None or not path.exists():
        return None, []
    try:
        record = read_json_file(path, "decision record")
        closed_keys(record, {"format_version", "decision_id", "release", "phase", "decision", "decision_statement",
                             "decided_by", "decided_on", "approvals", "delivery_plan", "approved_evidence", "scope"},
                    "decision record")
        if record["format_version"] != DECISION_FORMAT or record["release"] != RELEASE or record["phase"] != PHASE:
            fail("DECISION_RECORD_INVALID", "format, release or phase is wrong")
        if not isinstance(record["decision_id"], str) or not DECISION_ID.match(record["decision_id"]):
            fail("DECISION_RECORD_INVALID", "decision_id must be REL-001-ENTRY-NNN")
        if record["decision"] != "APPROVE" or record["decision_statement"] != DECISION_STATEMENT \
                or record["decided_by"] != "project_owner":
            fail("DECISION_RECORD_INVALID", "decision, statement or decider is not the Z10 approval")
        parse_date(record["decided_on"], "decided_on")
        approvals = closed_keys(record["approvals"], set(APPROVAL_AREAS), "approvals")
        for area in APPROVAL_AREAS:
            keys = {"approved_by", "legal_review_by_role"} if area == "legal" else {"approved_by"}
            closed_keys(approvals[area], keys, f"approval {area}")
            if approvals[area]["approved_by"] != "project_owner":
                fail("DECISION_RECORD_INVALID", "Z10: the project owner approves every area")
        if approvals["legal"]["legal_review_by_role"] not in LEGAL_REVIEW_ROLES:
            fail("DECISION_RECORD_INVALID", "legal_review_by_role is not a recorded role")
        plan = closed_keys(record["delivery_plan"], {"planned_real_deliveries", "period_start", "period_end",
                                                     "sender_count", "zone_count"}, "delivery_plan")
        if not all(is_int(plan[key]) for key in ("planned_real_deliveries", "sender_count", "zone_count")):
            fail("DECISION_RECORD_INVALID", "delivery_plan counts must be integers")
        parse_date(plan["period_start"], "period_start")
        parse_date(plan["period_end"], "period_end")
        anchors = closed_keys(record["approved_evidence"], {"tested_git_sha", "foundation_run_id",
                                                            "restore_drill_verified_at"}, "approved_evidence")
        if not isinstance(anchors["tested_git_sha"], str) or not SHA.match(anchors["tested_git_sha"]) \
                or not isinstance(anchors["foundation_run_id"], str) or not RUN_ID.match(anchors["foundation_run_id"]):
            fail("DECISION_RECORD_INVALID", "approved_evidence names a malformed commit or run")
        parse_instant(anchors["restore_drill_verified_at"], "restore_drill_verified_at")
        if record["scope"] != DECISION_SCOPE:
            fail("DECISION_RECORD_INVALID", "scope flags are not the entry-phase scope")
        return record, []
    except Rel001Error as error:
        return None, [error.code]


def delivery_plan_criterion(record: dict[str, Any] | None, record_problems: list[str]) -> tuple[dict[str, Any], dt.date | None]:
    source = "AI-08 REL-001 50-100 entregas reales planificadas; Z12"
    if record_problems:
        return criterion(FAILED, record_problems, source=source, recorded_in=DECISION_RECORD.as_posix()), None
    if record is None:
        return criterion(PENDING, ["DECISION_RECORD_ABSENT"], source=source, recorded_in=DECISION_RECORD.as_posix()), None
    plan = record["delivery_plan"]
    reasons = []
    numbers = (plan["planned_real_deliveries"], plan["sender_count"], plan["zone_count"])
    start, end = parse_date(plan["period_start"], "period_start"), parse_date(plan["period_end"], "period_end")
    decided = parse_date(record["decided_on"], "decided_on")
    if not all(n >= 1 for n in numbers):
        reasons.append("PLAN_COUNTS_INVALID")
    else:
        total, senders, zones = numbers
        if not EXPECTED_READINESS["delivery_plan_minimum"] <= total <= EXPECTED_READINESS["delivery_plan_maximum"]:
            reasons.append("PLAN_TOTAL_OUTSIDE_50_100")
        if senders > total or zones > total:
            reasons.append("PLAN_COUNTS_INCONSISTENT")
    if end < start:
        reasons.append("PLAN_PERIOD_INVERTED")
    if start < decided:
        reasons.append("PLAN_STARTS_BEFORE_DECISION")
    summary = {"planned_real_deliveries": plan["planned_real_deliveries"], "period_start": plan["period_start"],
               "period_end": plan["period_end"], "period_days": (end - start).days + 1 if end >= start else None,
               "sender_count": plan["sender_count"], "zone_count": plan["zone_count"]}
    return criterion(FAILED if reasons else PASSED, reasons, source=source, recorded_in=DECISION_RECORD.as_posix(),
                     plan=summary, personal_data=False), start


def approval_status(record: dict[str, Any] | None, record_problems: list[str], provenance: dict[str, Any],
                    restore: dict[str, Any]) -> dict[str, Any]:
    if record_problems:
        return criterion(FAILED, record_problems, decision_record=DECISION_RECORD.as_posix())
    if record is None:
        return criterion(PENDING, ["DECISION_RECORD_ABSENT"], decision_record=DECISION_RECORD.as_posix())
    anchors = record["approved_evidence"]
    reasons = []
    if anchors["tested_git_sha"] != provenance["tested_git_sha"]:
        reasons.append("APPROVED_COMMIT_DIFFERS")
    if anchors["foundation_run_id"] != provenance["foundation_run_id"]:
        reasons.append("APPROVED_FOUNDATION_RUN_DIFFERS")
    drill = restore.get("pilot_drill") or {}
    if anchors["restore_drill_verified_at"] != drill.get("verified"):
        reasons.append("APPROVED_RESTORE_DRILL_DIFFERS")
    approvals = record["approvals"]
    return criterion(FAILED if reasons else PASSED, reasons, decision_record=DECISION_RECORD.as_posix(),
                     decision_id=record["decision_id"], decided_by=record["decided_by"], decided_on=record["decided_on"],
                     approvals={area: {"approved_by": approvals[area]["approved_by"]} for area in APPROVAL_AREAS},
                     legal_review_by_role=approvals["legal"]["legal_review_by_role"])


# ------------------------------------------------------------------------------------------------ report
def build_report(args: argparse.Namespace) -> dict[str, Any]:
    tested_root, control_root = Path(args.tested_root).resolve(), Path(args.control_root).resolve()
    generated_at = parse_instant(args.generated_at, "generated_at")
    normative = load_normative(tested_root)
    provenance = foundation_provenance(args, control_root)
    head = subprocess.run(["git", "-C", str(tested_root), "rev-parse", "HEAD"], capture_output=True, text=True)
    if head.returncode != 0 or head.stdout.strip() != provenance["tested_git_sha"]:
        fail("TESTED_TREE_MISMATCH", "the tested tree is not a checkout of tested_git_sha")
    run_id, tested = provenance["foundation_run_id"], provenance["tested_git_sha"]
    artifacts = Path(args.artifacts_dir).resolve()
    manifests, artifact_problems = load_execution_manifests(artifacts, run_id, tested)
    rel000_summary, rel000_problems = rel000_report_check(artifacts, run_id, tested)
    screens, scenarios = load_browser_manifest(tested_root), load_ai09_map(tested_root)
    regression = regression_criterion(provenance, rel000_summary, rel000_problems, manifests, artifact_problems,
                                      screens, scenarios)
    states = gate_states(read_text(tested_root, DECISION_LOG), normative["release_gates"])
    gates = release_gates_criterion(states, normative["gate_severity"])
    notices = recipient_notice_criterion(gate_states(read_text(tested_root, DECISION_LOG), list(NOTICE_GATES)),
                                         recipient_notice_switches(tested_root))
    rehearsal_dir = Path(args.rehearsal_dir).resolve() if args.rehearsal_dir else None
    completeness, latency = rehearsal_criteria(rehearsal_dir, tested, args.workflow_run_id, normative["technical_thresholds"])
    record_path = (control_root / DECISION_RECORD) if args.decision_record else None
    record, record_problems = load_decision_record(record_path)
    plan, planned_start = delivery_plan_criterion(record, record_problems)
    line = os.environ.get(args.restore_evidence_env, "") if args.restore_evidence_env else ""
    restore = restore_criterion(line or None, planned_start, generated_at, rel000_summary, manifests)
    approval = approval_status(record, record_problems, provenance, restore)

    technical = {"full_regression": regression, "data_completeness": completeness, "latency": latency,
                 "restore_vigente": restore, "recipient_notices_line": notices, "release_gates": gates}
    technical_status = worst([c["status"] for c in technical.values()])
    blocking = sorted(name for name, c in {**technical, "delivery_plan": plan, "owner_approval": approval}.items()
                      if c["status"] != PASSED)
    if technical_status != PASSED or FAILED in (plan["status"], approval["status"]):
        entry_status = "NOT_READY"
    elif plan["status"] == PASSED and approval["status"] == PASSED:
        entry_status = "APPROVED_FOR_PILOT_START"
    else:
        entry_status = "READY_FOR_OWNER_DECISION"
    summary = {
        "format_version": FORMAT_VERSION,
        "release": RELEASE,
        "item": ITEM_ID,
        "decision": DECISION,
        "phase": PHASE,
        "generated_at_utc": generated_at.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "provenance": provenance,
        "readiness_workflow_run_id": args.workflow_run_id,
        "depends_on": normative["depends_on"],
        "criteria": {name: {"status": c["status"], "reasons": c["reasons"]} for name, c in
                     {**technical, "delivery_plan": plan, "owner_approval": approval}.items()},
        "technical_status": technical_status,
        "owner_approval_status": approval["status"],
        "entry_status": entry_status,
        "blocking_criteria": blocking,
        "open_gates": gates["open_gates"],
        "recipient_notices": {"all_switches_off": notices["all_switches_off"], "gates": notices["gates"]},
        "closing_phase": {"status": "NOT_STARTED", "pending": CLOSING_PHASE_PENDING},
        "scope": {"pilot_start_authorized": entry_status == "APPROVED_FOR_PILOT_START", "production_authorized": False,
                  "closing_phase_approved": False, "real_pilot_data_read": False},
        "release_gates": gates,
        "recipient_notices_line": notices,
        "owner_approval": approval,
    }
    return {
        "rel001-pilot-readiness-report.json": summary,
        "rel001-regression-evidence.json": {"format_version": FORMAT_VERSION, **regression},
        "rel001-data-completeness.json": {"format_version": FORMAT_VERSION, **completeness},
        "rel001-latency-evidence.json": {"format_version": FORMAT_VERSION, **latency},
        "rel001-restore-evidence.json": {"format_version": FORMAT_VERSION, **restore},
        "rel001-delivery-plan.json": {"format_version": FORMAT_VERSION, **plan},
    }


STATUS_ES = {PASSED: "Cumple", FAILED: "No cumple", MISSING: "Falta evidencia", PENDING: "Pendiente del owner",
             "NOT_APPLICABLE": "No aplica"}
ENTRY_ES = {"NOT_READY": "No listo para arrancar", "READY_FOR_OWNER_DECISION": "Listo para la decisión del owner",
            "APPROVED_FOR_PILOT_START": "Arranque del piloto aprobado"}
CRITERIA_ES = {
    "full_regression": "Regresión completa (Foundation 13/13, REL-000, pantallas y escenarios AI-09)",
    "data_completeness": "Datos completos ≥98% (ensayo sintético)",
    "latency": "Latencias de producto (pantallas críticas p95 < 2 s, alta de orden p95 < 3 s)",
    "restore_vigente": "Restore vigente (simulacro de 7 días o menos, con conteos)",
    "recipient_notices_line": "Avisos al destinatario apagados hasta GATE-004 y GATE-007",
    "release_gates": "Compuertas de AI-10 para MVP-1_PILOT",
    "delivery_plan": "Plan de 50 a 100 entregas reales (conteos en la decisión)",
    "owner_approval": "Aprobación de operaciones, finanzas y legal",
}


MANIFEST_ES = {"COVERED": "con prueba", "GAP": "sin prueba", "NOT_APPLICABLE": "no aplican al piloto"}


def counts_es(counts: dict[str, int]) -> str:
    return ", ".join(f"{value} {MANIFEST_ES[key]}" for key, value in counts.items())


def render_markdown(files: dict[str, Any]) -> str:
    report = files["rel001-pilot-readiness-report.json"]
    regression = files["rel001-regression-evidence.json"]
    latency = files["rel001-latency-evidence.json"]
    provenance = report["provenance"]
    lines = [
        "# Reporte de preparación del piloto (REL-001, fase de arranque)",
        "",
        "Resumen generado por `tools/rel-001/rel001.py`. Los JSON redactados son la fuente; este texto solo los explica.",
        "",
        "```text",
        f"release = {report['release']}",
        f"decision = {report['decision']}",
        f"tested_git_sha = {provenance['tested_git_sha']}",
        f"foundation_run_id = {provenance['foundation_run_id']}",
        f"foundation_jobs = {provenance['jobs_successful']}/{provenance['jobs_expected']}",
        f"generated_at_utc = {report['generated_at_utc']}",
        f"technical_status = {report['technical_status']}",
        f"owner_approval_status = {report['owner_approval_status']}",
        f"entry_status = {report['entry_status']}",
        "```",
        "",
        f"**Estado:** {ENTRY_ES[report['entry_status']]}.",
        "",
        "## Criterios",
        "",
        "| Criterio | Estado | Motivos |",
        "|---|---|---|",
    ]
    for name, value in report["criteria"].items():
        shown = value["reasons"][:8]
        reasons = ", ".join(f"`{reason}`" for reason in shown) or "—"
        if len(value["reasons"]) > len(shown):
            reasons += f" y {len(value['reasons']) - len(shown)} más"
        lines.append(f"| {CRITERIA_ES[name]} | {STATUS_ES[value['status']]} | {reasons} |")
    lines += ["", "## Compuertas abiertas (AI-10, MVP-1_PILOT)", ""]
    lines += [f"- {gate}" for gate in report["open_gates"]] or ["- Ninguna."]
    notices = report["recipient_notices"]
    lines += ["", "## Avisos al destinatario", "",
              f"- Interruptores apagados: {'sí' if notices['all_switches_off'] else 'no'}.",
              f"- GATE-004: {notices['gates']['GATE-004']}; GATE-007: {notices['gates']['GATE-007']}.",
              "", "## Regresión completa", "",
              f"- Pantallas de AI-07 (MVP-0 y MVP-1): {counts_es(regression['browser_suites']['manifest_counts'])}.",
              f"- Escenarios de AI-09: {counts_es(regression['ai09_scenarios']['manifest_counts'])}.",
              "- Las pruebas de navegador que faltan se hacen en su propia tarea (Z2); REL-001 solo las lista.",
              "- Las fallas de base de datos responden 503 con una línea de log sin datos personales (Z3, PR #328).", ""]
    gaps = [row for row in regression["browser_suites"]["screens"] if row["manifest_status"] == "GAP"]
    if gaps:
        lines += ["Pantallas sin prueba de navegador:", ""]
        lines += [f"- `{row['screen']}` ({row['owner_task']})" for row in gaps]
        lines.append("")
    gaps = [row for row in regression["ai09_scenarios"]["scenarios"] if row["manifest_status"] == "GAP"]
    if gaps:
        lines += ["Escenarios de AI-09 sin prueba:", ""]
        lines += [f"- {row['feature']}: {row['scenario']}" for row in gaps]
        lines.append("")
    lines += ["## Latencias", "",
              "Los corredores de CI no representan el servidor de base de datos burstable del piloto: estas latencias "
              "no son una medición del piloto. Solo bloquean las de producto (AI-02); las técnicas de AI-15 solo se "
              "reportan (Z7).", ""]
    if latency.get("blocking"):
        lines += ["| Medición | Muestras | p95 (ms) | Límite p95 (ms) | Estado |", "|---|---|---|---|---|"]
        lines += [f"| `{row['metric']}` | {row['samples']} | {row['p95_ms']} | < {row['limit_p95_ms_below']} | "
                  f"{STATUS_ES[row['status']]} |" for row in latency["blocking"]]
        lines.append("")
    lines += ["## Fase de cierre", "",
              "Pendiente: el reporte de cierre mide el 98% con las entregas reales mediante una función de solo conteo "
              "en la base del piloto, revisada como cambio de seguridad (Z6). No se construye en esta fase.", ""]
    return "\n".join(lines)


def assert_redacted(name: str, text: str) -> None:
    for label, pattern in REDACTION_PATTERNS:
        if pattern.search(text):
            fail("OUTPUT_NOT_REDACTED", f"{name} contains a {label}-shaped value")


def write_outputs(output: Path, files: dict[str, Any]) -> None:
    if output.exists():
        fail("OUTPUT_EXISTS", "the output directory must not exist")
    rendered = {name: json.dumps(value, ensure_ascii=False, indent=2, sort_keys=False) + "\n" for name, value in files.items()}
    rendered["rel001-pilot-readiness-report.md"] = render_markdown(files)
    if tuple(sorted(rendered)) != tuple(sorted(OUTPUT_FILES)):
        fail("OUTPUT_ALLOWLIST_MISMATCH", "the report files are not the allowlist")
    for name, text in rendered.items():
        assert_redacted(name, text)
    output.mkdir(parents=True)
    for name in OUTPUT_FILES:
        (output / name).write_text(rendered[name], encoding="utf-8")


# ------------------------------------------------------------------------------------------------ CLI
def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    report = sub.add_parser("report", help="build the REL-001 entry report")
    report.add_argument("--tested-root", required=True, help="checkout of tested_git_sha")
    report.add_argument("--control-root", required=True, help="checkout of the dispatched main head (full history)")
    report.add_argument("--tested-git-sha", required=True)
    report.add_argument("--foundation-run-id", required=True)
    report.add_argument("--foundation-run-json", required=True)
    report.add_argument("--foundation-jobs-json", required=True)
    report.add_argument("--dispatch-ref", required=True)
    report.add_argument("--dispatch-sha", required=True)
    report.add_argument("--artifacts-dir", required=True, help="the certified run's REL-000 and execution artifacts")
    report.add_argument("--workflow-run-id", default=None, help="this readiness run (the rehearsal must come from it)")
    report.add_argument("--rehearsal-dir", default=None, help="the rehearsal evidence of this run (absent: MISSING)")
    report.add_argument("--restore-evidence-env", default=None, help="name of the environment variable holding the drill line")
    report.add_argument("--decision-record", action="store_true", help=f"read {DECISION_RECORD.as_posix()} from the control root")
    report.add_argument("--generated-at", default=dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"))
    report.add_argument("--output", required=True)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        files = build_report(args)
        write_outputs(Path(args.output), files)
    except Rel001Error as error:
        print(json.dumps({"result": "REL001_REPORT_NOT_BUILT", "reason_code": error.code, "detail": str(error)}))
        return 2
    except Exception as error:  # noqa: BLE001 - fail closed without echoing input values
        print(json.dumps({"result": "REL001_REPORT_NOT_BUILT", "reason_code": "INTERNAL_ERROR",
                          "detail": type(error).__name__}))
        return 2
    report = files["rel001-pilot-readiness-report.json"]
    print(json.dumps({"result": "REL001_REPORT_BUILT", "entry_status": report["entry_status"],
                      "technical_status": report["technical_status"], "blocking_criteria": report["blocking_criteria"]}))
    return 0 if report["entry_status"] in ("READY_FOR_OWNER_DECISION", "APPROVED_FOR_PILOT_START") else 1


if __name__ == "__main__":
    raise SystemExit(main())
