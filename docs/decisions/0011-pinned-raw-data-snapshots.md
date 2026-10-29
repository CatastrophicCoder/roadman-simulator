# 0011: Release builds from pinned raw data snapshots

Status: Accepted

## Context

Under ADR 0010, every tagged release downloaded the map data again from Overpass and the MML file service. That made releases depend on two external services: a busy Overpass server failed a release (ADR 0005 amendment, 2026-10-29), and the MML download needed the personal `MML_API_KEY` as a repository secret. Each release could also be built from different data than the one before, without anyone reviewing it first. Spec: `docs/specs/map-data-snapshots.md`.

## Decision

- **Data policy.** External data is fetched once. The user downloads a map's data locally with `mapgen download`, and only when they ask for it. Builds, `/verify`, `/regen-map` and `snapshot create` use only `tools/mapgen/data/raw/<map-id>/` and never contact Overpass or MML.
- **Snapshots.** `mapgen snapshot create <map-id>` packs the raw files of every enabled, non-`debug_only` source into one zip, together with the filtered `download-manifest.json`, a `SHA256SUMS` file and a `NOTICE.md` generated from the manifest (OSM under ODbL 1.0, offered as a derivative database under the ODbL; MML under CC BY 4.0 with exact dataset names and dates). The OSM file's contributor metadata (`user`, `uid`, `timestamp`, `changeset`, `version`) is removed in the snapshot, the manifest entry records the original file's `source_sha256`, and the NOTICE states the change. The archive is deterministic. `.github/scripts/check-no-secret.sh` checks it before it is kept.
- **Storage.** `mapgen snapshot publish <map-id>`, run only by the user, uploads the zip as the single asset of a GitHub release tagged `data-<map-id>-<YYYY-MM-DD>` (creation date, UTC), created with `--latest=false`. It fails if the tag exists. It writes `repo`, `release_tag`, `asset` and `sha256` into the `snapshot` block of `maps/<map-id>/map.yaml`, which `MapConfig` validates.
- **Releases.** `release.yml` runs `mapgen snapshot fetch <map-id>` for every map instead of `mapgen download`. Fetch downloads the asset from the public release URL without credentials, checks its SHA-256 against `map.yaml` before opening it, checks every member against `SHA256SUMS`, and only then unpacks it. The pipeline code of the release commit builds the pack from it, so pipeline fixes reach every release while the data stays fixed. A map without a snapshot block fails the release.
- **Local data is protected.** `snapshot fetch` is for fresh clones and CI. It fails and lists the files if local files of the snapshot's sources differ from the snapshot, or if those sources' folders hold extra files. With `--force`, it replaces the differing files and deletes the extra ones (the MML loaders read every file in their layer folder), so a local build matches the release. Other sources, such as the debug-only orthophoto, are never touched.
- **Workflows.** No workflow uses `MML_API_KEY`. The maps job's literal-mode leak check, which needed the key, is removed; the export job's pattern check on the release files stays. `ci.yml` ignores `data-*` tags (`branches: ["**"]` with `tags-ignore: ["data-*"]`) and keeps building from the committed test-area fixture.
- **Overpass.** Future downloads use `out body`, so the raw OSM file has no contributor metadata. `download --retries` keeps its default of 0.

This supersedes ADR 0010's "Release data" bullet, the `MML_API_KEY` parts of its "Secret" bullet, and its consequence that a release needs the `MML_API_KEY` repository secret.

## Consequences

- Releases no longer depend on Overpass or MML, and the `MML_API_KEY` repository secret can be deleted once a snapshot is published and a release has passed with this workflow.
- New data reaches releases only through a new snapshot that the user reviews, publishes and commits in `map.yaml`. Data releases stay in the repository's release list, but never as the latest release.
- A snapshot is about 90 MB for Somero today, mostly lidar. GitHub allows up to 2 GiB per release asset.
- The snapshot's OSM file differs from what Overpass returned; the original is identified by `source_sha256`.
- Snapshots are fetched from the repository named in `map.yaml`, so a fork builds from the upstream data unless it changes that field.
