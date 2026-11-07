# Test performance

## Status

Approved

## Goal

Get faster feedback from the engine test suite without losing coverage. The gdUnit4 run takes 884 s (14 m 44 s) today. The time comes mostly from loading the full Somero pack again for each test. This feature shortens the full run where that is safe, and adds a quick verify mode that runs only the gdUnit4 groups the changed files affect. The full `/verify` stays the gate before every commit, and CI keeps running the full suite, so a wrong mapping can only cost time and can't hide a regression.

## Context

- The engine tests are 19 files in `game/tests/`, with 13 suites that load a map. Each suite is one `[TestSuite]` class. They run in one Godot process (`.runsettings`: `MaxCpuCount` 1, `TestSessionTimeout` 1 200 000 ms; `verify.sh`: `GDUNIT_TIMEOUT` 1200 s). The 884 s run uses 74 % of that limit.
- The slowest classes, as the user measured them: `PlayerOnFootTest` 257 s, `WorldEdgeTest` 175 s, `SaveLoadTest` 86 s, `WorldEdgeBreakingTest` 83 s, `PlaceInformationTest` 66 s. The full per-class list is measured again at the start (criterion 1) and recorded under "Measurements".
- Most map tests build a new `World` per test with `ISceneRunner.Load(NewWorld(seed), true)`. The `Main` suites build a new `Main` per test through `MainTestSupport.BeginTest`/`TearDown`, and each game start loads the map (`GameStartFrames` 900).
- Every suite loads `res://maps/somero/`, whichever pack is there (`MapLoader.PackPath`). Locally that is the full pack (about 100 MB). CI builds the test-area pack from the committed fixture into the same folder (`mapgen build somero --area test`; ADR 0010).
- `MapLoader` loads tiles with `ResourceLoader.Load<PackedScene>` (`scripts/world/MapLoader.cs`). So a pack has to be inside the Godot project (`res://`), where Godot imports its `.glb` files. A folder outside `game/` can't be loaded this way.
- Some checks run only on the full pack. On a test-area pack they print a skip or N/A line and pass. Examples: `MapLoaderTest` `IsFullPack` (the road 52 ledge, the lake past B's west notch), the `world-edge` preset check, `PlayerOnFootTest` Ac14 (bridge railing) and Ac15 (bridge route, which must exist on the full pack), and the area-dependent parts of `WorldEdgeTest`. If one of these moves to the test-area pack, it silently stops checking anything.
- `verify.sh` runs every step, each step in full, and always exits 0. `/implement` runs the full `/verify` before it commits. CI (`ci.yml`) runs the whole gdUnit4 suite on the test-area pack.

## Behaviour

Nothing changes for the player. The changes are test code, a test-only override in `MapLoader`, scripts, the CI workflow and docs.

### Part 1: faster full suite

1. **Baseline first.** Before any test changes, run the full gdUnit4 suite on the full pack. Record the total time and each suite's time under "Measurements". Take the CI baseline from the GitHub Actions logs of a recent run on `main`: the gdUnit4 step and the whole `test` job. The after-measurements (criteria 12 and 13) use the same machine, pack and command.

2. **Shared instances.** A suite (or a group of tests within a suite) can load one `World` or `Main` once and reuse it across tests, when those tests don't need a fresh one. Rules:
   - **Reset before each test.** Every test that reuses an instance starts from a documented reset state. That state covers the player's position, velocity, input flags and run toggle, the cameras (player camera current, `InputEnabled`, no debug camera), `PlayerState` (`Reset()`), `Death.State` `Alive`, barriers restored (`RestoreAll()`) and no loose pieces, `VoidFall` and tracked bodies cleared, the pause state (`GetTree().Paused` false), every injected input released, `DebugBuild.Override` null, and the project settings and engine state that `MainTestSupport` already restores. Each suite that shares an instance has one reset helper, and that helper asserts the reset state, so a reset that fails fails the next test loudly instead of letting it pass on leftover state.
   - **Fresh instance where needed.** A test that depends on the first load or first spawn gets its own instance, as now. That includes spawn order and seed repeatability, the `Player: spawned at` line, `--give` at first placement, load placement, `MapLoaded`, reloads, the death sequence from its start where the start matters, spawn failure, temporary packs, `World.Seed`, and exports read only in `_Ready` (`SpaceSkyEnabled`) or at `Load` (`WorldEdgeEnabled`). So does any test whose code says it needs a fresh `World`/`Main`. Sharing is opt-in per test, not the default.
   - **Order independence.** Each test passes when it runs alone, when its suite runs alone, and in the full suite (criterion 4).
   - **Teardown.** The shared instance is freed after the last test that uses it (suite-level `[After]`, or the gdUnit4Net equivalent), including after a failed test, so no shared instance leaks into the next suite.
   - **Assertions stay.** Shared setup removes repeated loads only. It never removes, weakens or merges an assertion. Tolerances, frame budgets and wait limits stay as they are, unless a change is justified from measured data in the test's comment, never from a guessed range.

3. **Local test-area pack.**
   - **Location.** The pack lives at `res://tests/packs/somero-test/` (`game/tests/packs/somero-test/`). That folder is outside `game/maps/` and inside the Godot project, so Godot imports its `.glb` tiles and `MapLoader` loads them the same way it loads every pack. The existing `tests/*` export filter keeps it out of both exports. The folder is git-ignored, like `game/maps/`. The verify "exported build" step lists packs from `game/maps/*/` only, so it never sees this one.
   - **Map id.** The pack's map id stays `somero` (manifest, `road_graph.json`, `pois.json`). Only the folder differs.
   - **Override.** `MapLoader` gets a test-only pack-folder override, a static property set by tests and reset to null afterwards, in the same way as `DebugBuild.Override`. When it is set, `PackPath` is that folder and the map id is unchanged. Everything that reads `PackPath` (tiles, road graph, places, boundary, the debug orthophoto folder) follows it. Without the override nothing changes. The game never sets it. Its name and shape are decided in the plan.
   - **Build.** The pack is built offline from the committed fixture `tools/mapgen/tests/fixtures/raw/somero-test-area/` (the same data as CI) with `mapgen build somero --area test`, written to the folder above. The local raw data in `tools/mapgen/data/raw/somero/` is never read, replaced or changed for this. How the pipeline is pointed at the fixture (a new `mapgen build` option for the raw folder, or a script around the existing CLI) is decided in the plan. Either way it is generic and has no map-specific code. A script in `.claude/scripts/` builds the pack and runs the headless import, and `/regen-map` and `verify.sh` say how to rebuild it.
   - **Which tests use it.** A test that doesn't need the full map can use the test-area pack. A test that has a full-pack-only branch (`IsFullPack`, `Manifest.Area == "full"`, a "must exist on the full pack" assertion, an N/A path for the test area) stays on the full pack. So does any test whose spec criterion names something only the full pack has (boundary B, the road 52 ledge, Kirkkojärvi, the bridge routes, the `world-edge` preset). A test moved to the test-area pack takes every position and object it uses from the pack it loads, as all suites do today.
   - **Coverage.** The checks a full local run actually executes don't shrink. Every "skipped …", "N/A" and similar line that the full local run prints after the change was already printed by the baseline run, for the same test (criterion 6).
   - **Missing pack.** A test that wants the test-area pack and doesn't find it fails with a message naming the build script. It never falls back to the full pack.
   - **CI.** CI builds the test-area pack from the fixture into both `game/maps/somero/` (as now) and the test-pack folder, so every test finds its pack there too.

4. **No change to what counts as done.** `/verify` (no options) runs the whole gdUnit4 suite, the export check and the screenshots, as now. CI runs the whole suite, as now.

### Part 2: test groups and the quick verify mode

5. **Groups.** Each gdUnit4 suite belongs to exactly one feature group. The groups, their suites and the path rules are in `game/tests/test-groups.json`, which `verify.sh` reads (through `python3 -I`, as `camera_preset_names` reads presets). First grouping:

   | Group | Suites | Spec |
   | --- | --- | --- |
   | `map` | `MapLoaderTest`, `WorldSceneTest`, `OrthophotoDebugViewTest` | map-loading |
   | `roads` | `RoadGraphTest` | roads-and-road-graph |
   | `places` | `PlaceInformationTest` | place-information |
   | `player` | `PlayerOnFootTest` | player-on-foot |
   | `world-edge` | `WorldEdgeTest`, `WorldEdgeBreakingTest`, `DeathScreenLayoutTest` | world-edge-game |
   | `saves` | `MainMenuTest`, `PauseTest`, `SaveLoadTest`, `SettingsViewTest` | menu-settings-save |
   | `inventory` | `InventoryTest` | inventory |

   After the after-measurement, a group that is much slower than the others may be split or rebalanced, and the change is recorded under "Measurements". The selection check fails (criterion 7) when a suite is in no group or in more than one, or when a group names a suite that doesn't exist.

6. **Path rules.** Each rule maps a repo path glob to one or more groups, or to one of two special targets:
   - **`all`** (the full suite) for shared code: `game/scripts/world/World.cs`, `game/scripts/world/MapLoader.cs`, `game/scripts/main/Main.cs`, `game/scripts/autoload/**`, `game/project.godot`, `game/*.csproj`, `game/Directory.Build.*`, `global.json`, `game/.runsettings`, `game/tests/MainTestSupport.cs`, `game/tests/ErrorCapture.cs`, `game/tests/test-groups.json`, `.claude/scripts/verify.sh`, `.claude/scripts/lib.sh`, `tools/mapgen/**` and `maps/**` (a pack change after `/regen-map`). The plan completes this list from the code.
   - **`none`** for documentation: `docs/**` and `**/*.md`. In quick mode these select no gdUnit4 test (and quick mode runs no screenshots in any case).
   - `game/scripts/player/Player.cs` and `game/scripts/player/PlayerCamera.cs` → `all` (changed 2026-11-07 after review: every map suite spawns or checks the player, so they would select most groups).
   - `Roadman.Core`: `Items/**` → `inventory`, `saves`, `world-edge`; `Edge/**` → `world-edge`, `saves`, `inventory`; `Saves/**` → `saves`; `Player/**` → `player`, `world-edge`, `inventory` (widened 2026-11-07 after review to every group whose suites use them). Every other `Roadman.Core` path (`PlayerState.cs`, `Maps/`, `IO/`, `Settings/`, `Tools/`, the project file) → `all`. `Roadman.Core` is covered by xUnit in every mode either way.
   - A changed suite file `game/tests/<Suite>.cs` selects that suite's group. A helper file used by more than one suite (for example `WorldEdgeTest.NewWorld`, used elsewhere) maps to all their groups, or to `all`.
   - **Fail-safe.** A changed path that matches no rule selects `all`. A deleted or renamed file counts under both its old and new path. When the result includes `all`, the full suite runs. When it is only `none`, no gdUnit4 test runs.

7. **Changed files.** By default the quick mode diffs the working tree against `HEAD`: staged and unstaged tracked changes plus untracked files that aren't ignored. An optional base argument (a commit, branch or tag) diffs the working tree against that base instead, still with untracked files. Git-ignored files (`game/maps/`, the test pack, `tools/mapgen/data/raw/`) don't appear. A pack regenerated with no source change is therefore invisible to the selection. The quick output says this, and the full `/verify` covers it.

8. **Quick mode.** `verify.sh --quick [<base>]`, and a `/verify-quick` slash command (`.claude/commands/verify-quick.md`) that calls it. Claude may run it on its own, as it may `/verify`. Quick mode:
   - runs these steps exactly as the full mode does: dotnet build, `dotnet test` for `Roadman.Core`, `dotnet format`, the release check, pytest, ruff check, ruff format check, the headless import and headless startup.
   - skips the export check and the screenshots, and reports each as N/A ("quick mode; the full /verify runs it").
   - computes the groups (6, 7) and runs only their suites in one `dotnet test` call. When the result is `all`, it runs the full suite and says which path caused that. When the result is only `none`, it reports the gdUnit4 step as N/A ("no changed file affects a gdUnit4 group"), not PASS.
   - names the step with its groups (for example `gdunit4 tests (quick: player, world-edge)`) and lists the reason for each group (the first changed path that selected it).
   - ends with a line saying that a quick run doesn't meet the definition of done and that a full `/verify` is needed before a commit.
   - always exits 0, like the full mode.

9. **Choosing suites.** The first step of implementation is a small trial: run `dotnet test game/RoadmanSimulator.csproj --settings game/.runsettings --filter <class filter>` with gdUnit4.test.adapter 3.2.0 on two small suites. Check that only those suites run, and that `TreatNoTestsAsError` behaves sensibly. If class filtering works, quick mode uses it. If it doesn't, quick mode uses gdUnit4's own test selection instead. If that would need anything new (a package, a package upgrade, an add-on change), the implementer stops and asks the user first. The trial's result goes under "Measurements".

10. **Dry run.** `verify.sh --quick --dry-run [<base>]` prints the changed paths, the rule each one matched, and the resulting groups and suites, and runs nothing.

11. **Commits and CI.** The rule is written, not enforced by a hook. `/implement` already runs the full `/verify` before it commits. The root `CLAUDE.md`, `.claude/commands/verify.md` and the new `verify-quick.md` say that quick mode is for iterating and that commits need the full `/verify`. CI keeps running the full suite.

12. **Docs.** Update the root `CLAUDE.md` (commands table with `/verify-quick`, the `/verify` description, the permissions note if `.claude/settings.json` changes) and `game/CLAUDE.md` (commands, the test-pack build, and a "Tests" section on shared instances, the reset helper, the pack override and which tests use which pack). Also update `.claude/commands/verify.md`, `docs/architecture.md` "Testing", and add a new ADR for the test selection, fail-safe rule, test-area pack location and shared-instance rules.

### Edge cases

- **Detached HEAD, a missing base, or an unreadable git state.** The selection can't compute a diff, so it runs `all` and says why.
- **A shared-instance test fails mid-way.** The next test's reset assertion either restores the state or fails with a message naming the leftover state. The suite-level teardown still frees the instance.
- **Full pack absent locally** (fresh clone before `/regen-map`). Full-pack tests fail as now. Quick mode doesn't change that.
- **Test pack stale.** The fixture or pipeline changed, but the test pack wasn't rebuilt. `tools/mapgen/**` maps to `all`, so the full suite runs. Rebuilding the pack is still a manual step, and the build script's hint says so. Whether the verify scripts should rebuild it when it is older than the fixture or pipeline is decided in the plan.
- **Session timeout.** This feature doesn't change `GDUNIT_TIMEOUT` or `TestSessionTimeout`.

## Acceptance criteria

1. **Baseline recorded.** Before any test change, the full gdUnit4 suite runs on the full Somero pack. The total wall time and each suite's time are recorded under "Measurements", with the machine, commit, Godot version and command. The suite times come from the test run's output (a TRX from `--logger trx`, or the gdUnit4 log), not estimates. The CI baseline (gdUnit4 step and whole `test` job) is recorded from the GitHub Actions log of a named run.
2. **Full suite still green.** After the change, the full `/verify` reports PASS for every step that passed in the baseline, on the full pack.
3. **No test removed or weakened.** The number of test cases per suite is the same as in the baseline, or any difference is listed and justified in the commit. The diff removes no assertion and loosens no tolerance, frame budget or wait limit without a measured justification in the test's comment. The reviewer checks this against the diff.
4. **Order independence.** Each suite that shares an instance passes when run alone through the quick mode's selection. Each of its tests passes when run alone through a single-test selection.
5. **Reset is checked.** Every shared-instance suite has a reset helper that asserts the reset state from Behaviour 2. A gdUnit4 test leaves state behind on purpose (for example: player moved, death sequence started, a barrier broken, the tree paused, an input pressed), calls the helper, and checks the reset state. A deliberately broken reset makes that test fail.
6. **Coverage kept on the full pack.** The set of "skipped …" and "N/A" lines that the full local run prints (per test) after the change is a subset of the baseline's set. A script that compares the two logs shows this.
7. **Group map is complete.** A check run by `/verify` (both modes) fails in any of these cases: a `[TestSuite]` class in `game/tests/` is in no group or in more than one; `test-groups.json` names a suite that doesn't exist; a helper file used by more than one suite isn't mapped to all their groups or to `all`; a rule names a group that doesn't exist.
8. **Selection rules.** Fixed inputs to the selection give the expected result:
   - one suite file changed → its group;
   - `World.cs` → `all`;
   - `PlayerState.cs` → `all`;
   - `Player.cs` → `all`;
   - `Roadman.Core/Items/x.cs` → `inventory`, `saves`, `world-edge`;
   - `Roadman.Core/Player/x.cs` → `player`, `world-edge`, `inventory`;
   - `Roadman.Core/Maps/x.cs` → `all`;
   - `docs/specs/x.md` alone → nothing;
   - `docs/specs/x.md` with a suite file → that suite's group;
   - an unmapped path → `all`;
   - an untracked new file under a group's path → that group;
   - a rename across groups → both;
   - an unknown base → `all`, with the reason printed.
9. **Test-area pack.** The build script makes `game/tests/packs/somero-test/` from the fixture without touching `tools/mapgen/data/raw/somero/` (its file list and modification times are unchanged). The pack's manifest says map id `somero` and area `test`. A Windows export made after the build contains no file from `tests/packs/` (`check-export.sh` or a listing of the PCK). A gdUnit4 test sets the override, loads the pack and gets every manifest tile, the road graph, places and boundary from that folder. With the override null, `PackPath` is `res://maps/somero` again.
10. **Selection trial.** The class-filter trial (Behaviour 9) is run and its result recorded before the quick mode is built. No new NuGet package, Python package, GitHub Action or add-on is added without the user's approval.
11. **Quick mode runs only the selected suites.** For one fixed changed path under `game/scripts/world/edge/`, the quick mode's gdUnit4 log contains only the `world-edge` group's suites. Its summary names the group and the reason. The export check and screenshots are N/A ("quick mode"). The other steps listed in Behaviour 8 run the same commands as the full mode.
12. **Quick mode says it isn't the gate.** The quick summary ends with the line saying a full `/verify` is needed before a commit. `/implement`, `verify.md`, `verify-quick.md` and the root `CLAUDE.md` say that commits need the full `/verify`.
13. **Targets.** On the baseline machine:
    - the full gdUnit4 suite on the full pack takes under 480 s (8 minutes);
    - `verify.sh --quick`, with a change that selects one group, takes under 180 s (3 minutes) wall time in total. This is measured once for each group, with the import cache warm and the build up to date.

    Before and after numbers (total, per suite, per group) are recorded together under "Measurements".
14. **CI.** `ci.yml` still runs the whole gdUnit4 suite (on the test-area pack, with the test pack built as in Behaviour 3), and that run passes. The CI after-times (gdUnit4 step and whole `test` job) are recorded from the GitHub Actions log of a named run, beside the baseline.
15. **ADR and docs.** A new ADR records the grouping, the fail-safe selection rule, the full-`/verify`-before-commit rule, the test-area pack location and override, and the shared-instance rules. The docs listed in Behaviour 12 are updated.

## Test ideas

| Criterion | Layer |
| --- | --- |
| 1, 13, 14 | Timing runs: `dotnet test … --logger trx` (built into the test SDK), with per-suite times read from the TRX by a small script; quick-mode wall time per group; CI times from the Actions logs. Not part of `/verify`. |
| 2 | Full `/verify`. |
| 3 | Count of `[TestCase]` per suite before and after (script); `reviewer` subagent on the diff. |
| 4 | gdUnit4 through the quick selection per suite, and one run per test (scripted, not part of `/verify`). |
| 5 | gdUnit4: one test per shared-instance suite that dirties the state, runs the reset helper and asserts the reset state, plus a negative case. |
| 6 | A script comparing the skip and N/A lines of the baseline and after logs. |
| 7, 8 | Tests of the selection logic over fixed path lists and git states, plus the map completeness check. Language and home (a script in `.claude/scripts/` with a self-test) are decided in the plan, since `tools/mapgen/` holds the map pipeline. |
| 9 | Build-script run with a before/after listing of `data/raw/somero/`; gdUnit4 test of the override; export listing. |
| 10 | The trial run, recorded under "Measurements". |
| 11, 12 | One quick-mode run on a scratch change; check the log and summary. |
| 15 | Review. |

## Measurements

### Baseline (criterion 1)

Full gdUnit4 suite on the full Somero pack, before any test change: **464 s wall** (`dotnet test` reported 7 m 39 s), 185 cases, all passed. Suite times are the summed per-test durations from the TRX (`python3 -I .claude/scripts/gdunit-report.py suites`).

| Suite | Cases | Time (s) |
| --- | --- | --- |
| PlayerOnFootTest | 43 | 148.2 |
| WorldEdgeTest | 27 | 94.5 |
| WorldEdgeBreakingTest | 12 | 52.4 |
| SaveLoadTest | 12 | 40.4 |
| PlaceInformationTest | 14 | 27.1 |
| PauseTest | 6 | 20.9 |
| MapLoaderTest | 12 | 17.6 |
| MainMenuTest | 17 | 14.5 |
| InventoryTest | 7 | 13.9 |
| RoadGraphTest | 8 | 13.1 |
| SettingsViewTest | 16 | 12.4 |
| WorldSceneTest | 2 | 3.6 |
| OrthophotoDebugViewTest | 8 | 0.1 |
| DeathScreenLayoutTest | 1 | 0.1 |
| **Total** | 185 | 458.7 (464 s wall) |

**Setup.**
- Machine: Apple M4 Pro, macOS 15 (Darwin 24.6.0).
- Commit: `75b7819`.
- Godot: 4.7.2.stable.mono.official.
- Command: `GODOT_BIN=/Applications/Godot_mono.app/Contents/MacOS/Godot dotnet test game/RoadmanSimulator.csproj --settings game/.runsettings --logger "trx;LogFileName=baseline.trx"`.
- The build and import were up to date.
- Four orphaned gdUnit4 runner Godot processes from an earlier session (about 1 % CPU each) were running during both this run and the after run.

**The earlier 884 s figure.** It didn't reproduce: the same suite took 464 s here. It was most likely measured while other processes were running, such as an editor, a game or stuck test hosts. `verify.sh` now reports a WARN line ("other godot processes") when another Godot process runs before the gdUnit4 step.

**CI baseline.** Run [42293550026](https://github.com/CatastrophicCoder/roadman-simulator/actions/runs/42293550026) (`26d45f9`, test-area pack):
- gdUnit4 step: 210 s (21:07:31 to 21:11:01)
- whole `test` job: 449 s (21:03:42 to 21:11:11)
- building the test-area pack: 3 s

### Selection trial (criterion 10)

With gdUnit4.test.adapter 3.2.0:
- `dotnet test game/RoadmanSimulator.csproj --settings game/.runsettings --filter "FullyQualifiedName~Roadman.Tests.WorldSceneTest.|FullyQualifiedName~Roadman.Tests.DeathScreenLayoutTest."` ran exactly those suites' 3 cases (2 + 1) in 8 s wall, exit 0.
- A filter that matches nothing (`FullyQualifiedName~Roadman.Tests.NoSuchSuite.`) exits 1 with "No test matches the given testcase filter" (`TreatNoTestsAsError`). A group naming a missing suite therefore fails loudly.

Quick mode uses the class filter, with the trailing dot so `WorldEdgeTest.` doesn't match `WorldEdgeBreakingTest`. Nothing new was needed.

### Scope decisions during implementation (2026-11-07)

- **No shared instances.** The baseline was already under the 480 s target, and most of the time goes to long movement runs, not loads. So the user chose the test-area pack only. Behaviour 2's rules stay in ADR 0020 for a later suite. **Criterion 5 doesn't apply**: no suite shares an instance, so no reset helper exists. Criterion 4 is met trivially: every test still builds its own instance.
- **pytest in quick mode** (a deviation from Behaviour 8). The mapgen pytest suite alone takes about 167 s, which can't fit a 180 s quick run. Quick mode runs pytest only when a changed path is a pytest input, listed in `pytest_paths` in `game/tests/test-groups.json`:
  - `tools/mapgen/**`, `maps/**`, `.github/**`, `docs/specs/place-information.md`, `docs/specs/roads-and-road-graph.md`;
  - or on any fail-safe (a path matching no rule, a git state it can't read).

  Otherwise pytest is N/A ("quick mode; no pytest input changed"). Ruff always runs.
- **Wider rules** (review, 2026-11-07). Rules now select every group whose suites use the code: `Player.cs`/`PlayerCamera.cs` → `all`, and `Roadman.Core` `Items/`, `Edge/` and `Player/` widened as in Behaviour 6. `test_select.py check` also flags a script or `Roadman.Core` file whose rule misses the group of a suite that references one of its types. The reviewer checklist covers uses the check can't see, such as code reached only through `World`.
- **Stale test pack.** `/verify` and `/verify-quick` rebuild it ("test pack" step) when it is missing or its stamp (fixture, pipeline source and lock, `maps/somero/`) has changed.

### After (criteria 3, 6, 13)

Same machine, pack and command, with the test-area pack moves (commit of this feature), after the orphaned runner processes were stopped and no other Godot process ran: **254 s wall** (`dotnet test` 4 m 8 s), 186 cases, all passed. An earlier after run with the orphans still running took 253 s, so they didn't measurably slow the suite. Full `/verify` on the full pack: every step PASS, no WARN (criterion 2).

| Suite | Cases before / after | Before (s) | After (s) |
| --- | --- | --- | --- |
| PlayerOnFootTest | 43 / 43 | 148.2 | 94.8 |
| WorldEdgeTest | 27 / 27 | 94.5 | 53.1 |
| WorldEdgeBreakingTest | 12 / 12 | 52.4 | 40.1 |
| SaveLoadTest | 12 / 12 | 40.4 | 11.4 |
| PlaceInformationTest | 14 / 14 | 27.1 | 1.3 |
| PauseTest | 6 / 6 | 20.9 | 10.7 |
| MapLoaderTest | 12 / 13 | 17.6 | 17.1 |
| MainMenuTest | 17 / 17 | 14.5 | 1.8 |
| InventoryTest | 7 / 7 | 13.9 | 10.1 |
| RoadGraphTest | 8 / 8 | 13.1 | 0.5 |
| SettingsViewTest | 16 / 16 | 12.4 | 3.4 |
| WorldSceneTest | 2 / 2 | 3.6 | 3.6 |
| OrthophotoDebugViewTest | 8 / 8 | 0.1 | 0.1 |
| DeathScreenLayoutTest | 1 / 1 | 0.1 | 0.1 |
| **Total** (target < 480 s) | 185 / 186 | 458.7 (464 s wall) | 248.2 (254 s wall) |

- **Case counts (criterion 3).** Unchanged except MapLoaderTest, which gained the pack override test of criterion 9 (`PackFolderOverride_LoadsTheWholeTestAreaPackFromItsFolder_AndNullGivesTheMapsFolderAgain`).
- **Test-area pack build (criterion 9).** The fingerprint of `tools/mapgen/data/raw/somero/` (`find … -type f -exec stat -f '%m %N' {} + | sort | shasum`, file list and modification times) was `85611c70e30948beb4899bcb3a7870bf5afea8ad` before and after the build. The built manifest says `map_id` `somero`, `area` `test`, 4 tiles. The exported Windows build's PCK lists no file from `tests/`: `check-export.sh` fails on any, and the full `/verify` "exported build" step passes with the pack in place. The override is checked by MapLoaderTest `PackFolderOverride_LoadsTheWholeTestAreaPackFromItsFolder_AndNullGivesTheMapsFolderAgain`.
- **Skip and N/A lines (criterion 6).** `gdunit-report.py compare baseline.trx after.trx` finds 0 skip or N/A lines in either run, so the after set is a subset. The ledge search's information line ("... midpoint(s) before it skipped near a ring corner") isn't a skipped check, so the script doesn't count it.

| Group | Quick-mode wall time (s) (target < 180 s) |
| --- | --- |
| *(measured after the commit, with a scratch change per group; see the follow-up)* | |

CI after (run link, gdUnit4 step, whole `test` job): _after the push_

## Open questions

None left. The user answered all of them on 2026-11-07:

- Baseline measured fresh.
- Test-area pack outside `game/maps/`, never exported, map id `somero`, chosen through a pack-folder override, built from the committed fixture. The example location `build/packs/` is outside the Godot project, where `MapLoader` can't load imported tiles, so the pack is in `game/tests/packs/somero-test/`. That keeps both requirements: outside `game/maps/` and excluded from export.
- Diff against `HEAD` with untracked files, plus an optional base.
- Docs-only changes run no gdUnit4 tests and no screenshots.
- Quick mode skips the export check and the screenshots.
- `--quick` on `verify.sh`, plus `/verify-quick`.
- The commit rule is written, not enforced.
- The group map is `game/tests/test-groups.json`.
- The filter trial comes first, with gdUnit4's own selection as the fallback, and nothing is added without asking.
- Targets: 8 minutes for the full suite, 3 minutes for quick mode.
- The grouping is accepted, and may be adjusted after measuring.
- The `Player.cs`/`PlayerCamera.cs` and `Roadman.Core` mappings are as above.
- CI timing is recorded before and after.
