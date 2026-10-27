# Map pipeline (tools/mapgen)

A Python pipeline that turns a map's raw OSM and MML data into a Godot map pack in `game/maps/<map-id>/`. See `docs/architecture.md` and the ADRs 0002–0004.

## Rules

- No map-specific code. Everything about a map comes from `maps/<map-id>/`, and the map id is a CLI argument.
- Data sources go behind adapters with one shared interface (OSM, MML, global elevation model).
- Reproject to EPSG:3067 and subtract the map's local origin (ADR 0003).
- Raw downloads go to `data/raw/<map-id>/`, which is git-ignored. Never commit them.
- Write output only to `game/maps/<map-id>/`. That folder is never edited by hand.
- Name collision meshes with Godot's glTF suffixes (`-col`, `-colonly`, `-convcolonly`).
- Every override and data source records its licence, and the pipeline writes them into the pack's credits.
- Don't use MML's laser scanning data 5 p (it isn't open). The 0.5 p data is allowed only through the process `laserkeilausaineisto_05_karttalehti` (ADR 0005 amendment). Don't extract content from Google Street View.
- Ask before adding a dependency. The planned libraries are geopandas, shapely, pyproj, rasterio, numpy, osmnx or pyrosm, networkx, trimesh, and laspy with lazrs (LAZ point clouds).

## Conventions

- Python 3.12+, managed with uv. Use type hints throughout.
- Format and lint with ruff.
- Tests use pytest and a fixed small test area, so they stay fast.

## Commands

Run from `tools/mapgen/`, or from the repo root with `uv run --project tools/mapgen ...`.

```sh
uv sync                                   # install dependencies
uv run pytest                             # tests
uv run ruff check . && uv run ruff format --check .   # lint and format check
uv run mapgen download <map-id>           # download raw data, e.g. somero
uv run mapgen download <map-id> --only osm,mml_dem_2m --force   # some sources, replace existing files
uv run mapgen download <map-id> --only mml_lidar   # lidar; a map with mml_lidar enabled doesn't build without it
uv run mapgen origin <map-id>             # print the bbox centre to use as origin in map.yaml
uv run mapgen build <map-id>              # TODO: build a map pack
uv run mapgen enhance <map-id>            # TODO: merge building overrides
```

Environment variables:

- `MML_API_KEY`: MML open data API key for the MML sources. Never write it to a file. Without it, the MML sources print manual download steps and register files placed in `data/raw/<map-id>/mml/<layer>/`.
- `MAPGEN_CONTACT` (optional): contact appended to the User-Agent of every HTTP request (Overpass and MML).
