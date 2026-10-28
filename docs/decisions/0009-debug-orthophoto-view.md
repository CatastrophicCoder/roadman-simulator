# 0009: Debug orthophoto view

Status: Accepted

## Context

Checking the generated map against reality during a flyover is easier with aerial imagery on the terrain. MML orthophotos (CC BY 4.0) are already downloaded for Somero. The game's look will come from a low-poly palette (art direction), not from photos, so the imagery is a debug aid only and must not ship.

## Decision

- **Pipeline.** The terrain mesh in every tile gets UVs that map the tile's bounds onto [0,1]², north at the top of the image. When the `mml_orthophoto` source is enabled and its files are present, the build writes one JPEG per tile to `game/maps/<map-id>/debug/orthophoto/<tile_id>.jpg`, resampled to `debug_texture_m_per_px` (0.5–1.0 m, default 0.5), and an empty `debug/.gdignore`. The textures aren't listed in the manifest; the orthophoto's credit is, as for every source the build reads (ADR 0006).
- **Not in release builds.** Because of `.gdignore`, Godot neither imports nor exports anything under `debug/`. The game loads the JPEGs from disk with `Image.LoadFromFile`, so they work in editor and development runs only. The view itself is added only when `OS.IsDebugBuild()`. No export filter may include `maps/*/debug/`.
- **Game.** F2 (`debug_orthophoto`) switches every tile's terrain between the map material and an unshaded material with that tile's image, loaded and mipmapped on first use. While the view is on, an overlay shows the attribution and licence of the pack's `mml_orthophoto` credits, read from the manifest, never hard-coded. The screenshot tool can capture the view with `--orthophoto`.

## Consequences

- A missing orthophoto source doesn't fail the build; the pack just has no debug textures, and the view says so.
- About 6.6 MB of JPEGs per Somero pack on disk and roughly 100–140 MB of texture memory while the view is on.
- The terrain carries UVs from now on, which the art-direction work can also use.
