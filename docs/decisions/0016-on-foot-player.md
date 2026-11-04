# 0016: On-foot player

Status: Accepted

## Context

Milestone 2 ("On foot") adds the first playable character: a player who walks, runs and jumps on the generated map, seen through a third-person camera. The world-edge game spec that follows places barrier scenes from `boundary.json` (ADR 0015), so the jump height and the barrier height have to be decided together. The free-flying `DebugCamera` shares keys with the player, and must not ship. Spec: `docs/specs/player-on-foot.md` (with its "Amendments").

## Decision

- **Barriers and jump.** Barrier scenes (fences and roadblocks) are at most 0.9 m tall, and the player's jump apex is 1.3 m (`Player.JumpHeight`). The feet rise 1.3 m, which clears 0.9 m plus the 0.25 m that `fence.max_terrain_deviation_m` can add above the ground (ADR 0015), with 0.15 m of margin. A change to either number, or to `fence.max_terrain_deviation_m`, has to keep that margin.
- **Debug camera in debug builds only.** Release builds have only the player camera. While the debug camera is current, the player gets no movement or jump input and the player camera doesn't orbit; only the current camera's owner reads input, because the two share WASD and Shift.
- **One debug-build helper.** Every debug-only decision (the debug camera, the debug views, the inspector, debug screenshot flags) goes through one helper that only calls `OS.IsDebugBuild()`. A test switches the helper to check the release layout (no `DebugCamera`), and the review checks that no other code calls `OS.IsDebugBuild()` directly.
- **Debug keys.** New debug actions use letter or symbol keys by physical keycode, never F-keys (on macOS they need Fn, and F3 is Mission Control). The existing F2 (`debug_orthophoto`) stays. The debug camera toggle is the physical key left of 1.
- **Generic gamepad names** (decided 2026-11-03). Code, docs and in-game button prompts name gamepad inputs generically (south, east, west and north face buttons, left and right sticks, left-stick click), never by a console maker's button names, and the prompts use generic glyphs.
- **Collision layers.** Map bodies stay on layer 1. The player is on layer 2, with mask 1 (mask 1 plus the barrier layer since ADR 0017). Rays and shape casts that look at the map (the camera's spring arm, the street label, the place inspector, `MapLoader`'s ground rays) use mask 1, so they never hit the player.
- **Temporary fall respawn.** Until the world-edge death spec, a player more than 20 m below `cliff.bottom_y` respawns in the spawn area, with no death and no loss of money or gear. The code is marked as temporary. Replaced by ADR 0017 (2026-11-04): the void trigger, the safety net and the death sequence.

## Consequences

- The world-edge game spec builds its barrier scenes 0.9 m tall at most, and replaces the temporary fall respawn with the void trigger and the death sequence.
- New colliders that the player and the camera must hit go on layer 1; anything else picks its own layer and is left out of mask 1.
- The release check for "no `DebugCamera`" tests the helper's release branch, not the exported binary.
