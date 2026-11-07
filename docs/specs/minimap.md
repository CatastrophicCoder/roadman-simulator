# Spec: minimap

## Status

Approved

## Goal

Give the player a round minimap in a screen corner that shows where they are in Somero and what is around them. A top-down background image from the pipeline shows the buildings, water and land use. On top of it the game draws the roads from the road graph, street names, symbols for points of interest and the world edge. The map is centred on the player and turns with the camera, so up on the minimap is forward on the screen. Its corner comes from the existing "Minimap corner" gameplay setting. Covers "Minimap" under "Game features" in `docs/project-outline.md`, the minimap PNG of Phase 3 stage 9, and the "minimap" part of milestone 2. The geometry (projection, rotation, clipping, label placement, symbol groups) lives in `Roadman.Core` with xUnit tests; Godot holds only the drawing, the HUD node and its wiring.

Out of scope (later specs): quest, mission and objective markers (this spec only adds an empty marker layer and its API, see "Markers"); a full-screen map; zoom, including zooming out with speed (vehicles spec); waypoints and route guidance (routing comes with the traffic AI spec); police and wanted-level display (wanted level milestone); names of points of interest and business names (they wait for the business-name decision, CLAUDE.md); water and land use meshes in the 3D world (Phase 3 stage 5); vegetation; `natural`, `leisure` and other non-`landuse` areas in the background image; water lines from OSM (`waterway` lines; in OSM water mode the image has no stream lines); the final art-direction palette; rebinding controls.

## Context (2026-11-08)

- `docs/project-outline.md`, "Minimap": centred on the player, rotating with the player's heading, roads from the road graph, street names and points of interest; mission and quest markers later; position from the settings, any of the four corners. "Place information": "Minimap: street labels." Phase 3 stage 9: "render a top-down minimap PNG"; Phase 5 step 4: "the minimap PNG with a player marker". `docs/architecture.md` lists `minimap.png` in the planned pack contents. No pipeline code writes one yet.
- Settings (ADR 0018): `GameplaySettings.MinimapCorner` (`TopLeft`, `TopRight`, `BottomLeft`, `BottomRight`; default `BottomLeft`), stored as `minimap_corner` in `settings.json`. `SettingsView` shows "Minimap corner" with the note `SettingsView.MinimapNote` ("Used by the minimap"). Each change applies at once through `Changed(before, after)`; `Main` hands camera and quality values to `World` on every change. `docs/specs/menu-settings-save.md` criterion 15: "The minimap is in the bottom-left corner by default."
- Road graph (full Somero pack, generated 2026-11-04): 979 nodes, 1 283 edges, 3 722 polyline points, 864 drivable edges, 630 edges with a `name`, 29 bridge edges, 2 tunnel edges. Classes: residential 442, cycleway 380, service 149, tertiary 119, driveway 66, primary 43, footway 30, parking_aisle 17, unclassified 15, living_street 8, track 5, pedestrian 5, path 4. `RoadEdge.Points` is the polyline in local metres; `RoadEdge.Names` holds `Name`, `NameSv`, `Ref`. `StreetLabel.Format` gives `name (ref)`, the name, the ref, or a class text, Finnish only.
- Points of interest (`pois.json`, `MapLoader.Places`): 54 in the full pack, 42 areas and 12 points. Types: amenity 35, leisure 10, shop 7, tourism 2. Amenity subtypes: parking 16, fuel 6, school 3, grave_yard 2, and one each of cinema, cafe, pharmacy, marketplace, place_of_worship, restaurant, library, fire_station. `Poi.Position` is the node or the area's representative point.
- Pipeline inputs (Somero raw OSM): `landuse` areas residential 17, farmland 12, industrial 7, retail 4, meadow 1 (`osm_landuse` reads only closed ways and relations with a `landuse` tag); water areas (`natural=water`, `waterway=riverbank`) 6, `waterway` lines river 3, flowline 3. MML Topographic Database (`maastotietokanta_kaikki.gpkg`): `jarvi` 2, `virtavesialue` 2, `virtavesikapea` 11. The world-edge step (ADR 0015) takes its water areas from the MML Topographic Database when a source provides the `water_areas` capability (`mml_topographic` reads `jarvi` and `virtavesialue`, and the lines `virtavesikapea`), and from OSM water areas otherwise; Somero uses MML. Building footprints come from OSM and the MML Topographic Database (ADR 0014). The pipeline already depends on rasterio (GDAL), which can rasterise polygons and write PNG.
- Build area: Somero's bbox is about 2.2 × 2.2 km; the test area is a 300 m square (`test_area.size_m`).
- World edge: `MapLoader.Boundary.Ring` (counter-clockwise `PlanarPoint`s in local x/z), `Roadblocks` (spans with `start` and `end`), `EdgeGeometry`.
- HUD: `StreetLabelHud` (a `CanvasLayer`, bottom centre, every build) is `World.Hud`; `Main` hides it while paused (pause menu, settings, slot list, inventory) and `World` sets `Suppressed` from the death screen until the respawn. Screenshots move it into the capture viewport.
- Player and camera: `World.PlayerFacingYaw` is the model's facing (saved with the game). The player camera orbits freely, with no swing-back. While the debug camera is current the player camera doesn't orbit.
- Window: `stretch/mode` `canvas_items`, `aspect` `expand`, the default base size 1152 × 648, so at 1920 × 1080 one base unit is 1.667 px.
- UI: `UiTheme` holds the placeholder colours (`Panel`, `Shade`, `Accent`, `Text`, `TextMuted`, ...) and Godot's default font.
- Art direction (outline): low-poly, flat or near-flat shading, small palette; the palette is still undecided.
- Performance tooling: `PerfTool` (`--perf <spawn|roadblock> <out.json>`, `--no-world-edge`, `--no-space-sky`) and `.claude/scripts/perf-world-edge.sh` (world-edge spec criterion 27).

## Behaviour

### Pipeline: background image

A new build step in `tools/mapgen/` writes the background image for every map (no map-specific code).

- **Files:** `minimap.png` and `minimap.json` in the pack root, written by every build (full and test area). Not listed in the manifest, like `boundary.json` and `pois.json`.
- **Config:** an optional `minimap` block in `map.yaml` with `m_per_px` (metres per pixel; default 1.0, allowed 0.5 to 2.0) and `stream_width_m` (see content step 5), both checked by `MapConfig`. The default comes from the panel: at 1080p the panel shows 300 m across 220 px, 1.36 m per screen pixel, so 1.0 m/px gives at least one image pixel per screen pixel up to about 1470 px of screen height. Somero at 1.0 m/px is about 2 200 × 2 200 px.
- **Grid:** the image covers the build area's bounds in local x/z (the union of the tile bounds), from its minimum x and minimum z (north edge) corner; width = ceil((max_x − min_x) / m_per_px), height likewise; the top row is north, the left column west. RGBA, 8 bits per channel.
- **Content, painted in this order** (later layers cover earlier ones), with no roads, paths, bridges, labels or symbols:
  1. Outside the boundary ring: fully transparent (alpha 0).
  2. Inside the ring: the ground colour.
  3. `landuse` areas, one colour per group: built land (`residential`, `retail`, `commercial`, `industrial`, and other built-up values listed in the step's table), farmland and green land (`farmland`, `meadow`, `orchard`, `grass`, and the other open-land values in the table). Values not in the table keep the ground colour; the build log counts them by value.
  4. Water areas, one colour, from the same source as the world edge (ADR 0015): the MML Topographic Database's `jarvi` and `virtavesialue` when a source provides the `water_areas` capability, else OSM water areas. The step reuses the world-edge step's water loading, so the two never disagree.
  5. Narrow streams: MML `virtavesikapea` lines (only in MML water mode), in the water colour, as lines `stream_width_m` wide (buffered by half the width and rasterised as polygons). Default 2.0 m, which is about 1.5 screen px at 1080p (the panel shows 1.36 m per screen pixel there); `MapConfig` allows 1.4 to 2.7 m, the 1 to 2 px band at 1080p. They scale with the panel like the rest of the image.
  6. Building footprints, every building in `buildings.json` (OSM and MML-only), one colour.
  Every layer is clipped to the ring, so nothing paints outside it.
- **Palette:** flat RGBA colours in one table in the pipeline step (placeholders until the art-direction palette exists; then the style preset supplies them). No shading, gradients or textures.
- **Rasterising:** with rasterio (`rasterio.features.rasterize`, pixel-centre rule, no anti-aliasing) and written with GDAL's PNG driver. No new dependency.
- **`minimap.json`:** `schema_version` (`1`), `map_id`, `area`, `image` (`"minimap.png"`), `m_per_px`, `width`, `height`, `bounds_local` (`min_x`, `max_x`, `min_z`, `max_z` of the image's outer pixel edges, so `max_x − min_x = width × m_per_px`). A committed JSON Schema `minimap-v1.schema.json`, validated after every build.
- **Determinism:** the same input gives identical pixels and an identical `minimap.json`.
- **Pack checks:** the PNG's size matches `width` and `height`; the bounds cover every tile; every pixel outside the ring (by pixel centre) has alpha 0, and every pixel inside has alpha 255.
- **Import in Godot:** the PNG is imported as a texture (no `.gdignore`), lossless, with mipmaps, so it shrinks cleanly when rotated. How those import settings are set for a generated, git-ignored file (`[importer_defaults]` or a pipeline-written `.import` file) is part of the implementation plan and recorded in the ADR. Both exports include it.

### What the player sees

- A round minimap panel in the corner from `GameplaySettings.MinimapCorner`, inset from both screen edges by a fixed margin, in every build (debug and release).
- **Size:** `PanelDiameter` 132 base units (220 px at 1920 × 1080; it scales with the UI through the stretch settings). **Range:** `RangeM` 150: the rim is 150 m from the player. Both are exports; there is no zoom.
- **Rotation:** the map turns with the player camera's yaw, so the direction the camera looks (projected on the ground) is panel up. The player sits at the centre as an arrow in `UiTheme.Accent`, pointing in the player's facing (`World.PlayerFacingYaw`) relative to the camera yaw; it points up when the player faces away from the camera.
- **North indicator:** an "N" on the rim at the bearing of north, moving round the rim as the map turns.
- **Layers**, back to front, all clipped to the circle:
  1. The void: a dark flat fill over the whole circle.
  2. The background image, placed by `minimap.json` and rotated with the map (outside the ring it is transparent, so the void shows).
  3. The boundary ring as a line, and each roadblock as a short bar across the road between its `start` and `end`. No fence symbols.
  4. Roads: every non-tunnel edge as a polyline with flat strokes, width and colour per class group: main roads (`primary`, `secondary`), roads (`tertiary`, `unclassified`, `residential`, `living_street`), minor drivable (`service`, `driveway`, `parking_aisle`, `track`) and paths (`cycleway`, `footway`, `path`, `pedestrian`). Paths are fainter and thinner than every drivable group. An unknown class goes to minor drivable if the edge is drivable, else to paths. Bridge edges are drawn after the others, so they sit on top. Tunnel edges are not drawn.
  5. Street names (see "Labels").
  6. Point-of-interest symbols (see "Points of interest").
  7. The marker layer (empty in this spec).
  8. The player arrow and the north indicator.
- **Style:** flat colours, no shading or gradients, outlines at most one pixel. The game's colours are new constants in `UiTheme` (one place for the art-direction palette later). Our own look: generic shapes, no other games' styles, symbols or names.

### Corner and visibility

- Changing "Minimap corner" in the settings view (also from the pause menu) moves the minimap within one process frame, with no reload: `Main` hands the gameplay settings to `World` on every change (`World.MinimapSettings`, like `World.CameraSettings`). While paused the minimap is hidden, so the new corner shows on resume. In direct `World` runs (tests, screenshots, `--perf`) it uses the default `BottomLeft` unless a test or `--minimap-corner` sets it.
- **Hidden** while paused (pause menu, settings view, slot list, inventory screen), whenever the death sequence isn't `Alive` (the fall, the death screen, respawning and a failed respawn), and whenever there is no player (before the spawn, after a failed spawn).
- **Shown** while the debug camera is on (debug builds), still centred on the player; the heading stays the player camera's yaw (which doesn't change while the debug camera is current).
- Viewport size changes keep it in its corner at the same size in base units.

### Data and map loads

- `MapLoader` reads `minimap.json` (`MapLoader.MinimapImage`, a `Roadman.Core.Maps.MinimapImage` from `MinimapImageReader`, strict like the other readers) and loads `res://maps/<id>/minimap.png` after the boundary, before `MapLoaded`. A missing or invalid `minimap.json`, a missing image, or an image whose size doesn't match pushes an error naming the path and `/regen-map <id>` and leaves it null; the tiles, road graph, places and boundary still load.
- The minimap reads `MinimapImage`, `RoadGraph`, `Places` and `Boundary` after each `MapLoaded` and rebuilds its prepared data on every load or reload. Each is optional: no image draws no background (the inside of the ring is then filled with the ground colour, so the edge still reads); no road graph draws no roads or labels; no places draws no symbols; no boundary draws no ring or roadblocks and no void (the whole circle gets the ground colour). The minimap itself pushes no error for these (`MapLoader` already does).
- Prepared data is built once per load: road polylines grouped per class group and per spatial cell, so each frame only cells within the visible radius are projected.

### Roadman.Core

New folder `Roadman.Core/Minimap/`, engine-free, plus the reader in `Roadman.Core/Maps/`.

- **`MinimapImage`** and **`MinimapImageReader`** (`Roadman.Core/Maps/`): the parsed `minimap.json`; the reader rejects unknown or missing keys, a wrong schema version, a map id or area that differs from the manifest's, non-positive sizes, and bounds that don't equal size × `m_per_px`. `PixelOf(x, z)` and its inverse for placing the image.
- **`MinimapProjection`**: built from the centre (x, z), the heading yaw, the range in metres and the panel radius in pixels. `ToPanel(x, z)` maps a local ground point to panel coordinates (origin at the panel centre, +y down), so that the heading direction is panel up and the scale is `radius / range` px per metre; `ToWorld(px, py)` is its inverse. Godot's axes: x east, z south (north is −z); yaw follows `Node3D.Rotation.Y` (0 faces −z, north). `NorthBearing` is the panel angle of north. `ArrowAngle(facingYaw)` is the arrow's panel angle.
- **`MinimapClip`**: clips polylines to the circle and gives `ClampToRim(px, py)` for points outside it (used by the north indicator and later by markers).
- **`MinimapStyle`**: maps a road edge to its class group (the table above) and a point of interest to its symbol group (see "Points of interest").
- **`MinimapLabels`**: given the projected, clipped edges with their label text (see "Labels") and a text-measuring function (width and height in pixels, supplied by Godot), returns label placements: the text, an anchor point, an angle and the polyline section it runs along. Deterministic for the same input.
- **`MinimapMarker`** and **`MinimapMarkerSet`**: see "Markers".

### Labels

- Only edges with a `name` or a `ref` get labels, with the text from `StreetLabel.Format` (Finnish names only, never `name_sv`; class texts are not shown on the map). Edges are grouped by their label text; each group gets at most one label in view.
- A label runs along the longest straight-enough stretch of its group's visible polyline, centred on it, rotated to the stretch's direction and turned by 180° when it would read upside down (the text's up vector never points into the lower half-plane of the panel).
- A label needs a stretch at least as long as the text plus a padding; groups without one get no label.
- Labels never overlap each other, the player arrow, the north indicator or the rim: candidates are placed in order of class group (main roads first), then visible length (longest first), then label text (ordinal), and a candidate that would overlap an earlier one is dropped.
- Text in the default font with a dark outline (`UiTheme.Shade`), at a fixed size in base units (export).
- Labels are recomputed when the centre moves or the heading turns by more than a threshold (both exports), not every frame, so they don't flicker. Between recomputes they are drawn at their stored world positions, re-projected each frame, so they move with the map.

### Points of interest

- Every point of interest in `pois.json` is shown as a symbol at `Poi.Position`; no outlines, no names. Symbols stay upright while the map turns. Symbols outside the circle are not drawn.
- Five symbol groups, each with its own generic shape and colour:

  | Group | Points of interest | Symbol |
  | --- | --- | --- |
  | Shop | type `shop`; amenity `marketplace` | square |
  | Food and drink | amenity `cafe`, `restaurant`, `fast_food`, `bar`, `pub`, `ice_cream`, `food_court`, `biergarten` | circle |
  | Public services | amenity `school`, `kindergarten`, `college`, `university`, `library`, `pharmacy`, `hospital`, `clinic`, `doctors`, `dentist`, `fire_station`, `police`, `townhall`, `post_office`, `community_centre`, `place_of_worship`, `grave_yard` | diamond |
  | Parking | amenity `parking`, `parking_space`, `motorcycle_parking` | rounded square with "P" |
  | Other | everything else (amenity `fuel`, `cinema` and others not listed; types `office`, `tourism`, `leisure`) | small dot |

### Markers

- `MinimapMarker`: an id (non-blank), a world position (x, z), a kind from an enum with one value now (`Generic`), and whether it clamps to the rim when outside the circle.
- `MinimapMarkerSet`: `Add` (a blank or repeated id throws, naming it), `Remove(id)` (returns whether it removed one), `Clear`, `All` (ordered by id), and a `Changed` event raised once per change, never for a no-op.
- The Godot minimap owns one set (`MinimapHud.Markers`) and draws its markers in the marker layer as a generic shape; clamped markers sit on the rim. Nothing in the game adds markers in this spec; the quest system will, resolving marker ids from the pack to positions (missions never use raw coordinates). A map load clears the set.

### Godot

- `scripts/hud/MinimapHud.cs` (`Roadman.Hud.MinimapHud`), a `CanvasLayer` built in code, added by `World` next to the street label (`World.Minimap`), in every build.
- Centre, facing and camera yaw come from the player and the player camera each process frame (their rendered positions, so the map moves in step with the camera).
- Exports: `PanelDiameter`, margin, `RangeM`, line widths per class group, label font size, the recompute thresholds. Colours in `UiTheme`.
- Screenshots: in `--player` runs (the only runs with a visible player) the minimap is moved into the capture viewport, so `centre-street-level-player.png` shows it; other preset images are unchanged. A new flag `--minimap-corner <top-left|top-right|bottom-left|bottom-right>` (debug builds, only with `--player`; otherwise an argument error, exit 1) sets the corner for that capture. `/verify` adds one run: `centre-street-level --player --minimap-corner top-right` (`centre-street-level-player-minimap-top-right.png`).
- Performance: `PerfTool` gets `--no-minimap` (no minimap is added). `.claude/scripts/perf-minimap.sh` runs both spots (`spawn`, `roadblock`), headless (with `--fixed-fps`) and windowed, `PERF_REPEATS` (3) times each, alternating full and `--no-minimap`, and reports medians: the minimap's headless frame cost and windowed render cost, plus the drawn segment, label and symbol counts. Per spot, headless frame cost + windowed render cost < 0.5 ms is PASS, else FAIL (exit 1). Not part of `/verify`.

### Settings view

- `SettingsView.MinimapNote` is removed: "Minimap corner" has no note.

### Docs

- `game/CLAUDE.md`: a "Minimap" section (node, data, settings, visibility, markers, screenshots, the perf script); the screenshot list gets the new run.
- `tools/mapgen/CLAUDE.md`: the background-image step and the `minimap` config block.
- `docs/architecture.md`: `minimap.png` and `minimap.json` under the pack contents (replacing "planned"), the step in the pipeline, the HUD minimap under "Gameplay (Godot C#)", `Roadman.Core/Minimap/`.
- `docs/specs/menu-settings-save.md`: an amendment noting that the "Used by the minimap" note was removed by this spec.
- A new ADR: the background image from the pipeline under roads, labels and symbols drawn live from the pack data in the game; its grid, transparency, water source (the world edge's) and import settings.

## Acceptance criteria

1. Pipeline: `mapgen build` writes `minimap.png` and `minimap.json` for the full and the test-area build; `minimap.json` validates against `minimap-v1.schema.json`; the PNG's size equals `width` × `height`; `bounds_local` spans `width × m_per_px` by `height × m_per_px` and covers every tile; the test area at the default is 300 × 300 px.
2. Pipeline content: pixels whose centre is outside the ring have alpha 0, inside alpha 255; a pixel at a building footprint's interior has the building colour; a water-area interior pixel (away from buildings) the water colour; in MML water mode the water comes from `jarvi` and `virtavesialue` and a pixel on a `virtavesikapea` line has the water colour, with the painted line between `stream_width_m` − 1 px and `stream_width_m` + 1 px wide across its direction; in OSM water mode the water comes from the OSM areas and no line is painted; the image's water areas are the same polygons the world-edge step uses for its water crossings; `farmland` and `residential` interior pixels (away from water and buildings) their group colours; an unlisted `landuse` value keeps the ground colour and is counted in the log; no pixel has a colour outside the palette. Checked on a small synthetic config and on the test area.
3. Pipeline: two builds from the same input give identical pixels and an identical `minimap.json`; `m_per_px` outside 0.5 to 2.0 and `stream_width_m` outside 1.4 to 2.7 are config errors naming the key.
4. `MinimapImageReader` accepts the pipeline's output and rejects, naming the key: unknown or missing keys, schema version 2, a map id or area not matching the manifest's, a non-positive size, bounds that don't equal size × `m_per_px`. `PixelOf` and its inverse round-trip.
5. `MinimapProjection`: with the camera yaw facing north, a point 10 m north of the centre maps to (0, −10 · s) and a point 10 m east to (+10 · s, 0), where s = radius / range; facing east, a point 10 m east maps to (0, −10 · s); facing south, a point 10 m north maps to (0, +10 · s). `ToWorld(ToPanel(p))` returns p within 1e-9 for a seeded set of points, centres and yaws. `NorthBearing` matches the panel direction of a point due north. `ArrowAngle` is 0 (up) when facing yaw equals camera yaw and 90° (right) when the player faces 90° clockwise of the camera's direction.
6. `MinimapClip`: segments fully inside are unchanged, fully outside are dropped, crossing ones end on the rim within 1e-6 px; `ClampToRim` of an outside point lies on the rim on the ray from the centre, of an inside point is the point itself.
7. `MinimapStyle`: every road class in the full pack maps to the group in the table, an unknown drivable class to minor drivable and an unknown non-drivable one to paths; every point of interest in the full pack maps to the group in the table (fuel and cinema to Other, marketplace to Shop, grave_yard to Public services, parking to Parking); paths have a smaller width and a lower opacity than every drivable group.
8. `MinimapLabels`: for a fixture of crossing named streets, no two placed labels' rectangles overlap, none overlaps the player arrow's or north indicator's rectangle, and every label lies inside the circle; no label reads upside down for yaws in 15° steps round the full circle; each label text appears at most once; edges without `name` or `ref` get none; a stretch shorter than the text plus padding gets none; a label is never the Swedish name; the same input gives the same placements.
9. `MinimapMarkerSet`: `Add` of a repeated id throws naming it and changes nothing; a blank id throws; `Remove` of an unknown id returns false; `All` is ordered by id; `Changed` fires once per change and not for a no-op.
10. On the full pack, with the player at the spawn, the minimap is visible in the bottom-left corner by default (round, `PanelDiameter` across, the margin from the left and bottom edges), draws the background image, at least one road segment and the player arrow (menu-settings-save criterion 15 now checked).
11. Changing the corner through `SettingsView` (each of the four values) moves the panel to that corner within one process frame, without a reload; the setting persists through `Close()` and the next game uses it. "Minimap corner" shows no note.
12. Visibility (gdUnit4, test pack): hidden while the pause menu, settings view, slot list and inventory screen show; hidden during the death fall and the death screen, visible again after the respawn; hidden before the spawn and after a failed spawn; shown while the debug camera is on, centred on the player, with its heading unchanged by moving the debug camera.
13. Rotation in the game: with the player camera turned to look east, a road point due east of the player projects above the centre and the north indicator is on the left of the rim; turning the player model without turning the camera rotates only the arrow.
14. A map reload rebuilds the minimap's data and clears its markers. A pack without `minimap.json` (or with a mismatched image size) pushes one error naming the path and `/regen-map <id>`, still loads, and the minimap draws the ring filled with the ground colour; a pack without `road_graph.json`, `pois.json` or `boundary.json` still shows the minimap with the other layers and pushes no error beyond `MapLoader`'s.
15. Tunnel edges are not drawn; bridge edges are drawn after all non-bridge edges (checked on the draw order the minimap exposes for tests).
16. No point-of-interest or building name appears in any text the minimap draws (its labels are only Finnish street names and refs); checked against every name in the full pack's `pois.json` and `buildings.json`.
17. Screenshot: `--screenshot centre-street-level ... --player --seed 1` shows the round minimap in the bottom-left corner of the capture, with the background image, roads, at least one street label, point-of-interest symbols where the area has them, and the player arrow; `--minimap-corner top-right` moves it; checked by viewing the images. `--minimap-corner` without `--player`, with an unknown value, or in a non-debug build exits 1.
18. The export check finds the imported minimap image in both exports' packs, and the exported game loads it (the headless startup of an export build reports no minimap error).
19. Performance: on the full Somero pack at the `spawn` and `roadblock` spots, `perf-minimap.sh` reports the minimap's headless frame cost and windowed render cost (medians of 3 alternating runs against `--no-minimap`) and the drawn segment, label and symbol counts, and per spot their sum is under 0.5 ms.
20. The minimap's text, colours and symbols, and the pipeline step's names, contain no other games' names or trademarks (word-list test as in ADR 0019, plus the reviewer).
21. `/verify` passes, including the headless startup and the screenshots.

## Test ideas

| Criterion | Layer |
| --- | --- |
| 1, 2, 3 | pytest: a synthetic config with one building, one water area, one stream line and two land use areas inside a square ring, in MML and OSM water mode; the test-area end-to-end build; a determinism test building twice |
| 4, 5, 6, 7, 8, 9 | `Roadman.Core` xUnit (`Roadman.Core.Tests/Minimap/`, `Maps/`); seeded random points for the projection round trip; a hand-built road graph fixture for labels; the full pack's class and subtype lists for 7 copied as constants |
| 10, 13, 15, 16 | gdUnit4 on the full pack (real roads, names and points of interest) |
| 11, 12, 14 | gdUnit4 with `Main` (11, 12) and `World` (14) on the test pack; 14 uses temporary packs (`res://maps/tmp-*`) with files removed, clearing `PackFolderOverride` first |
| 17 | Screenshots from `/verify` (the `--player` image and the new `--minimap-corner top-right` one), viewed by Claude Code and the user; argument errors in a `ScreenshotArgs` xUnit test |
| 18 | `/verify`'s export check, extended |
| 19 | `perf-minimap.sh`, run by hand (not part of `/verify`, like the world-edge check) |
| 20 | xUnit and pytest word-list tests, plus the reviewer |
| Readability, label density, colours, size, how the rotation feels | Playtest |

The new gdUnit4 suite(s) need a group in `game/tests/test-groups.json` and path rules for `scripts/hud/MinimapHud.cs`, `Roadman.Core/Minimap/` and the pack's minimap files; the pytest selection in `verify.sh --quick` must include the new pipeline step's files.

## Open questions

None. Resolved 2026-11-08 by the user:

1. The map turns with the player camera's yaw, so up on the minimap matches forward on the screen; the arrow shows the player's facing.
2. The pipeline also writes a top-down background PNG (buildings, water and land use; no roads or labels) in the flat palette; the game rotates it under the live roads, labels and symbols.
3. Round panel.
4. About 220 px diameter at 1080p, scaled with the UI; about 150 m around the player. Fixed for now; zooming out with speed comes with vehicles.
5. All points of interest as symbols, grouped by type: shop, food and drink, public services, parking, other. No outlines.
6. Footpaths and cycleways drawn fainter and thinner than roads.
7. Hidden during the death fall and the death screen. Shown while the debug camera is on, still centred on the player.
8. Symbols only for now; names wait for the business-name decision.
9. Budget 0.5 ms per frame on the full pack, measured like the world edge.
10. Remove "Used by the minimap" from the setting.
11. Finnish names only, like the street label.
12. The edge line with roadblock marks and the darker fill outside are enough; no separate fence symbols.
13. Water comes from the same source as the world edge (MML water areas first, OSM as fallback), and MML's narrow streams (`virtavesikapea`) are drawn as thin lines, 1 to 2 px.
