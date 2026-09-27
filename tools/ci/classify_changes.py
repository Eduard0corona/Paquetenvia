#!/usr/bin/env python3
"""Change-aware validation planner for future ``pull_request`` → ``development`` runs.

The classifier maps the paths changed by a tested merge ref to the validation jobs
whose outcome those paths can affect, using the impact model in
``tools/ci/change-domains.json``. It never applies to ``development → main`` or to
``push``/``main``, which always run the full Foundation topology.

Provenance follows the established tested-merge-ref model: the diff is taken between
``tested_git_sha^1`` (the tested base) and ``tested_git_sha`` only after proving that
``tested_git_sha^2`` is the declared source head. The pull_request event base SHA is
never consulted.

Fail-closed rules:

* an input, topology, git or configuration problem exits non-zero (no plan);
* an unmatched path, an empty diff, a dependency/CI self change or the ``full-ci``
  label yields ``FULL``;
* a NuGet ``packages.lock.json`` leaves the ``DEPS`` domain only when its content is
  proven to change nothing but the internal ``type: Project`` graph (see
  ``prove_nuget_project_graph_only``); anything unproven, unparseable or ambiguous
  stays ``DEPS``;
* ``FULL`` is never reduced by any later rule or label;
* a domain ``exclude`` removes exact literal paths from that one domain only (every
  other domain still evaluates them); only ``REL000_INPUT`` may exclude, and every
  excluded path must be covered by its own patterns (see ``validate_exclude``);
* a head repository different from the repository is rejected;
* ``MAIN_BACKSYNC`` (head ``main``, every job except REL-000) requires explicit
  certification evidence (``--certified-main-sha`` equal to the source head); an
  uncertified ``main`` head classifies ``FULL``.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any

PLAN_FORMAT = "pv-plan-v1"
CONFIG_FORMAT = "pv-change-domains-v1"
CLASSIFICATION_SELECTIVE = "SELECTIVE"
CLASSIFICATION_FULL = "FULL"
CLASSIFICATION_MAIN_BACKSYNC = "MAIN_BACKSYNC"
CLASSIFICATIONS = (CLASSIFICATION_SELECTIVE, CLASSIFICATION_FULL, CLASSIFICATION_MAIN_BACKSYNC)
FULL_CI_LABEL = "full-ci"
MAIN_BRANCH = "main"
DEFAULT_CONFIG_PATH = Path(__file__).resolve().parent / "change-domains.json"
SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")
JOB_ID_PATTERN = re.compile(r"^[a-z0-9][a-z0-9-]*$")
DOMAIN_NAME_PATTERN = re.compile(r"^[A-Z][A-Z0-9_]*$")
CONTENT_PROOF_NUGET_PROJECT_GRAPH = "nuget-project-graph-only"
CONTENT_PROOFS = (CONTENT_PROOF_NUGET_PROJECT_GRAPH,)
# Only REL-000's input domain may narrow itself by exact path; no control domain
# (SECURITY_CONTROL, CI_SELF, DEPS, ...) can ever exclude anything.
EXCLUDE_ALLOWED_DOMAINS = frozenset({"REL000_INPUT"})
EXCLUDE_FORBIDDEN_CHARACTERS = frozenset("*?[]{}")
NUGET_LOCK_NAME = "packages.lock.json"
NUGET_LOCK_VERSIONS = (1, 2)
NUGET_PROJECT_NODE_TYPE = "Project"
NUGET_PACKAGE_NODE_TYPES = ("Direct", "Transitive", "CentralTransitive")
NUGET_PROJECT_NODE_KEYS = frozenset({"type", "dependencies"})
NUGET_PACKAGE_NODE_KEYS = frozenset({"type", "requested", "resolved", "contentHash", "dependencies"})


class ClassifyError(Exception):
    """A fail-closed classification error with a machine-readable code."""

    def __init__(self, code: str, message: str, **details: Any) -> None:
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message
        self.details = details

    def to_dict(self) -> dict[str, Any]:
        return {"error": self.code, "message": self.message, "details": self.details}


def fail(code: str, message: str, **details: Any) -> None:
    raise ClassifyError(code, message, **details)


# --------------------------------------------------------------------------- patterns


def compile_pattern(pattern: str) -> re.Pattern[str]:
    """Translate a path glob into a regex.

    ``**`` matches any number of path segments (including none), ``*`` matches within
    a single segment, everything else is literal. Paths are POSIX-separated.
    """
    if not pattern or pattern.startswith("/") or "\\" in pattern:
        fail("CONFIG_PATTERN_INVALID", "Patterns must be non-empty relative POSIX globs.", pattern=pattern)
    regex = ""
    index = 0
    while index < len(pattern):
        char = pattern[index]
        if pattern.startswith("**/", index):
            regex += "(?:.*/)?"
            index += 3
        elif pattern.startswith("**", index):
            regex += ".*"
            index += 2
        elif char == "*":
            regex += "[^/]*"
            index += 1
        else:
            regex += re.escape(char)
            index += 1
    return re.compile("^" + regex + "$")


# --------------------------------------------------------------------------- configuration


def _require_string_list(value: Any, code: str, what: str) -> list[str]:
    if not isinstance(value, list) or not value or any(
        not isinstance(item, str) or not item.strip() for item in value
    ):
        fail(code, f"{what} must be a non-empty array of non-empty strings.")
    if len(set(value)) != len(value):
        fail(code, f"{what} must not repeat entries.", entries=value)
    return list(value)


def validate_exclude(name: str, value: Any, compiled: list[re.Pattern[str]]) -> list[str]:
    """Validate a domain ``exclude`` list: exact literal paths the domain already covers.

    Only ``EXCLUDE_ALLOWED_DOMAINS`` may exclude; wildcards, non-normalized paths and
    paths outside the domain's own patterns are configuration errors, never ignored.
    """
    if name not in EXCLUDE_ALLOWED_DOMAINS:
        fail("CONFIG_EXCLUDE_FORBIDDEN", "This domain may not exclude paths.", name=name)
    exclude = _require_string_list(value, "CONFIG_EXCLUDE_INVALID", f"domains.{name}.exclude")
    for path in exclude:
        if (
            path != path.strip()
            or path.startswith("/")
            or "\\" in path
            or set(path) & EXCLUDE_FORBIDDEN_CHARACTERS
            or any(segment in ("", ".", "..") for segment in path.split("/"))
        ):
            fail("CONFIG_EXCLUDE_INVALID", "Excluded paths must be literal normalized POSIX paths.", name=name, path=path)
        if not any(pattern.match(path) for pattern in compiled):
            fail("CONFIG_EXCLUDE_INVALID", "Excluded paths must be covered by the domain's own patterns.", name=name, path=path)
    return exclude


def load_config(path: Path) -> dict[str, Any]:
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        fail("CONFIG_UNREADABLE", "The change-domain configuration cannot be read.", path=str(path), cause=str(error))
    return validate_config(raw)


def validate_config(raw: Any) -> dict[str, Any]:
    if not isinstance(raw, dict):
        fail("CONFIG_INVALID", "The change-domain configuration must be a JSON object.")
    if raw.get("format") != CONFIG_FORMAT:
        fail("CONFIG_FORMAT_INVALID", "Unknown change-domain configuration format.", format=raw.get("format"))
    for key in ("jobs", "universal_jobs", "full_only_jobs", "job_sets", "domains"):
        if key not in raw:
            fail("CONFIG_KEY_MISSING", f"Configuration key '{key}' is required.", key=key)

    jobs = _require_string_list(raw["jobs"], "CONFIG_JOBS_INVALID", "jobs")
    for job in jobs:
        if not JOB_ID_PATTERN.match(job):
            fail("CONFIG_JOBS_INVALID", "Job ids must be lowercase kebab-case.", job=job)
    job_index = {job: position for position, job in enumerate(jobs)}
    known = set(jobs)

    universal = _require_string_list(raw["universal_jobs"], "CONFIG_UNIVERSAL_INVALID", "universal_jobs")
    full_only = _require_string_list(raw["full_only_jobs"], "CONFIG_FULL_ONLY_INVALID", "full_only_jobs")
    for name, values in (("universal_jobs", universal), ("full_only_jobs", full_only)):
        unknown = sorted(set(values) - known)
        if unknown:
            fail("CONFIG_UNKNOWN_JOB", f"{name} references unknown job ids.", jobs=unknown)
    if set(universal) & set(full_only):
        fail("CONFIG_INVALID", "A job cannot be both universal and full-only.")

    job_sets_raw = raw["job_sets"]
    if not isinstance(job_sets_raw, dict):
        fail("CONFIG_JOB_SETS_INVALID", "job_sets must be an object.")
    job_sets: dict[str, list[str]] = {}
    for set_name, members in job_sets_raw.items():
        if not DOMAIN_NAME_PATTERN.match(str(set_name)):
            fail("CONFIG_JOB_SETS_INVALID", "job_sets keys must be upper snake case.", name=set_name)
        values = _require_string_list(members, "CONFIG_JOB_SETS_INVALID", f"job_sets.{set_name}")
        unknown = sorted(set(values) - known)
        if unknown:
            fail("CONFIG_UNKNOWN_JOB", f"job_sets.{set_name} references unknown job ids.", jobs=unknown)
        if set(values) & set(full_only):
            fail("CONFIG_JOB_SETS_INVALID", "Job sets cannot contain full-only jobs.", name=set_name)
        job_sets[str(set_name)] = values

    domains_raw = raw["domains"]
    if not isinstance(domains_raw, list) or not domains_raw:
        fail("CONFIG_DOMAINS_INVALID", "domains must be a non-empty array.")
    domains: list[dict[str, Any]] = []
    seen_names: set[str] = set()
    for entry in domains_raw:
        if not isinstance(entry, dict):
            fail("CONFIG_DOMAINS_INVALID", "Each domain must be an object.")
        name = entry.get("name")
        if not isinstance(name, str) or not DOMAIN_NAME_PATTERN.match(name):
            fail("CONFIG_DOMAINS_INVALID", "Domain names must be upper snake case.", name=name)
        if name in seen_names:
            fail("CONFIG_DOMAINS_INVALID", "Domain names must be unique.", name=name)
        seen_names.add(name)
        patterns = _require_string_list(entry.get("patterns"), "CONFIG_PATTERN_INVALID", f"domains.{name}.patterns")
        compiled = [compile_pattern(pattern) for pattern in patterns]
        full = entry.get("full", False)
        if not isinstance(full, bool):
            fail("CONFIG_DOMAINS_INVALID", "domain.full must be a boolean.", name=name)
        allowed_keys = {"name", "patterns", "exclude", "full", "jobs", "job_sets", "content_proof", "supersedes"}
        extra = sorted(set(entry) - allowed_keys)
        if extra:
            fail("CONFIG_DOMAINS_INVALID", "Domain carries unknown keys.", name=name, keys=extra)
        exclude = validate_exclude(name, entry["exclude"], compiled) if "exclude" in entry else []
        domain_jobs: list[str] = []
        if full:
            if "jobs" in entry or "job_sets" in entry:
                fail("CONFIG_DOMAINS_INVALID", "A full domain must not list jobs.", name=name)
        else:
            if "jobs" not in entry and "job_sets" not in entry:
                fail("CONFIG_DOMAINS_INVALID", "A selective domain must declare jobs and/or job_sets.", name=name)
            listed = entry.get("jobs", [])
            if not isinstance(listed, list) or any(not isinstance(job, str) for job in listed):
                fail("CONFIG_DOMAINS_INVALID", "domain.jobs must be an array of job ids.", name=name)
            for set_name in entry.get("job_sets", []):
                if set_name not in job_sets:
                    fail("CONFIG_DOMAINS_INVALID", "domain.job_sets references an unknown job set.", name=name, set=set_name)
                listed = listed + job_sets[set_name]
            unknown = sorted(set(listed) - known)
            if unknown:
                fail("CONFIG_UNKNOWN_JOB", f"domains.{name} references unknown job ids.", jobs=unknown)
            if set(listed) & set(full_only):
                fail("CONFIG_DOMAINS_INVALID", "Selective domains cannot require full-only jobs.", name=name)
            domain_jobs = sorted(set(listed), key=job_index.__getitem__)
        content_proof = entry.get("content_proof")
        supersedes: list[str] = []
        if content_proof is not None:
            if content_proof not in CONTENT_PROOFS:
                fail("CONFIG_DOMAINS_INVALID", "domain.content_proof names an unknown proof.", name=name, proof=content_proof)
            if full:
                fail("CONFIG_DOMAINS_INVALID", "A content-proven domain must be selective.", name=name)
            supersedes = _require_string_list(entry.get("supersedes"), "CONFIG_DOMAINS_INVALID", f"domains.{name}.supersedes")
        elif "supersedes" in entry:
            fail("CONFIG_DOMAINS_INVALID", "Only a content-proven domain may supersede other domains.", name=name)
        domains.append(
            {
                "name": name,
                "patterns": patterns,
                "compiled": compiled,
                "exclude": exclude,
                "full": full,
                "jobs": domain_jobs,
                "content_proof": content_proof,
                "supersedes": supersedes,
            }
        )
    for domain in domains:
        unknown = sorted(set(domain["supersedes"]) - seen_names)
        if unknown or domain["name"] in domain["supersedes"]:
            fail(
                "CONFIG_DOMAINS_INVALID",
                "domain.supersedes must name other existing domains.",
                name=domain["name"],
                supersedes=domain["supersedes"],
            )

    return {
        "jobs": jobs,
        "job_index": job_index,
        "universal_jobs": sorted(set(universal), key=job_index.__getitem__),
        "full_only_jobs": sorted(set(full_only), key=job_index.__getitem__),
        "job_sets": job_sets,
        "domains": domains,
    }


# --------------------------------------------------------------------------- git provenance


def validate_sha(value: Any, field: str) -> str:
    text = str(value or "").strip()
    if not SHA_PATTERN.match(text):
        fail("CLASSIFY_SHA_MALFORMED", f"{field} must be a lowercase 40-hex commit SHA.", field=field)
    return text


def run_git(repo_root: Path, *args: str) -> str:
    completed = subprocess.run(
        ["git", "-C", str(repo_root), *args],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if completed.returncode != 0:
        fail(
            "CLASSIFY_GIT_FAILED",
            "A git command required for classification failed.",
            command=["git", *args],
            stderr=completed.stderr.strip(),
        )
    return completed.stdout


def resolve_changed_paths(repo_root: Path, tested_git_sha: str, source_head_sha: str) -> list[str]:
    """Prove the tested merge topology and return the paths it changed versus its base."""
    return resolve_tested_diff(repo_root, tested_git_sha, source_head_sha)[2]


def resolve_tested_diff(repo_root: Path, tested_git_sha: str, source_head_sha: str) -> tuple[str, str, list[str]]:
    """Prove the tested merge topology and return ``(base, tested, changed_paths)``."""
    tested = validate_sha(tested_git_sha, "tested_git_sha")
    source = validate_sha(source_head_sha, "source_head_sha")
    if tested == source:
        fail("CLASSIFY_SOURCE_IS_TESTED", "A pull_request tests a merge commit, never the source head itself.")
    parents = run_git(repo_root, "rev-list", "--parents", "-n", "1", tested).split()
    if not parents or parents[0] != tested:
        fail("CLASSIFY_TESTED_SHA_UNRESOLVED", "tested_git_sha did not resolve to itself.", resolved=parents)
    parent_shas = parents[1:]
    if len(parent_shas) != 2:
        fail(
            "CLASSIFY_MERGE_TOPOLOGY_INVALID",
            "The tested commit must be a two-parent merge of base and source head.",
            parents=parent_shas,
        )
    base, merged_source = parent_shas
    if merged_source != source:
        fail(
            "CLASSIFY_SOURCE_HEAD_MISMATCH",
            "tested_git_sha^2 is not the declared source head.",
            expected=source,
            actual=merged_source,
        )
    output = run_git(repo_root, "diff", "--name-only", "--no-renames", "-z", base, tested)
    paths = sorted({item.replace("\\", "/") for item in output.split("\0") if item})
    return base, tested, paths


# --------------------------------------------------------------------------- content proofs


class _NotProven(Exception):
    """Internal: the content could not be proven safe; the path keeps its path-based domains."""


def _git_blob(repo_root: Path, sha: str, path: str) -> str | None:
    """Return the blob at ``sha:path``, or ``None`` when the path does not exist there."""
    listing = run_git(repo_root, "ls-tree", "-z", sha, "--", path)
    entries = [entry for entry in listing.split("\0") if entry]
    if not entries:
        return None
    if len(entries) != 1 or not entries[0].endswith("\t" + path) or " blob " not in entries[0].split("\t", 1)[0]:
        raise _NotProven()
    return run_git(repo_root, "cat-file", "blob", f"{sha}:{path}")


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    keys = [key for key, _ in pairs]
    if len(set(keys)) != len(keys):
        raise _NotProven()
    return dict(pairs)


def _string_map(value: Any) -> dict[str, str]:
    if not isinstance(value, dict) or any(
        not isinstance(key, str) or not key or not isinstance(item, str) or not item for key, item in value.items()
    ):
        raise _NotProven()
    return value


def _parse_nuget_lock(text: str) -> tuple[int, dict[str, dict[str, dict[str, Any]]]]:
    """Strictly parse a NuGet lockfile into ``(version, {framework: {"packages", "projects"}})``.

    Any shape this parser does not fully understand is not proven.
    """
    try:
        data = json.loads(text, object_pairs_hook=_reject_duplicate_keys)
    except ValueError as error:
        raise _NotProven() from error
    if not isinstance(data, dict) or set(data) != {"version", "dependencies"}:
        raise _NotProven()
    version = data["version"]
    if isinstance(version, bool) or version not in NUGET_LOCK_VERSIONS:
        raise _NotProven()
    frameworks = data["dependencies"]
    if not isinstance(frameworks, dict) or not frameworks:
        raise _NotProven()
    graphs: dict[str, dict[str, dict[str, Any]]] = {}
    for framework, nodes in frameworks.items():
        if not isinstance(framework, str) or not framework or not isinstance(nodes, dict):
            raise _NotProven()
        packages: dict[str, dict[str, Any]] = {}
        projects: dict[str, dict[str, Any]] = {}
        for name, node in nodes.items():
            if not isinstance(name, str) or not name or not isinstance(node, dict):
                raise _NotProven()
            node_type = node.get("type")
            if node_type == NUGET_PROJECT_NODE_TYPE:
                if not set(node) <= NUGET_PROJECT_NODE_KEYS:
                    raise _NotProven()
                _string_map(node.get("dependencies", {}))
                projects[name] = node
            elif node_type in NUGET_PACKAGE_NODE_TYPES:
                if not set(node) <= NUGET_PACKAGE_NODE_KEYS:
                    raise _NotProven()
                for field in ("resolved", "contentHash"):
                    if not isinstance(node.get(field), str) or not node[field]:
                        raise _NotProven()
                if "requested" in node and (not isinstance(node["requested"], str) or not node["requested"]):
                    raise _NotProven()
                _string_map(node.get("dependencies", {}))
                packages[name] = node
            else:
                raise _NotProven()
        folded_packages = {name.casefold() for name in packages}
        folded_projects = {name.casefold() for name in projects}
        if (
            len(folded_packages) != len(packages)
            or len(folded_projects) != len(projects)
            or folded_packages & folded_projects
        ):
            raise _NotProven()
        graphs[framework] = {"packages": packages, "projects": projects}
    return version, graphs


class _BaseNuGetCatalog:
    """Every external package node and package range already present in the base tree."""

    def __init__(self, repo_root: Path, base: str) -> None:
        self.versions: set[int] = set()
        self.frameworks: set[str] = set()
        self.nodes: dict[tuple[str, str], list[dict[str, Any]]] = {}
        self.ranges: set[tuple[str, str, str]] = set()
        listing = run_git(repo_root, "ls-tree", "-r", "-z", "--name-only", base)
        for path in sorted(item for item in listing.split("\0") if item):
            if path.rsplit("/", 1)[-1] != NUGET_LOCK_NAME:
                continue
            text = _git_blob(repo_root, base, path)
            if text is None:
                raise _NotProven()
            version, graphs = _parse_nuget_lock(text)
            self.versions.add(version)
            for framework, graph in graphs.items():
                self.frameworks.add(framework)
                for name, node in graph["packages"].items():
                    self.nodes.setdefault((framework, name), []).append(node)
                    if "requested" in node:
                        self.ranges.add((framework, name.casefold(), node["requested"]))
                package_names = {name.casefold() for name in graph["packages"]}
                for node in graph["projects"].values():
                    for dependency, requested in node.get("dependencies", {}).items():
                        if dependency.casefold() in package_names:
                            self.ranges.add((framework, dependency.casefold(), requested))


def _check_project_nodes(framework: str, graph: dict[str, dict[str, Any]], catalog: _BaseNuGetCatalog) -> None:
    """Project nodes may reference internal projects freely, and external packages only
    through a (package, range) pair that already exists in the base."""
    project_names = {name.casefold() for name in graph["projects"]}
    package_names = {name.casefold() for name in graph["packages"]}
    for node in graph["projects"].values():
        for dependency, requested in node.get("dependencies", {}).items():
            folded = dependency.casefold()
            if folded in project_names:
                continue
            if folded not in package_names or (framework, folded, requested) not in catalog.ranges:
                raise _NotProven()


def _prove_lockfile(repo_root: Path, base: str, tested: str, path: str, catalog_factory: Any) -> None:
    head_text = _git_blob(repo_root, tested, path)
    if head_text is None:
        # A removed lockfile removes external packages from the repository graph.
        raise _NotProven()
    head_version, head_graphs = _parse_nuget_lock(head_text)
    base_text = _git_blob(repo_root, base, path)
    catalog = catalog_factory()
    if base_text is not None:
        base_version, base_graphs = _parse_nuget_lock(base_text)
        if head_version != base_version or set(head_graphs) != set(base_graphs):
            raise _NotProven()
        for framework, graph in head_graphs.items():
            if graph["packages"] != base_graphs[framework]["packages"]:
                raise _NotProven()
    else:
        if head_version not in catalog.versions:
            raise _NotProven()
        for framework, graph in head_graphs.items():
            if framework not in catalog.frameworks:
                raise _NotProven()
            for name, node in graph["packages"].items():
                if node not in catalog.nodes.get((framework, name), []):
                    raise _NotProven()
    for framework, graph in head_graphs.items():
        _check_project_nodes(framework, graph, catalog)


def prove_nuget_project_graph_only(repo_root: Path, base: str, tested: str, changed_paths: list[str]) -> list[str]:
    """Return the changed NuGet lockfiles whose only change is the internal Project graph.

    A changed ``packages.lock.json`` is proven only when, between ``base`` and ``tested``:

    * it still exists and parses strictly (known lock version, known node types and keys,
      no duplicate or case-colliding names);
    * an existing lockfile keeps its lock version, its target frameworks and every
      external (non-``Project``) node exactly: no package added, removed, re-versioned,
      re-hashed, re-ranged or re-typed;
    * a new lockfile (a new project) contains only external nodes that already exist,
      identically, for the same framework in some base lockfile;
    * every ``Project`` node reaches external packages only through a (package, range)
      pair already present in the base.

    Everything else — including any git or parse problem — is simply not proven, so the
    path keeps its ``DEPS`` classification (fail closed).
    """
    lockfiles = sorted(path for path in set(changed_paths) if path.rsplit("/", 1)[-1] == NUGET_LOCK_NAME)
    if not lockfiles:
        return []
    cache: dict[str, _BaseNuGetCatalog] = {}

    def catalog_factory() -> _BaseNuGetCatalog:
        if "base" not in cache:
            cache["base"] = _BaseNuGetCatalog(repo_root, base)
        return cache["base"]

    proven: list[str] = []
    for path in lockfiles:
        try:
            _prove_lockfile(repo_root, base, tested, path, catalog_factory)
        except (_NotProven, ClassifyError):
            continue
        proven.append(path)
    return proven


def resolve_content_proofs(repo_root: Path, base: str, tested: str, changed_paths: list[str]) -> dict[str, list[str]]:
    return {CONTENT_PROOF_NUGET_PROJECT_GRAPH: prove_nuget_project_graph_only(repo_root, base, tested, changed_paths)}


# --------------------------------------------------------------------------- classification


def match_domains(config: dict[str, Any], path: str, content_proofs: dict[str, Any] | None = None) -> list[str]:
    """Return the domains of ``path``.

    A path a domain explicitly excludes does not match that domain; every other domain
    still evaluates it. A content-proven domain applies only to paths its proof
    accepted, and then removes the domains it supersedes for that path; without a proof
    the path-based domains stand unchanged.
    """
    proofs = content_proofs or {}
    matched: list[str] = []
    superseded: set[str] = set()
    for domain in config["domains"]:
        if path in domain["exclude"] or not any(pattern.match(path) for pattern in domain["compiled"]):
            continue
        if domain["content_proof"] is not None:
            if path not in set(proofs.get(domain["content_proof"], ())):
                continue
            superseded.update(domain["supersedes"])
        matched.append(domain["name"])
    return [name for name in matched if name not in superseded]


def classify_paths(
    config: dict[str, Any],
    changed_paths: list[str],
    *,
    head_ref: str,
    head_repo: str,
    repository: str,
    labels: list[str] | None = None,
    source_head_sha: str | None = None,
    certified_main_sha: str | None = None,
    content_proofs: dict[str, list[str]] | None = None,
) -> dict[str, Any]:
    """Pure classification of an already-proven changed-path list.

    ``certified_main_sha`` is the head SHA of a successful ``push``/``main`` Foundation
    run (13/13), obtained by the calling workflow and passed in as trusted evidence.
    ``MAIN_BACKSYNC`` is emitted only when the head is ``main`` and that evidence equals
    ``source_head_sha`` exactly; branch identity alone never certifies anything.

    ``content_proofs`` maps a proof kind to the changed paths whose content that proof
    accepted (see ``resolve_content_proofs``); it can only remove a superseded domain
    from a proven path, never add jobs beyond the proven domain's own.
    """
    labels = sorted({str(label).strip() for label in (labels or []) if str(label).strip()})
    head_ref = str(head_ref or "").strip()
    head_repo = str(head_repo or "").strip()
    repository = str(repository or "").strip()
    if not head_ref:
        fail("CLASSIFY_HEAD_REF_MISSING", "head_ref is required.")
    if not repository or not head_repo:
        fail("CLASSIFY_REPOSITORY_MISSING", "repository and head_repo are required.")
    if head_repo != repository:
        fail("CLASSIFY_FORK_HEAD_FORBIDDEN", "The head repository must be the repository itself.", head_repo=head_repo, repository=repository)
    source = validate_sha(source_head_sha, "source_head_sha") if str(source_head_sha or "").strip() else None
    certified = (
        validate_sha(certified_main_sha, "certified_main_sha") if str(certified_main_sha or "").strip() else None
    )
    for path in changed_paths:
        if not isinstance(path, str) or not path or path.startswith("/") or "\\" in path or ".." in path.split("/"):
            fail("CLASSIFY_PATH_INVALID", "Changed paths must be relative POSIX paths.", path=path)

    job_index = config["job_index"]
    reasons: set[str] = set()
    domains: set[str] = set()
    required: set[str] = set(config["universal_jobs"])
    unmatched: list[str] = []
    full = False

    for path in sorted(set(changed_paths)):
        matched = match_domains(config, path, content_proofs)
        if not matched:
            unmatched.append(path)
            continue
        for name in matched:
            domains.add(name)
            domain = next(item for item in config["domains"] if item["name"] == name)
            if domain["full"]:
                full = True
                reasons.add(f"FULL:{name}")
            else:
                required.update(domain["jobs"])

    if unmatched:
        full = True
        reasons.add("FULL:UNMATCHED_PATHS")
    if not changed_paths:
        full = True
        reasons.add("FULL:EMPTY_DIFF")
    label_full = FULL_CI_LABEL in labels

    # A head of `main` is a back-sync of main content. Only push/main-certified content
    # may skip the full-only REL-000 job (REL-000 NORMAL would reject already-certified
    # dependency drift against the older development base), so the back-sync path
    # requires explicit certification evidence equal to the source head. An
    # uncertified main never bypasses REL-000.
    backsync_certified = False
    if head_ref == MAIN_BRANCH:
        if source is not None and certified is not None and certified == source:
            backsync_certified = True
        else:
            full = True
            reasons.add("FULL:MAIN_BACKSYNC_UNCERTIFIED")

    # Precedence: the full-ci label always expands to FULL; otherwise a certified
    # back-sync runs every development validation except the full-only jobs regardless
    # of the paths it carries; otherwise path-derived FULL wins over SELECTIVE.
    if label_full:
        classification = CLASSIFICATION_FULL
        reasons.add(f"FULL:LABEL_{FULL_CI_LABEL}")
        required = set(config["jobs"])
    elif backsync_certified:
        classification = CLASSIFICATION_MAIN_BACKSYNC
        reasons = {reason for reason in reasons if not reason.startswith("FULL:")}
        reasons.add("MAIN_BACKSYNC:CERTIFIED_MAIN_HEAD")
        required = set(config["jobs"]) - set(config["full_only_jobs"])
    elif full:
        classification = CLASSIFICATION_FULL
        required = set(config["jobs"])
    else:
        classification = CLASSIFICATION_SELECTIVE
        for name in sorted(domains):
            reasons.add(f"DOMAIN:{name}")

    return {
        "format": PLAN_FORMAT,
        "classification": classification,
        "reasons": sorted(reasons),
        "domains": sorted(domains),
        "required_jobs": sorted(required, key=job_index.__getitem__),
        "unmatched_paths": sorted(unmatched),
    }


def serialize_plan(plan: dict[str, Any]) -> str:
    return json.dumps(plan, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def validate_plan(plan: Any, config: dict[str, Any] | None = None) -> dict[str, Any]:
    """Structural validation of a plan (used by the gate)."""
    if not isinstance(plan, dict):
        fail("PLAN_INVALID", "The plan must be a JSON object.")
    if plan.get("format") != PLAN_FORMAT:
        fail("PLAN_FORMAT_INVALID", "Unknown plan format.", format=plan.get("format"))
    expected_keys = {"format", "classification", "reasons", "domains", "required_jobs", "unmatched_paths"}
    if set(plan) != expected_keys:
        fail("PLAN_INVALID", "The plan carries unexpected or missing keys.", keys=sorted(plan))
    if plan["classification"] not in CLASSIFICATIONS:
        fail("PLAN_INVALID", "Unknown classification.", classification=plan["classification"])
    for key in ("reasons", "domains", "required_jobs", "unmatched_paths"):
        value = plan[key]
        if not isinstance(value, list) or any(not isinstance(item, str) for item in value):
            fail("PLAN_INVALID", f"plan.{key} must be an array of strings.", key=key)
        if len(set(value)) != len(value):
            fail("PLAN_INVALID", f"plan.{key} must not repeat entries.", key=key)
    if config is not None:
        unknown = sorted(set(plan["required_jobs"]) - set(config["jobs"]))
        if unknown:
            fail("PLAN_INVALID", "The plan requires unknown jobs.", jobs=unknown)
        if plan["required_jobs"] != sorted(plan["required_jobs"], key=config["job_index"].__getitem__):
            fail("PLAN_INVALID", "plan.required_jobs is not in canonical order.")
        missing_universal = sorted(set(config["universal_jobs"]) - set(plan["required_jobs"]))
        if missing_universal:
            fail("PLAN_INVALID", "The plan omits universal jobs.", jobs=missing_universal)
        if plan["classification"] == CLASSIFICATION_FULL and set(plan["required_jobs"]) != set(config["jobs"]):
            fail("PLAN_INVALID", "A FULL plan must require every job.")
        if plan["classification"] == CLASSIFICATION_MAIN_BACKSYNC and set(plan["required_jobs"]) != (
            set(config["jobs"]) - set(config["full_only_jobs"])
        ):
            fail("PLAN_INVALID", "A MAIN_BACKSYNC plan must require every job except full-only jobs.")
        if plan["classification"] == CLASSIFICATION_SELECTIVE and set(plan["required_jobs"]) & set(config["full_only_jobs"]):
            fail("PLAN_INVALID", "A SELECTIVE plan cannot require full-only jobs.")
    return plan


# --------------------------------------------------------------------------- CLI


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--repo-root", default=".")
    parser.add_argument("--config", default=str(DEFAULT_CONFIG_PATH))
    parser.add_argument("--tested-git-sha", required=True, help="github.sha of the pull_request run (tested merge ref)")
    parser.add_argument("--source-head-sha", required=True, help="github.event.pull_request.head.sha")
    parser.add_argument("--head-ref", required=True, help="github.head_ref")
    parser.add_argument("--head-repo", required=True, help="github.event.pull_request.head.repo.full_name")
    parser.add_argument("--repository", required=True, help="github.repository")
    parser.add_argument("--label", action="append", default=[], help="pull request label (repeatable)")
    parser.add_argument(
        "--certified-main-sha",
        default=None,
        help="head SHA of a successful push/main Foundation run (13/13); required for MAIN_BACKSYNC",
    )
    parser.add_argument("--output", default=None, help="write the plan JSON to this file")
    parser.add_argument("--github-output", default=None, help="append plan=<json> to this $GITHUB_OUTPUT file")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        config = load_config(Path(args.config))
        repo_root = Path(args.repo_root).resolve()
        base, tested, changed = resolve_tested_diff(repo_root, args.tested_git_sha, args.source_head_sha)
        proofs = resolve_content_proofs(repo_root, base, tested, changed)
        plan = classify_paths(
            config,
            changed,
            head_ref=args.head_ref,
            head_repo=args.head_repo,
            repository=args.repository,
            labels=args.label,
            source_head_sha=args.source_head_sha,
            certified_main_sha=args.certified_main_sha,
            content_proofs=proofs,
        )
        validate_plan(plan, config)
    except ClassifyError as error:
        print(json.dumps(error.to_dict(), sort_keys=True), file=sys.stderr)
        return 1
    serialized = serialize_plan(plan)
    print(serialized)
    if args.output:
        Path(args.output).write_text(serialized + "\n", encoding="utf-8")
    if args.github_output:
        with open(args.github_output, "a", encoding="utf-8") as handle:
            handle.write(f"plan={serialized}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
