# 0005: Source adapters, Overpass and the MML file service

Status: Accepted

## Context

The pipeline needs raw OSM and MML data for each map, downloaded by a script so the download can be repeated and checked (project outline, phase 2). ADR 0002 requires data sources behind one adapter interface and no map-specific code. MML's open data file service needs a personal API key, which must not end up in the repository. MML's laser scanning data 5 p isn't open data.

## Decision

- **Adapter interface.** Every source in `map.yaml` names an `adapter`. The registry in `tools/mapgen/src/mapgen/sources/registry.py` maps that name to a `SourceAdapter` class with a `name`, a `licence`, an `attribution`, an options model and `download(ctx) -> list[DownloadedFile]`. Country-specific logic lives only inside adapters. The HTTP client is injected through the context, so tests never touch the network.
- **OSM.** One Overpass request for the whole map bbox (nodes and ways in the bbox, all nodes of those ways, and the relations that use them, without the relations' other members), saved as `.osm` XML. One attempt only: when Overpass is busy (HTTP 429 or 504), the command says so and exits non-zero instead of retrying.
- **MML.** The MML adapters use the OGC API Processes interface of the file service (execute, poll the job, list results, download). The key is read only from the `MML_API_KEY` environment variable and is redacted from every error message; it is never written to the manifest or logs. Without a key, each MML source prints the manual download steps for MML's Karttapaikka file service and registers any files the user put in its raw folder (origin `manual`).
- **Orthophoto and 3D buildings by map sheet.** These processes take TM35 map sheet names, which `mapgen/tm35.py` computes from the bbox. The sheet scale and the `mapSheetInput` type are per-source options: confirmed for the orthophoto (a list of 1:10 000 sheets), not yet for 3D buildings.
- **No laser scanning data 5 p.** The adapters refuse any process id that starts with `laserkeilausaineisto`, and a test checks this.
- **Manifest.** Each download run writes `data/raw/<map-id>/download-manifest.json` with the checksum, licence, attribution and request parameters of every file. Runs with `--only` merge into the existing manifest.

## Consequences

- Downloads are reproducible with `uv run mapgen download <map-id>`, and the manifest feeds the credits of each map pack.
- A map outside Finland adds a config with other adapters (for example a global elevation model) instead of changing the pipeline.
- Relations arrive incomplete: the Overpass query returns the relations that use nodes or ways in the bbox, but not their members outside it. Multipolygons (lakes, forests, land use) and boundaries that cross the bbox edge therefore have open or missing rings, and later stages must handle partial multipolygons, for example by clipping to the bbox and skipping or repairing rings that can't be closed.
- The orthophoto process takes a list of up to 100 TM35 1:10 000 sheets and `yearInput` 9999 for the latest year (from its process description). The 3D buildings process is not verified yet; its sheet scale and input type are options in `map.yaml`, and the adapter logs the process input description before running.
- Overpass busy errors need a manual retry.
