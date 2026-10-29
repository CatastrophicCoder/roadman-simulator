# Spec: place information

## Status

Approved

## Goal

Bake all place information into the map pack, so the game can always tell where the player is: street names and road numbers, building categories, names and addresses, and points of interest. The pipeline resolves each building's category from OSM, the MML Topographic Database and OSM land use, keeps the raw values and their sources, and writes a new `pois.json`. The game shows the current street name in a HUD label, and a debug inspector shows what the pack knows about the building or point of interest under the crosshair. Covers "Place information" in `docs/project-outline.md`, the category, name and address part of Phase 3 stage 3, and the points of interest of stage 8.

Out of scope: the rest of stage 8 (spawn area, parking spots, `markers.json`), minimap street labels (no minimap yet), how business names are displayed in the game (a separate decision on the legal checklist), the `mapgen enhance` command and appearance overrides (this spec defines only category and name overrides), adding the buildings that exist only in MML (the next spec), Swedish names on screen, and interiors.

## Data (2026-10-29)

Counted from local raw data only. The OSM file is `tools/mapgen/data/raw/somero/osm/somero.osm`. The MML file is `tools/mapgen/data/raw/somero/mml/topographic/maastotietokanta_kaikki.gpkg`, downloaded once on 2026-10-29 with the user's explicit permission (`mapgen download somero --only mml_topographic`, 10.2 MB, attribution "Contains data from the National Land Survey of Finland Topographic Database, 10/2026", CC BY 4.0).

### Roads (498 `highway` ways)

| Tag | Ways | Notes |
| --- | --- | --- |
| `name` | 189 | 132 distinct names. Named by class: residential 133, primary 25, tertiary 15, living_street 6, unclassified 5, cycleway 3, service 1, pedestrian 1 |
| `name:sv` | 0 | |
| `ref` | 34 | `52` (26), `2810` (6), `2802` (2); 32 of them also have a `name` |

309 ways have no name, mostly `service` (155) and `cycleway` (123). The road graph already carries `name`, `name_sv` and `ref` on every edge (ADR 0012).

### Buildings (1,841 ways, no relations)

| `building` value | Count | | `building` value | Count |
| --- | --- | --- | --- | --- |
| `yes` | 1,794 | | `industrial` | 3 |
| `residential` | 10 | | `detached` | 3 |
| `commercial` | 6 | | `retail` | 2 |
| `roof` | 6 | | `church` | 2 |
| `house` | 5 | | `farm_auxiliary` | 2 |
| `school` | 3 | | `garage` | 2 |
| `carport` | 1 | | `apartments` | 1 |
| `supermarket` | 1 | | | |

- 47 buildings (2.6%) have a specific value; 97.4% are `yes`.
- `addr:street`: 930, `addr:housenumber`: 930, always together (116 distinct streets). House numbers: 867 digits only, 56 with a letter, 7 ranges (`-`). 13 also have `addr:postcode` and `addr:city`.
- `name`: 13 (6 on `yes`, 4 on `roof`, 1 each on `supermarket`, `retail`, `church`). `name:sv`: 0.
- POI tags on building ways: 11 (`amenity=fuel` on 4 `roof` canopies, `amenity` `fire_station`, `place_of_worship` and `library`, `shop` `supermarket` (2), `clothes` and `yes`, `leisure=sports_centre`).

### Points of interest

54 features have one of `shop`, `amenity`, `office`, `tourism`, `leisure` (12 nodes, 42 ways, no relations); 29 are named, none has `name:sv`.

| Key | Named | Named values | Unnamed |
| --- | --- | --- | --- |
| `amenity` | 16 | fuel 6, school 3, cafe, pharmacy, cinema, place_of_worship, restaurant, library, marketplace | 19 (parking 16, grave_yard 2, fire_station 1) |
| `shop` | 6 | supermarket 3, gift, convenience, clothes | 1 (`shop=yes`) |
| `leisure` | 5 | sports_centre 2, park 2, playground | 5 (pitch 2, track, ice_rink, park) |
| `tourism` | 2 | museum, artwork | 0 |
| `office` | 0 | | 0 |

- By element: 12 named nodes and 17 named ways (10 of them building ways).
- 10 of the 12 named POI nodes lie inside a building footprint, at most one per building; the other 2 (`tourism` museum and artwork) are not within 5 m of any building. 10 POI nodes carry an address.
- POI areas that aren't buildings contain buildings: an `amenity=school` area (4 buildings), a `leisure=park` (5), a `leisure=playground` (1), an `amenity=grave_yard` (1).
- Other named nodes outside these keys: 18 `highway=bus_stop`, 4 `place`, 1 `healthcare`, 1 with only `name` and `short_name`.

### Land use (41 closed ways, no relations)

`residential` 17, `farmland` 12, `industrial` 7, `retail` 4, `meadow` 1. By footprint centroid, 1,597 buildings lie in `residential`, 80 in `industrial`, 29 in `retail`, 3 in `farmland`, 1 in both `industrial` and `residential`, and 131 in none.

### MML Topographic Database buildings

The GPKG has 128 tables, 44 with features. Table `rakennus` holds 1,951 building polygons (EPSG:3067), 1,947 with their centroid in the bbox, each with `mtk_id`, `kayttotarkoitus` (use class), `kohdeluokka` (feature class) and `kerrosluku` (floor class).

| `kayttotarkoitus` | Count | `kohdeluokka` | MML feature class |
| --- | --- | --- | --- |
| 1 | 972 | 42211 (946), 42212 (26) | Asuinrakennus (residential), 1–2 / 3–n floors |
| 6 | 892 | 42261 (885), 42260 (5), 42262 (2) | Muu rakennus (other building), 1–2 / ? / 3–n floors |
| 2 | 82 | 42221 (80), 42222 (2) | Liike- tai julkinen rakennus (commercial or public), 1–2 / 3–n floors |
| 3 | 3 | 42231 (3) | Lomarakennus (holiday home), 1–2 floors |
| 8 | 1 | 42270 (1) | Kirkko (church) |
| 5 | 1 | 42251 (1) | Kirkollinen rakennus (church-related building), 1–2 floors |

- **Verified** against MML's "Maanmittauslaitoksen maastotietokohteet" (updated 27.5.2025, linked from MML's Topographic Database product description; fetched 2026-10-29, documentation only). It names every area feature class: 42210–42212 Asuinrakennus, 42220–42222 Liike- tai julkinen rakennus, 42230–42232 Lomarakennus, 42240–42242 Teollinen rakennus, 42250–42252 Kirkollinen rakennus, 42260–42262 Muu rakennus, and 42270 Kirkko. The last digit is the floor class: 0 undefined, 1 1–2 floors, 2 3–n floors.
- The document gives no table for the integer `kayttotarkoitus` column. In Somero's data each value pairs with exactly one feature-class group (counts above), so the pipeline classifies by `kohdeluokka`, which is documented, and keeps `kayttotarkoitus` as a raw value.
- Per the document, "Muu rakennus" includes saunas, outbuildings and garages of homes, storage buildings, animal and farm buildings, non-winterised holiday cabins, equipment stores and energy plants. "Liike- tai julkinen rakennus" includes shops, offices, schools, public administration, transport buildings (including service stations and repair shops) and fire stations. Mixed-use buildings take the class of their main use.
- Class 4 (Teollinen rakennus, industrial) doesn't occur, although OSM has industrial buildings and land use. The GPKG's QGIS styles carry no code labels.

### Matching MML to OSM

Rule tested: an OSM building matches the MML building that overlaps it most, when that one covers at least half of the OSM footprint (the rule `docs/specs/building-heights.md` set for MML 3D buildings).

- Of 1,840 OSM buildings (closed ways, centroid in the bbox), 1,814 overlap an MML building, 1,795 (97.6%) match, and 1,239 are covered at least 80%.
- Intersection over union of the best match: median 0.79 (p10 0.62, p90 0.93). Centroid distance of matches: median 0.57 m, p90 2.16 m, max 30.2 m.
- 12 MML buildings are matched by 2 OSM buildings each (24 OSM buildings).
- 45 OSM buildings don't match (44 `yes`, 1 `house`; median 61 m², against 131 m² for matched ones); 26 of them overlap nothing.
- 164 MML buildings match no OSM building (median 75 m²; class 6: 98, 1: 61, 2: 5).
- Matched `yes` buildings (1,749) by use class: 1 899, 6 783, 2 63, 3 3, 8 1.
- Specific OSM values against MML classes agree where both are informative (`residential`/`house`/`detached`/`apartments` with 1, `commercial`/`retail`/`supermarket`/`school` with 2, `church` with 8 and 5). `industrial` (3), `garage` (2), `farm_auxiliary` (2), `carport` and 2 `commercial` are class 6.

### Test area (300 m square around the origin)

60 OSM buildings, all `building=yes`, 32 with an address, none named; 56 match an MML building (MML in the area: class 1 30, class 6 28, class 2 2). One POI: a named `shop=convenience` node. Land use: 2 `residential` areas and 1 `retail` area touch it. 31 highway ways touch it, 20 named; refs `52` (9) and `2802` (1).

### Current state

- Enabling `mml_topographic` in `maps/somero/map.yaml` (done for the download) makes `tests/test_snapshot.py::test_fixture_snapshot_builds_the_same_credits` fail, because the committed fixture has no topographic file (596 other tests pass). `mapgen snapshot fetch` against the pinned `data-somero-2026-10-29` snapshot also fails now ("the snapshot has no files of source(s) mml_topographic"), so a release tag fails until a new snapshot is published and pinned.
- The `mml_topographic` adapter can download but has no `load` and no capability.
- `buildings.json` has no pydantic model or committed schema. `Roadman.Core` has no reader for it. The game has no HUD, player or crosshair; the only camera is `DebugCamera` (W, A, S, D, Q, E, Shift, right mouse to look). Debug keys in use: F2, G.
- `CREDITS.md` lists the Topographic Database as "TODO: not downloaded yet".


### Expected categories (preview, 2026-10-29)

A throwaway script applied the rules in "Building category" below to Somero's full bbox, before any pipeline code exists. The build report is authoritative; this preview gives the order of magnitude.

| Category | Buildings | | Source | Buildings |
| --- | --- | --- | --- | --- |
| `residential` | 1,415 | | `mml_use_class` | 1,155 |
| `garage_or_shed` | 243 | | `osm_landuse` | 586 |
| `industrial` | 76 | | `osm_building` | 41 |
| `unknown` | 43 | | `osm_poi` | 15 |
| `commercial` | 26 | | `osm_poi_area` | 0 |
| `retail` | 22 | | none (`unknown`) | 43 |
| `agricultural` | 5 | | | |
| `public` | 4 | | | |
| `religious` | 3 | | | |
| `education` | 3 | | | |

Of 1,840 buildings: class 6 under 40 m² gave `garage_or_shed` for 240, and class 6 of 40 m² or more (541) went on to land use. Class 2 (57) went on to land use too; 13 of those had no land use value and became `commercial`. `osm_poi_area` gave nothing in Somero: the buildings in the school ground already have `building=school`, and parks and playgrounds map to no value.

## Behaviour

### Name fields

Buildings and points of interest use the name format the roads spec defined ("Name fields" in `docs/specs/roads-and-road-graph.md`, ADR 0012): `name`, `name_sv` and `ref` from the OSM tags `name`, `name:sv` and `ref`, always present, string or `null`, trimmed and NFC-normalised, otherwise exactly as tagged, never split, translated or shortened, written as UTF-8 with unescaped non-ASCII. The implementation reuses the road code's function; there is no second implementation. The address fields `street` and `housenumber` follow the same value rules (from `addr:street` and `addr:housenumber`). The pack keeps `name_sv` everywhere; the game shows Finnish names only for now.

### Sources and loading

- **OSM** (`osm_vector`, no new download): the OSM adapter also loads, from the same parse:
  - points of interest: nodes, closed ways and multipolygon relations with a `shop`, `amenity`, `office`, `tourism` or `leisure` tag, named or not;
  - land use areas: closed ways and multipolygon relations with a `landuse` tag, rings built and repaired as for buildings; a relation that can't be closed is left out and listed in the report (ADR 0005);
  - the `building`, `addr:street`, `addr:housenumber`, `name`, `name:sv` and `ref` tags of each building footprint, and its own POI tags.
- **MML Topographic Database** (`mml_topographic`): the adapter declares a new capability `building_use` and gets a `load`:
  - It reads table `rakennus` from the GPKG files in `data/raw/<map-id>/mml/topographic/` with the standard library `sqlite3` and shapely's WKB reader (GeoPackage binary header skipped). No new dependency; if that turns out not to work, the implementation stops and asks.
  - Each record: `mml:rakennus/<mtk_id>`, the polygon, `kohdeluokka` and `kayttotarkoitus`. Polygons are reprojected if needed and moved to local coordinates like footprints. Records whose polygon doesn't intersect the build area are dropped.
  - The feature-class table lives in the adapter, because the codes are MML's (ADR 0002: country-specific logic only inside adapters). See "Building category" step 5.
  - An enabled `building_use` source without a GPKG fails the build with the download hint, like the DEM. A GPKG without a `rakennus` table fails too. At most one source per capability, as for the others.
  - The read file is credited like every other read file (ADR 0006); the manifest's credits gain the Topographic Database entry. `download --release`, `build --release` and `snapshot create` include the source, since it isn't `debug_only`.

### Building overrides

A minimal, optional override file, so wrong categories and names found in playtests can be fixed without touching the raw data: `maps/<map-id>/overrides/buildings.json`.

```json
{
  "schema_version": 1,
  "buildings": {
    "osm:way/123": {"category": "garage_or_shed", "source": "playtest 2026-11-02", "licence": "own work"},
    "mml:rakennus/456": {"name": "Example", "name_sv": null, "ref": null, "source": "Mapillary image 123456", "licence": "CC BY-SA 4.0", "attribution": "Example contributor, Mapillary", "retrieved_at": "2026-11-02"}
  }
}
```

- Keys are building ids (`osm:way/<id>`, `osm:relation/<id>`) or MML ids (`mml:rakennus/<mtk_id>`). An MML key applies to every OSM building matched to that MML building. When both keys reach one building, the OSM key wins, field by field.
- Fields: `category` (one of the fixed set), and the name fields `name`, `name_sv`, `ref`. An override that sets any name field sets all three (missing ones become `null`), so names are never mixed between sources. `source` and `licence` are required strings, as every override records its source and licence (`docs/architecture.md`). At least one of `category` or a name field is required.
- Own work and third-party data: the licence value `own work` marks the override as the project's own work. Any other licence marks third-party data: the entry then also requires `attribution` (non-empty string) and `retrieved_at` (`YYYY-MM-DD`), and the licence must be one `mapgen notice` knows a URL for (`CC BY 4.0`, `ODbL 1.0`, `CC BY-SA 4.0`), or the file fails validation. `attribution` and `retrieved_at` are not allowed on an `own work` entry.
- A pydantic model with a committed `building-overrides-v1.schema.json`; unknown keys, unknown categories and wrong types are errors. `mapgen build` validates the file before the load stage, so an invalid file fails the build before the pack folder is touched. A missing file is allowed.
- An override id that matches no building in the build area is a warning, listed in `03-buildings/report.json` (a test-area build doesn't contain most of the map).
- Credits: every override that applies to a building in the build area (listed in some building's `overrides`) with a licence other than `own work` adds a manifest credit: `source` `overrides`, `dataset` the entry's `source`, `licence`, `attribution` and `retrieved_at` from the entry. Identical credits fold into one, as for read files. `own work` overrides add no credit. The manifest schema doesn't change. The report lists every applied override with its `source` and `licence`.
- `mapgen notice` knows `CC BY-SA 4.0` (`https://creativecommons.org/licenses/by-sa/4.0/`) in addition to the existing licences, since street-level imagery such as Mapillary is a possible override source.
- Appearance overrides (height, roof, facade, models) and `mapgen enhance` stay for their own spec; this file's format may grow then, with a new schema version.

### MML matching

- Each OSM building gets at most one MML building: the one with the largest overlap with its footprint, if it covers at least 50% of the footprint. Ties go to the lower `mtk_id`, so builds are deterministic.
- Several OSM buildings may match the same MML building; each gets its class.
- MML buildings that no OSM building matches are counted in the report and not added to the pack (next spec).
- `buildings.json` records `mml_building` (`mml:rakennus/<mtk_id>` or `null`) and `mml_overlap` (share of the OSM footprint covered, 0–1, rounded to 3 decimals, or `null`).

### POI to building links

- A POI is **on or in** a building when:
  - it is the building itself (POI tags on a building way or relation; the POI id equals the building id); or
  - it is a node whose point is covered by the footprint (boundary included); or
  - it is an area that isn't a building and whose representative point (shapely `point_on_surface`, deterministic) is covered by the footprint.

  When several footprints cover the point, the smallest footprint wins and the case is listed in the report. The building lists it in `poi_ids`, and the POI's `building_id` points back.
- A building is **inside a POI area** when an area POI that isn't a building covers the building's footprint centroid (a school ground, a sports area, a park). The building lists those areas in `area_poi_ids`, and each area lists the building in `contains_building_ids`.
- All lists are sorted by id, and the links always agree in both directions.

### Building category

The resolved `category` is one of: `residential`, `commercial`, `retail`, `industrial`, `public`, `religious`, `education`, `agricultural`, `garage_or_shed`, `other`, `unknown`.

First step with a value wins; `category_source` names it:

1. `override`: the override file.
2. `osm_building`: the building's `building` tag, unless it is `yes` or `roof`. Values not in the table give `other`.
3. `osm_poi`: a POI tag (`shop`, `amenity`, `office`, `leisure`, `tourism`) on the building, or on a POI on or in it. Tags on the building itself come first. If the POIs on or in it map to different categories, this step has no value, and the building is listed in the report. Values not in the table give `other`; values the table maps to "no value" are ignored.
4. `osm_poi_area`: the POI areas the building is inside. If they map to one category, that one; if they differ, no value (reported). Values not in the table, or mapped to "no value", are ignored.
5. `mml_use_class`: the matched MML building's `kohdeluokka`, by its first four digits:

   | Feature classes | Category |
   | --- | --- |
   | 4221x Asuinrakennus | `residential` |
   | 4223x Lomarakennus | `residential` |
   | 4224x Teollinen rakennus | `industrial` |
   | 4225x Kirkollinen rakennus, 42270 Kirkko | `religious` |
   | 4226x Muu rakennus | `garage_or_shed` when the OSM footprint's area (map CRS) is under 40 m²; otherwise no value |
   | 4222x Liike- tai julkinen rakennus | no value here; becomes `commercial` after step 6 if land use gives nothing |
   | any other code | no value, counted in the report |

6. `osm_landuse`: the `landuse` area that covers the footprint's centroid; when several do, the smallest that maps to a category.
7. Fallback: a building with MML class 4222x and no value so far gets `commercial` with `category_source: "mml_use_class"`.
8. `unknown`, with `category_source: null`.

Raw values are always kept, whichever step wins: `category_raw` holds `osm_building` (string or `null`), `osm_poi` (the `key=value` strings considered in step 3, sorted, possibly empty), `osm_poi_area` (the same for step 4), `mml_feature_class` (`kohdeluokka`, integer or `null`), `mml_use_class` (`kayttotarkoitus`, integer or `null`), `osm_landuse` (string or `null`) and `override` (the category from the override file, or `null`).

The OSM tables are OSM's, not map-specific, so they live in the pipeline, as the road class mapping does (ADR 0012). Each value not in its table is counted in the report.

`building` values (step 2):

| Category | Values |
| --- | --- |
| `residential` | `residential`, `house`, `detached`, `apartments`, `semidetached_house`, `terrace`, `bungalow`, `dormitory`, `cabin` |
| `commercial` | `commercial`, `office`, `hotel` |
| `retail` | `retail`, `supermarket`, `kiosk` |
| `industrial` | `industrial`, `warehouse`, `factory`, `manufacture` |
| `public` | `public`, `civic`, `government`, `fire_station`, `hospital`, `train_station`, `transportation` |
| `religious` | `church`, `chapel`, `cathedral`, `religious`, `mosque`, `temple`, `synagogue` |
| `education` | `school`, `kindergarten`, `university`, `college` |
| `agricultural` | `farm`, `farm_auxiliary`, `barn`, `cowshed`, `stable`, `greenhouse`, `sty` |
| `garage_or_shed` | `garage`, `garages`, `carport`, `shed` |

POI tags (steps 3 and 4):

| Category | Tags |
| --- | --- |
| `retail` | `shop=*`; `amenity=marketplace` |
| `commercial` | `office=*` except `government`; `amenity` `restaurant`, `cafe`, `fast_food`, `bar`, `pub`, `bank`, `pharmacy`, `fuel`, `cinema`; `leisure=fitness_centre`; `tourism` `hotel`, `motel`, `hostel`, `guest_house` |
| `public` | `office=government`; `amenity` `townhall`, `library`, `fire_station`, `police`, `post_office`, `hospital`, `clinic`, `community_centre`, `courthouse`; `leisure` `sports_centre`, `stadium`, `sports_hall`, `swimming_pool`, `ice_rink`; `tourism` `museum`, `gallery`, `information` |
| `religious` | `amenity` `place_of_worship`, `grave_yard` |
| `education` | `amenity` `school`, `kindergarten`, `college`, `university` |
| no value | `amenity=parking`; `leisure` `park`, `playground`, `pitch`, `track`, `garden`; `tourism` `artwork`, `viewpoint`, `picnic_site` |

`landuse` values (step 6): `residential`, `commercial`, `retail`, `industrial` map to the category of the same name; `farmland`, `farmyard` → `agricultural`; `religious`, `cemetery` → `religious`; `education` → `education`. Other values give no value.

### Building name and address

- Name fields, first with a value wins; `name_source` names it:
  1. `override`: the override file.
  2. `osm`: the building's own tags, when any of the three is non-null.
  3. `osm_poi`: when exactly one POI on or in the building is named, its name fields. POI areas the building is inside don't count.
  4. All `null`, `name_source: null`.
- `address`: `{street, housenumber}`, each string or `null`, from the building's own tags; `address_source` is `osm` when either is non-null, else `null`. House numbers like `12a` and `3-5` are kept as tagged.

### `buildings.json` (schema version 2)

Each entry keeps its current fields and gains: `category`, `category_source`, `category_raw`, `name`, `name_sv`, `ref`, `name_source`, `address`, `address_source`, `poi_ids`, `area_poi_ids`, `mml_building`, `mml_overlap` and `overrides`. All are always present.

- `overrides`: the override entries that set at least one attribute of this building after precedence (OSM key over MML key, field by field), sorted by `id`, empty when none applies. Each is `{id, source, licence, fields}`: `id` the override file key (`osm:way/123` or `mml:rakennus/456`), `source` and `licence` from the entry, and `fields` the attributes it actually set, sorted, from `category` and `name` (`name` stands for all three name fields). An entry whose every field lost to the OSM key is not listed. `category_source: override` requires an entry with `category` in `fields`, and `name_source: override` one with `name`, and the other way round.

- `schema_version` becomes 2, because readers reject unknown keys. Every reader of the file (the pack checks and the tests) is updated to version 2.
- The file gets a pydantic model and a committed JSON Schema, `tools/mapgen/src/mapgen/build/schemas/buildings-v2.schema.json`, validated on every write, with a test that the committed and generated schemas match (as for the manifest, camera presets and road graph). Unknown keys are errors.

### `pois.json`

A new file in the pack, found by its fixed name and not listed in the manifest, so the manifest stays at schema version 1 (as for `buildings.json`, `camera_presets.json` and `road_graph.json`).

- Fields: `schema_version` (1), `map_id`, `area`, `pois`, sorted by id.
- Each POI:

  | Field | Meaning |
  | --- | --- |
  | `id` | `osm:node/<id>`, `osm:way/<id>` or `osm:relation/<id>` |
  | `name`, `name_sv`, `ref` | Name fields; `null` for unnamed POIs |
  | `type` | The first of `shop`, `amenity`, `office`, `tourism`, `leisure` the feature has |
  | `subtype` | That tag's value, e.g. `parking` |
  | `other_tags` | Any further of those five keys as sorted `key=value` strings, e.g. `["amenity=fuel"]` on a `shop=convenience` that is also tagged `amenity=fuel` (its `type` is `shop`, the first key in the order above) |
  | `geometry` | `point` (node) or `area` (way or relation) |
  | `position` | `[x, y, z]`, Godot local metres: the node, or the area's representative point; y is the terrain height there (`Heightfield.sample`) |
  | `outline` | Areas: a list of polygons, each a list of rings (exterior first, then holes), each ring a list of `[x, z]` Godot local points, closed (first point repeated). `null` for points |
  | `address` | `{street, housenumber}`, each string or `null` |
  | `address_source` | `osm` (the POI's own tags), `building` (its `building_id`'s address, when the POI has neither field), or `null` |
  | `building_id` | The building it is on or in, or `null` |
  | `contains_building_ids` | Areas: the buildings inside it (sorted); empty for points |

- Kept when the position is inside the build area. An area's outline is clipped to the build area. A POI area whose ring can't be built is skipped with a reason in the report.
- Defined by a pydantic model with a committed `pois-v1.schema.json`, validated on every write.
- Real names are stored as tagged. The pack never holds logos or brand images.

### Reports and pack checks

- `03-buildings/report.json` gains: count per `category` and per `category_source`; class 6 buildings split at 40 m²; class 2 buildings decided by land use and by the fallback; MML matched, unmatched OSM, unmatched MML, MML buildings matched more than once; unmapped raw values per step with counts; buildings with conflicting POI categories; POIs covered by more than one footprint; applied overrides (entries that set at least one field), shadowed overrides (entries that reach a building but whose every field lost to the OSM key) and unmatched overrides; MML records skipped in the build area, and skipped records without a location (counted separately, since they can't be placed). `03-buildings/buildings.geojson` gains the new fields.
- A new `08-places/` folder: `pois.geojson` (map CRS, for QGIS) and `report.json` (POI counts by type, named and unnamed, linked and unlinked, skipped with reasons, land use areas used and left out). Its `pois.outside_area` counts the POIs whose geometry touches the build area but whose position (the node, or an area's representative point) lies outside it, so they aren't in `pois.json`. POIs that don't touch the build area at all are dropped at load and counted nowhere.
- Pack checks (`build/pack_checks.py`), in every build: every building's `category`, `category_source`, `name_source` and `address_source` are valid values; `category_source` agrees with `category_raw` (the winning step has a raw value); every `poi_ids` and `area_poi_ids` entry exists in `pois.json` and the POI links back; every POI's `building_id` and `contains_building_ids` name existing buildings that link back; every POI position and outline point is finite and inside the area bounds; every outline ring is closed with at least 4 points.

### Test-area fixture

Part of this work: recut the fixture from local data only.

- `cut_test_area.py` also writes `mml/topographic/` for the fixture: a GPKG holding the `rakennus` records that intersect the test area (all columns), with `sqlite3` only, from `data/raw/somero/`.
- It also keeps the land use and POI features touching the test area, with their nodes. Contributor metadata stays stripped.
- The fixture's `download-manifest.json` gets the topographic entry, and its `NOTICE.md` names the Topographic Database with its date. `test_fixture_snapshot_builds_the_same_credits` passes again.

### Roadman.Core

- `Maps/Buildings.cs` and `BuildingsReader` read `buildings.json` (version 2 only); `Maps/Pois.cs` and `PoisReader` read `pois.json`. Both follow `RoadGraphReader` and reject, with a `MapPackException` naming the problem: another `schema_version`, missing or unknown keys, unknown `category`, source, `type` or `geometry` values, duplicate ids, open or short outline rings, an outline on a point or none on an area, non-finite numbers.
- `PlaceIndex.Create(buildings, pois)` joins them and rejects links that point to missing ids or don't point back.
- Lookups:
  - building by id and by glTF node name (`node` and `collision_nodes`);
  - the POIs on or in a building, and the POI areas it is inside;
  - the POI area whose outline contains an x/z point (smallest area first), and the POI point nearest to a position within a radius;
  - `RoadGraph.NearestEdge(position, maxDistance)`: the edge (drivable or walkable) whose polyline is nearest in 3D, with the distance and the nearest point; ties go to the lower edge id.
- `StreetLabel.Format(edge)`: the display text, Finnish only.
  - `name` when present, with `ref` after it when the edge has both (`Jokioistentie (52)`); `ref` alone when there is no name.
  - Neither: the class's display text: `primary`, `secondary` "Main road"; `tertiary`, `unclassified` "Road"; `residential` "Residential street"; `living_street` "Living street"; `service` "Service road"; `driveway` "Driveway"; `parking_aisle` "Parking aisle"; `track` "Track"; `cycleway` "Cycle path"; `footway` "Footpath"; `path` "Path"; `pedestrian` "Pedestrian street". A class not in this list shows its value with `_` replaced by spaces and the first letter capitalised.

### Game

- `MapLoader` reads `buildings.json` and `pois.json` after the road graph and exposes `MapLoader.Places` (`PlaceIndex`). A missing or invalid file pushes an error with the pack path and `/regen-map <id>`; the tiles and road graph still load, and `Places` is null. Building collision bodies go in a new group `map_buildings`.
- **HUD street label.** A HUD `CanvasLayer` with one `Label` shows the current street, in debug and release builds.
  - Reference point: the player's position once a player exists; until then, the point below the active camera where a downward ray first hits `map_roads` or `map_terrain`. With no hit (outside the map), the label is empty.
  - The label shows `StreetLabel.Format` of the nearest edge within `StreetMaxDistance` (export, default 40 m) of the reference point, in 3D. Nothing within it: the label is empty.
  - The label only changes when the text changes. It shows street names, road numbers and road classes only, never business names.
- **Debug inspector.** In debug builds only (`OS.IsDebugBuild()`), I (`debug_inspect`, physical key; works on a Mac keyboard without fn) toggles it.
  - While on, a crosshair is drawn at the screen centre, and each frame a ray from the camera through it is cast.
  - A hit on a body in `map_buildings` shows that building: id, category with `category_source`, every `category_raw` value, `mml_building` and `mml_overlap`, name fields with `name_source`, address with `address_source`, height with `height_source`, its POIs and POI areas (id, type, subtype, name fields), and its `overrides` list (id, source, licence, fields) next to `category_source` and `name_source`.
  - Any other hit shows the POI point nearest to the hit point within `PoiPickRadius` (export, default 3 m); failing that, the smallest POI area whose outline contains the hit point's x/z; else "No building or point of interest here". A POI shows all its fields.
  - Missing `Places`: the inspector shows "No place data in this pack; run /regen-map <id>".
  - The panel shows the pack's raw data, including real business names; it is a debug tool and never ships.
- **Screenshot tool.** `--inspect` renders with the inspector on, aimed at the preset's look-at point through the screen centre; the HUD label and the panel are moved into the capture `SubViewport`, like the orthophoto overlay. Exit 1 in a non-debug build.

### Snapshot

After the implementation passes `/verify`, Claude runs `uv run mapgen snapshot create somero` (local only) and reports the archive path, size, members and NOTICE. The snapshot then holds the OSM file, DEM, lidar and the topographic GPKG. Claude doesn't run `snapshot publish`; the user reviews and publishes it, which updates the `snapshot` block in `map.yaml`. Until then a release tag fails at `snapshot fetch` (see "Current state").

### Docs and decisions

- ADR 0013: building categories (the fixed set, the precedence, the mapping tables and where they live, classifying MML by `kohdeluokka`, the 40 m² rule, MML matching, raw values), the override file (the `own work` marker, third-party overrides as manifest credits, the per-building `overrides` list), `pois.json` with outlines and addresses, the POI-building link rules, the `building_use` capability, and the HUD label rules.
- ADR 0006 amendment: `buildings.json` schema version 2 and its committed schema.
- `docs/architecture.md`: the new capability, the override file in `maps/<map-id>/overrides/`, stage 3 and stage 8 outputs, `pois.json`, the readers, `MapLoader.Places`, the HUD and the inspector. `game/CLAUDE.md`: the HUD label and the inspector. `tools/mapgen/CLAUDE.md`: the fixture contents. `CREDITS.md`: the Topographic Database row with its download date (2026-10-29).

### Edge cases

- A building with no OSM, MML or land use information: `unknown`, `category_source: null`, raw values all `null` or empty.
- A building way with POI tags and a different POI node inside it: both count in step 3; tags on the building itself win.
- A POI node exactly on a shared wall of two footprints: the smaller footprint wins and the case is reported.
- A building inside two POI areas with different categories: step 4 has no value; reported.
- A POI with its own `addr:street` but no `addr:housenumber`: `address_source: osm`, the building's address isn't mixed in.
- An MML polygon with an invalid geometry: repaired with `make_valid`, or skipped and counted when that fails.
- An `addr:housenumber` without `addr:street` (none in Somero): kept, `street: null`.
- A map with `mml_topographic` disabled: step 5 and the fallback never have a value, `mml_building` and `mml_overlap` are `null`, and the build says so in the log. It isn't a failure.
- A map with no POIs: a valid `pois.json` with an empty list.
- The reference point exactly between two edges: the lower edge id wins, so the label doesn't flicker.
- Under a bridge: 3D distance means the label shows the way under the deck, not the bridge road.

## Acceptance criteria

1. `mapgen build somero --area test` writes `buildings.json` (schema version 2) and `pois.json` that validate against the committed `buildings-v2.schema.json` and `pois-v1.schema.json`, and each committed schema, including `building-overrides-v1.schema.json`, equals the one generated from its model.
2. The manifest validates against the unchanged `manifest-v1.schema.json` and lists a Topographic Database credit with the attribution from the download manifest. `mapgen notice --check` passes with it.
3. A synthetic snippet fixture of buildings, POIs, land use, MML records and an override file gives the expected `category` and `category_source` for each step: an override; a specific `building` value; `roof` and `yes` with `amenity=fuel` on the way (`commercial`, `osm_poi`); `yes` with a POI node inside; two inside POIs of different categories (on to the next step, reported); a building inside a school area (`education`, `osm_poi_area`) and inside a park (no value); MML 4221x, 4223x, 4225x, 42270; 4226x at 39 m² and at 41 m²; 4222x with and without a land use value; land use; nothing (`unknown`). Each entry's `category_raw` holds every source's raw value.
4. Every value in the three OSM tables maps as listed; an unmapped value is counted in the report and gives `other` (steps 2, 3) or no value (steps 4, 6).
5. MML matching on snippets: a 60% overlap matches, a 40% overlap doesn't, two OSM buildings on one MML building both match, and equal overlaps go to the lower `mtk_id`. The full Somero build (local only) reports its matched count and its counts per category and source; the implementation report compares them with "Expected categories" and explains any difference.
6. Overrides: a valid file applies by OSM id and by MML id, the OSM key wins over the MML key, a name override sets all three name fields, an unmatched id is a warning in the report, and an invalid file (unknown key, unknown category, missing `source` or `licence`, neither category nor a name, a third-party licence without `attribution` or `retrieved_at` or with a licence `mapgen notice` doesn't know, `attribution` on an `own work` entry) fails the build before the pack folder is touched. Each building's `overrides` lists exactly the entries that set something, with the right `fields`, including a building where the MML key sets the category and the OSM key the name. A `CC BY-SA 4.0` override adds one manifest credit (folded when two entries share it) and `mapgen notice --check` passes with it; an `own work` override adds none; an unmatched third-party override adds none.
7. Names and addresses: a building with its own name keeps it; one with exactly one named POI inside takes its name fields with `name_source: osm_poi`; one with two named POIs inside gets `null`. A POI with its own address keeps it (`osm`); one without takes its building's (`building`). Name and address values follow the road edge rules through the same function (missing, empty, padded, non-NFC and `;` cases); `12a` and `3-5` are kept.
8. The test-area build links the `shop=convenience` node to the building that contains it, both ways, and that building takes the shop's name.
9. POI outlines: snippet areas with a hole and a multipolygon give closed rings in Godot x/z, exterior first; points have `outline: null`; an area crossing the build-area edge is clipped. Unnamed POIs (a parking area) are in `pois.json` with their type.
10. The pack checks fail a pack with a dangling or one-way link, an invalid category or source, a category source without its raw value, a POI or outline point outside the area, or an open ring; a failed check fails the build and leaves the previous pack.
11. `mml_topographic` enabled without its GPKG fails the build with the download hint; disabled, the build succeeds with step 5 empty and `mml_building` `null`.
12. Two builds from the same raw data write byte-identical `buildings.json` and `pois.json`.
13. The recreated fixture holds the topographic `rakennus` records, land use and POI features touching the test area, a download manifest entry and NOTICE line for the Topographic Database, and no contributor metadata, cut from local data only. `test_fixture_snapshot_builds_the_same_credits` passes again, and the whole pytest suite passes.
14. `BuildingsReader`, `PoisReader` and `PlaceIndex` accept valid files and reject each invalid case listed under "Roadman.Core" (including a version 1 `buildings.json`) with a `MapPackException` naming the problem. `RoadGraph.NearestEdge` returns the right edge, distance and point for a small hand-made graph, including the 40 m limit, a tie, and a lower edge under a higher one. The POI area lookup finds a point inside an outline and not inside its hole. `StreetLabel.Format` gives name and ref, name only, ref only, and the class text for every listed class and for an unlisted one.
15. After the test-area pack loads, `MapLoader.Places` has the building and POI counts of the files, and every building collision body is in `map_buildings` and resolves to its building id. A pack copy without `pois.json` loads tiles and road graph, pushes an error naming the path and `/regen-map`, and leaves `Places` null.
16. With the camera above a named edge in the test area, the HUD label shows its `StreetLabel.Format` text; above an unnamed service edge, "Service road"; more than 40 m from every edge, empty.
17. I toggles the inspector in a debug build. A ray at a test-area building shows its id, category, `category_source` and address; aimed at the convenience shop's building, the panel lists the POI. A ray hitting the ground within 3 m of a POI point shows that POI.
18. `/verify` passes. A `centre-street-level` screenshot shows the HUD label with a street name, and an `--inspect` screenshot shows the inspector panel on a building. Both have been looked at and described.
19. After `/verify`, `mapgen snapshot create somero` writes an archive whose members include `mml/topographic/maastotietokanta_kaikki.gpkg`, whose `SHA256SUMS` and download manifest list it, and whose `NOTICE.md` names the Topographic Database; `check-no-secret.sh` passes on it. `snapshot publish` isn't run.

## Test ideas

| Criterion | Layer |
| --- | --- |
| 1, 2, 8, 12 | pytest: end-to-end test-area build from the fixture |
| 3, 4, 5 (snippets), 6, 7, 9, 11 | pytest: unit tests on small hand-written OSM snippets, a synthetic `rakennus` GPKG written with `sqlite3`, and override files |
| 5 (full counts) | pytest, marked to run only when `data/raw/somero/` has the topographic file (skipped in CI), plus the report in the implementation summary |
| 10 | pytest calling `build/pack_checks.py` on corrupted staged packs, plus the build-fails test |
| 13 | pytest on the fixture files and the existing snapshot test |
| 14 | `Roadman.Core` xUnit |
| 15, 16, 17 | gdUnit4: load the test-area pack, camera above edges, inspector rays, a temporary pack copy without `pois.json` |
| 18 | `/verify` screenshots, described in the report; a playtest flying through the centre with the inspector on |
| 19 | manual run after `/verify`, reported to the user |

## Amendments

- 2026-10-29, during implementation: `buildings.json` v2 gains `overrides` (`{id, source, licence, fields}` per applied entry), so the debug inspector can show an override's source from the pack. Overrides marked `own work` add no credit; third-party overrides require `attribution` and `retrieved_at` and add a manifest credit, following the outline's rule that the pipeline writes every override's licence into the credits. `mapgen notice` learns `CC BY-SA 4.0`. Decided by the user.
- 2026-10-29, game part: the `other_tags` example corrected to match the `type` key order (`["amenity=fuel"]` on a feature also tagged `shop=convenience`), and `08-places/report.json`'s `pois.outside_area` described exactly (POIs that touch the build area but whose position lies outside it). Decided by the user.
- 2026-10-29, review: the buildings report separates `overrides.shadowed` from `overrides.applied`, and counts skipped MML records only inside the build area, de-duplicated by id across files, with records without a location under `skipped_without_location`.

## Open questions

None. All questions were answered on 2026-10-29:

- MML labels checked against MML's feature descriptions (see "Data").
- How business names are displayed in the game stays a separate decision on the legal checklist. This spec shows them only in the debug inspector, so nothing here depends on it.
- The 164 MML-only buildings get their own spec, next after this one.
