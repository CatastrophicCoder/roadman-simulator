# Architecture

This file describes the systems and how they connect. Source: `docs/project-outline.md`. Parts that are not designed yet are marked **TODO**. Decisions are recorded in `docs/decisions/`.

## Data flow

```
  OSM (Overpass / Geofabrik)   MML (DEM 2 m, orthophoto, 3D buildings, topo DB)
              \                     /
               v                   v
        tools/mapgen/data/raw/<map-id>/        (downloaded, git-ignored)
                       |
  maps/<map-id>/  -->  |  map.yaml, markers.json, overrides/
                       v
        tools/mapgen   uv run mapgen build <map-id>
                       uv run mapgen enhance <map-id>
                       |
                       v
        game/maps/<map-id>/   map pack (generated, never hand-edited)
          manifest.json, tiles/*.glb, road graph, markers,
          vegetation, boundary.json, minimap.png
                       |
                       v
        Godot: World scene -> MapLoader -> tile scenes, barriers, debug draw
                       |                         |
                       v                         v
        Roadman.Core (engine-free C#)  <-->  Gameplay nodes (C#)
          road graph, routing, wanted          vehicles, player, camera,
          level, missions, death/respawn       HUD, AI, void trigger
```

## Systems

### Map config: `maps/<map-id>/`

All map-specific input lives here, and the pipeline code contains none.

- `map.yaml`: name, bounding box, origin, CRS, tile size, data sources, style preset, playable boundary (the bounding box by default, or a custom polygon).
- `markers.json`: spawn area (polygon), parking spots, points of interest with fictional names.
- `overrides/`: per-building fixes and appearance, keyed by OSM or MML building id. Each override records its source and licence.

`map.yaml` schema (validated by `MapConfig` in `tools/mapgen/src/mapgen/config.py`; unknown keys are errors):

| Key | Meaning |
| --- | --- |
| `id` | Lowercase slug, must equal the folder name |
| `display_name` | Name shown in the game |
| `bbox_wgs84` | `lat_min`, `lat_max`, `lon_min`, `lon_max` in degrees, min < max |
| `crs` | Projected map CRS, e.g. `EPSG:3067` |
| `origin` | `x`, `y` in metres in the map CRS, inside the bbox; print it with `mapgen origin <map-id>` |
| `tile_size_m` | Tile edge in metres (> 0) |
| `test_area` | `size_m`: a square centred on the origin, inside the bbox, used by fast tests |
| `style_preset` | Style preset name (placeholder for now) |
| `sources` | Named sources, each with `adapter`, `enabled` and adapter-specific `options` |

The playable boundary is the bounding box for now. TODO: a custom boundary polygon, and the `markers.json` schema.

### Map pipeline: `tools/mapgen/`

A Python pipeline that takes a map id and produces a self-contained map pack. Data sources sit behind adapters with one interface: OSM (worldwide), MML (Finland), and a global elevation model (for example Copernicus GLO-30).

Stages, each with output that can be inspected:

1. Load and project to EPSG:3067, then subtract the local origin (ADR 0003).
2. Terrain: mesh or Terrain3D heightmap. TODO: ADR 0004 is open.
3. Buildings: extruded footprints, with attributes resolved by the enhancement layer.
4. Roads: buffered and triangulated, draped on the terrain, with bridges and layers.
5. Water and land use.
6. Vegetation: positions only.
7. Road graph: intersections and edges with class, lanes, speed limit and one-way flag.
8. Gameplay markers.
9. Tiling (256 m to start) and export: `.glb` per tile, JSON files, minimap PNG.
10. World edge: roadblocks, land and water fences, cliff edge, void trigger, written to `boundary.json`.

Building attribute precedence: manual override, then enhancement data, then OSM tags, then MML 3D, then style preset defaults.

Collision comes from glTF node name suffixes (`-col`, `-colonly`, `-convcolonly`).

Module layout (`tools/mapgen/src/mapgen/`):

- `cli.py`: `mapgen download <map-id> [--only a,b] [--force]` and `mapgen origin <map-id>`, with `--repo-root` (found automatically by default). `build` and `enhance` will be added as further subcommands.
- `config.py`: the `map.yaml` model and loader. `paths.py`: repo root, `maps/<map-id>/` and `data/raw/<map-id>/`.
- `geo.py`: WGS84 to map CRS, projected bbox, world and local coordinates, test area.
- `tm35.py`: ETRS-TM35FIN map sheet names for a bbox (used by MML adapters).
- `http.py`: a small urllib client behind a protocol, so tests can stub it.
- `sources/`: the adapter interface (`base.py`), the registry, and the adapters `osm_overpass`, `mml_dem_2m`, `mml_orthophoto`, `mml_buildings_3d` and `mml_topographic` (ADR 0005).
- `download.py` and `manifest.py`: run the enabled sources and write `data/raw/<map-id>/download-manifest.json` with the checksum, licence and attribution of each file.

Adapter interface: a source's `adapter` value selects a `SourceAdapter` class, whose options model validates the source's `options`. `download(ctx)` gets the map config, the raw folder, the `--force` flag and an injected HTTP client, and returns one `DownloadedFile` record per file.

TODO: the build stages' module layout and the `build` and `enhance` commands.

### Map pack: `game/maps/<map-id>/`

Data only: meshes, scenes and JSON, with no code (ADR 0002). Contents: tiles, road graph, markers, vegetation, minimap, `boundary.json`, and `manifest.json` with a schema version and the map's data credits. Map packs are git-ignored and regenerated by the pipeline.

TODO: manifest schema and JSON formats. TODO: shipping packs as runtime-loaded `.pck` files.

### MapLoader (Godot)

A node in the `World` scene. It reads the map id from settings or a menu, checks the manifest's schema version, and instances tiles. It loads all tiles at first and adds streaming near the camera later. It also places barrier scenes from `boundary.json`.

TODO: the streaming design, and the import presets for `.glb` files.

### Roadman.Core (engine-free C#)

A plain C# class library with no Godot dependency, tested with `dotnet test`. It contains the road graph types and parsing, routing, the wanted level, mission state, and the death and respawn state machine (including clearing money and gear).

TODO: the project structure and the public API (prompt 5).

### Gameplay (Godot C#)

- Autoloads: `Events` (global signal bus) and `GameState` (player, wanted level, settings). Both stay small.
- `World` scene: MapLoader, WorldEnvironment, sun, debug camera.
- Vehicle (`VehicleBody3D`), chase camera, HUD with minimap, input map.
- World edge: barrier scenes, the void `Area3D` and its death sequence, space backdrop.
- Missions refer to marker ids in the pack, never to raw coordinates.

TODO: traffic AI, pedestrian AI, police, missions, player character. Each of these gets a spec in `docs/specs/` before implementation.

## Testing

- Pipeline: pytest, using a fixed small test area.
- Core: xUnit on `Roadman.Core`.
- Engine: gdUnit4Net, run headless.
- Visual: headless screenshots from fixed camera positions.
- CI: builds Somero plus a tiny second test area from another town. TODO: set up CI (prompt 7).
