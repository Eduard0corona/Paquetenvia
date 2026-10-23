"""Checks that a web image ships only the production dependencies of apps/web.

The deploy image smoke (``Build and smoke-test the deploy images (no push)`` in ci.yml and pr-validation.yml) pipes the
listing of the image's pnpm virtual store (``ls -1A /app/node_modules/.pnpm``) into this script. pnpm keeps the files of
every installed package there, linked or hoisted, so the listing names every package the image ships. The check fails
unless:

* every package in the store belongs to the production graph of ``apps/web/pnpm-lock.yaml``: the importer's
  ``dependencies`` and everything their lockfile snapshots link (dependencies, optional dependencies and resolved
  peers), at the locked version. A package that only a devDependency brings (vite, vitest, eslint, typescript, ...)
  is outside it;
* every ``dependencies`` entry of ``apps/web/package.json`` is in the store at its locked version, so an empty or
  unrelated listing never passes;
* every entry is a package directory, ``node_modules`` (pnpm's hoisting directory) or ``lock.yaml``.

A devDependency of ``apps/web/package.json`` is therefore in the image only when the production graph links it itself
(today ``@types/node``, which ``sharp`` declares as an optional peer). Each one is printed with the production packages
that link it.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

import yaml

# A store directory is the package's dependency path with "/" written as "+": name@version, then "_" and the resolved
# peers, possibly cut short and ended with a hash. Neither a package name nor a version contains "_" or "+".
STORE_ENTRY = re.compile(r"^(?P<name>(?:@[a-z0-9][a-z0-9._~-]*\+)?[a-z0-9][a-z0-9._~-]*)@(?P<version>[0-9][^_+]*)(?:_.*)?$")
STORE_FILES = {"lock.yaml", "node_modules"}
SNAPSHOT_KEY = re.compile(r"^(?P<name>@?[^@]+)@(?P<version>[^(]+)")


def package_of(key: str) -> tuple[str, str]:
    """``name@version(peer@version)...`` -> (name, version)."""
    match = SNAPSHOT_KEY.match(key)
    if match is None:
        raise ValueError(f"unreadable lockfile key {key!r}")
    return match["name"], match["version"]


def dependency_key(name: str, reference: str) -> str:
    """A snapshot's reference to a dependency -> that dependency's snapshot key (``npm:`` aliases name the package)."""
    if SNAPSHOT_KEY.match(reference.split("(", 1)[0]):
        return reference
    return f"{name}@{reference}"


def production_graph(lockfile: dict) -> dict[str, set[str]]:
    """Every snapshot key the importer's production dependencies reach, with the keys that link it."""
    importer = lockfile["importers"]["."]
    snapshots = lockfile.get("snapshots") or {}
    linked_by: dict[str, set[str]] = {}
    pending: list[tuple[str, str]] = []
    for section in ("dependencies", "optionalDependencies"):
        for name, spec in (importer.get(section) or {}).items():
            pending.append((dependency_key(name, str(spec["version"])), "package.json"))
    while pending:
        key, parent = pending.pop()
        seen = key in linked_by
        linked_by.setdefault(key, set()).add(parent)
        if seen:
            continue
        snapshot = snapshots.get(key) or {}
        for section in ("dependencies", "optionalDependencies"):
            for name, reference in (snapshot.get(section) or {}).items():
                pending.append((dependency_key(name, str(reference)), key))
    return linked_by


@dataclass
class Result:
    directories: int = 0
    problems: list[str] = field(default_factory=list)
    dev_dependencies: list[str] = field(default_factory=list)  # devDependencies in the store, name@version
    linked: list[str] = field(default_factory=list)  # the same, each with the production packages that link it


def check(store_listing: list[str], manifest: dict, lockfile: dict) -> Result:
    graph = production_graph(lockfile)
    links: dict[tuple[str, str], set[str]] = {}
    for key, parents in graph.items():
        links.setdefault(package_of(key), set()).update(
            "{}@{}".format(*package_of(parent)) if parent in graph else parent for parent in parents)
    dev_names = set(manifest.get("devDependencies") or {})
    result = Result()
    store: set[tuple[str, str]] = set()
    for entry in (line.strip() for line in store_listing):
        if not entry or entry in STORE_FILES:
            continue
        result.directories += 1
        match = STORE_ENTRY.match(entry)
        if match is None:
            result.problems.append(f"{entry}: not a pnpm package directory")
            continue
        package = (match["name"].replace("+", "/", 1), match["version"])
        store.add(package)
        if package not in links:
            result.problems.append(f"{package[0]}@{package[1]}: outside the production graph")
    for name, version in sorted(store):
        if name in dev_names:
            result.dev_dependencies.append(f"{name}@{version}")
            if (name, version) in links:
                result.linked.append(f"{name}@{version} (linked by {', '.join(sorted(links[(name, version)]))})")
    locked = lockfile["importers"]["."].get("dependencies") or {}
    for name in sorted(manifest.get("dependencies") or {}):
        if name not in locked:
            result.problems.append(f"{name}: a production dependency the lockfile does not lock")
            continue
        version = package_of(dependency_key(name, str(locked[name]["version"])))[1]
        if (name, version) not in store:
            result.problems.append(f"{name}@{version}: a production dependency missing from the image")
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n", 1)[0])
    parser.add_argument("--name", required=True, help="image name in the smoke step, for the messages")
    parser.add_argument("--dockerfile", required=True, help="Dockerfile the image was built from, for the annotation")
    parser.add_argument("--web-dir", type=Path, default=Path("apps/web"))
    args = parser.parse_args(argv)
    web = args.web_dir.as_posix()
    manifest = json.loads((args.web_dir / "package.json").read_text(encoding="utf-8"))
    lockfile = yaml.safe_load((args.web_dir / "pnpm-lock.yaml").read_text(encoding="utf-8"))
    result = check(sys.stdin.read().splitlines(), manifest, lockfile)
    if result.problems:
        for problem in result.problems:
            print(f"{args.name}: {problem}")
        print(f"::error file={args.dockerfile}::{args.name} must ship the production dependencies of {web} and nothing "
              f"else: {len(result.problems)} problem(s), listed above (devDependencies in the image: "
              f"{', '.join(result.dev_dependencies) or 'none'})")
        return 1
    print(f"{args.name}: {result.directories} package directories, all in the production graph of {web}/pnpm-lock.yaml")
    print(f"{args.name}: devDependencies that the production graph links: {'; '.join(result.linked) or 'none'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
