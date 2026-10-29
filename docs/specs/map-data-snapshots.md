# Spec: map data snapshots

## Status

Approved

## Goal

Release builds stop depending on Overpass and the MML file service. The user downloads a map's raw data once, locally, and publishes it as a pinned, checksummed snapshot on its own GitHub release; every release fetches that snapshot and rebuilds the map pack from it with the current pipeline code. Releases then need no MML API key and no longer fail when Overpass is busy.

## Data policy

External data is fetched once.

- All data a map needs is already in `tools/mapgen/data/raw/<map-id>/` (for Somero: OSM, DEM 2 m, lidar 0.5 p and the debug-only orthophoto).
- Builds, `/verify`, `/regen-map` and `snapshot create` use only that local data and never contact Overpass or MML.
- `mapgen download`, or any other external data fetch, runs only when the user explicitly asks for it.
- `snapshot fetch` is for fresh clones and CI. It never replaces existing local data unless it is run with `--force`.

This rule is also in the root `CLAUDE.md` and `tools/mapgen/CLAUDE.md`.

## Behaviour

### Snapshot contents

A snapshot is one zip archive (zip, so `.github/scripts/check-no-secret.sh` can unpack and check it unchanged), laid out like `data/raw/<map-id>/`:

- The files of every enabled, non-`debug_only` source, exactly as listed for that source in `download-manifest.json`. For Somero today these are `osm/somero.osm`, `mml/dem_2m/korkeusmalli_2m.tif`, `mml/lidar_05p/L4211B2.laz` and `mml/lidar_05p/L4212A1.laz`. Nothing else from the folder goes in: no `.gpkg`, QGIS project, `.ovr` or orthophoto files.
- The OSM file has its contributor metadata removed: the attributes `user`, `uid`, `timestamp`, `changeset` and `version` are stripped from every element. The local file isn't changed. The pipeline doesn't read these attributes.
- `download-manifest.json`: the local manifest filtered to the included sources' entries (same schema, version 1). For a file changed by the snapshot (the OSM file), the entry has the archive file's `size` and `sha256`, plus `source_size` and `source_sha256` of the original local file and `modifications: "contributor metadata removed"`. If an entry already has `source_sha256` (data that came from an earlier snapshot), that value is kept.
- `SHA256SUMS`: one `<sha256>  <path>` line for every other file in the archive, in `sha256sum -c` format.
- `NOTICE.md`, generated from the manifest entries, never hand-written:
  - OSM: "© OpenStreetMap contributors", ODbL 1.0 with its URL, and a statement that the extract is a derivative database of the OpenStreetMap database, offered under the ODbL 1.0.
  - MML: "Contains data from the National Land Survey of Finland" with each exact dataset name and date from the manifest attribution (for example "Elevation model 2 m, 10/2026" or "Laser scanning data 0,5 p, 10/2026"), the `retrieved_at` date of each file, and CC BY 4.0 with its URL.
  - Changes: the OSM contributor metadata was removed; the MML files are as downloaded and identified by their `sha256` in the manifest.
  - Licence URLs come from `notice.LICENCE_URLS`. An unknown licence fails create.
- Never a credential: no `MML_API_KEY` value and no `api-key=` value.

### Storage

- Each snapshot is the single asset of its own GitHub release. The tag is `data-<map-id>-<YYYY-MM-DD>`, using the creation date in UTC. The asset name is `mapdata-<map-id>-<YYYY-MM-DD>.zip`.
- Data releases are created with `--latest=false` and are never marked as latest. Their tags don't match `release.yml`'s `v*` filter, and `ci.yml` ignores `data-*` tags.
- `maps/<map-id>/map.yaml` gets an optional block:

  ```yaml
  snapshot:
    repo: CatastrophicCoder/roadman-simulator
    release_tag: data-somero-2026-10-29
    asset: mapdata-somero-2026-10-29.zip
    sha256: <64 lowercase hex characters>
  ```

  `MapConfig.snapshot` (a `StrictModel`, optional) validates the block:
  - `repo` is `<owner>/<name>`.
  - `release_tag` is `data-<this map's id>-YYYY-MM-DD` with a real calendar date.
  - `asset` is a plain file name ending in `.zip`, with no `/`, `\` or `..`.
  - `sha256` is 64 lowercase hex characters.
  - No extra keys.

  The block is optional, so maps and test configs without a snapshot still load.

### Commands

`uv run mapgen snapshot create <map-id> [--out DIR]`

- Reads `data/raw/<map-id>/download-manifest.json` and nothing outside `data/raw/<map-id>/`. Makes no network request.
- Fails with exit 1, naming the source or file, if an enabled non-`debug_only` source has no manifest entries, a listed file is missing, or a file's SHA-256 differs from the manifest.
- Writes the zip to `tools/mapgen/build/snapshots/` (git-ignored) by default. Member order and timestamps are fixed, so the same input gives the same checksum.
- Runs `.github/scripts/check-no-secret.sh` on the archive (literal mode when `MML_API_KEY` is set locally) and deletes the archive if the check fails.
- Prints the archive path, size, SHA-256 and the member list with sizes.

`uv run mapgen snapshot publish <map-id> [--archive PATH] [--repo OWNER/NAME]`

- The user runs it. Claude never runs it without asking first, and tests never call it.
- The repository is `--repo`, else the existing `snapshot.repo`, else `gh repo view --json nameWithOwner`.
- Runs the leak check again, then `gh release create data-<map-id>-<date> <archive> --repo <repo> --latest=false --title ... --notes ...` (the notes carry the NOTICE attributions), and checks with `gh release view` that the asset is there.
- Fails before uploading if the tag already exists, or `gh` is missing or not logged in.
- On success, writes `repo`, `release_tag`, `asset` and `sha256` into `map.yaml`'s `snapshot:` block. Only that block's text changes, so the file's comments and order are kept. The user commits the change.

`uv run mapgen snapshot fetch <map-id> [--force]`

- Fails with exit 2 and "map.yaml has no snapshot block; run `mapgen snapshot publish` or `mapgen download`" when `snapshot` is missing.
- Downloads `https://github.com/<repo>/releases/download/<release_tag>/<asset>` through the injected `HttpClient` into a temp file, with no `gh`, key or auth. HTTP 404 fails with exit 1 and "snapshot asset <asset> not found on release <tag>".
- Checks the archive's SHA-256 against map.yaml before opening it. A mismatch fails with exit 1, shows both values, and leaves `data/raw/` untouched.
- Treats the archive as untrusted. It refuses absolute paths, `..`, symlinks, and members other than the files in `SHA256SUMS` and `SHA256SUMS`, `download-manifest.json` and `NOTICE.md`. It unpacks to a temp folder and checks every file against `SHA256SUMS` and the bundled manifest before anything reaches `data/raw/<map-id>/`.
- Protects local data. Fetch compares the snapshot with the local files of the snapshot's sources. If a local file differs from the snapshot, or a folder of those sources holds a file the snapshot doesn't have, fetch fails with exit 1, lists those files and changes nothing. With `--force`, it replaces the differing files and deletes the extra ones, because the MML loaders read every file in their layer folder; a build then matches the release. Identical files are left as they are. Files of other sources, such as the orthophoto, are never touched.
- Merges the manifest with `manifest.merge_entries`: the entries of the snapshot's sources are replaced, and other sources' entries are kept while their files exist.
- Writes `NOTICE.md` and `SHA256SUMS` to `data/raw/<map-id>/snapshot/`, so they don't mix with source files.

`uv run mapgen download` stays for refreshing data locally, and only when the user asks for it. The Overpass query uses `out body` from now on (no contributor metadata). The Overpass retry (429, 502, 503, 504; 30, 60, 120 s; Retry-After up to 600 s) already exists as `--retries N`, and its default stays 0.

### Workflows

- `release.yml`, `maps` job: the "Download source data" step becomes "Fetch data snapshots", which runs `mapgen snapshot fetch "$map"` for each `maps/*/map.yaml`. A map without a snapshot fails the release. Build, the `area: full` check, the download manifest copies and the NOTICE stay as they are. The "Check packs and manifests for the key" step (literal mode) is removed, and no step references `secrets.MML_API_KEY`. The `export` job's pattern check on `dist` (zips and manifests) stays. The release notes name each map's snapshot tag.
- `ci.yml`: `on.push` gets `branches: ['**']` and `tags-ignore: ['data-*']`; both are needed, because `tags-ignore` alone stops branch runs. CI keeps using the committed test-area fixture.
- The `MML_API_KEY` repository secret can be deleted once the snapshot block is published and committed and a `v*` release has passed with the new workflow.

### Docs and decisions

- New ADR `0011-pinned-raw-data-snapshots.md`: releases build from pinned raw data snapshots. It supersedes ADR 0010's "Release data" bullet, the `MML_API_KEY` parts of its "Secret" bullet, and the consequence "A release needs the `MML_API_KEY` repository secret". ADR 0010 gets a "Partly superseded by ADR 0011" line. ADR 0005's OSM bullet says `out body`.
- `docs/architecture.md`: the `snapshot` config field, the `snapshot` commands, and the data flow (local download → snapshot → release fetch).
- `docs/project-outline.md` isn't edited here, because it is exported from the outline doc. Its line "On tags: download the source data ..." is now out of date.
- `CLAUDE.md`: the data policy rule and the CI/release paragraph (no secret; snapshot fetch). `tools/mapgen/CLAUDE.md`: the data policy, the three commands, that only the user runs `publish`, and that `MML_API_KEY` is needed only for `download`.
- `CREDITS.md`: release builds get their data from the snapshot release; the dates come from its manifest.

### First snapshot

After implementation, Claude runs `mapgen snapshot create somero` on the existing `data/raw/somero/` and shows the member list, sizes, archive size, SHA-256 and `NOTICE.md`. The user reviews it and runs `publish` themselves.

### Out of scope

Pruning or deleting snapshots, multiple assets per release, snapshots of `debug_only` sources, signed archives, and reading zips directly in the build.

## Acceptance criteria

1. `snapshot create` on a fake raw folder writes a zip holding exactly the non-debug sources' manifest files, the filtered `download-manifest.json`, `SHA256SUMS` and `NOTICE.md`. Extra files in the folder and debug-only source files aren't in it.
2. `SHA256SUMS` matches every member, and two creates from the same input give byte-identical archives.
3. `NOTICE.md` contains the OSM attribution, the ODbL 1.0 URL, the "offered under the ODbL" sentence, each MML dataset attribution line from the manifest, the CC BY 4.0 URL and the changes statement.
4. The archive's OSM file has no `user`, `uid`, `timestamp`, `changeset` or `version` attributes. Its manifest entry has `source_sha256` equal to the local file's checksum and `sha256` equal to the archived file's. The local file is unchanged.
5. `snapshot create` fails with exit 1, naming the source, when an enabled non-debug source has no files, when a listed file is missing, and when a file's checksum differs from the manifest.
6. `snapshot create` fails and leaves no archive when a file contains an unredacted `api-key=` value.
7. `MapConfig` accepts a valid `snapshot` block and a config without one. It rejects a tag for another map id, an invalid date, a non-hex or wrong-length `sha256`, an asset with a path separator or `..`, a malformed `repo`, and extra keys.
8. `snapshot fetch`, with a stubbed HTTP client serving the archive and a matching `sha256`, places the files in an empty raw folder, requests the URL built from `repo`, `release_tag` and `asset`, and writes the merged manifest.
9. `snapshot fetch` with a mismatching `sha256` exits 1, names both checksums, and leaves the raw folder unchanged.
10. `snapshot fetch` without a `snapshot` block exits 2 with a message naming `snapshot publish` and `download`.
11. `snapshot fetch` exits 1, naming the asset and tag, when the stub returns HTTP 404.
12. `snapshot fetch` refuses an archive whose checksum matches but which contains `../` paths, absolute paths or members not in `SHA256SUMS`, and writes nothing.
13. Without `--force`, `snapshot fetch` fails and lists local files that differ from the snapshot and extra files in the snapshot's source folders, and leaves them untouched. With `--force`, it replaces and deletes them, and keeps the orthophoto files and their manifest entries.
14. `snapshot fetch` passes with `MML_API_KEY` unset.
15. An archive built from the committed fixture, fetched into a fresh raw folder, builds (`--area test --release`) to the same pack manifest credits as a build from the fixture directly.
16. No test calls `snapshot publish` or `gh`. The map.yaml block writer is tested as a pure function on text and keeps comments.
17. The Overpass query ends with `out body;`.
18. `release.yml` contains no `MML_API_KEY` and no `mapgen download`, runs `mapgen snapshot fetch` per map, and keeps the export job's `check-no-secret.sh dist`. `ci.yml` has `branches: ['**']` and `tags-ignore: ['data-*']` under `push`.
19. pytest, ruff and `/verify` pass, and CI passes on the change.

## Test ideas

| Criteria | Layer |
| --- | --- |
| 1–6 | pytest, `tests/test_snapshot.py`: a fake raw folder in `tmp_path` with small fake files and a hand-written manifest; one run of the real leak-check script on a planted `api-key=abc` file |
| 7 | pytest, `tests/test_config.py` |
| 8–14 | pytest: a stub `HttpClient` whose `download` copies a test-built zip or raises `HttpError(404)`; `env={}` for 14 |
| 15 | pytest: an archive built from `tests/fixtures/raw/somero-test-area/`, then a test-area build in `tmp_path` |
| 16 | pytest on the block writer |
| 17 | pytest, `tests/test_osm.py` |
| 18 | pytest text checks of the workflow files |
| 19 | `/verify`, CI |

No `Roadman.Core`, gdUnit4, screenshot or playtest coverage, because the game doesn't change.

## Open questions

None. The draft's questions were answered on 2026-10-29: strip OSM metadata in the snapshot and switch to `out body` without a re-download; tag date is the creation date (UTC); publish fails on an existing tag; fetch fails on differing local data unless `--force`, which also deletes extra files because the MML loaders read whole folders; the retry default stays 0; `ci.yml` ignores `data-*` tags; the snapshot block names its `repo`; create and publish reuse `check-no-secret.sh`.
