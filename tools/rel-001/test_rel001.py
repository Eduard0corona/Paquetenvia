"""Tests for the REL-001 pilot readiness evidence tool (tools/rel-001/rel001.py).

Every test is offline and synthetic: the end-to-end tests build a throwaway git repository that holds the normative
files, manifests and sources the tool reads, plus synthetic Foundation records and artifacts. Nothing here reads a real
run, a real drill, real people or Azure.
"""

from __future__ import annotations

import contextlib
import copy
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import unittest.mock
from pathlib import Path

import yaml

sys.path.insert(0, str(Path(__file__).resolve().parent))

import rel001  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = REPO_ROOT / ".github/workflows/pilot-readiness.yml"
DRILL = "EVIDENCE restore_point=2026-11-20T10:00:00Z started=2026-11-20T10:05:00Z restored=2026-11-20T10:40:00Z " \
        "verified=2026-11-20T10:50:00Z verify_status=Succeeded execution=job-pv-pilot-verify-abc123"
GENERATED_AT = "2026-11-21T12:00:00Z"


def counted_drill(sha: str, row_counts: str = "MATCHED", asserted: str | None = None) -> str:
    """The EVIDENCE line of a drill that compared the row counts (Z18) and names the commit its assert ran."""
    return DRILL.replace(" execution=", f" row_counts={row_counts} asserted_git_sha={asserted or sha} execution=")


def write_ops002_report(artifacts: Path, sha: str, counts: dict | None = None) -> None:
    directory = artifacts / rel001.OPS002_ARTIFACT
    directory.mkdir(parents=True, exist_ok=True)
    counts = counts or {"organizations": 2, "orders": 2, "order_events": 9, "proof_photos": 2}
    (directory / rel001.OPS002_REPORT_FILE).write_text(json.dumps({
        "result": "RESTORE_DRILL_PASSED", "tested_git_sha": sha, "row_counts_matched": True,
        "row_counts_source": counts, "row_counts_restored": counts}), encoding="utf-8")
# The files and directories the tool reads from a tested tree.
TREE_PATHS = (
    "docs/normative/v0.6/specs/AI-02_PRODUCT_CONTRACT.yaml",
    "docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml",
    "docs/normative/v0.6/specs/AI-08_BACKLOG.yaml",
    "docs/normative/v0.6/specs/AI-10_DECISIONS_AND_GATES.yaml",
    "docs/normative/v0.6/specs/AI-15_SCALABILITY_CONTRACT.yaml",
    "docs/normative/v0.6/specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml",
    "docs/normative/v0.6/tests/AI-09_ACCEPTANCE_TESTS.feature",
    "docs/normative/v0.6/decision-log.md",
    "tests/fixtures/rel-001",
    "deploy/azure/pilot",
    *[switch["source"] for switch in rel001.RECIPIENT_NOTICE_SWITCHES],
    *[f"{host}/{name}" for host in rel001.APPSETTINGS_HOSTS
      for name in ("appsettings.json", "appsettings.ScaleReady.json")],
)


def git(cwd: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(cwd), *args], check=True, capture_output=True, text=True).stdout.strip()


def build_tree(root: Path, mutate=None) -> str:
    """Copy what the tool reads into a new git repository, optionally mutate it, commit and return the SHA."""
    for relative in dict.fromkeys(TREE_PATHS):
        source, target = REPO_ROOT / relative, root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        if source.is_dir():
            shutil.copytree(source, target, dirs_exist_ok=True)
        elif source.is_file():
            shutil.copy2(source, target)
    if mutate:
        mutate(root)
    git(root, "init", "-q", "-b", "main")
    git(root, "-c", "user.email=rel001@example.invalid", "-c", "user.name=rel001", "add", "-A")
    git(root, "-c", "user.email=rel001@example.invalid", "-c", "user.name=rel001", "commit", "-q", "-m", "tree")
    return git(root, "rev-parse", "HEAD")


def foundation_records(sha: str, run_id: str = "7001", jobs: int = 13) -> tuple[dict, dict]:
    run = {"id": int(run_id), "name": "Foundation CI", "head_sha": sha, "event": "push", "head_branch": "main",
           "status": "completed", "conclusion": "success", "run_attempt": 1}
    names = [f"Job {index:02d}" for index in range(jobs)]
    return run, {"jobs": [{"name": name, "run_id": int(run_id), "head_sha": sha, "status": "completed",
                           "conclusion": "success"} for name in names]}


def manifest_identities(root: Path) -> dict[str, set[str]]:
    identities: dict[str, set[str]] = {}
    browser = json.loads((root / rel001.BROWSER_MANIFEST).read_text(encoding="utf-8"))
    ai09 = json.loads((root / rel001.AI09_MAP).read_text(encoding="utf-8"))
    for entry in [*browser["screens"]]:
        for test in entry["suites"]:
            identities.setdefault(test["job"], set()).add(test["test"])
    for entry in ai09["scenarios"]:
        for test in entry["tests"]:
            identities.setdefault(test["job"], set()).add(test["test"])
    return identities


def write_artifacts(directory: Path, root: Path, sha: str, run_id: str = "7001", outcome: str = "PASSED",
                    skip: set[str] | None = None) -> None:
    identities = manifest_identities(root)
    for job, artifact in rel001.REL000.EXECUTION_ARTIFACT_NAMES.items():
        if skip and artifact in skip:
            continue
        tests = [{"job": job, "test_project": "synthetic", "fully_qualified_test_name": name if "::" in name or
                  name.startswith("tools.") else f"{name}.Synthetic_case", "category": "Synthetic", "outcome": outcome,
                  "executed": True, "skipped": False} for name in sorted(identities.get(job, set()))]
        tests.append({"job": job, "test_project": "synthetic", "fully_qualified_test_name": f"Synthetic.{job}.Smoke",
                      "category": "Synthetic", "outcome": "PASSED", "executed": True, "skipped": False})
        target = directory / artifact
        target.mkdir(parents=True)
        (target / rel001.EXECUTION_RESULTS_FILE).write_text(json.dumps({
            "format_version": rel001.REL000.EXECUTION_EVIDENCE_VERSION, "artifact_name": artifact, "job": job,
            "workflow_run_id": run_id, "workflow_run_attempt": "1", "source_head_sha": sha, "tested_git_sha": sha,
            "base_main_sha": sha, "result_files": [], "content_digest": "0" * 64, "tests": tests}), encoding="utf-8")
    report = directory / rel001.REL000_REPORT_ARTIFACT
    report.mkdir(parents=True)
    (report / rel001.REL000_REPORT_FILE).write_text(json.dumps({
        "format_version": "paquetenvia-rel000-v1", "workflow_run_id": run_id, "tested_git_sha": sha,
        "technical_evidence_status": "PASSED", "dependency_security_status": "PASSED", "ops001_evidence_valid": True,
        "ops002_restore_drill_passed": True, "cross_tenant_incidents_observed": 0, "result": "REL000_OWNER_APPROVED"}),
        encoding="utf-8")


def rehearsal_document(sha: str, run_id: str = "9001", **overrides) -> dict:
    sample = {"samples": 120, "p50_ms": 300, "p95_ms": 900, "p99_ms": 1200}
    document = {
        "format_version": rel001.REHEARSAL_FORMAT, "tested_git_sha": sha, "workflow_run_id": run_id,
        "workflow_run_attempt": 1,
        "environment": {"kind": "LOCAL_CI_SYNTHETIC", "azure": False, "proof_storage": "S3_COMPATIBLE_DOUBLE",
                        "proof_scanner": "SYNTHETIC", "representative_of_pilot_database": False},
        "journey": {step: "PASSED" for step in ("cod_recorded_reconciled_closed", "failed_attempt_decided_redelivered",
                                                 "csv_import_created_orders",
                                                 "settlement_calculated_approved_paid_exported")},
        "completeness": {"delivered_orders": 60, "complete_orders": 60,
                         "missing": {"event_version_gaps": 0, "unaudited_status_changes": 0, "missing_pickup_photo": 0,
                                     "missing_delivery_photo": 0}},
        "latency": {"critical_screens": {screen: dict(sample) for screen in rel001.EXPECTED_READINESS["critical_screens"]},
                    "order_creation": {"samples": 110, "p50_ms": 800, "p95_ms": 1500, "p99_ms": 2100},
                    "technical": {"http_p95_ms": 120, "event_delivery_p95_seconds": 2.5,
                                  "signed_url_generation_p95_ms": 600}},
    }
    for key, value in overrides.items():
        document[key] = value
    return document


def decision_record(sha: str, run_id: str = "7001", **overrides) -> dict:
    record = {
        "format_version": rel001.DECISION_FORMAT, "decision_id": "REL-001-ENTRY-001", "release": "MVP-1_PILOT",
        "phase": "ENTRY", "decision": "APPROVE", "decision_statement": rel001.DECISION_STATEMENT,
        "decided_by": "project_owner", "decided_on": "2026-11-21",
        "approvals": {"operations": {"approved_by": "project_owner"}, "finance": {"approved_by": "project_owner"},
                      "legal": {"approved_by": "project_owner", "legal_review_by_role": "OWNER_LAWYER"}},
        "delivery_plan": {"planned_real_deliveries": 80, "period_start": "2026-11-23", "period_end": "2027-01-21",
                          "sender_count": 6, "zone_count": 3},
        "approved_evidence": {"tested_git_sha": sha, "foundation_run_id": run_id,
                              "restore_drill_verified_at": "2026-11-20T10:50:00Z"},
        "scope": dict(rel001.DECISION_SCOPE),
    }
    for key, value in overrides.items():
        record[key] = value
    return record


class World:
    """A synthetic tested tree, control tree, Foundation run and artifacts."""

    def __init__(self, case: unittest.TestCase, mutate=None, record: dict | None = None, rehearsal: bool = False,
                 drill: str | None = DRILL, artifacts_skip: set[str] | None = None, outcome: str = "PASSED",
                 counted: bool = False) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="rel001-test-")
        case.addCleanup(self.temp.cleanup)
        base = Path(self.temp.name)
        self.root = base / "tree"
        self.root.mkdir()
        self.sha = build_tree(self.root, mutate)
        run, jobs = foundation_records(self.sha)
        (base / "run.json").write_text(json.dumps(run), encoding="utf-8")
        (base / "jobs.json").write_text(json.dumps(jobs), encoding="utf-8")
        self.artifacts = base / "artifacts"
        write_artifacts(self.artifacts, self.root, self.sha, skip=artifacts_skip, outcome=outcome)
        self.output = base / "out"
        self.base = base
        args = ["report", "--tested-root", str(self.root), "--control-root", str(self.root), "--tested-git-sha", self.sha,
                "--foundation-run-id", "7001", "--foundation-run-json", str(base / "run.json"),
                "--foundation-jobs-json", str(base / "jobs.json"), "--dispatch-ref", "refs/heads/main",
                "--dispatch-sha", self.sha, "--artifacts-dir", str(self.artifacts), "--workflow-run-id", "9001",
                "--generated-at", GENERATED_AT, "--output", str(self.output), "--restore-evidence-env",
                "REL001_TEST_DRILL", "--decision-record"]
        if rehearsal:
            directory = base / "rehearsal"
            directory.mkdir()
            (directory / "rel001-rehearsal-evidence.json").write_text(json.dumps(rehearsal_document(self.sha)),
                                                                       encoding="utf-8")
            args += ["--rehearsal-dir", str(directory)]
        if record is not None:
            target = self.root / rel001.DECISION_RECORD
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(json.dumps(record), encoding="utf-8")
        self.args = args
        self.drill = drill
        if counted:
            # Z18: the drill compared the counts and ran this commit; the OPS-002 CI drill compared the same counts.
            self.drill = counted_drill(self.sha)
            write_ops002_report(self.artifacts, self.sha)

    def run(self) -> tuple[int, dict]:
        env = dict(os.environ)
        if self.drill is None:
            env.pop("REL001_TEST_DRILL", None)
        else:
            env["REL001_TEST_DRILL"] = self.drill
        stdout = io.StringIO()
        with unittest.mock.patch.dict(os.environ, env, clear=True), contextlib.redirect_stdout(stdout):
            code = rel001.main(self.args)
        return code, json.loads(stdout.getvalue())

    def output_json(self, name: str) -> dict:
        return json.loads((self.output / name).read_text(encoding="utf-8"))



class NormativeTests(unittest.TestCase):
    def test_repository_normative_tree_loads(self):
        normative = rel001.load_normative(REPO_ROOT)
        self.assertEqual(["GATE-003", "GATE-004", "GATE-007", "GATE-008", "GATE-009", "GATE-010"], normative["release_gates"])
        self.assertIn("NTF-005", normative["depends_on"])
        self.assertEqual({"warning": 500, "critical": 800}, normative["technical_thresholds"]["http_p95_ms"])

    def test_ai24_threshold_drift_fails(self):
        for path, old, new in (
            ("specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml", "critical_screen_p95_ms_below: 2000", "critical_screen_p95_ms_below: 2500"),
            ("specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml", "    max_days_before_start: 7", "    max_days_before_start: 14"),
            ("specs/AI-02_PRODUCT_CONTRACT.yaml", "orders_with_complete_events_and_pod: '>=0.98'", "orders_with_complete_events_and_pod: '>=0.95'"),
        ):
            with self.subTest(change=new), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                for relative in TREE_PATHS[:8]:
                    (root / relative).parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(REPO_ROOT / relative, root / relative)
                target = root / "docs/normative/v0.6" / path
                text = target.read_text(encoding="utf-8")
                self.assertEqual(1, text.count(old))
                target.write_text(text.replace(old, new), encoding="utf-8")
                with self.assertRaises(rel001.Rel001Error) as raised:
                    rel001.load_normative(root)
                self.assertEqual("NORMATIVE_DRIFT", raised.exception.code)

    def test_ai08_rel001_keeps_its_criteria_and_decided_row(self):
        backlog = yaml.safe_load((REPO_ROOT / rel001.AI08).read_text(encoding="utf-8"))
        item = next(i for i in backlog["items"] if i["id"] == "REL-001")
        decided = [c for c in item["acceptance_criteria"] if c.startswith(f"DECIDED {rel001.DECISION}")]
        self.assertEqual(1, len(decided))
        for answer in ("Z1", "Z4", "Z5", "Z6", "Z7", "Z8", "Z9", "Z10", "Z11", "Z12"):
            self.assertIn(f"({answer})", decided[0])

    def test_decision_log_has_the_row(self):
        rows = rel001.ENV001.decision_log_rows((REPO_ROOT / rel001.DECISION_LOG).read_text(encoding="utf-8"))
        self.assertEqual(1, sum(1 for row in rows if row[1] == rel001.DECISION))


class ManifestTests(unittest.TestCase):
    def test_repository_manifests_are_closed(self):
        screens = rel001.load_browser_manifest(REPO_ROOT)
        scenarios = rel001.load_ai09_map(REPO_ROOT)
        self.assertEqual(18, len(screens))
        self.assertEqual(81, len(scenarios))
        statuses = {s: sum(1 for e in scenarios if e["status"] == s) for s in ("COVERED", "GAP", "NOT_APPLICABLE")}
        self.assertEqual({"COVERED": 73, "GAP": 3, "NOT_APPLICABLE": 5}, statuses)
        gaps = sorted(e["screen"] for e in screens if e["status"] == "GAP")
        self.assertEqual(["/login", "/onboarding", "screen:external_driver_offers"], gaps)

    def test_critical_screens_are_ai07_routes(self):
        routes = yaml.safe_load((REPO_ROOT / rel001.AI07).read_text(encoding="utf-8"))["routes"]
        for screen in rel001.EXPECTED_READINESS["critical_screens"]:
            self.assertIn(screen, routes)

    def test_every_mapped_dotnet_identity_exists_in_the_sources(self):
        sources = {path: path.read_text(encoding="utf-8", errors="replace")
                   for path in (REPO_ROOT / "tests").rglob("*.cs") if "/bin/" not in str(path) and "/obj/" not in str(path)}
        for job, names in manifest_identities(REPO_ROOT).items():
            for name in sorted(names):
                with self.subTest(job=job, test=name):
                    if name.startswith("tools."):
                        script = name.split(".")[1]
                        self.assertTrue((REPO_ROOT / "tools" / f"{script}.ps1").is_file())
                        continue
                    if name.startswith("src/"):
                        self.assertTrue((REPO_ROOT / "apps/web" / name.split("::", 1)[0]).is_file())
                        continue
                    parts = name.split(".")
                    candidates = [(parts[-1], None), (parts[-2], parts[-1])]
                    found = False
                    for class_name, method in candidates:
                        for text in sources.values():
                            if re.search(rf"\bclass {re.escape(class_name)}\b", text) and (
                                    method is None or re.search(rf"\b{re.escape(method)}\s*\(", text)):
                                found = True
                                break
                        if found:
                            break
                    self.assertTrue(found, f"{name} is not declared under tests/")

    def _mutated(self, path: Path, mutate) -> Path:
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = Path(temp.name)
        for relative in (rel001.AI07, rel001.AI09, path):
            (root / relative).parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(REPO_ROOT / relative, root / relative)
        document = json.loads((root / path).read_text(encoding="utf-8"))
        mutate(document)
        (root / path).write_text(json.dumps(document), encoding="utf-8")
        return root

    def test_browser_manifest_refuses_missing_extra_and_malformed_entries(self):
        cases = {
            "missing": lambda d: d["screens"].pop(0),
            "extra": lambda d: d["screens"].append({"screen": "/business/orders", "status": "COVERED", "suites": [
                {"job": "operations-dashboard", "test": "X"}]}),
            "duplicate": lambda d: d["screens"].append(copy.deepcopy(d["screens"][0])),
            "gap_without_reason": lambda d: next(e for e in d["screens"] if e["status"] == "GAP").pop("reason"),
            "covered_without_tests": lambda d: next(e for e in d["screens"] if e["status"] == "COVERED").update(suites=[]),
            "unknown_job": lambda d: next(e for e in d["screens"] if e["status"] == "COVERED")["suites"][0].update(job="web"),
            "unknown_status": lambda d: d["screens"][0].update(status="PARTIAL"),
            "extra_key": lambda d: d["screens"][0].update(note="x"),
        }
        for name, mutate in cases.items():
            with self.subTest(case=name):
                root = self._mutated(rel001.BROWSER_MANIFEST, mutate)
                with self.assertRaises(rel001.Rel001Error):
                    rel001.load_browser_manifest(root)

    def test_ai09_map_refuses_missing_renamed_and_unreasoned_entries(self):
        cases = {
            "missing": lambda d: d["scenarios"].pop(),
            "renamed": lambda d: d["scenarios"][0].update(scenario="Otro escenario"),
            "na_with_tests": lambda d: next(e for e in d["scenarios"] if e["status"] == "NOT_APPLICABLE").update(
                tests=[{"job": "dotnet", "test": "X"}]),
            "na_without_reason": lambda d: next(e for e in d["scenarios"] if e["status"] == "NOT_APPLICABLE").update(reason=" "),
        }
        for name, mutate in cases.items():
            with self.subTest(case=name):
                root = self._mutated(rel001.AI09_MAP, mutate)
                with self.assertRaises(rel001.Rel001Error):
                    rel001.load_ai09_map(root)


def log_with(rows: list[tuple[str, str]]) -> str:
    lines = ["# Decision log", "", "| Date | ID | Type | Decision | Impacted files | Approved by |", "|---|---|---|---|---|---|"]
    lines += [f"| 2026-11-01 | {row_id} | {row_type} | text | files | owner |" for row_id, row_type in rows]
    return "\n".join(lines) + "\n"


class GateTests(unittest.TestCase):
    def test_only_resolution_and_scoping_rows_close_a_gate(self):
        text = log_with([("GATE-004-CHANNELS", "Provider decision"), ("GATE-007-PRIVACY-OK", "Gate resolution"),
                         ("GATE-012-PILOT-SCOPE", "Gate scoping"), ("GATE-0071-X", "Gate resolution")])
        states = rel001.gate_states(text, ["GATE-004", "GATE-007", "GATE-012", "GATE-008"])
        self.assertEqual("OPEN", states["GATE-004"]["status"])
        self.assertEqual(["GATE-004-CHANNELS"], states["GATE-004"]["related_rows"])
        self.assertEqual("RESOLVED", states["GATE-007"]["status"])
        self.assertEqual("SCOPED", states["GATE-012"]["status"])
        self.assertEqual("OPEN", states["GATE-008"]["status"])
        result = rel001.release_gates_criterion(states, {})
        self.assertEqual("FAILED", result["status"])
        self.assertEqual(["GATE-004", "GATE-008"], result["open_gates"])

    def test_repository_pilot_gates_are_open(self):
        normative = rel001.load_normative(REPO_ROOT)
        states = rel001.gate_states((REPO_ROOT / rel001.DECISION_LOG).read_text(encoding="utf-8"), normative["release_gates"])
        self.assertEqual({"OPEN"}, {state["status"] for state in states.values()})


class RecipientNoticeTests(unittest.TestCase):
    def tree(self, mutate=None) -> Path:
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = Path(temp.name)
        for relative in TREE_PATHS[9:]:
            source, target = REPO_ROOT / relative, root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            if source.is_dir():
                shutil.copytree(source, target, dirs_exist_ok=True)
            elif source.is_file():
                shutil.copy2(source, target)
        if mutate:
            mutate(root)
        return root

    def test_repository_switches_are_all_off(self):
        switches = rel001.recipient_notice_switches(REPO_ROOT)
        self.assertEqual(6, len(switches))
        self.assertTrue(all(s["off"] for s in switches), switches)
        states = {gate: {"status": "OPEN"} for gate in rel001.NOTICE_GATES}
        self.assertEqual("PASSED", rel001.recipient_notice_criterion(states, switches)["status"])

    def test_a_switch_on_fails_while_a_gate_is_open_and_passes_once_both_resolve(self):
        def worker_on(root: Path) -> None:
            path = root / "src/Paqueteria.Worker/appsettings.json"
            document = json.loads(path.read_text(encoding="utf-8"))
            document.setdefault("Notifications", {})["recipientnotices"] = {"Enabled": True}
            path.write_text(json.dumps(document), encoding="utf-8")
        switches = rel001.recipient_notice_switches(self.tree(worker_on))
        on = [s for s in switches if not s["off"]]
        self.assertEqual(["Notifications:RecipientNotices:Enabled"], [s["switch"] for s in on])
        open_states = {"GATE-004": {"status": "RESOLVED"}, "GATE-007": {"status": "OPEN"}}
        self.assertEqual("FAILED", rel001.recipient_notice_criterion(open_states, switches)["status"])
        resolved = {"GATE-004": {"status": "RESOLVED"}, "GATE-007": {"status": "RESOLVED"}}
        self.assertEqual("PASSED", rel001.recipient_notice_criterion(resolved, switches)["status"])

    def test_a_provider_other_than_disabled_is_on(self):
        def api_synthetic(root: Path) -> None:
            path = root / "src/Paqueteria.Api/appsettings.json"
            document = json.loads(path.read_text(encoding="utf-8"))
            document["Messaging"]["Email"]["Provider"] = "Synthetic"
            path.write_text(json.dumps(document), encoding="utf-8")
        on = [s["switch"] for s in rel001.recipient_notice_switches(self.tree(api_synthetic)) if not s["off"]]
        self.assertEqual(["Messaging:Email:Provider"], on)

    def test_pilot_template_env_counts_only_outside_comments(self):
        def comment_only(root: Path) -> None:
            path = root / "deploy/azure/pilot/apps.bicep"
            path.write_text(path.read_text(encoding="utf-8") + "\n// Messaging__WhatsApp__Provider stays Disabled\n"
                            "/* Notifications__RecipientNotices__Enabled */\nvar note = 'https://example.invalid/x'\n",
                            encoding="utf-8")
        self.assertTrue(all(s["off"] for s in rel001.recipient_notice_switches(self.tree(comment_only))))

        def env_set(root: Path) -> None:
            path = root / "deploy/azure/pilot/apps.bicep"
            path.write_text(path.read_text(encoding="utf-8") + "\nvar extra = [{ name: 'Messaging__WhatsApp__Provider', "
                            "value: 'MetaCloudApi' }] // set\n", encoding="utf-8")
        findings = {s["switch"]: s["findings"] for s in rel001.recipient_notice_switches(self.tree(env_set))}
        self.assertEqual(["SET_IN:deploy/azure/pilot/apps.bicep"], findings["Messaging:WhatsApp:Provider"])

        def settings_json(root: Path) -> None:
            path = root / "deploy/azure/pilot/apps.settings.json"
            entries = json.loads(path.read_text(encoding="utf-8"))
            entries.append({"name": "Locations__NoticeKeyProtector", "value": "AzureKeyVault"})
            path.write_text(json.dumps(entries), encoding="utf-8")
        findings = {s["switch"]: s["findings"] for s in rel001.recipient_notice_switches(self.tree(settings_json))}
        self.assertEqual(["SET_IN:deploy/azure/pilot/apps.settings.json"], findings["Locations:NoticeKeyProtector"])

    def test_an_option_default_that_is_not_off_fails(self):
        def default_on(root: Path) -> None:
            path = root / "src/Modules/Notifications/Notifications.Infrastructure/RecipientNotices/RecipientNoticesOptions.cs"
            text = path.read_text(encoding="utf-8")
            path.write_text(text.replace("public bool Enabled { get; set; }\n", "public bool Enabled { get; set; } = true;\n", 1),
                            encoding="utf-8")
        findings = {s["switch"]: s["findings"] for s in rel001.recipient_notice_switches(self.tree(default_on))}
        self.assertTrue(findings["Notifications:RecipientNotices:Enabled"][0].startswith("DEFAULT_NOT_OFF:"))

    def test_a_symlinked_pilot_template_fails_closed(self):
        def symlinked(root: Path) -> None:
            path = root / "deploy/azure/pilot/apps.bicep"
            outside = root / "outside-apps.bicep"
            outside.write_text(path.read_text(encoding="utf-8"), encoding="utf-8")
            path.unlink()
            path.symlink_to(outside)
        tree = self.tree(symlinked)
        with self.assertRaises(rel001.Rel001Error) as raised:
            rel001.recipient_notice_switches(tree)
        self.assertEqual("INPUT_FILE_MISSING", raised.exception.code)

    def test_a_symlinked_option_default_source_fails_closed(self):
        def symlinked(root: Path) -> None:
            path = root / "src/Modules/Notifications/Notifications.Infrastructure/RecipientNotices/RecipientNoticesOptions.cs"
            outside = root / "outside-options.cs"
            outside.write_text(path.read_text(encoding="utf-8"), encoding="utf-8")
            path.unlink()
            path.symlink_to(outside)
        tree = self.tree(symlinked)
        with self.assertRaises(rel001.Rel001Error) as raised:
            rel001.recipient_notice_switches(tree)
        self.assertEqual("INPUT_FILE_MISSING", raised.exception.code)

    def test_a_missing_option_default_source_fails_closed(self):
        def missing(root: Path) -> None:
            (root / "src/Modules/Notifications/Notifications.Infrastructure/RecipientNotices/RecipientNoticesOptions.cs").unlink()
        with self.assertRaises(rel001.Rel001Error) as raised:
            rel001.recipient_notice_switches(self.tree(missing))
        self.assertEqual("INPUT_FILE_MISSING", raised.exception.code)

    def test_development_and_testing_settings_are_ignored(self):
        def dev(root: Path) -> None:
            (root / "src/Paqueteria.Api/appsettings.Development.json").write_text(
                json.dumps({"Messaging": {"WhatsApp": {"Provider": "Synthetic"}}}), encoding="utf-8")
        self.assertTrue(all(s["off"] for s in rel001.recipient_notice_switches(self.tree(dev))))

    def test_strip_bicep_comments_keeps_strings(self):
        text = "var a = 'x // not a comment' // gone\n/* gone */var b = 'it''s'\n"
        self.assertEqual("var a = 'x // not a comment' \nvar b = 'it''s'\n", rel001.strip_bicep_comments(text))


class RehearsalTests(unittest.TestCase):
    THRESHOLDS = {"http_p95_ms": {"warning": 500, "critical": 800},
                  "event_delivery_p95_seconds": {"warning": 2, "critical": 5},
                  "signed_url_generation_p95_ms": {"warning": 250, "critical": 500}}

    def evaluate(self, document: dict, sha: str = "a" * 40, run_id: str = "9001"):
        with tempfile.TemporaryDirectory() as temp:
            (Path(temp) / "rel001-rehearsal-evidence.json").write_text(json.dumps(document), encoding="utf-8")
            return rel001.rehearsal_criteria(Path(temp), sha, run_id, self.THRESHOLDS)

    def test_absent_rehearsal_is_missing_never_passed(self):
        data, latency = rel001.rehearsal_criteria(None, "a" * 40, "9001", self.THRESHOLDS)
        self.assertEqual(("MISSING", "MISSING"), (data["status"], latency["status"]))
        self.assertIn("burstable", latency["environment_note"])

    def test_valid_rehearsal_passes_and_reports_technical_levels(self):
        data, latency = self.evaluate(rehearsal_document("a" * 40))
        self.assertEqual(("PASSED", "PASSED"), (data["status"], latency["status"]))
        self.assertEqual(1.0, data["ratio"])
        levels = {row["metric"]: row["level"] for row in latency["reported_only"]}
        self.assertEqual({"http_p95_ms": "OK", "event_delivery_p95_seconds": "WARNING",
                          "signed_url_generation_p95_ms": "CRITICAL"}, levels)

    def test_completeness_below_98_percent_or_too_few_orders_fails(self):
        low = rehearsal_document("a" * 40)
        low["completeness"] = {"delivered_orders": 100, "complete_orders": 97,
                               "missing": {"event_version_gaps": 1, "unaudited_status_changes": 0,
                                           "missing_pickup_photo": 2, "missing_delivery_photo": 0}}
        self.assertIn("COMPLETENESS_BELOW_0_98", self.evaluate(low)[0]["reasons"])
        boundary = copy.deepcopy(low)
        boundary["completeness"]["complete_orders"] = 98
        boundary["completeness"]["missing"]["missing_pickup_photo"] = 1
        self.assertEqual("PASSED", self.evaluate(boundary)[0]["status"])
        few = rehearsal_document("a" * 40)
        few["completeness"]["delivered_orders"] = few["completeness"]["complete_orders"] = 49
        self.assertEqual(["TOO_FEW_DELIVERED_ORDERS"], self.evaluate(few)[0]["reasons"])

    def test_inconsistent_counts_are_refused(self):
        bad = rehearsal_document("a" * 40)
        bad["completeness"] = {"delivered_orders": 60, "complete_orders": 58,
                               "missing": {"event_version_gaps": 1, "unaudited_status_changes": 0,
                                           "missing_pickup_photo": 0, "missing_delivery_photo": 0}}
        with self.assertRaises(rel001.Rel001Error):
            self.evaluate(bad)

    def test_a_failed_journey_step_fails_completeness(self):
        document = rehearsal_document("a" * 40)
        document["journey"]["csv_import_created_orders"] = "FAILED"
        self.assertEqual(["JOURNEY_STEP_NOT_PASSED:csv_import_created_orders"], self.evaluate(document)[0]["reasons"])

    def test_latency_limits_are_strict_and_need_100_samples(self):
        document = rehearsal_document("a" * 40)
        document["latency"]["critical_screens"]["/ops/inbox"]["p95_ms"] = 2000
        document["latency"]["critical_screens"]["/ops/inbox"]["p99_ms"] = 2100
        document["latency"]["order_creation"]["samples"] = 99
        reasons = self.evaluate(document)[1]["reasons"]
        self.assertEqual(["P95_AT_OR_ABOVE_LIMIT:/ops/inbox", "TOO_FEW_SAMPLES:order_creation"], reasons)

    def test_shape_environment_and_provenance_are_closed(self):
        cases = {
            "other_commit": (rehearsal_document("b" * 40), "a" * 40, "9001"),
            "other_run": (rehearsal_document("a" * 40, run_id="1"), "a" * 40, "9001"),
            "azure": (rehearsal_document("a" * 40, environment={"kind": "AZURE_DEV_SYNTHETIC", "azure": True,
                                                                "proof_storage": "S3_COMPATIBLE_DOUBLE",
                                                                "proof_scanner": "SYNTHETIC",
                                                                "representative_of_pilot_database": False}), "a" * 40, "9001"),
            "extra_key": ({**rehearsal_document("a" * 40), "notes": "x"}, "a" * 40, "9001"),
        }
        screens = rehearsal_document("a" * 40)
        screens["latency"]["critical_screens"].pop("/track/:token")
        cases["missing_screen"] = (screens, "a" * 40, "9001")
        percentile = rehearsal_document("a" * 40)
        percentile["latency"]["order_creation"]["p50_ms"] = 5000
        cases["percentiles_out_of_order"] = (percentile, "a" * 40, "9001")
        technical = rehearsal_document("a" * 40)
        technical["latency"]["technical"]["core_query_p95_ms"] = 10
        cases["unknown_technical_metric"] = (technical, "a" * 40, "9001")
        for name, (document, sha, run_id) in cases.items():
            with self.subTest(case=name), self.assertRaises(rel001.Rel001Error):
                self.evaluate(document, sha, run_id)

    def test_missing_technical_metric_is_reported_not_blocking(self):
        document = rehearsal_document("a" * 40)
        document["latency"]["technical"] = {}
        latency = self.evaluate(document)[1]
        self.assertEqual("PASSED", latency["status"])
        self.assertEqual({"NOT_MEASURED"}, {row["level"] for row in latency["reported_only"]})


class RestoreTests(unittest.TestCase):
    GENERATED = rel001.parse_instant(GENERATED_AT, "generated_at")
    CI_OK = {"ops002_restore_drill_passed": True}
    MANIFESTS = {"backup-restore": [{}]}

    CI_COUNTS = {"organizations": 2, "orders": 2, "order_events": 9, "proof_photos": 2}
    SHA = "c" * 40

    def evaluate(self, line, start=None, rel000=None, ci_counts=None):
        return rel001.restore_criterion(line, start, self.GENERATED, rel000 or self.CI_OK, self.MANIFESTS,
                                        ci_counts or ("PASSED", dict(self.CI_COUNTS), []), self.SHA)

    def ops002_report(self, mutate=None, sha="a" * 40) -> Path:
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        directory = Path(temp.name) / rel001.OPS002_ARTIFACT
        directory.mkdir()
        report = {"result": "RESTORE_DRILL_PASSED", "tested_git_sha": sha, "row_counts_matched": True,
                  "row_counts_source": dict(self.CI_COUNTS), "row_counts_restored": dict(self.CI_COUNTS)}
        if mutate:
            mutate(report)
        (directory / rel001.OPS002_REPORT_FILE).write_text(json.dumps(report), encoding="utf-8")
        return Path(temp.name)

    def test_the_ci_drill_counts_match(self):
        status, counts, reasons = rel001.ci_drill_row_counts(self.ops002_report(), "a" * 40)
        self.assertEqual(("PASSED", self.CI_COUNTS, []), (status, counts, reasons))
        result = self.evaluate(None)
        self.assertEqual("MATCHED", result["ci_drill"]["row_count_comparison"])
        self.assertEqual(self.CI_COUNTS, result["ci_drill"]["row_counts"])
        self.assertNotIn("CI_DRILL_ROW_COUNTS_MISSING", result["reasons"])

    def test_a_ci_drill_without_counts_is_missing(self):
        empty = tempfile.TemporaryDirectory()
        self.addCleanup(empty.cleanup)
        self.assertEqual(("MISSING", None, ["CI_DRILL_ROW_COUNTS_MISSING"]),
                         rel001.ci_drill_row_counts(Path(empty.name), "a" * 40))

        def before_counts(report):
            for key in ("row_counts_matched", "row_counts_source", "row_counts_restored"):
                del report[key]
        self.assertEqual(("MISSING", None, ["CI_DRILL_ROW_COUNTS_MISSING"]),
                         rel001.ci_drill_row_counts(self.ops002_report(before_counts), "a" * 40))

    def test_a_ci_drill_whose_counts_differ_or_drift_fails(self):
        cases = {
            "differ": lambda r: r["row_counts_restored"].update(order_events=8),
            "not_matched": lambda r: r.update(row_counts_matched=False),
            "extra_key": lambda r: r["row_counts_source"].update(users=1),
            "negative": lambda r: (r["row_counts_source"].update(orders=-1), r["row_counts_restored"].update(orders=-1)),
            "not_int": lambda r: (r["row_counts_source"].update(orders="2"), r["row_counts_restored"].update(orders="2")),
            "missing_side": lambda r: r.pop("row_counts_restored"),
        }
        for name, mutate in cases.items():
            with self.subTest(case=name):
                status, counts, reasons = rel001.ci_drill_row_counts(self.ops002_report(mutate), "a" * 40)
                self.assertEqual(("FAILED", None, ["CI_DRILL_ROW_COUNTS_MISMATCH"]), (status, counts, reasons))
        for name, (mutate, sha) in {"other_commit": (None, "b" * 40),
                                    "not_passed": (lambda r: r.update(result="RESTORE_DRILL_FAILED"), "a" * 40)}.items():
            with self.subTest(case=name):
                self.assertEqual(("FAILED", None, ["CI_DRILL_REPORT_INVALID"]),
                                 rel001.ci_drill_row_counts(self.ops002_report(mutate), sha))
        result = self.evaluate(None, ci_counts=("FAILED", None, ["CI_DRILL_ROW_COUNTS_MISMATCH"]))
        self.assertEqual("FAILED", result["status"])

    def test_a_symlinked_ci_drill_report_is_missing(self):
        root = self.ops002_report()
        report = root / rel001.OPS002_ARTIFACT / rel001.OPS002_REPORT_FILE
        outside = root / "outside.json"
        outside.write_text(report.read_text(encoding="utf-8"), encoding="utf-8")
        report.unlink()
        report.symlink_to(outside)
        self.assertEqual("MISSING", rel001.ci_drill_row_counts(root, "a" * 40)[0])

    def test_absent_drill_is_missing(self):
        result = self.evaluate(None, ci_counts=("MISSING", None, ["CI_DRILL_ROW_COUNTS_MISSING"]))
        self.assertEqual("MISSING", result["status"])
        self.assertIn("PILOT_DRILL_EVIDENCE_MISSING", result["reasons"])
        self.assertIn("CI_DRILL_ROW_COUNTS_MISSING", result["reasons"])

    def test_a_counted_drill_of_the_certified_commit_passes(self):
        result = self.evaluate(counted_drill(self.SHA), rel001.parse_date("2026-11-27", "s"))
        self.assertEqual("PASSED", result["status"], result["reasons"])
        self.assertEqual([], result["reasons"])
        self.assertEqual("MATCHED", result["pilot_drill"]["row_count_comparison"])
        self.assertEqual(self.SHA, result["pilot_drill"]["asserted_git_sha"])
        self.assertNotIn("abc123", json.dumps(result))

    def test_a_drill_that_did_not_match_or_ran_another_commit_fails(self):
        cases = {
            "unverified_counts": (counted_drill(self.SHA, row_counts="UNVERIFIED"), "PILOT_DRILL_ROW_COUNTS_NOT_MATCHED"),
            "real_restore": (counted_drill(self.SHA, row_counts="NOT_COMPARED"), "PILOT_DRILL_ROW_COUNTS_NOT_MATCHED"),
            "unverified_commit": (counted_drill(self.SHA, asserted="UNVERIFIED"), "PILOT_DRILL_COMMIT_MISMATCH"),
            "other_commit": (counted_drill(self.SHA, asserted="d" * 40), "PILOT_DRILL_COMMIT_MISMATCH"),
        }
        for name, (line, reason) in cases.items():
            with self.subTest(case=name):
                result = self.evaluate(line)
                self.assertEqual("FAILED", result["status"])
                self.assertIn(reason, result["reasons"])

    def test_a_counted_drill_still_needs_the_ci_drill_counts(self):
        missing = self.evaluate(counted_drill(self.SHA), ci_counts=("MISSING", None, ["CI_DRILL_ROW_COUNTS_MISSING"]))
        self.assertEqual("MISSING", missing["status"])
        self.assertEqual(["CI_DRILL_ROW_COUNTS_MISSING"], missing["reasons"])
        failed = self.evaluate(counted_drill(self.SHA), ci_counts=("FAILED", None, ["CI_DRILL_ROW_COUNTS_MISMATCH"]))
        self.assertEqual("FAILED", failed["status"])

    def test_a_drill_line_with_only_one_new_token_is_refused(self):
        for line in (DRILL.replace(" execution=", " row_counts=MATCHED execution="),
                     DRILL.replace(" execution=", f" asserted_git_sha={self.SHA} execution="),
                     counted_drill(self.SHA, asserted="C" * 40)):
            with self.subTest(line=line), self.assertRaises(rel001.Rel001Error):
                self.evaluate(line)

    def test_a_drill_line_from_before_the_comparison_misses_row_counts_and_commit_and_never_passes(self):
        result = self.evaluate(DRILL)
        self.assertEqual("MISSING", result["status"])
        self.assertIn("PILOT_DRILL_ROW_COUNTS_MISSING", result["reasons"])
        self.assertIn("PILOT_DRILL_COMMIT_MISSING", result["reasons"])
        self.assertNotIn("abc123", json.dumps(result))
        self.assertEqual("REPORT_TIME", result["pilot_drill"]["freshness_basis"])

    def test_failed_stale_and_out_of_window_drills_fail(self):
        cases = {
            "not_succeeded": (DRILL.replace("verify_status=Succeeded", "verify_status=Failed"), None, "PILOT_DRILL_NOT_SUCCEEDED"),
            "stale_for_start": (DRILL, rel001.parse_date("2026-11-28", "s"), "PILOT_DRILL_NOT_CURRENT"),
            "after_start": (DRILL, rel001.parse_date("2026-11-19", "s"), "PILOT_DRILL_NOT_CURRENT"),
            "pitr": (DRILL.replace("restore_point=2026-11-20T10:00:00Z", "restore_point=2026-11-05T10:00:00Z"), None,
                     "RESTORE_POINT_OUTSIDE_PITR_WINDOW"),
            "order": (DRILL.replace("restored=2026-11-20T10:40:00Z", "restored=2026-11-20T10:00:00Z"), None,
                      "PILOT_DRILL_TIMES_OUT_OF_ORDER"),
        }
        for name, (line, start, reason) in cases.items():
            with self.subTest(case=name):
                result = self.evaluate(line, start)
                self.assertEqual("FAILED", result["status"])
                self.assertIn(reason, result["reasons"])

    def test_a_week_before_start_is_current(self):
        result = self.evaluate(DRILL, rel001.parse_date("2026-11-27", "s"))
        self.assertNotIn("PILOT_DRILL_NOT_CURRENT", result["reasons"])
        self.assertEqual("PLANNED_START", result["pilot_drill"]["freshness_basis"])

    def test_a_failed_ci_drill_fails(self):
        self.assertEqual("FAILED", self.evaluate(None, rel000={"ops002_restore_drill_passed": False})["status"])

    def test_malformed_lines_are_refused(self):
        for line in ("EVIDENCE restore_point=2026-11-20T10:00:00Z", DRILL + " extra=1", DRILL.replace("Z started", " started"),
                     DRILL.replace("execution=job-pv-pilot-verify-abc123", "execution=a b"), "restore_point=x"):
            with self.subTest(line=line), self.assertRaises(rel001.Rel001Error):
                self.evaluate(line)


class DecisionRecordTests(unittest.TestCase):
    PROVENANCE = {"tested_git_sha": "a" * 40, "foundation_run_id": "7001"}
    RESTORE = {"pilot_drill": {"verified": "2026-11-20T10:50:00Z"}}

    def load(self, record) -> tuple:
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "record.json"
            path.write_text(json.dumps(record), encoding="utf-8")
            return rel001.load_decision_record(path)

    def test_absent_record_is_pending(self):
        record, problems = rel001.load_decision_record(Path("/nonexistent/record.json"))
        plan, start = rel001.delivery_plan_criterion(record, problems)
        approval = rel001.approval_status(record, problems, self.PROVENANCE, self.RESTORE)
        self.assertEqual(("PENDING_OWNER_INPUT", "PENDING_OWNER_INPUT", None), (plan["status"], approval["status"], start))

    def test_valid_record_passes_with_matching_anchors(self):
        record, problems = self.load(decision_record("a" * 40))
        plan, start = rel001.delivery_plan_criterion(record, problems)
        approval = rel001.approval_status(record, problems, self.PROVENANCE, self.RESTORE)
        self.assertEqual(("PASSED", "PASSED"), (plan["status"], approval["status"]))
        self.assertEqual("2026-11-23", start.isoformat())
        self.assertEqual(60, plan["plan"]["period_days"])
        self.assertEqual("OWNER_LAWYER", approval["legal_review_by_role"])

    def test_plan_outside_50_100_or_inconsistent_fails(self):
        for total, senders, reason in ((49, 3, "PLAN_TOTAL_OUTSIDE_50_100"), (101, 3, "PLAN_TOTAL_OUTSIDE_50_100"),
                                       (60, 61, "PLAN_COUNTS_INCONSISTENT"), (60, 0, "PLAN_COUNTS_INVALID")):
            with self.subTest(total=total, senders=senders):
                plan = {"planned_real_deliveries": total, "period_start": "2026-11-23", "period_end": "2027-01-21",
                        "sender_count": senders, "zone_count": 3}
                record, problems = self.load(decision_record("a" * 40, delivery_plan=plan))
                result, _ = rel001.delivery_plan_criterion(record, problems)
                self.assertEqual("FAILED", result["status"])
                self.assertIn(reason, result["reasons"])
        for boundary in (50, 100):
            plan = {"planned_real_deliveries": boundary, "period_start": "2026-11-23", "period_end": "2027-01-21",
                    "sender_count": 1, "zone_count": 1}
            record, problems = self.load(decision_record("a" * 40, delivery_plan=plan))
            self.assertEqual("PASSED", rel001.delivery_plan_criterion(record, problems)[0]["status"])

    def test_closed_shape_rejects_personal_data_and_wrong_values(self):
        plan_with_names = {"planned_real_deliveries": 60, "period_start": "2026-11-23", "period_end": "2027-01-21",
                           "sender_count": 3, "zone_count": 2, "senders": ["Tienda Ejemplo"]}
        cases = {
            "extra_plan_field": decision_record("a" * 40, delivery_plan=plan_with_names),
            "extra_top_field": {**decision_record("a" * 40), "notes": "x"},
            "statement": decision_record("a" * 40, decision_statement="Apruebo"),
            "decision": decision_record("a" * 40, decision="REJECT"),
            "legal_role": decision_record("a" * 40, approvals={
                "operations": {"approved_by": "project_owner"}, "finance": {"approved_by": "project_owner"},
                "legal": {"approved_by": "project_owner", "legal_review_by_role": "Lic. Persona"}}),
            "three_signers": decision_record("a" * 40, approvals={
                "operations": {"approved_by": "project_owner"}, "finance": {"approved_by": "accountant"},
                "legal": {"approved_by": "project_owner", "legal_review_by_role": "OWNER_LAWYER"}}),
            "scope": decision_record("a" * 40, scope={**rel001.DECISION_SCOPE, "production_authorized": True}),
            "decision_id": decision_record("a" * 40, decision_id="REL-001-OWNER-1"),
            "text_count": decision_record("a" * 40, delivery_plan={"planned_real_deliveries": "Tienda Ejemplo",
                                                                   "period_start": "2026-11-23", "period_end": "2027-01-21",
                                                                   "sender_count": 3, "zone_count": 2}),
            "run_as_number": decision_record("a" * 40, approved_evidence={
                "tested_git_sha": "a" * 40, "foundation_run_id": 7001, "restore_drill_verified_at": "2026-11-20T10:50:00Z"}),
            "bad_date": decision_record("a" * 40, decided_on="21/11/2026"),
        }
        for name, record in cases.items():
            with self.subTest(case=name):
                loaded, problems = self.load(record)
                self.assertIsNone(loaded)
                self.assertTrue(problems)
                approval = rel001.approval_status(loaded, problems, self.PROVENANCE, self.RESTORE)
                self.assertEqual("FAILED", approval["status"])
                self.assertNotIn("Tienda", json.dumps(approval))
                self.assertNotIn("Persona", json.dumps(approval))

    def test_anchors_must_match_this_run(self):
        record, problems = self.load(decision_record("b" * 40, run_id="1"))
        approval = rel001.approval_status(record, problems, self.PROVENANCE, {"pilot_drill": {"verified": "x"}})
        self.assertEqual(["APPROVED_COMMIT_DIFFERS", "APPROVED_FOUNDATION_RUN_DIFFERS", "APPROVED_RESTORE_DRILL_DIFFERS"],
                         approval["reasons"])


class EndToEndTests(unittest.TestCase):
    def test_today_the_entry_report_is_built_and_not_ready(self):
        world = World(self, drill=None)
        code, result = world.run()
        self.assertEqual(1, code)
        self.assertEqual("NOT_READY", result["entry_status"])
        self.assertEqual(sorted(rel001.OUTPUT_FILES), sorted(p.name for p in world.output.iterdir()))
        report = world.output_json("rel001-pilot-readiness-report.json")
        criteria = {name: value["status"] for name, value in report["criteria"].items()}
        self.assertEqual({"full_regression": "FAILED", "data_completeness": "MISSING", "latency": "MISSING",
                          "restore_vigente": "MISSING", "recipient_notices_line": "PASSED", "release_gates": "FAILED",
                          "delivery_plan": "PENDING_OWNER_INPUT", "owner_approval": "PENDING_OWNER_INPUT"}, criteria)
        self.assertEqual(["GATE-003", "GATE-004", "GATE-007", "GATE-008", "GATE-009", "GATE-010"], report["open_gates"])
        self.assertFalse(report["scope"]["pilot_start_authorized"])
        self.assertFalse(report["scope"]["real_pilot_data_read"])
        self.assertEqual("NOT_STARTED", report["closing_phase"]["status"])
        regression = world.output_json("rel001-regression-evidence.json")
        self.assertEqual({"COVERED": 15, "GAP": 3}, regression["browser_suites"]["manifest_counts"])
        self.assertEqual({"COVERED": 73, "GAP": 3, "NOT_APPLICABLE": 5}, regression["ai09_scenarios"]["manifest_counts"])
        self.assertEqual(73, regression["ai09_scenarios"]["status_counts"]["PASSED"])
        markdown = (world.output / "rel001-pilot-readiness-report.md").read_text(encoding="utf-8")
        self.assertIn("No listo para arrancar", markdown)
        self.assertIn("burstable", markdown)

    def test_even_a_complete_world_is_not_approved_until_the_drill_counts_rows(self):
        def close_everything(root: Path) -> None:
            log = root / rel001.DECISION_LOG
            rows = [f"| 2026-11-21 | {gate}-SYNTHETIC-CLOSED | Gate resolution | synthetic | none | owner |"
                    for gate in ("GATE-003", "GATE-004", "GATE-007", "GATE-008", "GATE-009", "GATE-010")]
            log.write_text(log.read_text(encoding="utf-8") + "\n".join(rows) + "\n", encoding="utf-8")
            for path, key in ((rel001.BROWSER_MANIFEST, "screens"), (rel001.AI09_MAP, "scenarios")):
                document = json.loads((root / path).read_text(encoding="utf-8"))
                for entry in document[key]:
                    if entry["status"] == "GAP":
                        entry.pop("reason")
                        entry.pop("owner_task", None)
                        entry["status"] = "COVERED"
                        tests_key = "suites" if key == "screens" else "tests"
                        entry[tests_key] = [{"job": "dotnet", "test": "Paqueteria.Synthetic.GapClosedTests"}]
                (root / path).write_text(json.dumps(document), encoding="utf-8")
        world = World(self, mutate=close_everything, rehearsal=True)
        # A record names the commit only after the tree exists: write it into the control tree afterwards.
        target = world.root / rel001.DECISION_RECORD
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(decision_record(world.sha)), encoding="utf-8")
        code, result = world.run()
        self.assertEqual(1, code)
        report = world.output_json("rel001-pilot-readiness-report.json")
        criteria = {name: value["status"] for name, value in report["criteria"].items()}
        self.assertEqual("PASSED", criteria["full_regression"], report["criteria"]["full_regression"])
        self.assertEqual("PASSED", criteria["data_completeness"])
        self.assertEqual("PASSED", criteria["latency"])
        self.assertEqual("PASSED", criteria["release_gates"])
        self.assertEqual("PASSED", criteria["delivery_plan"])
        self.assertEqual("PASSED", criteria["owner_approval"])
        self.assertEqual("MISSING", criteria["restore_vigente"])
        self.assertEqual(["restore_vigente"], report["blocking_criteria"])
        self.assertEqual("NOT_READY", report["entry_status"])
        for name in rel001.OUTPUT_FILES:
            text = (world.output / name).read_text(encoding="utf-8")
            self.assertNotIn("abc123", text)
            self.assertNotIn("job-pv-pilot-verify", text)

    def test_a_complete_world_with_a_counted_drill_is_approved(self):
        def close_everything(root: Path) -> None:
            log = root / rel001.DECISION_LOG
            rows = [f"| 2026-11-21 | {gate}-SYNTHETIC-CLOSED | Gate resolution | synthetic | none | owner |"
                    for gate in ("GATE-003", "GATE-004", "GATE-007", "GATE-008", "GATE-009", "GATE-010")]
            log.write_text(log.read_text(encoding="utf-8") + "\n".join(rows) + "\n", encoding="utf-8")
            for path, key in ((rel001.BROWSER_MANIFEST, "screens"), (rel001.AI09_MAP, "scenarios")):
                document = json.loads((root / path).read_text(encoding="utf-8"))
                for entry in document[key]:
                    if entry["status"] == "GAP":
                        entry.pop("reason")
                        entry.pop("owner_task", None)
                        entry["status"] = "COVERED"
                        tests_key = "suites" if key == "screens" else "tests"
                        entry[tests_key] = [{"job": "dotnet", "test": "Paqueteria.Synthetic.GapClosedTests"}]
                (root / path).write_text(json.dumps(document), encoding="utf-8")
        world = World(self, mutate=close_everything, rehearsal=True, counted=True)
        target = world.root / rel001.DECISION_RECORD
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(decision_record(world.sha)), encoding="utf-8")
        code, result = world.run()
        report = world.output_json("rel001-pilot-readiness-report.json")
        self.assertEqual("PASSED", report["criteria"]["restore_vigente"]["status"], report["criteria"]["restore_vigente"])
        self.assertEqual([], report["blocking_criteria"])
        self.assertEqual("APPROVED_FOR_PILOT_START", report["entry_status"])
        self.assertEqual(0, code)
        restore = world.output_json("rel001-restore-evidence.json")
        self.assertEqual({"organizations": 2, "orders": 2, "order_events": 9, "proof_photos": 2},
                         restore["ci_drill"]["row_counts"])
        for name in rel001.OUTPUT_FILES:
            text = (world.output / name).read_text(encoding="utf-8")
            self.assertNotIn("abc123", text)
            self.assertNotIn("job-pv-pilot-verify", text)

    def test_missing_and_failed_execution_evidence_never_pass(self):
        world = World(self, artifacts_skip={"rel000-execution-dotnet"})
        world.run()
        regression = world.output_json("rel001-regression-evidence.json")
        self.assertIn("ARTIFACT_MISSING:rel000-execution-dotnet", regression["reasons"])
        failing = World(self, outcome="FAILED")
        failing.run()
        regression = failing.output_json("rel001-regression-evidence.json")
        self.assertEqual("FAILED", regression["status"])
        self.assertTrue(any(r.startswith("RUN_HAS_FAILED_OR_SKIPPED_TESTS:") for r in regression["reasons"]))

    def test_invalid_inputs_build_nothing(self):
        cases = {
            "twelve_jobs": lambda w: (w.base / "jobs.json").write_text(json.dumps(foundation_records(w.sha, jobs=12)[1])),
            "pull_request_run": lambda w: (w.base / "run.json").write_text(json.dumps(
                {**foundation_records(w.sha)[0], "event": "pull_request"})),
            "output_exists": lambda w: w.output.mkdir(),
            "dispatch_not_main": lambda w: w.args.__setitem__(w.args.index("refs/heads/main"), "refs/heads/development"),
            "malformed_drill": lambda w: setattr(w, "drill", "EVIDENCE nonsense"),
        }
        for name, mutate in cases.items():
            with self.subTest(case=name):
                world = World(self)
                mutate(world)
                code, result = world.run()
                self.assertEqual(2, code)
                self.assertEqual("REL001_REPORT_NOT_BUILT", result["result"])
                if name != "output_exists":
                    self.assertFalse(world.output.exists())

    def test_tested_tree_must_be_the_tested_commit(self):
        world = World(self)
        (world.root / "extra.txt").write_text("x", encoding="utf-8")
        git(world.root, "-c", "user.email=r@example.invalid", "-c", "user.name=r", "add", "-A")
        git(world.root, "-c", "user.email=r@example.invalid", "-c", "user.name=r", "commit", "-q", "-m", "later")
        code, result = world.run()
        self.assertEqual(2, code)
        self.assertEqual("TESTED_TREE_MISMATCH", result["reason_code"])


class RedactionTests(unittest.TestCase):
    def test_personal_and_secret_shapes_are_refused(self):
        for text in ("contact: persona@example.com", "tel +5216671234567", "id 11111111-1111-1111-1111-111111111111",
                     "https://example.invalid/x", "sig=abc", "execution=job-pv-pilot-verify-1"):
            with self.subTest(text=text), self.assertRaises(rel001.Rel001Error):
                rel001.assert_redacted("file", text)

    def test_shas_and_run_ids_are_allowed(self):
        rel001.assert_redacted("file", json.dumps({"sha": "a" * 40, "run": "41186501080"}))

    def test_formatted_phone_numbers_are_refused(self):
        for text in ("+52 667 123 4567", "+52-667-123-4567", "+52.667.123.4567", "+52 (667) 123-4567",
                     "+(52) 667 123 4567", "+1 (555) 010-0199", "tel: +52 1 667 123 4567"):
            with self.subTest(text=text), self.assertRaises(rel001.Rel001Error) as raised:
                rel001.assert_redacted("file", text)
            self.assertEqual("OUTPUT_NOT_REDACTED", raised.exception.code)

    def test_the_tools_own_values_are_never_phone_shaped(self):
        legitimate = {
            "tested_git_sha": "0123456789abcdef0123456789abcdef01234567",
            "foundation_run_id": "41186501080",
            "workflow_run_id": "19876543210",
            "generated_at": "2026-11-02T10:15:00Z",
            "period": ["2026-11-09", "2026-11-23"],
            "offset": "2026-11-02T10:15:00+00:00",
            "counts": {"COVERED": 12, "GAP": 6, "planned_real_deliveries": 100, "delta": "+12"},
            "latency": {"samples": 100, "p95_ms": 1999, "ratio": 0.98},
        }
        rel001.assert_redacted("file", json.dumps(legitimate, indent=2))
        rel001.assert_redacted("file.md", "SHA 0123456789abcdef0123456789abcdef01234567, corrida 41186501080, "
                                          "12 cubiertas y 6 pendientes, 2026-11-02, p95 1999 ms (+12 ms)")


class WorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = WORKFLOW.read_text(encoding="utf-8")
        cls.workflow = yaml.safe_load(cls.text)

    def test_dispatch_is_the_only_trigger(self):
        triggers = self.workflow.get("on", self.workflow.get(True))
        self.assertEqual(["workflow_dispatch"], list(triggers))
        self.assertEqual({"tested_git_sha", "foundation_run_id", "restore_drill_evidence"},
                         set(triggers["workflow_dispatch"]["inputs"]))

    def test_least_privilege_and_three_jobs(self):
        self.assertEqual({}, self.workflow["permissions"])
        jobs = self.workflow["jobs"]
        self.assertEqual(["gate", "rehearsal", "report"], list(jobs))
        self.assertEqual({"contents": "read", "actions": "read"}, jobs["gate"]["permissions"])
        self.assertEqual({"contents": "read"}, jobs["rehearsal"]["permissions"])
        self.assertEqual({"contents": "read", "actions": "read"}, jobs["report"]["permissions"])
        for job in jobs.values():
            self.assertEqual("ubuntu-24.04", job["runs-on"])
            self.assertNotIn("environment", job)
        self.assertNotIn("secrets.", self.text)
        self.assertNotIn("azure/login", self.text)
        self.assertNotIn("id-token", self.text)

    def test_no_retry_sleep_or_input_interpolation_in_shells(self):
        self.assertNotRegex(self.text.lower(), r"\bretry\b|\bsleep\b")
        for job_id, job in self.workflow["jobs"].items():
            for step in job["steps"]:
                self.assertNotIn("${{", str(step.get("run", "")), f"{job_id}: {step.get('name')}")

    def test_the_provenance_gate_runs_before_the_rehearsal_the_tested_checkout_and_the_report(self):
        jobs = self.workflow["jobs"]
        self.assertEqual("gate", jobs["rehearsal"]["needs"])
        self.assertEqual(["gate", "rehearsal"], jobs["report"]["needs"])
        self.assertEqual("always() && needs.gate.result == 'success'", jobs["report"]["if"])
        for job_id in ("gate", "report"):
            names = [step["name"] for step in jobs[job_id]["steps"]]
            gate = names.index("Enforce the 13/13 provenance gate")
            self.assertIn("azr001_static_guards.py deploy-gate", jobs[job_id]["steps"][gate]["run"])
            if job_id == "report":
                self.assertLess(gate, names.index("Check out tested_git_sha"))
                self.assertLess(gate, names.index("Build the REL-001 entry report"))

    def test_the_rehearsal_runs_only_its_category_on_the_tested_commit_and_publishes_only_its_evidence(self):
        job = self.workflow["jobs"]["rehearsal"]
        checkout = job["steps"][0]
        self.assertEqual("${{ inputs.tested_git_sha }}", checkout["with"]["ref"])
        runs = [step.get("run", "") for step in job["steps"]]
        tests = [run for run in runs if "dotnet test" in run]
        self.assertEqual(1, len(tests))
        self.assertIn('--filter "Category=PilotRehearsal"', tests[0])
        upload = next(step for step in job["steps"] if step.get("uses", "").startswith("actions/upload-artifact"))
        self.assertEqual("rel001-rehearsal-evidence", upload["with"]["name"])
        self.assertEqual("${{ runner.temp }}/rel001-rehearsal/rel001-rehearsal-evidence.json", upload["with"]["path"])
        self.assertEqual("error", upload["with"]["if-no-files-found"])
        report = self.workflow["jobs"]["report"]
        download = next(step for step in report["steps"] if step.get("name") == "Download this run's rehearsal evidence")
        self.assertEqual("needs.rehearsal.result == 'success'", download["if"])
        self.assertNotIn("run-id", download["with"])

    def test_no_other_workflow_runs_the_rehearsal(self):
        # Z5 "Solo en el ensayo": every dotnet job excludes the category and nothing else selects it.
        for path in sorted((REPO_ROOT / ".github/workflows").glob("*.yml")):
            text = path.read_text(encoding="utf-8")
            with self.subTest(workflow=path.name):
                if path.name == WORKFLOW.name:
                    continue
                self.assertNotIn("Category=PilotRehearsal", text.replace("Category!=PilotRehearsal", ""))
                workflow = yaml.safe_load(text)
                if "dotnet" in workflow.get("jobs", {}):
                    run = next(step["run"] for step in workflow["jobs"]["dotnet"]["steps"] if step.get("name") == "Test")
                    self.assertIn("&Category!=PilotRehearsal", run)

    def test_only_the_allowlist_is_published(self):
        step = next(s for s in self.workflow["jobs"]["report"]["steps"] if s.get("uses", "").startswith("actions/upload-artifact"))
        paths = [line.strip() for line in step["with"]["path"].splitlines() if line.strip()]
        self.assertEqual([f"${{{{ runner.temp }}}}/rel001-output/{name}" for name in rel001.OUTPUT_FILES], paths)
        self.assertEqual("rel001-pilot-readiness-report", step["with"]["name"])
        self.assertEqual("always()", step["if"])

    def test_foundation_and_pr_validation_run_these_tests(self):
        for name, job in (("ci.yml", "normative"), ("pr-validation.yml", "azr-static")):
            workflow = yaml.safe_load((REPO_ROOT / ".github/workflows" / name).read_text(encoding="utf-8"))
            runs = [step.get("run", "") for step in workflow["jobs"][job]["steps"]]
            self.assertIn('python -m unittest discover -s ./tools/rel-001 -p "test_*.py" -v', runs)
            self.assertNotIn("rel001.py report", (REPO_ROOT / ".github/workflows" / name).read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
