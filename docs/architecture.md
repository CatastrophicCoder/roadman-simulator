# Architecture

This file describes the systems and how they connect. Source: `docs/project-outline.md`. Parts that are not designed yet are marked **TODO**. Decisions are recorded in `docs/decisions/`.

## Data flow

```
  OSM (Overpass / Geofabrik)   MML (DEM 2 m, lidar 0.5 p, orthophoto, 3D buildings, topo DB)
              \                     /
               v                   v
        tools/mapgen/data/raw/<map-id>/        (downloaded, git-ignored)
                       |
  maps/<map-id>/  -->  |  map.yaml, markers.json, overrides/
                       v
        tools/mapgen   uv run mapgen build <map-id>
                       uv run mapgen enhance <map-id>
                       |
                       v
        game/maps/<map-id>/   map pack (generated, never hand-edited)
          manifest.json, tiles/*.glb, road graph, markers,
          vegetation, boundary.json, minimap.png,
          debug/orthophoto/*.jpg (debug runs only)
                       |
                       v
        Godot: World scene -> MapLoader -> tile scenes, barriers, debug draw
                       |                         |
                       v                         v
        Roadman.Core (engine-free C#)  <-->  Gameplay nodes (C#)
          road graph, routing, wanted          vehicles, player, camera,
          level, missions, death/respawn       HUD, AI, void trigger
```

## Systems

### Map config: `maps/<map-id>/`

All map-specific input lives here, and the pipeline code contains none.

- `map.yaml`: name, bounding box, origin, CRS, tile size, data sources, style preset, playable boundary (the bounding box by default, or a custom polygon).
- `markers.json`: spawn area (polygon), parking spots, points of interest with fictional names.
- `overrides/`: per-building fixes and appearance, keyed by OSM or MML building id. Each override records its source and licence.
- `camera_presets.json` (optional): named camera positions for screenshots, copied unchanged into the pack (see the map pack section below).

`map.yaml` schema (validated by `MapConfig` in `tools/mapgen/src/mapgen/config.py`; unknown keys are errors):

| Key | Meaning |
| --- | --- |
| `id` | Lowercase slug, must equal the folder name |
| `display_name` | Name shown in the game |
| `bbox_wgs84` | `lat_min`, `lat_max`, `lon_min`, `lon_max` in degrees, min < max |
| `crs` | Projected map CRS, e.g. `EPSG:3067` |
| `origin` | `x`, `y` in metres in the map CRS, inside the bbox; print it with `mapgen origin <map-id>` |
| `tile_size_m` | Tile edge in metres (> 0) |
| `test_area` | `size_m`: a square centred on the origin, inside the bbox, used by fast tests |
| `style_preset` | Style preset name (placeholder for now) |
| `sources` | Named sources, each with `adapter`, `enabled`, `debug_only` and adapter-specific `options` |
| `sources.<name>.debug_only` | Optional, default `false`. The source feeds debug aids only (Somero: `mml_orthophoto`, ADR 0009). `download --release` and `build --release` skip it, so a release pack neither reads nor credits its files |

The playable boundary is the bounding box for now. TODO: a custom boundary polygon, and the `markers.json` schema.

### Map pipeline: `tools/mapgen/`

A Python pipeline that takes a map id and produces a self-contained map pack. Data sources sit behind adapters with one interface: OSM (worldwide), MML (Finland), and a global elevation model (for example Copernicus GLO-30).

Stages, each with output that can be inspected:

1. Load and project to EPSG:3067, then subtract the local origin (ADR 0003).
2. Terrain: behind a `TerrainBuilder` interface. Mesh terrain is the interim implementation; ADR 0004 (mesh or Terrain3D heightmap) is still open.
3. Buildings: extruded footprints, with attributes resolved by the enhancement layer.
4. Roads: buffered and triangulated, draped on the terrain, with bridges and layers.
5. Water and land use.
6. Vegetation: positions only.
7. Road graph: intersections and edges with class, lanes, speed limit and one-way flag.
8. Gameplay markers.
9. Tiling (256 m to start) and export: `.glb` per tile, JSON files, minimap PNG.
10. World edge: roadblocks, land and water fences, cliff edge, void trigger, written to `boundary.json`.

Building attribute precedence: manual override, then enhancement data, then measured data (MML lidar, then MML 3D), then OSM tags, then style preset defaults.

Collision comes from glTF node name suffixes (`-col`, `-colonly`, `-convcolonly`).

Module layout (`tools/mapgen/src/mapgen/`):

- `cli.py`: `mapgen download <map-id> [--only a,b] [--force] [--release] [--strict]`, `mapgen origin <map-id>`, `mapgen build <map-id> [--area full|test] [--out DIR] [--release]` and `mapgen notice <map-id>... (--out FILE | --check FILE)`, with `--repo-root` (found automatically by default). `enhance` will be added as a further subcommand.
  - `download --release` skips `debug_only` sources (logs `[<name>] skipped (debug only)`); naming one in `--only` together with `--release` is a usage error (exit 2).
  - `download --strict` fails (exit 1) when a selected source returned no files, or when its adapter's required environment variables are unset (the MML adapters need `MML_API_KEY`; with `--strict` they neither print manual steps nor register files already on disk). Each failed source is reported with its reason; the key is never printed. Without `--strict` nothing changes.
  - `build --release` treats `debug_only` sources as disabled, even when their raw files exist.
  - `notice` reads each built pack's `manifest.json` credits and writes a plain-text NOTICE: a heading, a sentence that the data was modified (processed into 3D map tiles), per map each credit's attribution exactly as in the manifest followed by `Licence: <name>, <url>`, and the licence URLs once each. A missing pack or manifest, an invalid manifest or a licence without a known URL fails (exit 1). `--check FILE` exits 1 and lists every attribution or licence line missing from FILE (whole lines, surrounding spaces ignored).
- `notice.py`: the NOTICE text, its expected lines and the licence name to URL table.
- `config.py`: the `map.yaml` model and loader. `paths.py`: repo root, `maps/<map-id>/`, `data/raw/<map-id>/`, the build work folder and the pack folder.
- `geo.py`: WGS84 to map CRS, projected bbox, world and local coordinates, test area, geometry reprojection and offsets.
- `tm35.py`: ETRS-TM35FIN map sheet names for a bbox (used by MML adapters).
- `http.py`: a small urllib client behind a protocol, so tests can stub it.
- `sources/`: the adapter interface (`base.py`), the registry, and the adapters `osm_overpass`, `mml_dem_2m`, `mml_lidar`, `mml_orthophoto`, `mml_buildings_3d` and `mml_topographic` (ADR 0005).
- `download.py` and `manifest.py`: run the enabled sources and write `data/raw/<map-id>/download-manifest.json` with the checksum, licence and attribution of each file.

Adapter interface: a source's `adapter` value selects a `SourceAdapter` class, whose options model validates the source's `options`. `download(ctx)` gets the map config, the raw folder, the `--force` flag and an injected HTTP client, and returns one `DownloadedFile` record per file. Each adapter also declares what it can load in `provides` (`osm_vector`, `elevation`, `point_cloud`, `buildings_3d`, `imagery`), and `load(ctx)` reads its raw files for a build and returns a `SourceData` (footprints, an elevation raster, a point cloud, imagery files, skipped features, and the files it read). The build picks enabled sources by capability, never by source name, and allows at most one source per capability.

- `osm_overpass` loads building footprints from the `.osm` XML (standard library `iterparse`): closed ways and `type=multipolygon` relations, with rings joined from member ways and both outer and inner rings repaired with `make_valid`. Unclosed rings are dropped. Features that can't be built (no closed outer ring, geometry errors, duplicates of a relation's outer way) are skipped with a reason; relations built despite missing member ways or unclosed rings are listed as partial (ADR 0005).
- `mml_dem_2m` loads its GeoTIFFs, mosaics them with `rasterio.merge`, and crops to the area plus a 10 m margin. A file is warped only when its CRS isn't equivalent to the map CRS (compared semantically, not as text); warped CRSs are listed in the load report. Nodata becomes NaN.
- `mml_lidar` downloads MML's laser scanning data 0.5 p as LAZ on TM35 1:5 000 sheets (`sheet_scale`, `dataset` options; `Uusin` is the latest). Only the process `laserkeilausaineisto_05_karttalehti` is allowed; the 5 p data is refused (ADR 0005 amendment). `load` reads the files with laspy and lazrs in chunks, keeps the points inside the requested bounds and drops classes 2 (ground), 7 (low noise) and 18 (high noise). Its options also hold the height thresholds used by stage 3.
- `mml_orthophoto` provides `imagery`: `load` lists the JPEG 2000 (or GeoTIFF) files that overlap the area, without reading pixels; the build reads them per tile for the debug textures. Missing files aren't an error: the build then logs it and writes no `debug/` folder. Its option `debug_texture_m_per_px` (default 0.5, 0.5–1.0) sets the texture resolution.
- `mml_buildings_3d` has a stub `load` that returns no buildings: MML has no 3D building coverage for Somero, and its CityGML reader is out of scope (ADR 0005).

Build stages (`build/`; ADR 0006):

- `build/__init__.py`: `run_build(config, area, raw_dir, out_dir, work_dir, *, release=False)` runs the stages in order and writes the pack. With `release`, `debug_only` sources are disabled first.
- `build/area.py`: `BuildArea` (the full bbox or the test area in local coordinates, rounded outward to whole metres) and the tile grid.
- `build/stage1_load.py`: loads the sources by capability, reprojects footprints to the map CRS, subtracts the origin, and keeps footprints whose centroid is in the area. The point cloud is read only around those footprints and moved to local coordinates.
- `build/heightfield.py`: `Heightfield`, the single terrain sampler for terrain, buildings and later road draping. `sample(x, y)` is bilinear. `min_under(footprint)` is the lowest node of every DEM cell the footprint touches, so a building base is never above the terrain mesh. Nodata is filled from neighbouring cells.
- `build/terrain/`: the `TerrainBuilder` protocol (`build_tile(heightfield, tile) -> TerrainChunk`) and `MeshTerrainBuilder`, a regular grid at DEM resolution cut to the tile. Vertex normals come from the heightfield gradient (central differences of `Heightfield.sample`), so neighbouring tiles have identical edge vertices and normals.
- `build/lidar.py`: the building height from the point cloud (buffer ladder, tree check, limits; see "Building heights" below).
- `build/buildings.py`: height resolution, base height, flat-roof extrusion (holes supported) and collision boxes: one minimum rotated rectangle per footprint part. Courtyards and concave corners are filled in the collision box.
- `build/export_glb.py`: one `.glb` per tile with trimesh, checking the node names after export.
- `build/pack_manifest.py` and `build/schemas/manifest-v1.schema.json`: the manifest model, the committed JSON Schema, and the credits.
- `build/camera_presets.py` and `build/schemas/camera-presets-v1.schema.json`: the optional camera presets model and its committed JSON Schema. `run_build` validates `maps/<map-id>/camera_presets.json` before the load stage and copies it into the pack.
- `build/orthophoto.py`: terrain UVs (`terrain_uv`) and the debug orthophoto textures. It opens the imagery files as one mosaic, fails the build if they don't cover every tile completely, and writes one JPEG per tile resampled (average) to exactly the tile's bounds, plus `debug/.gdignore`.
- `build/pack_io.py`: replacing the pack folder safely. The pack is written to a temporary sibling folder, validated, then swapped in, so a failed build leaves the previous pack untouched. A non-empty target without a `manifest.json` is never replaced.

Intermediate output goes to `tools/mapgen/build/<map-id>/<area>/` (git-ignored). The GeoJSON files are in map CRS world coordinates with a legacy `crs` member, so QGIS places them correctly:

- `01-load/`: `footprints.geojson`, `dem.tif` (cropped), `report.json` (counts, skipped and partial features with reasons, DEM statistics including filled nodata cells and any warped CRSs).
- `02-terrain/`: `<tile>.glb` (terrain only) and `stats.json`.
- `03-buildings/`: `buildings.geojson` (resolved attributes and their sources) and `report.json` (count per height source, lidar rejections per reason, buildings per lidar buffer, buildings using single returns, ignored tags).
- `timings.json`: seconds per stage.

Building heights, first valid value wins (spec: `docs/specs/building-heights.md`):

1. Manual override (comes later with `mapgen enhance`).
2. `mml_lidar`: the non-ground points inside the footprint shrunk by 1.0 m, else 0.5 m, else 0 m, using the first buffer with at least 5 points. If the 90th minus 10th percentile spread is over 8 m (trees over the roof), only single returns are used. The height is the median point elevation minus `base_z`, so the flat roof sits at the measured median. Values under 1 m or over 40 m are rejected (`too_low`, `too_high`), as are footprints without enough points (`few_points`, `tree_cover`). All thresholds are `mml_lidar` options in `map.yaml`.
3. `mml_3d`: MML 3D measured height, when a 3D building covers at least half the footprint (stub, no data yet).
4. `osm_height`: OSM `height` (`12`, `12 m`, `12.5`; invalid or ≤ 0 values are ignored).
5. `osm_levels`: `building:levels` × 3 m.
6. `default`: 2 floors × 3 m.

TODO: the `enhance` command.

### Map pack: `game/maps/<map-id>/`

Data only: meshes, scenes and JSON, with no code (ADR 0002). Planned contents: tiles, road graph, markers, vegetation, minimap, `boundary.json`, and `manifest.json` with a schema version and the map's data credits. Map packs are git-ignored and regenerated by the pipeline. The full and test builds write the same folder; the manifest's `area` says which one it holds (ADR 0006).

Contents so far:

- `manifest.json` (schema version 1).
- `tiles/tile_x{i}_y{j}.glb`: one file per tile. Tile (i, j) covers local [i·T, (i+1)·T) × [j·T, (j+1)·T), clipped to the area, where T is `tile_size_m`. Each file holds the terrain and the buildings whose footprint centroid is in the tile. Buildings are never split. The terrain has TEXCOORD_0 mapping the tile's `bounds_local` onto [0, 1] × [0, 1], with v = 0 at the north edge (u = (east − min_x) / width, v = (max_y − north) / height), so the tile's debug texture covers it exactly. Buildings have no UVs.
- `buildings.json`: `schema_version`, `map_id` and `buildings`, one provenance entry per building: `id` (e.g. `osm:way/123`), `tile`, `node`, `collision_nodes` (a list, one per footprint part), `height_m`, `height_source` (`mml_lidar`, `mml_3d`, `osm_height`, `osm_levels` or `default`), `base_z`, `base_source` (`dem`), `roof_shape` (`flat`) and `roof_shape_source` (`default`). Lidar buildings add `lidar_points`, `lidar_buffer_m`, `lidar_returns` (`all` or `single`), `roof_z_median` and `roof_z_p90` (absolute elevations; p90 is kept for pitched roofs later). A building whose lidar value was rejected adds `lidar_rejected` with the reason.
- `debug/` (optional, ADR 0006 amendment): written only when an `imagery` source is enabled and its files are present. `debug/.gdignore` (empty) keeps Godot from importing or exporting the folder; the game loads the files from disk at runtime, in debug runs only. Not listed in the manifest.
  - `debug/orthophoto/<tile_id>.jpg`: the orthophoto resampled to exactly the tile's bounds, round(width / r) × round(height / r) pixels at r = `debug_texture_m_per_px`, JPEG quality 85, top row north.
- `camera_presets.json` (optional, ADR 0006 amendment): a byte-for-byte copy of `maps/<map-id>/camera_presets.json`, validated by the build and absent when the map has none. Not listed in the manifest. Fields: `schema_version` (`1`) and `presets`, a non-empty object from preset name (lowercase slug, e.g. `centre-street-level`) to:

  | Field | Meaning |
  | --- | --- |
  | `position` | Camera position `[x, y, z]` in Godot local metres |
  | `look_at` | Point the camera looks at, `[x, y, z]`; must differ from `position` |
  | `fov` | Vertical field of view in degrees, 1–179, default 70 |
  | `ground_relative` | Default false. When true, `position[1]` and `look_at[1]` are heights above the terrain at that point's x/z |

  Unknown keys are errors. JSON Schema: `tools/mapgen/src/mapgen/build/schemas/camera-presets-v1.schema.json`.

Axes: glTF is Y-up. Local east is +X, up is +Y, and north is −Z. Units are metres from the map origin.

glTF node names: `terrain-col` (visual mesh plus collision), `bld_<source>_<type>_<id>` (building visual, e.g. `bld_osm_way_123`) and `bld_<source>_<type>_<id>-colonly` (collision box only). A building with several footprint parts gets one box per part: `bld_<...>_c0-colonly`, `bld_<...>_c1-colonly`, and so on.

Manifest v1 fields, validated against the committed JSON Schema `tools/mapgen/src/mapgen/build/schemas/manifest-v1.schema.json` after every build:

| Field | Meaning |
| --- | --- |
| `schema_version` | `1` |
| `map_id`, `display_name` | From `map.yaml` |
| `area` | `full` or `test` |
| `crs`, `origin` | The map CRS and the local origin `{x, y}` in it (ADR 0003) |
| `height_datum` | What the heights are: currently `source DEM heights, metres` |
| `tile_size_m` | Tile edge in metres |
| `axes` | `{x: "east", y: "up", z: "-north"}` |
| `tiles[]` | `id`, `file` (relative to the pack), `bounds_local` `[min_x, min_y, max_x, max_y]`, `building_count` |
| `credits[]` | `source`, `dataset`, `licence`, `attribution`, `retrieved_at`, taken from the download manifest entries of the raw files the build read. Files with the same source, dataset, licence and attribution fold into one credit with the latest `retrieved_at` |
| `generated_at` | UTC time of the build |
| `tool` | `{name, version}` of the pipeline |

TODO: the JSON formats of the road graph, markers, vegetation and boundary. TODO: shipping packs as runtime-loaded `.pck` files.

### MapLoader (Godot)

A node in the `World` scene. It reads the map id from settings or a menu, checks the manifest's schema version, and instances tiles. It loads all tiles at first and adds streaming near the camera later. It also places barrier scenes from `boundary.json`.

Tile `.glb` files import with the project-wide `[importer_defaults]` (ADR 0008). The screenshot tool renders the pack's camera presets for visual checks (ADR 0008, `game/CLAUDE.md`).

TODO: the streaming design.

### Roadman.Core (engine-free C#)

A plain C# class library with no Godot dependency, tested with `dotnet test`. It contains the road graph types and parsing, routing, the wanted level, mission state, and the death and respawn state machine (including clearing money and gear).

Projects (all `net10.0`, ADR 0007):

- `game/Roadman.Core/`: the library. The Godot project references it and excludes its folder from its own compile glob. A `.gdignore` keeps Godot from scanning it.
- `game/Roadman.Core.Tests/`: xUnit tests, run with `dotnet test game/Roadman.Core.Tests`.

**`PlayerState`** holds the player's money, gear and wanted level. The `GameState` autoload owns one instance and forwards its events as `Events` signals; the rules live here, not in the autoload.

- Money is a whole number, never negative. Adding or spending a negative amount is an error. A purchase the player can't afford fails and changes nothing.
- Gear is a set of string item ids.
- The wanted level runs from 0 to 5 (`MaxWantedLevel`). Setting it outside that range clamps it.
- Death clears all money and gear, and resets the wanted level to 0.
- `MoneyChanged`, `GearChanged` and `WantedLevelChanged` fire only when the value actually changes; `Died` fires on every death.

TODO: road graph types and routing, mission state, and the respawn state machine.

### Gameplay (Godot C#)

- Autoloads: `Events` (global signal bus) and `GameState` (player, wanted level, settings). Both stay small.
- `World` scene: MapLoader, WorldEnvironment, sun, debug camera.
- Vehicle (`VehicleBody3D`), chase camera, HUD with minimap, input map.
- World edge: barrier scenes, the void `Area3D` and its death sequence, space backdrop.
- Missions refer to marker ids in the pack, never to raw coordinates.

TODO: traffic AI, pedestrian AI, police, missions, player character. Each of these gets a spec in `docs/specs/` before implementation.

## Testing

- Pipeline: pytest, using a fixed small test area. `tools/mapgen/tests/fixtures/raw/somero-test-area/` holds the Somero test area's raw data (OSM without contributor metadata, DEM, lidar and orthophoto cuts, download manifest, attribution in `NOTICE.md`), laid out like `data/raw/<map-id>/`, so tests and CI can build the test-area pack offline. `tools/mapgen/scripts/cut_test_area.py` recreates it.
- Core: xUnit on `Roadman.Core`.
- Engine: gdUnit4Net, run headless.
- Visual: screenshots from the camera presets in the map pack, taken with a short windowed run (ADR 0008).
- CI: builds Somero plus a tiny second test area from another town. TODO: set up CI (prompt 7).
