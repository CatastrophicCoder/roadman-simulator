# Roadman Simulator: Somero-City

A small open-world crime game in Godot 4 (.NET, C#) set in an accurate model of Somero town centre. A Python pipeline generates the map from OpenStreetMap and Maanmittauslaitos (MML) open data. Targets: macOS and Windows desktop. Free tools only.

The first goal is a drivable Somero centre. Gameplay comes after the map pipeline works.

Version 1 scope: one map area with no interiors, a few vehicle types, one player character, simple traffic and pedestrian AI, a wanted level with police, and a handful of missions. Don't add anything beyond this. See `docs/vision.md`.

## Repo map

| Path | Contents |
| --- | --- |
| `docs/project-outline.md` | Source of truth for the project plan |
| `docs/vision.md` | Concept, fixed decisions, scope limits |
| `docs/architecture.md` | Systems and data flow (check plans against it) |
| `docs/specs/` | One spec per feature, approved before coding |
| `docs/decisions/` | ADRs; follow them |
| `maps/<map-id>/` | All map-specific input: `map.yaml`, `markers.json`, `overrides/` |
| `tools/mapgen/` | Python map pipeline, no map-specific code. See `tools/mapgen/CLAUDE.md` |
| `game/` | Godot project and C# solution. See `game/CLAUDE.md` |
| `game/maps/<map-id>/` | Generated map packs (pipeline output, git-ignored) |
| `.claude/` | Claude Code settings, commands, agents |
| `CREDITS.md` | Data, engine, add-on and asset attributions |

## Commands

Build and test commands are listed in `tools/mapgen/CLAUDE.md` and `game/CLAUDE.md`. TODO: add `/verify` once it exists (prompt 2).

## Definition of done

- Tests pass (pytest, `dotnet test`, gdUnit4, whichever apply).
- Linters are clean (`ruff`, `dotnet format --verify-no-changes`).
- A headless Godot run finishes without errors.
- For visual changes, a screenshot has been taken and checked.

## Rules

- Never edit `game/maps/` by hand. Change the pipeline or `maps/<map-id>/` and regenerate.
- Never commit raw data (`tools/mapgen/data/raw/`).
- Ask before adding any dependency (NuGet, Python package, Godot add-on, GitHub Action).
- No other games' names or trademarks in code, assets or text.
- Real business names and logos from OSM are replaced with fictional ones.
- Record new decisions as ADRs in `docs/decisions/`.
- Record the licence of every new asset or add-on in `CREDITS.md`.
- Commit messages and pull request descriptions describe only the change itself. No `Co-Authored-By` trailers, "Generated with" lines or any other mention of Claude or AI tools.
