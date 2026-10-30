# Spec: MML-only buildings

## Status

Approved

## Goal

Add the buildings that exist in the MML Topographic Database but have no OSM counterpart (168 records in the full Somero build) to the map pack, so Somero has all its real buildings. The pipeline builds them from the local MML footprints with the parts that overlap OSM buildings cut away, gives them lidar heights, categories by the existing place information order, collision and provenance like OSM buildings, and uses the lidar as a reality check that leaves out records it clearly contradicts. Covers the deferral in ADR 0013 ("MML-only buildings aren't added (a later spec)"), Phase 3 stage 3 (buildings), "Place information" (building category) and "Map packs and building enhancement" (attribute sources, MML-keyed overrides) in `docs/project-outline.md`.

Out of scope: replacing or reshaping OSM footprints with MML geometry, MML road classes or water areas, pitched roofs, appearance overrides and `mapgen enhance`, and any data download. Only local data in `tools/mapgen/data/raw/somero/` is used; the Topographic Database GPKG is already there and in the pinned `data-somero-2026-10-30` snapshot, so no new snapshot is needed.

## Data (2026-10-30)

Counted from local data only: `tools/mapgen/data/raw/somero/mml/topographic/maastotietokanta_kaikki.gpkg`, the LAZ sheets in `mml/lidar_05p/` (scanned 2024-05-31), the OSM file through the pipeline's `run_load`, and the last full build in `tools/mapgen/build/somero/full/`. Throwaway scripts in the session scratchpad reused the pipeline's `read_rakennus`, `read_points`, `lidar_height` with Somero's `mml_lidar` options, `Heightfield`, and the category tables, and applied the rules in "Behaviour". The build report is authoritative; these numbers give the order of magnitude.

### Records

- **Count:** 168 `rakennus` records in the build area match no OSM building (the build report's `unmatched_records`; the place information spec counted 164 against the WGS84 bbox, and the build area is the tile union rounded outward). All have their centroid inside the build area, none crosses its edge, none is a multipolygon.
- **Columns of `rakennus`:** `mtk_id`, `sijaintitarkkuus`, `aineistolahde`, `alkupvm` (record start date: 2025-03-25 for 1,921 of 1,951 records, later dates for 30), `kohderyhma`, `kohdeluokka`, `korkeustarkkuus`, `kayttotarkoitus`, `kerrosluku`, `pohjankorkeus`, `korkeusarvo` (always empty), `sijainti_piste`. No name and no address column.
- **Feature class:** 42261 (Muu rakennus, 1–2 floors) 100, 42211 (Asuinrakennus, 1–2 floors) 62, 42221 (Liike- tai julkinen rakennus, 1–2 floors) 5, 42260 (Muu rakennus, floors undefined) 1.
- **Overlap with OSM footprints** (share of the MML footprint covered by OSM buildings): none 124, over 0 and under 50% 14, 50% or more 30. The last group is mostly where MML draws several buildings and OSM one (for example a house and its attached garage).
- **Size:** 53 under 40 m², 7 under 10 m², 4 under 1 m² (0.38, 0.43, 0.58 and 0.86 m², none with a lidar point; the 0.58 m² one is class 42211, residential).
- **Roads:** 1 record overlaps a stage 4 road or path surface, by 1.1 m².
- **Points of interest:** no POI point lies in an MML-only footprint; 2 footprint centroids lie in POI areas (`338992151` in a `retail` area, `339078083` in a `religious` area).

### The rules applied to Somero

| Outcome | Records |
| --- | --- |
| Added whole (no OSM overlap, at least 1 m²) | 120 |
| Added as a cut piece | 20 |
| Discarded: every remaining piece is a sliver | 23 |
| Left out: under 1 m² | 4 |
| Left out: lidar contradicts it (a cut piece) | 1 |
| **Added** | **140** |

- No cut record keeps more than one piece; 1 record (`2226370717`) leaves two pieces, one of them a sliver.
- The two sliver width tests considered (inward shrink by 0.75 m leaves nothing, or the minimum rotated rectangle's short side under 1.5 m) disagree on 1 piece in Somero. Neither catches an L-shaped strip 1.4 m wide (a circle of radius 0.82 m fits in its corner), so the spec uses a morphological opening instead (see "Behaviour", step 4; changed during implementation on 2026-10-30). The table above was computed with the shrink test; the implementation report gives the counts with the opening rule.
- Lidar on the 140 added footprints: 133 get a height, 5 are `few_points`, 2 `tree_cover`; those 7 get the default.
- **Largest cut pieces** (record area → kept area, the OSM buildings cut away), for checking in QGIS:

  | Record | Class | Record area | Kept | OSM buildings |
  | --- | --- | --- | --- | --- |
  | `mml:rakennus/338991899` | 42261 | 304.8 m² | 208.4 m² | `osm:way/464653041` |
  | `mml:rakennus/3000144352` | 42261 | 165.7 m² | 164.6 m² | `osm:way/448515971` |
  | `mml:rakennus/3000145173` | 42211 | 159.9 m² | 142.3 m² (removed by the lidar rule) | `osm:way/464653919` |
  | `mml:rakennus/2227776985` | 42261 | 2,679.8 m² | 136.7 m² | `osm:way/448515952` |
  | `mml:rakennus/2226370717` | 42221 | 500.3 m² | 110.3 m² | `osm:way/425249807` |
  | `mml:rakennus/339077639` | 42211 | 175.6 m² | 102.9 m² | `osm:way/1555944295`, `osm:way/426723955` |
  | `mml:rakennus/338992907` | 42211 | 175.0 m² | 81.6 m² | `osm:way/425249733` |
  | `mml:rakennus/339002621` | 42211 | 144.4 m² | 80.1 m² | `osm:way/464652853` |
  | `mml:rakennus/2225284551` | 42261 | 80.5 m² | 77.6 m² | `osm:way/464652319` |
  | `mml:rakennus/2227777058` | 42261 | 71.9 m² | 69.5 m² | `osm:way/464653151` |

- **Lidar contradiction rule** (at least 10 points inside the footprint, at least 90% of them within 1.5 m of the ground): removes 1 record in Somero.
  - Removed: `3000145173` (the 160 m² record, cut to 142.3 m²): 17 points inside, 16 of them (94.1%) within 1.5 m of the ground; the highest is 1.96 m above it (the next highest 1.40, 1.22, 1.17 m, the lowest 0.38 m). Without the rule it would get the default height (`few_points`).
  - Kept: `339041253` (89.3 m², whole): 6 points inside, all within 0.27 m of the ground, but fewer than 10 points, so the rule doesn't apply; it gets the default height (`few_points`).
  - No other added footprint with at least 10 points has even 75% of its points within 1.5 m of the ground. A first version of the rule (no point more than 1.5 m above the ground) removed no record, because of `3000145173`'s single 1.96 m point.
- **Test area** (300 m square): of its 4 MML-only records, `339002621` is added as a cut piece (144.4 → 80.1 m²) and the other 3 (`339002477`, `339002489`, `2227061378`, 99.9–100% covered) are discarded as slivers.

### Lidar background

The point cloud has about 0.5 points per m² and no building class: non-ground points are class 1, 3, 4 or 5. Before cutting, 157 of the 168 records had a lidar height (OSM buildings: 1,799 of 1,841); their roof point spread (p90 − median: median 0.50 m, p99 4.13 m) is like that of OSM buildings (0.57 m, 5.94 m). Footprints under about 10 m² expect fewer than 5 points, so the lidar can't confirm or rule them out. The lidar is from 2024-05-31; whether a record's `alkupvm` marks construction isn't stated in the local data, so the lidar can't tell "demolished before the scan" from "built after it".

## Behaviour

### Which records become buildings

For each record of the map's `building_use` source (Somero: `mml_topographic`, table `rakennus`), in `mtk_id` order (`record_number`):

1. **Matched:** an OSM building matched it (ADR 0013 matching, unchanged, runs first). Not added.
2. **Outside:** its centroid is outside the build area. Not added, as for OSM footprints. A record whose kept footprint (after step 4) has its centroid outside the build area is `outside` too, because every building belongs to the tile containing its centroid (added during implementation, 2026-10-30; no Somero record is affected).
3. **Too small:** its footprint is under 1 m². Not added.
4. **Cut:** the footprint minus the union of the OSM building footprints it overlaps (shared area over 0) splits into pieces (the polygons of the difference). A piece is a **sliver**, and is discarded, when its area is under 4 m², or when less than 50% of its area survives a morphological opening by 0.75 m (shrink inward by 0.75 m, then grow outward by 0.75 m: `buffer(-0.75).buffer(0.75)`). The opening removes every part narrower than 1.5 m, straight or bent (an L- or T-shaped strip keeps only a small blob at its corner, whatever the length of its arms), and only rounds the corners of a wider piece (a rectangle loses about 0.48 m²). A record with no OSM overlap is one whole piece and isn't subject to the sliver test. A record whose pieces are all slivers is discarded (`slivers`).
5. **Lidar contradiction:** the kept footprint has at least 10 lidar points inside it (all classes except the noise classes 7 and 18; ground included), and at least 90% of them are within 1.5 m of the ground: |point z − `Heightfield.sample` at the point's x/y| ≤ 1.5 m. Not added (`lidar_contradicts`).
6. Otherwise the record becomes one building.

The kept pieces of one record form one building: one id, one footprint (a polygon, or a multipolygon when several pieces are kept), one height, one collision box per part. The 0.75 m opening radius, 50% surviving share, 4 m², 1 m², 10 point, 90% and 1.5 m height limits are pipeline constants, listed in the report.

Added whenever the map's `building_use` source is enabled and loaded; there is no separate switch. A map with the source disabled adds nothing and the log says so; that isn't a failure.

### Geometry

- The footprint is the kept geometry from step 4 (the record's repaired polygon when whole), in local coordinates.
- Base at `Heightfield.min_under(footprint)`, flat roof, extrusion with holes, and one collision box (minimum rotated rectangle) per part, exactly as for OSM buildings.
- A cut footprint shares at most its boundary with OSM footprints; its area of overlap with any OSM building is under 0.01 m² (floating point).

### Height

- Lidar first, on the kept footprint, with the existing rule and the source's options unchanged. The lidar loader reads points around the added footprints as well as the OSM ones.
- Without a lidar value: `default` (2 floors × 3 m).
- `height_source`, `lidar_*`, `roof_z_*` and `lidar_rejected` as for OSM buildings.

### Ids and nodes

- `id` is the record id, `mml:rakennus/<mtk_id>`, also for a cut building. The id prefix is the only mark of an MML-only building; `buildings.json` stays at schema version 2.
- `node` is `bld_mml_rakennus_<mtk_id>` (`node_name`), collision nodes `bld_mml_rakennus_<mtk_id>_c<k>-colonly`. The node rules and pack checks of ADR 0006 apply unchanged.

### Place information

- **Category:** the existing place information order (`docs/specs/place-information.md`), first step with a value wins:
  1. `override`: an override keyed by the record's MML id.
  2. `osm_building`: never has a value (`category_raw.osm_building` is `null`).
  3. `osm_poi`: POI tags on POIs on or in the building.
  4. `osm_poi_area`: the POI areas the building is inside.
  5. `mml_use_class`: the record's `kohdeluokka` with the adapter's existing rules; the 40 m² size is the kept footprint's area.
  6. `osm_landuse`.
  7. The 4222x fallback to `commercial`.
  8. `unknown`.

  All raw values kept in `category_raw` as for OSM buildings.
- **Names:** `override`, then `osm_poi` (exactly one named POI on or in it), else all `null`. `name_source` is never `osm`. `rakennus` has no name column.
- **Address:** `{street: null, housenumber: null}`, `address_source: null`.
- **MML fields:** `mml_building` is the building's own id and `mml_overlap` is `1.0` (the record covers the whole kept footprint).
- **POI links** in both directions by the existing rules, so a POI node on a wall between an OSM and an MML-only footprint goes to the smaller one.
- **Overrides:** "OSM key over MML key" doesn't arise for these buildings. An MML-keyed override that reaches an added building is applied and listed in its `overrides`, and isn't reported as unmatched.

### Report

`03-buildings/report.json` gains a block `mml_only`:

- the limits used (1 m², 4 m², 0.75 m opening radius with 50% surviving share, 10 points, 90% within 1.5 m of the ground);
- records considered; added whole; added cut; not added per reason (`outside`, `too_small`, `slivers`, `lidar_contradicts`);
- `cut`: one entry per cut record (id, record area, kept area, pieces kept, pieces discarded, the OSM building ids cut away, and for each kept piece its area, the share of it surviving the opening of step 4, and its compactness 4π·area/perimeter², 1.0 for a circle and near 0 for a thin strip, so remnant strips stand out), sorted by kept area, largest first;
- `lidar_contradicts`: one entry per removed record (id, footprint area, points inside, share within 1.5 m of the ground, highest point above the ground);
- added per feature class, per height source and lidar rejection reason, per category and category source;
- added buildings with and without a lidar height, and for each without: id, kept area, points inside, share within 1.5 m of the ground, highest point above the ground, rejection reason;
- ids of added buildings that overlap a road or path surface or a bridge deck (listed, not removed).

The existing counts (`building_count`, height, category, name and address counts) include the added buildings. `unmatched_records` keeps its meaning (records no OSM building matched).

`03-buildings/buildings.geojson` contains the added buildings with their kept footprints, so they can be checked in QGIS next to the OSM ones.

### Pack checks

- No two buildings share an id or node name (as now).
- Every building with an `mml:` id has `mml_building` equal to its id and `mml_overlap` `1.0`.
- No `mml:` building's footprint overlaps an `osm:` building's footprint by 0.01 m² or more. This needs the footprints, which the checks read from the stage output.

### Credits and data

- No new source, download or snapshot. The Topographic Database file is read and credited whenever `building_use` is loaded (ADR 0006), so the manifest credits don't change.
- `CREDITS.md` states that the Topographic Database also supplies building footprints.

### Game

- No game code is expected to change. The added buildings load through `MapLoader` like OSM ones, their bodies join `map_buildings`, and `MapLoader.BuildingOf` resolves them by node name.
- The place inspector shows them with their `mml:rakennus/...` id, category, `category_source`, `mml_building`, height and `height_source`.

### Docs

- A new ADR 0014 records the rules (cutting, slivers, minimum, lidar contradiction, attribute order, id prefix as provenance); ADR 0013's line "MML-only buildings aren't added" gets a pointer to it.
- `docs/architecture.md`: stage 3 (MML-only buildings), the report block, the pack check, and the note that an `mml:` id in `buildings.json` marks an MML footprint.

### Edge cases

- A record matched by several OSM buildings: matched, never added.
- A record whose centroid lies outside the build area: not added, even if it overlaps the area.
- An invalid MML polygon: repaired or skipped by the reader as now; a skipped record is never added.
- A record whose remaining part splits into several pieces: slivers dropped, the rest one multipart building.
- A record whose difference with OSM leaves lines or points only: discarded as `slivers`.
- A record touching an OSM footprint only along an edge (shared area 0): added whole.
- Two MML-only records sharing a wall: both added, each with its own collision box.
- An MML-only footprint overlapping a road surface: added and listed in the report.
- A record with an unmapped or missing feature class: step 5 has no value, counted as unmapped as now.
- A build area where no record qualifies: the build succeeds and the report says 0 added.

## Acceptance criteria

1. On synthetic snippets (OSM footprints, a `rakennus` GPKG written with `sqlite3`, DEM and point arrays): a record with no OSM building near it is added whole; a matched record isn't added; one with its centroid outside the build area isn't; one of 0.9 m² is `too_small` and one of 1.1 m² is added.
2. Cutting: a record half covered by an OSM building is added with the uncovered half as its footprint; a remaining piece of 3.9 m² is a sliver and one of 4.1 m² (at least 1.5 m wide) is kept; a 1.4 m wide strip of 10 m² is a sliver and a 1.6 m wide one is kept; an L-shaped strip 1.4 m wide is a sliver, also with long arms (each 20 m), and one 1.6 m wide is kept; a compact annex (for example 3 × 2.5 m) is kept; a record leaving two kept pieces becomes one building with two parts and two collision boxes; a record leaving only slivers is discarded as `slivers`; a record touching an OSM footprint only along an edge is added whole.
3. Lidar contradiction: a footprint with 10 points of which 9 are within 1.5 m of the ground (90%) is removed as `lidar_contradicts`; with 10 points of which 8 are within (80%) it is added; with 9 points all within it is added (too few points). Heights are measured against `Heightfield.sample` on a sloped DEM, so ground points on a slope count as within 1.5 m of the ground.
4. Height: an added building with enough roof points gets `mml_lidar` with the same values as `lidar_height` on its kept footprint; one without gets `default` (6 m) and its `lidar_rejected` reason.
5. Category on snippets, one building per step: an MML-keyed override; a POI node inside (`osm_poi`); inside a POI area (`osm_poi_area`); 4221x (`mml_use_class`); 4226x with a kept footprint of 39 m² (`garage_or_shed`) and of 41 m² (on to land use), including a record over 40 m² cut below 40 m²; 4222x with and without land use; nothing (`unknown`). `category_raw.osm_building` is always `null`. Name fields come from the override or one named POI, else `null`; `address` is `null`; `mml_building` is the own id with `mml_overlap` `1.0`.
6. POI links: a POI node inside an added footprint and a POI area covering its centroid are linked in both directions in `buildings.json` and `pois.json`; an MML-keyed override that applies is listed in `overrides` and not in the report's unmatched list.
7. The report holds the `mml_only` block; on snippets every count and entry is exact, including the `cut` list sorted by kept area and the `lidar_contradicts` entries.
8. The pack checks fail a pack where an `mml:` building overlaps an `osm:` building by 0.01 m² or more, or has `mml_building` other than its id; a failed check fails the build and leaves the previous pack.
9. A build with the `building_use` source disabled adds no MML building, succeeds and logs why.
10. `buildings.json` stays at schema version 2 and validates against the unchanged committed schema.
11. Two builds from the same raw data write byte-identical `buildings.json` and tiles.
12. The test-area build from the committed fixture adds `mml:rakennus/339002621` as a cut building and reports the other 3 test-area records as `slivers`.
13. The full Somero build (local data only; a test marked to skip without raw data) reports its counts; the implementation report compares them with "The rules applied to Somero" (140 added: 120 whole, 20 cut; 23 slivers; 4 too small; 1 removed by the lidar rule, `mml:rakennus/3000145173`; `339041253` kept), lists the largest cut pieces, and explains any difference.
14. `Roadman.Core`: `BuildingsReader` and `PlaceIndex` accept a file with an `mml:rakennus/<n>` building and resolve it by id and by node name.
15. gdUnit4: after the test-area pack loads, the body of `mml:rakennus/339002621` is in `map_buildings`, `MapLoader.BuildingOf` returns its entry, and an inspector ray at it shows its id and `category_source`.
16. `/verify` passes. A screenshot over a group of added buildings (a camera preset, or `--presets` with a temporary file) shows them standing on the terrain next to their OSM neighbours without overlapping walls, and an `--inspect` screenshot shows the panel on one. Both are looked at and described.
17. ADR 0014, the ADR 0013 pointer, `docs/architecture.md` and `CREDITS.md` are updated.

## Test ideas

| Criterion | Layer |
| --- | --- |
| 1, 2, 3, 4, 5, 6, 7, 9 | pytest unit tests on snippets: synthetic footprints, a `rakennus` GPKG written with `sqlite3`, a sloped synthetic DEM and point arrays (roofs, bare ground, a canopy) |
| 8 | pytest calling `build/pack_checks.py` on corrupted staged packs, plus the build-fails test |
| 10, 11, 12 | pytest: schema validation, a double build and the test-area build from the committed fixture |
| 13 | pytest marked to run only when `data/raw/somero/` has the topographic and lidar files (skipped in CI), plus the counts in the implementation summary |
| 14 | `Roadman.Core` xUnit |
| 15 | gdUnit4 on the test-area pack |
| 16 | `/verify` screenshots, described in the report; QGIS check of the largest cut pieces by the user; playtest: fly over the plots that were empty before |
| 17 | reviewer |

## Open questions

None. All questions were answered on 2026-10-30:

- **Overlap with OSM:** every record is added with the part that overlaps OSM buildings cut away; pieces under 4 m² or narrower than 1.5 m are discarded as slivers (narrowness by the opening rule of step 4); remaining pieces get their own lidar height.
- **Minimum footprint:** 1 m².
- **Records without roof points:** left out only when the lidar clearly contradicts them: at least 10 points inside the footprint and at least 90% of them within 1.5 m of the ground (changed from "none more than 1.5 m above the ground", which removed no record). The others get their lidar height or the default. In Somero this removes `mml:rakennus/3000145173` and keeps `339041253` (6 points).
- **Provenance:** the `mml:` id prefix only; `buildings.json` stays at version 2.
- **Switch:** none; added whenever the map's `building_use` source is enabled.
- **Category order:** the full existing order (override, POIs on or in the building, POI areas, MML use class, land use, unknown); the specific OSM building value step has no value.
- **Interpretations made while writing** (from the answers, for the user to check): "narrower than 1.5 m" was first the inward-shrink test and became the opening rule during implementation (2026-10-30), because the shrink test keeps a 1.4 m wide L-shaped strip; the kept pieces of one record form one building, so ids keep the `mml:rakennus/<mtk_id>` form; "within 1.5 m of the ground" is the absolute difference from the DEM at each point (counting only points above the ground gives the same result in Somero), with ground points counted among the 10.
