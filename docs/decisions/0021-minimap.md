# 0021: Minimap

Status: Accepted

## Context

The project outline asks for a minimap centred on the player that turns with the heading, with roads from the road graph, street names and points of interest. Phase 3 stage 9 plans a "minimap PNG" in the pack. The game already reads the road graph, the places and the boundary, but nothing draws buildings, water or land use from above. Spec: `docs/specs/minimap.md` (decided 2026-11-08).

## Decision

- **Split between the pipeline and the game.** The pipeline writes a top-down background image of the slow-changing area data: the ground inside the boundary, `landuse` groups, water and building footprints. It has no roads, labels or symbols. The game draws everything that needs to stay sharp or that the game reads anyway live on top of it: the boundary ring and roadblocks, the roads from `road_graph.json`, Finnish street names, point-of-interest symbols and markers. The image turns with the map; text and symbols stay upright.
- **Files.** `minimap.png` (RGBA, 8 bits per channel), `minimap.png.import` and `minimap.json` (schema `minimap-v1`) in the pack root, written by every build and not listed in the manifest.
- **Grid.** The image covers the union of the written tiles' bounds in local x/z at `minimap.m_per_px` (default 1.0 m per pixel, 0.5 to 2.0 allowed), with the top row north and the left column west. `minimap.json`'s `bounds_local` are the outer pixel edges, so the game places the image from the JSON alone.
- **Transparency.** Pixels whose centre is outside the boundary ring have alpha 0 and pixels inside have alpha 255, so the game's void colour shows past the world edge without the image knowing it. Every layer is clipped to the ring.
- **Water source.** The image's water is the world-edge step's water (ADR 0015): the MML Topographic Database's `jarvi` and `virtavesialue`, plus `virtavesikapea` lines buffered to `minimap.stream_width_m`, when a source provides `water_areas`; otherwise the OSM water areas, without lines. The step reuses the world-edge step's loaded water, so the edge's water fences and the minimap never disagree.
- **Rasterising.** `rasterio.features.rasterize` (pixel-centre rule, no anti-aliasing) and GDAL's PNG driver, which the pipeline already depends on. The output is deterministic.
- **Import: kept as a raw file and loaded at runtime.** The pipeline writes `minimap.png.import` with exactly `[remap]` and `importer="keep"`, and a pack check verifies that content. The game reads the PNG's bytes at runtime, builds an `Image`, generates mipmaps (so it shrinks cleanly when rotated) and makes an `ImageTexture`. Tested with `--export-pack` before deciding:
  - A PNG left to Godot's default import becomes a texture, and the export then holds only the compressed `.ctex`, not the PNG, so runtime loading from the file fails in an exported game. Changing the project's texture import defaults would affect every texture in the project instead.
  - A PNG in a `.gdignore`d folder (as the debug orthophoto textures are) isn't exported at all, even with an `include_filter`.
  - With `importer="keep"`, Godot leaves the `.import` file as written and exports the raw PNG unchanged. The file is three lines and holds no version-specific import parameters.
- **Missing or invalid data.** `MapLoader` reads `minimap.json` strictly (`MinimapImageReader`) and pushes an error naming the path and `/regen-map <id>` for a missing or invalid JSON, a missing image or a size mismatch. The minimap then still draws: the inside of the ring in the ground colour, and the other layers from whatever data the pack has.

## Consequences

- The art-direction palette changes the pipeline's colour table (later the style preset) and the game's `UiTheme` constants; the background needs a pack rebuild, the live layers don't.
- Water and land use meshes in the 3D world (Phase 3 stage 5) can reuse the same layers and should keep the same water source.
- Zoom levels larger than the image's resolution allows need a smaller `m_per_px`; the game reads the scale from `minimap.json`.
- Any later raw file the game reads at runtime from a pack uses the same `importer="keep"` pattern and an export check.
