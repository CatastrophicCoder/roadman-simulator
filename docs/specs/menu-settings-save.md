# Spec: main menu, settings and save/load

## Status

Approved

## Goal

Give the game a front door and a way to keep progress: a main menu at startup (new game, continue, load, settings, quit), a pause menu in the game (resume, save, load, settings, quit to menu), a settings view stored per player in the user data folder, and five save slots plus an autosave that hold the player's position, money, gear, wanted level and quest progress in a versioned format. Covers "Main menu, settings and save/load" under "Game features" in `docs/project-outline.md` and the "menu with settings and save/load" part of milestone 2. The state logic (save data, its reader and writer, slots, the settings model and their validation) lives in `Roadman.Core` with xUnit tests; Godot holds only the screens and the code that applies settings to the engine.

Out of scope (later specs): the inventory screen, equip slots and item display names (inventory spec), the minimap itself (minimap spec; this spec only stores its corner), the quest system and its progress data (tutorial questline spec; this spec only reserves a place for it), the wanted level's gameplay (wanted level milestone), audio content (sounds and music; the buses are ready for it), the art-direction palette and the real UI style, rebinding controls, quicksave, gamepad glyph images, localisation, cloud saves, and any pipeline or map pack change.

## Context (2026-11-05)

- `run/main_scene` is `res://scenes/world/World.tscn`. `World._Ready` loads the map, spawns the player and reads the user args `--screenshot`, `--perf` and `--seed`. gdUnit4 tests instance `World` directly. There is no menu, no pause and nothing in `user://`.
- Autoloads: `Events` (signal bus: `MoneyChanged`, `GearChanged`, `WantedLevelChanged`, `PlayerDied`) and `GameState`, which owns one `Roadman.Core.PlayerState`. The architecture says `GameState` also holds the settings.
- `PlayerState` has `Money` (`AddMoney`, `TrySpend`), `Gear` (a set of string ids; `AddGear`, `RemoveGear`), `WantedLevel` (clamped 0..5), `Position` (`SetPosition`, written by the player every physics frame) and `Die()`. It has no way to set money or gear wholesale, and no facing.
- Esc is `ui_cancel`. `PlayerCamera._UnhandledInput` releases the mouse on `ui_cancel` while it holds the capture, and a left click captures it again; losing window focus releases it. Godot's built-in `ui_cancel` also holds the gamepad east face button, so today that button releases the mouse too. `DebugCamera` looks around only while the right mouse button is held.
- `World._Input` passes any key, gamepad button or mouse button press to `DeathSequence.Dismiss()` while the death screen shows (after `MinScreenTime`), so Esc dismisses it today.
- `PlayerCamera` has `InvertMouseY` and `InvertStickY` (default false, kept for this view by the player-on-foot spec), `MouseSensitivity` (0.003 rad per pixel) and `StickSensitivity` (3 rad/s at full tilt).
- No audio buses exist (no `default_bus_layout.tres`), and the game plays no sound.
- `World.Hud` (`StreetLabelHud`) and `World.DeathScreen` are `CanvasLayer`s. There is no `Theme` and no font asset.
- The manifest has `map_id`, `display_name` ("Somero-City"), `generated_at` and `tool.version`.
- `project.godot` sets `config/name="RoadmanSimulator"`, no `config/version` and no custom user directory. `release.yml` exports from the committed `project.godot`.
- The perf tool sets `Engine.MaxFps` 0 and, windowed, vsync off; screenshots render into their own 1600×900 `SubViewport`.
- The definition of done includes a headless run, `godot --headless --path game --quit-after 120` (`game/CLAUDE.md`); `/verify` itself runs the import and gdUnit4, not this run.
- Gamepad inputs are named generically (ADR 0016).

## Behaviour

### Roadman.Core

All new types are engine-free, in two folders.

**`Roadman.Core/Saves/`**

- **`SaveGame`**: the data of one save (an immutable record):
  - `FormatVersion` (int; the current version is `SaveFormat.CurrentVersion` = 1).
  - `GameVersion` (string, not empty: the game's `application/config/version`, for example `0.2.0` or `dev`). Information only; loading never depends on it.
  - `SavedAtUtc` (a UTC timestamp).
  - `PlaySeconds` (total play time, finite, ≥ 0).
  - `MapId` (a slug, the pipeline's `SLUG_PATTERN`) and `MapGeneratedAt` (the manifest's `generated_at`).
  - `Player`: `Position` (`LocalPoint?`, finite when present), `FacingYaw` (`double?`, radians, finite, normalised to (−π, π] when present), `Money` (≥ 0), `Gear` (string ids, no blanks, no duplicates, written in ordinal order), `WantedLevel` (0..`PlayerState.MaxWantedLevel`).
  - **Empty position:** `position` and `facing_yaw` are both `null` in a save written during a death (see "Saving"), and both present otherwise. The keys are always there; one `null` without the other is an error. A save with an empty position is a normal valid save; loading it spawns the player with the normal spawn draw.
  - `Quests`: quest progress, an empty object in version 1. The quest system spec defines its content and raises the format version.
- **`SaveReader` / `SaveWriter`**: JSON (`System.Text.Json`, already used by the pack readers), `snake_case` keys. The reader follows the pack readers' rules: missing or unknown keys, wrong types, non-finite numbers and out-of-range values are errors (`SaveFormatException` with the key path). A `format_version` newer than `CurrentVersion` is an error ("saved by a newer version of the game"). An older version goes through `SaveMigrations` step by step (none exist for version 1; the mechanism and its test hook exist). The writer writes the current version only, and its output read back gives an equal `SaveGame`.
- **`SaveSlots`**: slot naming and listing without file I/O (a small `ISaveStore` interface for list, read, write, rename and delete, with an in-memory store for tests).
  - Slots: `SlotId.Autosave` (file `autosave.json`) and manual slots 1..`ManualSlotCount` (5; files `slot_<n>.json`). A manual slot number outside 1..5 throws.
  - `List()` gives each slot, autosave first, as `Empty`, `Valid(SaveGame)` or `Damaged(reason)` (unreadable, invalid, or saved by a newer version).
  - `MostRecent()` is the valid slot, autosave included, with the latest `SavedAtUtc` (ties: the autosave, then the lower slot number), or none.
  - `Write(slot, save)` writes `<file>.tmp` and renames it over `<file>`, so a failed write leaves the old save; `Delete(slot)` removes the file.
- **`PlayTime`**: counts play seconds from `Advance(dt)` (rejects a negative or non-finite `dt`), starting from a save's `PlaySeconds` or 0. `PlayTimeText.Format(seconds)` gives the slot list text (`0:05`, `1:23` as h:mm; `25:07` above a day).
- **`PlayerState`** gets:
  - `Snapshot(includePosition)`: the save's `Player` part except the facing, which comes from the player node; with `includePosition` false the position is empty.
  - `Restore(snapshot)`: validates first, then sets money, gear, wanted level and, when the snapshot has one, position at once. Each event fires only when its value changes, as now; `Died` never fires. An empty position leaves `Position` as it is (the spawn sets it). An invalid snapshot throws and changes nothing.
  - `Reset()`: a new game's values (money 0, no gear, wanted level 0, position at the origin), with the usual change events and no `Died`.

**`Roadman.Core/Settings/`**

- **`GameSettings`**: an immutable record with defaults, grouped:

  | Group | Setting | Values | Default |
  | --- | --- | --- | --- |
  | Display | `WindowMode` | `Windowed`, `BorderlessFullscreen` | `Windowed` |
  | | `WindowSize` | 1280×720, 1600×900, 1920×1080, 2560×1440 (`WindowSizes`) | 1600×900, or the largest that fits |
  | | `RenderScale` | 50..100 %, steps of 5; applies in fullscreen only | 100 % |
  | | `Quality` | `Low`, `Medium`, `High` | `Medium` |
  | | `VSync` | on, off | on |
  | | `FrameCap` | off, 30, 60, 120 | off |
  | Audio | `MasterVolume`, `MusicVolume`, `EffectsVolume` | 0..100 %, steps of 5 | 100, 70, 80 % |
  | Gameplay | `InvertMouseY`, `InvertStickY` | on, off | off |
  | | `MouseSensitivity`, `StickSensitivity` | 0.25× to 3.0× the camera's default, steps of 0.05 | 1.0× |
  | | `MinimapCorner` | `TopLeft`, `TopRight`, `BottomLeft`, `BottomRight` | `BottomLeft` |

  and `FormatVersion` (1). The sensitivities are stored as the multiplier, so 1.0× is the camera's current default (0.003 rad per pixel, 3 rad/s).
- **`SettingsReader` / `SettingsWriter`**: JSON, the same type and range rules as the save reader, but forgiving per key: a single bad or unknown value falls back to its default and is reported in a warnings list, and the other values are kept. A file that isn't JSON at all, or has a newer `format_version`, gives all defaults and one warning.
- **`WindowSizes.Fitting(usableSize)`**: the four sizes that fit the screen's usable area (window decorations not counted), in ascending order; when none fits, the smallest (1280×720).
- **`VolumeMath.ToDb(percent)`**: 100 % is 0 dB, 0 % is muted (the bus mute flag), linear amplitude in between (`20·log10(p/100)`).
- **`QualityPreset`**: the values each preset applies, as plain data (starting values, tuned in the playtest):

  | Preset | Directional shadows | Shadow distance | Shadow atlas | Anti-aliasing | Render scale factor |
  | --- | --- | --- | --- | --- | --- |
  | Low | on, 1 split | 300 m | 2048 | FXAA | 0.75 |
  | Medium | on, 2 splits | 800 m | 4096 | MSAA 2× | 1.0 |
  | High | on, 4 splits | 1500 m (today's `SunShadowMaxDistance`) | 4096, soft filter high | MSAA 4× | 1.0 |

  The effective 3D render scale (`Viewport.Scaling3DScale`) is the preset's factor × `RenderScale` in fullscreen, and the preset's factor alone in a window.

### Startup and scenes

- **`Main`** (`scenes/main/Main.tscn`, the node tree built in code in `scripts/main/Main.cs`) becomes `run/main_scene`. It owns the menus and the loading screen and instances `World.tscn` when a game starts, as a child, and frees it when the game ends. `World.tscn` itself is unchanged in what it does; tests keep instancing it directly.
- **Direct start, no menus:**
  - When the user args hold `--screenshot` or `--perf`, `Main` instances `World` at once and adds no menus, no pause and no settings: the run behaves exactly as today (same window size handling, vsync and `MaxFps` as the tools set them, same exit codes).
  - `--new-game` (new user arg, parsed in `Roadman.Core.Tools`) starts a new game at once, skipping the main menu but with settings, the pause menu and the autosave as in a normal game. The definition-of-done headless run becomes `godot --headless --path game --quit-after 120 -- --new-game`, so it still loads the map and spawns the player.
- **Background:** the main menu and its sub-views sit on a plain flat background; no map is loaded until a game starts or loads.
- **Loading screen:** while `World` loads (new game, continue, load), a screen shows "Loading <display_name>…" (from the manifest; the map id if the manifest can't be read). It ends when the player has spawned or been placed. A map that fails to load, or a spawn that fails, shows the error text (the same message `MapLoader` or `World` pushes) and a "Back to main menu" button, and frees `World`.
- **Settings at startup:** `GameState` reads `user://settings.json` before the first frame and applies display, audio and gameplay values (not in direct-start tool runs; see above). No file: defaults, written when the player first closes the settings view. A file with warnings: the valid values are used, each warning is printed once (`push_warning`), and the corrected file is written when the settings view next closes.
- **User data folder:** `project.godot` sets `application/config/use_custom_user_dir=true` and `application/config/custom_user_dir_name="RoadmanSimulator"`, so `user://` is `~/Library/Application Support/RoadmanSimulator/` on macOS and `%APPDATA%\RoadmanSimulator\` on Windows. Settings are `user://settings.json`, saves are in `user://saves/`. Nothing is written in the map packs. Tests point both at a temporary folder (a path override on the stores) and never touch the real one.
- **Version:** `project.godot` commits `application/config/version="dev"`. `release.yml` writes the `v*` tag without the `v` (for example `0.2.0`, `0.2.0-test1`) into it before exporting. The main menu shows it in its bottom-right corner (`Version dev`), and every save stores it.

### Main menu

- Title "Roadman Simulator: Somero-City" and the buttons **New game**, **Continue**, **Load**, **Settings**, **Quit**, in that order, operated with the mouse, the keyboard or a gamepad (Godot's `ui_*` actions: arrow keys, the d-pad and the left stick move focus; Enter, Space and the south face button press; Esc and the east face button go back). The first enabled button has focus when the menu opens. The mouse is visible.
- **New game**: `PlayerState.Reset()`, the loading screen, then the existing spawn draw (`--seed` still applies). Play time starts at 0. No save is written.
- **Continue**: loads `SaveSlots.MostRecent()` (see "Loading a save"). Disabled (greyed, not focusable) when there is none.
- **Load**: the slot list in load mode. Disabled when no slot is valid.
- **Settings**: the settings view; back returns here.
- **Quit**: asks "Quit the game?" (**Quit**, **Cancel**); Quit exits with code 0.
- Esc or the east face button on the main menu's top level does nothing.

### Pause menu

- A new input action **`pause`**: Esc and the gamepad Start button (`JoyButton.Start`, used only through a `GamepadButtons.Start` constant, as with the face buttons), physical keycode, device −1. In the controls view the gamepad binding is shown as "Start button". In the game it replaces `ui_cancel`: `PlayerCamera` no longer handles `ui_cancel`, so the east face button stops releasing the mouse and is free for gameplay.
- `pause` opens the pause menu whenever a game is running and the loading screen isn't showing: in every death state (`Alive`, `Falling`, `DeathScreen`, `Respawning`, `Failed`) and while the debug camera is current. A `pause` press never reaches the death screen's dismiss check, so Esc and Start open the pause menu instead of dismissing the death screen; every other key, button or click dismisses it as before.
- **Window focus loss** during a game opens the pause menu (if it isn't open already). Regaining focus doesn't resume.
- **Opening:** `GetTree().Paused = true`. `Main`'s menus, dialogs and the loading screen run with `ProcessMode.Always`; everything in `World` stops (physics, the player, the death sequence and its camera and screen fade, the void fall, the street label, the debug views and debug keys). Play time stops. The mouse is released (visible); the player camera remembers that it held the capture.
- **Buttons:** **Resume**, **Save**, **Load**, **Settings**, **Quit to menu**. `pause` or back on the top level resumes; inside a sub-view or dialog they go back one level.
- **Resume:** unpauses; the player camera captures the mouse again if it held the capture before and is current (the debug camera and death camera never capture). The press that closes the menu is marked handled, so it never jumps the player, moves a camera or dismisses the death screen.
- **Save:** the slot list in save mode. Disabled, with the hint "You can save when you're not falling off the world", unless the death sequence is `Alive` and a player exists.
- **Load:** the slot list in load mode. Choosing a save asks "Load slot <n>? Progress since your last save will be lost." (**Load**, **Cancel**).
- **Settings:** the same settings view; changes apply while paused.
- **Quit to menu:** asks "Quit to the main menu?" with a second line: "Your game will be saved to the autosave slot." **Quit** writes the autosave (in every death state; see "Saving"), frees `World`, unpauses and shows the main menu; **Cancel** returns to the pause menu. A failed autosave keeps the dialog open with "Could not autosave: <reason>" and the buttons **Quit without saving** and **Cancel**.
- **Closing the window** (the window's close button, Cmd+Q on macOS, Alt+F4 on Windows; `AutoAcceptQuit` off, handled on `NotificationWMCloseRequest`): during a game it pauses and asks "Quit the game?" with the same autosave line as Quit to menu; on the menus it asks "Quit the game?". Confirming autosaves (during a game, in every death state) and exits with code 0. In direct-start tool runs the window closes as today.

### Slot list (save and load)

- Six rows: **Autosave** first, then **Slot 1** to **Slot 5**. A valid row shows the map's display name, the local date and time saved (24-hour, `yyyy-MM-dd HH:mm`), the money, and the play time (`PlayTimeText`). An empty row shows "Empty"; a damaged row "Damaged" and a short reason ("unreadable" or "saved by a newer version").
- **Load mode:** valid rows are enabled; empty and damaged rows are disabled. A save whose map has no pack (`res://maps/<id>/manifest.json` missing) shows "Map <id> not installed" and is disabled.
- **Save mode:** the autosave row is shown but disabled ("Saved automatically when you quit"); manual rows are enabled. An empty slot is written at once; a valid or damaged one asks "Overwrite slot <n>?" first. After writing, the row updates and "Saved to slot <n>" shows; the menu stays open. A failed write shows "Could not save: <reason>" and the old file stays.
- **Delete:** every non-empty row (the autosave and damaged rows included) has a **Delete** button, reachable with the keyboard and gamepad. It asks "Delete <Autosave | slot n>? This can't be undone." and then removes the file; the row shows "Empty".

### Saving

- A save records `PlayerState.Snapshot()`, the player's facing yaw, the map id and the manifest's `generated_at`, the game version, the current UTC time and the play time. The camera's orbit isn't saved; after a load it is behind the player.
- A manual save needs a spawned player and the death state `Alive`. Where the debug camera is doesn't matter; the player's position is saved.
- The autosave is written only on Quit to menu and on quitting the game, as above, in every death state:
  - `Alive`: as a manual save.
  - During a death (`Falling`, `DeathScreen`, `Respawning`, `Failed`): the post-death state with an empty position (`Snapshot(includePosition: false)`, facing empty). `PlayerState.Die()` has already run at void entry, so the save holds money 0, no gear and wanted level 0; play time and the rest as usual. So quitting mid-fall and loading again can't dodge the loss.

### Loading a save

1. The save's map must have a pack (see "Slot list").
2. With a running game, after the confirmation, the current `World` is freed. The loading screen shows and a new `World` loads the save's map and waits for terrain collision, as the spawn does.
3. `PlayerState.Restore` sets money, gear, wanted level and position when present (events fire for changed values). Play time continues from the save's `PlaySeconds`.
4. **Empty position:** the normal spawn draw places the player (as after a death), with no notice. Otherwise the **placement check** at the saved position, with the spawn's tests (a downward ray from above the saved x/z must hit `map_terrain` or `map_roads`, and the capsule `SpawnDropHeight` above that hit overlaps nothing but terrain and roads). Passes: the player is placed on that ground at the saved x/z, facing `FacingYaw`, with zero velocity. Fails (the pack was regenerated and something now stands there, or the point is outside the new boundary): the normal spawn draw places the player, and a short notice shows for a few seconds: "The map has changed since this save, so you start somewhere else." Money, gear and wanted level are kept either way. The same rule holds whether or not `MapGeneratedAt` matches the pack.
5. The death sequence is `Alive`, the player camera current and behind the player, the street label targets the player, the game unpaused and the mouse captured (when the window has focus). `World.PlayerLoaded(position, moved)` (a new signal; `moved` is true only when the placement check failed, false for an empty position) fires; `PlayerSpawned` and `PlayerRespawned` don't.

### Settings view

Four tabs, each a column of labelled controls, operated with the mouse, the keyboard or a gamepad. Every change applies at once and is written to `user://settings.json` when the view closes. Each tab has **Reset to defaults** with a confirmation.

- **Display:** window mode, window size (disabled in fullscreen), render scale (disabled in a window), quality, vsync, frame cap.
  - Window mode and size go through `DisplayServer` (mode, size, centred on the current screen). The size list is `WindowSizes.Fitting` of the current screen.
  - Quality applies `QualityPreset` to the viewport (MSAA, FXAA, the 3D scale) and to `World`'s sun and environment (`World` reads it when it enters the tree and when the setting changes).
  - VSync sets `DisplayServer.WindowSetVsyncMode` (enabled or disabled); the frame cap sets `Engine.MaxFps` (0 for off).
  - **Confirm or revert:** after a change to window mode, window size or render scale, a dialog asks "Keep these display settings?" with **Keep** and **Revert** and a 10-second countdown. Revert, Esc, the east face button or the countdown running out restores those three values from before the change. Further display changes are blocked while the dialog shows. Quality, vsync and the frame cap apply without it.
- **Audio:** one slider per bus, 0..100 in steps of 5, with the value shown. Each sets its `AudioServer` bus volume (`VolumeMath.ToDb`; 0 mutes). The buses `Master`, `Music` and `Effects` (both routed to `Master`) are in a committed `default_bus_layout.tres`. No sound plays yet.
- **Controls:** a read-only table of every gameplay action with its keyboard (or mouse) binding and its gamepad binding, read from `InputMap` at runtime so it never goes out of date. Keys show the label of their physical key on the current layout (`DisplayServer.KeyboardGetLabelFromPhysical`); mouse look shows as "Mouse"; gamepad inputs by generic text names ("South face button", "Left stick up", "Left-stick click", "Right stick left", "Start button", ...). Action labels are plain English ("Move forward", "Run (hold)", "Run toggle", "Jump", "Camera left", ..., "Pause"). The `debug_*` actions and `debug_camera` show in a separate "Debug" group in debug builds only (`DebugBuild.IsDebug`). No rebinding.
- **Gameplay:** "Invert mouse Y" and "Invert stick Y" (checkboxes), "Mouse sensitivity" and "Stick sensitivity" (sliders 0.25× to 3.0×, value shown), "Minimap corner" (four options, with the note "Used by the minimap", which reads it in its own spec). The camera values apply to the player camera at once and at every spawn and load: `InvertMouseY`, `InvertStickY`, and `MouseSensitivity` / `StickSensitivity` = the camera's export default × the multiplier. `PlayerCamera`'s exports keep working in the editor and in tests that build `World` directly; in a game started by `Main`, the settings win.

### UI style

- One `Theme` built in code (`scripts/ui/UiTheme.cs`), used by every menu, dialog, the loading screen, the slot list, the settings view and the death screen: flat panels with no gradients, textures or glossy rounded corners, a dark translucent panel colour, one accent colour for focus and the selected tab, clear focus outlines for gamepad use, readable text sizes. The colours are placeholders kept in one place; the real UI style comes with the art direction.
- Godot's default font; no font asset. Gamepad inputs are text names; no glyph images.
- Our own look: no layout, colours, wording or icons taken from another game, and no other game's name anywhere.
- All text is English.

### Interaction with existing systems

- **Death:** dying doesn't touch the save files. Loading a save after a death restores what that save held.
- **Map reload** (`MapLoader.Load()`, tests and debug): unchanged; it doesn't open or close menus.
- **Release builds:** the same menus; no "Debug" group in the controls tab.

### Screenshots

- New flag **`--menu <main|pause|settings|slots>`** (debug builds only), with `--screenshot <preset> <out.png>`. The map loads and the preset's checks run as for every preset (same exit codes). Then:
  - `main`: the main menu on its plain background, filling the capture.
  - `pause`: the pause menu over the paused game at the preset's camera.
  - `settings`: the settings view's Display tab, opened from the pause menu.
  - `slots`: the slot list in load mode, opened from the pause menu, with the saves folder pointed at a temporary folder holding fixed sample saves (the autosave and slot 1 valid, slot 2 damaged, the rest empty), so no real save is shown or touched.
  - The menu is moved into the capture `SubViewport`, as the HUD is. The image is checked for a flat colour like the others.
  - Exit 1 outside a debug build, or together with `--player`, `--inspect`, `--death`, `--orthophoto` or `--road-graph` (argument errors).
- `/verify` runs `--menu` with each of the four values on `overview` (`menu-<value>.png`).

### Docs

- New ADR 0018 (menus, settings and saves): `Main` as the main scene with direct start for `--screenshot`/`--perf` (no settings applied) and `--new-game`; the `pause` action replacing `ui_cancel` in the game and taking precedence over the death screen's dismiss; pausing with `GetTree().Paused`, also during a death; the save format (JSON, versioned, strict reader, migrations, atomic writes, five manual slots and an autosave written on quitting, manual saves only when `Alive`, an autosave during a death holding the post-death state with an empty position); settings in `user://settings.json` (forgiving per key); the custom user folder; the placement check on load; the game version from the tag; "Start button" as the generic name of the gamepad's start button (an addition to ADR 0016's list, which gets a note).
- `docs/architecture.md`: Roadman.Core (`Saves/`, `Settings/`, `PlayTime`, `PlayerState.Snapshot/Restore/Reset`), Gameplay (`Main`, menus, settings applier, `pause`), and that player data lives in the user data folder, not in map packs.
- `game/CLAUDE.md`: a "Menus, settings and saves" section; the "Player" section's Esc rule and controls list (`pause`); the headless run command with `-- --new-game`; `--menu` under "Screenshots".
- Root `CLAUDE.md`: the release paragraph mentions that `release.yml` writes the version.
- `docs/specs/player-on-foot.md`: an amendment noting that Esc (and Start) now open the pause menu, which releases the mouse, and the east face button no longer does.
- `docs/specs/world-edge-game.md`: an amendment noting that `pause` opens the pause menu on the death screen instead of dismissing it, and that the sequence pauses with the game.

## Acceptance criteria

1. `SaveWriter` then `SaveReader` round-trips a `SaveGame` with money, three gear ids, wanted level 3, a position and facing, a play time, a game version and an empty quest object, to an equal value; gear is written in ordinal order; keys are `snake_case`. A save with an empty position (money 0, no gear, wanted level 0) round-trips too, with `position` and `facing_yaw` written as `null`.
2. `SaveReader` rejects, with the offending key path: a missing key, an unknown key, a wrong type, a non-finite number, negative money or play time, a wanted level outside 0..5, blank or duplicate gear ids, a non-slug map id, an empty game version, `position` `null` with `facing_yaw` present or the other way round, a missing `position` or `facing_yaw` key (`null` is allowed, absent isn't), and a `format_version` above `CurrentVersion` (the message says a newer version saved it). A version-1 file passes through `SaveMigrations` unchanged; a test migration registered for a fake version 0 → 1 is applied.
3. `SaveSlots` with the in-memory store: `List()` gives six slots, autosave first, each `Empty`, `Valid` or `Damaged` correctly (invalid JSON, a newer version); `MostRecent()` picks the latest valid `SavedAtUtc` across the autosave and the manual slots, ties to the autosave then the lower slot, ignores damaged slots, and is none with no valid slot; manual slot numbers outside 1..5 throw; `Write` leaves no `.tmp` after success and the old file after a failed rename; `Delete` empties the slot.
4. `PlayerState.Restore` sets money, gear, wanted level and position; each change event fires once and only for a changed value; `Died` never fires; an invalid snapshot throws and leaves the state unchanged. `Snapshot(true)` then `Restore()` on a fresh state gives an equal state. `Snapshot(false)` has an empty position; restoring it changes money, gear and wanted level but not `Position`, and `PositionChanged` doesn't fire. `Reset()` gives money 0, no gear, wanted level 0 and the origin, without `Died`.
5. `PlayTime.Advance` sums steps and rejects negative or non-finite ones; `PlayTimeText.Format` gives `0:00` for 0 s, `0:05` for 300 s, `1:23` for 4,980 s and `25:07` for 90,420 s.
6. `SettingsReader`: a full valid file reads as written; one bad value (wrong type, out of range, unknown enum, a window size not in the list) or an unknown key gives that value's default, keeps the others and adds one warning naming the key; non-JSON or a newer `format_version` gives all defaults and one warning. `SettingsWriter` output read back equals the input. The defaults are the table's.
7. `VolumeMath.ToDb`: 100 % → 0 dB, 0 % → muted, 50 % → −6.02 dB (± 0.01), monotonic. `WindowSizes.Fitting` returns the fitting sizes in order and 1280×720 when none fits. The effective render scale is the preset factor × `RenderScale` in fullscreen and the factor alone in a window.
8. Startup with no user args (headless, test-area pack): `Main` shows the main menu with five buttons in order and the version text; no `World` exists; no error is pushed. Continue and Load are disabled with an empty saves folder and enabled with one valid save. `godot --headless --path game --quit-after 120 -- --new-game` loads the map, spawns the player and exits without errors.
9. `--screenshot` and `--perf` runs through `Main` behave as before: no menus, no settings applied, the same exit codes (every existing screenshot run in `/verify` passes), and the existing gdUnit4 tests that instance `World` directly pass unchanged.
10. New game: the loading screen shows, then the player spawns in the spawn area with `PlayerState` reset (money, gear and wanted level from a previous game gone), play time 0, the player camera current; with `--seed <n>` the spawn point equals a direct `World` run's with the same seed. A pack that fails to load shows the error and the button back to the main menu, and no `World` is left.
11. Pause: a `pause` press (Esc, and the gamepad Start button) in a game pauses the tree and shows the pause menu with five buttons and releases the mouse; over 60 frames the player's position, the death sequence's time in state, physics bodies and play time don't change, and the debug keys do nothing. Resume (button, `pause` or back) unpauses; the player camera wants the capture again when it held it before (checked through its wanted-capture state, since headless can't capture); the closing press doesn't jump the player, move the camera or dismiss the death screen. The east face button doesn't release the mouse.
12. `pause` opens the pause menu during `Falling` and on the death screen (before and after `MinScreenTime`) without dismissing it, and Save is disabled there with its hint; after Resume the sequence continues from where it paused. Another key after `MinScreenTime` still dismisses the death screen.
13. Window focus loss during a game opens the pause menu; on the main menu it does nothing.
14. Save and load in one session: after saving to slot 2 at a known position with money 120, gear `a` and `b` and wanted level 2, then moving, spending and dying, loading slot 2 (after its confirmation) puts the player within 0.05 m (x/z) of the saved spot on the ground, facing the saved yaw (± 0.01 rad), with money 120, gear `a` and `b`, wanted level 2, play time from the save, state `Alive`, the death screen hidden, every barrier intact and `PlayerLoaded` fired once with `moved` false.
15. Load from the main menu (Continue and Load) in a fresh `Main` gives the same result as criterion 14; Continue uses the most recent valid slot, the autosave included.
16. A save whose position now fails the placement check (a blocking body placed there in the test) loads with the spawn draw, shows the "map has changed" notice, fires `PlayerLoaded` with `moved` true and keeps money, gear and wanted level. A save naming a map with no pack is shown "not installed" and can't be chosen.
17. Saving: an empty manual slot is written without asking, an occupied one only after the overwrite confirmation; the autosave row can't be chosen in save mode; a failing write (a read-only temporary folder) shows the error and leaves the old file; no `.tmp` is left after a successful save. Delete asks first and then empties the row, for a manual slot, a damaged slot and the autosave.
18. Autosave: Quit to menu while `Alive` writes `autosave.json` with the current state and then shows the main menu with no `World` left and the tree unpaused; Cancel returns to the pause menu. Quit to menu during `Falling` (the player had money 50 and gear `a` before the void) and on the death screen writes an autosave with an empty position, money 0, no gear and wanted level 0; loading it spawns the player in the spawn area with nothing, no notice, `PlayerLoaded` with `moved` false. A window close request during a game asks first, then autosaves (in any death state, as above) and exits with code 0; on the main menu Quit and a close request ask "Quit the game?" first.
19. Settings view: each value applies at once — window mode and size on `DisplayServer` (or through a test seam where the headless display server can't), render scale and quality on the viewport and on `World`'s sun and environment, vsync and `Engine.MaxFps`, each volume on its `AudioServer` bus (0 mutes), the invert and sensitivity values on the player camera and on the next spawn and load. Closing the view writes `settings.json`; a new `Main` reads it back and applies it before the menu shows. A settings file with one bad value starts with that value's default, the others kept, and one warning.
20. Display confirm-or-revert: after a change to window mode, window size or render scale the dialog shows with a 10-second countdown; Keep keeps it; Revert, Esc, the east face button and the countdown running out each restore those three values from before. A change to quality, vsync or the frame cap applies with no dialog.
21. The controls tab lists every non-debug action in `project.godot` with its keyboard and gamepad bindings read from `InputMap`; `pause` shows Esc and "Start button"; no gamepad label contains a console maker's button names (a word list in the test); the debug actions appear only when `DebugBuild.IsDebug` (checked with `DebugBuild.Override`).
22. Every menu, dialog and settings tab can be operated with the keyboard alone and with gamepad input alone (simulated `ui_*` events): focus starts on the first enabled control and every enabled control is reachable.
23. With the custom user directory set, `OS.GetUserDataDir()` ends in `RoadmanSimulator` (not under `app_userdata`); the release workflow sets `application/config/version` from the tag before exporting (checked in the workflow's export step log, `0.x.y-…` for a pre-release tag); a local build shows `Version dev`; a save stores the version.
24. Screenshots `--menu main`, `pause`, `settings` and `slots` on `overview` show the four views as described, readable, in one consistent flat style of our own. Checked by eye. Outside a debug build, or combined with another debug flag, `--menu` exits 1.
25. No code, asset or text names or imitates another game (reviewer check); gamepad inputs are named generically everywhere.
26. `/verify` passes: dotnet build, test and format, pytest and ruff (unchanged), the headless import, gdUnit4, the export check and the screenshots, including the new `--menu` ones.
27. Playtest (the user): the menus are easy to use with mouse, keyboard and gamepad; Esc, Start and the mouse capture feel natural, also on the death screen; a save and load round trip and the autosave work across a restart; settings survive a restart; display changes and their revert behave on macOS and Windows.

## Test ideas

| Criteria | Layer |
| --- | --- |
| 1, 2, 3 | `Roadman.Core` xUnit: `SaveReader`/`SaveWriter` on literal JSON strings; `SaveSlots` with the in-memory `ISaveStore` (a store that fails on rename for 3) |
| 4, 5 | xUnit on `PlayerState` (counting events) and `PlayTime`/`PlayTimeText` |
| 6, 7 | xUnit on `SettingsReader`/`SettingsWriter`, `VolumeMath`, `WindowSizes`, `QualityPreset` |
| 8 | gdUnit4 on `Main`; the headless `--new-game` run (documented command, run in the implementation report) |
| 9 | `/verify`'s screenshot runs and the existing gdUnit4 suite |
| 10 to 22 | gdUnit4: `Main` on the test-area pack, with the saves and settings stores pointed at a temporary folder; simulated input events; `NotificationWMWindowFocusOut` and `NotificationWMCloseRequest` sent to the tree; `DebugBuild.Override`; a blocking body for 16; a read-only temp folder for 17; a display-server seam for 19 and 20 |
| 23 | gdUnit4 for the user directory and the version text; the release workflow by a pre-release tag (`v0.x.y-testN`) |
| 24 | screenshot via `/verify`, viewed |
| 25 | reviewer |
| 26 | `/verify` |
| 27 | playtest |

## Amendments

- 2026-11-05, plan (the user's decisions):
  1. Implemented in two parts. Part A: Roadman.Core, `Main`, the main and pause menus, the slot list, the loading screen, saving and loading, the autosave, ADR 0018 and the docs (criteria 1–18, 22 for those views, 23 and 25). Part B: the settings view (tabs, display, quality and audio application, the confirm-or-revert dialog, the controls table) and the `--menu` screenshots (19–21, 22 for the settings view, 24, 26).
  2. Until Part B, the Settings buttons in the main and pause menus are shown disabled (not focusable).
  3. `/verify` runs the headless startup (`godot --headless --path game --quit-after 120 -- --new-game`) as its own step, "headless startup": it fails on a non-zero exit, an `ERROR` line, a timeout or no `Player: spawned at` line.
- 2026-11-05, implementation of part A:
  1. Godot 4.7's built-in `ui_accept` holds only Enter, Kp Enter and Space, and `ui_cancel` only Escape (the "Context" line about the east face button is wrong for this version). `project.godot` overrides both, adding the south face button to `ui_accept` and the east face button to `ui_cancel`, so the menus work with a gamepad alone. In the game `Main` ignores `ui_cancel`, so the east face button still neither pauses nor releases the mouse.
  2. `World` is added with `ProcessMode.Pausable`, because `Main` runs with `Always` and `Inherit` would keep `World` running while paused.
  3. The load confirmation over a running game names the autosave as "Load Autosave? Progress since your last save will be lost.". The pause menu's Load is disabled when no slot is valid, like the main menu's. The slot list has a Back button. Quitting while the loading screen shows asks "Quit the game?" and writes no autosave. A save whose map isn't installed, reached through Continue, shows the load-failed screen. A manifest without `generated_at` saves `"unknown"`.
  4. The death screen keeps its own look until Part B moves it onto `UiTheme`, so the death screenshots don't change in Part A.
- 2026-11-05, implementation of part B:
  1. The settings view's tabs are a row of plain buttons over the content, not a `TabContainer`, so the explicit focus neighbours reach every control with the keyboard or a gamepad. Reset to defaults resets the open tab only, after "Reset <tab> settings to defaults?".
  2. Display changes go through an `IDisplayBackend` (the test seam for criteria 19 and 20). A pending confirm-or-revert also reverts when the view closes or another dialog replaces it. Quality sets the viewport and the sun; `QualityPreset` has no environment values, so nothing is applied to the environment. Low and Medium keep the project's soft shadow filter; High uses the high one.
  3. The controls table lists the gameplay actions and, in debug builds, a separate "Debug" table; the `ui_*` menu actions aren't listed. Keys show the layout's character where there is one, special keys Godot's name ("Escape", "Shift"), so criterion 21's Esc shows as "Escape".
  4. `--menu` screenshots: `Main` builds its menus in that direct start only, without settings, input or quit handling; the capture lays the menus out at the project's base 2D size. `pause`, `settings` and `slots` pause the tree.
  5. The death screen uses `UiTheme`, so the `world-edge-death-screen` image changed look.
  6. From the part A review: the load placement ray starts `World.LoadRayHeadroom` (2 m) above the saved height instead of high above the map, so a save under a bridge loads under the deck. The save-building rules are in `Roadman.Core` (`SaveFactory`), settings file I/O in `SettingsFile`, save and settings files are flushed to disk before the rename, and CI runs the headless startup against the test-area pack.
  7. Part A's full-rect menu controls were anchored in `_Ready` with `SetAnchorsPreset`, which left them 0×0 in the window; fixed with `SetAnchorsAndOffsetsPreset`.

## Open questions

None. Resolved 2026-11-05 by the user:

1. A new `Main` scene shows the menu and loads `World` when a game starts. The headless `--quit-after` run starts a game directly (`--new-game`), so it still exercises the map load.
2. A plain background for now; a loading screen when a game starts or loads.
3. A new `pause` action: Esc and the gamepad's Start button, named "Start button" in the controls view.
4. Five manual slots plus one autosave slot. Saves can be deleted, with a confirmation. The autosave is written when quitting to the menu or quitting the game. No quicksave for now.
5. Pausing works at any time, including during the death fall and the death screen, but Save is disabled unless the player is alive. Losing window focus opens the pause menu.
6. Play time is stored in saves and shown in the slot list.
7. Confirm before loading over a running game, before quitting to the menu and before quitting the game.
8. The controls view shows the bindings only; rebinding comes later.
9. Windowed sizes 1280×720, 1600×900, 1920×1080 and 2560×1440, limited to what fits the screen. Fullscreen uses the native size with a 3D render scale (50–100 %).
10. Windowed and borderless fullscreen only. Display changes apply at once, with a 10-second confirm-or-revert.
11. Three presets, Low, Medium and High, setting shadows, anti-aliasing and render scale. Default Medium. No frame-time target for now.
12. VSync (default on) and a frame cap (off, 30, 60, 120) in the display tab.
13. Buses Master, Music and Effects, defaults 100 %, 70 % and 80 %; ready for later audio.
14. Mouse and stick sensitivity sliders, 0.25× to 3× the current default.
15. The minimap is in the bottom-left corner by default.
16. Godot's default font, text names for gamepad inputs, placeholder colours. The real UI style comes with the art direction.
17. A save from an older pack uses the saved position if the spot is free, otherwise a normal spawn, keeping money, gear and wanted level, with a short message when the player was moved.
18. The release workflow writes the version from the `v*` tag into `application/config/version`; local builds show "dev". Saves store it, and the main menu shows it in a corner.
19. A custom user data folder named "RoadmanSimulator".
20. A `--menu` screenshot flag, so the menus are checked like the rest.

Filled in while writing the answers into the spec (2026-11-05); change them before implementation if they don't fit:

- Quitting during a death autosaves the post-death state (money 0, no gear, wanted level 0) with an empty position, so the next load spawns normally and a reload mid-fall can't dodge the loss; the save format allows an empty position (changed by the user, 2026-11-05).
- The window's close button and Cmd+Q / Alt+F4 count as quitting the game: the same confirmation and autosave.
- A `pause` press on the death screen opens the pause menu and never dismisses it; other keys still dismiss it.
- The confirm-or-revert dialog covers window mode, window size and render scale only; quality, vsync and the frame cap apply without it (changed by the user, 2026-11-05). Render scale multiplies the quality preset's factor; the preset values in the `QualityPreset` table are starting values for the playtest.
- `--menu` renders on `overview` in `/verify`, with sample saves in a temporary folder for `slots`.
