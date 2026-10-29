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
| Laser scanning data 0,5 p | MML file service (`laserkeilausaineisto_05_karttalehti`, latest dataset) or Karttapaikka | 2026-10-27 |
| Orthophoto | MML file service (`ortoilmakuva_karttalehti`) or Karttapaikka. Used only for the debug orthophoto view; the textures are never exported with the game (ADR 0009) | 2026-10-27 (latest year version) |
| Buildings 3D (not available for Somero) | MML file service (`3d-rakennukset_karttalehti`) | Not used: no coverage for Somero |
| Topographic Database (if used) | MML file service (`maastotietokanta_bbox`) | TODO: not downloaded yet |

Dataset names follow MML's product descriptions (https://www.maanmittauslaitos.fi/en/maps-and-spatial-data/datasets-and-interfaces/product-descriptions), checked on 2026-10-27.

Each map pack's `manifest.json` also lists its own data credits.

### Test fixture

`tools/mapgen/tests/fixtures/raw/somero-test-area/` contains cuts of the OpenStreetMap data (ODbL 1.0, contributor metadata removed), MML's Elevation model 2 m, Laser scanning data 0,5 p and a reduced-quality orthophoto cut (CC BY 4.0) for the Somero test area. Its `NOTICE.md` gives the attributions.

## Pipeline tools

Python libraries used by the map pipeline (`tools/mapgen/`). They are not shipped with the game.

| Library | Licence | URL |
| --- | --- | --- |
| laspy | BSD-2-Clause | https://github.com/laspy/laspy |
| lazrs (lazrs-python) | MIT | https://github.com/laz-rs/laz-rs-python |

## Engine

This game uses the Godot Engine, available under the MIT licence.
Copyright (c) 2014-present Godot Engine contributors. Copyright (c) 2007-2014 Juan Linietsky, Ariel Manzur.
https://godotengine.org/license

Each release zip has a `licenses/` folder with the third-party licence texts of the exact versions in that build (`.claude/scripts/collect-licenses.py`, ADR 0010):

- `godot-<version>/`: Godot's `LICENSE.txt`, `COPYRIGHT.txt` (the engine's third-party components with their licence texts) and `AUTHORS.md`, from the Godot repository at the release commit.
- `<package>-<version>/` for every NuGet package the export ships: the .NET runtime packs (`Microsoft.NETCore.App.Runtime.<rid>`, MIT, with `THIRD-PARTY-NOTICES.TXT`) and `GodotSharp` (MIT; the package has no licence file, so it gets Godot's `LICENSE.txt` from the same commit).
- `README.txt`: what each folder is and where it came from.

## Add-ons

Record the name, version, licence and URL of each add-on.

| Add-on | Version | Licence | URL |
| --- | --- | --- | --- |
| gdUnit4 (`game/addons/gdUnit4/`) | 6.2.2 | MIT, Copyright (c) 2023 Mike Schulze | https://github.com/godot-gdunit-labs/gdUnit4 |

## Test libraries

NuGet packages used only by the tests. They are referenced in Debug builds only and are not shipped with the game.

| Package | Version | Licence | URL |
| --- | --- | --- | --- |
| gdUnit4.api, gdUnit4.test.adapter, gdUnit4.analyzers (gdUnit4Net) | 5.1.0-rc6, 3.2.0, 1.1.0 | MIT | https://github.com/godot-gdunit-labs/gdUnit4Net |
| xUnit (xunit, xunit.runner.visualstudio) | 2.9.3, 3.1.5 | Apache-2.0 | https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk | 18.11.0 | MIT | https://github.com/microsoft/vstest |

## Assets

TODO: none yet. Record the source, author and licence of each texture, model and sound. Keep CC BY-SA textures in a separate, labelled folder.
