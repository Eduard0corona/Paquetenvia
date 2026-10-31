"""ENV-001 F-05: the deploy image build smoke step of azr-static (PR Validation) and of Foundation's normative job.

The step builds every image the Azure deploy workflows build, from the same Dockerfiles, build context and build
arguments as ``deploy-azure-pilot.yml``, and then runs each image offline: it must run as a non-root uid, each db-ops
image must pass the migrator's ``verify``, and each web image must ship the production dependencies of apps/web and
nothing else (``tools/ci/web_image_dependencies.py``) and serve ``GET /`` with 200 from ``next start``. These tests pin
that list to the Dockerfiles in the repository and to the pilot workflow, and run the real step with a stand-in
``docker`` (the real build runs in CI).
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
SMOKE_CALL = re.compile(r"^smoke (\S+) (\S+) (verify|web|no-verify)(.*)$", re.MULTILINE)
BUILD_ARG = re.compile(r'--build-arg "([A-Z_]+)=([^"]*)"')
# The pilot deploy's build: `$(build <name> <dockerfile> [--build-arg "..."]...)`, possibly over continuation lines.
PILOT_BUILD_CALL = re.compile(r"\$\(build\s+([a-z0-9-]+)\s+(\S+?)(\s[^)]*)?\)")
CI_BUILD_LINE = 'docker build --pull --file "${dockerfile}" --build-arg "SOURCE_SHA=${source_sha}" "$@" --tag "${image}" .'
WEB_DOCKERFILES = ("deploy/azure/pilot/Dockerfile.web", "deploy/azure/Dockerfile.web")
WEB_MANIFESTS = ["apps/web/package.json", "apps/web/pnpm-lock.yaml", "apps/web/pnpm-workspace.yaml"]
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


def dockerfile_stages(path: str) -> list[tuple[str | None, list[str]]]:
    """(stage name or None, instructions) per FROM, backslash continuations joined and comments dropped."""
    stages: list[tuple[str | None, list[str]]] = []
    pending = ""
    for raw in (REPOSITORY_ROOT / path).read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.endswith("\\"):
            pending += line[:-1] + " "
            continue
        line, pending = pending + line, ""
        if line.split()[0].upper() == "FROM":
            match = re.match(r"^FROM\s+\S+(?:\s+AS\s+(\S+))?$", line, re.IGNORECASE)
            assert match, line
            stages.append((match[1], []))
        stages[-1][1].append(line)
    return stages


def production_store() -> list[str]:
    """The pnpm store entries of the direct production dependencies of apps/web, as `ls -1A` prints them."""
    lockfile = yaml.safe_load((REPOSITORY_ROOT / "apps/web/pnpm-lock.yaml").read_text(encoding="utf-8"))
    locked = lockfile["importers"]["."]["dependencies"]
    return sorted(f"{name.replace('/', '+')}@{spec['version'].split('(', 1)[0]}" for name, spec in locked.items()) + [
        "lock.yaml", "node_modules"]


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
        # Each image runs without a network: `id -u`, the migrator's verify, the web store listing and `next start`
        # need none. No port is published: the web probe runs inside the container.
        self.assertIn('docker run --rm --network none --entrypoint id "${image}" -u', run)
        self.assertIn('docker run --rm --network none "${image}" verify', run)
        self.assertIn('docker run --rm --network none --entrypoint ls "${image}" -1A /app/node_modules/.pnpm \\\n'
                      '    | python ./tools/ci/web_image_dependencies.py --name "${name}" --dockerfile "${dockerfile}"',
                      run)
        self.assertIn('if ! docker run --rm --network none --entrypoint node "${image}" -e "${start_probe}"; then', run)
        self.assertEqual(4, run.count("docker run "))
        for forbidden in ("--publish", " -p ", "--network host", "--user", "--detach", "docker exec"):
            self.assertNotIn(forbidden, run, forbidden)
        # Foundation CI adds no retries or sleeps (REL-000 test_174 reads ci.yml for these words): the start check
        # waits on the promise `next start` itself resolves once it is ready, then sends a single request.
        self.assertNotRegex(run.lower(), r"\bretry\b|\bsleep\b|\bseq\b|\bwhile\b|\buntil\b|\bfor\b .* in ")
        probe = re.search(r"^start_probe='([^']*)'$", run, re.MULTILINE | re.DOTALL)
        self.assertIsNotNone(probe)
        for fragment in ('fs.accessSync("node_modules/.bin/next", fs.constants.X_OK)',
                         'require("next/dist/cli/next-start").nextStart({ port: 3000 })',
                         '.then(() => fetch("http://127.0.0.1:3000/"))',
                         "process.exit(response.status === 200 ? 0 : 1)",
                         "}, 120000).unref()"):
            self.assertIn(fragment, probe[1])
        # The image's command is the one the probe stands in for.
        for dockerfile in WEB_DOCKERFILES:
            self.assertIn('CMD ["./node_modules/.bin/next", "start", "-p", "3000"]',
                          (REPOSITORY_ROOT / dockerfile).read_text(encoding="utf-8").splitlines())
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

    def test_every_db_ops_image_runs_verify_and_every_web_image_the_web_checks(self):
        for name, dockerfile, check, _ in smoke_calls():
            with self.subTest(dockerfile=dockerfile):
                expected = ("verify" if dockerfile.endswith("Dockerfile.db-ops")
                            else "web" if dockerfile.endswith("Dockerfile.web") else "no-verify")
                self.assertEqual(expected, check)
                text = (REPOSITORY_ROOT / dockerfile).read_text(encoding="utf-8")
                self.assertEqual(check == "verify", "Paqueteria.DatabaseMigrator.dll" in text, name)
                self.assertEqual(check == "web", '"./node_modules/.bin/next", "start"' in text, name)
        self.assertEqual(sorted(WEB_DOCKERFILES), sorted(d for d in DEPLOY_DOCKERFILES if d.endswith("Dockerfile.web")))


class WebImageDependencyTests(unittest.TestCase):
    """The final stage of each web Dockerfile takes node_modules from a stage that installs only the production
    dependencies, from the manifests alone (the build stage keeps the devDependencies `next build` needs)."""

    def test_node_modules_come_from_a_production_only_install(self):
        build_install = "RUN corepack enable && corepack prepare pnpm@11.15.1 --activate && pnpm install --frozen-lockfile"
        prod_install = ("RUN corepack enable && corepack prepare pnpm@11.15.1 --activate && "
                        "pnpm install --prod --frozen-lockfile")
        for dockerfile in WEB_DOCKERFILES:
            with self.subTest(dockerfile=dockerfile):
                (build_name, build), (prod_name, prod), (final_name, final) = dockerfile_stages(dockerfile)
                self.assertEqual(("build", "prod-deps", None), (build_name, prod_name, final_name))
                # The build stage installs everything (`next build` needs the devDependencies), then builds.
                self.assertIn(build_install, build)
                self.assertIn("RUN pnpm build", build)
                # prod-deps: the same base and pnpm, the three manifests and nothing else, a production-only install.
                self.assertEqual(build[0].split()[1], prod[0].split()[1])
                self.assertEqual(["COPY " + " ".join(WEB_MANIFESTS) + " ./", prod_install],
                                 [line for line in prod if line.split()[0] in ("COPY", "RUN", "ADD")])
                # The final stage runs nothing, takes node_modules from prod-deps and everything else from build.
                copies = [line.split() for line in final if line.split()[0] in ("COPY", "ADD")]
                self.assertIn(["COPY", "--from=prod-deps", "/app/node_modules", "./node_modules"], copies)
                self.assertEqual(["--from=build"] * (len(copies) - 1),
                                 [copy[1] for copy in copies if copy[1] != "--from=prod-deps"])
                self.assertFalse([line for line in final if line.startswith("RUN ")], final)


# A stand-in for the Docker CLI: images are entries in $DOCKER_STATE/images.json (tag -> Dockerfile) and every call is
# logged to $DOCKER_STATE/calls as a JSON array. FAKE_BUILD_FAIL and FAKE_VERIFY_FAIL name Dockerfiles whose build or
# verify fails; FAKE_UIDS maps a Dockerfile to what `id -u` prints (default 1654). A web image's pnpm store lists
# FAKE_STORE (JSON), plus FAKE_STORE_ADD and minus FAKE_STORE_DROP (JSON maps of Dockerfile -> entries); FAKE_START_FAIL
# names Dockerfiles whose `next start` probe fails.
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
def store_json(name):
    return json.loads(os.environ.get(name, "{}"))
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
elif args[:4] == ["run", "--rm", "--network", "none"] and args[4:6] == ["--entrypoint", "ls"] and args[7:] == ["-1A", "/app/node_modules/.pnpm"]:
    dockerfile = images[args[6]]
    entries = [e for e in json.loads(os.environ["FAKE_STORE"]) + store_json("FAKE_STORE_ADD").get(dockerfile, [])
               if e not in store_json("FAKE_STORE_DROP").get(dockerfile, [])]
    print("\n".join(entries))
elif args[:4] == ["run", "--rm", "--network", "none"] and args[4:6] == ["--entrypoint", "node"] and args[7] == "-e" and len(args) == 9:
    if "nextStart" not in args[8] or listed("FAKE_START_FAIL", images[args[6]]):
        print("Error: Failed to load SWC binary for linux/x64", file=sys.stderr); sys.exit(1)
    print("GET / answered 200")
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
                           DOCKER_STATE=str(self.root / "state"), FAKE_STORE=json.dumps(production_store()))
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
                store = ["run", "--rm", "--network", "none", "--entrypoint", "ls", self.tag(name), "-1A",
                         "/app/node_modules/.pnpm"]
                starts = [call for call in calls if call[:6] == ["run", "--rm", "--network", "none", "--entrypoint", "node"]
                          and call[6] == self.tag(name)]
                self.assertEqual(check == "web", store in calls)
                self.assertEqual(1 if check == "web" else 0, len(starts))
                if check == "web":
                    # The store is read first; then one start probe, with the step's own script.
                    self.assertLess(calls.index(store), calls.index(starts[0]))
                    self.assertEqual("-e", starts[0][7])
                    self.assertIn('require("next/dist/cli/next-start").nextStart({ port: 3000 })', starts[0][8])
                    self.assertIn(f"{name}: {len(production_store()) - 2} package directories, all in the production "
                                  "graph of apps/web/pnpm-lock.yaml", result.stdout)
                self.assertIn(f"{name}: {dockerfile} built and checked in ", result.stdout)
        self.assertEqual(2, result.stdout.count("GET / answered 200"))
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

    def test_a_web_image_with_a_package_outside_the_production_graph_fails_before_it_starts(self):
        lockfile = yaml.safe_load((REPOSITORY_ROOT / "apps/web/pnpm-lock.yaml").read_text(encoding="utf-8"))
        vitest = lockfile["importers"]["."]["devDependencies"]["vitest"]["version"].split("(", 1)[0]
        for dockerfile, name in (("deploy/azure/pilot/Dockerfile.web", "pilot-web"), ("deploy/azure/Dockerfile.web", "dev-web")):
            for entry in (f"vitest@{vitest}_@types+node@26.1.1", "next@16.3.8"):
                with self.subTest(dockerfile=dockerfile, entry=entry):
                    (self.root / "state" / "calls").unlink(missing_ok=True)
                    result = self.run_step(FAKE_STORE_ADD=json.dumps({dockerfile: [entry]}))
                    self.assertNotEqual(0, result.returncode)
                    self.assertEqual(dockerfile, self.builds()[-1])
                    self.assertIn(f"{name}: {entry.split('_', 1)[0]}: outside the production graph", result.stdout)
                    self.assertIn(f"::error file={dockerfile}::{name} must ship the production dependencies of apps/web "
                                  "and nothing else: 1 problem(s)", result.stdout)
                    self.assertFalse([call for call in self.calls() if call[4:7] == ["--entrypoint", "node", self.tag(name)]])
                    self.assert_nothing_left()

    def test_a_web_image_without_a_production_dependency_fails(self):
        next_entry = next(entry for entry in production_store() if entry.startswith("next@"))
        result = self.run_step(FAKE_STORE_ADD=json.dumps({}),
                               FAKE_STORE_DROP=json.dumps({"deploy/azure/pilot/Dockerfile.web": [next_entry]}))
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("deploy/azure/pilot/Dockerfile.web", self.builds()[-1])
        self.assertIn(f"pilot-web: {next_entry}: a production dependency missing from the image", result.stdout)
        self.assert_nothing_left()

    def test_a_web_image_whose_server_cannot_start_fails_the_step(self):
        for dockerfile, name in (("deploy/azure/pilot/Dockerfile.web", "pilot-web"), ("deploy/azure/Dockerfile.web", "dev-web")):
            with self.subTest(dockerfile=dockerfile):
                (self.root / "state" / "calls").unlink(missing_ok=True)
                result = self.run_step(FAKE_START_FAIL=dockerfile)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(dockerfile, self.builds()[-1])
                self.assertIn("Failed to load SWC binary", result.stderr)
                self.assertIn(f"::error file={dockerfile}::{name}: next start did not serve GET / with 200", result.stdout)
                self.assertNotIn(f"{name}: {dockerfile} built and checked in ", result.stdout)
                self.assertEqual(["image", "rm", "--force"], self.calls()[-1][:3])
                self.assert_nothing_left()

if __name__ == "__main__":
    unittest.main()
