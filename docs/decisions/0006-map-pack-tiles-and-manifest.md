# 0006: Map pack tiles, axes and manifest v1

Status: Accepted

## Context

The pipeline's first stages (load, terrain, buildings) need a pack layout that the Godot map loader can rely on: how the map is split into tiles, which axes the meshes use, how buildings relate to tiles, where per-building provenance goes, and how the manifest is versioned and checked (ADR 0002). Test builds and full builds both have to produce a loadable pack.

## Decision

- **Tile grid.** Tiles are aligned to the map origin. Tile (i, j) covers local [i·T, (i+1)·T) × [j·T, (j+1)·T), where T is `tile_size_m`, clipped to the build area. Its id and file name are `tile_x{i}_y{j}` (`tiles/tile_x{i}_y{j}.glb`); i and j can be negative. The build area's local bounds are rounded outward to whole metres, so tile edges are exact in the meshes' 32-bit floats.
- **Axes.** glTF is Y-up: local east is +X, up is +Y and north is −Z, in metres from the origin. The manifest records this as `axes`.
- **Buildings per tile.** Each building belongs to the tile that contains its footprint centroid, and is never split across tiles. A building may overhang its tile's edge.
- **Node names.** `terrain-col` for terrain, `bld_<source>_<type>_<id>` for a building's visual mesh and `bld_<source>_<type>_<id>-colonly` for its collision box. The exporter reads the names back from the written file and fails if any changed.
- **Collision boxes.** Each footprint part gets its own collision box, its minimum rotated rectangle, so separate parts of one building don't get a box spanning the gap between them. A multi-part building's boxes are named `bld_<...>_c0-colonly`, `bld_<...>_c1-colonly` and so on, and `buildings.json` lists them in `collision_nodes`. Courtyards (holes) and concave corners are filled: the box covers them, so the player can't walk into a courtyard. Finer collision is left for later.
- **Terrain normals.** Terrain vertex normals come from the shared heightfield's gradient, not from each tile's faces, so normals match exactly across tile edges and no lighting seams appear.
- **Provenance.** `buildings.json` records, per building, the id, tile, node names, height and its source (`mml_3d`, `osm_height`, `osm_levels`, `default`), base height and its source (`dem`), and roof shape and its source. The game and later enhancement layers can see where every value came from.
- **Manifest v1.** `manifest.json` holds `schema_version` 1, map id and name, area, CRS, origin, height datum, tile size, axes, the tile list (id, file, local bounds, building count), credits, build time and tool version. It is defined by a pydantic model, and the JSON Schema generated from it is committed (`tools/mapgen/src/mapgen/build/schemas/manifest-v1.schema.json`). Every written manifest is validated against the committed file, and a test checks that the committed schema equals the generated one.
- **Credits.** Only the raw files the build actually read are credited, using their entries in the download manifest. Files with the same source, dataset, licence and attribution (for example several DEM sheets) fold into one credit with the latest `retrieved_at`. A read file without an entry fails the build, so no data reaches a pack without its licence.
- **One pack folder.** `--area full` (default) and `--area test` both write `game/maps/<map-id>/`, and the manifest's `area` says which one it holds.
- **Atomic replacement.** The credits and manifest are built and validated before the pack folder is touched. The whole pack is then written into a temporary sibling folder, its manifest file is validated, and it is swapped in (old folder moved aside, new one moved in, old one deleted). A failed build leaves the previous pack untouched, and no stale tiles remain after a successful one.
- **Target safety.** The build refuses to replace a non-empty folder that has no `manifest.json`, so a mistyped `--out` can't delete unrelated files.

## Consequences

- The map loader can place tiles from the manifest alone. It must check `schema_version` and can read `axes` instead of assuming them.
- A new pack field means a schema change: regenerate the schema, and bump the version if old loaders can't read the change.
- Switching between test and full builds replaces the pack, so the game always sees one consistent area.
- Buildings that overhang a tile edge are loaded with their tile. Streaming by tile later must allow for this overhang.
- Building heights are mostly defaults until MML 3D buildings or overrides are added: in Somero's current OSM data, 1 of 1841 buildings has a height or level tag.

## Amendment (2026-10-27): lidar heights

Spec: `docs/specs/building-heights.md`.

- `height_source` in `buildings.json` can also be `mml_lidar`, which comes first: `mml_lidar`, `mml_3d`, `osm_height`, `osm_levels`, `default`.
- Lidar buildings also record `lidar_points`, `lidar_buffer_m`, `lidar_returns`, `roof_z_median` and `roof_z_p90`. A building whose lidar value was rejected records `lidar_rejected` with the reason. These are additions to `buildings.json`, not to the manifest, so the manifest schema stays at version 1.
- The last consequence above no longer holds for Somero: about 98% of its buildings get a measured lidar height.
