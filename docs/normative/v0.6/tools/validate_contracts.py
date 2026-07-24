#!/usr/bin/env python3
"""Static contract validator for Paqueteria Culiacan AI package v0.6."""
from __future__ import annotations

import collections
import hashlib
import json
import pathlib
import re
import sys
from typing import Any

import yaml

ROOT = pathlib.Path(__file__).resolve().parents[1]


def load_yaml(relative: str) -> Any:
    return yaml.safe_load((ROOT / relative).read_text(encoding="utf-8"))


def resolve_pointer(document: Any, ref: str) -> Any:
    current = document
    for part in ref[2:].split("/"):
        part = part.replace("~1", "/").replace("~0", "~")
        current = current[int(part)] if isinstance(current, list) else current[part]
    return current


def collect_refs(value: Any, output: list[str]) -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            if key == "$ref" and isinstance(child, str) and child.startswith("#/"):
                output.append(child)
            collect_refs(child, output)
    elif isinstance(value, list):
        for child in value:
            collect_refs(child, output)


def write_integrity() -> None:
    manifest_path = ROOT / "MANIFEST.json"
    checksums_path = ROOT / "CHECKSUMS_SHA256.txt"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))

    checksum_files = sorted(
        path
        for path in ROOT.rglob("*")
        if path.is_file()
        and path not in {manifest_path, checksums_path}
        and "__pycache__" not in path.parts
        and path.suffix != ".pyc"
    )
    checksum_lines = [
        f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.relative_to(ROOT).as_posix()}"
        for path in checksum_files
    ]
    checksums_path.write_text("\n".join(checksum_lines) + "\n", encoding="utf-8", newline="\n")

    identity_files = sorted(
        path
        for path in ROOT.rglob("*")
        if path.is_file()
        and path != manifest_path
        and "__pycache__" not in path.parts
        and path.suffix != ".pyc"
    )
    manifest["canonical_sql_sha256"] = hashlib.sha256(
        (ROOT / "database/AI-06_SCHEMA.sql").read_bytes()
    ).hexdigest()
    manifest["file_count"] = len(identity_files)
    manifest["files"] = [
        {
            "path": path.relative_to(ROOT).as_posix(),
            "bytes": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        }
        for path in identity_files
    ]
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    print(f"INTEGRITY_WRITTEN: {len(identity_files)} files")


def main() -> int:
    errors: list[str] = []
    checks: list[str] = []

    parsed: dict[str, Any] = {}
    for path in sorted(ROOT.rglob("*.yaml")):
        rel = path.relative_to(ROOT).as_posix()
        try:
            parsed[rel] = yaml.safe_load(path.read_text(encoding="utf-8"))
        except Exception as exc:  # pragma: no cover - validation utility
            errors.append(f"YAML parse failed: {rel}: {exc}")
    checks.append(f"YAML parsed: {len(parsed)}")

    backlog = parsed["specs/AI-08_BACKLOG.yaml"]
    items = backlog["items"]
    ids = [item["id"] for item in items]
    if len(ids) != len(set(ids)):
        errors.append("Duplicate backlog IDs")
    id_set = set(ids)
    missing = [(item["id"], dep) for item in items for dep in item.get("depends_on", []) if dep not in id_set]
    if missing:
        errors.append(f"Missing backlog dependencies: {missing}")
    graph = {item["id"]: item.get("depends_on", []) for item in items}
    state: dict[str, int] = {}
    cycles: list[list[str]] = []

    def dfs(node: str, path: list[str]) -> None:
        state[node] = 1
        for dependency in graph[node]:
            if state.get(dependency) == 1:
                cycles.append(path + [node, dependency])
            elif state.get(dependency, 0) == 0:
                dfs(dependency, path + [node])
        state[node] = 2

    for node in graph:
        if state.get(node, 0) == 0:
            dfs(node, [])
    if cycles:
        errors.append(f"Backlog cycles: {cycles[:2]}")
    if backlog["meta"]["item_count"] != len(items):
        errors.append("Backlog item_count mismatch")
    checks.append(f"Backlog: {len(items)} tasks, DAG, dependencies resolved")

    api = parsed["contracts/AI-05_OPENAPI.yaml"]
    refs: list[str] = []
    collect_refs(api, refs)
    for ref in refs:
        try:
            resolve_pointer(api, ref)
        except Exception as exc:
            errors.append(f"Bad OpenAPI ref {ref}: {exc}")
    operation_ids: list[str] = []
    for path_item in api["paths"].values():
        for method, operation in path_item.items():
            if method.lower() in {"get", "post", "put", "patch", "delete"} and isinstance(operation, dict):
                operation_ids.append(operation.get("operationId"))
    duplicates = [key for key, count in collections.Counter(operation_ids).items() if key and count > 1]
    if duplicates:
        errors.append(f"Duplicate operationIds: {duplicates}")
    checks.append(
        f"OpenAPI: {len(api['paths'])} paths, {len(api['components']['schemas'])} schemas, {len(refs)} refs"
    )

    assign = api["paths"]["/orders/{orderId}/assignments"]["post"]
    expected_responses = {
        "401": "#/components/responses/Unauthorized",
        "403": "#/components/responses/Forbidden",
        "404": "#/components/responses/UniformNotFound",
        "409": "#/components/responses/DispatchAssignmentConflict",
    }
    actual_responses = {str(status): response for status, response in assign["responses"].items()}
    if list(actual_responses) != ["201", "401", "403", "404", "409"]:
        errors.append(f"DSP-002 response status matrix drift: {list(actual_responses)}")
    if (
        actual_responses.get("201", {})
        .get("content", {})
        .get("application/json", {})
        .get("schema", {})
        .get("$ref")
        != "#/components/schemas/Assignment"
    ):
        errors.append("DSP-002 201 response schema drift")
    actual_problem_refs = {
        status: actual_responses.get(status, {}).get("$ref")
        for status in expected_responses
    }
    if actual_problem_refs != expected_responses:
        errors.append(f"DSP-002 problem response refs drift: {actual_problem_refs}")
    if (
        assign.get("x-authorization-precedence")
        != "shape-validation-then-capability-before-persisted-state"
    ):
        errors.append("DSP-002 authorization precedence drift")
    if assign.get("x-shape-validation") != "invalid-request-without-productive-transaction":
        errors.append("DSP-002 request-shape validation precedence drift")
    if assign.get("x-capability-protected-state") != [
        "idempotency_lock",
        "idempotency_record",
        "replay_evidence",
        "order_packages",
        "driver_profile_documents",
    ]:
        errors.append("DSP-002 capability-protected state drift")
    if assign.get("x-authorized-visibility-plan") != [
        "order_packages",
        "driver_profile_documents",
    ]:
        errors.append("DSP-002 non-enumerable visibility plan drift")
    if (
        assign.get("x-non-enumeration")
        != "structural-postgresql-no-artificial-delay"
    ):
        errors.append("DSP-002 non-enumeration mechanism drift")

    assignment_request = api["components"]["schemas"]["CreateAssignmentRequest"]
    if assignment_request.get("additionalProperties") is not False:
        errors.append("DSP-002 request must reject undeclared properties")
    assignment_type = assignment_request["properties"]["assignment_type"]
    if assignment_type.get("enum") != ["OWN", "EXTERNAL", "ALLY_CAPACITY"]:
        errors.append("DSP-002 global assignment_type enum drift")
    if assignment_type.get("x-dsp-002-enabled-values") != ["OWN"]:
        errors.append("DSP-002 enabled assignment_type values drift")
    if assignment_type.get("x-reserved-for") != {
        "EXTERNAL": "EXT-001",
        "ALLY_CAPACITY": "ALY-004",
    }:
        errors.append("DSP-002 reserved assignment_type ownership drift")
    route_id = assignment_request["properties"]["route_id"]
    if route_id.get("type") != ["string", "null"] or route_id.get("format") != "uuid":
        errors.append("DSP-002 route_id must remain nullable UUID")
    if route_id.get("x-dsp-002-support") != "omitted-or-null-only":
        errors.append("DSP-002 route_id incremental support drift")

    dispatch_conflict = api["components"]["schemas"]["DispatchAssignmentConflictProblem"]
    if dispatch_conflict["properties"]["code"].get("enum") != [
        "INVALID_REQUEST",
        "CONFLICT",
        "DRIVER_INELIGIBLE",
        "DRIVER_DOCUMENT_EXPIRED",
    ]:
        errors.append("DSP-002 safe conflict-code set drift")
    checks.append(
        "DSP-002: shape validation, capability-before-state, structural visibility and 201/401/403/404/409 contract"
    )

    product = parsed["specs/AI-02_PRODUCT_CONTRACT.yaml"]
    domain = parsed["specs/AI-04_DOMAIN_MODEL.yaml"]
    signalr = parsed["contracts/AI-12_SIGNALR_CONTRACT.yaml"]
    sql = (ROOT / "database/AI-06_SCHEMA.sql").read_text(encoding="utf-8")
    role_sql = (ROOT / "database/AI-18_DATABASE_ROLE_MODEL.sql").read_text(encoding="utf-8")

    statuses = domain["order_state_machine"]["statuses"]
    status_match = re.search(
        r"status text NOT NULL CHECK \(status IN \(([^)]*'CANCELLED'[^)]*)\)\),\n  subtotal_cents",
        sql,
        re.S,
    )
    sql_statuses = re.findall(r"'([^']+)'", status_match.group(1)) if status_match else []
    api_statuses = api["components"]["schemas"]["OrderStatus"]["enum"]
    if not (statuses == product["authoritative_statuses"] == sql_statuses == api_statuses):
        errors.append("Internal order status drift")
    checks.append("Internal order statuses: 17 consistent")

    public_contract = domain["public_tracking_contract"]
    mapping = public_contract["internal_to_public"]
    map_match = re.search(
        r"FUNCTION security\.map_public_order_status[\s\S]*?SELECT CASE p_status([\s\S]*?)ELSE NULL", sql
    )
    sql_map = dict(re.findall(r"WHEN '([^']+)' THEN '([^']+)'", map_match.group(1))) if map_match else {}
    if set(mapping) != set(statuses):
        errors.append("Public status mapping does not cover exactly the internal statuses")
    if sql_map != mapping:
        errors.append("SQL public status mapping drift")
    if signalr["events"]["PublicOrderStatusChanged"]["public_status_enum"] != public_contract["public_statuses"]:
        errors.append("SignalR public status enum drift")
    if api["components"]["schemas"]["PublicOrderStatus"]["enum"] != public_contract["public_statuses"]:
        errors.append("OpenAPI public status enum drift")
    checks.append("Public status mapping: 17 contract/SQL/SignalR/OpenAPI entries consistent")

    tables = set(re.findall(r"CREATE TABLE\s+([a-z_]+\.[a-z_]+)", sql, re.I))
    references = re.findall(r"REFERENCES\s+([a-z_]+\.[a-z_]+)", sql, re.I)
    missing_tables = sorted(set(references) - tables)
    if missing_tables:
        errors.append(f"Missing FK target tables: {missing_tables}")
    checks.append(f"SQL tables: {len(tables)}; foreign-key targets resolved")

    required_sql = [
        "CREATE SCHEMA IF NOT EXISTS extensions",
        "CREATE EXTENSION IF NOT EXISTS postgis;",
        "CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA extensions;",
        "extensions.digest(pg_catalog.convert_to(p_token,'UTF8'),'sha256')",
        "CREATE TABLE orders.order_acceptances",
        "public_event_code text CHECK",
        "driver_position_id uuid NOT NULL UNIQUE",
        "lease_token uuid",
        "lease_expires_at timestamptz",
    ]
    for fragment in required_sql:
        if fragment not in sql:
            errors.append(f"Missing SQL contract: {fragment}")
    location_table = re.search(r"CREATE TABLE platform\.location_outbox_events \(([\s\S]*?)\n\);", sql)
    if location_table and "REFERENCES drivers.driver_positions" in location_table.group(1):
        errors.append("Location outbox foreign key still present")
    lifecycle = [
        "claim_outbox",
        "settle_outbox",
        "requeue_stale_outbox",
        "purge_outbox",
        "claim_location_outbox",
        "settle_location_outbox",
        "requeue_stale_location_outbox",
        "purge_location_outbox",
    ]
    for function in lifecycle:
        if f"FUNCTION security.{function}" not in sql:
            errors.append(f"Missing SQL function: {function}")
        if f"FUNCTION security.{function}" not in role_sql:
            errors.append(f"Missing role-model function binding: {function}")
    checks.append("SQL runtime hardening: extensions, acceptance, leases and eight lifecycle functions present")

    purge_contracts = {
        "purge_outbox": "platform.outbox_events",
        "purge_location_outbox": "platform.location_outbox_events",
    }
    for function, table in purge_contracts.items():
        function_match = re.search(
            rf"CREATE OR REPLACE FUNCTION security\.{function}\([\s\S]*?AS \$\$([\s\S]*?)END \$\$;",
            sql,
        )
        if not function_match:
            errors.append(f"Missing purge body: {function}")
            continue
        body = function_match.group(1)
        if "FOR UPDATE" in body or "SKIP LOCKED" in body:
            errors.append(f"{function} still requires UPDATE privilege for candidate locking")
        if f"DELETE FROM {table} o" not in body:
            errors.append(f"{function} does not delete from its canonical lane")
        if "o.status='PROCESSED'" not in body or "o.status='DEAD'" not in body:
            errors.append(f"{function} does not recheck terminal state in the DELETE target")
        if "o.processed_at < p_processed_before" not in body:
            errors.append(f"{function} does not recheck the processed cutoff in the DELETE target")
        if "COALESCE(o.processed_at,o.created_at) < p_dead_before" not in body:
            errors.append(f"{function} does not recheck the dead cutoff in the DELETE target")

    if "GRANT SELECT,DELETE ON platform.outbox_events,platform.location_outbox_events TO paqueteria_maintenance;" not in role_sql:
        errors.append("Maintenance no longer has the exact SELECT/DELETE outbox grant")
    if re.search(r"GRANT[^;]*UPDATE[^;]*TO paqueteria_maintenance", role_sql, re.S):
        errors.append("Maintenance was granted UPDATE contrary to ADR-030")
    checks.append("Purge remediation: target predicates rechecked; maintenance remains SELECT/DELETE without row locking")

    for line_number, line in enumerate(sql.splitlines(), start=1):
        stripped = line.strip()
        if re.match(r"^[a-z_][a-z0-9_]*_cents[a-z0-9_]*\s+", stripped):
            if " bigint" not in f" {stripped}":
                errors.append(f"Non-bigint cents column at SQL line {line_number}: {stripped}")
    bad_money: list[str] = []

    def inspect_money(value: Any, path: tuple[str, ...] = ()) -> None:
        if isinstance(value, dict):
            for key, child in value.items():
                if isinstance(child, dict) and "_cents" in key:
                    if not (child.get("type") == "integer" and child.get("format") == "int64"):
                        bad_money.append("/".join(path + (key,)))
                inspect_money(child, path + (key,))
        elif isinstance(value, list):
            for index, child in enumerate(value):
                inspect_money(child, path + (str(index),))

    inspect_money(api)
    if bad_money:
        errors.append(f"OpenAPI cents properties not int64: {bad_money}")
    checks.append("Money: SQL bigint cents and OpenAPI int64")

    required_roles = [
        "REVOKE SELECT,UPDATE,DELETE ON platform.outbox_events,platform.location_outbox_events",
        "GRANT INSERT ON platform.outbox_events,platform.location_outbox_events",
        "paqueteria_outbox_executor",
        "paqueteria_maintenance",
        "orders.order_acceptances",
        "REVOKE CREATE ON SCHEMA public",
        "GRANT USAGE ON SCHEMA public",
        "ALTER FUNCTION security.purge_outbox",
        "ALTER FUNCTION security.settle_outbox",
        "ALTER FUNCTION security.map_public_order_status",
        "ALTER TABLE %I.%I OWNER TO paqueteria_migrator",
    ]
    for fragment in required_roles:
        if fragment not in role_sql:
            errors.append(f"Missing role-model contract: {fragment}")
    if "GRANT UPDATE (" in role_sql:
        errors.append("Legacy direct column UPDATE grant remains")
    runtime_grant_block = re.search(r"-- Runtime table grants[\s\S]*?END \$\$;", role_sql)
    if runtime_grant_block and "'platform'" in runtime_grant_block.group(0):
        errors.append("platform remains in broad runtime grants")
    checks.append("Role model: ownership normalized; outbox access function-only; maintenance separated")

    runtime = parsed["specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml"]
    token_vector = runtime["tracking_token"]["test_vector"]
    if hashlib.sha256(token_vector["token"].encode("utf-8")).hexdigest() != token_vector["sha256_hex"]:
        errors.append("Tracking token hash vector invalid")
    acceptance_vector = runtime["legal_acceptance"]["test_vector"]
    if hashlib.sha256(acceptance_vector["canonical_utf8"].encode("utf-8")).hexdigest() != acceptance_vector["sha256_hex"]:
        errors.append("Legal acceptance canonical vector invalid")
    checks.append("Cryptographic test vectors recomputed successfully")

    gherkin = (ROOT / "tests/AI-09_ACCEPTANCE_TESTS.feature").read_text(encoding="utf-8")
    for feature in [
        "Extensiones y hash simétrico v0.6",
        "Lifecycle del outbox con lease v0.6",
        "Tracking público fail-closed v0.6",
        "Evidencia legal de aceptación v0.6",
        "Provisioning transaccional v0.6",
        "Dinero de 64 bits v0.6",
    ]:
        if feature not in gherkin:
            errors.append(f"Missing Gherkin feature: {feature}")
    checks.append("Acceptance tests include v0.6 runtime/security scenarios")

    if sql.count("$$") % 2 or role_sql.count("$$") % 2:
        errors.append("Unbalanced SQL dollar delimiters")
    checks.append("SQL dollar delimiters balanced")

    checksums_path = ROOT / "CHECKSUMS_SHA256.txt"
    checksum_entries = []
    for line in checksums_path.read_text(encoding="utf-8").splitlines():
        digest, relative = line.split("  ", maxsplit=1)
        checksum_entries.append((digest, relative))
        path = ROOT / relative
        if not path.exists():
            errors.append(f"Checksum file missing: {relative}")
            continue
        if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            errors.append(f"Checksum mismatch: {relative}")
    checks.append(f"Checksum entries checked: {len(checksum_entries)}")

    manifest_path = ROOT / "MANIFEST.json"
    if manifest_path.exists():
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        for entry in manifest.get("files", []):
            path = ROOT / entry["path"]
            if not path.exists():
                errors.append(f"Manifest file missing: {entry['path']}")
                continue
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            if digest != entry["sha256"]:
                errors.append(f"Manifest hash mismatch: {entry['path']}")
        checks.append(f"Manifest entries checked: {len(manifest.get('files', []))}")

    for check in checks:
        print(f"PASS: {check}")
    if errors:
        for error in errors:
            print(f"FAIL: {error}", file=sys.stderr)
        return 1
    print("VALIDATION_OK")
    return 0


if __name__ == "__main__":
    if sys.argv[1:] == ["--write-integrity"]:
        write_integrity()
        raise SystemExit(0)
    if sys.argv[1:]:
        print("Usage: validate_contracts.py [--write-integrity]", file=sys.stderr)
        raise SystemExit(2)
    raise SystemExit(main())
