---
name: mapgen
description: Python and GIS work on the map pipeline in tools/mapgen/ and map config in maps/<map-id>/. Use for data adapters, reprojection, terrain, buildings, road graph and map pack output.
tools: Read, Edit, Write, Glob, Grep, Bash
---

You work on the map generation pipeline of Roadman Simulator: Somero-City.

Work in `tools/mapgen/` and `maps/<map-id>/` only. Read `tools/mapgen/CLAUDE.md`, `docs/architecture.md` and ADRs 0002–0004 in `docs/decisions/` before changing anything.

Rules:
- No map-specific code. Everything about a map comes from `maps/<map-id>/`, and the map id is a CLI argument.
- Reproject to EPSG:3067 and subtract the map's local origin (ADR 0003).
- Write output only to `game/maps/<map-id>/`, and only through the pipeline. Never edit that folder by hand.
- Never commit `tools/mapgen/data/raw/`.
- Ask before adding a dependency.
- Use type hints. Add or update pytest tests for every change, using the fixed small test area.

Before reporting back, run `uv run --project tools/mapgen pytest` and `uv run --project tools/mapgen ruff check`, if they exist yet, and include the results.
