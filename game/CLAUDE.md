# Godot project (game)

Godot 4 .NET project `RoadmanSimulator` (Forward+ renderer, Jolt physics), scripted in C#. See `docs/architecture.md` and ADR 0001.

## Layout

- `Roadman.Core/`: engine-free C# logic (road graph, routing, wanted level, missions, death and respawn). No Godot references.
- `scenes/`, `scripts/`: Godot scenes and node scripts.
- `tests/`: engine-dependent tests (gdUnit4Net).
- `addons/`: gdUnit4, and Terrain3D if ADR 0004 chooses it.
- `maps/<map-id>/`: generated map packs. Never edit them by hand; they are git-ignored.

## Conventions

- Nullable reference types are on. TODO: `RoadmanSimulator.csproj` doesn't set `<Nullable>enable</Nullable>` yet (prompt 5).
- Declare Godot signals with `[Signal]` delegates.
- Logic that doesn't need the engine goes in `Roadman.Core`, with tests.
- Keep scenes small, and prefer building node trees in code. Hand-merged `.tscn` edits break easily.
- Use `[Export]` on anything that will be tuned.
- Autoloads (`Events`, `GameState`) stay small.
- Keep `.import` files in Git.
- Missions refer to marker ids, never to raw coordinates.

## Commands

Run these from the repo root.

```sh
dotnet build game/RoadmanSimulator.sln                       # works
dotnet test                                                  # TODO: no Roadman.Core.Tests project yet (prompt 5)
dotnet format game/RoadmanSimulator.sln --verify-no-changes  # TODO: confirm once the solution has code
godot --headless --path game --import                        # needs a wrapper, not a symlink; see "Godot binary" in the root CLAUDE.md
# TODO: gdUnit4 headless test command (add-on not installed yet)
# TODO: headless screenshot script (prompt 6)
```
