# Godot project (game)

Godot 4 .NET project `RoadmanSimulator` (Forward+ renderer, Jolt physics), scripted in C#. See `docs/architecture.md` and ADR 0001.

## Layout

- `Roadman.Core/`: engine-free C# logic (road graph, routing, wanted level, missions, death and respawn). No Godot references. Tests in `Roadman.Core.Tests/` (xUnit).
- `scenes/`, `scripts/`: Godot scenes and node scripts. `scripts/autoload/` holds `Events` (signal bus) and `GameState` (owns `PlayerState`).
- `tests/`: engine-dependent tests (gdUnit4Net). Built in `Debug` only; export builds contain no test code.
- `addons/`: gdUnit4, and Terrain3D if ADR 0004 chooses it.
- `maps/<map-id>/`: generated map packs. Never edit them by hand; they are git-ignored.

## Conventions

- All projects target `net10.0` (ADR 0007); `global.json` pins the SDK.
- Nullable reference types are on in every project.
- Test packages (gdUnit4Net, `Microsoft.NET.Test.Sdk`) are referenced only in `Debug`. `Directory.Build.targets` keeps the game assembly a library.
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
dotnet build game/RoadmanSimulator.sln                       # all projects, Debug
dotnet test game/Roadman.Core.Tests                          # Roadman.Core unit tests (xUnit)
godot --headless --path game --import                        # needs a wrapper, not a symlink; see "Godot binary" in the root CLAUDE.md
GODOT_BIN=/Applications/Godot_mono.app/Contents/MacOS/Godot \
  dotnet test game/RoadmanSimulator.csproj --settings game/.runsettings  # gdUnit4 tests in tests/; run the import first
dotnet format game/RoadmanSimulator.sln --verify-no-changes  # format check
godot --headless --path game --quit-after 120                # runs the main scene (World.tscn) headless
dotnet build game/RoadmanSimulator.csproj -c ExportRelease   # release assembly, no test code; output in game/.godot/mono/temp/bin/ExportRelease/
# TODO: headless screenshot script (prompt 6)
```

`GODOT_BIN` must point at the real Godot .NET binary, not a symlink. The gdUnit4 adapter writes its runner scene to `game/gdunit4_testadapter_v*/` on each run (git-ignored).
Building with `-c ExportRelease` or `ExportDebug` re-restores the shared `obj/` without test packages; the next Debug `dotnet build` or `dotnet test` restores them again, so don't pass `--no-restore` after switching configuration.
