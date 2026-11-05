# Architecture

This file describes the systems and how they connect. Source: `docs/project-outline.md`. Parts that are not designed yet are marked **TODO**. Decisions are recorded in `docs/decisions/`.

## Data flow

```
  OSM (Overpass / Geofabrik)   MML (DEM 2 m, lidar 0.5 p, orthophoto, 3D buildings, topo DB)
              \                     /
               v                   v
        tools/mapgen/data/raw/<map-id>/        (downloaded once, git-ignored)
                       |  \
                       |   mapgen snapshot create / publish (user only)
                       |   -> GitHub release data-<map-id>-<date>[.<n>] (zip asset)
                       |   -> mapgen snapshot fetch (fresh clones, release.yml)
                       |      back into data/raw/<map-id>/  (ADR 0011)
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
- `markers.json` (required, ADR 0015): version 1 holds `schema_version` 1 and `spawn_area_wgs84`, the spawn area as a list of `{lat, lon}` points. Unknown keys are errors; committed schema `tools/mapgen/src/mapgen/build/schemas/markers-v1.schema.json`; validated before the load stage. Parking spots and gameplay points of interest with fictional names come later with a version change. The real OSM points of interest are in the pack's `pois.json` (ADR 0013).
- `overrides/`: per-building fixes and appearance, keyed by OSM or MML building id. Each override records its source and licence. So far `overrides/buildings.json` (ADR 0013): `category` and name fixes; licence `own work`, or a third-party licence with `attribution` and `retrieved_at`, which adds a manifest credit. Committed schema `tools/mapgen/src/mapgen/build/schemas/building-overrides-v1.schema.json`.
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
| `snapshot` | Optional. The pinned raw data snapshot that releases build from (ADR 0011): `repo` (`<owner>/<name>`), `release_tag` (`data-<map id>-YYYY-MM-DD`, or `data-<map id>-YYYY-MM-DD.<n>` with n ≥ 2 and no leading zeros when that date already had a data release), `asset` (a `.zip` file name; publish names it `mapdata-<map id>-YYYY-MM-DD[.<n>].zip` after the tag) and `sha256` (64 lowercase hex). Written by `mapgen snapshot publish` |
| `world_edge` | Optional (ADR 0015, spec `docs/specs/world-edge-pipeline.md`). Every key has a pipeline default; list only overrides: `boundary_wgs84` (a custom boundary polygon as `{lat, lon}` points; absent: the build area rectangle), `building_clearance_m`, `min_feature_width_m` (3.0: a spike or notch of the boundary narrower than this fails the build before the pack is touched, for every boundary source; 0 turns it off), `fence` (`max_segment_m`, `max_terrain_deviation_m`), `roadblock` (`clearance_m`, `merge_distance_m`), `cliff` (`ledge_m`, `depth_m`), `void` (`inner_offset_m`, `width_m`, `top_margin_m`) and `spawn` (`obstacle_buffer_m`, `min_open_area_m2`, `min_clear_radius_m`, `min_boundary_distance_m`). Unknown keys are errors |
| `roads` | Optional (ADR 0012). `surface_offset_m`, `default_speed_kmh`, `bridge` (`deck_thickness_m`, `railing_height_m`, `railing_width_m`, `min_clearance_m`, `inferred_waterways`, `ramp_length_m`, `rim_search_m`, `walk_rim_search_m`, `max_ramp_grade`, `dip_report_m`, `ground_above_deck_warning_m`, `lidar_fit`) and `classes`: per road class `width_m`, `lanes`, `drivable`, `walkable` and optional `speed_kmh`. Without it the map builds without road meshes and with an empty `road_graph.json` |

The playable boundary (ADR 0015) is the build area rectangle by default, or the custom polygon `world_edge.boundary_wgs84` in a full build; every test build uses the test-area square. The polygon must be a single valid, simple polygon inside the build area; points up to 0.05 m outside it are clamped onto its edge. Somero uses boundary B (162 points).

### Map pipeline: `tools/mapgen/`

A Python pipeline that takes a map id and produces a self-contained map pack. Data sources sit behind adapters with one interface: OSM (worldwide), MML (Finland), and a global elevation model (for example Copernicus GLO-30).

Stages, each with output that can be inspected:

1. Load and project to EPSG:3067, then subtract the local origin (ADR 0003).
2. Terrain: behind a `TerrainBuilder` interface. Mesh terrain is the interim implementation; ADR 0004 (mesh or Terrain3D heightmap) is still open.
3. Buildings: extruded footprints, with attributes resolved by the enhancement layer. OSM footprints, plus the MML Topographic Database buildings no OSM building matched, with the parts overlapping OSM buildings cut away (ADR 0014).
4. Roads: buffered, merged, cut along the terrain mesh triangles and laid `surface_offset_m` above them; bridge decks and inferred bridges (ADR 0012).
5. Water and land use.
6. Vegetation: positions only.
7. Road graph: nodes at OSM nodes, edges with class, drivable and walkable flags, lanes, speed limit, width, one-way flag and names. Built in the same `roads` stage as 4, because the graph's heights and the inferred bridges depend on the surface.
8. Gameplay markers.
9. Tiling (256 m to start) and export: `.glb` per tile, JSON files, minimap PNG.
10. World edge (ADR 0015): roadblocks where road graph `area_edge` nodes are (with traffic roles), land and water fences along the rest of the boundary, the cliff (as tile nodes: the ledge, the face, seams closing the height steps between pieces, a skirt from the ledge's inner edge to the terrain mesh's edge, and the bottom cap), the void trigger prisms and the spawn area check, written to `boundary.json`. Runs after the roads stage.

Building attribute precedence: manual override, then enhancement data, then measured data (MML lidar, then MML 3D), then OSM tags, then style preset defaults. The building category has its own order, with OSM tags before MML use classes (ADR 0013, "Building categories" below).

Collision comes from glTF node name suffixes (`-col`, `-colonly`, `-convcolonly`).

Module layout (`tools/mapgen/src/mapgen/`):

- `cli.py`: `mapgen download <map-id> [--only a,b] [--force] [--release] [--strict] [--retries N]`, `mapgen origin <map-id>`, `mapgen build <map-id> [--area full|test] [--out DIR] [--release]` and `mapgen notice <map-id>... (--out FILE | --check FILE)` and `mapgen snapshot (create [--out DIR] | publish [--archive PATH] [--repo OWNER/NAME] | fetch [--force]) <map-id>`, with `--repo-root` (found automatically by default). `enhance` will be added as a further subcommand.
  - `download --release` skips `debug_only` sources (logs `[<name>] skipped (debug only)`); naming one in `--only` together with `--release` is a usage error (exit 2).
  - `download --strict` fails (exit 1) when a selected source returned no files, or when its adapter's required environment variables are unset (the MML adapters need `MML_API_KEY`; with `--strict` they neither print manual steps nor register files already on disk). Each failed source is reported with its reason; the key is never printed. Without `--strict` nothing changes.
  - `build --release` treats `debug_only` sources as disabled, even when their raw files exist.
  - `notice` reads each built pack's `manifest.json` credits and writes a plain-text NOTICE: a heading, a sentence that the data was modified (processed into 3D map tiles), per map each credit's attribution exactly as in the manifest followed by `Licence: <name>, <url>`, and the licence URLs once each. A missing pack or manifest, an invalid manifest or a licence without a known URL fails (exit 1). `--check FILE` exits 1 and lists every attribution or licence line missing from FILE (whole lines, surrounding spaces ignored).
  - `snapshot create` packs the manifest-listed files of the enabled, non-`debug_only` sources (OSM contributor metadata removed), the filtered download manifest, `SHA256SUMS` and a generated `NOTICE.md` into a deterministic zip in `tools/mapgen/build/snapshots/`, and runs `.github/scripts/check-no-secret.sh` on it. It reads only `data/raw/<map-id>/`. `snapshot publish` (run only by the user) lists the existing releases and tags with `gh`, takes the next tag of the archive's date (`data-<map-id>-<date>` when none exists, else the highest existing number plus one, the plain tag counting as 1), uploads the archive under the matching asset name as a new `data-*` release that is never latest, and writes the `snapshot` block. It never edits, deletes or uploads to an existing release, and stops without publishing if the listing fails. `snapshot fetch` downloads the pinned asset, checks its SHA-256 and members, and unpacks it into `data/raw/<map-id>/`; it fails on differing or extra local files of those sources unless `--force` (ADR 0011).
- `notice.py`: the NOTICE text, its expected lines and the licence name to URL table.
- `snapshot.py`: snapshot create, fetch and publish, and the `map.yaml` snapshot block writer.
- `config.py`: the `map.yaml` model and loader. `paths.py`: repo root, `maps/<map-id>/`, `data/raw/<map-id>/`, the build work folder and the pack folder.
- `geo.py`: WGS84 to map CRS, projected bbox, world and local coordinates, test area, geometry reprojection and offsets.
- `tm35.py`: ETRS-TM35FIN map sheet names for a bbox (used by MML adapters).
- `http.py`: a small urllib client behind a protocol, so tests can stub it.
- `sources/`: the adapter interface (`base.py`), the registry, and the adapters `osm_overpass`, `mml_dem_2m`, `mml_lidar`, `mml_orthophoto`, `mml_buildings_3d` and `mml_topographic` (ADR 0005).
- `download.py` and `manifest.py`: run the enabled sources and write `data/raw/<map-id>/download-manifest.json` with the checksum, licence and attribution of each file.

Adapter interface: a source's `adapter` value selects a `SourceAdapter` class, whose options model validates the source's `options`. `download(ctx)` gets the map config, the raw folder, the `--force` flag and an injected HTTP client, and returns one `DownloadedFile` record per file. Each adapter also declares what it can load in `provides` (`osm_vector`, `elevation`, `point_cloud`, `buildings_3d`, `imagery`, `building_use`, `water_areas`), and `load(ctx)` reads its raw files for a build and returns a `SourceData` (footprints, an elevation raster, a point cloud, imagery files, skipped features, and the files it read). The build picks enabled sources by capability, never by source name, and allows at most one source per capability.

- `osm_overpass` loads building footprints from the `.osm` XML (standard library `iterparse`): closed ways and `type=multipolygon` relations, with rings joined from member ways and both outer and inner rings repaired with `make_valid`. Unclosed rings are dropped. Features that can't be built (no closed outer ring, geometry errors, duplicates of a relation's outer way) are skipped with a reason; relations built despite missing member ways or unclosed rings are listed as partial (ADR 0005). From the same parse it also loads each footprint's `building`, address, name and POI tags, the points of interest (nodes, closed ways and multipolygon relations with `shop`, `amenity`, `office`, `tourism` or `leisure`), land use areas (a relation that can't be closed is left out and reported), the `highway` ways (with node ids and tags) and water: water areas (`natural=water`, `waterway=riverbank`; a relation with a missing member or an open ring is left out and reported) and waterway lines.
- `mml_dem_2m` loads its GeoTIFFs, mosaics them with `rasterio.merge`, and crops to the area plus a 10 m margin. A file is warped only when its CRS isn't equivalent to the map CRS (compared semantically, not as text); warped CRSs are listed in the load report. Nodata becomes NaN.
- `mml_lidar` downloads MML's laser scanning data 0.5 p as LAZ on TM35 1:5 000 sheets (`sheet_scale`, `dataset` options; `Uusin` is the latest). Only the process `laserkeilausaineisto_05_karttalehti` is allowed; the 5 p data is refused (ADR 0005 amendment). `load` reads the files with laspy and lazrs in chunks, keeps the points inside the requested bounds and drops classes 2 (ground), 7 (low noise) and 18 (high noise). Its options also hold the height thresholds used by stage 3.
- `mml_orthophoto` provides `imagery`: `load` lists the JPEG 2000 (or GeoTIFF) files that overlap the area, without reading pixels; the build reads them per tile for the debug textures. Missing files aren't an error: the build then logs it and writes no `debug/` folder. Its option `debug_texture_m_per_px` (default 0.5, 0.5–1.0) sets the texture resolution.
- `mml_topographic` provides `building_use` and `water_areas` (ADR 0015: tables `jarvi` and `virtavesialue` as water areas, `virtavesikapea` as water lines; a missing table fails the build, an empty one is fine): `load` reads table `rakennus` from the GPKG files in `mml/topographic/` with `sqlite3` and shapely's WKB reader (`mml:rakennus/<mtk_id>`, polygon, `kohdeluokka`, `kayttotarkoitus`). The feature-class to category table lives in the adapter (ADR 0013). An enabled source without a GPKG, or a GPKG without `rakennus`, fails the build.
- `mml_buildings_3d` has a stub `load` that returns no buildings: MML has no 3D building coverage for Somero, and its CityGML reader is out of scope (ADR 0005).

Build stages (`build/`; ADR 0006):

- `build/__init__.py`: `run_build(config, area, raw_dir, out_dir, work_dir, *, release=False)` runs the stages in order and writes the pack. With `release`, `debug_only` sources are disabled first.
- `build/area.py`: `BuildArea` (the full bbox or the test area in local coordinates, rounded outward to whole metres, plus the custom boundary polygon when there is one) and the tile grid.
- `build/markers.py` and `build/schemas/markers-v1.schema.json`: the `markers.json` model (the spawn area), validated before the load stage.
- `build/stage1_load.py`: loads the sources by capability, reprojects footprints to the map CRS, subtracts the origin, and keeps footprints whose centroid is in the area. The non-ground point cloud is read around those footprints and the in-area building-use records (ADR 0014), the ground points (class 2) in a second read around those records for the MML-only lidar check, and, for deck heights, around ways with `bridge`, `tunnel` or `layer` tags and around water, and moved to local coordinates.
- `build/heightfield.py`: `Heightfield`, the single terrain sampler for terrain, buildings and later road draping. `sample(x, y)` is bilinear. `min_under(footprint)` is the lowest node of every DEM cell the footprint touches, so a building base is never above the terrain mesh. Nodata is filled from neighbouring cells.
- `build/terrain/`: the `TerrainBuilder` protocol (`build_tile(heightfield, tile) -> TerrainChunk`) and `MeshTerrainBuilder`, a regular grid at DEM resolution cut to the tile. Vertex normals come from the heightfield gradient (central differences of `Heightfield.sample`), so neighbouring tiles have identical edge vertices and normals. `terrain/clip.py` cuts a tile's terrain along a custom boundary polygon: triangles inside are kept, crossed ones replaced by their inside part in the same plane, the rest removed; a tile with no terrain left isn't written.
- `build/lidar.py`: the building height from the point cloud (buffer ladder, tree check, limits; see "Building heights" below).
- `build/buildings.py`: height resolution, base height, flat-roof extrusion (holes supported) and collision meshes: each footprint part extruded from base to roof like the visual mesh (geometrically closed, outward faces), so courtyards stay open and concave corners stay concave (ADR 0006 amendment 2026-11-04). The pack checks require every collision mesh to stay within 0.05 m of the building's footprint, to match the visual mesh's outline and height, and to face outward.
- `build/roads/` (ADR 0012, spec `docs/specs/roads-and-road-graph.md`): `tags.py` (class, flags, value parsing, name fields), `graph.py` (clipping, nodes, edges, ids, connected components), `bridges.py` (spans, straight decks from the lidar fit or the rim line, span extent and `span_end` nodes, railings, inferred bridges over water and underpasses, clearance), `ramps.py` (road-side ramps at deck ends, and gradient-limited ramps for walk-only decks), `decks.py` (deck heights: lidar consensus fit and the geometric rim fallback), `surface.py` (buffering, merging, draping on each tile's terrain chunk triangles, tile-edge points), `model.py` and `build/schemas/road-graph-v1.schema.json` (the `road_graph.json` model and its committed JSON Schema).
- `build/names.py`: the one implementation of the name fields (`name`, `name_sv`, `ref`) and address values, used by roads, buildings and POIs.
- `build/places/` (ADR 0013, spec `docs/specs/place-information.md`): `categories.py` (the OSM `building`, POI and `landuse` tables), `resolve.py` (category precedence, names, addresses), `matching.py` (MML matching), `links.py` (POI to building links), `overrides.py` (the override file, validated before the load stage), `pois.py` (`pois.json` entries and outlines) and `model.py` with the committed schemas `buildings-v2`, `pois-v1` and `building-overrides-v1`.
- `build/mml_only.py` (ADR 0014, spec `docs/specs/mml-only-buildings.md`): which building-use records no OSM building matched become buildings, cutting away the OSM overlap, the sliver test (under 4 m², or under 50% surviving a 0.75 m opening), the lidar contradiction check, road overlaps and the report's `mml_only` block.
- `build/world_edge/` (ADR 0015, spec `docs/specs/world-edge-pipeline.md`): `ring.py` (the boundary ring, distances `s` along it and circular stretches), `roadblocks.py` (spans from the merged surfaces, merging, roles), `fences.py` (segmentation and the terrain deviation), `water.py` (water crossings, water lines, OSM/MML disagreements), `void.py` (the trigger prisms, at least `void.inner_offset_m` from the whole ring, and their coverage), `narrow.py` (spikes and notches narrower than `min_feature_width_m`), `spawn.py` (buildings crossing the boundary, the spawn check), `cliff.py` (the cliff meshes: ledge, face, seams, skirt and bottom cap per tile, flat-shaded) and `model.py` with the committed schema `boundary-v1`. `__init__.py` picks the boundary (`boundary_for`) and runs the stage.
- `build/export_glb.py`: one `.glb` per tile with trimesh, checking the node names after export and reading back every mesh's positions, indices and given normals. `flat_shaded` makes a node whose triangles have their own vertices with the face normal (the cliff).
- `build/pack_checks.py`: the pack checks (tiles, buildings, road and path surfaces including ramps, deck straightness, deck-end steps, edge ends at their nodes, terrain under decks, place values and links, POI positions and outline rings, `mml:` buildings with `mml_building` equal to their id and `mml_overlap` 1.0 and not overlapping any `osm:` footprint by 0.01 m² or more; the world edge, the cliff and the custom boundary polygon, ADR 0015). `run_build` runs them on the staged pack in every build, including `--release`; a failed check fails the build and leaves the previous pack. The tests call the same checks.
- `build/pack_manifest.py` and `build/schemas/manifest-v1.schema.json`: the manifest model, the committed JSON Schema, and the credits.
- `build/camera_presets.py` and `build/schemas/camera-presets-v1.schema.json`: the optional camera presets model and its committed JSON Schema. `run_build` validates `maps/<map-id>/camera_presets.json` before the load stage and copies it into the pack.
- `build/orthophoto.py`: terrain UVs (`terrain_uv`) and the debug orthophoto textures. It opens the imagery files as one mosaic, fails the build if they don't cover every tile completely, and writes one JPEG per tile resampled (average) to exactly the tile's bounds, plus `debug/.gdignore`.
- `build/pack_io.py`: replacing the pack folder safely. The pack is written to a temporary sibling folder, validated, then swapped in, so a failed build leaves the previous pack untouched. A non-empty target without a `manifest.json` is never replaced.

Intermediate output goes to `tools/mapgen/build/<map-id>/<area>/` (git-ignored). The GeoJSON files are in map CRS world coordinates with a legacy `crs` member, so QGIS places them correctly:

- `01-load/`: `footprints.geojson`, `dem.tif` (cropped), `report.json` (counts, skipped and partial features with reasons, DEM statistics including filled nodata cells and any warped CRSs, point counts and read bounds including `ground_points` and `ground_points_bounds_world`).
- `02-terrain/`: `<tile>.glb` (terrain only) and `stats.json`.
- `03-buildings/`: `buildings.geojson` (resolved attributes and their sources) and `report.json` (count per height source, lidar rejections per reason, buildings per lidar buffer, buildings using single returns, ignored tags; count per category and category source, MML matching counts, class 6 split at 40 m², class 2 decisions, unmapped raw values, conflicting POI categories, POIs covered by several footprints, applied and unmatched overrides; the `mml_only` block: limits, records added whole or cut and not added per reason, the cut records with their kept pieces' area, opened share and compactness, records removed by the lidar rule, added buildings per class, height source and category, those without a lidar height, and those overlapping a road or path surface or a bridge deck).
- `04-roads/`: `roads.geojson` (merged surfaces and bridge outlines) and `report.json` (skipped ways with reasons, defaults used, clearance warnings, `suspected_missing_bridges` with OSM id and local, map CRS and WGS84 positions, excluded crossings, water relations left out, `ramps`, `terrain_above_deck`, per-span deck height method and fit, `suspected_dips`, `warnings`).
- `07-road-graph/`: `graph.geojson` (nodes and edges for QGIS) and `report.json` (per-class counts, weakly and strongly connected components, the largest drivable component's share and, in full builds, the 95% check).
- `08-places/`: `pois.geojson` and `report.json` (POI counts by type, named and unnamed, linked and unlinked, skipped and partial POIs with reasons, land use areas used and left out).
- `10-world-edge/`: `boundary.geojson` (the ring, roadblock spans, fence segments by type, water crossings and areas, void footprints, the spawn polygon and open ground) and `report.json` (counts, roadblocks per class, merged roadblocks, roles, surfaces touching the boundary without a crossing, water crossings with endpoints, ids and source, water lines, OSM/MML disagreements, dropped buildings, heights, `void` (prisms, their smallest distance to the ring, the arc slack), the spawn check, and `cliff`: pieces, corners, triangles per kind and per node, pieces and cap parts moved to another tile). With a custom polygon, `02-terrain/stats.json` gains `boundary_cut` (triangles kept, cut and removed per tile, tiles not written).
- `timings.json`: seconds per stage.

Building heights, first valid value wins (spec: `docs/specs/building-heights.md`):

1. Manual override (comes later with `mapgen enhance`).
2. `mml_lidar`: the non-ground points inside the footprint shrunk by 1.0 m, else 0.5 m, else 0 m, using the first buffer with at least 5 points. If the 90th minus 10th percentile spread is over 8 m (trees over the roof), only single returns are used. The height is the median point elevation minus `base_z`, so the flat roof sits at the measured median. Values under 1 m or over 40 m are rejected (`too_low`, `too_high`), as are footprints without enough points (`few_points`, `tree_cover`). All thresholds are `mml_lidar` options in `map.yaml`.
3. `mml_3d`: MML 3D measured height, when a 3D building covers at least half the footprint (stub, no data yet).
4. `osm_height`: OSM `height` (`12`, `12 m`, `12.5`; invalid or ≤ 0 values are ignored).
5. `osm_levels`: `building:levels` × 3 m.
6. `default`: 2 floors × 3 m.

Building categories (ADR 0013), first step with a value wins: `override`, `osm_building`, `osm_poi`, `osm_poi_area`, `mml_use_class` (MML `kohdeluokka`; 4226x only under 40 m²), `osm_landuse`, the MML 4222x fallback to `commercial`, else `unknown`. Every source's raw value is kept in `category_raw`.

TODO: the `enhance` command.

### Map pack: `game/maps/<map-id>/`

Data only: meshes, scenes and JSON, with no code (ADR 0002). Planned contents: tiles, road graph, markers, vegetation, minimap, `boundary.json` (written since ADR 0015), and `manifest.json` with a schema version and the map's data credits. Map packs are git-ignored and regenerated by the pipeline. The full and test builds write the same folder; the manifest's `area` says which one it holds (ADR 0006).

Contents so far:

- `manifest.json` (schema version 1).
- `tiles/tile_x{i}_y{j}.glb`: one file per tile. Tile (i, j) covers local [i·T, (i+1)·T) × [j·T, (j+1)·T), clipped to the area, where T is `tile_size_m`. Each file holds the terrain, the buildings whose footprint centroid is in the tile, the tile's part of the road and path surfaces and bridge decks, and its part of the world edge cliff (ADR 0015). Buildings are never split; roads and decks are split at tile edges with identical vertices on both sides (ADR 0012). The terrain has TEXCOORD_0 mapping the tile's `bounds_local` onto [0, 1] × [0, 1], with v = 0 at the north edge (u = (east − min_x) / width, v = (max_y − north) / height), so the tile's debug texture covers it exactly. Buildings have no UVs.
- `buildings.json`: `schema_version`, `map_id` and `buildings`, one provenance entry per building: `id` (e.g. `osm:way/123`; an `mml:rakennus/<mtk_id>` id marks a footprint from the MML Topographic Database that no OSM building matched, ADR 0014), `tile`, `node`, `collision_nodes` (a list, one per footprint part), `height_m`, `height_source` (`mml_lidar`, `mml_3d`, `osm_height`, `osm_levels` or `default`), `base_z`, `base_source` (`dem`), `roof_shape` (`flat`) and `roof_shape_source` (`default`). Lidar buildings add `lidar_points`, `lidar_buffer_m`, `lidar_returns` (`all` or `single`), `roof_z_median` and `roof_z_p90` (absolute elevations; p90 is kept for pitched roofs later). A building whose lidar value was rejected adds `lidar_rejected` with the reason. Schema version 2 (ADR 0006 amendment, ADR 0013, committed schema `buildings-v2.schema.json`) adds `category`, `category_source`, `category_raw`, the name fields with `name_source`, `address` (`street`, `housenumber`) with `address_source`, `poi_ids`, `area_poi_ids`, `mml_building`, `mml_overlap` and `overrides` (`id`, `source`, `licence`, `fields` per applied override entry).
- `pois.json` (ADR 0013): `schema_version` (`1`), `map_id`, `area` and `pois`, sorted by id. Not listed in the manifest. JSON Schema: `tools/mapgen/src/mapgen/build/schemas/pois-v1.schema.json`. Each POI: `id`, the name fields, `type` (first of `shop`, `amenity`, `office`, `tourism`, `leisure`), `subtype`, `other_tags`, `geometry` (`point` or `area`), `position` `[x, y, z]` on the terrain, `outline` (areas: polygons of closed rings of `[x, z]`, exterior first, clipped to the area; `null` for points), `address` with `address_source` (`osm`, `building` or `null`), `building_id` and `contains_building_ids`.
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
- `boundary.json` (ADR 0015): always written, byte-identical for the same input, not listed in the manifest. JSON Schema: `tools/mapgen/src/mapgen/build/schemas/boundary-v1.schema.json`. Godot local metres (points `[x, y, z]`, footprints `[x, z]`); `s` is metres along the ring. Fields: `schema_version` (`1`), `map_id`, `area`, `boundary` (`ring` counter-clockwise from its lowest-x, lowest-n vertex, `length_m`, `source` `area`, `custom` or `test_area`), `roadblocks` (`id`, `nodes` with `node`, `edge` and `role` `spawn_despawn` or `turn_around`, `class`, `drivable`, `walkable`, `width_m`, `width_source`, `start`, `end`, `position`, `yaw_deg`, `outward`, `s_start`, `s_end`, `over_water`), `fences` (`id`, `type` `land` or `water`, `start`, `end`, `s_start`, `s_end`, `outward`, `water_ids` for water), `cliff` (`ledge_m`, `bottom_y`), `void_trigger` (`bottom_y`, `top_y`, `prisms` of convex `footprint` quads), `spawn_area` (`polygon`, `open_ground`, `open_area_m2`) and `water_crossings` (`s_start`, `s_end`, `source`, `water_ids`). Roadblocks and fences sorted by `s_start` tile the ring. The game reads it into `MapLoader.Boundary` (`BoundaryReader`); the player spawns from `spawn_area.open_ground` (ADR 0016).
- `road_graph.json` (ADR 0012): always written; empty `nodes` and `edges` when `map.yaml` has no `roads` block. Not listed in the manifest. JSON Schema: `tools/mapgen/src/mapgen/build/schemas/road-graph-v1.schema.json`. Coordinates are Godot local metres. Fields: `schema_version` (`1`), `map_id`, `area`, `nodes` and `edges`.
  - Node: `id` (`osm:node/<id>`, `cut:way/<id>:<n>` or `span:way/<id>:<n>`), `kind` (`junction`, `joint`, `dead_end`, `area_edge`, `span_end`), `position` `[x, y, z]`, `osm_node` (or `null`).
  - Edge: `id` (`osm:way/<id>#<n>`), `from`, `to`, `class`, `drivable`, `walkable`, `lanes`, `speed_kmh` (`null` when not drivable), `width_m`, each of these five with a `*_source` (`osm` or `default`); `oneway` (`from` → `to` is the allowed driving direction), `bridge`, `bridge_source` (`osm`, `inferred`, `inferred_underpass` or `null`), `tunnel`, `layer`, `length_m`, `osm_way`, the name fields `name`, `name_sv` and `ref` (string or `null`), and `points` (at least 2 `[x, y, z]`).

Axes: glTF is Y-up. Local east is +X, up is +Y, and north is −Z. Units are metres from the map origin.

glTF node names: `terrain-col` (visual mesh plus collision), `bld_<source>_<type>_<id>` (building visual, e.g. `bld_osm_way_123`) and `bld_<source>_<type>_<id>_c<k>-colonly` (collision only: the footprint part extruded, one per footprint part, k = 0, 1, ...; a one-part building has `bld_<...>_c0-colonly`). No two node names in a tile are equal once Godot strips the collision suffixes (`-col`, `-colonly`, `-convcolonly`), so the import never renames a node; the pack checks enforce both rules. Roads: `road-col` (drivable surfaces) and `path-col` (the others), one each per tile, and `bridge_osm_way_<id>-col` per bridge deck with its railings (`bridge_osm_way_<id>_p<n>-col` when a deck is split across tiles), all with collision. World edge (ADR 0015): `edge_cliff-col` (the cliff ledge, face, seams and skirt, visual plus collision) in every written tile the boundary passes through, and `edge_bottom` (the bottom cap, visual only) in every written tile, both flat-shaded with the glTF material `edge_cliff` and no UVs.

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

TODO: the pack formats of markers (parking spots, gameplay points of interest) and vegetation. TODO: shipping packs as runtime-loaded `.pck` files.

### MapLoader (Godot)

A node in the `World` scene. It reads the map id from settings or a menu, checks the manifest's schema version, and instances tiles. It loads all tiles at first and adds streaming near the camera later. It also places the world edge from `boundary.json`: the barriers and the void trigger under `MapLoader/Edge` (ADR 0017).

After the tiles it reads `road_graph.json` into `MapLoader.RoadGraph`, then `buildings.json` and `pois.json` into `MapLoader.Places` (a `PlaceIndex`), then `boundary.json` into `MapLoader.Boundary`. Each is null, with an error naming the pack path and `/regen-map`, when its file is missing or invalid; the tiles still load. Road, path and bridge collision bodies are in the group `map_roads`, terrain bodies in `map_terrain`, building collision bodies in `map_buildings`. Each building body is named after its glTF collision node, so `MapLoader.BuildingOf(body)` resolves it through `PlaceIndex.TryGetBuildingByNode`.

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

**Place information** (ADR 0013): `BuildingsReader` (`buildings.json` version 2 only) and `PoisReader` (`pois.json`) reject another schema version, missing or unknown keys, unknown values, duplicate ids, bad outline rings and non-finite numbers. `PlaceIndex.Create(buildings, pois)` checks the links in both directions and looks up buildings by id or glTF node name, the POIs on or in a building and the areas it is inside, the POI area at an x/z point (smallest first, holes excluded) and the nearest POI point within a radius. `StreetLabel.Format(edge)` gives the HUD text: name and `(ref)`, the ref alone, or the road class's text.

**`RoadGraph`** (`Maps/RoadGraph.cs`, read by `RoadGraphReader`, ADR 0012) holds the pack's nodes and edges. The reader rejects another schema version, unknown keys, unknown node kinds, duplicate ids, dangling edges, edges with fewer than 2 points, edges neither drivable nor walkable, drivable edges without a speed and non-finite numbers. Lookups: `DrivableEdgesFrom(node)` (one-way edges only in their allowed direction), `WalkableEdgesAt(node)`, `NearestNode(position, filter)`, `NearestEdge(position, maxDistance)` (nearest polyline in 3D, ties to the lower edge id).

**World edge and player** (ADR 0015, ADR 0016):

- `BoundaryReader` reads the whole of `boundary.json` version 1 into `Boundary`. It rejects another schema version, missing or unknown keys, unknown enum values (boundary `source`, node `role`, fence `type`, `width_source`; a water crossing's `source` is a free map.yaml source name), non-finite numbers, a `map_id` or `area` that isn't the manifest's, short or unclosed rings, prism footprints that aren't 4 points, `open_area_m2` ≤ 0 and a void `bottom_y` ≥ `top_y`.
- `SpawnPicker` draws points uniformly by area over the open-ground polygons (holes excluded) from an injected `Random`, so a seed gives the same sequence.
- `PlayerState.Position` is the player's last committed position (Godot local metres). `PositionChanged` fires only when it changes; death leaves it as it is.
- `OnFootMotion` holds the on-foot movement maths: camera-relative target velocity, acceleration and deceleration limits, air control, turning, and `JumpVelocity`.
- `Edge/EdgeGeometry` (ADR 0017): whether a point is in a void prism (a grid index over the prisms), the nearest ring point and its `s`, the outward direction at a point (out of the ring for points inside it too; ties to the lower `s`), the ledge height at an `s`, and the safety-net test (`y < cliff.bottom_y − margin`).
- `Edge/BarrierLayout` turns the roadblocks and fences into piece and collision boxes (`BarrierParameters` from the barrier nodes' exports): at most 0.9 m above the base, at most 1.0 m outward, collision covering the ring with no gap over 0.1 m, shared posts at fence joints, roadblock stands on a ground height the engine supplies.
- `Edge/VoidPull` is the acceleration that replaces gravity in a void fall (outward plus down).
- `Edge/DeathSequence` is the death and respawn state machine (`Alive`, `Falling`, `DeathScreen`, `Respawning`, `Failed`): `EnterVoid` records the losses and calls `PlayerState.Die()`, `Advance(dt)` runs the timed states, `Dismiss()` ends the death screen early once `MinScreenTime` has passed, `CompleteRespawn` and `Reset`. `DeathScreenText.Format` gives the death screen's three lines.
- `RunToggle` is the gamepad run toggle: a press flips it, and while it is on, the move input going from outside its deadzone to inside turns it off.

**Saves and settings** (ADR 0018, `docs/specs/menu-settings-save.md`):

- `Saves/`: `SaveGame` (format version 1: game version, UTC save time, play seconds, map id and the pack's `generated_at`, the player's position and facing (both `null` in a save written during a death), money, gear, wanted level, and an empty `quests` object), the strict `SaveReader` and `SaveWriter` (JSON, `snake_case`, step-by-step `SaveMigrations`, a newer version is an error), `SaveSlots` over an `ISaveStore` (an autosave and manual slots 1..5, `List()`, `MostRecent()`, writes through `<file>.tmp`, flushed to disk, and a rename, `Delete`), `SaveFactory` (the save-building rules), and `PlayTime` / `PlayTimeText`.
- `PlayerState.Snapshot(includePosition)`, `Restore(snapshot)` (validated first; change events only for changed values, never `Died`) and `Reset()` (a new game).
- `Settings/`: `GameSettings` (display, audio, gameplay; defaults), the forgiving `SettingsReader` (a bad or unknown value gives its default and a warning) and `SettingsWriter`, `SettingsFile` (file reading and writing), `WindowSizes`, `VolumeMath`, `QualityPreset`, `DisplayRevert` (the display confirm-or-revert countdown) and `ControlNames` (action labels and generic gamepad names).

TODO: routing (with the traffic AI spec) and mission state.

### Gameplay (Godot C#)

- Autoloads: `Events` (global signal bus) and `GameState` (player, wanted level, settings). Both stay small.
- `Main` (ADR 0018, `scenes/main/Main.tscn`) is the main scene: the main menu, the pause menu (the `pause` action: Esc and the gamepad Start button; `GetTree().Paused`), the slot list, the settings view (display, audio, controls and gameplay tabs; display changes to window mode, size or render scale are confirmed or reverted within 10 s), the loading screen and the confirmations, built in code under one placeholder theme (`scripts/ui/`). It instances `World` when a game starts or a save loads and frees it when the game ends. `--screenshot` and `--perf` start `World` directly with no menus or settings; `--new-game` starts a game at once. `scripts/settings/SettingsApplier` applies the settings to the engine (window through an `IDisplayBackend`, viewport anti-aliasing and render scale, shadow atlas, audio buses); `World` takes the quality's sun values and the camera values from `Main`. Player data (`settings.json`, `saves/`) lives in the user data folder (`user://`, a custom folder named `RoadmanSimulator`), never in map packs. Loading a save puts the player at the saved position when the spawn's placement tests pass there, else through the spawn draw with a notice (`World.PlayerLoaded`).
- `World` scene: MapLoader, WorldEnvironment, sun, HUD street label, and the player once it has spawned. In debug builds also the debug camera, the orthophoto view (F2), the road graph view (G) and the place inspector (I); release builds have only the player camera.
- Debug builds (ADR 0016): every debug-only decision asks `DebugBuild.IsDebug` (`scripts/debug/DebugBuild.cs`), the only code that calls `OS.IsDebugBuild()`. Tests set `DebugBuild.Override` to check the release layout.
- Player (ADR 0016, `docs/specs/player-on-foot.md`): a `CharacterBody3D` built in code (`scripts/player/Player.cs`) that walks, runs and jumps with `OnFootMotion`, on collision layer 2, and writes its position to `PlayerState` every physics frame. `World` spawns it at a random open-ground point of `boundary.json`'s spawn area (`SpawnPicker`, seeded from the time or `--seed`), checking each draw with a ray and a capsule query, and spawns it again after a map reload. No valid `boundary.json`, or no free draw: an error, and no player.
- Player camera (`scripts/player/PlayerCamera.cs`): a pivot that follows the player, a spring arm on the map's collision layer so the camera never clips into walls, mouse and right-stick orbit with clamped pitch, no automatic swing-back.
- Input map: `move_*` (WASD, left stick), `run` (Shift, held), `run_toggle` (left-stick click, off again when the stick returns inside its deadzone), `jump` (Space, south face button), `camera_*` (right stick), `debug_camera` (the physical key left of 1: `Quoteleft` and `Section`), and the `debug_*` actions. Gamepad events listen to any device. Only the current camera's owner reads input.
- Debug camera toggle (debug builds only): `debug_camera` switches between the player camera and the debug camera, which starts at the player camera's transform. While it is current the player gets no input and the player camera doesn't orbit; switching back restores the player camera and its mouse capture. Without a player it does nothing.
- HUD street label (ADR 0013): the street of the nearest road graph edge within 40 m of the player, or of the ground below the camera until there is a player, in every build. Never business names.
- Place inspector (debug builds only): a crosshair and a panel with the pack's data for the building or point of interest under the screen centre, including raw names and the override sources. Its text comes from `Roadman.Core`'s `PlaceText`.
- Vehicle (`VehicleBody3D`), chase camera, HUD with minimap, input map.
- World edge (ADR 0017, `docs/specs/world-edge-game.md`): `Roadblock`, `LandFence` and `WaterFence` items (`scripts/world/edge/`), each a `StaticBody3D` on `CollisionLayers.BarrierStatic` (loose pieces on `CollisionLayers.Barrier`, layer 3; both in the player's mask, not the camera's; the void trigger monitors only `Barrier`), with the visuals batched per piece mesh and type in `MultiMeshInstance3D`s; only vehicles break them (bodies implementing `IBarrierBreaker`; the vehicles spec adds real ones), and they are restored on respawn and reload. The void trigger `Area3D` (one convex shape per prism) and a safety net below the slab send the player into the death sequence (input off, collision mask cleared, `VoidPull` instead of gravity, the `DeathCamera` pull-back, the `DeathScreen` (dismissed by a key, gamepad button or mouse click after `MinScreenTime`, else closed after `ScreenTime`), a respawn from the spawn area); `IVoidFaller` bodies handle their own fall and other rigid bodies fall and are freed. A procedural sky shader draws a starfield below the horizon.
- Missions refer to marker ids in the pack, never to raw coordinates.

TODO: traffic AI, pedestrian AI, police, missions. Each of these gets a spec in `docs/specs/` before implementation.

## Testing

- Pipeline: pytest, using a fixed small test area. `tools/mapgen/tests/fixtures/raw/somero-test-area/` holds the Somero test area's raw data (OSM buildings, highway ways, water, land use and points of interest touching the area, without contributor metadata, the Topographic Database `rakennus` records intersecting it, DEM, lidar and orthophoto cuts, download manifest, attribution in `NOTICE.md`), laid out like `data/raw/<map-id>/`, so tests and CI can build the test-area pack offline. `tools/mapgen/scripts/cut_test_area.py` recreates it.
- Core: xUnit on `Roadman.Core`.
- Engine: gdUnit4Net, run headless.
- Visual: screenshots from the camera presets in the map pack, taken with a short windowed run (ADR 0008). `--player` (debug builds) renders the player in the third-person view at a preset's look-at point.
- CI (ADR 0010): `ci.yml` builds the Somero test-area pack from the committed fixture; it ignores `data-*` tags. `release.yml` fetches each map's pinned snapshot and builds the full packs from it (ADR 0011).
