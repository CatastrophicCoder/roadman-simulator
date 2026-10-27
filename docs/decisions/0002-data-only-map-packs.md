# 0002: Map packs are data-only and map-agnostic

Status: Accepted

## Context

Somero is the first map, but the tooling should support more towns, including towns outside Finland. Building appearance improves over time through overrides, and that must not require changing the base data or the code.

## Decision

- All map-specific input lives in `maps/<map-id>/` (`map.yaml`, `markers.json`, `overrides/`). The pipeline takes the map id as input and contains no map-specific code.
- Data sources sit behind adapters with one interface (OSM, MML, a global elevation model).
- The pipeline outputs a self-contained map pack in `game/maps/<map-id>/` with only meshes, scenes and JSON, plus a `manifest.json` that holds a schema version and data credits. All C# code stays in the main game.
- Missions refer to marker ids inside the pack, never to raw coordinates.

## Consequences

- A new town needs a config folder, not new code.
- The game must check the manifest's schema version when it loads a pack.
- Packs could later ship as runtime-loaded `.pck` files without a new game build.
- CI builds a second tiny test area from another town so that Somero-specific code is caught early.
- Generated packs aren't edited by hand. Because they are regenerated, they are kept out of Git.
