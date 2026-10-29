# Roadman Simulator: Somero-City — Project Kickoff Outline

Oct 27, 2026 · @Kimmo

## Project summary

The first goal is a drivable, accurate model of Somero centre in Godot; gameplay comes after the map pipeline works. Everything else in this outline builds toward that first playable.

**Fixed decisions**

- Map: Somero town centre (about 2 × 2 km) as the first map pack, generated from OpenStreetMap and Maanmittauslaitos (MML) open data; the tooling supports adding more maps.
- Engine: Godot 4, .NET build (latest stable release), scripted in C#.
- Platforms: macOS and Windows desktop builds.
- Tooling: free tools only.
- Development: Claude Code does most of the implementation; you orchestrate, review and playtest.
- Genre: small open-world crime game inspired by classic open-world sandbox crime games.

**Scope limits for version 1**

- One map area, no interiors.
- A few vehicle types and one player character.
- Simple traffic and pedestrian AI, a wanted level with police, a handful of missions.

**Title:** Roadman Simulator: Somero-City. Repo name: `roadman-simulator`.

## Phase 0: Tool setup

Install these on the Mac before starting; all are free. Homebrew covers most of them.

| Tool | Purpose | Install |
| --- | --- | --- |
| Godot 4 .NET build (latest stable) | Game engine and editor with C# support | godotengine.org, the .NET download |
| Godot .NET export templates | macOS and Windows builds | Editor → Manage Export Templates |
| .NET SDK (the version Godot's docs list for your release) | C# compiler, `dotnet build`, `dotnet test`, `dotnet format` | dot.net or `brew install --cask dotnet-sdk` |
| JetBrains Rider and PyCharm | C# game code (Rider), Python pipeline (PyCharm) | Your JetBrains licence |
| Python 3.12+ and uv | Map generation pipeline | `brew install uv` |
| GDAL | Raster clipping and heightmaps | `brew install gdal` |
| QGIS | Inspecting data, checking the bounding box | qgis.org |
| Blender | Inspecting and fixing generated meshes, making props | blender.org |
| Git and Git LFS | Version control, large binary assets | `brew install git git-lfs` |
| Claude Code | Development agent (Team plan) and the Claude Code \[Beta\] JetBrains plugin | Anthropic docs |

**Language: C#.** Godot 4 exports C# projects to all desktop platforms, Windows, macOS and Linux, so both targets are covered ([Godot docs](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/index.html)). Things to know:

- Use the .NET editor build and .NET export templates; the standard build has no C# support.
- C# projects can't be exported to the web in Godot 4. This doesn't affect the macOS and Windows targets.
- Godot's built-in script editor has minimal C# support, so edit code in Rider. Claude Code works on the files directly.
- Most tutorials and many add-ons use GDScript. C# can use GDScript and GDExtension add-ons, but through untyped `Call` and `Get` methods.
- Keep engine-free logic in a plain C# class library, testable with `dotnet test` in seconds.

**Accounts**

- GitHub (free) for the repository and CI.
- An MML API key (free registration) only if you use MML's interface services; file downloads need no key.
- An itch.io account (free) when you get to releases.

## Development environment

Rider for the game, PyCharm for the map pipeline, the Godot editor for scenes, and Claude Code (Team plan) running inside both IDEs through Anthropic's JetBrains plugin. The Claude Code features this outline relies on (`CLAUDE.md`, slash commands, subagents, hooks, permissions) all come with that setup.

**Rider (game)**

- Open the C# solution in `game/`. Godot support is bundled with Rider.
- In the Godot editor, set Rider as the external C# editor (Editor Settings → Dotnet → Editor), and install JetBrains' Rider integration addon for project discovery and scene run configurations.
- Run and debug the game with the generated Player run configuration; the debugger shows the current scene and node children.
- Run xUnit (`Roadman.Core.Tests`) and gdUnit4Net tests from Rider's test runner.
- Add `.idea/` to `.gitignore`.

**PyCharm (map pipeline)**

- Open `tools/mapgen/` as the project and point the interpreter at the `.venv` that uv creates.
- Run pytest from PyCharm's test runner, and use the Ruff plugin for formatting and linting in the editor.

**Claude Code in the IDEs**

- Install the Claude Code CLI and sign in with your Team account.
- Install the Claude Code \[Beta\] plugin from the JetBrains Marketplace in both Rider and PyCharm, then restart them.
- Run `claude` from the IDE's terminal at the repo root. Changes then appear in the IDE's diff viewer, and your selection or open file is shared with Claude Code.
- Use the IDE that matches the prompt: Rider for game prompts, PyCharm for pipeline prompts. From a separate terminal, `/ide` connects Claude Code to a running IDE.
- If an IDE stops being detected after a Claude Code update, update the plugin first; `claude doctor` shows whether the IDE is detected.

## Phase 1: Repository and Claude Code foundation

Set up the repo so Claude Code can build, test and verify on its own from day one. If your own development-environment template is ready, start from it and add the game-specific parts below; otherwise prompts 1 and 2 create everything.

**Repository layout**

```
roadman-simulator/
  CLAUDE.md              # project-wide rules for Claude Code
  docs/
    vision.md            # game concept, scope limits
    architecture.md      # systems and how they connect
    specs/               # one spec per feature, written before coding
    decisions/           # short ADRs (language, tile size, etc.)
  maps/                  # one folder per map, all map-specific input
    somero/
      map.yaml           # name, bounding box, origin, CRS, tile size, sources, style
      markers.json       # spawn area, parking, fictional points of interest
      overrides/         # per-building fixes and appearance
  tools/mapgen/          # Python map pipeline, no map-specific code
    CLAUDE.md            # pipeline-specific rules
    data/raw/<map-id>/   # downloaded OSM/MML data (git-ignored)
  game/                  # Godot project
    CLAUDE.md            # Godot-specific rules
    project.godot
    addons/              # gdUnit4, Terrain3D if used
    maps/<map-id>/       # generated map packs (pipeline output)
    Roadman.Core/        # engine-free C# logic
    scenes/  scripts/  tests/
  .claude/
    settings.json        # permissions and hooks
    commands/            # custom slash commands
    agents/              # subagent definitions
  CREDITS.md             # data and asset attributions
```

**CLAUDE.md contents (root)**

- What the project is and the scope limits from `docs/vision.md`.
- Build, run and test commands, with exact command lines.
- Coding conventions: C# style, nullable reference types on, Godot signals via \[Signal\] delegates, which logic goes in the core library, scene structure.
- Definition of done: tests pass, linter clean, headless run without errors, screenshot checked where visual.
- Rules: never edit `game/maps/` by hand, never commit raw data, ask before adding dependencies.

**Claude Code configuration**

- Permissions allowlist in `.claude/settings.json`: `godot --headless`, `dotnet build`, `dotnet test`, `dotnet format`, `uv run`, `pytest`, `git` read commands.
- Hooks: run `dotnet format` on changed `.cs` files and `ruff format` on changed `.py` files after edits.
- Slash commands: `/spec` (draft a feature spec), `/implement` (build from a spec), `/verify` (build, tests, format check, headless run, screenshot), `/regen-map` (run the pipeline and reimport).
- Subagents: `mapgen` (Python and GIS), `godot-dev` (C# gameplay code and scenes), `reviewer` (reads diffs against conventions), `tester` (writes and runs tests).

**Git setup**

- `.gitignore`: `game/.godot/`, `tools/mapgen/data/raw/`, build output, the .NET bin and obj folders, and generated map packs in game/maps/.
- Git LFS for `.glb`, `.png`, `.wav`, `.ogg`. Use it for hand-made assets only; generated map packs are git-ignored and rebuilt by the pipeline from local raw data, while release builds get their raw data from a pinned snapshot (see Map packs).
- Keep Godot's `.import` files in Git; they hold import settings. The exception is game/maps/: its .import files are generated and ignored along with the map packs.

**Fitting this into your agent pipeline.** Map the requirements → release flow onto the folders: requirements in `docs/vision.md`, specs in `docs/specs/`, implementation by subagents, verification by `/verify`, release via CI. You approve each spec before implementation and playtest each milestone.

## Phase 2: Data acquisition for Somero centre

Download the data once, by script, so it can be repeated and checked. Claude Code can write the download script; you verify the area in QGIS.

1. **Define the area.** Somero centre is roughly latitude 60.62–60.64 N, longitude 23.50–23.54 E, about 2.2 × 2.2 km. Confirm and adjust it in QGIS over an OSM basemap, then record it in `maps/somero/map.yaml`.
2. **Choose a projection and origin.** Use ETRS-TM35FIN (EPSG:3067), which is metric. Put the local origin near the town centre so game coordinates stay within about ±1.5 km.
3. **OpenStreetMap.** Download the bounding box via the Overpass API, or cut it from the Geofabrik Finland extract with `osmium extract`. Keep roads, buildings, land use, water, railways, and points of interest.
4. **MML elevation model 2 m.** Download the GeoTIFF map sheets covering the area from MML's open data file service, and merge and clip them with GDAL.
5. **MML orthophoto.** Download it for ground textures and as a modelling reference.
6. **MML 3D buildings.** Check MML's coverage map for Somero. If it's covered, download them; otherwise building heights come from OSM tags or defaults.
7. **Optional: MML topographic database.** It has building footprints and road classes that can fill gaps in OSM.
8. **Inspect everything in QGIS.** Check alignment between layers, missing buildings, and road connectivity at bridges and intersections.

Avoid MML's high-density laser scanning data 5 p: it isn't open and carries a charge. The free elevation model and 0.5 p point cloud are enough at game scale. The 0.5 p point cloud comes on 1:5 000 map sheets and supplies the building heights.

**Done when:** all raw files are in `data/raw/`, a download script reproduces them, and a QGIS project shows all layers aligned over the chosen area.

## Phase 3: Map generation pipeline

A Python pipeline in `tools/mapgen/` turns any map's raw data into a Godot-ready map pack with one command. Build it in stages; each stage has its own output you can inspect.

**Libraries (all open source):** geopandas, shapely, pyproj, rasterio, numpy, osmnx or pyrosm, networkx, trimesh. Manage them with uv.

**Stages**

1. **Load and project:** read OSM and MML data, reproject to EPSG:3067, subtract the local origin.
2. **Terrain:** produce either chunked terrain meshes or a 16-bit heightmap for the Terrain3D add-on. Decide with an ADR (see below).
3. **Buildings:** extrude footprints to height, from MML 0.5 p lidar (median roof height minus ground), MML 3D data, OSM `height` or `building:levels` tags, or a default of 2 floors × 3 m. Set the base at terrain height under the footprint. Start with flat roofs; add simple pitched roofs later. Each building also carries its category, name and address (see Place information).
4. **Roads:** buffer centre lines to widths by road class, triangulate, drape onto the terrain with a small offset. Handle bridges with OSM `bridge` and `layer` tags.
5. **Water and land use:** water polygons as flat meshes, land-use polygons as material zones on the ground.
6. **Vegetation:** scatter tree positions inside forest and park polygons; output positions only, not meshes.
7. **Road graph:** nodes at intersections, edges with class, lane count, speed limit, one-way flag, street name (Finnish and Swedish) and road number. Godot uses it for traffic and the minimap.
8. **Gameplay markers:** the spawn area, parking spots, and points of interest from OSM with their real names (display rules in Place information).
9. **Tiling and export:** split everything into tiles (start with 256 m), export one `.glb` per tile plus JSON for the graph, vegetation and markers, and render a top-down minimap PNG.

**Collision on import.** Godot generates collision automatically from glTF node name suffixes, such as `-col`, `-colonly` and `-convcolonly`. Name meshes accordingly in the pipeline, for example simplified building boxes as collision-only shapes.

**Terrain decision (ADR)**

- Mesh terrain from the pipeline: one code path, exact match with roads and buildings, collision from the mesh. Needs your own LOD for distant tiles.
- Terrain3D heightmap: built-in LOD and painting tools, editable in the editor. Adds an add-on dependency (a C++ GDExtension, called from C# through untyped Call and Get methods), and road draping must use the same heightmap sampling.

**Tests:** pytest checks that tile bounds are correct, there are no NaN vertices, building and road counts match the source data, and the road graph is connected. A fixed small test area keeps tests fast.

**Done when:** `uv run mapgen build` somero produces all tiles and JSON from raw data in one run, tests pass, and the output looks right when opened in Blender.

## Map packs and building enhancement

Maps are data, not code: a new town is a new config folder, and building appearance comes from a separate enhancement layer that improves over time without changing the base data.

**Map packs**

- Each map has its own folder `maps/<map-id>/` with `map.yaml` (name, bounding box, origin, CRS, tile size, data sources, style preset), `markers.json` and `overrides/`. Somero is `maps/somero/`.
- The pipeline takes the map id as input: `uv run mapgen build somero`. The pipeline code contains nothing specific to Somero.
- Data sources sit behind adapters with one interface: OSM (worldwide), MML (Finland), and a global elevation model such as Copernicus GLO-30 (about 30 m) for areas without national data. A map outside Finland then needs a config, not new code.
- Output is a self-contained map pack in `game/maps/<map-id>/`: tiles, road graph, markers, minimap, and a `manifest.json` with a schema version and the map's data credits. Map packs are git-ignored and rebuilt from raw data: locally from data/raw/\<map-id>/, in releases from a pinned raw data snapshot.
- The game reads the map id from settings or a menu, and the map loader checks the manifest's schema version. Missions refer to marker ids inside the pack, never raw coordinates, so content stays tied to its map.
- Map packs stay data-only (meshes, scenes, JSON), with all C# code in the main game. Godot can load extra resource packs (.pck) at runtime, so new maps could later ship without a new game build.
- CI builds Somero plus a tiny second test area from another town, which shows quickly when something Somero-specific slips into the code.

**Data policy and snapshots**

- External data is fetched once. Builds, `/verify` and `/regen-map` use only `data/raw/<map-id>/`; `mapgen download` runs only when you explicitly ask for it.
- A raw data snapshot holds OSM (without contributor metadata), the DEM and lidar, never the debug-only orthophoto, with `SHA256SUMS` and a NOTICE (OSM ODbL, MML CC BY 4.0). It's the single asset of its own `data-<map-id>-<date>` GitHub release, and `map.yaml` pins it by repo, tag, asset and checksum.
- `mapgen snapshot create` builds it locally with no network, `snapshot publish` uploads it (you run this yourself), and `snapshot fetch` fills `data/raw/` on fresh clones and in CI, replacing existing local data only with `--force`.
- Releases fetch the pinned snapshot and rebuild the map with the current pipeline, so they need no MML key and don't depend on Overpass or MML. The map only changes when you publish a new snapshot.

**Building enhancement layer**

Each building attribute comes from the first source that has a value:

1. Manual override in `maps/<map-id>/overrides/`, keyed by OSM or MML building id: height, roof shape and colour, facade style, texture set, or a hand-made replacement model (.glb).
2. Enhancement data gathered for the map, for appearance (sources below).
3. MML 0.5 p lidar: measured height per building, the median roof height minus ground. The 90th percentile is stored too, for future pitched roofs.
4. MML 3D building geometry: slot reserved, no reader yet.
5. OSM tags: `height`, `building:levels`, `roof:shape`, `roof:colour`, `building:material`, `building:colour`.
6. Defaults from the map's style preset (2 floors × 3 m for height).

Tooling for this layer:

- Style presets per map: facade styles by building type (for example wooden detached house, brick commercial, apartment block), window grids from the floor count, tileable CC0 textures.
- `uv run mapgen enhance somero` merges the overrides and records where each attribute came from.
- An in-game debug inspector shows a building's id and the source of each attribute, so you can click a building in Godot and add an override for it.
- A facade helper script (OpenCV, open source) corrects a photo's perspective into a facade texture.
- For landmark buildings, open source photogrammetry (COLMAP or Meshroom) turns your own photo sets into models.
- Every override records its source and licence, and the pipeline writes them into the map's credits automatically.

**Image sources and their terms**

| Source | Use in the game | Terms |
| --- | --- | --- |
| Your own photographs | Facade textures, photogrammetry, reference | You own them. Avoid identifiable people and number plates. |
| Mapillary street-level imagery | Facade textures, reference | CC BY-SA 4.0: attribution, and textures made from the photos must be shared under the same licence ([Mapillary](https://help.mapillary.com/hc/en-us/articles/115001770409)). |
| MML orthophotos | Roof colours and shapes from above | CC BY 4.0, attribution |
| MML 0.5 p point cloud | Building heights (in use); later roof shapes | CC BY 4.0, attribution |
| Google Street View | Viewing it yourself as a modelling reference | Google Maps Platform terms forbid exporting or extracting its content for use outside Google's services, so no textures or automated extraction ([Cesium forum, quoting the terms](https://community.cesium.com/t/saving-google-map-data/31228)). |

Whether CC BY-SA's share-alike reaches beyond the textures themselves is a legal question; keep those textures in a separate, labelled folder. Tags you add to OSM from your own survey help both the pipeline and other OSM users, but OSM doesn't accept data copied from Google sources.

## Place information

All place information goes into the map pack, so the game can always tell the player where they are: street names, road numbers, addresses, building categories and names, and points of interest.

**Sources**

- OSM (already local): street `name`, `name:sv` and `ref` (road number); building `building` values; `addr:street` and `addr:housenumber`; named points of interest (`shop`, `amenity`, `office`, `tourism`, `leisure`); land use areas.
- MML topographic database: a use class for every building (residential, commercial or public, industrial, holiday home, church, other). It's fetched once, by explicit request, and included in the next data snapshot.

**In the map pack**

- Road graph edges: street name in Finnish and Swedish, and road number.
- `buildings.json`: category, category source, name, address, and links to the points of interest inside.
- `pois.json`: id, name, Swedish name, type, map-local position, and the building it belongs to.

**Building category**, from the first source that has a value:

1. Manual override.
2. A specific OSM `building` value (`apartments`, `industrial`, `church` and so on, but not `yes`).
3. A shop, amenity or office on or inside the building.
4. MML topographic use class.
5. The OSM land use area the building stands in.
6. Unknown.

Raw values map onto a small fixed set: residential, commercial, retail, industrial, public, religious, education, agricultural, garage or shed, other, unknown. The raw values are kept alongside.

**In the game**

- HUD: the current street name. Minimap: street labels.
- A debug inspector shows the building or point of interest under the crosshair, with its category, name, address and the source of each value.
- How real business names are displayed (real, fictional replacements or generic labels such as "Café") is a separate decision; the pack always stores the real data, and logos are never used.

## World edge

Every map ends at a physical barrier, and anything that crosses it falls off the world into space and dies. The pipeline generates the barrier for any map, so new map packs get it automatically.

**In-game premise:** in this world the Earth is flat. The map border is its edge, guarded by roadblocks on roads and barbed-wire fences everywhere else.

**Pipeline (Phase 3)**

- `map.yaml` defines the playable boundary: the bounding box by default, or a custom polygon. The pipeline reports every place where the boundary crosses water, so you can keep those crossings short. OSM water multipolygons can arrive incomplete at the edge of the download (ADR 0005), which is exactly where the boundary runs, so this check must close or clip them, or use MML topographic water areas instead.
- Roadblocks: wherever a road crosses the boundary, place a roadblock across it, sized from the road's class and width.
- Fences: barbed-wire fence segments along the rest of the boundary, following terrain height, with gaps only at roadblocks. Segments over water get a separate water fence type with its own scene, placed at the water surface instead of on terrain.
- Edge geometry: the terrain ends in a cliff face just outside the barrier, so the world reads as a slab floating in space.
- Road graph: edges crossing the boundary end at the roadblock as dead ends, marked as turn-around or despawn points for traffic.
- Output: `boundary.json` in the map pack with fence segments and their type (land or water), roadblock positions and orientations, and the void trigger volume.
- Tests: every boundary-crossing road has a roadblock, fences and roadblocks form a closed loop, no fence segment crosses a road, and every segment over water is a water fence.

**Game (Phase 5)**

- Barrier scenes: reusable roadblock, land fence and water fence scenes, low enough to jump over on foot, with breakable physics pieces that vehicles drive straight through, placed from `boundary.json`.
- Void trigger: an `Area3D` beyond the barrier. Entering it starts the death sequence: controls off, a pull outward and down replaces normal gravity, the camera pulls back to show the floating world against space, then a death screen and respawn at a random point inside the map's fixed spawn area.
- Space backdrop: a starfield sky (procedural shader or CC0 panorama), visible past the edge.
- Same rule for everyone: vehicles, pedestrians and police that cross also fall. AI routes never lead through barriers, and police pursuit stops at the edge.
- Code split: the death and respawn state machine lives in `Roadman.Core` with unit tests, including clearing money and gear on death; visuals and physics live in Godot.
- Tests: a gdUnit4 test drives a car through a roadblock at speed and asserts the death state within a set time.

**Decisions**

- Barriers are easy to cross: the player can jump over them on foot or drive through them.
- Boundaries avoid water: the boundary is placed so it doesn't run through lakes or along rivers. Where it has to cross water, a special water fence replaces the land fence.
- Any death costs all money and all gear the player has collected.
- After death, the player respawns at a random point inside a fixed spawn area defined in the map pack.

**Spawn area**

- Each map pack defines a fixed spawn area as a polygon in `markers.json`.
- On respawn, the game picks a random point inside the area, redraws it if it lands inside a building, in water or on another object, and drops the player onto the ground there.
- A pipeline test checks that the spawn area has enough open ground to spawn on.

## Art direction: low-poly 3D

The game uses a low-poly 3D look: simple geometry, flat or near-flat shading, and a small colour palette instead of detailed textures. Somero stays recognisable through its real street layout, building footprints and heights, the river and terrain, and its colours, not through photographic detail.

**What this means for the pipeline**

- Buildings: the extruded footprints already fit. Simple pitched roofs can come later from the stored 90th-percentile roof height, and windows as simple dark quads or a small texture atlas rather than photo facades.
- Roof colours: sample each building's average roof colour from the MML orthophoto and snap it to the palette. Real roof colours are one of the cheapest ways to make the town recognisable from above.
- Terrain: a coarser grid than the 2 m elevation model (for example 4–8 m) with faceted shading. This bears on ADR 0004: faceted mesh terrain suits the look directly, while Terrain3D is built for smooth, detailed terrain.
- Materials: one shared palette texture (a strip of colour swatches) or vertex colours, so most of the map uses a single material. This also keeps draw calls low on older Macs.
- Style presets: `style_preset` in `map.yaml` becomes a palette plus facade rules per building type.

**Assets**

- Vehicles, characters, trees and props from CC0 low-poly kits, such as Kenney, Quaternius and KayKit, recorded in `CREDITS.md`. One or two kits keep the styles consistent.
- Hand-made Blender models for a few Somero landmarks, kept to the same polygon budget and palette.

**Effect on earlier sections**

- Photo-based facade textures (Mapillary, your own photos) become optional. Your own photos stay useful as modelling references for landmarks and for picking palette colours.
- The image sources table still applies to anything photo-derived you decide to use.

**Still to decide** (in `docs/art-direction.md` with an ADR, after the first flyover in prompt 6)

- [ ] Palette, and whether shading is fully flat or softly smoothed.
- [ ] Season and time of day: summer light, autumn, or winter with snow.
- [ ] Terrain grid size for the faceted look.
- [ ] Which CC0 kits to use for vehicles and characters.

## Phase 4: Godot project skeleton and map import

The Godot project should load the generated map and let Claude Code verify it without opening the editor.

1. **Create the project** in `game/` with the Godot .NET editor and the Forward+ renderer. Generate the C# solution (Project → Tools → C#) and set Jolt as the 3D physics engine in Project Settings → Physics → 3D.
2. **Split the C# code:** the Godot project for nodes and scenes, a plain class library (`Roadman.Core`) for engine-free logic such as wanted level, road graph routing and mission state, and a test project for that library.
3. **Install add-ons:** gdUnit4 with its C# support (gdUnit4Net) for tests that need the engine, and Terrain3D if the ADR chose it.
4. **Core autoloads:** `Events` (a global signal bus) and `GameState` (player, wanted level, settings). Keep them small.
5. **World scene:** a `World` root with a `MapLoader` node, `WorldEnvironment`, sun light, and a free-fly debug camera.
6. **MapLoader:** reads a manifest from `game/maps/<map-id>/`, instances tiles near the camera and frees distant ones. Start by loading all tiles; add streaming once it works.
7. **Road graph loader:** parses the JSON into C# types in `Roadman.Core`, with debug drawing of nodes and edges in Godot.
8. **Import presets:** set `.glb` import options once (collision suffixes, mesh compression, LOD generation) so regenerated tiles import the same way.
9. **Headless verification:**
   - `godot --headless --path game --import` imports assets and shows errors.
   - `dotnet build` compiles the solution, and `dotnet test` runs the `Roadman.Core` tests in seconds.
   - gdUnit4 runs the engine-dependent tests headless from the command line.
   - A debug script loads the world, positions the camera, saves a viewport screenshot to PNG, and quits. Claude Code can view the image to check visual results.

**Done when:** the free camera flies over a recognisable Somero centre, and `/verify` imports, tests and screenshots without errors.

## Phase 5: First playable — driving through Somero

One car you can drive around the real streets is the first point where it feels like a game. It also tests the map's collisions and scale.

1. **Vehicle scene:** `VehicleBody3D` with four `VehicleWheel3D` nodes, a placeholder box body, and tunable exported parameters (engine force, brake, steering, suspension).
2. **Input map:** accelerate, brake/reverse, steer, handbrake, reset vehicle, camera toggle. Support keyboard and gamepad from the start.
3. **Chase camera:** spring arm with smoothing, avoiding clipping through buildings.
4. **HUD:** speedometer and the minimap PNG with a player marker.
5. **Spawn and reset:** spawn at a marker from the pipeline, reset upright on a key press.
6. **Tuning pass:** you drive and adjust; Claude Code exposes parameters and implements changes you describe.

Claude Code can verify that the car spawns, moves under scripted input and stays on the ground in headless tests. How the car feels to drive is your job in playtesting.

**Done when:** you can drive from one end of the centre to the other without falling through the map, and the minimap position matches.

## Gameplay milestones

&#91;embedded content: gameplay roadmap · 7 milestones\]

Milestones 1 and 2 are Phases 2–5 above. Milestones 3–7 each start with a spec in `docs/specs/`, approved by you before Claude Code implements it.

## Testing, CI and builds

Automated checks let Claude Code work in longer stretches without you checking each change. Set them up during Phases 3 and 4, not at the end.

**Test layers**

- Pipeline: pytest unit tests on geometry and a small end-to-end build of the test area.
- Core logic: `dotnet test` (xUnit or NUnit) on `Roadman.Core`, covering wanted level, road graph routing and mission state. No engine needed.
- Godot integration: gdUnit4 scenes run headless, spawn the world, run scripted input for some seconds and assert results.
- Visual: screenshots from fixed camera positions, compared by Claude Code or by you after changes to the map or rendering.
- Style: `dotnet format --verify-no-changes` and .NET analyzers for C#, `ruff` for Python.

**CI on GitHub Actions**

- On every push: pipeline tests, Godot tests, format checks, on Linux runners with the .NET SDK installed. The Godot tests use a test-area map pack built first from a small committed data fixture.
- On v\* tags: fetch each map's pinned raw data snapshot, run the pipeline, then export macOS and Windows builds and attach them to a GitHub release together with the download manifests. Data tags (data-\*) don't trigger CI or releases.
- Community GitHub Actions exist for installing Godot and export templates; check their licences and maintenance before using one.
- Free plans include monthly Actions minutes for private repos, and macOS runners use them faster. Exporting both platforms from a Linux runner avoids the macOS minutes.

**Export presets**

- Windows Desktop: x86\_64, embed the PCK. Godot exports Windows builds from a Mac.
- macOS: universal binary (Apple Silicon and Intel), ad-hoc signing.
- Keep `export_presets.cfg` in Git, with no credentials in it.

**Signing (cost decision)**

- Unsigned or ad-hoc signed builds are free. Players see Gatekeeper and SmartScreen warnings and must click through.
- Removing the macOS warning requires notarization with a paid Apple Developer Program membership. Removing the Windows warning requires a paid code-signing certificate.

For itch.io hobby releases, unsigned builds with install instructions on the game page are common.

## Claude Code workflow per feature

Each feature goes through the same loop: spec, plan, implement, verify, playtest, commit. You decide at the spec and playtest steps; Claude Code does the rest.

1. **Spec:** `/spec traffic-lights` drafts `docs/specs/traffic-lights.md` with goal, behaviour, acceptance criteria and test ideas. You edit and approve it.
2. **Plan:** Claude Code proposes the change in plan mode (files, scenes, signals, tests). Check it against `docs/architecture.md` before approving.
3. **Implement:** the relevant subagent writes code and tests. Keep tasks to something one session can finish and verify.
4. **Verify:** `/verify` runs tests, lint, a headless run and screenshots. Claude Code fixes failures before reporting done.
5. **Playtest:** you play the feature and give concrete feedback ("brakes lock too easily above 60 km/h").
6. **Review and commit:** the `reviewer` subagent checks the diff against conventions, then commit with a message referencing the spec.

**Practical tips for Godot with Claude Code**

- `.tscn` and `.tres` files are text and editable by Claude Code, but hand-merged scene edits break easily. Building node trees in code, or keeping scenes small, reduces errors.
- Ask for exported variables on anything you'll tune, so you can adjust values in the inspector without a new session.
- Feed back Godot editor errors by copying them, or by having Claude Code run the headless import and read the output.
- Use git worktrees to run two sessions in parallel, for example pipeline work and gameplay work.
- Update `CLAUDE.md` whenever you correct the same mistake twice.
- Record decisions as short ADRs in `docs/decisions/`, so future sessions follow them.

## Legal, attribution and risks

Settle the trademark and attribution items before the first public build. This is general information, not legal advice.

**Legal and attribution**

- [ ] Title "Roadman Simulator: Somero-City" checked in EUIPO eSearch and TMview (classes 9 and 41) and on Steam and itch.io; logo and UI designed independently, not imitating any existing game's style.
- [ ] Credits screen and `CREDITS.md`: "© OpenStreetMap contributors" (ODbL), plus MML attribution with source, dataset and date as MML's CC BY 4.0 terms specify.
- [x] Godot's MIT licence text and third-party licences, the .NET runtime's licence and notices, and every shipped package's licence included with the game: each release zip has a `licenses/` folder, checked by `check-export.sh`. An in-game credits screen can come later.
- [ ] Licence of every add-on and asset recorded in `CREDITS.md`.
- [ ] Decide how real business names from OSM are shown in the game (real, fictional replacements, or generic labels); logos are never used.

**Risks**

| Risk | Effect | Mitigation |
| --- | --- | --- |
| Scope growth toward a full-scale commercial open world | Project never reaches a playable release | Scope limits in `docs/vision.md`, one milestone at a time |
| OSM gaps in a small town (missing heights, incomplete buildings) | Inaccurate or empty areas | MML topographic data, defaults, manual fixes in a small override file |
| Bridges and overlapping roads on terrain | Cars fall through or bounce | Handle `bridge`/`layer` tags early; dedicated test area |
| Claude Code breaking scene files | Lost work, confusing errors | Small scenes, code-built nodes, frequent commits |
| Performance on older Macs | Low frame rate | Tile streaming, LOD, a performance test scene measured each milestone |

## Getting started

Setup is one evening of manual steps. After that, Claude Code works through the prompts in Claude Code prompts in order, one reviewed commit per prompt.

**Setup before prompt 1** (about one evening; commands are for macOS)

- [ ] **1. Command-line tools.** In Terminal:
  - `brew install git git-lfs uv gdal`
  - `brew install --cask dotnet-sdk qgis blender`
  - `git lfs install`
  - Check that `dotnet --list-sdks` shows an SDK meeting the C# requirements in Godot's docs.
- [ ] **2. Godot .NET.** Download the latest stable Godot 4 .NET build for macOS from godotengine.org and move the app to Applications.
  - Don't symlink the binary: the .NET build then can't find its C# support files and crashes. Create a wrapper script instead. If a symlink already exists, remove it first with rm "$(brew --prefix)/bin/godot": writing to the symlink would overwrite the Godot binary itself. Then: `printf '#!/bin/sh\nexec "/Applications/Godot_mono.app/Contents/MacOS/Godot" "$@"\n' > "$(brew --prefix)/bin/godot"`, then `chmod +x "$(brew --prefix)/bin/godot"` (adjust the app name if yours differs). The repo's scripts also read `$GODOT` (default `godot`) and resolve symlinks, as a second safeguard.
  - Check that `godot --version` prints a version that includes `mono`.
- [ ] **3. Claude Code.** Install the CLI with the method in the Claude Code docs, run `claude` once and sign in with your Team account.
- [ ] **4. Repository.** Create a private GitHub repo `roadman-simulator`, clone it (for example to `~/dev/roadman-simulator`) and, if your development-environment template is ready, copy it in. Otherwise skip that part: prompts 1 and 2 create the Claude Code setup from scratch.
- [ ] **5. Outline.** Download this doc's main tab as Markdown and save it as `docs/project-outline.md`. Re-export it whenever the outline changes.
- [ ] **6. Godot project.** Open Godot .NET and create a project:
  - Name `RoadmanSimulator`, without spaces or a colon, so the C# assembly and namespace stay clean. The game's display title can be set later.
  - Path: the repo's `game/` folder. Renderer: Forward+. Version control metadata: None, since prompt 1 writes the repo's Git files.
  - In the editor: Project → Tools → C# → Create C# solution.
  - Editor → Manage Export Templates → Download and Install.
  - Editor → Editor Settings → Dotnet → Editor → External Editor: JetBrains Rider.
  - Close Godot.
- [ ] **7. Rider.** Open `game/RoadmanSimulator.sln`, install the Claude Code \[Beta\] plugin (Settings → Plugins → Marketplace) and restart Rider. If Rider asks for the Godot executable, point it at the Godot .NET app.
- [ ] **8. PyCharm.** Install the Claude Code \[Beta\] and Ruff plugins now; you open the pipeline project after prompt 3.
- [ ] **9. First commit.** From the repo root: `git add -A`, `git commit -m "chore: Godot project and outline"`, `git push`.
- [ ] **10. Start prompt 1.** In Rider's terminal, `cd` to the repo root, run `claude`, check the IDE connection with `/ide`, switch to plan mode (Shift+Tab) and paste prompt 1.

**Claude Code sequence (first week)**

1. Scaffold the repository, docs, ADRs and `CLAUDE.md` files. You review: `docs/vision.md` and the `CLAUDE.md` rules.
2. Configure Claude Code: permissions, hooks, slash commands, subagents. You review: what runs without asking.
3. Map config and data download for Somero. You review: the map area and layers in QGIS.
4. Pipeline foundation: load, terrain, buildings. You review: a tile opened in Blender.
5. Godot skeleton and C# solution structure. You review: the editor opens cleanly and `/verify` passes.
6. Map import and headless screenshots. You review: the screenshots, then a debug-camera flyover.
7. CI on GitHub Actions. You review: the first green run.

**Setup between prompts:** each prompt in Claude Code prompts is followed by your manual steps before, during and after it, including the Rider, PyCharm, Godot, QGIS, Blender and GitHub checks.

- Re-export this main tab to `docs/project-outline.md` whenever the outline changes, so Claude Code works from the current version.
