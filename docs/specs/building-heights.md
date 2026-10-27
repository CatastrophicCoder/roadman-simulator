# Spec: building heights

## Status

Approved

## Goal

Give buildings realistic heights from MML's open laser scanning data 0.5 p, instead of the 2-floor default that 1,840 of Somero's 1,841 buildings get today. The lidar height becomes a new source in the stage 3 height priority with its own provenance value. It works for any map from config (ADR 0002), and laser scanning data 5 p stays forbidden.

## Investigation (2026-10-27)

All requests went through MML's OGC API Processes file service. Output stayed in the session scratchpad, not in `data/raw/`.

### MML Buildings 3D (`3d-rakennukset_karttalehti`): not available

- **Inputs:** `mapSheetInput` (up to 100 sheets), `fileFormatInput` (only `CityGML`) and `levelOfDetailInput` (only `LOD2`).
- **Request 1**, 1:10 000 sheets `L4211B` and `L4212A`: failed with "data not found with user's parameters: CityGML/LOD2/L4211B,L4212A".
- **Request 2**, 1:5 000 sheets `L4211B2` and `L4212A1`: failed with "data not found with user's parameters: CityGML/LOD2/L4211B2,L4212A1".
- **Why:** MML's product description says Buildings 3D currently exists "only for a few sample areas", with nationwide coverage planned as laser scanning continues. Somero isn't covered at either sheet scale.
- `mml_3d` stays a stub. A CityGML reader is out of scope.

### MML laser scanning data 0.5 p (`laserkeilausaineisto_05_karttalehti`)

- **Inputs:** `mapSheetInput` (up to 100 sheets), `fileFormatInput` (`LAZ`) and `dataSetInput` (`05p_2008-2019`, `05p_2020-` or `Uusin` for the latest).
- **Sheet scale:**
  - Files come on 3 × 3 km TM35 1:5 000 sheets (MML product description).
  - A request with the 1:10 000 sheet `L4212A` succeeded but returned "no files found".
- **The map bbox needs two sheets**, both downloaded:

  | Sheet | Size | Scan date | MML project |
  | --- | --- | --- | --- |
  | `L4212A1` | 45 MB | 2024-05-31 | `20240103_Leica_Karkkila_kesa` |
  | `L4211B2` | 38 MB | 2024-05-31 | `20240103_Leica_Karkkila_kesa` |

  - Together: 12.3 M points, about 0.74 points/m², EPSG:3067, heights in N2000.
  - Every one of the 1,841 OSM buildings is covered.
- **Classes in the files:** only 1 (unclassified), 2 (ground) and 3–5 (low, medium and high vegetation). MML's class list for this product has no building class.
  - Inside the footprints, 96% of points are class 5, so roofs show up as "high vegetation".
- **Returns:** 7.0 M single-return and 5.3 M multi-return points.
- **Licence:** CC BY 4.0. MML product name: "Laser scanning data 0,5 p".
- **Not open:** the 5 p product (MML: "cannot be published or transferred as such or at a similar accuracy").

### What each filter changes (all 1,841 Somero buildings)

Heights in this section are roof points minus the median DEM height along the footprint ring. Parameters were varied one at a time.

**Inner buffer** (all non-ground returns, at least 3 points): a larger buffer cuts out walls and eaves but loses small buildings.

| Buffer | Buildings with a value | Median height |
| --- | --- | --- |
| 0 m | 1,825 (99%) | 4.0 m |
| 0.5 m | 1,815 (99%) | 4.1 m |
| 1.0 m | 1,783 (97%) | 4.1 m |
| 1.5 m | 1,698 (92%) | 4.3 m |
| 2.0 m | 1,591 (86%) | 4.4 m |

**Fallback ladder:** trying 1.0 m, then 0.5 m, then 0 m recovers most buildings that are too small for a 1 m buffer.

| Minimum points | Buildings with a value | Buildings using a smaller buffer |
| --- | --- | --- |
| 3 | 1,825 | 42 |
| 5 | 1,819 | 88 |

**Minimum point count:** 3 → 5 → 10 points gives 1,783 → 1,731 → 1,576 buildings with a 1 m buffer and no ladder. With the ladder, 5 points still gives 1,819.

**Tree check:**
- At a 1 m buffer, the spread between the 90th and 10th percentile has a median of 1.3 m, a 90th percentile of 3.7 m and a 99th percentile of 9.9 m.
- 36 buildings have a spread over 8 m, which fits trees over the roof.
- Using only single returns for all buildings changes the median of 25 buildings by more than 0.5 m and 15 by more than 1 m. 23 buildings then fall below 3 points.
- **Gated on spread over 8 m** (minimum 5 points, with the ladder): 22 buildings use single returns, and 16 have too few single returns and get no value. A gate at 6 m catches 52 and 28.

**Low heights:** with minimum 5 points, the ladder and the tree check, 16 buildings have a median under 2 m.
- 5 are under 1 m (−0.49, −0.38, −0.26, 0.08 and 0.98 m, on 31–103 m²). They look like footprints with no building at the scan date, or roof points classed as ground.
- 11 are 1.5–2.0 m, on 13–55 m², which fits small sheds.
- A 2 m minimum would send those sheds to the 6 m default. A 1 m minimum rejects only the first 5.

**High heights:** the tallest median is 16.6 m and no building is over 20 m, so a 40 m maximum changes nothing in Somero.

**90th percentile minus median** (single returns, 1 m buffer): median 0.5 m, 90th percentile 1.2 m. This approximates how far the ridge sits above the mid-roof.

## Behaviour

### Height priority

Each building takes its height from the first source with a valid value:

1. Manual override (not implemented yet; it stays first once it exists)
2. `mml_lidar`
3. `osm_height`
4. `osm_levels` (× 3 m)
5. `default` (2 floors × 3 m)

`mml_3d` stays a stub that never returns a value. Whether it stays in the list (for example below `mml_lidar`) or is removed is up to the implementation plan. `docs/architecture.md` ("Building attribute precedence") and the stage 3 code change to this order.

### Lidar adapter

- **Name and capability:** adapter `mml_lidar` with a new capability (for example `building_heights` or `point_cloud`). The build picks it by capability, never by source name.
- **Download:**
  - TM35 1:5 000 sheets covering the map bbox (`tm35.sheets_covering(..., 5000)`), with `dataSetInput: Uusin` and `fileFormatInput: LAZ`.
  - Options in `map.yaml`: `sheet_scale` (default 5000) and `dataset` (default `Uusin`).
  - Files go to `data/raw/<map-id>/mml/lidar_05p/`, with download-manifest entries like the other MML sources.
- **Allowed process:** only `laserkeilausaineisto_05_karttalehti`. Any other process id starting with `laserkeilausaineisto`, including every 5 p process, is refused (ADR 0005 amendment).
- **Load:**
  - Reads the LAZ files with laspy and lazrs.
  - Keeps only points inside the build area plus a margin, so the test area stays fast.
  - Drops class 2 (ground). If the noise classes 7 (low noise) or 18 (high noise) appear, they are dropped too (ASPRS LAS 1.4 classes). Other classes, such as 15–17 (transmission tower, wire connector, bridge deck), are kept.
- **Config:** `maps/somero/map.yaml` gets an enabled `mml_lidar` source.

### Height from lidar

For each footprint:

1. **Pick points:** non-ground points inside the footprint shrunk by `inner_buffers_m`. Try `[1.0, 0.5, 0.0]` in order, and stop at the first buffer that leaves at least `min_points` (5) points.
2. **Tree check:** if the 90th-minus-10th percentile spread of those points is over `tree_spread_m` (8 m), keep only single returns (`number_of_returns == 1`). If fewer than `min_points` remain, there is no lidar value (reason `tree_cover`).
3. **Roof elevations:** `roof_z_median` and `roof_z_p90` (absolute, N2000) of the selected points.
4. **Height:** `height_m = roof_z_median − base_z`. `base_z` is the lowest terrain under the footprint, as now, so the roof ends at the measured median elevation even on sloping ground.
5. **Limits:** if the median is below `min_height_m` (1.0 m) or above `max_height_m` (40 m), measured as roof elevation minus `base_z`, there is no lidar value (reason `too_low` or `too_high`).
6. **No points** (fewer than `min_points` at every buffer): no lidar value (reason `few_points`).
7. **No value:** the next source in the priority list is used.

`inner_buffers_m`, `min_points`, `tree_spread_m`, `min_height_m` and `max_height_m` are options of the `mml_lidar` source in `map.yaml`, defaulting to the values above.

Expected result for Somero from the investigation: about 1,798 of 1,841 buildings (about 97.7%) get `mml_lidar`. About 43 keep `default`: 22 with too few points, 16 with tree cover and 5 too low. The ground reference differs slightly from the investigation (`base_z` instead of the ring median), so exact counts may shift.

### Provenance

Each building in `buildings.json` and `03-buildings/buildings.geojson` gets `height_source: "mml_lidar"`. Lidar buildings also record:

- `lidar_points`: the number of points used
- `lidar_buffer_m`: the buffer used
- `lidar_returns`: `all` or `single`
- `roof_z_median` and `roof_z_p90`

A building whose lidar value was rejected records `lidar_rejected` with the reason (`few_points`, `tree_cover`, `too_low` or `too_high`), so overrides can target it.

`roof_z_p90` is stored for future pitched roofs and isn't used for geometry yet.

### Reports and credits

- **`03-buildings/report.json`:** the number of buildings per height source, the number rejected per reason, the number per buffer used, and the number using single returns.
- **Manifest credits:** the lidar dataset, only when the build read it. Attribution: "Contains data from the National Land Survey of Finland Laser scanning data 0,5 p, MM/YYYY".
- **`CREDITS.md`:**
  - A row for "Laser scanning data 0,5 p".
  - The existing dataset rows checked against MML's exact product names, for example "Elevation model 2 m", "Orthophotos" and "Buildings 3D". Confirm each name on MML's product description page.
  - An entry for laspy (BSD-2-Clause) and lazrs (MIT) in the pipeline tooling section, or wherever the CREDITS conventions put Python tools.

### Geometry

Flat roofs at `base_z + height_m`. Collision boxes use the same height.

### CI and the test fixture

- **Test fixture:** a small LAZ cut from the Somero data to the test area (300 × 300 m, about 70k points) is committed under `tools/mapgen/tests/fixtures/`.
  - It sits next to a `README` or `NOTICE` file with the MML attribution ("Contains data from the National Land Survey of Finland Laser scanning data 0,5 p, 10/2026") and a CC BY 4.0 note.
  - Tests that need lidar use this fixture and never the network.
- **CI release builds:** download `mml_lidar` along with the other sources. CI isn't set up yet (prompt 7); this spec only records the requirement.

### Docs and decisions

- **ADR 0005 amendment:** laser scanning data 0.5 p is allowed through `laserkeilausaineisto_05_karttalehti` only, and 5 p stays forbidden. Also update the comment in `ensure_allowed_process`.
- **`docs/architecture.md`:** the new priority order and the `mml_lidar` adapter.
- **`tools/mapgen/CLAUDE.md`:** add laspy and lazrs to the libraries. Its rule already says "don't use 5 p".

### Out of scope

- The CityGML reader, the topographic database floor counts, pitched roofs and manual overrides.
- The in-game inspector.

## Acceptance criteria

1. `uv run mapgen download somero --only mml_lidar` downloads the two 1:5 000 sheets that cover the bbox (`L4211B2` and `L4212A1`) with download-manifest entries, and `uv run mapgen build somero` succeeds.
2. The full Somero build gives `mml_lidar` heights for at least 95% of buildings. `03-buildings/report.json` lists the count per source and per rejection reason.
3. The priority order is: override (when present), `mml_lidar`, `osm_height`, `osm_levels`, `default`. A fixture with one building per case shows each source wins exactly where it should.
4. With a sloped DEM fixture, a lidar building's roof top equals `roof_z_median` within 0.01 m, and `height_m = roof_z_median − base_z`.
5. Buffer ladder: a footprint too small for the 1.0 m buffer uses 0.5 m or 0 m, and `lidar_buffer_m` records which.
6. Tree check: a fixture building with canopy points over the roof (spread over 8 m) uses only single returns. It is rejected as `tree_cover` when too few single returns remain.
7. Limits: medians under 1.0 m or over 40 m above `base_z` are rejected with reason `too_low` or `too_high`, and the next source is used. Fewer than 5 points at every buffer gives `few_points`.
8. `roof_z_p90` is stored for every lidar building and is at least `roof_z_median`.
9. Every process id starting with `laserkeilausaineisto` other than `laserkeilausaineisto_05_karttalehti` is refused, both in the adapter options and in the client. A test checks this with a 5 p id.
10. Manifest credits include "Laser scanning data 0,5 p" only when the build read lidar files. The manifest still validates against the schema.
11. No building has a NaN, zero or negative height, and collision boxes span `base_z` to `base_z + height_m`.
12. The pytest suite, including the lidar fixture, runs without network access and in under about 10 s. The committed fixture has MML attribution next to it.
13. `docs/architecture.md`, ADR 0005, `CREDITS.md`, `tools/mapgen/CLAUDE.md` and `maps/somero/map.yaml` are updated as described above.
14. A screenshot of the centre tiles shows buildings of varied heights, checked by hand.

## Test ideas

| Criteria | Layer |
| --- | --- |
| 3, 5, 6, 7, 8 | pytest unit tests on the lidar height function, with synthetic point arrays (no LAZ needed) |
| 4, 11 | pytest with the synthetic sloped DEM in `conftest` and a synthetic LAZ written with laspy in `conftest` |
| 1 (download) | pytest with the stubbed HTTP client, as for the other MML adapters: sheet list, inputs, manifest entries |
| 9 | pytest on `ensure_allowed_process` and the adapter options |
| 10 | pytest on manifest credits and schema validation |
| 2, 12 | pytest build of the test area from the committed Somero lidar fixture. A `rawdata`-marked test runs the full Somero build when raw data is present. Timing is checked in the suite. |
| 13 | reviewer |
| 14 | screenshot (Blender render, or the headless screenshot step once it exists) |

## Open questions

None. All questions were decided on 2026-10-27:

- **Scope:** lidar only. The topographic floor counts are dropped.
- **Order:** override > `mml_lidar` > OSM height > OSM levels > default.
- **MML 3D:** a second request was made and found no coverage; no reader.
- **Lidar settings:** median for the flat-roof height, with the 90th percentile stored. The filter values above were proposed from the measured sample.
- **ADR 0005:** amended to allow only 0.5 p.
- **Dependencies:** laspy and lazrs approved.
- **Config and CI:** `mml_lidar` is enabled by default and downloaded in CI release builds. Tests use a committed test-area fixture with attribution.
- **Licences:** lazrs is MIT and laspy is BSD-2-Clause, from the installed package metadata of lazrs 0.8.2 and laspy 2.7.0. MML's exact dataset names go in `CREDITS.md`.
