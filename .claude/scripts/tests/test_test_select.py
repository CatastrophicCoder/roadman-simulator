"""Self-tests for .claude/scripts/test_select.py (docs/specs/test-performance.md, criteria 7 and 8).

Run from the repo root (standard library only):

    python3 -I -m unittest discover -s .claude/scripts/tests -v

The git tests build throw-away repositories in a temporary folder, with the
user's global and system git config switched off.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
REPO = SCRIPTS.parents[1]
SCRIPT = SCRIPTS / "test_select.py"
sys.path.insert(0, str(SCRIPTS))

import test_select as ts  # noqa: E402

REAL_GROUPS = REPO / ts.GROUPS_FILE


def real_map() -> ts.GroupMap:
    return ts.load_groups(REAL_GROUPS)


def selected(paths: list[str]) -> ts.Selection:
    return ts.select(real_map(), paths)


class GlobTest(unittest.TestCase):
    def test_double_star_spans_zero_or_more_folders(self):
        self.assertTrue(ts.glob_match("docs/**", "docs/x.md"))
        self.assertTrue(ts.glob_match("docs/**", "docs/a/b/c.md"))
        self.assertTrue(ts.glob_match("**/*.md", "CLAUDE.md"))
        self.assertTrue(ts.glob_match("**/*.md", "game/a/b/CLAUDE.md"))
        self.assertTrue(ts.glob_match("a/**/b.cs", "a/b.cs"))
        self.assertTrue(ts.glob_match("a/**/b.cs", "a/x/y/b.cs"))
        self.assertFalse(ts.glob_match("docs/**", "game/docs/x.md"))

    def test_star_and_question_mark_stay_in_one_segment(self):
        self.assertTrue(ts.glob_match("game/*.csproj", "game/RoadmanSimulator.csproj"))
        self.assertFalse(ts.glob_match("game/*.csproj", "game/Roadman.Core/Roadman.Core.csproj"))
        self.assertTrue(
            ts.glob_match("game/tests/WorldEdgeTest.cs*", "game/tests/WorldEdgeTest.cs.uid")
        )
        self.assertFalse(
            ts.glob_match("game/tests/WorldEdgeTest.cs*", "game/tests/WorldEdgeBreakingTest.cs")
        )
        self.assertTrue(ts.glob_match("a?c", "abc"))
        self.assertFalse(ts.glob_match("a?c", "a/c"))

    def test_other_characters_are_literal(self):
        self.assertTrue(ts.glob_match("game/.runsettings", "game/.runsettings"))
        self.assertFalse(ts.glob_match("game/.runsettings", "game/xrunsettings"))
        self.assertFalse(ts.glob_match("game/Roadman.Core/**", "game/Roadman.Core.Tests/x.cs"))


class SelectionRulesTest(unittest.TestCase):
    """Criterion 8 over fixed path lists, with the real test-groups.json."""

    def assertGroups(self, sel, groups):
        self.assertEqual(sel.mode, "groups", sel.all_reason)
        self.assertEqual(sorted(sel.groups), sorted(groups))

    def assertAll(self, sel, path):
        self.assertEqual(sel.mode, "all")
        self.assertIn(path, sel.all_reason)
        self.assertEqual(sel.suites, ts.all_suites(real_map()))

    def test_one_suite_file_selects_its_group(self):
        sel = selected(["game/tests/RoadGraphTest.cs"])
        self.assertGroups(sel, ["roads"])
        self.assertEqual(sel.suites, ["RoadGraphTest"])
        self.assertEqual(sel.reasons, {"roads": "game/tests/RoadGraphTest.cs"})

    def test_every_suite_file_selects_its_own_group(self):
        gmap = real_map()
        for group, suites in gmap.groups.items():
            for suite in suites:
                for path in (f"game/tests/{suite}.cs", f"game/tests/{suite}.cs.uid"):
                    sel = ts.select(gmap, [path])
                    self.assertIn(group, sel.groups, path)

    def test_world_cs_selects_all(self):
        self.assertAll(selected(["game/scripts/world/World.cs"]), "game/scripts/world/World.cs")

    def test_player_state_selects_all(self):
        self.assertAll(
            selected(["game/Roadman.Core/PlayerState.cs"]), "game/Roadman.Core/PlayerState.cs"
        )

    def test_player_cs_and_player_camera_select_all(self):
        for path in (
            "game/scripts/player/Player.cs",
            "game/scripts/player/PlayerCamera.cs",
            "game/scripts/player/GamepadButtons.cs",
        ):
            self.assertAll(selected([path]), path)

    def test_core_items(self):
        self.assertGroups(
            selected(["game/Roadman.Core/Items/x.cs"]), ["inventory", "saves", "world-edge"]
        )

    def test_core_player(self):
        self.assertGroups(
            selected(["game/Roadman.Core/Player/x.cs"]),
            ["player", "world-edge", "inventory", "saves"],
        )

    def test_core_edge(self):
        self.assertGroups(
            selected(["game/Roadman.Core/Edge/x.cs"]), ["world-edge", "saves", "inventory"]
        )

    def test_core_maps_selects_all(self):
        self.assertAll(selected(["game/Roadman.Core/Maps/x.cs"]), "game/Roadman.Core/Maps/x.cs")

    def test_docs_alone_select_nothing(self):
        sel = selected(["docs/specs/x.md"])
        self.assertEqual(sel.mode, "none")
        self.assertEqual(sel.suites, [])
        self.assertEqual(
            ts.format_shell(sel, False)[:4], ["MODE=none", "GROUPS=", "SUITES=", "FILTER="]
        )

    def test_docs_with_a_suite_file_select_its_group(self):
        sel = selected(["docs/specs/x.md", "game/tests/InventoryTest.cs"])
        self.assertGroups(sel, ["inventory"])

    def test_unmapped_path_selects_all(self):
        sel = selected(["game/tests/RoadGraphTest.cs", "somewhere/new.txt"])
        self.assertAll(sel, "somewhere/new.txt")
        self.assertIn("matches no rule", sel.all_reason)

    def test_edge_script_selects_world_edge_only(self):
        sel = selected(["game/scripts/world/edge/IBarrierBreaker.cs"])
        self.assertGroups(sel, ["world-edge"])
        self.assertEqual(
            sel.suites, ["DeathScreenLayoutTest", "WorldEdgeBreakingTest", "WorldEdgeTest"]
        )

    def test_world_edge_helper_maps_to_all_its_users(self):
        self.assertGroups(
            selected(["game/tests/WorldEdgeTest.cs"]), ["world-edge", "saves", "inventory"]
        )

    def test_reason_is_first_path_per_group(self):
        sel = selected(["game/tests/PlayerOnFootTest.cs", "game/scripts/debug/PlaceInspector.cs"])
        self.assertEqual(sel.reasons["places"], "game/scripts/debug/PlaceInspector.cs")
        self.assertEqual(sel.reasons["player"], "game/scripts/debug/PlaceInspector.cs")

    def test_filter_ends_each_class_with_a_dot(self):
        sel = selected(["game/scripts/world/edge/IBarrierBreaker.cs"])
        shell = dict(
            line.split("=", 1)
            for line in ts.format_shell(sel, False)
            if not line.startswith("REASON_")
        )
        self.assertEqual(shell["MODE"], "groups")
        self.assertEqual(shell["GROUPS"], "world-edge")
        self.assertEqual(
            shell["FILTER"],
            "FullyQualifiedName~Roadman.Tests.DeathScreenLayoutTest."
            "|FullyQualifiedName~Roadman.Tests.WorldEdgeBreakingTest."
            "|FullyQualifiedName~Roadman.Tests.WorldEdgeTest.",
        )


class PytestSelectionTest(unittest.TestCase):
    """Quick mode runs mapgen pytest only for pytest inputs or on a fail-safe."""

    def shell(self, paths):
        out = ts.format_shell(selected(paths), True)
        return dict(line.split("=", 1) for line in out)

    def test_docs_alone_skip_pytest(self):
        out = self.shell(["docs/specs/x.md"])
        self.assertEqual((out["PYTEST"], out["PYTEST_REASON"]), ("0", ""))

    def test_pipeline_source_runs_pytest(self):
        out = self.shell(["tools/mapgen/src/a.py"])
        self.assertEqual((out["PYTEST"], out["PYTEST_REASON"]), ("1", "tools/mapgen/src/a.py"))
        self.assertEqual(out["MODE"], "all")

    def test_workflow_runs_pytest_and_no_gdunit4(self):
        out = self.shell([".github/workflows/ci.yml"])
        self.assertEqual(out["PYTEST"], "1")
        self.assertEqual(out["MODE"], "none")
        self.assertTrue(out["DETAIL_1"].endswith(" (pytest)"), out["DETAIL_1"])

    def test_spec_read_by_pytest_runs_pytest_and_no_gdunit4(self):
        for spec in ("docs/specs/place-information.md", "docs/specs/roads-and-road-graph.md"):
            out = self.shell([spec])
            self.assertEqual((out["PYTEST"], out["PYTEST_REASON"]), ("1", spec))
            self.assertEqual(out["MODE"], "none")

    def test_map_input_runs_pytest(self):
        self.assertEqual(self.shell(["maps/somero/map.yaml"])["PYTEST"], "1")

    def test_game_code_skips_pytest(self):
        out = self.shell(["game/scripts/world/World.cs"])
        self.assertEqual(out["MODE"], "all")
        self.assertEqual((out["PYTEST"], out["PYTEST_REASON"]), ("0", ""))

    def test_unmatched_path_runs_pytest(self):
        out = self.shell(["somewhere/new.txt"])
        self.assertEqual(out["PYTEST"], "1")
        self.assertIn("somewhere/new.txt matches no rule", out["PYTEST_REASON"])

    def test_fail_safe_reason_names_the_unmatched_path(self):
        out = self.shell(["game/scripts/world/World.cs", "somewhere/new.txt"])
        self.assertEqual(out["PYTEST_REASON"], "somewhere/new.txt matches no rule")

    def test_shell_order(self):
        keys = [line.split("=", 1)[0] for line in ts.format_shell(selected(["x/y"]), False)]
        self.assertEqual(
            keys[:7],
            ["MODE", "GROUPS", "SUITES", "FILTER", "PYTEST", "PYTEST_REASON", "ALL_REASON"],
        )

    def test_text_lines(self):
        text = ts.format_text(selected(["docs/specs/x.md"]), False)
        self.assertIn("Pytest: skipped (no pytest input changed)", text)
        text = ts.format_text(selected(["maps/somero/map.yaml"]), False)
        self.assertIn("Pytest: run (maps/somero/map.yaml)", text)


class GitRepo:
    """A throw-away repository holding the real groups file."""

    def __init__(self, root: Path):
        self.root = root
        self.git("-c", "init.defaultBranch=main", "init", "-q")
        self.git("config", "user.name", "Test")
        self.git("config", "user.email", "test@example.invalid")
        self.git("config", "commit.gpgsign", "false")
        self.write(ts.GROUPS_FILE, REAL_GROUPS.read_text(encoding="utf-8"))
        self.write(".gitignore", "game/maps/*/\n")

    def git(self, *args: str) -> str:
        return subprocess.run(
            ["git", "-C", str(self.root), *args], check=True, capture_output=True, text=True
        ).stdout

    def write(self, rel: str, text: str = "content\n") -> None:
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def commit(self) -> None:
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "c")

    def select(self, *args: str) -> dict[str, str]:
        out = subprocess.run(
            [
                sys.executable,
                "-I",
                str(SCRIPT),
                "select",
                "--format",
                "shell",
                "--repo",
                str(self.root),
                *args,
            ],
            check=True,
            capture_output=True,
            text=True,
        ).stdout
        return dict(line.split("=", 1) for line in out.splitlines())


class GitSelectionTest(unittest.TestCase):
    def setUp(self):
        self._env = {k: os.environ.get(k) for k in ("GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM")}
        os.environ["GIT_CONFIG_GLOBAL"] = os.devnull
        os.environ["GIT_CONFIG_NOSYSTEM"] = "1"
        self.tmp = Path(tempfile.mkdtemp(prefix="test-select-"))
        self.repo = GitRepo(self.tmp)
        self.repo.write(
            "game/Roadman.Core/Edge/Thing.cs", "class Thing { int a = 1; int b = 2; }\n" * 20
        )
        self.repo.write("game/tests/RoadGraphTest.cs", "// suite\n")
        self.repo.write(
            "game/scripts/debug/ScreenshotTool.cs", "class Tool { int a = 1; int b = 2; }\n" * 20
        )
        self.repo.commit()

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)
        for k, v in self._env.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v

    def test_clean_tree_selects_nothing(self):
        out = self.repo.select()
        self.assertEqual(out["MODE"], "none")
        self.assertIn("full /verify", out["NOTE"])

    def test_unstaged_and_staged_changes_count(self):
        self.repo.write("game/tests/RoadGraphTest.cs", "// changed\n")
        self.assertEqual(self.repo.select()["GROUPS"], "roads")
        self.repo.git("add", "-A")
        self.assertEqual(self.repo.select()["GROUPS"], "roads")

    def test_untracked_new_file_under_a_group_path(self):
        self.repo.write("game/scripts/world/edge/NewThing.cs")
        out = self.repo.select("--dry-run")
        self.assertEqual(out["MODE"], "groups")
        self.assertEqual(out["GROUPS"], "world-edge saves inventory")
        self.assertEqual(out["REASON_1"], "world-edge: game/scripts/world/edge/NewThing.cs")
        self.assertIn(
            "game/scripts/world/edge/NewThing.cs -> rule 'game/scripts/world/edge/**'",
            out["DETAIL_1"],
        )

    def test_ignored_file_is_invisible(self):
        self.repo.write("game/maps/somero/manifest.json")
        self.assertEqual(self.repo.select()["MODE"], "none")

    def test_rename_across_groups_selects_both(self):
        # ScreenshotTool.cs selects player only, game/scripts/ui/** saves and inventory.
        (self.tmp / "game/scripts/ui").mkdir(parents=True)
        self.repo.git(
            "mv", "game/scripts/debug/ScreenshotTool.cs", "game/scripts/ui/ScreenshotTool.cs"
        )
        out = self.repo.select("--dry-run")
        self.assertEqual(out["MODE"], "groups")
        self.assertEqual(out["GROUPS"], "player saves inventory")

    def test_deletion_counts(self):
        self.repo.git("rm", "-q", "game/tests/RoadGraphTest.cs")
        self.assertEqual(self.repo.select()["GROUPS"], "roads")

    def test_base_argument_diffs_against_that_commit(self):
        base = self.repo.git("rev-parse", "HEAD").strip()
        self.repo.write("game/tests/RoadGraphTest.cs", "// changed\n")
        self.repo.commit()
        self.assertEqual(self.repo.select()["MODE"], "none")
        self.assertEqual(self.repo.select(base)["GROUPS"], "roads")

    def test_unknown_base_selects_all_with_reason(self):
        out = self.repo.select("no-such-branch")
        self.assertEqual(out["MODE"], "all")
        self.assertIn("no-such-branch", out["ALL_REASON"])
        self.assertIn("doesn't resolve", out["ALL_REASON"])
        self.assertEqual(out["PYTEST"], "1")
        self.assertEqual(out["PYTEST_REASON"], out["ALL_REASON"])
        text = subprocess.run(
            [
                sys.executable,
                "-I",
                str(SCRIPT),
                "select",
                "--repo",
                str(self.tmp),
                "no-such-branch",
            ],
            check=True,
            capture_output=True,
            text=True,
        ).stdout
        self.assertIn("base 'no-such-branch' doesn't resolve", text)

    def test_option_like_base_selects_all(self):
        self.assertEqual(self.repo.select("--", "--output=x")["MODE"], "all")

    def test_detached_head_selects_all(self):
        self.repo.git("checkout", "-q", "--detach")
        self.repo.write("game/tests/RoadGraphTest.cs", "// changed\n")
        out = self.repo.select()
        self.assertEqual(out["MODE"], "all")
        self.assertIn("detached HEAD", out["ALL_REASON"])
        self.assertEqual(out["PYTEST"], "1")

    def test_no_head_selects_all(self):
        empty = self.tmp / "empty"
        empty.mkdir()
        GitRepo(empty)
        out = subprocess.run(
            [
                sys.executable,
                "-I",
                str(SCRIPT),
                "select",
                "--format",
                "shell",
                "--repo",
                str(empty),
            ],
            check=True,
            capture_output=True,
            text=True,
        ).stdout
        self.assertIn("MODE=all", out.splitlines())
        self.assertIn("ALL_REASON=no HEAD commit", out.splitlines())
        self.assertIn("PYTEST=1", out.splitlines())

    def test_not_a_repository_selects_all(self):
        plain = self.tmp / "plain"
        (plain / "game/tests").mkdir(parents=True)
        shutil.copy(REAL_GROUPS, plain / ts.GROUPS_FILE)
        out = subprocess.run(
            [
                sys.executable,
                "-I",
                str(SCRIPT),
                "select",
                "--format",
                "shell",
                "--repo",
                str(plain),
            ],
            check=True,
            capture_output=True,
            text=True,
            env={**os.environ, "GIT_CEILING_DIRECTORIES": str(self.tmp)},
        ).stdout
        self.assertIn("MODE=all", out.splitlines())

    def test_invalid_groups_file_selects_all(self):
        self.repo.write(ts.GROUPS_FILE, "{")
        out = self.repo.select()
        self.assertEqual(out["MODE"], "all")
        self.assertIn("test-groups.json is invalid", out["ALL_REASON"])
        self.assertEqual(out["PYTEST"], "1")


SUITE = (
    "using GdUnit4;\nnamespace Roadman.Tests;\n"
    "[TestSuite]\npublic class {name}\n{{\n    {body}\n}}\n"
)


class CompletenessTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="test-select-check-"))
        (self.tmp / ts.TESTS_DIR).mkdir(parents=True)
        self.suite("ATest")
        self.suite("BTest")
        self.groups = {"groups": {"a": ["ATest"], "b": ["BTest"]}, "rules": []}

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def suite(self, name: str, body: str = "") -> None:
        text = SUITE.format(name=name, body=body)
        (self.tmp / ts.TESTS_DIR / f"{name}.cs").write_text(text, encoding="utf-8")

    def helper(self, name: str) -> None:
        text = (
            "namespace Roadman.Tests;\n"
            f"internal static class {name}\n{{\n    public static int X() => 1;\n}}\n"
        )
        (self.tmp / ts.TESTS_DIR / f"{name}.cs").write_text(text, encoding="utf-8")

    def rule(self, glob: str, *targets: str) -> None:
        self.groups["rules"].append({"glob": glob, "targets": list(targets)})

    def problems(self) -> list[str]:
        if not self.groups["rules"]:
            self.rule("game/tests/ATest.cs*", "a")
            self.rule("game/tests/BTest.cs*", "b")
        data = {
            "groups": {k: {"suites": v, "spec": k} for k, v in self.groups["groups"].items()},
            "pytest_paths": ["tools/**"],
            "rules": self.groups["rules"],
        }
        return ts.completeness_problems(self.tmp, ts.parse_groups(data))

    def test_minimal_map_passes(self):
        self.assertEqual(self.problems(), [])

    def test_suite_in_no_group(self):
        self.suite("CTest")
        self.assertTrue(any("CTest" in p and "no group" in p for p in self.problems()))

    def test_suite_in_two_groups(self):
        self.groups["groups"]["b"].append("ATest")
        self.assertTrue(any("ATest" in p and "more than one group" in p for p in self.problems()))

    def test_group_naming_a_missing_suite(self):
        self.groups["groups"]["a"].append("GoneTest")
        self.assertTrue(
            any("GoneTest" in p and "no [TestSuite] class" in p for p in self.problems())
        )

    def test_rule_naming_an_unknown_group(self):
        self.rule("game/tests/ATest.cs*", "a")
        self.rule("game/tests/BTest.cs*", "b")
        self.rule("game/scripts/**", "nope")
        self.assertTrue(any("unknown group 'nope'" in p for p in self.problems()))

    def test_helper_used_by_two_suites_mapped_to_one_group(self):
        self.helper("Shared")
        self.suite("ATest", "int A() => Shared.X();")
        self.suite("BTest", "int B() => Shared.X();")
        self.rule("game/tests/Shared.cs*", "a")
        self.rule("game/tests/ATest.cs*", "a")
        self.rule("game/tests/BTest.cs*", "b")
        problems = self.problems()
        self.assertTrue(any("Shared.cs" in p and "BTest.cs" in p for p in problems), problems)
        self.assertFalse(any("ATest.cs (group a)" in p for p in problems), problems)

    def test_helper_mapped_to_both_groups_or_all_passes(self):
        self.helper("Shared")
        self.suite("ATest", "int A() => Shared.X();")
        self.suite("BTest", "int B() => Shared.X();")
        for targets in (("a", "b"), ("all",)):
            self.groups["rules"] = []
            self.rule("game/tests/Shared.cs*", *targets)
            self.rule("game/tests/ATest.cs*", "a")
            self.rule("game/tests/BTest.cs*", "b")
            self.assertEqual(self.problems(), [], targets)

    def test_unmapped_helper_falls_to_all_and_passes(self):
        self.helper("Shared")
        self.suite("ATest", "int A() => Shared.X();")
        self.suite("BTest", "int B() => Shared.X();")
        self.assertEqual(self.problems(), [])

    def test_suite_used_by_another_suite_counts_as_helper(self):
        self.suite("ATest", "public static int Make() => 1;")
        self.suite("BTest", "int B() => ATest.Make();")
        problems = self.problems()
        self.assertTrue(
            any("helper game/tests/ATest.cs" in p and "group b" in p for p in problems), problems
        )

    def test_helper_reached_through_another_helper(self):
        self.helper("Inner")
        (self.tmp / ts.TESTS_DIR / "Outer.cs").write_text(
            "namespace Roadman.Tests;\n"
            "internal static class Outer\n{\n    static int Y() => Inner.X();\n}\n",
            encoding="utf-8",
        )
        self.suite("BTest", "int B() => Outer.Y();")
        self.rule("game/tests/Inner.cs*", "a")
        self.rule("game/tests/Outer.cs*", "b")
        self.rule("game/tests/ATest.cs*", "a")
        self.rule("game/tests/BTest.cs*", "b")
        self.assertTrue(any("helper game/tests/Inner.cs" in p for p in self.problems()))

    def test_mentions_in_comments_and_strings_dont_count(self):
        self.helper("Shared")
        self.suite("BTest", '// Shared.X()\nstring s = "Shared.X()";')
        self.rule("game/tests/Shared.cs*", "a")
        self.rule("game/tests/ATest.cs*", "a")
        self.rule("game/tests/BTest.cs*", "b")
        self.assertEqual(self.problems(), [])

    def test_suite_file_rule_must_include_its_group(self):
        self.rule("game/tests/ATest.cs*", "b")
        self.rule("game/tests/BTest.cs*", "b")
        self.assertTrue(any("leaves out its own group a" in p for p in self.problems()))

    def test_json_shape_errors(self):
        def valid():
            return {
                "groups": {"a": {"suites": ["ATest"], "spec": "x"}},
                "pytest_paths": ["tools/**"],
                "rules": [{"glob": "x", "targets": ["a"]}],
            }

        ts.parse_groups(valid())
        mutations = [
            lambda d: d.update(x=1),
            lambda d: d["groups"]["a"].update(extra=1),
            lambda d: d["rules"][0].update(y=2),
            lambda d: d["groups"]["a"].update(suites=[]),
            lambda d: d.update(groups={"all": {"suites": ["ATest"], "spec": "x"}}),
            lambda d: d["rules"][0].update(targets=[]),
            lambda d: d["groups"]["a"].pop("spec"),
            lambda d: d.pop("pytest_paths"),
            lambda d: d.update(pytest_paths=[]),
            lambda d: d.update(pytest_paths=["ok/**", 3]),
            lambda d: d.update(pytest_paths="tools/**"),
        ]
        for i, mutate in enumerate(mutations):
            data = valid()
            mutate(data)
            with self.assertRaises(ts.GroupsError, msg=f"mutation {i}: {json.dumps(data)}"):
                ts.parse_groups(data)
        with self.assertRaises(ts.GroupsError):
            ts.parse_groups([])

    def test_mixed_special_target_is_a_problem(self):
        self.rule("game/tests/ATest.cs*", "a", "all")
        self.rule("game/tests/BTest.cs*", "b")
        self.assertTrue(any("must be its only target" in p for p in self.problems()))

    def test_check_command_exit_codes(self):
        self.groups["groups"] = {
            k: {"suites": v, "spec": k} for k, v in self.groups["groups"].items()
        }
        path = self.tmp / ts.GROUPS_FILE
        self.groups["rules"] = [{"glob": "**", "targets": ["all"]}]
        self.groups["pytest_paths"] = ["tools/**"]
        path.write_text(json.dumps(self.groups), encoding="utf-8")
        run = [sys.executable, "-I", str(SCRIPT), "check", "--repo", str(self.tmp)]
        ok = subprocess.run(run, capture_output=True, text=True)
        self.assertEqual(ok.returncode, 0, ok.stdout)
        self.suite("CTest")
        bad = subprocess.run(run, capture_output=True, text=True)
        self.assertEqual(bad.returncode, 1)
        self.assertIn("ERROR: [TestSuite] class CTest", bad.stdout)


class AreaCheckTest(unittest.TestCase):
    """A suite using a type from game/scripts or Roadman.Core needs its group in that rule."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="test-select-area-"))
        (self.tmp / ts.TESTS_DIR).mkdir(parents=True)
        self.write_suite("ATest", "")
        self.write_suite("BTest", "")
        self.source("game/scripts/feature/Gadget.cs", "Gadget")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write(self, rel: str, text: str) -> None:
        path = self.tmp / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def write_suite(self, name: str, body: str) -> None:
        self.write(f"{ts.TESTS_DIR}/{name}.cs", SUITE.format(name=name, body=body))

    def source(self, rel: str, name: str) -> None:
        self.write(rel, f"namespace Roadman.Feature;\npublic partial class {name}\n{{\n}}\n")

    def problems(self, *targets: str) -> list[str]:
        rules = [
            {"glob": "game/tests/ATest.cs*", "targets": ["a"]},
            {"glob": "game/tests/BTest.cs*", "targets": ["b"]},
        ]
        if targets:
            rules.append({"glob": "game/scripts/feature/**", "targets": list(targets)})
        data = {
            "groups": {
                "a": {"suites": ["ATest"], "spec": "a"},
                "b": {"suites": ["BTest"], "spec": "b"},
            },
            "pytest_paths": ["tools/**"],
            "rules": rules,
        }
        return ts.completeness_problems(self.tmp, ts.parse_groups(data))

    def test_flags_a_rule_that_leaves_out_the_users_group(self):
        self.write_suite("ATest", "Gadget G() => new Gadget();")
        problems = self.problems("b")
        self.assertEqual(len(problems), 1, problems)
        for part in ("game/scripts/feature/Gadget.cs", "type Gadget", "ATest", "group a"):
            self.assertIn(part, problems[0])
        self.assertIn("'game/scripts/feature/**'", problems[0])

    def test_passes_when_widened_or_all_or_unmapped(self):
        self.write_suite("ATest", "Gadget G() => new Gadget();")
        self.assertEqual(self.problems("a", "b"), [])
        self.assertEqual(self.problems("all"), [])
        self.assertEqual(self.problems(), [])

    def test_unused_type_is_fine(self):
        self.assertEqual(self.problems("b"), [])

    def test_use_through_a_helper_counts(self):
        self.write(
            f"{ts.TESTS_DIR}/Help.cs",
            "namespace Roadman.Tests;\ninternal static class Help\n{\n"
            "    static object Make() => new Gadget();\n}\n",
        )
        self.write_suite("ATest", "object G() => Help.Make();")
        problems = [p for p in self.problems("b") if "Gadget.cs" in p]
        self.assertEqual(len(problems), 1, problems)

    def test_fully_qualified_use_counts(self):
        self.write_suite("ATest", "object G() => new Roadman.Feature.Gadget();")
        self.assertEqual(len(self.problems("b")), 1)

    def test_core_files_count_and_build_output_and_core_tests_do_not(self):
        self.source("game/Roadman.Core/Feature/Widget.cs", "Widget")
        self.source("game/Roadman.Core/obj/Debug/Stale.cs", "Stale")
        self.source("game/Roadman.Core.Tests/Probe.cs", "Probe")
        self.write_suite("ATest", "Widget W; Stale S; Probe P;")
        data_rules = self.problems("b")
        self.assertEqual(data_rules, [])
        rules = [
            {"glob": "game/tests/ATest.cs*", "targets": ["a"]},
            {"glob": "game/tests/BTest.cs*", "targets": ["b"]},
            {"glob": "game/Roadman.Core/**", "targets": ["b"]},
            {"glob": "game/Roadman.Core.Tests/**", "targets": ["b"]},
        ]
        data = {
            "groups": {
                "a": {"suites": ["ATest"], "spec": "a"},
                "b": {"suites": ["BTest"], "spec": "b"},
            },
            "pytest_paths": ["tools/**"],
            "rules": rules,
        }
        problems = ts.completeness_problems(self.tmp, ts.parse_groups(data))
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("Widget.cs", problems[0])


class RealRepoTest(unittest.TestCase):
    def test_real_group_map_passes_the_check(self):
        self.assertEqual(ts.completeness_problems(REPO, real_map()), [])

    def test_real_map_holds_the_spec_groups(self):
        self.assertEqual(
            real_map().groups,
            {
                "map": ["MapLoaderTest", "WorldSceneTest", "OrthophotoDebugViewTest"],
                "roads": ["RoadGraphTest"],
                "places": ["PlaceInformationTest"],
                "player": ["PlayerOnFootTest"],
                "world-edge": ["WorldEdgeTest", "WorldEdgeBreakingTest", "DeathScreenLayoutTest"],
                "saves": ["MainMenuTest", "PauseTest", "SaveLoadTest", "SettingsViewTest"],
                "inventory": ["InventoryTest"],
            },
        )

    def test_every_repo_file_the_pytest_suite_reads_outside_tools_mapgen_is_a_pytest_input(self):
        # The mapgen tests read some repo files by path, e.g. REPO_ROOT / "docs" / "specs" / "x.md"
        # or REPO_ROOT / ".github" / "workflows" / "ci.yml". Each such file must be in pytest_paths,
        # or quick mode would skip pytest when only that file changed.
        part = r'"[A-Za-z0-9_.-]+"'
        chain = re.compile(rf"REPO_ROOT((?:\s*/\s*{part})+)")
        found = set()
        for test in sorted((REPO / "tools" / "mapgen" / "tests").glob("*.py")):
            for match in chain.finditer(test.read_text(encoding="utf-8")):
                rel = "/".join(re.findall(r'"([^"]+)"', match.group(1)))
                if (REPO / rel).is_file() and not rel.startswith("tools/mapgen/"):
                    found.add(rel)
        self.assertTrue(found, "the scan found no repo file read by the mapgen tests")
        globs = real_map().pytest_paths
        missing = [rel for rel in sorted(found) if not any(ts.glob_match(g, rel) for g in globs)]
        self.assertEqual(missing, [])


if __name__ == "__main__":
    unittest.main()
