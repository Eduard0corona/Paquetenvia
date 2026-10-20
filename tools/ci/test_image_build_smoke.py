"""ENV-001 F-05: the deploy image build smoke step of azr-static (PR Validation) and of Foundation's normative job.

The step builds every image the Azure deploy workflows build, from the same Dockerfiles, build context and build
arguments as ``deploy-azure-pilot.yml``, and then runs each image offline: it must run as a non-root uid, and each
db-ops image must pass the migrator's ``verify``. These tests pin that list to the Dockerfiles in the repository and to
the pilot workflow, and run the real step with a stand-in ``docker`` (the real build runs in CI).
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

import yaml

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
CI_WORKFLOW = REPOSITORY_ROOT / ".github/workflows/ci.yml"
PR_VALIDATION_WORKFLOW = REPOSITORY_ROOT / ".github/workflows/pr-validation.yml"
PILOT_WORKFLOW = REPOSITORY_ROOT / ".github/workflows/deploy-azure-pilot.yml"
STEP_NAME = "Build and smoke-test the deploy images (no push)"
DEPLOY_DOCKERFILES = sorted(
    path.relative_to(REPOSITORY_ROOT).as_posix() for path in (REPOSITORY_ROOT / "deploy/azure").rglob("Dockerfile*")
)
PILOT_DOCKERFILES = {
    "deploy/azure/Dockerfile.api",
    "deploy/azure/Dockerfile.worker",
    "deploy/azure/pilot/Dockerfile.db-ops",
    "deploy/azure/pilot/Dockerfile.web",
}
SMOKE_CALL = re.compile(r"^smoke (\S+) (\S+) (verify|no-verify)(.*)$", re.MULTILINE)
BUILD_ARG = re.compile(r'--build-arg "([A-Z_]+)=([^"]*)"')
# The pilot deploy's build: `$(build <name> <dockerfile> [--build-arg "..."]...)`, possibly over continuation lines.
PILOT_BUILD_CALL = re.compile(r"\$\(build\s+([a-z0-9-]+)\s+(\S+?)(\s[^)]*)?\)")
CI_BUILD_LINE = 'docker build --pull --file "${dockerfile}" --build-arg "SOURCE_SHA=${source_sha}" "$@" --tag "${image}" .'
PILOT_BUILD_LINE = 'docker build --pull --file "${dockerfile}" --build-arg "SOURCE_SHA=${TESTED_GIT_SHA}" "$@" --tag "${ref}" .'


def load_jobs(path: Path) -> dict:
    return yaml.safe_load(path.read_text(encoding="utf-8"))["jobs"]


def smoke_step(job: dict) -> dict:
    steps = [step for step in job["steps"] if step.get("name") == STEP_NAME]
    assert len(steps) == 1, [step.get("name") for step in job["steps"]]
    return steps[0]


def smoke_calls() -> list[tuple[str, str, str, dict[str, str]]]:
    run = smoke_step(load_jobs(PR_VALIDATION_WORKFLOW)["azr-static"])["run"]
    return [(name, dockerfile, check, dict(BUILD_ARG.findall(rest))) for name, dockerfile, check, rest in SMOKE_CALL.findall(run)]


def pilot_builds() -> dict[str, dict[str, str]]:
    text = PILOT_WORKFLOW.read_text(encoding="utf-8")
    return {dockerfile: dict(BUILD_ARG.findall(rest or "")) for _, dockerfile, rest in PILOT_BUILD_CALL.findall(text)}


class SmokeStepPlacementTests(unittest.TestCase):
    def test_both_workflows_run_the_same_step_last_in_the_job_that_owns_the_azure_checks(self):
        foundation = load_jobs(CI_WORKFLOW)["normative"]
        pr = load_jobs(PR_VALIDATION_WORKFLOW)["azr-static"]
        self.assertEqual(smoke_step(foundation), smoke_step(pr))
        for job in (foundation, pr):
            self.assertEqual(STEP_NAME, job["steps"][-1]["name"])
            # It runs only after the static guards passed.
            names = [step["name"] for step in job["steps"]]
            self.assertLess(names.index("Run AZR-001 §28 static guards"), names.index(STEP_NAME))
        step = smoke_step(pr)
        self.assertEqual("bash", step["shell"])
        self.assertEqual({"name", "shell", "run"}, set(step))
        self.assertTrue(step["run"].startswith("set -euo pipefail\n"))

    def test_the_step_never_pushes_logs_in_or_reaches_azure(self):
        run = smoke_step(load_jobs(PR_VALIDATION_WORKFLOW)["azr-static"])["run"]
        for forbidden in ("docker push", "docker login", "az ", "acr", "${{"):
            self.assertNotIn(forbidden, run, forbidden)
        # Each image runs without a network: `id -u` and the migrator's verify need none.
        self.assertIn('docker run --rm --network none --entrypoint id "${image}" -u', run)
        self.assertIn('docker run --rm --network none "${image}" verify', run)
        self.assertIn(CI_BUILD_LINE, run)


class SmokeStepCoverageTests(unittest.TestCase):
    def test_every_deploy_dockerfile_is_built_exactly_once(self):
        calls = smoke_calls()
        self.assertEqual(DEPLOY_DOCKERFILES, sorted(dockerfile for _, dockerfile, _, _ in calls))
        self.assertEqual(len(calls), len({name for name, _, _, _ in calls}))
        self.assertEqual(6, len(calls))

    def test_the_pilot_images_are_built_as_the_pilot_workflow_builds_them(self):
        pilot = pilot_builds()
        self.assertEqual(PILOT_DOCKERFILES, set(pilot))
        self.assertIn(PILOT_BUILD_LINE, PILOT_WORKFLOW.read_text(encoding="utf-8"))
        calls = {dockerfile: (name, args) for name, dockerfile, _, args in smoke_calls()}
        for dockerfile, arguments in pilot.items():
            with self.subTest(dockerfile=dockerfile):
                name, args = calls[dockerfile]
                self.assertTrue(name.startswith("pilot-"), name)
                self.assertEqual(set(arguments), set(args))
        # The pilot web carries no support link while GATE-001 is open (PILOT_TRACKING_SUPPORT_URL unset).
        self.assertEqual({"NEXT_PUBLIC_TRACKING_SUPPORT_URL": "${support_url}"}, pilot["deploy/azure/pilot/Dockerfile.web"])
        self.assertEqual({"NEXT_PUBLIC_TRACKING_SUPPORT_URL": ""}, calls["deploy/azure/pilot/Dockerfile.web"][1])
        self.assertEqual({name for name, _, _, _ in smoke_calls() if name.startswith("pilot-")},
                         {calls[dockerfile][0] for dockerfile in PILOT_DOCKERFILES})

    def test_the_dev_synthetic_web_gets_an_https_api_base_url(self):
        # deploy/azure/Dockerfile.web always defines NEXT_PUBLIC_API_BASE_URL, and next.config.ts refuses an empty or
        # non-HTTPS value in a production build. The reserved .invalid name never resolves.
        calls = {dockerfile: args for _, dockerfile, _, args in smoke_calls()}
        self.assertEqual({"NEXT_PUBLIC_API_BASE_URL": "https://api.example.invalid"}, calls["deploy/azure/Dockerfile.web"])

    def test_every_db_ops_image_and_only_those_run_verify(self):
        for name, dockerfile, check, _ in smoke_calls():
            with self.subTest(dockerfile=dockerfile):
                self.assertEqual("verify" if dockerfile.endswith("Dockerfile.db-ops") else "no-verify", check)
                text = (REPOSITORY_ROOT / dockerfile).read_text(encoding="utf-8")
                self.assertEqual(check == "verify", "Paqueteria.DatabaseMigrator.dll" in text, name)


# A stand-in for the Docker CLI: images are entries in $DOCKER_STATE/images.json (tag -> Dockerfile) and every call is
# logged to $DOCKER_STATE/calls as a JSON array. FAKE_BUILD_FAIL and FAKE_VERIFY_FAIL name Dockerfiles whose build or
# verify fails; FAKE_UIDS maps a Dockerfile to what `id -u` prints (default 1654).
FAKE_DOCKER = r'''#!/usr/bin/env python3
import json, os, sys
state = os.environ["DOCKER_STATE"]
args = sys.argv[1:]
with open(os.path.join(state, "calls"), "a", encoding="utf-8") as log:
    log.write(json.dumps(args) + "\n")
path = os.path.join(state, "images.json")
images = json.load(open(path, encoding="utf-8")) if os.path.exists(path) else {}
def listed(name, dockerfile):
    return dockerfile in os.environ.get(name, "").split(",")
if args[0] == "build":
    dockerfile = args[args.index("--file") + 1]
    if listed("FAKE_BUILD_FAIL", dockerfile):
        print("ERROR: failed to solve: " + dockerfile, file=sys.stderr); sys.exit(1)
    images[args[args.index("--tag") + 1]] = dockerfile
    json.dump(images, open(path, "w", encoding="utf-8"))
elif args[:4] == ["run", "--rm", "--network", "none"] and args[4:6] == ["--entrypoint", "id"] and args[7:] == ["-u"]:
    print(json.loads(os.environ.get("FAKE_UIDS", "{}")).get(images[args[6]], "1654"))
elif args[:4] == ["run", "--rm", "--network", "none"] and args[5:] == ["verify"]:
    if listed("FAKE_VERIFY_FAIL", images[args[4]]):
        print("Baseline v0.6: FAILED", file=sys.stderr); sys.exit(5)
    print("Baseline v0.6: VERIFIED")
elif args[:3] == ["image", "rm", "--force"]:
    for tag in args[3:]:
        images.pop(tag)
    json.dump(images, open(path, "w", encoding="utf-8"))
else:
    print("unexpected docker call: " + " ".join(args), file=sys.stderr); sys.exit(9)
'''


class SmokeStepRunTests(unittest.TestCase):
    """Runs the real step as GitHub does (`bash --noprofile --norc -eo pipefail`) from the repository root, with the
    stand-in `docker` first on PATH."""

    @classmethod
    def setUpClass(cls):
        cls.step = smoke_step(load_jobs(PR_VALIDATION_WORKFLOW)["azr-static"])
        cls.calls_in_order = smoke_calls()
        cls.head = subprocess.run(["git", "-C", str(REPOSITORY_ROOT), "rev-parse", "HEAD"], check=True,
                                  capture_output=True, text=True).stdout.strip()

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="image-smoke-")
        self.root = Path(self.tmp.name)
        (self.root / "bin").mkdir()
        (self.root / "state").mkdir()
        docker = self.root / "bin" / "docker"
        docker.write_text(FAKE_DOCKER, encoding="utf-8")
        docker.chmod(0o755)

    def tearDown(self):
        self.tmp.cleanup()

    def run_step(self, **env: str) -> subprocess.CompletedProcess:
        script = self.root / "step.sh"
        script.write_text(self.step["run"], encoding="utf-8")
        environment = dict(os.environ, PATH=f"{self.root / 'bin'}{os.pathsep}{os.environ.get('PATH', '')}",
                           DOCKER_STATE=str(self.root / "state"))
        environment.update(env)
        return subprocess.run(["bash", "--noprofile", "--norc", "-eo", "pipefail", str(script)], cwd=REPOSITORY_ROOT,
                              capture_output=True, text=True, env=environment)

    def calls(self) -> list[list[str]]:
        path = self.root / "state" / "calls"
        return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()] if path.exists() else []

    def builds(self) -> list[str]:
        return [call[call.index("--file") + 1] for call in self.calls() if call[0] == "build"]

    def tag(self, name: str) -> str:
        return f"pv-image-smoke-{name}:{self.head}"

    def assert_nothing_left(self):
        self.assertEqual({}, json.loads((self.root / "state" / "images.json").read_text(encoding="utf-8")))

    def test_every_image_is_built_from_the_tested_commit_and_checked(self):
        result = self.run_step()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([dockerfile for _, dockerfile, _, _ in self.calls_in_order], self.builds())
        calls = self.calls()
        for name, dockerfile, check, args in self.calls_in_order:
            with self.subTest(dockerfile=dockerfile):
                build = next(call for call in calls if call[0] == "build" and dockerfile in call)
                expected = ["build", "--pull", "--file", dockerfile, "--build-arg", f"SOURCE_SHA={self.head}"]
                for key, value in args.items():
                    expected += ["--build-arg", f"{key}={value}"]
                self.assertEqual(expected + ["--tag", self.tag(name), "."], build)
                self.assertIn(["run", "--rm", "--network", "none", "--entrypoint", "id", self.tag(name), "-u"], calls)
                self.assertEqual(check == "verify", ["run", "--rm", "--network", "none", self.tag(name), "verify"] in calls)
                self.assertIn(f"{name}: {dockerfile} built and checked in ", result.stdout)
        self.assertEqual(["image", "rm", "--force", *[self.tag(name) for name, _, _, _ in self.calls_in_order]], calls[-1])
        self.assertIn("6 images built and checked in ", result.stdout)
        self.assert_nothing_left()

    def test_an_image_that_runs_as_root_fails_the_step(self):
        for uid in ("0", "", "root", "1654 0"):
            with self.subTest(uid=uid):
                (self.root / "state" / "calls").unlink(missing_ok=True)
                result = self.run_step(FAKE_UIDS=json.dumps({"deploy/azure/Dockerfile.worker": uid}))
                self.assertNotEqual(0, result.returncode)
                self.assertIn("::error file=deploy/azure/Dockerfile.worker::pilot-worker runs as uid", result.stdout)
                self.assertEqual(["deploy/azure/Dockerfile.api", "deploy/azure/Dockerfile.worker"], self.builds())
                self.assert_nothing_left()

    def test_a_db_ops_image_without_its_migration_sources_fails_the_step(self):
        for dockerfile in ("deploy/azure/pilot/Dockerfile.db-ops", "deploy/azure/Dockerfile.db-ops"):
            with self.subTest(dockerfile=dockerfile):
                (self.root / "state" / "calls").unlink(missing_ok=True)
                result = self.run_step(FAKE_VERIFY_FAIL=dockerfile)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(dockerfile, self.builds()[-1])
                self.assertIn("Baseline v0.6: FAILED", result.stderr)
                self.assert_nothing_left()

    def test_a_broken_dockerfile_stops_the_step_and_removes_what_was_built(self):
        result = self.run_step(FAKE_BUILD_FAIL="deploy/azure/pilot/Dockerfile.web")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("deploy/azure/pilot/Dockerfile.web", self.builds()[-1])
        self.assertNotIn("deploy/azure/Dockerfile.web", self.builds())
        removed = self.calls()[-1]
        self.assertEqual(["image", "rm", "--force"], removed[:3])
        self.assertEqual(4, len(removed[3:]))
        self.assertNotIn(self.tag("pilot-web"), removed)
        self.assert_nothing_left()


if __name__ == "__main__":
    unittest.main()
