"""OPS-005: the platform end-to-end suite runs in exactly one existing job, and its results reach REL-000.

The suite lives in tests/Paqueteria.IntegrationTests (folder PlatformEndToEnd, category PlatformEndToEnd), as the owner
decided in OPS-005-SUITE-IN-INTEGRATIONTESTS-2026-10-14, and runs inside ``driver-stops-pwa`` in both Foundation CI and
PR Validation; no job is added, so the 13-job Foundation topology is unchanged. These tests pin:

* the host job runs only the suite's category from the IntegrationTests build it already restores and builds, with no
  restore or build step of its own, and publishes its TRX;
* the host job's ``collect-results`` step attributes that TRX to the IntegrationTests project and the suite's category,
  and the evidence it produces from a TRX of the suite carries those results (rel000 ``collect_execution_results``);
* every ``dotnet`` job (Foundation, PR Validation, development push smoke) excludes the category;
* the change classifier routes the suite's files to the host job and to ``dotnet`` as any IntegrationTests file;
* the category in the C# source is the one the workflows name.
"""

from __future__ import annotations

import argparse
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

import yaml

sys.path.insert(0, str(Path(__file__).resolve().parent))

import classify_changes as classifier  # noqa: E402

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = REPOSITORY_ROOT / ".github/workflows"
FOUNDATION_AND_PR = ("ci.yml", "pr-validation.yml")
HOST_JOB = "driver-stops-pwa"
CATEGORY = "PlatformEndToEnd"
PROJECT = "tests/Paqueteria.IntegrationTests/Paqueteria.IntegrationTests.csproj"
SUITE_DIRECTORY = "tests/Paqueteria.IntegrationTests/PlatformEndToEnd"
RESERVED_END_TO_END_PROJECT = "tests/Paqueteria.EndToEndTests"
TRX = "./TestResults/driver-stops-pwa/platform-end-to-end.trx"
TRX_SPEC = f"{PROJECT}|{CATEGORY}={TRX}"


def load_jobs(name: str) -> dict:
    return yaml.safe_load((WORKFLOWS / name).read_text(encoding="utf-8"))["jobs"]


def step(job: dict, name: str) -> dict:
    matches = [candidate for candidate in job["steps"] if candidate.get("name") == name]
    if len(matches) != 1:
        raise AssertionError(f"expected one step named {name!r}, found {len(matches)}")
    return matches[0]


def step_index(job: dict, name: str) -> int:
    return [candidate.get("name") for candidate in job["steps"]].index(name)


class HostJobTests(unittest.TestCase):
    def test_the_suite_runs_in_exactly_one_job_of_each_workflow(self):
        for name in FOUNDATION_AND_PR:
            with self.subTest(workflow=name):
                running = sorted(
                    job_id
                    for job_id, job in load_jobs(name).items()
                    for candidate in job.get("steps", [])
                    if f'--filter "Category={CATEGORY}"' in str(candidate.get("run", ""))
                )
                self.assertEqual([HOST_JOB], running)

    def test_the_host_job_runs_the_suite_from_its_integration_build_after_chromium(self):
        for name in FOUNDATION_AND_PR:
            with self.subTest(workflow=name):
                job = load_jobs(name)[HOST_JOB]
                restore = step(job, "Restore locked integration dependencies")["run"].split()
                self.assertEqual(["dotnet", "restore", f"./{PROJECT}", "--locked-mode"], restore)
                build = step(job, "Build driver PWA tests")["run"].split()
                self.assertEqual(
                    ["dotnet", "build", f"./{PROJECT}", "--configuration", "Release", "--no-restore"], build
                )
                run_step = step(job, "Execute platform end-to-end suite (OPS-005)")
                self.assertEqual({"MSBUILDDISABLENODEREUSE": "1"}, run_step["env"])
                self.assertEqual(
                    [
                        "dotnet",
                        "test",
                        f"./{PROJECT}",
                        "--configuration",
                        "Release",
                        "--no-build",
                        "--filter",
                        f'"Category={CATEGORY}"',
                        "--logger",
                        '"trx;LogFileName=platform-end-to-end.trx"',
                        "--results-directory",
                        "./TestResults/driver-stops-pwa",
                    ],
                    run_step["run"].split(),
                )
                order = [
                    step_index(job, "Restore locked integration dependencies"),
                    step_index(job, "Build driver PWA tests"),
                    step_index(job, "Install frozen web dependencies"),
                    step_index(job, "Install Playwright Chromium"),
                    step_index(job, "Execute real offline operation flows"),
                    step_index(job, "Execute platform end-to-end suite (OPS-005)"),
                    step_index(job, "Correlate executed driver tests"),
                    step_index(job, "Publish structured driver execution evidence"),
                ]
                self.assertEqual(sorted(order), order)

    def test_the_suite_adds_no_restore_or_build_step_and_no_workflow_names_the_reserved_project(self):
        for name in FOUNDATION_AND_PR:
            with self.subTest(workflow=name):
                job = load_jobs(name)[HOST_JOB]
                restores_and_builds = [
                    candidate["run"].split()[:3]
                    for candidate in job["steps"]
                    if str(candidate.get("run", "")).split()[:2] in (["dotnet", "restore"], ["dotnet", "build"])
                ]
                self.assertEqual(
                    [["dotnet", "restore", f"./{PROJECT}"], ["dotnet", "build", f"./{PROJECT}"]], restores_and_builds
                )
        for path in sorted(WORKFLOWS.glob("*.yml")):
            with self.subTest(workflow=path.name):
                self.assertNotIn(RESERVED_END_TO_END_PROJECT, path.read_text(encoding="utf-8"))

    def test_the_host_job_attributes_the_suite_trx_to_its_project_and_category(self):
        for name in FOUNDATION_AND_PR:
            with self.subTest(workflow=name):
                run = step(load_jobs(name)[HOST_JOB], "Correlate executed driver tests")["run"]
                self.assertEqual(1, run.count(f'--trx "{TRX_SPEC}"'))
                self.assertIn("--job driver-stops-pwa", run)
                self.assertIn("--artifact-name rel000-execution-driver-stops-pwa", run)

    def test_every_dotnet_job_excludes_the_suite(self):
        for name in (*FOUNDATION_AND_PR, "development-push.yml"):
            with self.subTest(workflow=name):
                run = step(load_jobs(name)["dotnet"], "Test")["run"]
                self.assertIn(f"&Category!={CATEGORY}", run)
                self.assertNotIn(f"Category={CATEGORY}", run.replace(f"Category!={CATEGORY}", ""))

    def test_the_csharp_category_is_the_one_the_workflows_name(self):
        source = (REPOSITORY_ROOT / SUITE_DIRECTORY / "PlatformEndToEndCategory.cs").read_text(encoding="utf-8")
        self.assertIn(f'internal const string Name = "{CATEGORY}";', source)


class ClassifierRoutingTests(unittest.TestCase):
    """The suite's files are IntegrationTests files: the classifier routes them to the host job, never as DEPS."""

    def test_a_suite_file_runs_the_host_job_and_dotnet_as_backend(self):
        config = classifier.load_config(classifier.DEFAULT_CONFIG_PATH)
        for relative in sorted((REPOSITORY_ROOT / SUITE_DIRECTORY).glob("*.cs")):
            path = relative.relative_to(REPOSITORY_ROOT).as_posix()
            with self.subTest(path=path):
                self.assertEqual(["BACKEND"], classifier.match_domains(config, path))
                plan = classifier.classify_paths(
                    config,
                    [path],
                    head_ref="feature/x",
                    head_repo="Eduard0corona/Paquetenvia",
                    repository="Eduard0corona/Paquetenvia",
                )
                self.assertEqual("SELECTIVE", plan["classification"])
                self.assertTrue({"dotnet", HOST_JOB} <= set(plan["required_jobs"]))


TRX_TEMPLATE = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testId="00000000-0000-0000-0000-000000000001" testName="journey" outcome="Passed" duration="00:00:20" />
    <UnitTestResult testId="00000000-0000-0000-0000-000000000002" testName="limit" outcome="Passed" duration="00:00:05" />
  </Results>
  <TestDefinitions>
    <UnitTest name="journey" id="00000000-0000-0000-0000-000000000001">
      <TestCategory><TestCategoryItem TestCategory="PlatformEndToEnd" /></TestCategory>
      <TestMethod className="Paqueteria.IntegrationTests.PlatformEndToEnd.MainJourneyEndToEndTests, Paqueteria.IntegrationTests"
                  name="Main_journey_runs_through_the_real_screens_within_the_platform_budgets" />
    </UnitTest>
    <UnitTest name="limit" id="00000000-0000-0000-0000-000000000002">
      <TestCategory><TestCategoryItem TestCategory="PlatformEndToEnd" /></TestCategory>
      <TestMethod className="Paqueteria.IntegrationTests.PlatformEndToEnd.RateLimitEndToEndTests, Paqueteria.IntegrationTests"
                  name="A_limited_quote_answers_a_problem_with_Retry_After_and_the_wizard_shows_the_approved_text" />
    </UnitTest>
  </TestDefinitions>
</TestRun>
"""


class Rel000EvidenceTests(unittest.TestCase):
    """The workflow's own ``--trx`` spec, given to rel000 ``collect_execution_results`` with a TRX of the suite."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(REPOSITORY_ROOT / "tools" / "rel-000"))
        import rel000  # noqa: WPS433

        cls.rel000 = rel000

    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="ops005-evidence-"))
        self.addCleanup(shutil.rmtree, self.root, True)

    def collect(self, spec: str) -> dict:
        results = self.root / "TestResults" / "driver-stops-pwa"
        results.mkdir(parents=True)
        (results / "platform-end-to-end.trx").write_text(TRX_TEMPLATE, encoding="utf-8")
        project, _, path = spec.partition("=")
        args = argparse.Namespace(
            job=HOST_JOB,
            artifact_name="rel000-execution-driver-stops-pwa",
            workflow_run_id="1",
            workflow_run_attempt="1",
            source_head_sha="a" * 40,
            tested_git_sha="b" * 40,
            base_main_sha="c" * 40,
            trx=[f"{project}={self.root / path.removeprefix('./')}"],
            junit=None,
            structured=None,
            output=results / "rel000-execution-results.json",
        )
        return self.rel000.collect_execution_results(args)

    def test_the_host_job_spec_yields_the_suite_results_under_its_project_and_category(self):
        manifest = self.collect(TRX_SPEC)
        self.assertEqual(HOST_JOB, manifest["job"])
        self.assertEqual(
            [
                ("Paqueteria.IntegrationTests.PlatformEndToEnd.MainJourneyEndToEndTests."
                 "Main_journey_runs_through_the_real_screens_within_the_platform_budgets"),
                ("Paqueteria.IntegrationTests.PlatformEndToEnd.RateLimitEndToEndTests."
                 "A_limited_quote_answers_a_problem_with_Retry_After_and_the_wizard_shows_the_approved_text"),
            ],
            [test["fully_qualified_test_name"] for test in manifest["tests"]],
        )
        for test in manifest["tests"]:
            self.assertEqual(PROJECT, test["test_project"])
            self.assertEqual(CATEGORY, test["category"])
            self.assertEqual("PASSED", test["outcome"])
            self.assertTrue(test["executed"])
        self.assertEqual(["platform-end-to-end.trx"], [entry["path"] for entry in manifest["result_files"]])

    def test_the_dotnet_job_auto_mapping_attributes_the_suite_namespace_to_its_project(self):
        # The two instrument tests run uncategorized in the dotnet job, whose spec is auto=...; their namespace is
        # under Paqueteria.IntegrationTests, so the evidence names the IntegrationTests project.
        results = self.root / "TestResults" / "dotnet"
        results.mkdir(parents=True)
        instruments = (
            TRX_TEMPLATE.replace('<TestCategory><TestCategoryItem TestCategory="PlatformEndToEnd" /></TestCategory>', "")
            .replace("MainJourneyEndToEndTests", "PlatformInstrumentTests")
            .replace("RateLimitEndToEndTests", "PlatformInstrumentTests")
            .replace(
                "Main_journey_runs_through_the_real_screens_within_the_platform_budgets",
                "The_outbound_request_recorder_sees_a_real_request_of_this_process",
            )
            .replace(
                "A_limited_quote_answers_a_problem_with_Retry_After_and_the_wizard_shows_the_approved_text",
                "The_api_log_reader_reads_method_path_and_content_type_of_real_hosting_lines",
            )
        )
        (results / "dotnet_suite.trx").write_text(instruments, encoding="utf-8")
        args = argparse.Namespace(
            job="dotnet",
            artifact_name="rel000-execution-dotnet",
            workflow_run_id="1",
            workflow_run_attempt="1",
            source_head_sha="a" * 40,
            tested_git_sha="b" * 40,
            base_main_sha="c" * 40,
            trx=[f"auto={results / 'dotnet*.trx'}"],
            junit=None,
            structured=None,
            output=results / "rel000-execution-results.json",
        )
        manifest = self.rel000.collect_execution_results(args)
        self.assertEqual(
            [
                ("Paqueteria.IntegrationTests.PlatformEndToEnd.PlatformInstrumentTests."
                 "The_outbound_request_recorder_sees_a_real_request_of_this_process"),
                ("Paqueteria.IntegrationTests.PlatformEndToEnd.PlatformInstrumentTests."
                 "The_api_log_reader_reads_method_path_and_content_type_of_real_hosting_lines"),
            ],
            [test["fully_qualified_test_name"] for test in manifest["tests"]],
        )
        self.assertEqual({PROJECT}, {test["test_project"] for test in manifest["tests"]})
        self.assertEqual({"Uncategorized"}, {test["category"] for test in manifest["tests"]})


if __name__ == "__main__":
    unittest.main()
