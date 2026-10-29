# 0005: Source adapters, Overpass and the MML file service

Status: Accepted

## Context

The pipeline needs raw OSM and MML data for each map, downloaded by a script so the download can be repeated and checked (project outline, phase 2). ADR 0002 requires data sources behind one adapter interface and no map-specific code. MML's open data file service needs a personal API key, which must not end up in the repository. MML's laser scanning data 5 p isn't open data.

## Decision

- **Adapter interface.** Every source in `map.yaml` names an `adapter`. The registry in `tools/mapgen/src/mapgen/sources/registry.py` maps that name to a `SourceAdapter` class with a `name`, a `licence`, an `attribution`, an options model and `download(ctx) -> list[DownloadedFile]`. Country-specific logic lives only inside adapters. The HTTP client is injected through the context, so tests never touch the network.
- **OSM.** One Overpass request for the whole map bbox (nodes and ways in the bbox, all nodes of those ways, and the relations that use them, without the relations' other members), saved as `.osm` XML. The query ends with `out body`, so the file has no contributor metadata (`user`, `uid`, `timestamp`, `changeset`, `version`; changed from `out meta` on 2026-10-29, ADR 0011). One attempt by default: when Overpass is busy, the command says so and exits non-zero instead of retrying (see the 2026-10-29 amendment for `--retries`).
- **MML.** The MML adapters use the OGC API Processes interface of the file service (execute, poll the job, list results, download). The key is read only from the `MML_API_KEY` environment variable and is redacted from every error message; it is never written to the manifest or logs. Without a key, each MML source prints the manual download steps for MML's Karttapaikka file service and registers any files the user put in its raw folder (origin `manual`).
- **Orthophoto and 3D buildings by map sheet.** These processes take TM35 map sheet names, which `mapgen/tm35.py` computes from the bbox. The sheet scale and the `mapSheetInput` type are per-source options: confirmed for the orthophoto (a list of 1:10 000 sheets), not yet for 3D buildings.
- **No laser scanning data 5 p.** The adapters refuse any process id that starts with `laserkeilausaineisto`, and a test checks this. Amended on 2026-10-27, see below.
- **Manifest.** Each download run writes `data/raw/<map-id>/download-manifest.json` with the checksum, licence, attribution and request parameters of every file. Runs with `--only` merge into the existing manifest.

## Consequences

- Downloads are reproducible with `uv run mapgen download <map-id>`, and the manifest feeds the credits of each map pack.
- A map outside Finland adds a config with other adapters (for example a global elevation model) instead of changing the pipeline.
- Relations arrive incomplete: the Overpass query returns the relations that use nodes or ways in the bbox, but not their members outside it. Multipolygons (lakes, forests, land use) and boundaries that cross the bbox edge therefore have open or missing rings, and later stages must handle partial multipolygons, for example by clipping to the bbox and skipping or repairing rings that can't be closed.
- The orthophoto process takes a list of up to 100 TM35 1:10 000 sheets and `yearInput` 9999 for the latest year (from its process description). The 3D buildings process takes a list of up to 100 TM35 sheets, `CityGML` and `LOD2` only; it has no coverage for Somero at either the 1:10 000 or the 1:5 000 scale (checked 2026-10-27, `docs/specs/building-heights.md`).
- Overpass busy errors need a manual retry, unless `--retries` is given (amendment 2026-10-29).

## Amendment (2026-10-27): laser scanning data 0.5 p

Spec: `docs/specs/building-heights.md`.

- MML's laser scanning data 0.5 p is open data under CC BY 4.0, and the `mml_lidar` adapter uses it for building heights.
- Only the process `laserkeilausaineisto_05_karttalehti` is allowed. `ensure_allowed_process` and the `mml_lidar` options refuse every other process id that starts with `laserkeilausaineisto`, including every 5 p process, and a test checks this with a 5 p id.
- The 5 p product stays forbidden: MML says it "cannot be published or transferred as such or at a similar accuracy".
- The files come on TM35 1:5 000 sheets (3 × 3 km) as LAZ, in EPSG:3067 with N2000 heights. The adapter's options are `sheet_scale` (5000) and `dataset` (`Uusin`, the latest).

## Amendment (2026-10-29): Overpass retries

A tagged release failed because Overpass answered HTTP 502 (server at capacity).

- Busy statuses are now 429, 502, 503 and 504.
- `mapgen download --retries N` (default 0) lets the OSM adapter retry a busy response up to N times. The waits are 30, 60, 120 s and so on, doubling each time. A `Retry-After` header (seconds or an HTTP date) replaces the wait. A `Retry-After` longer than 600 s ends the retries with an error instead of waiting.
- Each retry is logged with the HTTP status, the attempt and the wait only. Request bodies (the query) and response bodies are never logged.
- Local runs keep one attempt, so a busy server doesn't block an interactive run for minutes. Releases no longer download from Overpass (ADR 0011).
- Other HTTP errors and network failures aren't retried.
