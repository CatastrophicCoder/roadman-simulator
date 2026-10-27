# Credits

## Map data

### OpenStreetMap

Map data © OpenStreetMap contributors, available under the Open Database License (ODbL) 1.0.
https://www.openstreetmap.org/copyright

| Dataset | Source | Date |
| --- | --- | --- |
| OpenStreetMap data for the map bbox | Overpass API (overpass-api.de), `uv run mapgen download <map-id>` | 2026-10-27 |

### Maanmittauslaitos (National Land Survey of Finland)

Contains data from the National Land Survey of Finland, licensed under CC BY 4.0.
https://creativecommons.org/licenses/by/4.0/

Each dataset is listed with its source, name and download date, as MML's terms require. The attribution form is "Contains data from the National Land Survey of Finland <dataset>, <MM/YYYY>". `tools/mapgen/data/raw/<map-id>/download-manifest.json` records the exact date of each file.

| Dataset | Source | Date |
| --- | --- | --- |
| Elevation model 2 m | MML file service (OGC API Processes, `korkeusmalli_2m_bbox`) or Karttapaikka | 2026-10-27 |
| Orthophoto | MML file service (`ortoilmakuva_karttalehti`) or Karttapaikka | 2026-10-27 (latest year version) |
| 3D buildings (if available for Somero) | MML file service (`3d-rakennukset_karttalehti`) | TODO: not downloaded yet |
| Topographic database (if used) | MML file service (`maastotietokanta_bbox`) | TODO: not downloaded yet |

Each map pack's `manifest.json` also lists its own data credits.

## Engine

This game uses the Godot Engine, available under the MIT licence.
Copyright (c) 2014-present Godot Engine contributors. Copyright (c) 2007-2014 Juan Linietsky, Ariel Manzur.
https://godotengine.org/license

TODO: include the full MIT text and Godot's third-party licences in the game (Godot can output them at runtime).

## Add-ons

TODO: none yet. Record the name, version, licence and URL of each add-on (for example gdUnit4, and Terrain3D if used).

## Assets

TODO: none yet. Record the source, author and licence of each texture, model and sound. Keep CC BY-SA textures in a separate, labelled folder.
