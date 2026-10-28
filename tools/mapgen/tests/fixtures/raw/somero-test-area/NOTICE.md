# Test-area raw data for `somero`

Raw data cut to the map's test area, laid out like `tools/mapgen/data/raw/somero/`. Used by the tests and to build the test-area map pack.

## Attribution and licences

- Contains data from the National Land Survey of Finland Elevation model 2 m, 10/2026. Licence: [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
- Contains data from the National Land Survey of Finland Laser scanning data 0,5 p, 10/2026. Licence: [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
- Contains data from the National Land Survey of Finland Orthophoto, 10/2026. Licence: [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
- © OpenStreetMap contributors (https://www.openstreetmap.org/copyright). Licence: [ODbL 1.0](https://opendatacommons.org/licenses/odbl/1-0/).

## How it was produced

`uv run python scripts/cut_test_area.py somero` (in `tools/mapgen/`) cut the downloaded files listed in `download-manifest.json` to what a test-area build reads: OSM building footprints whose centroid is in the test area with their ways and nodes (contributor metadata removed), the elevation model around the test area, and the laser scanning points around those footprints, and the orthophoto over the test area (re-encoded lossy to keep it small). Each manifest entry records the cut bounds and the checksum of the original file.
