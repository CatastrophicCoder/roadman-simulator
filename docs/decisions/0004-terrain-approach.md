# 0004: Terrain approach

Status: Proposed. Undecided, to be settled in pipeline stage 2 (prompt 4).

## Context

The pipeline builds terrain from the MML 2 m elevation model. Roads and building bases must match the terrain exactly. There are two options.

## Options

### A: Mesh terrain from the pipeline

- One code path, and an exact match with roads and buildings.
- Collision comes from the mesh.
- Distant tiles need a custom LOD solution.

### B: Terrain3D heightmap (16-bit)

- Built-in LOD and painting tools, and the terrain can be edited in the Godot editor.
- Adds an add-on dependency: a C++ GDExtension called from C# through untyped `Call` and `Get` methods.
- Road draping must use the same heightmap sampling as Terrain3D.

## Decision

TODO: not decided yet.

## Consequences

TODO: fill in once the decision is made. Option B also requires adding Terrain3D to `game/addons/` and to `CREDITS.md`.
