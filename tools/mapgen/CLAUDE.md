# Map pipeline (tools/mapgen)

A Python pipeline that turns a map's raw OSM and MML data into a Godot map pack in `game/maps/<map-id>/`. See `docs/architecture.md` and the ADRs 0002–0004.

## Rules

- No map-specific code. Everything about a map comes from `maps/<map-id>/`, and the map id is a CLI argument.
- Data sources go behind adapters with one shared interface (OSM, MML, global elevation model).
- Reproject to EPSG:3067 and subtract the map's local origin (ADR 0003).
- Raw downloads go to `data/raw/<map-id>/`, which is git-ignored. Never commit them.
- External data is fetched once (ADR 0011). Builds, tests, `/verify`, `/regen-map` and `snapshot create` read only `data/raw/<map-id>/` and never contact Overpass or MML. Run `mapgen download` or any other external data fetch only when the user explicitly asks. `snapshot fetch` is for fresh clones and CI and never replaces local data without `--force`. Only the user runs `snapshot publish`; never run it without asking first.
- Write output only to `game/maps/<map-id>/`. That folder is never edited by hand.
- Name collision meshes with Godot's glTF suffixes (`-col`, `-colonly`, `-convcolonly`).
- Every override and data source records its licence, and the pipeline writes them into the pack's credits.
- Don't use MML's laser scanning data 5 p (it isn't open). The 0.5 p data is allowed only through the process `laserkeilausaineisto_05_karttalehti` (ADR 0005 amendment). Don't extract content from Google Street View.
- Road widths, lanes, speeds, drivable and walkable flags and bridge sizes come only from the `roads` block in `map.yaml` (ADR 0012); OSM values win, and each edge records its value sources. Road surfaces follow the terrain mesh triangles; never change the terrain under a bridge deck.
- Every build runs the pack checks (`build/pack_checks.py`) on the staged pack and fails on any failed check, also with `--release`. Add a check there when a pack gains a new guarantee, rather than only in a test.
- The test-area fixture holds the buildings, highway ways, water, land use and points of interest touching the test area, and a GPKG with the Topographic Database `rakennus` records intersecting it, cut from local raw data by `scripts/cut_test_area.py` with contributor metadata stripped.
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
uv run mapgen download <map-id>           # download raw data, e.g. somero (only when the user asks)
uv run mapgen download <map-id> --only osm,mml_dem_2m --force   # some sources, replace existing files
uv run mapgen download <map-id> --only mml_lidar   # lidar; a map with mml_lidar enabled doesn't build without it
uv run mapgen download <map-id> --release --strict   # skip debug_only sources; fail if a source
                                          #   gets no files or MML_API_KEY is missing
uv run mapgen download <map-id> --only osm --retries 3   # retry a busy Overpass (HTTP 429, 502, 503,
                                          #   504) after 30, 60, 120 s or its Retry-After; default 0
uv run mapgen origin <map-id>             # print the bbox centre to use as origin in map.yaml
uv run mapgen build <map-id>              # build the map pack (--area test: the small test area)
uv run mapgen build <map-id> --release    # skip debug_only sources: no debug/orthophoto/ in the pack
uv run mapgen notice <map-id>... --out build/NOTICE.txt   # plain-text NOTICE from the packs' manifest credits
uv run mapgen notice --check build/NOTICE.txt <map-id>... # exit 1 if any credit or licence line is missing
uv run mapgen snapshot create <map-id>    # zip the non-debug raw data into build/snapshots/ (no network)
uv run mapgen snapshot publish <map-id>   # USER ONLY: new gh release data-<map-id>-<date>[.<n>] (first free
                                          #   tag; never changes an existing one), writes map.yaml snapshot
uv run mapgen snapshot fetch <map-id>     # download the pinned snapshot into data/raw/<map-id>/ (fresh clones, CI)
uv run mapgen snapshot fetch <map-id> --force   # also replace differing and delete extra local files of its sources
uv run mapgen enhance <map-id>            # TODO: merge building overrides
```

Environment variables:

- `MML_API_KEY`: MML open data API key for the MML sources, needed only by `download` (releases use snapshots and no key). Never write it to a file. Without it, the MML sources print manual download steps and register files placed in `data/raw/<map-id>/mml/<layer>/`; with `--strict` they fail instead.
- `MAPGEN_CONTACT` (optional): contact appended to the User-Agent of every HTTP request (Overpass and MML).
