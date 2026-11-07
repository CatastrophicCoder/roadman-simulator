#!/usr/bin/env python3
"""Choose the gdUnit4 test groups that changed files affect (quick verify mode).

Spec: docs/specs/test-performance.md, Behaviour 5 to 8 and 10, criteria 7 and 8.
Standard library only; run with ``python3 -I``.

Commands (run from anywhere; ``--repo`` defaults to the repository holding
this script, ``--groups`` to ``<repo>/game/tests/test-groups.json``):

    test_select.py check  [--repo <root>] [--groups <file>]
    test_select.py select [--dry-run] [--format text|shell] [--repo <root>]
                          [--groups <file>] [<base>]

``check`` validates the group map (criterion 7): exit 0 and ``OK ...`` when it
is complete, else exit 1 with one ``ERROR: ...`` line per problem.

``select`` always exits 0 (problems become MODE=all with a reason). With
``--format shell`` it prints only ``KEY=value`` lines, one per line, in this
order. The value is the rest of the line after the first ``=`` (parse with
``IFS='=' read -r key value``; nothing needs eval). Newlines in paths are
printed as ``?``.

    MODE=all|groups|none
    GROUPS=<selected groups, space-separated, in file order>  (empty unless MODE=groups)
    SUITES=<suite classes to run, space-separated, sorted>  (every suite for all, empty for none)
    FILTER=<dotnet test --filter expression>                (empty for all and none)
    PYTEST=0|1   (1: a changed path matches pytest_paths, or a fail-safe: git state,
                  invalid groups file, a path matching no rule)
    PYTEST_REASON=<first path matching pytest_paths, or the fail-safe reason>  (empty for 0)
    ALL_REASON=<why the full suite runs>                     (only when MODE=all)
    REASON_<n>=<group>: <first changed path that selected it>   (n = 1.., only when MODE=groups)
    DETAIL_<n>=<path> -> <rule> -> <targets>[ (pytest)]    (n = 1.., only with --dry-run)
    NOTE=<the git-ignored files note>

FILTER is ``FullyQualifiedName~Roadman.Tests.<Suite>.`` per suite joined with
``|``; the trailing dot keeps ``WorldEdgeTest`` from matching
``WorldEdgeBreakingTest``.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

ALL = "all"
NONE = "none"
SPECIALS = (ALL, NONE)
TEST_NAMESPACE = "Roadman.Tests"
GROUPS_FILE = "game/tests/test-groups.json"
TESTS_DIR = "game/tests"
NOTE = (
    "git-ignored files (regenerated map packs in game/maps/, the test pack, "
    "tools/mapgen/data/raw/) are invisible to the selection; the full /verify covers them"
)


class GroupsError(Exception):
    """The groups file is unreadable or has the wrong shape."""


# ---------------------------------------------------------------- globs


def glob_to_regex(glob: str) -> re.Pattern[str]:
    """Translate a path glob: '**' spans folders (zero too), '*' and '?' stay in one segment."""
    out = []
    i = 0
    n = len(glob)
    while i < n:
        c = glob[i]
        if c == "*":
            if glob.startswith("**", i):
                at_segment_start = i == 0 or glob[i - 1] == "/"
                if at_segment_start and glob.startswith("**/", i):
                    out.append("(?:.*/)?")
                    i += 3
                    continue
                if at_segment_start and i + 2 == n:
                    out.append(".*")
                    i += 2
                    continue
                out.append("[^/]*")
                i += 2
                continue
            out.append("[^/]*")
        elif c == "?":
            out.append("[^/]")
        else:
            out.append(re.escape(c))
        i += 1
    return re.compile("".join(out) + r"\Z", re.DOTALL)


def glob_match(glob: str, path: str) -> bool:
    return glob_to_regex(glob).match(path) is not None


# ---------------------------------------------------------------- groups file


@dataclass
class Rule:
    glob: str
    targets: list[str]
    regex: re.Pattern[str] = field(repr=False)


@dataclass
class GroupMap:
    groups: dict[str, list[str]]  # group -> suites, in file order
    rules: list[Rule]
    pytest_paths: list[str] = field(default_factory=list)

    def is_pytest_input(self, path: str) -> bool:
        return any(glob_match(g, path) for g in self.pytest_paths)

    def match(self, path: str) -> Rule | None:
        for rule in self.rules:
            if rule.regex.match(path):
                return rule
        return None

    def suite_group(self) -> dict[str, list[str]]:
        result: dict[str, list[str]] = {}
        for group, suites in self.groups.items():
            for suite in suites:
                result.setdefault(suite, []).append(group)
        return result


def _check_keys(obj: dict, allowed: set[str], required: set[str], where: str) -> None:
    unknown = sorted(set(obj) - allowed)
    if unknown:
        raise GroupsError(f"{where}: unknown key(s) {', '.join(unknown)}")
    missing = sorted(required - set(obj))
    if missing:
        raise GroupsError(f"{where}: missing key(s) {', '.join(missing)}")


def _is_str_list(value) -> bool:
    return isinstance(value, list) and all(isinstance(v, str) and v for v in value)


def parse_groups(data) -> GroupMap:
    if not isinstance(data, dict):
        raise GroupsError("top level must be an object")
    _check_keys(
        data,
        {"comment", "groups", "rules", "pytest_paths"},
        {"groups", "rules", "pytest_paths"},
        "top level",
    )
    pytest_paths = data["pytest_paths"]
    if not _is_str_list(pytest_paths) or not pytest_paths:
        raise GroupsError("'pytest_paths' must be a non-empty list of path globs")
    for glob in pytest_paths:
        if glob.startswith("/"):
            raise GroupsError(f"pytest_paths: '{glob}' must be repo-relative")
    groups_obj = data["groups"]
    if not isinstance(groups_obj, dict) or not groups_obj:
        raise GroupsError("'groups' must be a non-empty object")
    groups: dict[str, list[str]] = {}
    for name, body in groups_obj.items():
        where = f"group '{name}'"
        if name in SPECIALS:
            raise GroupsError(f"{where}: '{name}' is a reserved target name")
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]*", name):
            raise GroupsError(f"{where}: name must be lowercase letters, digits and '-'")
        if not isinstance(body, dict):
            raise GroupsError(f"{where}: must be an object")
        _check_keys(body, {"comment", "suites", "spec"}, {"suites", "spec"}, where)
        suites = body["suites"]
        if not _is_str_list(suites) or not suites:
            raise GroupsError(f"{where}: 'suites' must be a non-empty list of class names")
        if not isinstance(body["spec"], str) or not body["spec"]:
            raise GroupsError(f"{where}: 'spec' must be a non-empty string")
        for suite in suites:
            if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", suite):
                raise GroupsError(f"{where}: '{suite}' is not a class name")
        groups[name] = list(suites)
    rules_obj = data["rules"]
    if not isinstance(rules_obj, list) or not rules_obj:
        raise GroupsError("'rules' must be a non-empty list")
    rules = []
    for i, body in enumerate(rules_obj, 1):
        where = f"rule {i}"
        if not isinstance(body, dict):
            raise GroupsError(f"{where}: must be an object")
        _check_keys(body, {"comment", "glob", "targets"}, {"glob", "targets"}, where)
        glob = body["glob"]
        targets = body["targets"]
        if not isinstance(glob, str) or not glob or glob.startswith("/"):
            raise GroupsError(f"{where}: 'glob' must be a non-empty repo-relative path glob")
        if not _is_str_list(targets) or not targets:
            raise GroupsError(f"{where} ({glob}): 'targets' must be a non-empty list of names")
        rules.append(Rule(glob, list(targets), glob_to_regex(glob)))
    return GroupMap(groups, rules, list(pytest_paths))


def load_groups(path: Path) -> GroupMap:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as e:
        raise GroupsError(f"can't read {path}: {e.strerror or e}") from e
    try:
        data = json.loads(text)
    except json.JSONDecodeError as e:
        raise GroupsError(f"{path} is not valid JSON: {e}") from e
    return parse_groups(data)


def rule_problems(gmap: GroupMap) -> list[str]:
    """Targets that name no group, or mix a special with other targets."""
    problems = []
    for i, rule in enumerate(gmap.rules, 1):
        for target in rule.targets:
            if target not in SPECIALS and target not in gmap.groups:
                problems.append(f"rule {i} ({rule.glob}) names unknown group '{target}'")
        if any(t in SPECIALS for t in rule.targets) and len(rule.targets) > 1:
            problems.append(f"rule {i} ({rule.glob}): 'all' or 'none' must be its only target")
        if len(set(rule.targets)) != len(rule.targets):
            problems.append(f"rule {i} ({rule.glob}) repeats a target")
    return problems


# ---------------------------------------------------------------- C# scan

_COMMENT_OR_STRING = re.compile(
    r"//[^\n]*"  # line comment
    r"|/\*.*?\*/"  # block comment
    r'|@"(?:[^"]|"")*"'  # verbatim string
    r'|"""(?:.|\n)*?"""'  # raw string (3 quotes)
    r'|\$?"(?:\\.|[^"\\\n])*"'  # regular or interpolated string (holes dropped too)
    r"|'(?:\\.|[^'\\\n])'",  # char literal
    re.DOTALL,
)
_TYPE_DECL = re.compile(
    r"\b(?:class|record|struct|interface|enum)\s+(?:class\s+|struct\s+)?([A-Za-z_][A-Za-z0-9_]*)"
)
_NAMESPACE_BLOCK = re.compile(r"\bnamespace\s+[\w.]+\s*\{")
_TEST_SUITE_ATTR = re.compile(r"\[\s*(?:GdUnit4\.)?TestSuite\b")


def strip_code(text: str) -> str:
    """Blank out comments and string literals, keeping newlines and offsets roughly."""
    return _COMMENT_OR_STRING.sub(lambda m: " " + "\n" * m.group(0).count("\n"), text)


@dataclass
class CsFile:
    path: str  # repo-relative
    code: str  # comments and strings stripped
    types: list[str]  # top-level type names declared in it
    suites: list[str]  # [TestSuite] classes declared in it


def scan_cs(rel: str, text: str) -> CsFile:
    code = strip_code(text)
    base_depth = 1 if _NAMESPACE_BLOCK.search(code) else 0
    types: list[str] = []
    suites: list[str] = []
    depth = 0
    pending_suite = False
    token = re.compile(
        r"(?P<open>\{)|(?P<close>\})|(?P<suite>"
        + _TEST_SUITE_ATTR.pattern
        + r")|(?P<decl>"
        + _TYPE_DECL.pattern.replace("([A-Za-z_]", "(?P<name>[A-Za-z_]", 1)
        + ")"
    )
    for m in token.finditer(code):
        if m.group("open"):
            depth += 1
        elif m.group("close"):
            depth -= 1
        elif m.group("suite"):
            if depth == base_depth:
                pending_suite = True
        else:
            if depth == base_depth:
                types.append(m.group("name"))
                if pending_suite and re.match(r"(?:class|record)\b", m.group("decl")):
                    suites.append(m.group("name"))
            pending_suite = False
    return CsFile(rel, code, types, suites)


def scan_tests(repo: Path) -> list[CsFile]:
    folder = repo / TESTS_DIR
    files = []
    for path in sorted(folder.rglob("*.cs")):
        rel = path.relative_to(repo).as_posix()
        files.append(scan_cs(rel, path.read_text(encoding="utf-8", errors="replace")))
    return files


def references(code: str, name: str) -> bool:
    return (
        re.search(r"(?<![\w.])" + re.escape(name) + r"\b", code) is not None
        or re.search(r"\bRoadman\.(?:\w+\.)*" + re.escape(name) + r"\b", code) is not None
    )


def completeness_problems(repo: Path, gmap: GroupMap) -> list[str]:
    problems = list(rule_problems(gmap))
    files = scan_tests(repo)
    declared: dict[str, str] = {}  # suite -> file
    for f in files:
        for s in f.suites:
            if s in declared:
                problems.append(f"[TestSuite] class {s} is declared in {declared[s]} and {f.path}")
            declared.setdefault(s, f.path)
    in_groups = gmap.suite_group()
    for suite, file in sorted(declared.items()):
        groups = in_groups.get(suite, [])
        if not groups:
            problems.append(f"[TestSuite] class {suite} ({file}) is in no group")
        elif len(groups) > 1:
            problems.append(
                f"[TestSuite] class {suite} is in more than one group: {', '.join(groups)}"
            )
    for suite, groups in sorted(in_groups.items()):
        if suite not in declared:
            problems.append(
                f"group {', '.join(groups)} names suite {suite}, but no [TestSuite] class {suite} "
                f"exists in {TESTS_DIR}/"
            )

    # Which files each file uses directly (by its top-level types).
    owner: dict[str, str] = {}
    for f in files:
        for t in f.types:
            owner.setdefault(t, f.path)
    uses: dict[str, set[str]] = {f.path: set() for f in files}
    for f in files:
        for t, other in owner.items():
            if other != f.path and references(f.code, t):
                uses[f.path].add(other)

    # A suite file uses everything it reaches through helpers too.
    def reach(start: str) -> set[str]:
        seen: set[str] = set()
        todo = [start]
        while todo:
            for nxt in uses[todo.pop()]:
                if nxt not in seen and nxt != start:
                    seen.add(nxt)
                    todo.append(nxt)
        return seen

    suite_file_group: dict[str, str] = {}
    for f in files:
        for s in f.suites:
            groups = in_groups.get(s, [])
            if len(groups) == 1:
                suite_file_group.setdefault(f.path, groups[0])

    for path, group in sorted(suite_file_group.items()):
        rule = gmap.match(path)
        targets = rule.targets if rule else [ALL]
        if group not in targets and ALL not in targets:
            where = f"rule '{rule.glob}'" if rule else "no rule"
            problems.append(
                f"suite file {path} matches {where} -> {', '.join(targets)}, "
                f"which leaves out its own group {group}"
            )
        for used in sorted(reach(path)):
            urule = gmap.match(used)
            utargets = urule.targets if urule else [ALL]
            if group not in utargets and ALL not in utargets:
                where = f"rule '{urule.glob}'" if urule else "no rule"
                problems.append(
                    f"helper {used} is used by {path} (group {group}), but it matches {where} -> "
                    f"{', '.join(utargets)}; add {group} or map it to all"
                )
    problems.extend(area_problems(repo, gmap, files, suite_file_group, reach))
    return problems


SOURCE_DIRS = ("game/scripts", "game/Roadman.Core")


def scan_sources(repo: Path) -> list[CsFile]:
    """The game's and Roadman.Core's C# files (not their tests, not build output)."""
    files = []
    for top in SOURCE_DIRS:
        folder = repo / top
        if not folder.is_dir():
            continue
        for path in sorted(folder.rglob("*.cs")):
            rel_parts = path.relative_to(folder).parts
            if "bin" in rel_parts or "obj" in rel_parts:
                continue
            rel = path.relative_to(repo).as_posix()
            files.append(scan_cs(rel, path.read_text(encoding="utf-8", errors="replace")))
    return files


def area_problems(repo, gmap, test_files, suite_file_group, reach) -> list[str]:
    """A suite (with the helpers it reaches) that uses a type from a source file whose
    rule names groups must find its own group among them."""
    by_path = {f.path: f for f in test_files}
    problems = []
    sources = scan_sources(repo)
    for path, group in sorted(suite_file_group.items()):
        codes = [by_path[path].code] + [by_path[p].code for p in sorted(reach(path))]
        suites = ", ".join(by_path[path].suites)
        for src in sources:
            rule = gmap.match(src.path)
            if rule is None or ALL in rule.targets or group in rule.targets:
                continue
            used = next((t for t in src.types if any(references(c, t) for c in codes)), None)
            if used is None:
                continue
            problems.append(
                f"{src.path} (type {used}) is used by suite {suites} (group {group}), but it "
                f"matches rule '{rule.glob}' -> {', '.join(rule.targets)}; add {group} or map "
                "it to all"
            )
    return problems


# ---------------------------------------------------------------- git


class GitError(Exception):
    pass


def git(repo: Path, *args: str) -> str:
    try:
        proc = subprocess.run(
            ["git", "-C", str(repo), *args],
            capture_output=True,
            check=False,
        )
    except OSError as e:
        raise GitError(f"can't run git: {e}") from e
    if proc.returncode != 0:
        msg = proc.stderr.decode("utf-8", "replace").strip().splitlines()
        raise GitError(f"'git {' '.join(args)}' failed: {msg[-1] if msg else proc.returncode}")
    return proc.stdout.decode("utf-8", "surrogateescape")


def git_ok(repo: Path, *args: str) -> bool:
    try:
        git(repo, *args)
    except GitError:
        return False
    return True


def parse_name_status(out: str) -> list[str]:
    """Paths from `git diff --name-status -z`; renames and copies give both paths."""
    parts = out.split("\0")
    paths: list[str] = []
    i = 0
    while i < len(parts):
        status = parts[i]
        if not status:
            i += 1
            continue
        if status[0] in "RC":
            paths.extend(parts[i + 1 : i + 3])
            i += 3
        else:
            paths.append(parts[i + 1])
            i += 2
    return paths


def changed_paths(repo: Path, base: str | None) -> tuple[list[str], str | None]:
    """(paths, None), or ([], reason) when the diff can't be computed."""
    try:
        git(repo, "rev-parse", "--git-dir")
    except GitError as e:
        return [], f"not a readable git repository ({e})"
    if not git_ok(repo, "rev-parse", "--verify", "--quiet", "HEAD^{commit}"):
        return [], "no HEAD commit"
    if not git_ok(repo, "symbolic-ref", "-q", "HEAD"):
        return [], "detached HEAD"
    ref = "HEAD"
    if base is not None:
        if (
            not base
            or base.startswith("-")
            or not git_ok(repo, "rev-parse", "--verify", "--quiet", f"{base}^{{commit}}")
        ):
            return [], f"base '{base}' doesn't resolve to a commit"
        ref = base
    try:
        diff = git(repo, "diff", "--name-status", "-M", "-z", ref, "--")
        untracked = git(repo, "ls-files", "--others", "--exclude-standard", "-z")
    except GitError as e:
        return [], str(e)
    paths = parse_name_status(diff) + [p for p in untracked.split("\0") if p]
    return sorted(set(paths)), None


# ---------------------------------------------------------------- selection


@dataclass
class Selection:
    mode: str  # all, groups, none
    groups: list[str]
    suites: list[str]
    reasons: dict[str, str]  # group -> path
    all_reason: str | None
    details: list[tuple[str, str, list[str], bool]]  # path, rule text, targets, pytest input
    base: str
    pytest_reason: str | None = None  # None: pytest skipped


def all_suites(gmap: GroupMap) -> list[str]:
    return sorted({s for suites in gmap.groups.values() for s in suites})


def select(gmap: GroupMap, paths: list[str], base: str = "HEAD") -> Selection:
    details = []
    reasons: dict[str, str] = {}
    all_reason = None
    pytest_reason = None
    for path in sorted(paths):
        is_pytest = gmap.is_pytest_input(path)
        if is_pytest and pytest_reason is None:
            pytest_reason = path
        rule = gmap.match(path)
        if rule is None:
            details.append((path, "no rule", [ALL], is_pytest))
            if all_reason is None:
                all_reason = f"{path} matches no rule"
            if pytest_reason is None:
                pytest_reason = f"{path} matches no rule"
            continue
        details.append((path, f"rule '{rule.glob}'", rule.targets, is_pytest))
        for target in rule.targets:
            if target == ALL:
                if all_reason is None:
                    all_reason = f"{path} (rule '{rule.glob}')"
            elif target != NONE:
                reasons.setdefault(target, path)
    if all_reason is not None:
        return Selection(ALL, [], all_suites(gmap), {}, all_reason, details, base, pytest_reason)
    if not reasons:
        return Selection(NONE, [], [], {}, None, details, base, pytest_reason)
    groups = [g for g in gmap.groups if g in reasons]
    suites = sorted({s for g in groups for s in gmap.groups[g]})
    reasons = {g: reasons[g] for g in groups}
    return Selection("groups", groups, suites, reasons, None, details, base, pytest_reason)


def fail_safe(gmap: GroupMap | None, reason: str, base: str) -> Selection:
    """Everything runs, gdUnit4 and pytest alike: the diff or the map can't be trusted."""
    suites = all_suites(gmap) if gmap else []
    return Selection(ALL, [], suites, {}, reason, [], base, reason)


def suite_filter(suites: list[str]) -> str:
    return "|".join(f"FullyQualifiedName~{TEST_NAMESPACE}.{s}." for s in suites)


def _clean(text: str) -> str:
    return text.replace("\r", "?").replace("\n", "?")


def format_shell(sel: Selection, dry_run: bool) -> list[str]:
    lines = [
        f"MODE={sel.mode}",
        f"GROUPS={' '.join(sel.groups)}",
        f"SUITES={' '.join(sel.suites)}",
        f"FILTER={suite_filter(sel.suites) if sel.mode == 'groups' else ''}",
        f"PYTEST={0 if sel.pytest_reason is None else 1}",
        f"PYTEST_REASON={_clean(sel.pytest_reason or '')}",
    ]
    if sel.mode == ALL:
        lines.append(f"ALL_REASON={_clean(sel.all_reason or '')}")
    for n, g in enumerate(sel.groups, 1):
        lines.append(f"REASON_{n}={g}: {_clean(sel.reasons[g])}")
    if dry_run:
        for n, (path, rule, targets, is_pytest) in enumerate(sel.details, 1):
            note = " (pytest)" if is_pytest else ""
            lines.append(
                f"DETAIL_{n}={_clean(path)} -> {_clean(rule)} -> {', '.join(targets)}{note}"
            )
    lines.append(f"NOTE={NOTE}")
    return lines


def format_text(sel: Selection, dry_run: bool) -> list[str]:
    lines = []
    if dry_run:
        lines.append(f"Changed paths (working tree vs {sel.base}, with untracked files):")
        if not sel.details:
            lines.append("  (none)")
        for path, rule, targets, is_pytest in sel.details:
            note = " (pytest)" if is_pytest else ""
            lines.append(f"  {_clean(path)} -> {rule} -> {', '.join(targets)}{note}")
    if sel.mode == ALL:
        lines.append(
            f"Selection: all ({len(sel.suites)} suites), because {_clean(sel.all_reason or '')}"
        )
    elif sel.mode == NONE:
        lines.append("Selection: none (no changed file affects a gdUnit4 group)")
    else:
        lines.append(f"Selection: {', '.join(sel.groups)}")
        for g in sel.groups:
            lines.append(f"  {g}: {_clean(sel.reasons[g])}")
    if dry_run or sel.mode != ALL:
        lines.append(f"Suites: {' '.join(sel.suites) if sel.suites else '(none)'}")
    if sel.pytest_reason is None:
        lines.append("Pytest: skipped (no pytest input changed)")
    else:
        lines.append(f"Pytest: run ({_clean(sel.pytest_reason)})")
    lines.append(f"Note: {NOTE}.")
    return lines


# ---------------------------------------------------------------- CLI


def default_repo() -> Path:
    return Path(__file__).resolve().parents[2]


def cmd_check(args) -> int:
    repo = Path(args.repo) if args.repo else default_repo()
    groups_path = Path(args.groups) if args.groups else repo / GROUPS_FILE
    try:
        gmap = load_groups(groups_path)
    except GroupsError as e:
        print(f"ERROR: {e}")
        return 1
    problems = completeness_problems(repo, gmap)
    for p in problems:
        print(f"ERROR: {p}")
    if problems:
        return 1
    print(
        f"OK: {len(all_suites(gmap))} suites in {len(gmap.groups)} groups, "
        f"{len(gmap.rules)} rules ({groups_path})"
    )
    return 0


def cmd_select(args) -> int:
    repo = Path(args.repo) if args.repo else default_repo()
    groups_path = Path(args.groups) if args.groups else repo / GROUPS_FILE
    base = args.base if args.base is not None else "HEAD"
    gmap = None
    try:
        gmap = load_groups(groups_path)
        problems = rule_problems(gmap)
        if problems:
            raise GroupsError(problems[0])
    except GroupsError as e:
        sel = fail_safe(gmap, f"{groups_path.name} is invalid: {e}", base)
    else:
        paths, reason = changed_paths(repo, args.base)
        sel = fail_safe(gmap, reason, base) if reason else select(gmap, paths, base)
    out = (
        format_shell(sel, args.dry_run)
        if args.format == "shell"
        else format_text(sel, args.dry_run)
    )
    sys.stdout.write("\n".join(out) + "\n")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("check", "select"):
        p = sub.add_parser(name)
        p.add_argument("--repo", help="repository root (default: the one holding this script)")
        p.add_argument("--groups", help=f"groups file (default: <repo>/{GROUPS_FILE})")
        if name == "select":
            p.add_argument(
                "--dry-run", action="store_true", help="also list each path and its rule"
            )
            p.add_argument("--format", choices=("text", "shell"), default="text")
            p.add_argument(
                "base", nargs="?", help="commit, branch or tag to diff against (default HEAD)"
            )
    args = parser.parse_args(argv)
    return cmd_check(args) if args.command == "check" else cmd_select(args)


if __name__ == "__main__":
    sys.exit(main())
