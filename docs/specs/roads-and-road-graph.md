# Spec: roads and road graph

## Status

Approved

## Goal

Give the map visible road and path surfaces that cars drive on, and a road graph, both generated from OSM by the pipeline for any map (ADR 0002). The meshes make Somero's street layout readable and give cars a surface that matches what the player sees, including bridges. The road graph (nodes and edges with class, lanes, speed limit, one-way, drivable and walkable flags, and names) is the data that traffic AI, pedestrian AI, police, routing and the minimap build on later. Covers pipeline stages 4 and 7 (`docs/architecture.md`) and Phase 4 step 7 (road graph loader and debug drawing) in `docs/project-outline.md`.

Out of scope: routing (comes with the traffic AI spec), road markings, traffic lights, lane-level geometry, the minimap, roadblocks and the world edge.

## Data (2026-10-29)

Counted from the local raw file `tools/mapgen/data/raw/somero/osm/somero.osm` (no download):

- 498 ways with a `highway` tag: `service` 156, `residential` 136, `cycleway` 126, `primary` 25, `footway` 17, `tertiary` 16, `unclassified` 8, `living_street` 6, `path` 4, `track` 3, `pedestrian` 1.
- Tag coverage on those 498 ways:

  | Tag | Ways | Values |
  | --- | --- | --- |
  | `maxspeed` | 178 | 40 (130), 50 (32), 20 (10), 30 (6) |
  | `lanes` | 33 | 2 (18), 1 (13), 3 (2) |
  | `oneway` | 22 | `yes` (21), `no` (1) |
  | `junction` | 1 | `roundabout` |
  | `bridge` | 12 | `yes`; 11 with `layer=1`, 1 without `layer` |
  | `tunnel` | 2 | `yes`, both `cycleway`, `layer=-1` |
  | `width` | 2 | 3 |
  | `service` | 65 | `driveway` (53), `parking_aisle` (12) |
  | `motor_vehicle` | 2 | `destination` |

- Every node that a highway way references is in the file (0 missing references).
- **Test area** (300 m square around the origin): 31 highway ways (`primary` 9, `residential` 8, `cycleway` 7, `service` 3, `tertiary` 2, `footway` 2). It has no tunnels and no mapped water.
- **The origin bridge** is the only bridge in the test area. It isn't a river bridge:
  - It is an overpass carrying Jokioistentie, a dual carriageway of two `primary` ways (88027326 and 813370683, each `bridge=yes`, `layer=1`, `oneway=yes`, about 30 m long), over the Heikintie cycleway (way 88027331).
  - The cycleway runs in a cutting and shares no node with the bridge ways.
  - The DEM is 91.5 m at both deck ends of each way and drops to 87.9 m under the middle. At the midpoint between the two carriageways, it is 88.5 m.
  - The nearest river line (Jaatilanjoki) is about 600 m away.
- **Water crossings:**
  - OSM water in the bbox: 3 `waterway=river` lines, 3 `waterway=flowline` lines and 6 closed water-area ways. There are no water multipolygon relations and no culverts.
  - 10 of the 12 tagged bridges cross a river line; the other 2 are the origin bridge.
  - No included highway way without a `bridge` or `tunnel` tag crosses a waterway line or water area. Somero's current data therefore has no suspected missing bridges.
- **Connectivity of drivable ways** (OSM segments between consecutive nodes, classes `primary` to `track` including `service`, ignoring direction): in the full bbox, the largest component has about 97.6% of 1,477 segments (6 components). In the test area, it has about 87% of 46 segments (2 components: 40 and 6), because the cut at the test-area edge separates a group of roads. These are estimates from a quick script; the build's own report is authoritative.
- **The committed test-area fixture has no roads.** `tools/mapgen/tests/fixtures/raw/somero-test-area/osm/somero.osm` holds only the 60 building ways (`cut_test_area.py` keeps buildings only).

## Behaviour

### Config: `roads` block in `map.yaml`

All road defaults are per map, in a new optional `roads` block in `maps/<map-id>/map.yaml`, validated by `MapConfig` (unknown keys are errors). Nothing about road widths, speeds or flags is hard-coded in the pipeline. A map without a `roads` block builds without roads: no road meshes, and a `road_graph.json` with empty `nodes` and `edges`, so every pack has the file. The build logs why.

Somero's starting values (Finnish practice; tweakable):

```yaml
roads:
  surface_offset_m: 0.05     # road surface height above the terrain mesh
  default_speed_kmh: 50      # built-up area; used by drivable classes without their own speed_kmh
  bridge:
    deck_thickness_m: 0.5
    railing_height_m: 1.0
    railing_width_m: 0.2
    min_clearance_m: 1.0     # deck underside above terrain; below this: warning in the report
    ramp_length_m: 5.0       # road-side ramp at each deck end, from terrain + offset to the deck-end height
    inferred_waterways: [river, stream, canal, ditch, drain, flowline]  # crossing these without a bridge tag: inferred bridge
  classes:
    primary:       {width_m: 8.0, lanes: 2, drivable: true,  walkable: false}
    secondary:     {width_m: 7.5, lanes: 2, drivable: true,  walkable: false}
    tertiary:      {width_m: 7.0, lanes: 2, drivable: true,  walkable: false}
    unclassified:  {width_m: 6.0, lanes: 2, drivable: true,  walkable: true}
    residential:   {width_m: 5.5, lanes: 2, drivable: true,  walkable: true, speed_kmh: 40}
    living_street: {width_m: 5.0, lanes: 1, drivable: true,  walkable: true, speed_kmh: 20}
    service:       {width_m: 4.0, lanes: 1, drivable: true,  walkable: true, speed_kmh: 20}
    driveway:      {width_m: 3.0, lanes: 1, drivable: true,  walkable: true, speed_kmh: 20}
    parking_aisle: {width_m: 5.0, lanes: 1, drivable: true,  walkable: true, speed_kmh: 20}
    track:         {width_m: 3.0, lanes: 1, drivable: true,  walkable: true}
    cycleway:      {width_m: 3.0, lanes: 1, drivable: false, walkable: true}  # combined cycle and foot path
    footway:       {width_m: 2.0, lanes: 1, drivable: false, walkable: true}
    path:          {width_m: 2.0, lanes: 1, drivable: false, walkable: true}
    pedestrian:    {width_m: 5.0, lanes: 1, drivable: false, walkable: true}
```

- **Class of a way:** the `highway` value, except `highway=service` with `service=driveway` is `driveway`, and with `service=parking_aisle` is `parking_aisle`. This mapping is OSM's, not map-specific, so it lives in the pipeline. A `highway` value with no entry in `classes` is skipped with the reason `class_not_configured`.
- `speed_kmh` in a class is optional: drivable classes without it use `default_speed_kmh`. Non-drivable classes have no speed.
- The `walkable` values for `primary`, `secondary` and `tertiary` are `false` because sidewalks along them are mapped as separate `cycleway` ways (combined cycle and foot paths), which are walkable.
- `surface_offset_m` must be > 0. Widths, lanes and speeds must be > 0. The bridge values must be > 0, except `min_clearance_m` ≥ 0.

### Input

- Roads come from the OSM source's `highway` ways, loaded through the adapter interface: the `osm_vector` capability gains road ways next to building footprints. There is no new source and no new download.
- For bridge inference, the same capability also loads water:
  - **Water areas:** closed ways and multipolygon relations with `natural=water` or `waterway=riverbank`. Rings are built and repaired as for buildings. A water relation that can't be closed (incomplete at the bbox edge, ADR 0005) is left out of the check and listed in the report.
  - **Waterway lines:** ways whose `waterway` value is in `bridge.inferred_waterways`.
  - Stage 5 (water meshes) is a separate feature; here water is only used to find crossings.
- Ways are skipped, and listed in the stage report with a reason, when: the class isn't configured (`class_not_configured`); `area=yes` (`area`); `highway=construction` or `proposed` (these are also unconfigured classes); or their line doesn't touch the build area (`outside_area`).
- Ways are clipped to the build area by their line, not by counting their nodes: a way whose line crosses the area is kept even when few or none of its nodes are inside (decided 2026-10-29, after the first full build dropped 8 such ways, about 422 m of road at the map edge). A cut gets a node of kind `area_edge`; an OSM node lying exactly on the edge, where the way continues outside, keeps its `osm:node/<id>` id and becomes `area_edge` too. Roadblocks, fences and the world edge are a separate feature; this spec only marks where roads leave the area.

### Road graph (stage 7)

- **One graph** holds every included way, roads and paths alike. Each edge is flagged `drivable`, `walkable`, or both.
- **Nodes** are created only at OSM nodes: at every end of an included way, and at every OSM node shared by two or more included ways. A way crossing over or under another without a shared OSM node (a bridge over a road, a tunnel) is never connected to it. Node kinds: `junction` (3 or more edge ends), `joint` (2 edge ends), `dead_end` (1 edge end) and `area_edge`.
- **Edges** follow one OSM way between two consecutive nodes and keep the way's polyline geometry with heights. One OSM way can become several edges.
- **Edge fields:** `id`, `from`, `to`, `class`, `drivable`, `walkable`, `lanes`, `speed_kmh`, `width_m`, `oneway`, `bridge`, `bridge_source` (`osm`, `inferred`, or `null` when not a bridge), `tunnel`, `layer`, `length_m` (3D polyline length), `osm_way`, `points`, and the name fields (see "Name fields").
- **Sources:** `drivable`, `walkable`, `lanes`, `speed_kmh` and `width_m` each have a source field (`drivable_source` and so on) with the value `osm` or `default`. `speed_kmh` and `speed_source` are `null` on edges that aren't drivable.
- **Tag parsing (OSM values always win over defaults):**
  - `maxspeed`: a plain number is km/h; `<n> mph` is converted to km/h and rounded. Anything else (`FI:urban`, `none`, `signals`, invalid) falls back to the default and is counted in the report.
  - `lanes`: a positive integer; anything else falls back to the default.
  - `width`: metres (`3`, `3 m`, `3.5`); invalid or ≤ 0 values fall back to the default. The width doesn't depend on `lanes`.
  - `oneway`: `yes`, `true` and `1` are one-way along the way; `-1` is one-way against it (the edge is stored reversed, so `from` → `to` is always the allowed direction); `no`, missing or anything else is two-way. `junction=roundabout` implies one-way unless `oneway=no`. One-way applies to driving; walking is always allowed both ways.
  - Access: `access=no` makes the edge neither drivable nor walkable, unless a more specific tag below says otherwise. `motor_vehicle` or `motorcar` set to `no` or `private` makes it not drivable; `yes`, `destination` or `designated` makes it drivable. `foot` set to `no` makes it not walkable; `yes` or `designated` makes it walkable. A flag changed by these tags has the source `osm`. An edge that ends up neither drivable nor walkable is skipped with the reason `no_access`.
  - `layer`: an integer, default 0. `bridge=yes` without `layer` counts as layer 1. `layer` on a way that is neither a bridge nor a tunnel is recorded but doesn't lift the way off the terrain.
- **Heights:** each graph point's height is the road surface height at that point (see "Road meshes"), so the graph and the mesh agree. Bridge and tunnel points are interpolated as described below.
- **Coordinates** in the pack are Godot local metres `[x, y, z]` (+X east, +Y up, −Z north), as in `camera_presets.json`.
- **Ids** are stable for the same input: node ids `osm:node/<id>`, or `cut:way/<id>:<n>` for `area_edge` cuts; edge ids `osm:way/<id>#<n>` (the edge's index along the way). Two builds from the same raw data write byte-identical `road_graph.json`.
- **Connectivity:** "connected" means the largest weakly connected component of the drivable edges (one-way direction ignored) contains at least 95% of the drivable edges. `07-road-graph/report.json` lists every component's edge count, the edges of every component except the largest, the share in the largest, and the strongly connected components when one-way edges are respected. The 95% rule is checked on the full-area build. Test-area builds report the share without a threshold: the cut at the test-area edge splits off roads (about 87% in Somero's test area, see "Data").

### Name fields

The road graph defines a shared name format for map features. The place-information spec reuses it.

- Every named feature has three keys, always present: `name`, `name_sv` and `ref`, from the OSM tags `name`, `name:sv` and `ref`.
- Each value is a string or `null`. A missing tag, or one that is empty after trimming, is `null`.
- Values are trimmed of surrounding whitespace and Unicode NFC-normalised. Otherwise they are kept exactly as tagged, including OSM's `;` separators for multiple values; they are never split, translated or shortened.
- The JSON is written as UTF-8 with non-ASCII characters unescaped (`Härkälä`, not `Härkälä`), so it stays byte-identical across builds.
- Street names and road numbers are real place names and are kept. The rule that replaces real business names doesn't apply to them.

### Road meshes (stage 4)

- **Surfaces.** Each included way that is neither a bridge nor a tunnel is buffered to `width_m / 2` on each side. The buffered shapes are merged per surface type, so overlaps at intersections don't z-fight:
  - `road`: the union of all drivable ways;
  - `path`: the union of the non-drivable ways, minus the `road` surface.
- **Draping.** The merged polygons are cut along the terrain mesh's triangle edges (the DEM grid lines and each cell's diagonal, taken from the same code that builds the terrain mesh). Each piece is triangulated inside its terrain triangle and gets that triangle's plane height plus `surface_offset_m`. The road surface is therefore parallel to the terrain mesh everywhere, exactly `surface_offset_m` above it, and the terrain can't poke through between DEM points. Proposed offset: 0.05 m. That is enough to avoid z-fighting with Godot 4's reversed-Z depth buffer at street level, and low enough that wheels don't visibly step at road edges. It is tunable in `map.yaml`.
- **Collision.** Road and path surfaces get collision, so the car drives on the visible surface: node names `road-col` and `path-col`, one each per tile.
- **Tiles.** Surfaces are split at tile edges and written into each tile's `.glb`, unlike buildings, which are never split. Vertices on a shared tile edge are identical in both tiles, so there are no gaps.
- **Bridges.** Every included way tagged `bridge` (any value other than `no`) gets a deck, and so does every inferred bridge (below):
  - **Spans.** A span is a chain of bridge ways joined at nodes that no non-bridge way uses. This covers a bridge mapped as several ways, but not the two carriageways of a dual carriageway, which are separate spans. A span's end height is the deck-end height `z_end` of its ramped end (see "Ramps"): the highest terrain mesh height plus `surface_offset_m` along the deck's end edge, so the whole end edge sits on or above the ground.
  - **Straight deck.** The deck's driving surface follows a straight line from one end height to the other, by distance along the span's centre line. The heights of nodes inside the span are on that line, so each bridge way is straight between its own ends too. The deck never follows the terrain under it.
  - The driving surface is never below that line and at most 0.05 m above it. The implementation aims for exactly on the line; the 0.05 m only allows for triangulation.
  - A span end that is an `area_edge` cut, or that has no connected ground road, takes the terrain mesh height plus `surface_offset_m` at that point.
- **Ramps** (decided 2026-10-29, after the first full build showed steps of up to 1.435 m between deck ends and the road beside them). Every deck end that a ground way meets gets a ramp on the road side, `bridge.ramp_length_m` long (Somero: 5.0 m) and as wide as the deck end:
  - In the ramp area, the surface height is `max(blend, terrain + offset)`, with `blend = a · z_end + (1 − a) · (terrain + offset)`, `a = 1 − d / ramp_length_m` (clamped to 0..1) and `d` the distance to the deck's end edge. Overlapping ramps are combined so each is exact at its own end edge.
  - The surface therefore equals the deck height along the whole end edge, is never below terrain + offset, and the terrain can't poke through it. The terrain itself isn't changed.
  - Ramp areas belong to `road` (or `path` for a non-drivable deck). A ground way that ends at a deck loses its part under the deck, and no road or path surface lies in a deck's footprint within `ramp_length_m` of a ramped end.
  - Graph points in a ramp area take the ramp height, so the graph still equals the surface. The ramps are listed in `04-roads/report.json` under `ramps`.
  - The report also lists `terrain_above_deck` per deck: how far the terrain rises above the deck's driving surface inside its footprint, the area and the distance from the nearer end. This is information only.
  - The deck is a low-poly slab of the way's width and `deck_thickness_m`, with a box railing (`railing_height_m` high, `railing_width_m` wide) along each long edge. There are no supports.
  - The node is `bridge_osm_way_<id>-col` (deck and railings, mesh plus collision). A bridge that crosses a tile edge is split, with a suffix per part (`_p0`, `_p1`).
  - The ground surfaces don't include bridge ways, so there is no road surface under a bridge unless another way runs there.
  - **Clearance check:** where the deck underside is less than `min_clearance_m` above the terrain mesh, `04-roads/report.json` records a warning with the bridge's way id, the smallest clearance and its position. This is never a build failure.
- **Inferred bridges.** An included way without a `bridge` or `tunnel` tag that crosses a water area or a waterway line is never draped into the valley. It is treated as a bridge:
  - **Span.** From the last node of the way before the water to the first node after it. If a way node lies inside a water area, the span extends to the nearest nodes outside on each side. If the way ends in the water at an area-edge cut, the span ends at the cut.
  - The span ends become graph nodes, so the bridge edges carry `bridge: true` and `bridge_source: "inferred"`. The span's deck, straight line, railings, clearance check and node names (`bridge_osm_way_<id>-col`) are the same as for tagged bridges.
  - **Not counted as crossings:** a waterway with a `tunnel` tag (a culvert) or a negative `layer`; a crossing at a way node tagged `ford=yes`; or a way tagged `ford=yes`. Each of these is counted in the report.
  - **Report.** `04-roads/report.json` lists every inferred bridge under `suspected_missing_bridges` with:
    - `osm_way` (`osm:way/<id>`), the class and the name fields;
    - the water feature's OSM id;
    - the crossing position as Godot local `[x, y, z]`, as map CRS world coordinates and as WGS84 latitude and longitude, so it can be checked in QGIS or on the OSM website.

    The build logs the count. Somero's current data has none (see "Data").
- **Terrain under bridges.** This spec doesn't flatten or otherwise change the terrain under roads: the terrain mesh is built from the DEM alone. If a later change flattens terrain under roads, it skips every bridge span (tagged and inferred) and never changes the terrain inside a deck's footprint.
- **Tunnels.** Ways with `tunnel=yes` get no mesh and no collision. They are in the graph only, with heights interpolated linearly between their end nodes' heights, like a bridge deck.
- **Material.** Each surface gets one plain glTF material, and the game replaces it. There are no UVs for now.
- **Low-poly terrain.** This draping and the offset depend on the terrain mesh being at DEM resolution. When the art direction moves the terrain to a coarser low-poly grid, or ADR 0004 picks Terrain3D, revisit: the draping must follow the new triangles, and the offset, the collision behaviour and criterion 9 have to be checked again.
- **Pack checks in every build** (decided 2026-10-29). The pack checks run on the staged pack before it is swapped in, in every build including `--release`, and any failed check fails the build (the previous pack stays). They cover criteria 8–11 and 13b, edge ends equal to their node positions, and the deck-end step check. `export_glb` also reads back every written mesh's positions and indices and fails on any mismatch. Cost on the full Somero build: 3.4 s for the pack checks and 0.25 s for the read-back.
- **Intermediate output** in `tools/mapgen/build/<map-id>/<area>/`:
  - `04-roads/roads.geojson`: merged surfaces and bridge outlines, with class, layer and source way ids;
  - `04-roads/report.json`: skipped ways, defaults used, clearance warnings, `suspected_missing_bridges`, excluded crossings (culverts and fords), water relations left out;
  - `07-road-graph/graph.geojson`: nodes and edges for QGIS, in map CRS world coordinates with the legacy `crs` member;
  - `07-road-graph/report.json`: connectivity and per-class counts.

### Map pack

- `road_graph.json`: `schema_version` (1), `map_id`, `area`, `nodes` (`id`, `kind`, `position`, `osm_node` or `null`) and `edges` (the fields above).
  - It is defined by a pydantic model, and its JSON Schema is committed as `tools/mapgen/src/mapgen/build/schemas/road-graph-v1.schema.json`.
  - Every written file is validated against the committed schema, and a test checks that the committed and generated schemas match (as for the manifest and camera presets).
- The file is found by its fixed name and isn't listed in the manifest, following `buildings.json` and `camera_presets.json`, so the manifest stays at schema version 1.
- There is no new credit: the OSM file is already read and credited.

### Test-area fixture

- `cut_test_area.py` also keeps every highway way that has at least one node inside the test area, with all of its nodes. The fixture's roads are then cut at the test-area edge by the build, not by the fixture.
- It reads only the local raw data in `tools/mapgen/data/raw/somero/`. Nothing is downloaded.
- Contributor metadata (`user`, `uid`, `timestamp`, `changeset`, `version`) is stripped as before.
- The fixture's `NOTICE.md` and its download manifest entry are updated to say that road ways are included.

### Roadman.Core

- `RoadGraph` types (nodes, edges, attributes, name fields) and `RoadGraphReader`, which parses `road_graph.json` the way `MapManifestReader` does. It rejects each of these with a `MapPackException` that names the problem:
  - another `schema_version`;
  - missing fields;
  - unknown node kinds;
  - duplicate ids;
  - edges that reference unknown nodes;
  - edges with fewer than 2 points;
  - an edge that is neither drivable nor walkable;
  - a drivable edge without `speed_kmh`;
  - non-finite numbers.
- Lookups needed by the debug view and later features: the drivable edges leaving a node in their allowed direction, the walkable edges at a node, and the nearest node to a position (optionally only drivable or only walkable nodes).

### Game

- `MapLoader` reads `road_graph.json` after the tiles and exposes it as `MapLoader.RoadGraph`. If the file is missing or invalid, it pushes an error with the pack path and the hint to run `/regen-map <id>`. The tiles still load, and `RoadGraph` is null.
- `MapLoader` sets a `RoadMaterial` export on `road` meshes and bridge decks, and a `PathMaterial` export on `path` meshes, with placeholders in `materials/placeholder/`, as it does for terrain and buildings.
- **Debug view:** in debug builds only (`OS.IsDebugBuild()`), G (`debug_road_graph`, physical key) toggles drawing of the graph just above the surfaces:
  - drivable edges coloured by class, and walkable-only edges in one separate colour;
  - an arrow on one-way edges;
  - nodes coloured by kind, with `area_edge` and `dead_end` nodes clearly distinct.

  F3 isn't used because it is Mission Control on macOS.
- The screenshot tool gets `--road-graph` to capture the view, like `--orthophoto`.

### Edge cases

- A way that leaves and re-enters the build area becomes separate pieces, each ending in `area_edge` nodes.
- A closed way (a roundabout or a loop) is split at its first node and at shared nodes, so no edge starts and ends at the same node with zero length.
- Two included ways that share only their end node meet at a `joint` node and stay separate edges.
- A self-intersecting way connects to itself only at OSM nodes it repeats.
- A bridge or tunnel way whose middle is cut by the area edge gets its end heights from the terrain at the cuts.
- A tagged bridge way that also crosses water isn't inferred again, and isn't listed as suspected.
- A way tagged both `bridge` and `tunnel` is treated as a bridge and counted in the report.
- No included ways in the area: a valid `road_graph.json` with empty lists, no road nodes in the tiles, and a log line. This isn't a build failure.

### Camera preset `bridge-side`

`maps/somero/camera_presets.json` gets a preset `bridge-side` that looks at the origin bridge from the side. The camera stands on the Heikintie cycleway in the cutting, about 30 m west-southwest of the bridge. It looks along the cycleway, which runs at right angles to the bridge axis, so both decks are seen side-on against the sky.

```json
"bridge-side": {"position": [-43.3, 1.7, -8.1], "look_at": [-15.8, 2.8, -20.1], "fov": 70, "ground_relative": true}
```

- These values come from the OSM and DEM data (Godot axes: z = −north).
- `position` is the point on the cycleway 30 m before its crossing, at eye height above a DEM ground height of 89.3 m.
- `look_at` is the midpoint between the two crossings, 2.8 m above the 88.5 m ground there, which puts it about 91.3 m, the middle of the 0.5 m deck under the 91.55 m driving surface.
- The implementation tries the preset with `--presets` before committing it, and adjusts it if a building or the terrain hides the bridge.
- The preset is inside the test area, so test-area packs render it too.

### Decisions to record

The implementation records an ADR (0012) for the `roads` block in `map.yaml`, the road surface draping and collision, the bridge geometry, inferred bridges, the rule that terrain under bridges is never changed, and the `road_graph.json` format, including the name fields. It also updates `docs/architecture.md`.

## Acceptance criteria

1. `mapgen build somero --area test` writes `road_graph.json` that validates against the committed `road-graph-v1.schema.json`, and the committed schema equals the one generated from the model.
2. `map.yaml` accepts the `roads` block above, and rejects unknown keys, non-positive widths, lanes, speeds and bridge sizes, a negative `min_clearance_m`, and a non-list `inferred_waterways`. A map without a `roads` block builds without road meshes, writes a `road_graph.json` with empty `nodes` and `edges`, and logs it.
3. Every included highway way whose line touches the build area is represented by at least one edge with its `osm_way`. Every other highway way in the area is in the report with a reason. The included and skipped counts add up to the number of highway ways in the area. In the test-area build, Jokioistentie way 88027328 (124 m in the area, with at most one node inside it) has edges and a road surface.
4. Graph nodes exist only at OSM nodes or `area_edge` cuts. In the test area, neither edge of the origin bridge connects to the Heikintie cycleway (way 88027331) that passes under it.
5. Class and flags: a fixture of OSM snippets gives the right class for `service`, `service=driveway` and `service=parking_aisle`, and the right `drivable` and `walkable` values and sources for class defaults and for each access tag case above.
6. Values and sources: OSM `maxspeed`, `lanes` and `width` win over defaults and have the source `osm`. Unparseable values fall back with the source `default` and are counted. Drivable classes without `speed_kmh` get `default_speed_kmh`. Non-drivable edges have `speed_kmh` `null`.
7. One-way: the snippet fixture with `oneway=yes`, `-1`, `no`, missing and `junction=roundabout` gives the directions described, and `-1` edges are stored reversed.
8. Every node and edge point is finite and inside the build area's local bounds, and each edge's `length_m` equals its polyline length within 1 mm.
9. Outside ramp areas, every `road` and `path` vertex is `surface_offset_m` above the terrain mesh surface at its x/z, within 1 mm. Inside ramp areas, every vertex equals the ramp height ("Ramps") within 1 mm. No vertex is below terrain + offset by more than 1 mm. Every graph point on a ground edge is at the surface height at its x/z, within 1 mm.
10. Each tile with surfaces in its bounds has `road-col` and/or `path-col` nodes. Every surface vertex is inside its tile's `bounds_local` (within 1 mm), and the surface vertices on a shared tile edge match the neighbour's within 1 mm. `road` and `path` don't overlap (intersection area under 0.01 m² per tile).
11. Every included way tagged as a bridge in the test-area build (the origin bridge's two ways), and every bridge in the snippet fixtures (single ways, a span of several ways, a span cut by the area edge), is exported as `bridge_osm_way_<id>-col` nodes with a railing along each long edge. Its deck ends meet the ramp surface (criterion 11a).
    - **Straight deck:** for every vertex of the deck's driving surface, its height minus the span's straight line at that point (by distance along the centre line) is at most 0.05 m. It is never negative; a tolerance of 0.01 mm allows only float32 rounding, which is under 0.008 mm at Somero's terrain heights (about 81 to 138 m).
12. A bridge snippet with a deck below `min_clearance_m` above the terrain produces a clearance warning in `04-roads/report.json`, and the build still succeeds.
13. A tunnel snippet produces graph edges with `tunnel: true`, heights linear between its end nodes, and no mesh or collision node.
13a. **Inferred bridges:** snippets of a road without a `bridge` tag crossing (1) a waterway line, (2) a water-area way and (3) a water multipolygon each produce:
    - a `suspected_missing_bridges` entry with `osm:way/<id>`, the water feature's id and its local, map CRS and WGS84 positions;
    - a deck that passes criterion 11's straight-deck check;
    - edges with `bridge_source: "inferred"`;
    - no road surface vertex below the deck span in the valley.

    Snippets with a culvert waterway, a negative-`layer` waterway and a `ford=yes` node give no inferred bridge and are counted as excluded. The full Somero build's report lists its suspected missing bridges; the 2026-10-29 data has none.
11a. **No steps at deck ends:** along every deck end edge that meets a road or path (probed at the corners and every 0.1 m), the vertical gap between the deck surface and the adjoining surface is at most 2 cm, in the test-area, full and snippet builds.
11b. **Pack checks fail the build:** a pack that fails a pack check, or a GLB whose read-back differs from what was written, fails the build with the previous pack left in place, also with `--release`.
13b. **Terrain under bridges:** in the test-area build, every terrain mesh vertex inside a bridge deck's footprint is identical to the vertex the terrain builder produces from the DEM alone.
14. Name fields: every edge has `name`, `name_sv` and `ref`. A snippet fixture covers a missing tag, an empty tag, surrounding whitespace, a non-NFC string and a `;` multi-value, and the output is UTF-8 with unescaped non-ASCII characters.
15. Connectivity: in the full Somero build (run locally when raw data is present; CI has only the test area), the largest weakly connected drivable component contains at least 95% of the drivable edges. In every build, `07-road-graph/report.json` lists the other components and the share.
16. `04-roads/` and `07-road-graph/` intermediate files are written.
17. Two builds from the same raw data write byte-identical `road_graph.json` and road meshes.
18. The pipeline has no map-specific road code: a test builds a small synthetic map config with a different `roads` block and gets that block's values.
19. The recreated test-area fixture contains the highway ways that touch the test area, and no contributor metadata attributes; `cut_test_area.py` reads only local raw data.
20. `RoadGraphReader` accepts a valid file and rejects each invalid case listed under "Roadman.Core" with a `MapPackException` that names the problem. The lookups return direction-aware drivable edges and walkable edges.
21. After the test-area pack loads in Godot, `MapLoader.RoadGraph` has the node and edge counts of the file. A ray cast down at a ground drivable edge's midpoint hits `road` collision at the edge's height within 1 cm.
22. **Along the bridge:** for each of the origin bridge's two ways, ray casts point down every 1 m along the centre line, from 0.5 m inside one end to 0.5 m inside the other, each starting 5 m above the deck line. Every ray's first hit is the way's `bridge_osm_way_<id>` collision, never the terrain or the cycleway's `path` collision below, at a height between the deck line and 0.05 m above it (with the same 0.01 mm tolerance as criterion 11).
23. A pack without `road_graph.json` loads its tiles, pushes an error naming the path and `/regen-map`, and leaves `RoadGraph` null. A pack with an empty graph loads without an error.
24. G toggles the road graph debug view in a debug build. `/verify` passes; the `overview` and `centre-street-level` screenshots show road and path surfaces, and a `--road-graph` screenshot of `overview` shows the graph. All three have been looked at and described.
25. `maps/somero/camera_presets.json` has the `bridge-side` preset, and `/verify` writes `build/screenshots/bridge-side.png` showing the origin bridge from the side. The implementation report describes whether the deck runs straight across: no sag or hump toward the cutting, ends meeting the road on both sides, railings visible.

## Test ideas

| Criterion | Layer |
| --- | --- |
| 1, 3, 4, 8, 16, 17 | pytest: end-to-end test-area build from the fixture (with roads) |
| 2, 18 | pytest: config validation, synthetic map config |
| 5, 6, 7, 12, 13, 13a, 14 | pytest: unit tests on small hand-written OSM snippets, with a synthetic heightfield that has a valley for the bridge and water cases |
| 9, 10, 11, 11a, 11b, 13b | the pipeline's own pack checks (`build/pack_checks.py`), which every build runs and pytest calls; they sample the terrain mesh triangles, not the heightfield. pytest also checks that a failing check or read-back fails the build |
| 25 | pytest: the Somero presets file validates and contains `bridge-side`; screenshot via `/verify`, described in the report |
| 15 | pytest, marked to run only when `data/raw/somero/` is present (skipped in CI), plus the report check on every build |
| 19 | pytest on the fixture's OSM file |
| 20 | `Roadman.Core` xUnit: reader valid and invalid cases, lookups |
| 21, 22, 23 | gdUnit4: load the test-area pack, ray casts (22 along both bridge centre lines), a temporary pack copy without `road_graph.json` |
| 24 | gdUnit4 for the toggle, screenshots via `/verify`, and a playtest drive through the centre, over the origin bridge and along the cycleway under it |

The test-area fixture must be recreated with roads before the pytest and gdUnit4 build tests can run in CI.

## Open questions

None. All earlier questions were answered on 2026-10-29.
