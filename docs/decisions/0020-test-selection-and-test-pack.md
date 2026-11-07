# 0020: Test selection, quick verify and the test-area pack

Status: Accepted

## Context

The gdUnit4 suite loads the full Somero pack (about 100 MB) in most tests, and the whole suite runs on every `/verify`. A change to one feature waits for every group. CI already runs the suite on the test-area pack built from the committed fixture (ADR 0010). Spec: `docs/specs/test-performance.md` (decided 2026-11-07).

## Decision

- **Groups.** Each gdUnit4 `[TestSuite]` belongs to exactly one feature group. `game/tests/test-groups.json` holds the groups, an ordered list of path rules (first match wins; targets are groups, `all` or `none`) and `pytest_paths`, the inputs of the mapgen pytest suite.
- **Fail-safe selection.** `.claude/scripts/test_select.py` (standard library only, run with `python3 -I`) takes the changed paths: the working tree against `HEAD`, or against a given base, plus untracked files. A rename counts under both paths. The selection falls back to the full suite in these cases:
  - a path matches no rule;
  - a rule says `all` (shared code: `World`, `MapLoader`, `Main`, autoloads, the project and build files, shared test helpers, the verify scripts, the pipeline and `maps/`);
  - git can't give a diff (no `HEAD`, detached `HEAD`, an unknown base, an unreadable repo).

  Docs alone select no gdUnit4 test. A wrong mapping can therefore only cost time, and the full `/verify` and CI never use the selection.
- **Group map checked.** `/verify` (both modes) runs the selection's self-tests and `test_select.py check`. The check fails when:
  - a suite is in no group or in two;
  - a group names a missing suite;
  - a rule names an unknown group;
  - a helper file reached from a suite (by type name, directly or through another helper) isn't mapped to that suite's group or to `all`;
  - a file under `game/scripts/` or `game/Roadman.Core/` has a type that a suite (or a helper it reaches) references, and its rule names groups without that suite's group.

  A rule selects every group whose suites use the code. A path that would select most groups maps to `all`. The reviewer also checks uses the scan can't see, such as code reached only through `World`.
- **Quick verify.** `verify.sh --quick [<base>]` (`/verify-quick`) runs these steps as the full mode does: the build, xUnit, format, the release check, ruff, the import and the headless startup. It also runs:
  - **gdUnit4:** only the selected suites, through `dotnet test --filter` on the class names;
  - **pytest:** only when a changed path is a pytest input, or on a fail-safe. This deviates from the spec, because pytest alone takes about 170 s.

  It skips the export check and the screenshots. `--dry-run` prints the selection and runs nothing.
- **Full `/verify` before every commit.** Quick mode is for iterating and says so in its summary. The rule is written, in `/implement`, `verify.md`, `verify-quick.md` and the root `CLAUDE.md`, not enforced by a hook. CI keeps running the whole suite.
- **Test-area pack in the project.** The tests' test-area pack is in `game/tests/packs/somero-test/` (git-ignored). It is inside the Godot project, so its `.glb` tiles are imported and `MapLoader` loads them as it loads any pack. It is outside `game/maps/`, so the export check never treats it as a game pack, and the `tests/*` export filter keeps it out of both exports.
  - It has the map id `somero`.
  - `.claude/scripts/build-test-pack.sh` builds it from the committed fixture with `mapgen build somero --area test --raw <fixture> --out <pack>`. It never reads or changes `tools/mapgen/data/raw/`.
  - A stamp (a hash of the fixture, the pipeline source and lock, and `maps/somero/`) marks a stale pack. `/verify` rebuilds a missing or stale pack in both modes.
  - CI builds it beside `game/maps/somero/`.
- **Pack override.** `MapLoader.PackFolderOverride`, a static property that only tests set (and reset to null), replaces the pack folder for every pack read and keeps the map id. Tests choose a pack with `TestPacks.UseTestArea()` or `TestPacks.UseFullPack()`. A test that wants the test pack and doesn't find it fails and names the build script; it never falls back to the full pack.
- **Which tests use which pack.** These tests stay on the full pack:
  - a test with a full-pack-only branch (`IsFullPack`, `Area == "full"`, `AssertNotFullPack`, an N/A path for the test area);
  - a test whose criterion names something only the full pack has.

  Every other map test may use the test pack. The skip and N/A lines of a full local run never grow.
- **Shared instances.** The spec allows a suite to reuse one `World` or `Main` across tests with a checked reset. They weren't adopted, because the baseline was already under the 8-minute target. If a suite adopts them later, these rules apply:
  - sharing is opt-in per test;
  - one reset helper per suite, which asserts the reset state;
  - fresh instances for anything that depends on the first load or spawn;
  - teardown in the suite-level `[After]`;
  - no assertion removed or weakened.

## Consequences

- A new suite needs a group in `test-groups.json`, and a new shared helper needs a rule, or the check fails.
- A new folder under `game/scripts/` without a rule selects the full suite until it gets one.
- A regenerated pack with no source change is invisible to quick mode. The full `/verify` covers it.
- Tests that load the test pack run against 4 tiles instead of 100 and no longer exercise the full pack's size. The full-pack-only tests and the screenshots still use the full pack.
