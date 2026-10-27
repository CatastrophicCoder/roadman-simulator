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
- Don't use MML's laser scanning data 5 p (it isn't open). Don't extract content from Google Street View.
- Ask before adding a dependency. The planned libraries are geopandas, shapely, pyproj, rasterio, numpy, osmnx or pyrosm, networkx, trimesh.

## Conventions

- Python 3.12+, managed with uv. Use type hints throughout.
- Format and lint with ruff.
- Tests use pytest and a fixed small test area, so they stay fast.

## Commands

All are TODO: there is no `pyproject.toml` yet (prompt 3).

```sh
uv sync                              # TODO: install dependencies
uv run pytest                        # TODO: tests
uv run ruff check . && uv run ruff format --check .   # TODO: lint and format check
uv run mapgen build <map-id>         # TODO: build a map pack, e.g. somero
uv run mapgen enhance <map-id>       # TODO: merge building overrides
```
