"""tools/ci/web_image_dependencies.py: a web image ships the production dependencies of apps/web and nothing else."""

from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

import yaml

sys.path.insert(0, str(Path(__file__).resolve().parent))

import web_image_dependencies as tool  # noqa: E402

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
WEB = REPOSITORY_ROOT / "apps/web"

# a needs b and @s/x, and declares t as an optional peer, which the lockfile resolves to the devDependency t; d is a
# devDependency that brings e.
MANIFEST = {"dependencies": {"a": "1.0.0"}, "devDependencies": {"d": "2.0.0", "t": "3.0.0"}}
LOCKFILE = {
    "lockfileVersion": "9.0",
    "importers": {".": {
        "dependencies": {"a": {"specifier": "1.0.0", "version": "1.0.0(t@3.0.0)"}},
        "devDependencies": {"d": {"specifier": "2.0.0", "version": "2.0.0"}, "t": {"specifier": "3.0.0", "version": "3.0.0"}},
    }},
    "snapshots": {
        "a@1.0.0(t@3.0.0)": {"dependencies": {"b": "1.0.0"}, "optionalDependencies": {"t": "3.0.0"}},
        "b@1.0.0": {"dependencies": {"@s/x": "1.0.0", "y-alias": "y@4.0.0"}},
        "@s/x@1.0.0": {},
        "y@4.0.0": {},
        "t@3.0.0": {},
        "d@2.0.0": {"dependencies": {"e": "1.0.0"}},
        "e@1.0.0": {},
    },
}
PRODUCTION_STORE = ["@s+x@1.0.0", "a@1.0.0_t@3.0.0", "b@1.0.0", "lock.yaml", "node_modules", "t@3.0.0", "y@4.0.0"]


def real_manifest() -> dict:
    return json.loads((WEB / "package.json").read_text(encoding="utf-8"))


def real_lockfile() -> dict:
    return yaml.safe_load((WEB / "pnpm-lock.yaml").read_text(encoding="utf-8"))


def store_entry(key: str) -> str:
    name, version = tool.package_of(key)
    return f"{name.replace('/', '+')}@{version}"


class SyntheticGraphTests(unittest.TestCase):
    def test_the_production_store_passes_and_names_the_dev_dependency_it_links(self):
        result = tool.check(PRODUCTION_STORE, MANIFEST, LOCKFILE)
        self.assertEqual([], result.problems)
        self.assertEqual(5, result.directories)
        self.assertEqual(["t@3.0.0"], result.dev_dependencies)
        self.assertEqual(["t@3.0.0 (linked by a@1.0.0)"], result.linked)

    def test_a_package_only_a_dev_dependency_brings_fails(self):
        for entry, problem in (("d@2.0.0", "d@2.0.0: outside the production graph"),
                               ("e@1.0.0", "e@1.0.0: outside the production graph")):
            with self.subTest(entry=entry):
                result = tool.check(PRODUCTION_STORE + [entry], MANIFEST, LOCKFILE)
                self.assertEqual([problem], result.problems)
        self.assertEqual(["d@2.0.0", "t@3.0.0"], tool.check(PRODUCTION_STORE + ["d@2.0.0"], MANIFEST, LOCKFILE).dev_dependencies)

    def test_a_production_package_at_another_version_fails(self):
        result = tool.check(PRODUCTION_STORE + ["b@1.0.1"], MANIFEST, LOCKFILE)
        self.assertEqual(["b@1.0.1: outside the production graph"], result.problems)

    def test_a_missing_production_dependency_or_an_empty_store_fails(self):
        without = [entry for entry in PRODUCTION_STORE if not entry.startswith("a@")]
        self.assertEqual(["a@1.0.0: a production dependency missing from the image"],
                         tool.check(without, MANIFEST, LOCKFILE).problems)
        self.assertEqual(["a@1.0.0: a production dependency missing from the image"],
                         tool.check([], MANIFEST, LOCKFILE).problems)
        self.assertEqual(["z: a production dependency the lockfile does not lock"],
                         tool.check(PRODUCTION_STORE, {**MANIFEST, "dependencies": {"a": "1.0.0", "z": "1"}}, LOCKFILE).problems)

    def test_an_entry_that_is_not_a_package_directory_fails_closed(self):
        for entry in ("garbage", "A@1.0.0", ".hidden", "a", "@s+@1.0.0", "a@x.y"):
            with self.subTest(entry=entry):
                self.assertEqual([f"{entry}: not a pnpm package directory"],
                                 tool.check(PRODUCTION_STORE + [entry], MANIFEST, LOCKFILE).problems)

    def test_a_shortened_entry_with_peers_and_a_hash_is_read_by_its_name_and_version(self):
        entry = "b@1.0.0_" + "@long+peer@1.2.3_" * 4 + "0123456789abcdef0123456789abcdef"
        self.assertEqual([], tool.check(PRODUCTION_STORE + [entry], MANIFEST, LOCKFILE).problems)

    def test_main_prints_the_problems_and_one_annotation(self):
        with tempfile.TemporaryDirectory() as directory:
            web = Path(directory)
            (web / "package.json").write_text(json.dumps(MANIFEST), encoding="utf-8")
            (web / "pnpm-lock.yaml").write_text(yaml.safe_dump(LOCKFILE), encoding="utf-8")

            def run(listing: list[str]) -> tuple[int, str]:
                output = io.StringIO()
                stdin = sys.stdin
                sys.stdin = io.StringIO("\n".join(listing) + "\n")
                try:
                    with contextlib.redirect_stdout(output):
                        code = tool.main(["--name", "pilot-web", "--dockerfile", "deploy/x/Dockerfile.web",
                                          "--web-dir", str(web)])
                finally:
                    sys.stdin = stdin
                return code, output.getvalue()

            code, output = run(PRODUCTION_STORE)
            self.assertEqual(0, code, output)
            self.assertIn("pilot-web: 5 package directories, all in the production graph of ", output)
            self.assertIn("pilot-web: devDependencies that the production graph links: t@3.0.0 (linked by a@1.0.0)", output)
            self.assertNotIn("::error", output)
            code, output = run(PRODUCTION_STORE + ["d@2.0.0", "e@1.0.0"])
            self.assertEqual(1, code)
            lines = output.splitlines()
            self.assertEqual(["pilot-web: d@2.0.0: outside the production graph",
                              "pilot-web: e@1.0.0: outside the production graph"], lines[:2])
            self.assertEqual(1, sum(line.startswith("::error ") for line in lines))
            self.assertTrue(lines[-1].startswith("::error file=deploy/x/Dockerfile.web::pilot-web must ship the production "
                                                 "dependencies of "), lines[-1])
            self.assertTrue(lines[-1].endswith("2 problem(s), listed above (devDependencies in the image: d@2.0.0, t@3.0.0)"),
                            lines[-1])


class RepositoryLockfileTests(unittest.TestCase):
    """The same rules against apps/web as it is."""

    @classmethod
    def setUpClass(cls):
        cls.manifest = real_manifest()
        cls.lockfile = real_lockfile()
        cls.production = tool.production_graph(cls.lockfile)
        importer = cls.lockfile["importers"]["."]
        everything = dict(cls.lockfile, importers={".": {"dependencies": {
            **importer.get("dependencies", {}), **importer.get("devDependencies", {})}}})
        cls.everything = tool.production_graph(everything)
        cls.store = sorted({store_entry(key) for key in cls.production}) + ["lock.yaml", "node_modules"]

    def test_the_whole_production_graph_passes(self):
        result = tool.check(self.store, self.manifest, self.lockfile)
        self.assertEqual([], result.problems)
        self.assertGreater(result.directories, len(self.manifest["dependencies"]))

    def test_every_package_that_only_dev_dependencies_bring_fails(self):
        production = {tool.package_of(key) for key in self.production}
        dev_only = sorted({tool.package_of(key) for key in self.everything} - production)
        self.assertGreater(len(dev_only), 100)
        for name, version in dev_only:
            with self.subTest(package=f"{name}@{version}"):
                entry = f"{name.replace('/', '+')}@{version}"
                self.assertEqual([f"{name}@{version}: outside the production graph"],
                                 tool.check(self.store + [entry], self.manifest, self.lockfile).problems)
        self.assertIn(("vite", "8.1.6"), dev_only)
        self.assertIn(("vitest", "4.1.12"), dev_only)

    def test_the_only_dev_dependency_the_production_graph_links_is_the_sharp_peer(self):
        # sharp 0.35.5 declares @types/node as an optional peer, and the lockfile resolves it to the devDependency, so a
        # production install links it (type declarations only, nothing next start loads). The README of ENV-001 and
        # branching-and-ci.md say so: update them if this changes.
        result = tool.check(self.store, self.manifest, self.lockfile)
        self.assertEqual(["@types/node@26.1.1 (linked by sharp@0.35.5)"], result.linked)
        importer = self.lockfile["importers"]["."]["devDependencies"]
        for name in self.manifest["devDependencies"]:
            with self.subTest(devDependency=name):
                version = tool.package_of(tool.dependency_key(name, str(importer[name]["version"])))[1]
                entry = f"{name.replace('/', '+')}@{version}"
                problems = tool.check([e for e in self.store if e != entry] + [entry], self.manifest, self.lockfile).problems
                self.assertEqual([] if name == "@types/node" else [f"{name}@{version}: outside the production graph"], problems)


if __name__ == "__main__":
    unittest.main()
