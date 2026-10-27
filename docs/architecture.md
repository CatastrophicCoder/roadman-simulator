# Architecture

This file describes the systems and how they connect. Source: `docs/project-outline.md`. Parts that are not designed yet are marked **TODO**. Decisions are recorded in `docs/decisions/`.

## Data flow

```
  OSM (Overpass / Geofabrik)   MML (DEM 2 m, orthophoto, 3D buildings, topo DB)
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
          vegetation, boundary.json, minimap.png
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
| `sources` | Named sources, each with `adapter`, `enabled` and adapter-specific `options` |

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

Building attribute precedence: manual override, then enhancement data, then OSM tags, then MML 3D, then style preset defaults.

Collision comes from glTF node name suffixes (`-col`, `-colonly`, `-convcolonly`).

Module layout (`tools/mapgen/src/mapgen/`):

- `cli.py`: `mapgen download <map-id> [--only a,b] [--force]`, `mapgen origin <map-id>` and `mapgen build <map-id> [--area full|test] [--out DIR]`, with `--repo-root` (found automatically by default). `enhance` will be added as a further subcommand.
- `config.py`: the `map.yaml` model and loader. `paths.py`: repo root, `maps/<map-id>/`, `data/raw/<map-id>/`, the build work folder and the pack folder.
- `geo.py`: WGS84 to map CRS, projected bbox, world and local coordinates, test area, geometry reprojection and offsets.
- `tm35.py`: ETRS-TM35FIN map sheet names for a bbox (used by MML adapters).
- `http.py`: a small urllib client behind a protocol, so tests can stub it.
- `sources/`: the adapter interface (`base.py`), the registry, and the adapters `osm_overpass`, `mml_dem_2m`, `mml_orthophoto`, `mml_buildings_3d` and `mml_topographic` (ADR 0005).
- `download.py` and `manifest.py`: run the enabled sources and write `data/raw/<map-id>/download-manifest.json` with the checksum, licence and attribution of each file.

Adapter interface: a source's `adapter` value selects a `SourceAdapter` class, whose options model validates the source's `options`. `download(ctx)` gets the map config, the raw folder, the `--force` flag and an injected HTTP client, and returns one `DownloadedFile` record per file. Each adapter also declares what it can load in `provides` (`osm_vector`, `elevation`, `buildings_3d`), and `load(ctx)` reads its raw files for a build and returns a `SourceData` (footprints, an elevation raster, skipped features, and the files it read). The build picks enabled sources by capability, never by source name, and allows at most one source per capability.

- `osm_overpass` loads building footprints from the `.osm` XML (standard library `iterparse`): closed ways and `type=multipolygon` relations, with rings joined from member ways and both outer and inner rings repaired with `make_valid`. Unclosed rings are dropped. Features that can't be built (no closed outer ring, geometry errors, duplicates of a relation's outer way) are skipped with a reason; relations built despite missing member ways or unclosed rings are listed as partial (ADR 0005).
- `mml_dem_2m` loads its GeoTIFFs, mosaics them with `rasterio.merge`, and crops to the area plus a 10 m margin. A file is warped only when its CRS isn't equivalent to the map CRS (compared semantically, not as text); warped CRSs are listed in the load report. Nodata becomes NaN.
- `mml_buildings_3d` has a stub `load` that returns no buildings, because its file format isn't confirmed (ADR 0005).

Build stages (`build/`; ADR 0006):

- `build/__init__.py`: `run_build(config, area, raw_dir, out_dir, work_dir)` runs the stages in order and writes the pack.
- `build/area.py`: `BuildArea` (the full bbox or the test area in local coordinates, rounded outward to whole metres) and the tile grid.
- `build/stage1_load.py`: loads the sources by capability, reprojects footprints to the map CRS, subtracts the origin, and keeps footprints whose centroid is in the area.
- `build/heightfield.py`: `Heightfield`, the single terrain sampler for terrain, buildings and later road draping. `sample(x, y)` is bilinear. `min_under(footprint)` is the lowest node of every DEM cell the footprint touches, so a building base is never above the terrain mesh. Nodata is filled from neighbouring cells.
- `build/terrain/`: the `TerrainBuilder` protocol (`build_tile(heightfield, tile) -> TerrainChunk`) and `MeshTerrainBuilder`, a regular grid at DEM resolution cut to the tile. Vertex normals come from the heightfield gradient (central differences of `Heightfield.sample`), so neighbouring tiles have identical edge vertices and normals.
- `build/buildings.py`: height resolution, base height, flat-roof extrusion (holes supported) and collision boxes: one minimum rotated rectangle per footprint part. Courtyards and concave corners are filled in the collision box.
- `build/export_glb.py`: one `.glb` per tile with trimesh, checking the node names after export.
- `build/pack_manifest.py` and `build/schemas/manifest-v1.schema.json`: the manifest model, the committed JSON Schema, and the credits.
- `build/pack_io.py`: replacing the pack folder safely. The pack is written to a temporary sibling folder, validated, then swapped in, so a failed build leaves the previous pack untouched. A non-empty target without a `manifest.json` is never replaced.

Intermediate output goes to `tools/mapgen/build/<map-id>/<area>/` (git-ignored). The GeoJSON files are in map CRS world coordinates with a legacy `crs` member, so QGIS places them correctly:

- `01-load/`: `footprints.geojson`, `dem.tif` (cropped), `report.json` (counts, skipped and partial features with reasons, DEM statistics including filled nodata cells and any warped CRSs).
- `02-terrain/`: `<tile>.glb` (terrain only) and `stats.json`.
- `03-buildings/`: `buildings.geojson` (resolved attributes and their sources) and `report.json` (count per height source, ignored tags).
- `timings.json`: seconds per stage.

Building heights: MML 3D measured height (when a 3D building covers at least half the footprint), OSM `height` (`12`, `12 m`, `12.5`; invalid or ≤ 0 values are ignored), `building:levels` × 3 m, then 2 floors × 3 m. Manual overrides and enhancement data come later (`mapgen enhance`).

TODO: the `enhance` command.

### Map pack: `game/maps/<map-id>/`

Data only: meshes, scenes and JSON, with no code (ADR 0002). Planned contents: tiles, road graph, markers, vegetation, minimap, `boundary.json`, and `manifest.json` with a schema version and the map's data credits. Map packs are git-ignored and regenerated by the pipeline. The full and test builds write the same folder; the manifest's `area` says which one it holds (ADR 0006).

Contents so far:

- `manifest.json` (schema version 1).
- `tiles/tile_x{i}_y{j}.glb`: one file per tile. Tile (i, j) covers local [i·T, (i+1)·T) × [j·T, (j+1)·T), clipped to the area, where T is `tile_size_m`. Each file holds the terrain and the buildings whose footprint centroid is in the tile. Buildings are never split.
- `buildings.json`: `schema_version`, `map_id` and `buildings`, one provenance entry per building: `id` (e.g. `osm:way/123`), `tile`, `node`, `collision_nodes` (a list, one per footprint part), `height_m`, `height_source` (`mml_3d`, `osm_height`, `osm_levels` or `default`), `base_z`, `base_source` (`dem`), `roof_shape` (`flat`) and `roof_shape_source` (`default`).

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

TODO: the streaming design, and the import presets for `.glb` files.

### Roadman.Core (engine-free C#)

A plain C# class library with no Godot dependency, tested with `dotnet test`. It contains the road graph types and parsing, routing, the wanted level, mission state, and the death and respawn state machine (including clearing money and gear).

TODO: the project structure and the public API (prompt 5).

### Gameplay (Godot C#)

- Autoloads: `Events` (global signal bus) and `GameState` (player, wanted level, settings). Both stay small.
- `World` scene: MapLoader, WorldEnvironment, sun, debug camera.
- Vehicle (`VehicleBody3D`), chase camera, HUD with minimap, input map.
- World edge: barrier scenes, the void `Area3D` and its death sequence, space backdrop.
- Missions refer to marker ids in the pack, never to raw coordinates.

TODO: traffic AI, pedestrian AI, police, missions, player character. Each of these gets a spec in `docs/specs/` before implementation.

## Testing

- Pipeline: pytest, using a fixed small test area.
- Core: xUnit on `Roadman.Core`.
- Engine: gdUnit4Net, run headless.
- Visual: headless screenshots from fixed camera positions.
- CI: builds Somero plus a tiny second test area from another town. TODO: set up CI (prompt 7).
