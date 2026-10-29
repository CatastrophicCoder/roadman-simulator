# 0013: Place information: building categories, points of interest and the street label

Status: Accepted

## Context

The game must always be able to tell where the player is: the street, the building and what it is, and the points of interest around it (project outline, "Place information"; pipeline stages 3 and 8). OSM tags most of Somero's buildings only `building=yes` (97.4%), so the category has to come from several sources: OSM building and POI tags, the MML Topographic Database's building use classes and OSM land use. The MML class codes are Finnish, so they can't live in the pipeline core (ADR 0002). Spec: `docs/specs/place-information.md`.

## Decision

- **Fixed category set.** `residential`, `commercial`, `retail`, `industrial`, `public`, `religious`, `education`, `agricultural`, `garage_or_shed`, `other`, `unknown`.
- **Precedence.** First step with a value wins, recorded in `category_source`: `override`, `osm_building` (the `building` tag unless `yes` or `roof`), `osm_poi` (POI tags on the building or on POIs on or in it; tags on the building first; conflicting POIs give no value; within one feature the first POI key in the order `shop`, `amenity`, `office`, `tourism`, `leisure` that isn't mapped to "no value" decides, so an unmapped tag gives `other` even when a later key maps), `osm_poi_area` (POI areas the building is inside), `mml_use_class`, `osm_landuse` (the smallest covering area that maps to a category), the 4222x fallback to `commercial`, else `unknown`. This order applies to the category only. Explicit OSM tagging is more specific than MML's broad use classes (one MML class covers shops, offices, schools and fire stations), so OSM tags come before MML here. Heights keep their own order (measured data before OSM tags).
- **Raw values.** Every building keeps every source's raw value in `category_raw`, whichever step wins, so a wrong category can be traced and the rules changed later without new data.
- **Where the tables live.** The OSM `building`, POI and `landuse` tables are OSM's, not a country's, so they live in the pipeline (`build/places/categories.py`), like the road class mapping (ADR 0012). The MML feature-class table lives in the `mml_topographic` adapter. Values not in a table are counted in the build report.
- **MML by `kohdeluokka`.** MML documents the feature classes (42210–42270), not the integer `kayttotarkoitus` column, so the pipeline classifies by `kohdeluokka`'s first four digits and keeps `kayttotarkoitus` as a raw value. "Muu rakennus" (4226x) gives `garage_or_shed` only for footprints under 40 m²; larger ones go on to land use. "Liike- tai julkinen rakennus" (4222x) goes on to land use and falls back to `commercial`.
- **MML matching.** An OSM building takes the MML building that overlaps it most, when that one covers at least 50% of its footprint (the rule of `docs/specs/building-heights.md`); ties go to the lower `mtk_id`. Several OSM buildings may share one MML building. MML-only buildings aren't added (a later spec).
- **New capability.** `building_use`, provided by `mml_topographic`, read with the standard library `sqlite3` and shapely's WKB reader (no new dependency). An enabled source without its GPKG fails the build; a disabled one leaves step 5 empty.
- **Overrides.** `maps/<map-id>/overrides/buildings.json` (schema version 1, committed schema) sets `category` and/or the name fields per OSM or MML building id; the OSM key wins field by field. Every entry records `source` and `licence`. The licence `own work` marks the project's own work; any other licence is third-party data, needs `attribution` and `retrieved_at`, must be a licence `mapgen notice` knows, and adds a manifest credit (source `overrides`). Each building lists the entries that set something in `overrides` (`id`, `source`, `licence`, `fields`), so the pack itself says where an overridden value came from.
- **Names and addresses.** Buildings and POIs use the road name format (ADR 0012) through the same function. A building without its own name takes the name of exactly one named POI on or in it. A POI without its own address takes its building's.
- **POI links.** A POI is on or in a building when it is the building itself, a node covered by the footprint, or a non-building area whose representative point the footprint covers (smallest footprint wins). A building is inside a POI area when the area covers its footprint centroid. Links are stored in both directions and checked in every build.
- **`pois.json`.** A new pack file (schema version 1, committed schema, not in the manifest): every OSM feature with `shop`, `amenity`, `office`, `tourism` or `leisure`, named or not, with its position on the terrain, its outline in Godot x/z (clipped to the area) for areas, address and links. Real names are stored as tagged; the pack never holds logos.
- **HUD street label.** The game shows the street of the nearest road graph edge within 40 m of the player (or of the ground below the camera, until there is a player), measured in 3D so a way under a bridge shows under the deck. Name, then the road number in brackets, else the class's text. Finnish only. It never shows business names.

## Consequences

- A new map in Finland gets categories with no code change; a map elsewhere gets them from OSM and land use only, until an adapter for its country provides `building_use`.
- Full Somero build (2026-10-29): 1,841 buildings, 1,795 matched to MML, 43 `unknown`; 54 POIs, 24 linked to a building.
- Readers reject unknown keys, so `buildings.json` moved to schema version 2 (ADR 0006 amendment).
- Business names are in the pack and shown only in the debug inspector. How the game shows them is a separate decision.
- A release built from the snapshot pinned before this change fails, because that snapshot has no Topographic Database file, until a new snapshot is published and pinned (ADR 0011).
