# Godot project (game)

Godot 4 .NET project `RoadmanSimulator` (Forward+ renderer, Jolt physics), scripted in C#. See `docs/architecture.md` and ADR 0001.

## Layout

- `Roadman.Core/`: engine-free C# logic (road graph, routing, wanted level, missions, death and respawn). No Godot references. Tests in `Roadman.Core.Tests/` (xUnit).
- `scenes/`, `scripts/`: Godot scenes and node scripts. `scripts/autoload/` holds `Events` (signal bus) and `GameState` (owns `PlayerState`).
- `tests/`: engine-dependent tests (gdUnit4Net). Built in `Debug` only; export builds contain no test code.
- `addons/`: gdUnit4, and Terrain3D if ADR 0004 chooses it.
- `maps/<map-id>/`: generated map packs. Never edit them by hand; they are git-ignored.

## Conventions

- All projects target `net10.0` (ADR 0007); `global.json` pins the SDK.
- Nullable reference types are on in every project.
- Test packages (gdUnit4Net, `Microsoft.NET.Test.Sdk`) are referenced only in `Debug`. `Directory.Build.targets` keeps the game assembly a library.
- Declare Godot signals with `[Signal]` delegates.
- Logic that doesn't need the engine goes in `Roadman.Core`, with tests.
- Keep scenes small, and prefer building node trees in code. Hand-merged `.tscn` edits break easily.
- Use `[Export]` on anything that will be tuned.
- Autoloads (`Events`, `GameState`) stay small.
- Keep `.import` files in Git.
- Missions refer to marker ids, never to raw coordinates.

## Commands

Run these from the repo root.

```sh
dotnet build game/RoadmanSimulator.sln                       # all projects, Debug
dotnet test game/Roadman.Core.Tests                          # Roadman.Core unit tests (xUnit)
godot --headless --path game --import                        # needs a wrapper, not a symlink; see "Godot binary" in the root CLAUDE.md
GODOT_BIN=/Applications/Godot_mono.app/Contents/MacOS/Godot \
  dotnet test game/RoadmanSimulator.csproj --settings game/.runsettings  # gdUnit4 tests in tests/; run the import first
dotnet format game/RoadmanSimulator.sln --verify-no-changes  # format check
godot --headless --path game --quit-after 120                # runs the main scene (World.tscn) headless
dotnet build game/RoadmanSimulator.csproj -c ExportRelease   # release assembly, no test code; output in game/.godot/mono/temp/bin/ExportRelease/
godot --path game --resolution 1600x900 -- --screenshot overview build/screenshots/overview.png --map somero  # screenshot of a camera preset; windowed, see "Screenshots"
```

`GODOT_BIN` must point at the real Godot .NET binary, not a symlink. The gdUnit4 adapter writes its runner scene to `game/gdunit4_testadapter_v*/` on each run (git-ignored).
Building with `-c ExportRelease` or `ExportDebug` re-restores the shared `obj/` without test packages; the next Debug `dotnet build` or `dotnet test` restores them again, so don't pass `--no-restore` after switching configuration.

## Map loading

- `World` adds a `MapLoader` (`scripts/world/MapLoader.cs`), which reads `res://maps/<id>/manifest.json` and instances every tile under `MapLoader/Tiles`. The id comes from `MapLoader.MapId` or the project setting `roadman/map/id` (default `somero`). Errors name the pack path and say to run `/regen-map <id>`.
- Terrain collision bodies are in the group `map_terrain`. Use `MapLoader.TerrainHeightAt(x, z)` for ground heights; never hard-code them (Somero's terrain is about 81 to 138 m). New collision bodies are queryable only after a physics frame: await `MapLoader.WaitForTerrainCollisionAsync()` first.
- `[importer_defaults]` in `project.godot` sets every scene import to name suffixes on, mesh compression off, LODs off, shadow meshes on and static light baking. A hand-made `.glb` added later gets these too; correct its import settings and commit its `.import` file.

## Screenshots

`godot --path game --resolution 1600x900 -- --screenshot <preset> <out.png> [--presets <file>] [--map <map-id>]`, run from the repo root (`/verify` runs it for every preset in the pack, with `--map "$MAP_ID"`).

- `--map <map-id>` renders that pack instead of the one in the `roadman/map/id` project setting: the tool sets `MapLoader.MapId` before the map loads. The id must be a lowercase slug (the pipeline's `SLUG_PATTERN`).
- Presets come from the rendered pack's `camera_presets.json`, or from `--presets <file>` (handy for trying a preset before it is in the pipeline). The reader follows the pipeline's rules: unknown or repeated keys, an empty `presets`, duplicate or non-slug preset names and `null` for `fov` or `ground_relative` are errors. A relative output or presets path resolves against the shell's working directory (`$PWD`), not `res://`: Godot changes into the project folder for `--path`. Where `PWD` isn't set (Windows `cmd`, PowerShell), pass absolute paths.
- The image comes from its own 1600x900 `SubViewport` (`ScreenshotTool.ImageSize`), so it doesn't depend on the window size, display scaling or window position. `--resolution` only sizes the window.
- Exit codes: 0 saved, 1 error (bad arguments, unknown preset, missing pack or presets file, a map that failed to load or loaded fewer tiles than the manifest lists, `--headless`, flat-colour image), 2 the whole map loaded but the ground-relative preset has no terrain under it (prints `N/A: outside built area`, writes no PNG).
- `--headless` disables rendering, so the tool refuses to run under it.
- The flat-colour check catches an empty or cleared image, not a camera below the terrain: the terrain is one-sided, so from below it is invisible and the image shows the underside of the buildings.

**macOS.** Godot has no off-screen display driver on macOS. The run opens a normal window (Metal) for about 3 s, and needs a logged-in GUI session. `--position` can't hide it: macOS moves the window back on screen (tried `-5000,-5000` and `20000,20000`). The saved image was byte-identical for every window position and for a 200x100 window. Don't minimise the window while it runs.

**CI (Linux, not set up yet).** Run under a virtual X server, with Mesa's lavapipe software Vulkan driver so Forward+ renders without a GPU: `xvfb-run -a godot --path game --resolution 1600x900 -- --screenshot ...` with `xvfb` and `mesa-vulkan-drivers` installed on the runner. If Vulkan isn't available, add `--rendering-driver opengl3` (Compatibility renderer); its images look different from Forward+, so don't compare them with macOS ones. These are runner packages, not project dependencies. Untested so far.
