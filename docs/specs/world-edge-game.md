# Spec: world edge (game)

## Status

Approved

## Goal

Make the world edge that the pipeline writes to `boundary.json` (ADR 0015) work in the game. Roadblocks, land fences and water fences stand on the boundary. They are low enough to jump over and break into loose physics pieces when a vehicle drives through them. A void trigger just beyond them starts a death sequence: the player is pulled off the slab, the camera pulls back to show the floating world against a starfield, a death screen follows, and the player respawns in the spawn area without money or gear. Covers "World edge → Game (Phase 5)" in `docs/project-outline.md` and milestone 2's "world-edge death and respawn". It also replaces the temporary fall respawn from `docs/specs/player-on-foot.md`.

Out of scope (later specs): vehicles and the car-through-roadblock test with a real car (vehicles spec), traffic, pedestrian and police AI and their routing at the edge, water surfaces (stage 5; water fences stay at terrain height), the art-direction palette, save and load, a money or gear HUD, item display names (inventory spec), and any pipeline or `boundary.json` change.

## Context (2026-11-04)

- `MapLoader.Boundary` (`Roadman.Core.Maps.Boundary`, read by `BoundaryReader`) already holds the whole file: `roadblocks`, `fences`, `cliff`, `void_trigger` and `spawn_area`. The game uses only `spawn_area` and `cliff.bottom_y` so far.
- `PlayerState.Die()` clears money and gear, resets the wanted level and raises `Died`, which `GameState` forwards as `Events.PlayerDied`. It leaves `Position` unchanged. Nothing calls it yet.
- `World` spawns the player with `SpawnPicker` (ray and capsule checks, `SpawnMaxAttempts`, `--seed`) and has the temporary fall respawn: `FallReset.IsBelow`, `World.FallRespawnDepth`, `World.RespawnPlayer()` and the `PlayerRespawned` signal. References: `game/Roadman.Core/Player/FallReset.cs`, `game/Roadman.Core.Tests/Player/FallResetTests.cs`, `game/scripts/world/World.cs`, `game/tests/PlayerOnFootTest.cs`, `game/CLAUDE.md`, `docs/architecture.md`, `docs/specs/player-on-foot.md`.
- The sky is a `ProceduralSkyMaterial` day sky, which is also the source of ambient and reflected light (`World.CreateEnvironment`).
- Collision layers: map bodies on layer 1 (`CollisionLayers.Map`), the player on layer 2 with mask 1. The camera's spring arm and the map rays use mask 1 (ADR 0016: colliders that the player and the camera must both hit go on layer 1, anything else picks its own layer).
- `Player.InputEnabled` and `PlayerCamera.InputEnabled` already switch input off, used by the debug camera toggle.
- Measured in the full Somero pack's `boundary.json` (boundary B):

  | Item | Value |
  | --- | --- |
  | Ring | 10,560 m, 184 points |
  | Roadblocks | 41 (34 drivable), spans 4.0 to 36.2 m, 450 m in all; classes residential 18, tertiary 5, cycleway 5, living_street 3, track 3, footway 2, primary 2, unclassified 2, driveway 1; widths 2.0 to 8.0 m; none `over_water`; up to 3 nodes each |
  | Fences | 764 (747 land, 17 water), 0.05 to 19.98 m long, 10,110 m in all (water 321 m); 6 under 1 m; slope up to 25.2° (95th percentile 11.1°); ends at y 81.8 to 133.3 |
  | Cliff | `ledge_m` 2.0, `bottom_y` 51.8 |
  | Void trigger | 1,177 prisms, `bottom_y` 31.8, `top_y` 153.4 |

- **Roadblocks are not straight in height.** No roadblock span bends in plan (every span is a straight chord), but the ground under a span isn't the line between its `start` and `end` heights. On road 52's south roadblock (`roadblock:cut:way/1066989655:0`, 36.2 m), `position` is 3.39 m above the midpoint of that line; on a tertiary roadblock 0.77 m. The span ends are on the terrain beside the road (1 m of clearance), so a rigid barrier from `start` to `end` would sink into the road. Fences don't have this problem: the pipeline keeps the terrain within 0.25 m of each fence line.

## Behaviour

### Roadman.Core

- **`EdgeGeometry`** (from `Boundary`):
  - `IsInVoid(point)`: whether a point lies inside any void prism (footprint and `bottom_y`..`top_y`).
  - `OutwardAt(x, z)`: the unit horizontal vector from the nearest point on the boundary ring to `(x, z)`. It is the "outward" direction of the pull and the camera. Ties (two ring points equally near) go to the lower ring distance `s`.
  - `NearestRingPoint(x, z)`: that point and its `s`.
  - `IsBelowSafetyNet(y, margin)`: true when `y < cliff.bottom_y − margin` (see "Safety net").
- **`BarrierLayout`** turns the roadblocks and fences into piece placements, with no engine types: for each item a list of pieces (kind, local transform along the item, size), from the item's `start`, `end`, `outward` and the piece rules under "Barriers". Roadblock pieces carry only x/z; the engine puts them on the ground. Exports of the barrier nodes are passed in, as `OnFootMotion` takes the player's.
- **`VoidPull`**: the acceleration that replaces gravity for a falling body: `outward × OutwardAcceleration + down × DownAcceleration`. Parameters come from exports.
- **`DeathSequence`**, the death and respawn state machine (outline: "Code split"):
  - States: `Alive`, `Falling`, `DeathScreen`, `Respawning`, `Failed`.
  - `EnterVoid(entry)` in `Alive` records the losses (`Losses`: the money and the gear item ids the player has), then calls `PlayerState.Die()` and moves to `Falling`. So `Die()` runs at the moment the player enters the void (decided). In any other state it does nothing, so a body can't die twice in one sequence.
  - `Advance(dt)`: `Falling` lasts `FallTime`, then `DeathScreen`; `DeathScreen` lasts `ScreenTime`, then `Respawning`. A negative or non-finite `dt` is an `ArgumentException`.
  - `CompleteRespawn(success)` in `Respawning` moves to `Alive` or `Failed`; in another state it is an `InvalidOperationException`.
  - `Reset()` returns to `Alive` from any state (map reload).
  - Events: `StateChanged(from, to)`. Only state changes raise it.
- **`DeathScreenText.Format(losses)`** gives the death screen's three lines (see "Death screen").
- `FallReset` and its tests are removed.

### Barriers

Three reusable barrier types, built in code (`game/CLAUDE.md`: prefer node trees built in code), under `scripts/world/edge/`: `Roadblock`, `LandFence` and `WaterFence`. `MapLoader` places them after it reads `Boundary`, under `MapLoader/Edge/Barriers`, one item per `boundary.json` roadblock or fence, named after its `id`. A null `Boundary` places none (the error is already pushed). A map reload frees and rebuilds them.

**Look** (decided; low-poly, flat-shaded, built in code from primitives, no assets):

- **`Roadblock`**: light red-and-white barrier boards on stands, across the span.
- **`LandFence`**: posts with barbed-wire strands.
- **`WaterFence`**: posts with a line on floats.

Each type uses placeholder flat-colour materials in `materials/placeholder/`, like the other placeholders, until the palette exists (the roadblock's red and white are part of its look and stay).

**Static until hit** (decided). Each item is one `StaticBody3D` with the item's collision shapes. Its visuals are batched: one `MultiMeshInstance3D` per piece mesh and barrier type for the whole map, not a node per piece. When a vehicle hits an item at `BreakSpeed` or more, the item breaks: its static body is removed, its instances are hidden in the batches, and loose `RigidBody3D` pieces (the same meshes, with their own collision) take their place with the breaking body's velocity at the contact. Items that aren't hit cost no physics simulation.

Rules for every barrier type (from ADR 0015 and ADR 0016):

- **Height:** no piece reaches more than **0.9 m** above its base: a fence piece above the fence's straight line from `start` to `end` at that point, a roadblock piece above the ground under it.
- **Outward reach:** no mesh or collision shape of an unbroken item reaches more than **1.0 m** outward from the boundary (`void.inner_offset_m`), so no item starts inside the void trigger.
- **No walls hit:** no item overlaps a `map_buildings` body. The pipeline keeps buildings 2.0 m inside (`building_clearance_m`), so a barrier's inward half-depth stays under that.
- **Closed:** the items' collision, projected on the ring, covers it with no gap wider than 0.1 m, so the player can't walk through anywhere, including fence-to-roadblock joints and ring corners. A roadblock covers its whole span (`s_start`..`s_end`), shoulder clearance included.
- **Jumpable:** the player clears any barrier from a run-up (ADR 0016: 1.3 m apex against 0.9 m plus the fence line's 0.25 m above the ground).

Per type:

- **`LandFence`**: posts stand vertical on the line from `start` to `end`, at both ends of a run and at most `PostSpacing` apart. Where two items meet, one post is shared, so short segments (down to 0.05 m) add no extra posts. The strands follow the line, which is within 0.25 m of the terrain. The collision is boxes along the line from the line up to the top, so nothing walks under a strand.
- **`WaterFence`**: for `type: water` segments, at the segments' heights (the terrain, until water surfaces exist; ADR 0015). Posts as for the land fence, with a line on floats between them. The same collision rule.
- **`Roadblock`**: boards on stands across the span from `start` to `end` along `yaw_deg`. Each stand stands on the highest `map_terrain` or `map_roads` hit under it (as `MapLoader.GroundOrRoadHitAt`), not on the `start`–`end` line (see "Context": up to 3.4 m off on road 52). Each board runs between two neighbouring stands, at most 0.9 m above the ground under each of them. No signs or text.

**Breaking** (decided: only vehicles). Bodies that can break barriers implement `IBarrierBreaker`: vehicles (later) and the car-sized test body of the tests. The player on foot never breaks a barrier, at any speed; it has to jump. A breaker that touches an item with a relative speed of at least `BreakSpeed` breaks that item; neighbouring items break only when they are hit in turn. A loose piece is an ordinary rigid body: it falls, tumbles and can be pushed further, also by the player. A loose piece that enters the void falls like any other body (see "Other bodies").

**Restoring** (decided). When the player respawns after a death, and on a map reload, every broken item is rebuilt as it was at load (static body and visible instances back), and every loose piece still in the scene is freed.

**Collision layers.** Barrier items and loose pieces get their own layer, `CollisionLayers.Barrier`. The player's mask includes it. The camera's spring arm doesn't (decided), and neither do the map rays (street label, inspector, `MapLoader` ground rays).

### Void trigger

- `MapLoader` builds one `Area3D`, `MapLoader/Edge/VoidTrigger` (`VoidTrigger`), from `void_trigger.prisms`: one `CollisionShape3D` with a `ConvexPolygonShape3D` of 8 points per prism (footprint at `bottom_y` and `top_y`). 1,177 shapes on the full Somero pack.
- It monitors the player's layer, the barrier layer, and the layers later specs add for vehicles, pedestrians and police. Map layer 1 is excluded.
- A body enters the void when any of its collision shapes overlaps a prism. `VoidTrigger` emits `BodyEnteredVoid(body, outward)`, with `outward` = `EdgeGeometry.OutwardAt` at the body's position. A body that enters again while it is already falling is ignored.
- The void band is 20 m wide (ADR 0015: many times a body's travel in one physics tick at 50 m/s), so a body can't pass it between two ticks.

### Safety net

Replaces the temporary fall respawn (decided). It catches anything that leaves the world without passing the trigger: a gap in the map's collision, a teleport, a body launched over `top_y`.

- Each physics frame, `World` checks the player and every body it tracks for the void (the bodies on the trigger's layers that `World` knows: the player, loose barrier pieces, and later vehicles and characters). A body with `EdgeGeometry.IsBelowSafetyNet(y, SafetyNetMargin)` that isn't already falling is handled as if it had entered the void, with `outward` from `OutwardAt` at its x/z:
  - the player, and any `IVoidFaller`: the death sequence (the player) or `EnterVoid` (others);
  - any other `RigidBody3D`: freed.
- With the starting margin, the net is at the void trigger's floor on Somero (y 31.8), where the temporary rule was.

### Death sequence (the player)

`World` owns a `DeathSequence` and connects `VoidTrigger.BodyEnteredVoid`. When the body is the player and the sequence is `Alive`:

1. **Falling** (`FallTime`):
   - `DeathSequence.EnterVoid` records the losses and calls `PlayerState.Die()`: money and gear are cleared and the wanted level reset, with the existing events (`Events.PlayerDied`).
   - The player's and the player camera's input go off (`InputEnabled` false), as with the debug camera. The gamepad run toggle and mouse capture keep their state.
   - The player's collision mask is cleared (decided), so it can't catch on the ledge, the cliff or a barrier, and falls cleanly. Its collision layer stays, so the trigger and the safety net still see it.
   - Gravity is replaced by `VoidPull` with the entry's `outward`; the player keeps its velocity at entry.
   - A `DeathCamera` (a `Camera3D` added by `World`, in every build) starts at the player camera's global transform and `Fov` and becomes current. Over `PullBackTime` it moves (smoothstep) to the entry point's nearest ring point + `outward × PullBackDistance` + `PullBackHeight` up, looking at the ring point `LookAtDrop` below the ledge. It looks down past the edge, so the slab's edge and cliff face stand against the starfield below the horizon. The camera has no collision.
2. **Death screen** (`ScreenTime`, timed): the `DeathScreen` `CanvasLayer` fades in over `FadeTime`. The player is hidden and its physics stopped. Then it moves on by itself.
3. **Respawning:** broken barriers are restored (see "Restoring"). The existing spawn draw procedure (`SpawnPicker`, ray and capsule checks, `SpawnMaxAttempts`) places the player in `spawn_area.open_ground` with zero velocity and a random facing. Normal gravity, its collision mask and input come back, the player is shown, the player camera turns behind it (`PlaceBehind`) and becomes current, the death screen fades out, and `World.PlayerRespawned(position)` fires. The player camera captures the mouse again under its existing rule (current and focused).
4. **Respawn failure:** the spawn error is pushed (path, reason, `/regen-map <id>`) as for a failed first spawn, the player is freed, the death screen is hidden and the state is `Failed`. Debug builds fall back to the debug camera.

### Death screen

Decided wording, in English:

- Title: **The Earth is flat**
- Subtitle: **You fell off the edge of the world**
- A losses line, from `DeathScreenText.Format(losses)`: the money lost and the gear lost, `Money lost: <amount> · Gear lost: <items>`, with the items as their ids, comma-separated, in ordinal order (display names come with the inventory spec), and `none` when there is no gear. The amount is a plain whole number; a currency symbol comes with the money HUD.

Look: a dark full-screen fade with the three lines centred in plain text (Godot's default font, white on dark), until a UI style exists. Nothing in it names or imitates another game.

### Edge cases

- **The debug camera toggle** does nothing outside `Alive` (`ToggleDebugCamera()` returns false). If the debug camera is current when the player enters the void (a player left standing on the ledge), the death camera still takes over, from the player camera's transform, and the respawn ends on the player camera.
- **Map reload during the sequence:** `DeathSequence.Reset()`, the death camera and screen are removed, barriers are rebuilt, and the normal spawn runs on the new map. `Die()` has already run and isn't undone.
- **Window focus loss** doesn't pause the sequence (there is no pause yet).
- **A player who jumps a fence** lands on the ledge. If its capsule reaches 1 m outward of the boundary, it enters the void; otherwise it stands behind the fence and can still walk out into it.
- **Loose pieces after a vehicle breaks a barrier** stay until the player's next respawn or a map reload, unless they fall into the void first.
- **Street label:** keeps following the player until it is hidden, then shows nothing until the respawn.

### Other bodies

The same rule holds for everything that crosses (outline: "Same rule for everyone"). Today the only other moving bodies are loose barrier pieces and the test breaker body; vehicles, pedestrians and police add theirs in their own specs.

- A body that implements `IVoidFaller` gets `EnterVoid(outward)` and handles its own fall (the hook for vehicles and characters; a vehicle with the player in it hands the player to the death sequence in the vehicles spec).
- Any other `RigidBody3D` falls under `VoidPull` (its gravity scale set to 0 and the pull applied as a force) and is freed once it leaves the trigger below `void_trigger.bottom_y`, passes the safety net, or after `MaxFallTime`, whichever comes first.
- Other body types are ignored.

### Space backdrop

Decided: a procedural sky shader, no assets. Above the horizon it draws the current day sky; below the horizon a starfield (where `ProceduralSkyMaterial` draws its ground colour today). So space shows wherever the view looks out and down past the edge, and in the death camera's frame. The town's lighting (ambient and reflected light, which come from the sky) doesn't visibly change: the street-level preset images are compared with the previous ones by eye.

### Exports, with starting values

Accepted as starting points (decided); tuned in the playtest.

| Export | Start | Reason |
| --- | --- | --- |
| `LandFence.PostSpacing` (also water fences) | 2.5 m | Breaks a 20 m segment (`fence.max_segment_m`) into 8 bays; 4,398 fence posts on B |
| `Roadblock`, `LandFence`, `WaterFence` height | 0.9 m | The decided maximum (ADR 0016) |
| `BreakSpeed` | 3 m/s | Low enough that a vehicle moving faster than walking pace drives through |
| `VoidPull.OutwardAcceleration` | 4 m/s² | From rest at the trigger's inner edge, the capsule clears the 2 m ledge (1.3 m plus its 0.3 m radius) in about 0.9 s |
| `VoidPull.DownAcceleration` | project gravity (9.8 m/s²) | Falls like normal gravity once off the ledge |
| `FallTime` | 3.0 s | A drop of about 44 m from rest, past the cliff's bottom where the face is shortest (30 m) |
| `PullBackTime` | 2.5 s | Ends before the death screen, so the last half second holds still |
| `PullBackDistance` | 60 m | At a 70° vertical field of view the frame is about 84 m tall there, more than the cliff's 30 to 81.6 m |
| `PullBackHeight` | −10 m | Below the ledge, so the slab's edge reads against the starfield rather than the town |
| `LookAtDrop` | 15 m | The same framing as the `world-edge` preset (look-at 15 m below the ledge) |
| `ScreenTime` | 3.0 s | Long enough to read three short lines |
| `FadeTime` | 0.5 s | Not abrupt |
| `MaxFallTime` | 10 s | Frees loose pieces that land on something and never leave the trigger |
| `SafetyNetMargin` | 20 m | The temporary rule's depth; equal to `void.width_m`, so on Somero the net is at the trigger's floor (y 31.8) |

### Performance

The world edge (barriers, trigger, safety net and backdrop together) costs **under 1 ms per frame** on the full Somero pack (decided). Measured on the user's development Mac, with the world edge built and with it left out (a `MapLoader` export for the measurement), as the difference in mean frame time over 600 frames:

- standing at the spawn;
- standing beside the road 52 north roadblock (`roadblock:cut:way/1445392542:0`, the `world-edge` preset's), looking along the fence.

Process and physics time come from a headless run (`Performance` monitors); rendering from a windowed run like the screenshots. The check is a script or test that skips when the full pack isn't there (CI has only the test-area pack), and the implementation reports the figures.

### Screenshots

- Existing presets keep their exit codes. `world-edge` now shows the roadblock on road 52, the fences beside it and the starfield below the horizon. Other images change only where the sky below the horizon shows.
- New flag **`--death <fall|screen>`**, debug builds only. After the usual map and look-at checks, the player is placed at the ring point nearest to the preset's look-at x/z, `void.inner_offset_m` + 1 m outward, at the ledge's height, which starts the death sequence. `fall` captures the death camera's frame at the end of `PullBackTime`; `screen` captures the death screen once it has faded in. It exits 1 outside a debug build, with `--player` or `--inspect`, or when the player didn't spawn. Run by `/verify` on `world-edge` for both values (N/A on a test-area pack, as the preset already is).

### Docs

- New ADR 0017 (world edge in the game): barrier rules (0.9 m, 1.0 m outward, static items with batched visuals that break into loose pieces only for vehicles, restored on respawn and reload, their own collision layer outside the camera's mask), the void trigger and the `IVoidFaller` hook, the safety net replacing the temporary fall respawn, the death sequence in `Roadman.Core` with `Die()` at void entry, the collision-off fall, the below-horizon procedural starfield, the death screen wording, and the 1 ms budget. ADR 0016's "Temporary fall respawn" bullet gets a note that ADR 0017 replaces it.
- `docs/specs/player-on-foot.md`: an amendment saying the temporary fall respawn and criteria 5 (the `FallReset` part) and 20 are replaced by this spec.
- `docs/architecture.md`: Roadman.Core (`EdgeGeometry`, `BarrierLayout`, `VoidPull`, `DeathSequence`, `DeathScreenText`; remove `FallReset` and the respawn state machine TODO), Gameplay (barriers, void trigger, safety net, death sequence, backdrop; remove the temporary fall respawn and the world-edge TODO), MapLoader (barriers and void trigger).
- `game/CLAUDE.md`: a "World edge" section (barriers, layers, breaking and restoring, void trigger, safety net, death sequence, `--death`, the performance check), and the temporary fall respawn removed from "Player".

## Acceptance criteria

1. `EdgeGeometry.IsInVoid` is true for 10,000 points sampled in the void band of the test-area and full packs' `boundary.json` (between 1 m and 21 m outside the ring, between `bottom_y` and `top_y`) and false for 10,000 points inside the boundary. `OutwardAt` returns unit vectors pointing out of the ring, including at a convex and a concave corner, and resolves ties to the lower `s`. `IsBelowSafetyNet` is false at exactly `bottom_y − margin` and true just below.
2. `BarrierLayout`, for every roadblock and fence in both packs: every piece's top is at most 0.9 m above its base (fence line or, for roadblocks, a base given by the test), no piece reaches more than 1.0 m outward, the items' collision footprints cover the ring with no gap over 0.1 m, posts are at most `PostSpacing` apart, and no two items put a post at the same end.
3. `DeathSequence`: `EnterVoid` in `Alive` records the money and gear as `Losses`, then calls `Die()` once (money and gear cleared, `Died` raised once) and moves to `Falling`; a second `EnterVoid` in any other state changes nothing. `Advance` moves through `DeathScreen` to `Respawning` at `FallTime` and `FallTime + ScreenTime` (boundaries exact, per step and across steps); `CompleteRespawn(true/false)` gives `Alive` / `Failed`, and in another state throws; `Reset()` returns to `Alive`; a negative or non-finite `dt` throws.
4. `VoidPull` gives `outward × OutwardAcceleration + down × DownAcceleration` and rejects a non-unit or non-horizontal `outward`.
5. `DeathScreenText.Format` gives "The Earth is flat", "You fell off the edge of the world" and the losses line, for losses with money and two items (ids in ordinal order), with money only (`Gear lost: none`), and with nothing (`Money lost: 0 · Gear lost: none`).
6. `FallReset`, `World.FallRespawnDepth` and the temporary respawn code are gone; no file in `game/` or `docs/` describes the temporary fall respawn as current behaviour.
7. On the test-area pack, the headless `World` places one barrier item per roadblock and fence, of the right type, named after its id, under `MapLoader/Edge/Barriers`, each a `StaticBody3D` on `CollisionLayers.Barrier`, with the visuals in batched `MultiMeshInstance3D`s and no `RigidBody3D` in the scene; and a `VoidTrigger` with one shape per prism; no errors pushed. On a map reload both are rebuilt with the same counts. With `boundary.json` missing, there are no barriers and no trigger, and the existing error is the only one.
8. No barrier item overlaps a `map_buildings` body or a void prism, and every roadblock stand's base is within 0.05 m of the ground or road hit under it.
9. Walking at `RunSpeed` into a fence, a water fence and a roadblock (positions taken from `boundary.json`, not hard-coded) stops the player inside the boundary within 3 s, and no item breaks.
10. From a run-up at `RunSpeed`, the player jumps a land fence segment where the terrain under it is lowest relative to its line, and lands beyond it.
11. The camera's spring arm passes through barriers: with the player beside a fence and the camera turned to put a post between the pivot and the camera, the arm keeps its full length.
12. A car-sized `IBarrierBreaker` test body (`RigidBody3D`, 1.8 × 1.5 × 4.5 m, 1,200 kg) driven at 15 m/s at a roadblock breaks it into loose pieces, passes the boundary and enters the void trigger within 3 s; the loose pieces that reach the void are freed within `MaxFallTime`. The same body at 2 m/s (under `BreakSpeed`) breaks nothing. (The outline's car test, with a stand-in until vehicles exist.)
13. A player placed 1.5 m outward of a boundary point on the ledge enters the void within one physics frame: `Died` fires once, money and gear are empty, the wanted level is 0, the losses hold what it had, its input is off, its collision mask is 0 and `DeathCamera` is current.
14. During `Falling`, the player moves outward and down: its distance from the ring grows and its y falls every frame, and its velocity changes by `VoidPull` per step (± 1 %), not by gravity alone.
15. The death camera starts at the player camera's transform and `Fov`, reaches its target (± 0.01 m) at `PullBackTime` and stays there, and looks at the ring point `LookAtDrop` below the ledge.
16. At `FallTime` the death screen is visible with the three lines and the player hidden; at `FallTime + ScreenTime` the player is inside `spawn_area.open_ground` on the floor within 120 physics frames, with zero velocity at placement, input on, its collision mask back, normal gravity, the player camera current and behind it, the death screen hidden, and `PlayerRespawned` fired once.
17. After criterion 12 broke a roadblock, a player death and respawn rebuilds it (static body back, instances visible, no loose pieces left); a map reload does the same.
18. With `--seed <n>`, two runs of the same death respawn at the same point.
19. A respawn where every draw fails (blocking bodies, as in the player-on-foot tests) pushes the spawn error, frees the player, hides the death screen and leaves the state `Failed`; a debug build is on the debug camera.
20. The debug camera toggle does nothing during the sequence. A player entering the void while the debug camera is current goes through the same sequence and comes back on the player camera.
21. A map reload during `Falling` or `DeathScreen` removes the death camera and screen, resets the sequence, rebuilds the barriers and spawns the player on the new map, with no errors.
22. Safety net: the player teleported to just below `cliff.bottom_y − SafetyNetMargin` inside the boundary (not in the trigger) starts the death sequence within one physics frame; at just above, nothing happens. A loose piece and a test `IVoidFaller` teleported there are freed and get `EnterVoid` once, respectively.
23. A loose `RigidBody3D` launched outward over the edge falls under `VoidPull` and is freed below `void_trigger.bottom_y` or after `MaxFallTime`. A test `IVoidFaller` entering the trigger gets `EnterVoid` once with the outward vector.
24. Screenshot `world-edge` (full pack) shows the red-and-white roadblock across road 52 with fences on both sides, and the starfield below the horizon past the edge. Checked by eye.
25. Screenshots `--death fall` and `--death screen` on `world-edge` show the slab's edge and cliff against the starfield, and the death screen with its three lines. Checked by eye. Outside a debug build, or with `--player` or `--inspect`, `--death` exits 1. On a test-area pack it is N/A (exit 2).
26. Every other preset keeps its exit code; the street-level images show no visible lighting change from the new sky (checked by eye against the previous images).
27. Performance: on the full Somero pack, the world edge adds under 1 ms to the mean frame time at the spawn and beside the road 52 north roadblock, in the headless and the windowed measurement; the implementation reports the figures and the barrier item and piece counts.
28. No code, asset or text names or imitates another game (reviewer check).
29. `/verify` passes: dotnet build, test and format, pytest and ruff (unchanged), the headless import and run, gdUnit4, the export check and the screenshots.
30. Playtest (the user): the barriers read as roadblocks and fences, are easy to jump, and the death sequence and screen feel right; the starting values are tuned and recorded in the exports.

## Test ideas

| Criteria | Layer |
| --- | --- |
| 1, 2 | `Roadman.Core` xUnit: synthetic boundaries (square, convex and concave corner, a 0.05 m segment, a roadblock beside a fence) and the real packs' `boundary.json` when present (skipped otherwise, like the other pack-reading tests) |
| 3, 4, 5 | xUnit: `DeathSequence` with a real `PlayerState`, stepped at 1/60 s and with large steps; `VoidPull`; `DeathScreenText` |
| 6 | the reviewer; `FallResetTests.cs` deleted, and player-on-foot criterion 20's gdUnit4 test replaced by 13, 16 and 22 |
| 7, 8 | gdUnit4: `World` on the test-area pack; missing-file case with a pack copy in a temp folder, as the other missing-file tests do |
| 9, 10, 11, 12 | gdUnit4: scripted input, the spring arm, and the test breaker body, at barriers picked from `boundary.json` |
| 13 to 23 | gdUnit4: the player placed on the ledge at a ring point from `boundary.json`; `--seed`; blocking bodies for 19; `MapLoader.Load()` during the sequence for 21; teleports for 22 |
| 24, 25, 26 | screenshot via `/verify`, viewed |
| 27 | a measurement script or test on the full pack (skipped without it), headless and windowed; figures in the implementation report |
| 28 | reviewer |
| 29 | `/verify` |
| 30 | playtest |

## Amendments

- 2026-11-04, plan (the user's answers):
  1. Implemented in two parts. Part A: Roadman.Core, the static barriers, void trigger, safety net, death sequence, death camera and screen, respawn, the starfield sky and the docs (criteria 1–11, 13–16, 18–23 and 26; 17's restore runs but is tested with breaking). Part B: vehicle breaking and restore (12, 17), the `--death` screenshots (24, 25) and the performance script (27).
  2. `EdgeGeometry.OutwardAt` points out of the ring for points inside it too (the vector from the point to the nearest ring point); on the ring it is that segment's outward normal.
  3. Each fence item owns the post at its end, and the one at its start only when the previous item is a roadblock. So fence joints have one post each; on boundary B that gives 4,439 posts (the 4,398 in "Exports" left out the 41 posts after roadblocks).
  4. `CollisionLayers.Vehicle` (layer 4) is reserved now, and the void trigger monitors it; Part B's test breaker uses it.
  5. Breaking is detected before the physics step (a sweep of each breaker's shape along its motion), so a breaker isn't stopped by the static body first (Part B).
  6. The sky shader draws the old ground colour below the horizon in the radiance pass, so the lighting doesn't change.
  7. The performance measurement is a separate script, not part of `/verify` (Part B).
  8. The death screen stays timed (`ScreenTime`), with no dismiss input.
- 2026-11-04, implementation of part A:
  1. `EdgeGeometry.LedgeHeightAt(s)` gives the ledge height for the death camera (linear between the covering item's end heights, ADR 0015).
  2. The mouse is released while the death camera is current (the player camera's existing rule, as with the debug camera) and captured again at the respawn; the run toggle keeps its state.
  3. On a map reload the death camera stops (not current) and the death screen is hidden; both nodes stay under `World`.
  4. The street label gets a `Suppressed` flag, so it shows nothing from the death screen until the respawn (with a null target it would measure from the camera).
  5. The tuned barrier values are in one `BarrierSettings` resource (`MapLoader.BarrierSettings`, kept across reloads), grouped by type: `LandFencePostSpacing` (also water fences), the heights, piece sizes and the shared collision values (`CollisionThickness`, `CollisionDepthBelow`, `JointOverlap`). Roadblock stands use the grey `fence_wire` material.
  6. A roadblock stand on the ring sits on the border of the terrain and road meshes, where a vertical ray can miss by under a millimetre (21 of 210 stands on Somero). It then stands on the ground `RoadblockStandGroundInset` (0.05 m) inward; criterion 8 uses the same rule.
  7. The player has two void-entry paths: the trigger, and `World`'s own per-frame test of its capsule against the prisms, because Jolt reports a `CharacterBody3D` to an `Area3D` a frame late (criterion 13). The respawn runs in `World._PhysicsProcess` after `DeathSequence.Advance` returns, so every `StateChanged` subscriber sees the transitions in order.
  8. `VoidDownAcceleration` is the project gravity unless set; the world-edge exports have range hints, and an invalid value pushes one error and keeps the last valid one.
- 2026-11-04, pipeline changes found by criterion 8 (the user's decisions; each its own commit before part A):
  1. Building collision is the extruded footprint (ADR 0006 amendment): the old minimum rotated rectangle of `osm:way/1494929523` reached 3.7 m past the boundary, so two fences stood inside it.
  2. Void prisms stay at least `void.inner_offset_m` from the whole ring, and the build fails on boundary notches or spikes narrower than 3 m (`world_edge.min_feature_width_m`; ADR 0015 amendment). Near convex corners the prisms lie up to 5 cm outside the 1 m circle (the user's choice, for fewer shapes), so criterion 1's band samples start at 1.05 m instead of 1 m.
  3. Boundary B was cleaned to pass that rule. The figures in "Context" change to: ring 10,330 m, 162 points; 41 roadblocks (34 drivable), unchanged; 735 fences (718 land, 17 water); 530 void prisms. The road 52 roadblocks and the `world-edge` preset are unchanged.

## Open questions

None. Resolved 2026-11-04 by the user:

1. Space backdrop: a starfield below the horizon only (visible past the edge and in the death camera), from a procedural shader, no assets.
2. Barriers built in code, low-poly: roadblocks as light red-and-white barrier boards on stands, land fences as posts with barbed wire, water fences as posts with a line on floats.
3. The player on foot can't break pieces; only vehicles (and the car-sized test body) can.
4. Broken barriers are restored when the player respawns and on a map reload.
5. The camera's spring arm doesn't collide with barriers.
6. The player's collision is turned off when the fall starts.
7. A safety net below the slab (`cliff.bottom_y` minus a margin) catches anything that misses the trigger: `IVoidFaller` bodies (and the player) start the death sequence, other rigid bodies are freed. It replaces the temporary fall respawn.
8. Death screen: title "The Earth is flat", subtitle "You fell off the edge of the world", and a line listing the money and gear lost.
9. `Die()` runs when the player enters the void.
10. The proposed starting values are fine; they get tuned in the playtest.
11. Static until hit: each item is a static body with batched visuals and turns into loose physics pieces only when a vehicle hits it at `BreakSpeed`. The world edge must cost under 1 ms per frame on the full pack.
