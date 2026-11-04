# Spec: world edge (pipeline)

## Status

Approved

## Goal

Give every map pack a closed, physical edge generated from the map's data: roadblocks where roads leave the playable area, barbed-wire land fences and water fences along the rest of the boundary, and a cliff just outside the barrier, so the world reads as a slab floating in space. The pipeline writes all of it to `boundary.json` (with a committed JSON Schema) together with the void trigger volume, the map's spawn area and the traffic role of every road end at the boundary. Covers "World edge → Pipeline (Phase 3)" and "Spawn area" in `docs/project-outline.md`, and pipeline stage 10 in `docs/architecture.md`.

Out of scope (later specs): the game side, meaning the barrier scenes, placing them from `boundary.json`, the void `Area3D` and death sequence, the space backdrop, the respawn state machine in `Roadman.Core`, and AI routing that respects the edge. Also out of scope: water meshes (stage 5), the other `markers.json` contents (parking spots, gameplay points of interest), the art-direction palette, and any data download. Only local data in `tools/mapgen/data/raw/somero/` is used. Every source this spec reads (OSM, DEM, Topographic Database GPKG) is already in the pinned `data-somero-2026-10-30` snapshot.

## Data (2026-11-01)

Measured from local data only: the last full build (`game/maps/somero/`, `tools/mapgen/build/somero/full/`), `osm/somero.osm` through the pipeline's `run_load`, and `mml/topographic/maastotietokanta_kaikki.gpkg`. Throwaway scripts in the session scratchpad. Positions are local metres: x east and n north from the origin (the pack's z is −n). The build area is x −1153…1152, n −1171…1171 (2305 × 2342 m, 100 tiles). Its perimeter is 9294 m.

### Roads at the build area edge

- **51 `area_edge` nodes**, each with exactly one edge: west 14, east 9, north 10, south 18. Of these, 35 edges are drivable and 16 walk-only (14 cycleway, 2 footway).
- **Classes:** residential 18, cycleway 14, tertiary 5, track 4, living_street 3, primary 2 (road 52 on both the north and south edges), unclassified 2, footway 2, driveway 1. Every width is the class default (2.0–8.0 m). None is a bridge, tunnel or one-way.
- **Crossings close together:** 9 pairs of `area_edge` nodes lie within 15 m of each other along the edge, usually a cycleway beside its road. The closest pair, 4.1 m apart, is Härkäläntie (tertiary, 7 m) and a cycleway (3 m) on the west edge. Track `osm:way/706294574` crosses the north edge three times, and two of those crossings are 9.5 m apart.
- **Road surface on the edge vs the road's width:** the merged road and path surfaces cover 25 stretches of the edge. At four crossings the surface extends more than 3 m beyond node ± width/2, because the road crosses at an angle or has a junction just inside the edge: near Auratie (east) by 12.9 m, Välitie (east) by 7.4 m, Jänistie (south) by 3.4 m and track 706294574 (north) by 3.2 m.

### Water

Kirkkojärvi (MML `jarvi` 2026919564) covers 256,000 m² of the build area's southwest. It is joined to Jaatilanjoki, which flows in from the north edge, and to Somerjoki, which drains it east along the south and leaves through the east edge. The three areas form one connected water body.

| Crossing of the build area edge | OSM area on the edge | MML area on the edge | MML surface Z | DEM on the crossing |
| --- | --- | --- | --- | --- |
| West, n −883…−555: Kirkkojärvi | `osm:way/26725604`, 319.6 m | `jarvi` 2026919564, 327.9 m | 81.4 | 81.93–81.96 |
| East, n −1168…−1115: Somerjoki | `osm:way/142159369`, 56.4 m | `virtavesialue` 337360971, 52.6 m | 82.2 → 81.8 | 81.84–82.21 |
| North, x −700…−687: Jaatilanjoki | none (only the `waterway=river` line) | `virtavesialue` 337343877, 12.3 m | 82.07 → 82.00 | 82.01–82.06 |

- **Narrowest places inside the area:** Kirkkojärvi's north–south width near the west edge is 256–290 m for x −1103…−978 (narrowest at x −1103). The outlet into Somerjoki is 25–29 m wide at x −100…−90 and 15 m at the road 52 bridge (x ≈ −40). Jaatilanjoki's mouth into the lake is about 19 m wide.
- **Lines crossing the edge:** OSM has 3 (Somerjoki, Jaatilanjoki, and a `waterway=flowline` inside the lake). MML narrow streams (`virtavesikapea`) cross at 5 points: 2 on the west edge near or in the lake, 1 on the south edge and 2 on the north edge.
- OSM water relations left out by the load stage: 0.

### Buildings and terrain at the build area edge

- 4 OSM buildings overhang the edge (by 82.7, 38.7, 27.4 and 0.9 m²), and 27 MML-only buildings have a wall exactly on it, because the Topographic Database records stop at the bbox edge. 17 more lie within 5 m of the edge.
- Terrain along the edge runs from 81.8 to 133.4 m (steepest 2 m step 0.59). The DEM is read with a 10 m margin around the build area (`DEM_MARGIN_M`).

### Spawn area

- `maps/somero/markers.json` doesn't exist yet.
- The test area is x, n −150…150. **No OSM parking area or parking aisle lies inside it.** The 16 `amenity=parking` areas in `pois.json` are all outside it. The nearest are 57–94 m outside its edge.
- The only mapped open area inside it is an MML park (`puisto` 2018299326, 2381 m² inside). Its other open patches (between buildings, roads and water, each grown by 2 m, and at least 10 m from the test-area edge) are OSM `landuse=residential` or have no land use.

## Behaviour

### Config: `world_edge` block in `map.yaml`

An optional block, validated by `MapConfig` (unknown keys are errors). Every key has a default in the pipeline, so `map.yaml` lists only what it overrides (decided 2026-11-01). A map without the block gets all the defaults and the default boundary. Defaults decided 2026-11-02. The Somero figures come from boundary B and the full build's DEM (see "Somero: boundary" and "Somero: spawn area").

| Key | Default | Meaning | Reason for the default |
| --- | --- | --- | --- |
| `boundary_wgs84` | none | A custom boundary polygon: a list of `{lat, lon}` points in degrees, like `bbox_wgs84`, without repeating the first point. Absent: the boundary is the build area rectangle. | — |
| `building_clearance_m` | 2.0 | A building closer than this to the boundary counts as crossing it (see "Buildings and the boundary"). | More than the inward half-depth of a barrier, so no fence post or roadblock stands in a wall. Boundary B was routed at this value (closest building 2.12 m). |
| `fence.max_segment_m` | 20.0 | Longest fence segment. | On B (with the 0.25 m deviation below), about 770 segments, against about 1,210 at 10 m. At 50 m it's about 570, but long straight segments then rely on the deviation split alone. |
| `fence.max_terrain_deviation_m` | 0.25 | Largest vertical gap allowed between a land fence segment's straight line and the terrain under it. | The barrier must stay low enough to jump over on foot. Where the terrain dips under a segment, the fence's top is that much higher above the ground, so this caps how much the pipeline adds to the game's barrier height. On B, 0.25 m is about 770 segments, 0.1 m about 1,140, and 0.5 m about 660. |
| `roadblock.clearance_m` | 1.0 | Extra span beyond the road surface on each side of a roadblock. | Covers the road shoulder, so a car can't pass between a roadblock end and the fence. On B this gives about 39–41 roadblocks (33–35 drivable) and spans up to 34.6 m. |
| `roadblock.merge_distance_m` | 15.0 | Roadblocks whose spans are closer than this are merged. | Decided 2026-11-01. |
| `cliff.ledge_m` | 2.0 | Ground between the boundary and the cliff top, measured outward. | Leaves room for the barrier's outward half and the start of the void trigger (1.0 m) on solid ground, so the drop reads as "just outside the barrier" and a player who jumps the fence lands on the ledge inside the trigger. |
| `cliff.depth_m` | 30.0 | Height of the cliff face below the lowest boundary point. | Somero's boundary runs from 81.8 to 133.4 m (51.6 m of relief). At 30 m, `bottom_y` is 51.8, so the slab is 30 to 81.6 m thick: a visible slab from below in the death sequence. A visual value, checked by the screenshot in criterion 20. |
| `void.inner_offset_m` | 1.0 | Where the void trigger starts, outside the boundary. | Beyond the outward half of a barrier, so barrier pieces at rest never start inside the trigger. Barrier scenes must reach at most 1.0 m outward from the boundary (a constraint for the game spec). |
| `void.width_m` | 20.0 | How far out the trigger reaches. | Many times what a body moves in one physics tick (0.83 m at 50 m/s at 60 Hz), so nothing passes through the band between two ticks. On Somero the trigger bottom is at 31.8 m. |
| `void.top_margin_m` | 20.0 | How far above the highest boundary point the trigger reaches. | Covers a jump or a vehicle airborne at the edge. On Somero `top_y` is 153.4 m, 71.6 m above the lowest boundary point. |
| `spawn.obstacle_buffer_m` | 2.0 | How far buildings, water and road and path surfaces are grown before they are cut from the spawn area. | Keeps a respawned player off roads and out of walls, with room for the drop onto the ground. |
| `spawn.min_open_area_m2` | 500.0 | Smallest allowed open ground. | Enough room for random respawn points to differ (about 22 × 22 m). |
| `spawn.min_clear_radius_m` | 5.0 | A disc of this radius must fit in the open ground. | At least one spot about a car length across, so the area isn't only thin slivers between obstacles. |
| `spawn.min_boundary_distance_m` | 10.0 | Open ground within this distance of the boundary is cut. | Well clear of the barrier, the ledge (2 m) and the void trigger (from 1 m outside), so a respawn never lands at the drop. |

`fence.max_segment_m`, `fence.max_terrain_deviation_m` and `roadblock.clearance_m` were measured on B's ring with the fence stretches approximated as the ring minus the road surfaces grown by 1 m. The real build gives slightly different counts.

The barrier height itself is set by the game's barrier scenes (later spec). The pipeline's only effect on it is the fence's deviation from the terrain (at most `fence.max_terrain_deviation_m`) and the step where a fence meets a roadblock at road height (`surface_offset_m`, 0.05 m on Somero).

Example, Somero (`maps/somero/map.yaml`). Only the boundary differs from the defaults:

```yaml
# Playable boundary and world edge (docs/specs/world-edge-pipeline.md). Every other
# key keeps its pipeline default; list a key here only to override it.
world_edge:
  # Candidate B (west notch), 184 points (the original count; 162 since the 2026-11-04 amendment). Start and winding don't matter (the pipeline
  # orders the ring). From tools/mapgen/build/somero/world-edge/boundary-candidates.geojson, feature B-west-notch.
  boundary_wgs84:
    - {lat: ..., lon: ...}
    # ... 184 points in all (originally; 162 since the 2026-11-04 amendment)
```

**First implementation step:** copy the 184 points of `B-west-notch` from `boundary-candidates.geojson` into `maps/somero/map.yaml` as `world_edge.boundary_wgs84`, so the committed `map.yaml` is the only record of the boundary. `tools/mapgen/build/` is git-ignored and can be overwritten; the candidates file stays out of Git. Because `MapConfig` forbids unknown keys, this goes in together with the `world_edge` config model, not before it.

A map that overrides a default lists only that key, for example:

```yaml
world_edge:
  fence:
    max_terrain_deviation_m: 0.1
  cliff:
    depth_m: 50.0
```

### The boundary

- **Default:** the build area rectangle (for a full build, the bbox rounded outward to whole metres).
- **Custom polygon** (`boundary_wgs84`): projected to the map CRS and moved to local coordinates like every other input. It must be a single valid, simple polygon with at least 3 points and no holes, lying entirely inside the build area. Its winding doesn't matter. A polygon that fails any of these rules fails config validation with the reason.
- **Inside it:** with a custom polygon, everything the build area clips today is clipped by the boundary instead. Buildings (OSM and MML-only) and POIs are kept by centroid. Road ways are cut by line, with `area_edge` nodes on the polygon. The terrain, road and path meshes are cut along the polygon, the way roads are cut along terrain triangles now. The tile grid and the manifest's tile bounds still come from the build area. A tile that lies entirely outside the polygon (ledge and cliff included) isn't written.
- **Test builds** (`--area test`): the boundary is always the test-area square, whatever `boundary_wgs84` says (decided 2026-11-01).
- **Ring order:** the boundary ring is stored counter-clockwise seen from above, starting at its vertex with the lowest x, then the lowest n. Distances along the ring (`s`, in metres) start there. Every item on the ring (roadblocks and fence segments) has `s_start` and `s_end`.

### Buildings and the boundary

No building may cross the boundary (decided 2026-11-01). A building crosses it when part of its footprint lies outside the boundary or within `building_clearance_m` of it.

- **Custom polygon:** the polygon must be routed around buildings. A crossing building fails the build (a pack check) and is named with its id and distance.
- **Default rectangle:** crossing buildings are dropped before tiling (they are left out of the tiles, `buildings.json` and the POI links, like buildings whose centroid is outside the area). The build report lists them under `world_edge.dropped_buildings` with id, source and the area outside the boundary.

### Water areas and crossings

- **Water areas** come from the MML Topographic Database (`jarvi` and `virtavesialue`) when the map has a source that provides them, and from OSM water areas otherwise (decided 2026-11-01). This needs a new adapter capability, `water_areas`, provided by `mml_topographic` (`load` reads the two tables from the same GPKG it already reads) and used through the existing rule that the build picks sources by capability. In OSM fallback mode, the OSM areas are clipped to the area, and an OSM water relation that the load stage left out (open ring or missing member) within `cliff.ledge_m + void.width_m` of the boundary fails the build, naming the relation.
- **Water crossings** are the parts of the boundary inside a water area. Each one becomes one or more water fence segments.
- **Water surface height:** for now, water fences sit at the terrain height (`Heightfield.sample`, what the terrain mesh shows), because the game draws no water surface yet (decided 2026-11-01). When water surfaces are added (stage 5), water fences move to the water surface height, and this spec's criteria for water fence heights change with them.
- **Water lines:** a waterway that is only a line (MML `virtavesikapea`, OSM `waterway` without an area) gets a land fence over it (decided 2026-11-01). It is still listed in the report.
- **Report:** `10-world-edge/report.json` lists every water crossing: `s_start`, `s_end` and length, its local, map CRS and WGS84 endpoints, the water area ids, and the source used. In MML mode it also lists the OSM areas on the same stretch, and every disagreement between the two (water in one and not the other, or endpoints more than 1 m apart). It also lists every water line crossing the boundary.
- The build prints one line per water crossing (`water crossing: <length> m at <WGS84 endpoints> (<ids>)`) and the total.

### Roadblocks

- Every `area_edge` node of the road graph belongs to exactly one roadblock.
- **Span:** the part of the boundary covered by the road or path surface (from stage 4's merged surfaces) that contains the node, plus `roadblock.clearance_m` on each side. It isn't node ± width/2, because angled crossings and junctions near the edge cover more of the line (see "Data").
- **Merging:** two roadblocks whose spans are less than `roadblock.merge_distance_m` (15 m) apart along the ring become one roadblock spanning both and the boundary between them (decided 2026-11-01). Merging repeats until no two roadblocks are that close. "Apart" means the fence gap between the spans, not the distance between the crossing nodes (decided 2026-11-02).
- **A road touching the boundary without crossing it** (its surface reaches the boundary, but no edge crosses it there) gets no roadblock. The fence continues over it (decided 2026-11-01), and the report lists the touched stretch.
- **Traffic roles** (decided 2026-11-01), stored in `boundary.json`. `road_graph.json` stays at schema version 1. Each node in a roadblock has a role:
  - `spawn_despawn` for a drivable edge: cars enter and leave town there.
  - `turn_around` for a walk-only edge.
- **Record:** `id` (`roadblock:<lowest node id>`), `nodes` (a list of `{node, edge, role}`, one per `area_edge` node, sorted by node id), `class` (of the widest edge), `drivable` and `walkable` (any of its edges), `width_m` (widest edge) with `width_source`, `start` and `end` (the span ends on the boundary, `[x, y, z]`, y = terrain + `surface_offset_m` when the map has roads, else terrain), `position` (span midpoint on the ring), `yaw_deg` (the angle of the boundary at the position, so a scene's local +X runs from `start` to `end`), `outward` (unit vector `[x, 0, z]` pointing out of the playable area), `s_start`, `s_end`, and `over_water` (true if any part of the span is in a water crossing).
- Barrier scene dimensions (height, depth, breakable pieces) are set by the game. The pipeline supplies the span, the class and the width to size them from.

### Fences

- Fences cover every part of the boundary that no roadblock covers, so there are gaps only at roadblocks.
- **Segmentation:** the uncovered stretches are split at boundary vertices, at water crossing ends and wherever needed so that each segment is at most `fence.max_segment_m` long. A land segment is split further until the terrain (`Heightfield.sample`) between its ends is within `fence.max_terrain_deviation_m` of its straight line.
- **Type:** `water` for a segment inside a water crossing, `land` otherwise. A segment is never partly over water: every crossing end is a segment end.
- **Heights:** every segment's end points sit on the terrain, for now, `water` segments included (see "Water surface height").
- **Record:** `id` (`fence:<n>`, in ring order), `type`, `start`, `end` (`[x, y, z]`), `s_start`, `s_end`, `outward`, and for water segments `water_ids`.

### Closed loop

The roadblocks and fence segments, sorted by `s_start`, tile the whole ring. Each item's `end` equals the next item's `start` in x and z (within 1 mm), the last one connects back to the first, and their lengths along the ring add up to the ring's length (within 1 mm). Heights may step where a fence meets a roadblock, because the roadblock is at road height.

### Cliff edge geometry

- **Ledge:** a strip from the boundary outward by `cliff.ledge_m`, at each boundary point's own height (a horizontal extrusion outward, so it needs no DEM beyond the area). At convex corners the strip is mitred, and at concave corners it is trimmed so it doesn't overlap itself or reach back over the playable area.
- **Face:** from the ledge's outer edge straight down to `bottom_y` = (lowest boundary height) − `cliff.depth_m`.
- **Bottom:** a flat cap at `bottom_y` under the whole slab, which the death sequence shows from below (decided 2026-11-01).
- **Low-poly:** planar quads, one per boundary piece between consecutive fence or roadblock break points (no finer than the fence segmentation). Flat-shaded: faces don't share vertices, and each vertex's normal is its face's normal. One material and no UVs. The palette colour comes with the art direction (`docs/project-outline.md`, "Still to decide"); until then it uses a plain glTF material that the game can replace.
- **Tiles and nodes:** the ledge and face are split at tile edges and belong to the tile they border, overhanging its `bounds_local` like buildings do. Node `edge_cliff-col` (visual plus collision) holds the ledge and face, and `edge_bottom` (visual only) holds the cap under each written tile. The existing check that node names stay distinct after stripping suffixes covers them.
- With the default boundary the terrain mesh is unchanged. With a custom polygon it is cut as described under "The boundary", so the ledge and the face also run along the polygon inside the build area, for example around an excluded lake.

### Void trigger volume

- A set of convex prisms around the outside of the boundary. Each prism has a footprint quadrilateral `[[x, z] × 4]`, and all share `bottom_y` and `top_y`. Together the footprints cover the band from `void.inner_offset_m` to `void.inner_offset_m + void.width_m` outside the boundary with no gaps, corners included (a fill prism at each convex corner). Where the band would reach back over the playable area (narrow inlets of a custom polygon), it is clipped to stay outside the boundary.
- `top_y` = (highest boundary height) + `void.top_margin_m`. `bottom_y` = the cliff's `bottom_y` − `void.width_m`, so anything falling past the cliff stays inside the trigger.
- The pipeline writes only the volume. The death sequence is game-side.

### Spawn area

- `maps/<map-id>/markers.json`: `schema_version` 1 and `spawn_area_wgs84`, one polygon as a list of `{lat, lon}` objects (same format as `boundary_wgs84`). Unknown keys are errors. Parking spots and gameplay points of interest come with their own specs and a schema version change. A pydantic model with a committed JSON Schema `markers-v1.schema.json`, validated before the load stage, like `camera_presets.json`. A missing file fails the build, because respawn needs a spawn area in every pack.
- **One spawn area for both builds:** the spawn area must lie inside the test area, and so inside both the test build's and the full build's boundary (decided 2026-11-01). A spawn area outside the test area fails both builds.
- **Check:** open ground = the spawn polygon minus the union of building footprints, water areas and road and path surfaces (including bridge decks), each grown by `spawn.obstacle_buffer_m`, and minus everything within `spawn.min_boundary_distance_m` of the boundary. The check passes when the open ground has at least `spawn.min_open_area_m2` and a disc of radius `spawn.min_clear_radius_m` fits inside it. Otherwise the build fails, giving the measured values and the largest obstacle overlaps.
- `boundary.json` carries the spawn polygon (`[x, z]` ring), the open ground (polygons of `[x, z]` rings, exterior first) and its area, so the game can draw random points from the open ground directly.

### `boundary.json`

Always written, in Godot local metres, sorted and rounded deterministically (two builds from the same input are byte-identical). It is defined by a pydantic model with a committed JSON Schema `tools/mapgen/src/mapgen/build/schemas/boundary-v1.schema.json`, validated on every write, with a test that the committed and generated schemas match. Unknown keys are errors. It isn't listed in the manifest (like `road_graph.json`), so the manifest stays at version 1.

Fields: `schema_version` (1), `map_id`, `area`, `boundary` (`ring` of `[x, z]` in ring order, `length_m`, `source`: `area`, `custom` or `test_area`), `roadblocks`, `fences`, `cliff` (`ledge_m`, `bottom_y`), `void_trigger` (`bottom_y`, `top_y`, `prisms`), `spawn_area` (`polygon`, `open_ground`, `open_area_m2`), `water_crossings` (`s_start`, `s_end`, `source`, `water_ids`).

### Report and intermediate output

`tools/mapgen/build/<map-id>/<area>/10-world-edge/`:

- `boundary.geojson`: the ring, roadblock spans, fence segments by type, water crossings, void footprints, and the spawn polygon and open ground. All in map CRS world coordinates with the legacy `crs` member, for QGIS.
- `report.json`: counts by type, roadblocks per class, merged roadblocks, roles per node, road surfaces touching the boundary without a crossing, water crossings and water lines as above, OSM/MML water disagreements, dropped buildings (default rectangle), and the spawn check values.

`timings.json` gains the stage.

### Pack checks

`pack_checks.py` gains checks that run on every build, including `--release`, and fail the build:

- `boundary.json` validates against the schema, and its `map_id` and `area` match the manifest's.
- Every `area_edge` node is in exactly one roadblock, every roadblock's nodes are `area_edge` nodes, and every role matches its edge (drivable `spawn_despawn`, walk-only `turn_around`).
- No two roadblocks are closer than `roadblock.merge_distance_m` along the ring.
- The closed loop rules above.
- No fence segment crosses a road graph edge (2D, tolerance 1 mm). A road surface that only touches the boundary is allowed.
- Every fence segment that overlaps a water area by more than 1 mm along the boundary is `water`, and every `water` segment lies entirely in water.
- Fence end heights equal `Heightfield.sample` within 1 mm.
- No building in `buildings.json` crosses the boundary (with `building_clearance_m`).
- The void prisms cover the band without gaps (sampled; see Acceptance criteria), and none intersects the playable area.
- The spawn area lies inside the test area, and the spawn check passes.
- Every tile the boundary passes through has an `edge_cliff` node, and the cliff mesh has no gaps along the boundary larger than 1 mm.

### Edge cases

- A road crossing the boundary at a very shallow angle covers a long stretch of the line. The roadblock spans the whole stretch.
- A road crossing the boundary inside a water crossing (a bridge at the edge): its roadblock gets `over_water: true` and sits at deck height. Somero has none.
- A boundary vertex inside a roadblock span: the span follows the ring around the corner, and `start`, `end` and `position` stay on the ring.
- A merged roadblock may hold both drivable and walk-only nodes. Each node keeps its own role.
- A map without a `roads` block: no roadblocks, and fences cover the whole ring.
- A map with neither a `water_areas` source nor OSM water areas: no water crossings, and every fence is `land`.

### Somero: boundary candidates

Somero gets a custom polygon (decided 2026-11-01). Four candidates were built from local data for review in QGIS. Each starts from the build area and avoids buildings by notching the polygon around any building closer than 2 m to it (2 m, the same as the `building_clearance_m` default). Lake cuts follow the MML lake outline grown by 2 m and simplified to 2 m.

File: `tools/mapgen/build/somero/world-edge/boundary-candidates.geojson` (WGS84, git-ignored, one feature per candidate with these values as properties).

| | A: rectangle | B: west notch | C: southwest cut | D: lake excluded |
| --- | --- | --- | --- | --- |
| Shape | Build area with small notches around edge buildings | A, plus the west edge stepped in to x −1103 across the lake, the narrowest north–south crossing near the edge | A, minus the lake and all land south of it, west of the outlet (x −100) | A, minus the lake only: the boundary follows its shore from the west edge to the outlet and back |
| Water on the boundary | 392.8 m (lake 327.9, Somerjoki 52.6, Jaatilanjoki 12.3) | 320.7 m (lake 255.8) | 119.4 m (outlet 25.7, Jaatilanjoki mouth 28.8, plus the two rivers) | 119.4 m (same crossings as C) |
| Playable area | 5.390 km² (99.8%) | 5.372 km² (99.5%) | 4.883 km² (90.4%) | 5.128 km² (95.0%) |
| Boundary length | 10,479 m | 10,560 m | 10,533 m | 12,912 m |
| Polygon points | 182 | 184 | 220 | 292 |
| Buildings dropped (OSM) | 41 (6) | 41 (6) | 93 (41) | 43 (8) |
| Road crossings / roadblocks after merging nodes < 15 m apart (drivable) | 51 / 42 (34) | 51 / 42 (34) | 45 / 37 (32) | 51 / 42 (34) |
| POIs outside | 0 | 0 | 4 | 0 |
| Lake | inside, crossed on the west edge | inside, crossed 50 m in | outside | outside, as an inlet of void reaching into the town from the west edge |

All four are valid and simple, and none has a building closer than 2 m to it. The dropped buildings are mostly the 27 MML-only records cut off at the bbox edge plus the 4 overhanging OSM buildings.

**Chosen: B, west notch** (decided 2026-11-02). It keeps the lake inside, with a 255.8 m water fence across it 50 m in from the west edge. Its 184 points from the GeoJSON feature `B-west-notch` go into `maps/somero/map.yaml` as `boundary_wgs84` (see the example under "Config"). With the defaults, B's ring is 10,560 m. The span rule (road surface + 1.0 m, merged under 15 m) gives about 39–41 roadblocks, 33–35 of them drivable; two crossings whose road surface doesn't reach the ring are counted here as one roadblock each. 4 road surfaces touch the ring without crossing it. The B row of the table above counted roadblocks by node distance instead, so its 42 / 34 doesn't apply to the decided rule.

### Somero: spawn area candidates

No candidate meets every stated condition (parking area, near the centre, inside the test area), because there is no parking area inside the test area. The candidates below each meet some of them. "Open ground" uses obstacles grown by 2 m and 10 m from the test-area edge, the same as the `spawn.*` defaults.

File: `tools/mapgen/build/somero/world-edge/spawn-candidates.geojson` (WGS84, git-ignored, with the test-area square).

| | Kind | Inside test area | Size | Open ground | Largest clear disc | Distance from origin |
| --- | --- | --- | --- | --- | --- | --- |
| S1 | Open patch at x 17…140, n 61…140 (no OSM land use; 24% MML park `puisto` 2018299326), not a parking area | yes | 6009 m² | 6009 m² | r 30.2 m | 127 m |
| S2 | Parking `osm:way/348709797`, x −266…−209, n −29…17 | no (59 m outside) | 1367 m² | 1367 m² (no roads or buildings in it) | r 14.6 m | 238 m |
| S3 | Parking `osm:way/257670732`, x −238…−207, n 82…135 | no (57 m outside) | 831 m² | 259 m² (278 m² of parking aisle in it) | r 2.6 m | 247 m |
| S4 | Parking `osm:way/425249803`, x −219…−170, n −265…−218 | no (78 m outside) | 1429 m² | 1397 m² | r 18.0 m | 309 m |

S2–S4 would have needed a larger test area (`test_area.size_m` at least about 2 × 266 m plus the boundary distance for S2), and so a recut CI fixture.

**Chosen: S1** (decided 2026-11-02). It lies inside the test area, so one spawn area serves both builds, and the test area and CI fixture stay unchanged.

**Trimmed** (decided 2026-11-02): S1's north and east sides are cut back to x, n ≤ 134.98, so the polygon is at least 15 m from the test area boundary (5 m more than `spawn.min_boundary_distance_m`). The cut is at 134.98 rather than 135 because rounding the points to 7 decimals moves them by up to about 1 cm, and at 135 the committed polygon came out 14.997 m from the boundary. `markers.json` holds one ring without holes, so building `osm:way/464653206` (310 m², 306 m² of it inside the polygon) lies within the spawn polygon; the check subtracts it like any other obstacle, and the game draws from `open_ground`. The trimmed polygon covers x 19.2…135.0, n 61.2…135.0, 5765 m².

Check with the defaults (obstacle buffer 2.0 m, 10.0 m from the boundary), using the 9 WGS84 points below after the round trip to local metres:

| Build | Boundary | Spawn area to boundary | Open ground (min 500 m²) | Largest clear disc (min r 5 m) | Result |
| --- | --- | --- | --- | --- | --- |
| Test | test-area square | 15.02 m | 5308 m² | r 30.0 m | passes |
| Full | B | 1017 m | 5308 m² | r 30.0 m | passes |

The boundary-distance cut removes nothing in either build, so the open ground is the spawn polygon minus the grown building and roads.

`maps/somero/markers.json`:

```json
{
  "schema_version": 1,
  "spawn_area_wgs84": [
    {"lat": 60.6311644, "lon": 23.5222501},
    {"lat": 60.6310860, "lon": 23.5223445},
    {"lat": 60.6310881, "lon": 23.5223565},
    {"lat": 60.6309377, "lon": 23.5223727},
    {"lat": 60.6305754, "lon": 23.5209802},
    {"lat": 60.6306577, "lon": 23.5208504},
    {"lat": 60.6312186, "lon": 23.5202245},
    {"lat": 60.6312736, "lon": 23.5223365},
    {"lat": 60.6311975, "lon": 23.5223447}
  ]
}
```

These are the outer ring of S1 from `spawn-candidates.geojson` (simplified to 1 m), trimmed as above. The implementation re-runs the check on the committed file; criterion 19 holds it to the result above.

### Decisions to record

ADR 0015 (world edge pipeline): `boundary.json` v1 and its schema, `markers.json` v1, the `world_edge` config block with pipeline defaults, polygons in WGS84, MML water first with OSM as the fallback, water fences at terrain height until water surfaces exist, roadblock merging at 15 m, traffic roles in `boundary.json`, test builds bounded by the test area, buildings never crossing the boundary, the cliff and bottom cap as tile nodes, the default values and their reasons, the 1.0 m outward limit on barrier scenes that the void trigger's inner offset sets, and Somero's boundary (B) and spawn area (S1).

## Acceptance criteria

1. `uv run mapgen build somero` and `--area test` write `boundary.json`, which validates against the committed `boundary-v1.schema.json`, and the committed schema equals the one generated from the model. The same holds for `markers-v1.schema.json`.
2. Two builds from the same raw data write byte-identical `boundary.json`.
3. `world_edge` keys are optional. A `map.yaml` without the block builds with the pipeline defaults, and an override changes only that value (pytest on `MapConfig`).
4. Every `area_edge` node in `road_graph.json` belongs to exactly one roadblock, and each roadblock's span covers the whole boundary stretch of its road or path surfaces plus `roadblock.clearance_m` on each side.
5. No two roadblocks are less than `roadblock.merge_distance_m` apart along the ring. A synthetic pair 4 m apart becomes one roadblock holding both nodes.
6. Every roadblock node has role `spawn_despawn` if its edge is drivable and `turn_around` if it is walk-only, and `road_graph.json` is unchanged at schema version 1.
7. Roadblocks and fence segments sorted by `s_start` form a closed loop: consecutive ends match within 1 mm in x/z, the last connects to the first, and their lengths add up to the ring length within 1 mm.
8. No fence segment crosses any road graph edge. A synthetic road surface that touches the boundary without crossing it gets no roadblock, the fence runs over it, and the report lists the stretch.
9. Every point of the boundary inside an MML water area (or an OSM area in fallback mode) lies on a `water` fence or a roadblock with `over_water: true`, and no `water` segment has a point outside water. A water line crossing without an area gets a `land` fence and a report entry.
10. With MML water available, an OSM-only map fixture uses OSM areas, and an OSM water relation left out near the boundary fails it with the relation named.
11. The build report lists every water crossing with its length, endpoints, ids and source, the OSM/MML disagreements, and every water line crossing.
12. Every fence end height (land and water) equals `Heightfield.sample` within 1 mm, the terrain between a land segment's ends deviates from its line by at most `fence.max_terrain_deviation_m`, and no segment is longer than `fence.max_segment_m`.
13. Each roadblock has `position` on the ring, `yaw_deg` along the ring at that point, and `outward` pointing out of the playable area (`position + outward` is outside the boundary).
14. Every tile the boundary passes through has `edge_cliff-col`, with flat-shaded planar faces reaching from the boundary to `bottom_y`, and every written tile has an `edge_bottom` cap at `bottom_y`. Their node names pass the existing name checks. A headless Godot import and run of the pack has no errors.
15. 10,000 sample points in the void band (between `void.inner_offset_m` and `void.inner_offset_m + void.width_m` outside the boundary, at heights between `bottom_y` and `top_y`) all lie inside some prism, and no prism contains a point inside the boundary.
16. A custom polygon that crosses a building fails the build, naming the building. With the default rectangle, crossing buildings are absent from the pack and listed in the report. No building in either Somero pack crosses its boundary.
17. With a custom polygon (pytest fixture), the road graph's `area_edge` nodes lie on the polygon, no kept building centroid, POI or terrain triangle lies outside it beyond the ledge, and criteria 4–15 hold. A test build of a map with a custom polygon uses the test-area square.
18. Invalid `boundary_wgs84` or `spawn_area_wgs84` input (self-intersecting, under 3 points, outside the build area or the test area) fails validation before the pack is touched.
19. The spawn check fails the build for fixture spawn polygons that are covered by a building, water or roads, that lie partly outside the test area, whose open ground is under `spawn.min_open_area_m2`, or that can't fit the clear-radius disc. It passes for Somero's committed spawn area in both the full and test builds.
20. Screenshot: a new camera preset `world-edge` in `maps/somero/camera_presets.json`, looking along the boundary at a roadblock crossing of road 52, shows the road ending at the ledge and the cliff face dropping to the bottom cap. Checked by eye.
21. The CI test-area build produces `boundary.json`, and all pack checks pass on it.

## Test ideas

| Criteria | Layer |
| --- | --- |
| 1, 2, 3, 18 | pytest: schema round-trips, committed vs generated schemas, determinism of two test-area builds, `MapConfig` defaults and overrides, polygon and markers validation errors |
| 4–8, 12, 13 | pytest: pack checks on the test-area build, plus unit tests on synthetic boundaries (a road crossing at 90° and at 20°, two crossings 4 m and 20 m apart, a drivable road beside a cycleway, a crossing at a corner, a road touching the line without crossing) |
| 9–11 | pytest: synthetic water (MML area, OSM-only map, OSM relation left out near the edge, MML and OSM disagreeing, a stream line) and the report's crossing list; full Somero build report checked against "Data" |
| 14 | pytest: glb read-back (planar faces, unshared vertices, face normals, cliff reaching `bottom_y`, cap present); headless Godot import and run via `/verify` |
| 15 | pytest: sampled point-in-prism test, including corners and a narrow inlet |
| 16, 17 | pytest: custom polygon fixtures on the test-area data, and a default-rectangle fixture with an overhanging building |
| 19 | pytest: spawn check fixtures, one per failure reason |
| 20 | screenshot via `/verify` |
| 21 | CI |

## Open questions

None. Resolved 2026-11-02:

1. Default values: see the table under "Config".
2. The 15 m merge distance is the fence gap between roadblock spans.
3. Somero's boundary is candidate B (west notch), with the lake inside and a water fence across it.
4. Somero's spawn area is S1, inside the test area. The test area stays at 300 m.

## Amendments

- 2026-11-02, decided by the user during slice (a):
  - `mml_topographic` `load` reads three tables from the GPKG, not two: `jarvi` and `virtavesialue` (water areas) and `virtavesikapea` (water lines). A missing table is an error that names the table; an empty table is fine.
  - The test-area fixture GPKG carries the three tables, empty, because no MML water meets the test area and the build needs the tables.
  - Stage 4: road and path runs that end at an `area_edge` cut now reach the boundary across their whole width, so the roadblock spans built from the merged surfaces cover the road where it crosses the boundary. See the 2026-11-02 amendment in `docs/specs/roads-and-road-graph.md` ("Amendments") and ADR 0012.
  - The void prisms' footprints are checked to be valid and convex quadrilaterals (pack check, `footprint_problems`), since the prisms are convex.
- 2026-11-02, implementation choices in slice (b) (custom boundary):
  - `boundary_wgs84` points up to 0.05 m outside the rounded build area (`BOUNDARY_SNAP_M`; 7-decimal WGS84 rounding moves points by a few mm) are clamped onto its edge; points further out fail validation. Local boundary points are rounded to 1 µm, so `boundary.json` holds the exact polygon the build cut with. On B, 43 points were clamped, by at most 5.6 mm.
  - A tile is written when any of its terrain lies inside the boundary; a tile with no terrain inside isn't written or listed in the manifest. On Somero every tile is written.
  - A building whose centroid lies outside a custom polygon but whose footprint reaches inside it (intersection area > 0) also fails the build, like a kept building crossing it; the message gives its real distance to the polygon. A building whose centroid lies outside isn't in the pack at all (no mesh, no collision, not in `buildings.json` or POI links), so the `building_clearance_m` rule, which keeps barrier pieces out of walls, applies only to kept buildings, that is to clearance measured from the inside. A pack check makes sure every `bld_` node in the tiles belongs to a building in `buildings.json`.
  - With the stage 4 change above, B has 2 road surfaces touching the ring without crossing it (3.4 m and 0.7 m), not 4: the two crossings whose surface didn't reach the ring now cover their own nodes and are roadblocks. B gives 41 roadblocks, 34 drivable.
- 2026-11-02, decided by the user in slice (b): the outside-building rule above replaces a review fix that also failed a building with its centroid outside when it lay within `building_clearance_m` of the polygon. B was routed at 2 m from the buildings, but 7-decimal WGS84 rounding brings 42 outside buildings a few mm under 2 m (1.9946 to 1.9998 m), and 2 MML records are at 1.81 and 1.72 m, so that fix failed the full Somero build on 44 buildings (38 MML-only records, 6 OSM buildings). A full build without it showed none of the 48 buildings and records with centroid outside B in `buildings.json`, the POI links or any tile node.
- 2026-11-02, implementation choices in slice (c) (cliff geometry; `build/world_edge/cliff.py`); the skirt and the screenshot rule below were decided by the user:
  - **Pieces and heights.** The ring is cut at every roadblock and fence end of `boundary.json`, at its own vertices and where it crosses a tile grid line. Each piece's ledge is a planar quad whose inner edge runs straight from its item's height at one end to the height at the other (linear in `s`), so the ledge's inner edge is at the fence and roadblock heights.
  - **Corners.** A convex corner gets a horizontal kite to the mitre point, at the height of the piece ending there. A mitre that would reach more than `MITRE_LIMIT` = 2 ledge widths from the corner (turns over 120°; boundary B has 18 such corners, up to 176°) is cut square to the bisector at that distance (a clipped mitre), so it still covers every point within `ledge_m` of the corner. At a concave corner, the line from the corner to the inner mitre point splits the two overlapping quads. All shapes, these bisectors and the ring are noded into one planar arrangement (snap-rounded to 1e-9 m); each cell outside the ring goes to the shape containing it whose ring piece or corner is nearest to the cell's representative point. A cell that another covering shape of a different height or tile is nearer to at one of its vertices (in an inlet narrower than `ledge_m`, one cell spans both walls) is split where the two are equally near, and the arrangement is noded again (up to 4 passes), so each fence end gets ledge vertices at its own height. So the ledge never overlaps itself or reaches over the playable area, also beside B's short segments (0.05 to 0.9 m) and in narrow inlets.
  - **Walls.** Every arrangement edge of a ledge cell is a vertical wall facing its lower side: to the void, the face down to `bottom_y`; to a ledge cell of a different height, a seam (where a fence meets a roadblock 0.05 m higher, or at a corner); on the ring, a **skirt** from the ledge's inner edge to the terrain mesh's own edge, split at every terrain edge vertex on the ring. The skirt is an addition to the spec's ledge and face: a fence's straight line is up to `fence.max_terrain_deviation_m` (0.25 m) from the terrain, and roadblocks are at road height, so without it the terrain edge would meet the ledge only at the break points. With it, every terrain vertex and edge midpoint on the ring lies on a cliff mesh edge within 1 mm. Kept by the user (2026-11-02) over a ledge without a skirt (slits up to 0.25 m between terrain and ledge) and a ledge following the terrain vertices (finer than the fence segmentation); this departs from "one quad per boundary piece, no finer than the fence segmentation" for the skirt only.
  - **Bottom cap.** The playable area plus the ledge at `bottom_y`, facing down, cut into the tile grid cells; the outer cells reach past the build area's edge, under the ledge.
  - **Tiles.** "A tile the boundary passes through" is a written tile whose bounds meet the ring in a straight stretch longer than 1 mm on the stretch's playable side: the stretch's midpoint, moved into the playable area, lies in the tile's bounds. So a stretch on a grid line between two tiles (or on the build area's edge) counts only for the tile on its playable side. The build applies it per stretch: the ring is cut at its vertices and grid crossings, and each stretch belongs to the grid cell containing its midpoint moved 0.1 mm into the playable area (`stretch_tile`); the pack check finds the tiles from the manifest's bounds alone (midpoint moved 1 mm). Each piece belongs to its stretch's tile, a convex corner to the piece ending at it, and every wall to its ledge cell. A piece (or cap part) whose tile isn't written goes to the nearest written tile that the boundary passes through (2D distance to its bounds; ties in manifest order). On Somero no piece or cap part is moved.
  - **Terrain edge.** The skirt follows the terrain mesh edges on the ring; a ring stretch longer than 1 mm without one fails the build, because the terrain always reaches the boundary.
  - **Pack check** (`check_cliff`): exactly the tiles the boundary passes through (found from geometry) have `edge_cliff-col`, and every written tile `edge_bottom`; every ledge stretch on the ring lies on the playable side within its own tile's bounds (or of no written tile); faces share no vertices, every vertex normal is its face's unit normal, the cap faces down and faces sharing an edge and a normal are planar; the ledge's inner edges cover the ring with no gap over 1 mm; every roadblock and fence end has a ledge vertex at its height (1 mm); no ledge point lies more than 1 mm inside the playable area (the float32 positions are off by up to 0.064 mm on B); the lowest cliff vertex and every cap vertex are at `bottom_y` (1 mm); the caps cover the playable area plus the ledge, per tile and as a whole, without overlaps; and the face reaches `bottom_y` all along the cap's outline.
  - **Camera preset `world-edge`** (criterion 20) looks along the north edge of B at the road 52 roadblock on Jokioistentie (`roadblock:cut:way/1445392542:0`); see ADR 0015 for its derivation. Its heights are absolute (`ground_relative: false`), because the camera is outside the build area, where there is no terrain.
  - **Screenshot N/A** (decided by the user): the screenshot tool returns N/A (exit 2, `N/A: outside built area`) for any preset whose look-at x/z has nothing below it: a downward ray there hits neither the terrain (`map_terrain`) nor the world-edge cliff (`map_edge`), so the absolute-height `world-edge` preset is N/A on a test-area pack, as ADR 0008 says for presets outside the area.
