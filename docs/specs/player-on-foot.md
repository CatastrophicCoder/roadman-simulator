# Spec: player on foot

## Status

Approved

## Goal

The first playable (milestone 2, "On foot", in `docs/project-outline.md`): one player character who walks, runs and jumps around the Somero map pack, seen through a third-person camera that doesn't clip through buildings, with keyboard and mouse or a gamepad. The player spawns at a random open-ground point of the pack's spawn area, and the HUD street label follows the player. Engine-free parts (the boundary file, spawn point choice, player position, movement maths) live in `Roadman.Core` with tests.

## Context (2026-11-02)

- `World` (`game/scripts/world/World.cs`) builds the environment, sun, `MapLoader`, `StreetLabelHud`, the debug views and a `DebugCamera` in code. The `DebugCamera` is added in every build today and is always current. It uses WASD (`debug_forward`, `debug_back`, `debug_left`, `debug_right`), Q/E (`debug_down`, `debug_up`), Shift (`debug_fast`), the right mouse button to look and the wheel for speed.
- Other bound keys: F2 (`debug_orthophoto`), G (`debug_road_graph`), I (`debug_inspect`). There are no gamepad bindings yet.
- `StreetLabelHud.Target` already exists ("set it to the player once there is one").
- `PlayerState` (`Roadman.Core`) holds money, gear and the wanted level, with no position. `GameState` owns one instance.
- `boundary.json` (ADR 0015) is in every pack, but the game doesn't read it yet.
  - Its `spawn_area` has `polygon` (an `[x, z]` ring), `open_ground` (polygons of closed `[x, z]` rings, exterior first) and `open_area_m2`.
  - Open ground is the spawn polygon minus buildings, water, and road, path and deck surfaces grown by `spawn.obstacle_buffer_m` (2.0 m), and minus everything within `spawn.min_boundary_distance_m` (10.0 m) of the boundary.
- Somero's spawn area S1 lies inside the test area, so the full pack and the CI test-area pack share it. In the current full pack it has one open-ground polygon (one ring, 91 points) of 5,308.4 m², within x 19.2 to 135.0, z −135.0 to −61.2.
- The full pack's `cliff.bottom_y` (the slab's bottom) is 51.8, and `void_trigger.bottom_y` is 31.8.
- Surfaces the player walks on: terrain (`map_terrain`), roads and paths 0.05 m above it (`surface_offset_m`, `map_roads`), and bridge decks with 5 m road-side ramps, with 1.0 m railings. At the world edge the ground steps by up to 0.25 m onto the ledge (ADR 0015). Buildings are collision meshes, the extruded footprint parts (`map_buildings`) (2026-11-04: ADR 0006 amendment, extruded footprint).
- Measured in the full Somero pack (2026-11-02, from the tile meshes' up-facing triangles weighted by area, and from `04-roads/report.json`):

  | Surface | 95th percentile | 99th percentile | 99.9th percentile | Area over 30° |
  | --- | --- | --- | --- | --- |
  | terrain | 11.1° | 19.9° | 34.7° | 10,593 m² (5,139 m² over 35°) |
  | road | 7.6° | 11.6° | 17.9° | 0 m² |
  | path | 8.6° | 14.5° | 24.5° | 7 m² |
  | bridge deck | 1.1° | 1.1° | 1.1° | 0 m² |

  The 32 road-side ramps are at most 5.8 % (3.3°, `span:way/813373050:1`). The maxima over 50° on roads and paths are single sliver triangles, with no area over 30° on roads.

## Behaviour

### Roadman.Core

- **`BoundaryReader`** reads the whole of `boundary.json` version 1 into Core types, following the other pack readers. This covers every field in ADR 0015: `boundary`, `roadblocks` with their nodes and roles, `fences`, `cliff`, `void_trigger` with its prisms, `spawn_area` and `water_crossings`. The world-edge game spec uses the rest of the file. It rejects each of the following with a `MapPackException` naming the problem:
  - another `schema_version`, missing or unknown keys, unknown enum values (boundary `source`, node `role`, fence `type`, crossing `source`) and non-finite numbers;
  - a `map_id` or `area` that isn't the manifest's;
  - a ring or spawn polygon with fewer than 3 points, or an `open_ground` ring with fewer than 4 points or not closed (first point equals last);
  - a void prism footprint that isn't 4 points;
  - `open_area_m2` ≤ 0, or `void_trigger.bottom_y` ≥ `top_y`.
- **`SpawnPicker`** gets the open-ground polygons and an injected `System.Random`.
  - `Next()` returns a point `(x, z)` that is uniformly distributed over the open ground by area (holes excluded), so a larger polygon is picked more often.
  - The same seed always gives the same sequence.
  - Empty or zero-area open ground is an `ArgumentException`.
  - The picker itself never returns a point outside the open ground. Retries for the engine-side checks happen through repeated `Next()` calls.
- **`PlayerState.Position`** (`LocalPoint`, Godot local metres) is the player's last committed position.
  - It is set by `SetPosition(point)`. Non-finite components are an `ArgumentException`.
  - It raises `PositionChanged` only when the value changes.
  - Death (`Die()`) leaves the position unchanged, because respawn belongs to the death and respawn spec.
  - The player node writes its position every physics frame. `PlayerState` is the single source for later systems (save and load, minimap, missions), and the node never reads its position back from it during normal movement.
- **`OnFootMotion`** holds the movement maths, with no engine types.
  - Inputs: the move vector (x, y, length clamped to 1), the camera yaw, the run flag, whether the player is on the floor, the current horizontal velocity and the time step. It returns the new horizontal velocity and the facing yaw.
  - Target speed: `WalkSpeed × |move|`, or `RunSpeed × |move|` while running, so analog sticks give speeds between 0 and the maximum.
  - Direction: camera-relative; forward is away from the camera.
  - The velocity moves toward the target at `Acceleration`, or at `Deceleration` with no input, limited per step and scaled by `AirControl` (0 to 1) in the air.
  - The facing turns toward the movement direction at `TurnSpeed` and stays put with no input.
  - `JumpVelocity(height, gravity)` = √(2 · gravity · height).
  - All parameters are constructor or property values, so the Godot node passes its exports.
- **`FallReset.IsBelow(y, slabBottomY, depth)`** is true when `y < slabBottomY − depth`. It is the temporary fall rule (see "Temporary fall respawn").

### Player (Godot)

- **`Player`** (`scripts/player/Player.cs`), a `CharacterBody3D` built in code.
  - It has a `CapsuleShape3D` collision shape and a placeholder model of a few primitive meshes: body, head, and a nose or cap that shows the facing.
  - The model uses a plain flat-colour material in `materials/placeholder/`, like the other placeholders. It is replaced when the CC0 character kit is chosen (Art direction, still to decide).
  - Its collision layer is its own. Its mask includes terrain, roads, buildings and the world-edge cliff.
- **Exports, with starting values** (all to be tuned in the playtest):

  | Export | Start | Reason |
  | --- | --- | --- |
  | `CapsuleHeight` | 1.8 m | Eyes at about 1.7 m, the eye height every street-level camera preset uses |
  | `CapsuleRadius` | 0.3 m | About shoulder width. Fits through the narrowest gaps that open ground and paths leave |
  | `WalkSpeed` | 2.8 m/s | Twice the usual human walking pace (1.4 m/s); doubled in the playtest |
  | `RunSpeed` | 7.5 m/s | Raised 50 % from 5.0 m/s in the playtest. Crossing the 2 km map takes about 4.5 minutes, which leaves room for vehicles to matter |
  | `Acceleration` | 20 m/s² | Reaches `RunSpeed` in 0.375 s, responsive without a jolt |
  | `Deceleration` | 25 m/s² | Stops from `RunSpeed` in 0.3 s (1.1 m), a little faster than it starts |
  | `AirControl` | 0.3 | Some steering in a jump, but the jump keeps its line |
  | `TurnSpeed` | 720 °/s | Turns round (180°) in 0.25 s |
  | `JumpHeight` | 1.3 m | The decided apex (see "Barriers and jump") |
  | `FloorMaxAngleDegrees` | 45° | Above the 99.9th percentile of every walkable surface (terrain 34.7°, paths 24.5°, roads 17.9°) and far above the steepest ramp (3.3°). Only steep banks and slivers make the player slide. Also Godot's default |
  | `FloorSnapLength` | 0.3 m | More than the 0.25 m step at the world edge and the 0.05 m road edges, so walking down them never leaves the floor |
  | `SpawnDropHeight` | 0.5 m | A short drop that settles within a few frames, clear of small terrain bumps under the capsule |
  | `SpawnMaxAttempts` | 50 | Open ground already excludes buildings, roads and water, so a draw fails only on later props or terrain gaps |
  | `FallRespawnDepth` | 20 m | Decided (see "Temporary fall respawn") |

- **Movement:** each physics frame it reads the input actions, runs `OnFootMotion`, applies gravity (the project's default gravity) and calls `MoveAndSlide()`.
  - Jump: on the floor, pressing `jump` sets the vertical velocity to `JumpVelocity(JumpHeight, gravity)`. At 9.8 m/s² that is about 5.05 m/s and about 1.03 s in the air on flat ground. In the air, the press is ignored, with no double jump and no buffered jump.
  - Floor snapping keeps the player on the ground going down slopes, ramps and the step from a road onto the terrain.
  - The player can't pass through buildings or bridge railings. It walks over the 0.05 m road edges without stopping or bouncing.
- **Player state:** after spawning and in each physics frame, the body's global position goes to `GameState.Instance.Player.SetPosition`.

### Barriers and jump

- Decided: barrier scenes (fences and roadblocks) are at most **0.9 m** tall, and the jump apex is about **1.3 m**. The feet rise 1.3 m, which clears 0.9 m plus the 0.25 m that `fence.max_terrain_deviation_m` can add above the ground, with 0.15 m of margin. The world-edge game spec uses these numbers.
- This spec places no barriers. Criterion 11 checks the apex, and criterion 12 checks that the player clears a 1.15 m test box from a run-up.

### Spawn

- After the places, `MapLoader` reads `boundary.json` into `MapLoader.Boundary`, before `MapLoaded`. A missing or invalid file pushes an error naming the path and `/regen-map <id>`, as for the other pack files, and leaves it null. The tiles, road graph and places still load.
- `World` waits for `MapLoader.WaitForTerrainCollisionAsync()`. It then draws points from a `SpawnPicker`, up to `SpawnMaxAttempts` times, until one passes. The picker is seeded from the time, or from `--seed <n>` on the user command line, for tests and screenshots. For each draw:
  1. A downward ray at the point's x/z (as `TerrainHeightAt`) must hit `map_terrain` as its first hit on any collision body. Open ground excludes roads, buildings and water, so anything else above the terrain (a later prop or tree) means a redraw.
  2. A shape query of the player's capsule, `SpawnDropHeight` above that hit, must overlap no collision body (the terrain ignored).
  3. If both pass, the player is placed there with zero velocity and a random facing from the same `Random`, and falls onto the ground. `World.PlayerSpawned(position)` is emitted, and the player camera becomes current, behind the player.
- **Spawn failure:** if `MapLoader.Boundary` is null, or no draw passes within `SpawnMaxAttempts`:
  - an error is pushed naming the pack path (`res://maps/<id>/boundary.json`) and `/regen-map <id>`;
  - no player is created, with no fallback position;
  - `StreetLabelHud.Target` stays null;
  - debug builds stay on the debug camera.
  - A release build has no debug camera and shows the map from no player camera (a plain default view with nothing to control). That only happens with a broken pack.
- On a map reload (`MapLoader.Load()` again), the old player is freed and the spawn runs again on the new map.
- The spawn area is the one the death and respawn spec will use. This spec covers the first spawn and the temporary fall respawn only.

### Temporary fall respawn

Replaced by `docs/specs/world-edge-game.md` (see "Amendments", 2026-11-04); this section records the original rule.


- Until the world-edge death spec replaces it: when `FallReset.IsBelow(player y, boundary.cliff.bottom_y, FallRespawnDepth)` holds in a physics frame, the player respawns. That means more than 20 m below the slab's bottom; on Somero, below y 31.8.
- The respawn runs the same draw procedure as the first spawn and sets the velocity to zero. It doesn't call `PlayerState.Die()` and clears no money or gear.
- `World.PlayerRespawned(position)` is emitted. If no draw passes, an error is pushed as for a failed spawn and the player is freed.
- The code is marked as temporary (a comment naming the world-edge death spec), so that spec removes it.

### Third-person camera

- **`PlayerCamera`** (`scripts/player/PlayerCamera.cs`): a pivot that follows the player at `PivotHeight` above its feet. It holds a `SpringArm3D` of length `ArmLength` with a `Camera3D` at its end.
- **Orbit:** the mouse (while captured) and the right stick change yaw and pitch, at `MouseSensitivity` and `StickSensitivity`. Pitch is clamped to `[MinPitchDegrees, MaxPitchDegrees]`. Neither the mouse nor the stick is inverted. An invert option comes with the settings view, so the code keeps `InvertMouseY` and `InvertStickY` exports (default false) for it.
- **No automatic swing-back:** the camera stays where the player left it. It never turns behind the player by itself.
- **No clipping:** the spring arm's collision mask is terrain, roads, buildings and the world-edge cliff, excluding the player. It uses a sphere of `CameraCollisionRadius`, so the camera stops in front of walls and the near plane doesn't cut into them. The arm shortens at once when blocked and extends back at `ArmReturnSpeed`. With the player against a wall and the camera pushed fully in, the camera sits at the pivot, never inside or behind the wall.
- **Smoothing:** the pivot follows the player's position with `FollowSmoothing` (0 = rigid).
- **Mouse capture:** the mouse is captured while the player camera is current and the window has focus. `ui_cancel` (Esc) releases it, and a left click in the window captures it again. Losing focus releases it. There are no menus (out of scope).
- **Exports, with starting values:**

  | Export | Start | Reason |
  | --- | --- | --- |
  | `PivotHeight` | 1.6 m | Just under the 1.7 m eye height, so the view frames head and shoulders |
  | `ArmLength` | 4.0 m | Shows the whole 1.8 m character and the ground around it at a 70° field of view |
  | `CameraCollisionRadius` | 0.2 m | Wider than the near plane's half-extent at the default 0.05 m near distance, so walls never cut the image |
  | `ArmReturnSpeed` | 8 m/s | The arm extends back to full length in 0.5 s, without popping |
  | `MinPitchDegrees` / `MaxPitchDegrees` | −70° / 30° | Looks well down at the ground and up at building tops, never straight down or up |
  | `MouseSensitivity` | 0.003 rad/px | Same as `DebugCamera` |
  | `StickSensitivity` | 3 rad/s | About half a turn per second at full tilt |
  | `FollowSmoothing` | 0 | Rigid first; the playtest decides if smoothing helps |
  | camera `Fov` | 70° | Same as the camera presets |

### Debug camera

- `DebugCamera` is added only in debug builds (`DebugBuild.IsDebug`, the single helper of ADR 0016), like the other debug views. Release builds have only the player camera.
- `debug_camera` toggles between the player camera and the debug camera. It is bound to the physical key left of 1 (§ on Finnish Mac keyboards, the backquote key on US layouts). The implementation checks which physical keycode Godot reports for that key on macOS ISO and on Windows (`Key.Quoteleft` or `Key.Section`), binds every one that differs, and records the result in `game/CLAUDE.md`.
  - Switching to the debug camera puts it at the player camera's current transform.
  - While it is on, the player gets no movement or jump input and stands still under gravity, and the player camera doesn't orbit. The debug camera keeps its WASD, Q/E, Shift and right-mouse controls.
  - Switching back restores the player camera as it was, and the mouse capture.
  - Without a player (spawn failure), the toggle does nothing and the debug camera stays current.
- The orthophoto view, road graph view and place inspector keep working with either camera. The place inspector casts from the current camera, so it inspects what the third-person view looks at.
- Debug-key convention (recorded in `game/CLAUDE.md`): new debug actions use letter or symbol keys (physical keycodes), never F-keys. The existing F2 binding stays.

### Input map

New actions in `project.godot`, each with keyboard and gamepad events (`device` −1, any pad):

| Action | Keyboard and mouse | Gamepad |
| --- | --- | --- |
| `move_forward`, `move_back`, `move_left`, `move_right` | W, S, A, D (physical keys) | left stick |
| `run` | Shift, hold | none |
| `run_toggle` | none | left-stick click, toggle |
| `jump` | Space | south face button (button index 0) |
| `camera_left`, `camera_right`, `camera_up`, `camera_down` | mouse motion (not an action) | right stick |
| `debug_camera` | the key left of 1 (see "Debug camera") | none |

- Running is on while Shift is held, or while the gamepad run toggle is on. The toggle turns off when the left stick returns inside its deadzone (the player stops), so the next movement starts walking. Pressing Shift doesn't change the toggle.
- Stick deadzones come from the action's `deadzone`. Movement uses `Input.GetVector` (radial deadzone).
- The player's WASD and Shift share keys with the debug camera's `debug_*` actions. Only the current camera's owner reads input, so neither moves while the other is current.
- Gamepad hot-plugging needs nothing extra, because the actions listen to any device. One local player.

### HUD street label

- `World` sets `StreetLabelHud.Target` to the player when it spawns. The label then shows the street of the nearest road graph edge within `StreetMaxDistance` of the player's position, whichever camera is current.
- Before the player spawns, and without a player, `Target` is null and the label keeps its camera-ray behaviour.

### Screenshots

- Existing presets and flags are unchanged. In a screenshot run without `--player`, the player spawns (seeded by `--seed` when given), but it is hidden and `Target` stays null. So the preset images, the street label measured from the capture camera, and `--inspect` stay as they are.
- New flag **`--player`**, debug builds only. After the usual checks:
  - the player is placed on the ground at the preset's look-at x/z (the higher of the terrain and road hits), facing away from the preset's camera position, and shown;
  - the capture camera takes the player camera's transform once the player is on the floor, after `SettleFrames`, with the arm at full length or shortened by an obstacle;
  - the street label follows the player.
- `--player` exits 1 outside a debug build or when the player didn't spawn. The look-at test (exit 2) applies as for other presets. `--player` together with `--inspect` is an argument error (exit 1).
- `/verify` runs it for `centre-street-level`.

### Edge cases

- **The player walks to the world edge.** No barriers or void trigger are placed yet (world-edge game spec). The player can step onto the ledge (a step of up to 0.25 m), walk off the cliff, and fall until the temporary fall respawn puts it back in the spawn area.
- **Falling through the ground** (a gap in collision): the same respawn applies. The test criteria check that it doesn't happen on the spawn area, the roads near it and a bridge.
- **Slopes over `FloorMaxAngleDegrees`:** the player slides and can't walk up them (in Somero, mostly steep banks: under 0.1 % of the terrain area is over 35°).
- **Window loses focus while running:** the input actions release, and the player decelerates to a stop. The gamepad run toggle stays as it was.
- **A map without roads or places:** the player still spawns if `boundary.json` is valid. Without a valid one, see "Spawn failure".
- **Test-area pack (CI):** spawn area S1 is inside the test area, so the player spawns there as in the full pack. `cliff.bottom_y` comes from that pack's own `boundary.json`.

### Docs

- `docs/architecture.md`:
  - Roadman.Core section: `BoundaryReader`, `SpawnPicker`, `PlayerState.Position`, `OnFootMotion`, `FallReset`.
  - Gameplay section: the player, the player camera, the input map, the debug camera rule and the temporary fall respawn.
  - MapLoader section: `MapLoader.Boundary`.
  - Remove the "player character" TODO and the "Not read by the game yet" note on `boundary.json`.
- `game/CLAUDE.md`: a "Player" section (spawn and spawn failure, controls, camera, debug camera toggle and its keycodes, temporary fall respawn, `--seed`, `--player`), and the debug-key convention.
- ADR 0016 (on-foot player): barriers at most 0.9 m and the jump apex 1.3 m, the debug camera only in debug builds with player input off while it is current, and the debug-key convention.

## Acceptance criteria

1. `BoundaryReader` reads the current Somero full pack's and the CI test-area pack's `boundary.json` without errors, with every field in ADR 0015 present in the result. It rejects each of the following with a `MapPackException` naming the problem:
   - another schema version, a missing key, an unknown key at each level, an unknown enum value and a non-finite number;
   - a `map_id` or `area` different from the manifest's;
   - an unclosed or too-short `open_ground` ring, a prism footprint that isn't 4 points, `open_area_m2` ≤ 0, and a void `bottom_y` ≥ `top_y`.
2. `SpawnPicker.Next()` with a fixed seed gives the same sequence twice. Over 10,000 draws on a test shape (two polygons of different areas, one with a hole), every point is inside the open ground and outside the hole. The share in each polygon matches its area share within 4 binomial standard deviations (stated in the test).
3. `SpawnPicker` with empty or zero-area open ground throws `ArgumentException`.
4. `PlayerState.SetPosition` raises `PositionChanged` only when the value changes, and rejects non-finite components. `Die()` leaves the position unchanged.
5. `OnFootMotion`:
   - It reaches `WalkSpeed` and `RunSpeed` (± 1 %) from rest under constant full input. Half stick input gives half the speed.
   - Forward input moves away from the camera for camera yaws 0°, 90°, 180° and 270°.
   - Velocity changes per step never exceed the acceleration limit.
   - With no input, it decelerates to zero and the facing stays.
   - `AirControl` scales the change.
   - `JumpVelocity` equals √(2gh).
   - `FallReset.IsBelow` is false at exactly `bottom − depth` and true just below it.
6. On the CI test-area pack and the full Somero pack, the headless `World` spawns the player within `SpawnMaxAttempts` at a point inside `spawn_area.open_ground`, and `PlayerSpawned` fires. After 120 physics frames the player is on the floor (`IsOnFloor()`) within 0.1 m of the terrain under it, the player camera is current, and no errors are pushed.
7. With `--seed <n>`, two runs spawn the player at the same position. With ten different seeds, the ten spawn points are not all equal.
8. A spawn draw whose capsule would overlap a collision body (a test body placed on the seeded first point) is redrawn, and the player ends up elsewhere on open ground.
9. A pack without `boundary.json`, or with an invalid one, pushes an error naming the path and `/regen-map <id>`, and `MapLoader.Boundary` is null while the tiles, road graph and places load. No `Player` node exists, `StreetLabelHud.Target` is null, and in a debug build the `DebugCamera` is current and `debug_camera` doesn't change that. The same holds when every draw fails (a test with `SpawnMaxAttempts` draws all blocked).
10. Scripted input (gdUnit4, `Input.ParseInputEvent` or `Input.ActionPress`) on flat open ground:
    - Holding `move_forward` for 2 s moves the player away from the camera at `WalkSpeed` (± 5 % after acceleration).
    - Holding `run` as well moves it at `RunSpeed`.
    - Releasing stops it within the distance `Deceleration` gives (+ 0.1 m).
11. Pressing `jump` on flat ground raises the player's feet 1.3 m (± 5 %, from `JumpHeight`) above the take-off height, and it lands again. A second press in the air does nothing.
12. From a run-up at `RunSpeed`, the player jumps over a 1.15 m tall, 0.2 m deep test box (the 0.9 m barrier plus 0.25 m) placed across its path, and lands on the far side.
13. Walking 20 m along a road from the spawn area onto the terrain and back, crossing its 0.05 m edge both ways, the player is never airborne for more than one physics frame, never stops, and never ends up below the surface.
14. Walking into a building's collision mesh (the extruded footprint part; a collision box before 2026-11-04: ADR 0006 amendment, extruded footprint) at `RunSpeed`, the player stops at the wall and doesn't pass through it within 3 s. Walking along a bridge deck into its railing doesn't pass it either.
15. Walking up and down a bridge ramp (at a `span_end` node picked from `road_graph.json`, not a hard-coded position), the player stays on the floor and crosses the deck.
16. With the player 1 m from a building wall and the camera turned in 15° steps all the way round the player, a ray from the pivot to the camera never hits a collision body.
17. Gamepad, with injected `InputEventJoypadMotion` and `InputEventJoypadButton` events:
    - The left stick moves the player like the keys (criterion 10). Half tilt gives half the speed.
    - A left-stick click toggles running on, and returning the stick inside the deadzone turns it off.
    - The south button jumps.
    - The right stick changes the camera yaw and pitch, pitch stays within its limits, and stick-up pitches the camera up (not inverted). Mouse motion up pitches up as well.
18. The camera yaw doesn't change while the player walks in a circle with no camera input (no automatic swing-back).
19. Debug builds:
    - `debug_camera`, bound to the key left of 1, switches to the debug camera at the player camera's transform.
    - While it is on, `move_forward` doesn't move the player and `debug_forward` moves the debug camera.
    - Switching back makes the player camera current again.
    - An `ExportRelease` build has no `DebugCamera` node, checked by an export-build test or the export check script.
20. Placing the player 21 m below the test pack's `cliff.bottom_y` respawns it inside `spawn_area.open_ground` within one physics frame with zero velocity, and `PlayerRespawned` fires. At 19 m below, nothing happens. Money and gear are unchanged and `Died` doesn't fire.
21. `StreetLabelHud.Target` is the player after the spawn. A test places the player beside a named edge with the debug camera far away, and the label text is that edge's `StreetLabel.Format`.
22. Every existing screenshot preset still renders with the same exit code as before (0, or 2 where it was N/A), and the player is not in those images.
23. `--player` on `centre-street-level` saves an image of the placeholder character seen from behind in the third-person view, with the street label of the street it stands on. Checked by viewing the screenshot. Outside a debug build, or with `--inspect`, it exits 1.
24. `/verify` passes: dotnet build, test and format, pytest and ruff (unchanged), the headless import, gdUnit4, the export check and the screenshots. A headless run with `--quit-after 120` finishes without errors.
25. Playtest (the user): walking, running, jumping and the camera feel acceptable on keyboard and mouse and on a gamepad. The starting values are tuned and the tuned values recorded in the exports.

## Test ideas

| Criterion | Layer |
| --- | --- |
| 1 | `Roadman.Core` xUnit: `BoundaryReader` on hand-written JSON cases. One test reads the real pack's file when it exists (skipped otherwise, like the other pack-reading tests) |
| 2, 3 | xUnit: `SpawnPicker` with a seeded `Random` and synthetic polygons |
| 4 | xUnit: `PlayerState` |
| 5 | xUnit: `OnFootMotion` stepping at 1/60 s, and `FallReset` |
| 6, 7, 8, 9 | gdUnit4: `World` on the test-area pack. 8 adds a `StaticBody3D` at the seeded first point. 9 uses a copy of the pack without (or with a broken) `boundary.json` in a temp folder, as the other missing-file tests do, and an export override of `SpawnMaxAttempts` with blocking bodies |
| 10, 11, 12, 17, 18 | gdUnit4: scripted input at the spawn point, measured over physics frames. 12 adds a `StaticBody3D` box |
| 13, 14, 15 | gdUnit4: the player placed at points derived from `road_graph.json` and `buildings.json` (the walkable edge nearest to the spawn area, the nearest building box, a `span_end` node), never hard-coded coordinates. 15 needs a bridge; on the test-area pack it is N/A if none is inside the area |
| 16 | gdUnit4: camera position against a ray on all map collision |
| 19 | gdUnit4 (debug build) for the toggle. `.claude/scripts/check-export.sh` or an export-build smoke test for "no `DebugCamera` in release" |
| 20 | gdUnit4: the player teleported below the slab |
| 21 | gdUnit4: `StreetLabelHud` with `Target` set to the player |
| 22, 23 | screenshot (`/verify`), viewed |
| 24 | `/verify` |
| 25 | playtest |

## Amendments

- 2026-11-02, the user's answers to the draft's open questions:
  1. `BoundaryReader` models the whole file.
  2. Starting values proposed with reasons; slopes and ramp grades measured from the full pack.
  3. Barriers at most 0.9 m; jump apex 1.3 m.
  4. Spawn failure: error with the path and the `/regen-map` hint, no player, the debug camera stays in debug builds, no fallback position.
  5. Camera not inverted; invert options come with the settings view.
  6. No automatic swing-back.
  7. The debug camera key is the physical key left of 1; the no-F-keys debug-key convention goes in `game/CLAUDE.md`.
  8. Bindings: WASD, hold Shift to run, Space to jump; left stick moves, left-stick click toggles running, the south face button jumps, right stick moves the camera.
  9. `--player` is added.
  10. A temporary respawn when the player is more than 20 m below the slab's bottom.
  11. The outline is being re-exported with the milestones as text; a "use" action belongs to later specs.
- 2026-11-03, implementation (part A of the user's split: Core, spawn, movement, jump, temporary fall respawn, street label target and the mouse-driven camera; part B: gamepad, the debug camera toggle, the debug-build helper and its release check, `--player`):
  1. `water_crossings[].source` is a free, non-empty string, not an enum: the pipeline sets it from the map.yaml source name (`model.py`), so a closed list would reject valid packs.
  2. `SpawnDropHeight`, `SpawnMaxAttempts` and `FallRespawnDepth` are exports on `World`, not `Player`, because they are needed before a player exists.
  3. `PlayerCamera.StartPitchDegrees` (−15°) is the pitch the first spawn puts the camera at; `FollowSmoothing` is a time constant in seconds (0 = rigid).
  4. Map bodies stay on collision layer 1 and the player is on layer 2 (ADR 0016); the existing map rays (`MapLoader`, `StreetLabelHud`, `PlaceInspector`) use mask 1, so they never hit the player.
  5. Criterion 19's release check tests the single debug-build helper's release branch (ADR 0016), not the exported binary.
- 2026-11-03, part B plan (the user's answers):
  1. The left-stick click is its own action, `run_toggle`; `run` keeps only Shift (hold). One action can't tell a held key from a toggling click.
  2. "Half tilt" (criterion 17) is the raw stick value halfway between the action's deadzone and full tilt: 0.6 raw with the 0.2 deadzone, which `Input.GetVector` remaps to 0.5 strength, so half the speed.
  3. `debug_camera` is bound to both physical `Key.Quoteleft` (the key left of 1 on Windows and US Macs) and `Key.Section` (left of 1 on ISO Macs, such as the Finnish layout). On ISO Macs `Quoteleft` is the `<>` key beside left Shift, so it toggles too; acceptable for a debug-only key.
- 2026-11-03, the user's playtest of part A: `WalkSpeed` doubled to 2.8 m/s and `RunSpeed` raised 50 % to 7.5 m/s; the rest felt right. Gamepad buttons are named generically (south, east, west and north face buttons, left and right sticks, left-stick click), and in-game button prompts will use generic glyphs.
- 2026-11-04, `docs/specs/world-edge-game.md` (ADR 0017): the temporary fall respawn ("Temporary fall respawn"), the `FallReset` part of criterion 5 and criterion 20 are replaced by that spec. `FallReset`, `World.FallRespawnDepth` and `World.RespawnPlayer()` are gone; a fall off the slab is caught by the void trigger or the safety net and runs the death sequence, which calls `PlayerState.Die()`. `World.PlayerRespawned` now fires after a death respawn.
- 2026-11-05, `docs/specs/menu-settings-save.md` (ADR 0018): Esc and the gamepad Start button are the `pause` action and open the pause menu, which releases the mouse; `PlayerCamera` no longer handles `ui_cancel`, so the east face button no longer releases the mouse. A left click still captures it again. The camera's invert and sensitivity values come from the settings in a game started by `Main`.

## Open questions

None.
