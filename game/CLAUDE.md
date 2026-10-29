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
- Map materials: `MapLoader` sets `TerrainMaterial` on each tile's `terrain` mesh and `BuildingMaterial` on every `bld_` mesh. Both default to the placeholders in `materials/placeholder/` (`terrain.tres`; `building.tres` with `building.gdshader`, which colours up-facing faces as roof). Replace that folder, or set the two exports, when the art-direction palette exists. Lighting (sun energy and shadow distance, ambient energy, AgX tone mapping, exposure) is exported on `World`.
- Roads (ADR 0012): tiles hold `road`, `path` and `bridge_osm_way_<id>[_p<n>]` meshes (imported from `-col` nodes, so each has a `StaticBody3D`). Their bodies are in the group `map_roads`, so `TerrainHeightAt` still returns bare terrain, 0.05 m (Somero's `surface_offset_m`) under the road surface. `MapLoader` sets `RoadMaterial` on `road` and bridge meshes and `PathMaterial` on `path` (placeholders `materials/placeholder/road.tres` and `path.tres`).
- `MapLoader.RoadGraph` (`Roadman.Core.Maps.RoadGraph`) is read from `road_graph.json` after the tiles, before `MapLoaded`. A missing or invalid file pushes an error with the `/regen-map` hint and leaves it null; the tiles still load. Routing comes with the traffic AI spec.
- `[importer_defaults]` in `project.godot` sets every scene import to name suffixes on, mesh compression off, LODs off, shadow meshes on and static light baking. A hand-made `.glb` added later gets these too; correct its import settings and commit its `.import` file.

## Orthophoto debug view

- F2 (`debug_orthophoto`, physical key) switches every loaded tile's terrain between `MapLoader.TerrainMaterial` and an unshaded material with that tile's aerial image (`scripts/debug/OrthophotoDebugView.cs`). Unshaded, because the photo already contains the sun's shading. While it is on, the bottom-right corner shows the attribution and licence of every manifest credit with source `mml_orthophoto` (`MapManifest.Credits`); the text always comes from the manifest.
- Debug builds only: `World` adds the view when `OS.IsDebugBuild()`.
- The images are `res://maps/<id>/debug/orthophoto/<tile_id>.jpg`, about 512x512, written by the pipeline with a `.gdignore` in `debug/`, so Godot never imports or exports them. The view reads them from disk with `Image.LoadFromFile` on the first switch-on (with mipmaps, cached until the map reloads). A tile without an image keeps the placeholder; with no images at all it shows "No orthophoto in this pack; run /regen-map <id>" and stays off.
- Don't add an export filter that includes `maps/*/debug/`: the files are debug-only, and the `.gdignore` is what keeps them out of the import cache and exports.

## Road graph debug view

- G (`debug_road_graph`, physical key; F3 is Mission Control on macOS) toggles a drawing of `MapLoader.RoadGraph` above the surfaces (`scripts/debug/RoadGraphDebugView.cs`): drivable edges coloured by class, walkable-only edges in one colour, arrows on one-way edges, nodes coloured by kind, and a legend. Sizes and colours are exports.
- Debug builds only, like the orthophoto view. Without a graph it shows "No road graph in this pack; run /regen-map <id>" and stays off. A map without roads has an empty graph (the pipeline always writes `road_graph.json`); the view then switches on with nothing drawn and a legend saying "road graph is empty".

## Screenshots

`godot --path game --resolution 1600x900 -- --screenshot <preset> <out.png> [--presets <file>] [--map <map-id>] [--orthophoto] [--road-graph]`, run from the repo root (`/verify` runs it for every preset in the pack, with `--map "$MAP_ID"`, and once more for `overview` with `--road-graph`).

- `--road-graph` renders with the road graph debug view on, its legend inside the capture. Exit 1 in a non-debug build or when the pack has no road graph.

- `--map <map-id>` renders that pack instead of the one in the `roadman/map/id` project setting: the tool sets `MapLoader.MapId` before the map loads. The id must be a lowercase slug (the pipeline's `SLUG_PATTERN`).
- `--orthophoto` renders with the orthophoto debug view on (see "Orthophoto debug view"). Its attribution overlay is moved into the capture `SubViewport`, so it is in the image (a `CanvasLayer` draws only in its own viewport). A pack without orthophoto images, or a build without the view (not a debug build), exits 1.
- Presets come from the rendered pack's `camera_presets.json`, or from `--presets <file>` (handy for trying a preset before it is in the pipeline). The reader follows the pipeline's rules: unknown or repeated keys, an empty `presets`, duplicate or non-slug preset names and `null` for `fov` or `ground_relative` are errors. A relative output or presets path resolves against the shell's working directory (`$PWD`), not `res://`: Godot changes into the project folder for `--path`. Where `PWD` isn't set (Windows `cmd`, PowerShell), pass absolute paths.
- The image comes from its own 1600x900 `SubViewport` (`ScreenshotTool.ImageSize`), so it doesn't depend on the window size, display scaling or window position. `--resolution` only sizes the window.
- Exit codes: 0 saved, 1 error (bad arguments, unknown preset, missing pack or presets file, a map that failed to load or loaded fewer tiles than the manifest lists, `--headless`, flat-colour image, `--orthophoto` without orthophoto images), 2 the whole map loaded but the ground-relative preset has no terrain under it (prints `N/A: outside built area`, writes no PNG).
- `--headless` disables rendering, so the tool refuses to run under it.
- The flat-colour check catches an empty or cleared image, not a camera below the terrain: the terrain is one-sided, so from below it is invisible and the image shows the underside of the buildings.

**macOS.** Godot has no off-screen display driver on macOS. The run opens a normal window (Metal) for about 3 s, and needs a logged-in GUI session. `--position` can't hide it: macOS moves the window back on screen (tried `-5000,-5000` and `20000,20000`). The saved image was byte-identical for every window position and for a 200x100 window. Don't minimise the window while it runs.

**CI (Linux).** CI doesn't run screenshots (ADR 0010). The route to try is to run under a virtual X server, with Mesa's lavapipe software Vulkan driver so Forward+ renders without a GPU: `xvfb-run -a godot --path game --resolution 1600x900 -- --screenshot ...` with `xvfb` and `mesa-vulkan-drivers` installed on the runner. If Vulkan isn't available, add `--rendering-driver opengl3` (Compatibility renderer); its images look different from Forward+, so don't compare them with macOS ones. These are runner packages, not project dependencies. Untested so far.

## Export

`game/export_presets.cfg` has two presets with no credentials: "Windows Desktop" (x86_64, PCK embedded) and "macOS" (universal, ad-hoc signed). Both exclude `addons/gdUnit4/*`, `tests/*` and `gdunit4_testadapter_v*/*` (ADR 0010). Export needs the .NET export templates; `.claude/scripts/check-export.sh <build-dir>` checks a build folder that also holds `NOTICE.txt` (`mapgen notice`), `CREDITS.md` and `licenses/` (`python3 -I .claude/scripts/collect-licenses.py <build-dir>`).

```sh
godot --headless --path game --export-release "Windows Desktop" "$PWD/build/export/windows/RoadmanSimulator.exe"
godot --headless --path game --export-release "macOS" "$PWD/build/export/RoadmanSimulator-macos.zip"
```
