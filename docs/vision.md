# Vision

Source: `docs/project-outline.md`. If the two disagree, the outline wins; update this file to match.

## Concept

**Roadman Simulator: Somero-City** is a small open-world crime game, inspired by classic open-world sandbox crime games, set in an accurate model of Somero town centre. The map is generated from OpenStreetMap and Maanmittauslaitos (MML) open data.

The first goal is a drivable, accurate model of Somero centre in Godot. Gameplay comes after the map pipeline works.

## Fixed decisions

- **Map:** Somero town centre (about 2 × 2 km) is the first map pack, generated from OSM and MML open data. The tooling supports adding more maps.
- **Engine:** Godot 4, .NET build (latest stable release), scripted in C#.
- **Platforms:** macOS and Windows desktop builds.
- **Tooling:** free tools only.
- **Development:** Claude Code does most of the implementation. The project owner orchestrates, reviews and playtests.
- **Genre:** small open-world crime game.

## Scope limits for version 1

- One map area, no interiors.
- A few vehicle types and one player character.
- Simple traffic and pedestrian AI, a wanted level with police, and a handful of missions.

Anything outside these limits waits until after version 1. The main project risk is scope growing toward a full-scale commercial open world, so work proceeds one milestone at a time.

## World edge

**Premise:** in this world the Earth is flat. The map border is its edge, guarded by roadblocks on roads and barbed-wire fences everywhere else. Anything that crosses the barrier falls off the world into space and dies. The pipeline generates the barrier for every map.

Decisions:

- Barriers are easy to cross: the player can jump over them on foot or drive through them.
- Boundaries avoid water. Where a boundary has to cross water, a water fence replaces the land fence.
- Any death costs all the money and gear the player has collected.
- After death, the player respawns at a random point inside a fixed spawn area defined in the map pack.
- The same rule applies to everyone: vehicles, pedestrians and police that cross also fall.
