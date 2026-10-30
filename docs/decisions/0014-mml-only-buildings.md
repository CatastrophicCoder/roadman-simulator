# 0014: MML-only buildings

Status: Accepted

## Context

OSM maps most of Somero's buildings, but the MML Topographic Database (`rakennus`) has 168 buildings in the build area that no OSM building matches (ADR 0013 matching), mostly sheds, garages and houses. ADR 0013 left them out for a later spec. Without them plots that have buildings in reality are empty. The records sometimes overlap OSM footprints (MML draws a house and its garage, OSM one building), the point cloud has no building class, and the records' start dates don't say when a building was built, so the lidar can't tell a demolished building from a new one. Spec: `docs/specs/mml-only-buildings.md`.

## Decision

- **Which records.** Every `building_use` record that no OSM building matched, whose centroid is in the build area (and whose kept footprint's centroid is, after cutting) and whose footprint is at least 1 m², in `mtk_id` order. No separate switch: they are added whenever the map's `building_use` source is enabled.
- **Cutting.** The footprint minus the union of the OSM footprints it overlaps. OSM geometry is never changed. A remaining piece under 4 m², or one of which less than 50% survives a morphological opening by 0.75 m (shrink inward, then grow back; it removes every part narrower than 1.5 m, straight or L-shaped, and only rounds the corners of wider ones), is a sliver and is dropped; a record left with only slivers isn't added. A record with no OSM overlap is kept whole. The kept pieces of one record are one building.
- **Lidar contradiction.** A footprint with at least 10 lidar points inside (every class but noise, ground included) of which at least 90% are within 1.5 m of the DEM isn't added. Otherwise the existing height rules apply: lidar height, else the default 2 floors.
- **Limits** are pipeline constants and listed in the build report.
- **Attributes.** The category follows ADR 0013's order unchanged (the `osm_building` step never has a value); the MML use class uses the kept area. Names come from an override or one named POI; there is no address. `mml_building` is the building's own id and `mml_overlap` 1.0. MML-keyed overrides apply.
- **Provenance.** The id `mml:rakennus/<mtk_id>` is the only mark of an MML-only building; `buildings.json` stays at schema version 2.
- **Checks.** The pack checks fail an `mml:` building that overlaps an `osm:` building by 0.01 m² or more or whose `mml_building` isn't its own id or `mml_overlap` isn't 1.0.

## Consequences

- Somero gains about 140 buildings with real footprints and no new data source, download or snapshot. The game needs no change.
- The load stage reads ground points around the MML records in a second point cloud read, which adds load time (0.6 s for the full Somero build).
- Where MML and OSM split a building differently, the result is OSM's building plus the MML remainder; a remainder can still be a thin strip above the sliver limits, which the report's compactness and opened-share values help find. The sliver test judges each piece whole, so a compact part joined to a longer thin strip is dropped with it (a 3 × 3 m block with a 1 × 20 m tail keeps 30% after the opening); no Somero piece has that shape.
- A record whose building is gone but whose footprint has fewer than 10 lidar points can't be ruled out; it is kept with the default height.
