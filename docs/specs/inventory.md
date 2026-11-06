# Spec: inventory

## Status

Approved

## Goal

Give the player gear they can hold, see and equip. Item types have ids, display names, a category (weapon, clothing, other) and a one-line description. There is one weapon slot and one outfit slot, and an inventory screen to view, equip and discard gear with the mouse, the keyboard or a gamepad. Gear and what is equipped are saved with the game and lost on death under the existing rule, and the death screen names the lost items. Covers "Inventory" under "Game features" in `docs/project-outline.md` and the "inventory" part of milestone 2. The state logic (item types, the catalogue, equipping, the save format change, the load cleanup) lives in `Roadman.Core` with xUnit tests; Godot holds only the screen, the input that opens it and the debug tools.

Out of scope (later specs): using weapons (aiming, attacking, damage) and any effect of an equipped item on gameplay, the wanted level or AI; pickups, dropping items into the world and any other way to get items in a release build (quest system spec); item quantities (when consumables or ammunition need them, with a save format upgrade); shops and money spent on items; a money HUD; item models, icons and any change to the player model (character kit, art direction); the tutorial's "equip gear" quest step (quest system spec; this spec only provides the event it listens to); rebinding controls; a data file for the catalogue (when the item list grows large); any pipeline or map pack change.

## Context (2026-11-06)

- `PlayerState` (`Roadman.Core/PlayerState.cs`) holds `Gear`, an ordinal set of string ids, with `AddGear` and `RemoveGear` (blank ids throw; each returns whether it changed anything and raises `GearChanged` only then). Nothing defines which ids exist, and nothing calls `AddGear` outside tests. There is no equipping.
- `Die()` clears money and gear and resets the wanted level. `DeathSequence.EnterVoid` records `DeathLosses(Money, Gear)` (gear ids sorted ordinal) before calling it.
- `DeathScreenText.Format(losses)` gives `Money lost: <amount> · Gear lost: <ids>` with the ids comma-separated in ordinal order, or `none`. `docs/specs/world-edge-game.md` says display names come with this spec.
- Saves (`Roadman.Core/Saves/`, ADR 0018): `SaveFormat.CurrentVersion` is 1. `SavePlayer` has `position`, `facing_yaw`, `money`, `gear` (non-blank, unique ids, written sorted) and `wanted_level`. The reader is strict; older versions go through `SaveMigrations.Default`, which has no steps yet. ADR 0018's consequences say new saved state needs a format version step and a migration. `PlayerState.Snapshot`/`Restore`/`Reset`; `SaveFactory.Make` builds a save. `Main.Notice` shows short notices (the "map has changed" one).
- `GameState` forwards `PlayerState` events to `Events` (`MoneyChanged`, `GearChanged`, `WantedLevelChanged`, `PlayerDied`).
- UI (`scripts/ui/`): `UiTheme`, `UiFocus` (explicit, wrapping focus neighbours), `UiBuild`, `MainMenu`, `PauseMenu` (Resume, Save, Load, Settings, Quit to menu), `SlotList`, `SettingsView`, `ConfirmDialog`. Disabled buttons have `FocusMode.None`; the first enabled control gets focus. `pause` (Esc, Start button) opens the pause menu while `Playing`; in the menus `pause` or `ui_cancel` is `Back()`. `Resume()` releases the actions pressed that frame and calls `World.OnResumedFromPause()`. While paused, `World.Hud` is hidden.
- Godot's built-in `ui_focus_next` holds Tab (and `ui_focus_prev` Shift+Tab).
- The controls tab lists every non-debug action from `InputMap` with a label from `ControlNames`; the gamepad's Back button (`JoyButton.Back`, button 4) is named "Back button" there (ADR 0018) and bound to nothing yet.
- `--screenshot ... --menu <main|pause|settings|slots>` renders a menu into the capture viewport (debug builds only); `/verify` runs each on `overview`.
- The player is a placeholder capsule model; the character kit is undecided.

## Behaviour

### Roadman.Core

New folder **`Roadman.Core/Items/`**, engine-free.

- **`ItemCategory`**: `Weapon`, `Clothing`, `Other`.
- **`EquipSlot`**: `Weapon` and `Outfit`, exactly (decided). `Weapon` items go in `Weapon`, `Clothing` items in `Outfit`; `Other` items can't be equipped. An outfit is one clothing item.
- **`ItemType`**: an immutable record with `Id` (a lowercase slug, the same pattern as map ids), `DisplayName` (non-blank, English), `Category` and `Description` (one line of English: non-blank, no line break).
- **`ItemCatalog`**: the set of known item types. Built from a list of `ItemType`s, it rejects (naming the id) a non-slug id, a repeated id, a blank display name, a display name used twice (case-insensitive, so the screen and the losses line never show two items with one name), a blank description or one with a line break, and an undefined category value. `TryGet(id)`, `Get(id)` (throws on an unknown id), `Contains(id)`, `All` (sorted by category in `ItemCategory` order, then display name ordinal-ignore-case, then id), `SlotFor(itemType)` (the slot or none). Tests build their own catalogues.
- **`ItemCatalog.Default`**: the game's catalogue, a code table in `Roadman.Core/Items/` (decided; a data file only if the list grows large). It ships in release builds too. The placeholder items (generic names, no brands or other games' items):

  | Id | Display name | Category | Description |
  | --- | --- | --- | --- |
  | `baseball_bat` | Baseball bat | Weapon | A wooden bat. Not for baseball. |
  | `crowbar` | Crowbar | Weapon | Opens things that were meant to stay shut. |
  | `tracksuit` | Tracksuit | Clothing | Matching top and bottoms, slightly too shiny. |
  | `hoodie` | Hoodie | Clothing | Hood up, head down. |
  | `phone` | Phone | Other | Cracked screen, one bar of signal. |
  | `bike_lock` | Bike lock | Other | A heavy chain lock. The bike is long gone. |

  The descriptions are placeholder text and name or hint at no brand.
- **`PlayerState`** gets a catalogue (constructor argument; `GameState` passes `ItemCatalog.Default`) and:
  - `AddGear(id)`: an id not in the catalogue throws `ArgumentException` naming it and changes nothing. Otherwise as today: one of each item type (decided), a second add returns false and raises nothing.
  - `Equipped`: the item id in each `EquipSlot`, or none. `EquippedIn(slot)`, `IsEquipped(id)`.
  - `Equip(id)`: the item must be held and have a slot; it goes into that slot, replacing whatever was there (the replaced item stays held). Returns whether anything changed; equipping the item already in its slot changes nothing. An item not held, an unknown id or an `Other` item throws (`InvalidOperationException` for not held and `Other`, `ArgumentException` for unknown, with the id in the message) and changes nothing.
  - `Unequip(slot)`: empties the slot; returns whether it changed.
  - `EquipmentChanged(slot)`: a new event, raised once per slot whose content changes. `GameState` forwards it as a new `Events.EquipmentChanged(slot)` signal (as an int); the quest system's "equip gear" step will listen to it.
  - **Invariant: an equipped item is always held and fits its slot.** `RemoveGear` of an equipped item empties its slot first (`EquipmentChanged`, then `GearChanged`). Discarding (see "Inventory screen") is `RemoveGear`.
  - `Die()` empties every slot (`EquipmentChanged` for each slot that held something), then clears money and gear and resets the wanted level, as now; `Died` last. `Reset()` empties the slots. `Restore()` sets the slots after the gear, with the usual change-only events, and never raises `Died`.
- **`DeathLosses`** gets the equipped items: `DeathLosses(Money, Gear, Equipped)`, recorded by `DeathSequence.EnterVoid` before `Die()`.
- **`DeathScreenText.Format(losses, catalogue)`**: `Money lost: <amount> · Gear lost: <items>`, the items comma-separated: first the equipped ones in slot order (`Weapon`, `Outfit`), each as `<name> (equipped)`, then the rest by category (`Weapon`, `Clothing`, `Other`) and display name (ordinal-ignore-case), as `<name>`; `none` when there are none. An id the catalogue doesn't know (it can't be held; a guard only) shows as the id, after the known ones. Example: `Money lost: 120 · Gear lost: Crowbar (equipped), Tracksuit (equipped), Baseball bat, Phone`.

### Save format, version 2

- `SaveFormat.CurrentVersion` becomes 2. `player` gets one new key, `equipped`, an object with exactly the keys `weapon` and `outfit`, each `null` or an item id:

  ```json
  "player": {
    "position": [12.5, 101.2, -40.0],
    "facing_yaw": 1.5708,
    "money": 120,
    "gear": ["crowbar", "phone", "tracksuit"],
    "equipped": { "weapon": "crowbar", "outfit": "tracksuit" },
    "wanted_level": 0
  }
  ```

- `SavePlayer` gets `Equipped`. `SavePlayer.Problem` (shared by the reader, the writer and `Restore`) adds structural rules only: `equipped` is present with exactly `weapon` and `outfit` (a missing, unknown or repeated key is an error); each value is `null` or a non-blank id that is in `gear`. The save format doesn't know the catalogue: unknown ids and an item in the wrong slot are not format errors (see "Loading").
- **Migration 1 → 2** (`SaveMigrations.Default`, the first registered step): adds `"equipped": {"weapon": null, "outfit": null}` to `player`. The rest of a version-1 save is unchanged, so its gear is kept unequipped.
- An autosave written during a death (ADR 0018) holds no gear and empty slots, since `Die()` has already run.

### Loading: unknown items

- New **`Roadman.Core.Items.SaveItemCheck.Clean(savePlayer, catalogue)`** gives the cleaned player part and what it removed: every gear id not in the catalogue is dropped (and its slot emptied), and a known item in a slot that doesn't fit its category (only possible in an edited file or after a category change in a later version) is unequipped and kept as held gear.
- `Main` runs it before `PlayerState.Restore` on every load. When it removed anything, each change is pushed once as a warning (`push_warning`, naming the slot file and the ids), and `Main.Notice` shows "Some items in this save no longer exist and were removed." (also when only a slot was emptied: "Some items in this save were unequipped."; when both happen, the first). The save stays loadable; the file isn't changed until the next save.
- The slot list doesn't check items: such a save shows as `Valid`.

### Getting items: debug tools

Pickups come with the quest system. Until then a release build has no way to get items. Debug builds have two:

- **Debug section in the inventory screen** (only when `DebugBuild.IsDebug`): below the player's gear, a "Debug" heading and one row per catalogue item (`ItemCatalog.All` order) with its name, category, **Add** (disabled while held) and **Remove** (disabled while not held). Add calls `AddGear`, Remove calls `RemoveGear` (no confirmation; an equipped item is unequipped first, as always).
- **`--give <id,...>`** user arg (`Roadman.Core.Tools.GiveArg`): comma-separated ids, no spaces, each a known catalogue id; repeated ids count once. Parsed by `Main` (and `World` in direct `World` runs and screenshot runs); applied once, through `AddGear` in the order given, right after the first player is placed (new game, load or direct start), before `GameStarted`; not on a respawn. A release build, an empty list, a blank entry or an unknown id is an error: one error naming the problem and the known ids is pushed and nothing is given; the game starts as usual. With `--screenshot`, an argument error exits 1 like the other argument errors.

### Inventory screen

**Opening** (decided: both ways).

- New action **`inventory`**: Tab (physical keycode) and the gamepad's Back button (`GamepadButtons.Back`, button 4, "Back button"), device −1. While `Playing`, `Main` opens the screen on `inventory` when the death sequence is `Alive`, a player exists and the player camera is current (not the debug camera, not the death camera); otherwise the press does nothing. Not while loading.
- New **Inventory** button in the pause menu, second: Resume, Inventory, Save, Load, Settings, Quit to menu. Disabled (not focusable) unless the same conditions hold; when the player isn't `Alive` it shows the hint "You can use the inventory when you're not falling off the world" (no hint while the debug camera is on; it is a debug state). It opens the screen over the paused game.

**Pausing** (decided). Opening from the game pauses the tree exactly as the pause menu does: `GetTree().Paused`, play time stops, the street label is hidden, the mouse is released (visible) and the player camera remembers that it held the capture. A new `MainState.Inventory` holds while the screen is open from the game; opened from the pause menu, the state stays `Paused`.

**Closing.**

- Opened from the game: **Close**, `inventory`, `pause` or `ui_cancel` (Esc, Tab, Start button, Back button, east face button) closes the screen and resumes as `Resume()` does (the actions pressed that frame released, `World.OnResumedFromPause()`, so the closing press doesn't jump, toggle running or dismiss anything; the camera captures the mouse again only if it held it, is current, has input and the window has focus).
- Opened from the pause menu: the same inputs return to the pause menu, with focus on its Inventory button.
- While a confirmation dialog shows, back closes the dialog only.
- In the screen, Tab is `inventory` and closes it; it doesn't move focus (focus moves with the arrow keys, the d-pad and the left stick). Elsewhere in the menus Tab keeps Godot's `ui_focus_next`.
- Window focus loss while the screen is open from the game changes nothing (the game is already paused).

**Layout** (built in code, `scripts/ui/InventoryScreen.cs`, on `UiTheme`, our own look, English text):

- Title "Inventory".
- **Equipped**: one row per slot, in slot order: the slot name ("Weapon", "Outfit"), the equipped item's name or "Empty", and **Unequip** (disabled when empty).
- **Items**: a heading per category in the order Weapons, Clothing, Other, each listing the held items of that category (`ItemCatalog.All` order); a category with no items is left out. Each row has, from left to right: an empty square reserved for an icon (a fixed size from `UiTheme`, drawn as nothing for now), the display name with "Equipped" after it when equipped, the description below the name in the theme's muted text, and the buttons **Equip** / **Unequip** (weapons and clothing only) and **Discard**. With no gear at all: "You have no gear."
- The **Debug** section (debug builds only; see "Getting items").
- **Close** at the bottom.
- The content scrolls in a `ScrollContainer`; the focused control is always scrolled into view.

**Use.**

- Mouse, keyboard and gamepad alone each reach every enabled control (`UiFocus`, wrapping); focus starts on the first enabled control (the first item's first button, else the first debug button in a debug build, else Close).
- **Equip** puts the item in its slot (replacing the one there, which stays held); **Unequip** empties the slot. The screen updates at once and focus stays on the same row's button.
- **Discard** asks "Discard <name>? It will be gone for good." (**Discard**, **Cancel**; Cancel and back keep it). Confirming calls `RemoveGear` (unequipping first); the item disappears (nothing drops into the world). Focus moves to the next row's first button, else the previous row's, else the first enabled control.
- The screen follows `GearChanged` and `EquipmentChanged` while it shows (the debug section, `--give` is never live), keeping focus on the same control where it still exists, else the first enabled control.

### Interaction with existing systems

- **Death:** the void entry records the losses (with the equipped items), then `Die()` empties the slots and the gear. The death screen's losses line names them as above. The screen can't be open then: it opens only while `Alive`, and the game is paused while it is open, so nothing can kill the player meanwhile.
- **New game:** `Reset()` gives no gear and empty slots; then `--give` if given.
- **Save and load:** gear and slots are written and restored (format version 2); a version-1 save keeps its gear, unequipped; unknown items are dropped on load with a warning and the notice.
- **Controls tab:** `inventory` shows as "Inventory" with "Tab" and "Back button", read from `InputMap` as the others are.
- **Equipping has no gameplay or visual effect** in this spec: no weapon use, no stat, no change to the wanted level, no change to the player model.

### Screenshots

- New `--menu inventory` value (debug builds only, the same rules and exit codes as the other `--menu` values): the inventory screen, opened from the game, over the paused game at the preset's camera. The gear comes from `--give`; the tool then equips the first weapon and the first clothing item given, so the image doesn't depend on any save. It prints `Screenshot: --menu inventory`. Without `--give` it shows the empty screen.
- `/verify` runs it on `overview` with `--give baseball_bat,crowbar,tracksuit,hoodie,phone` (`menu-inventory.png`); the capture shows the top of the screen (the debug section may be below it).

### Docs

- New ADR 0019 (inventory): item types with slug ids, display names, three categories and a one-line description; the catalogue as a code table in `Roadman.Core` shipping in release builds; one of each item type; exactly one weapon and one outfit slot; an equipped item is always held; save format version 2 with the `equipped` object and the 1 → 2 migration; unknown items dropped on load with a warning and a notice, an unknown id in `AddGear` an error; discarding destroys the item; death empties the slots with the gear and the losses line lists equipped items first; the debug section and `--give` (debug builds only); the `inventory` action (Tab, Back button) and the pause-menu button, both only while alive on the player camera, pausing the game; "Back button" now bound.
- `docs/architecture.md`: `Roadman.Core/Items/`, `PlayerState`'s slots and `EquipmentChanged`, the save format version 2 and the load cleanup, the inventory screen under Gameplay.
- `game/CLAUDE.md`: an "Inventory" section; the `Events` list; `inventory` in the controls list under "Player"; `MainState.Inventory` and the pause menu's buttons under "Menus, settings and saves"; `--give` and `--menu inventory` under "Screenshots".
- `docs/specs/world-edge-game.md`: an amendment noting that the losses line now shows display names, equipped items first (its criterion 5's ids become names).
- `docs/specs/menu-settings-save.md`: an amendment noting save format version 2 and its migration, the pause menu's sixth button (its criterion 11's "five buttons") and the load notice for removed items.

## Acceptance criteria

1. `ItemCatalog` accepts a valid list and rejects, naming the id: a non-slug id, a repeated id, a blank display name, a display name used twice (differing only in case), a blank description, a description with a line break, and an undefined category value. `TryGet`, `Get` (throws on an unknown id), `Contains`, `All` (category, then display name, then id) and `SlotFor` (`Weapon` → `Weapon`, `Clothing` → `Outfit`, `Other` → none) behave as described.
2. `ItemCatalog.Default` holds exactly the six placeholder items of the table, passes criterion 1's checks, and no id, name or description contains a word from a list of brands and other games' names (test with a word list, plus the reviewer).
3. `PlayerState.AddGear` with an id not in the catalogue throws, naming it, and changes nothing; a known id is added once (a second add returns false, no event).
4. `PlayerState.Equip`: equipping a held weapon fills `Weapon` and raises `EquipmentChanged(Weapon)` once; equipping the other held weapon replaces it (one event; both stay held); equipping the same item again changes nothing and raises nothing; an item not held, an unknown id and an `Other` item throw and change nothing. `Unequip` empties the slot with one event, and on an empty slot changes nothing.
5. `RemoveGear` of an equipped item empties its slot, raising `EquipmentChanged` before `GearChanged`; of an unequipped item it leaves the slots alone. After a seeded random sequence of `AddGear`, `RemoveGear`, `Equip`, `Unequip`, `Restore`, `Reset` and `Die` calls, every equipped item is held and fits its slot.
6. `Die()` empties both slots (one `EquipmentChanged` per slot that held an item), clears money and gear, resets the wanted level and raises `Died` last. `DeathSequence.EnterVoid` records the gear and the equipped items held before.
7. `DeathScreenText.Format(losses, catalogue)` gives `Money lost: 120 · Gear lost: Crowbar (equipped), Tracksuit (equipped), Baseball bat, Phone` for money 120, `crowbar` and `tracksuit` equipped, `baseball_bat` and `phone` held; equipped items in slot order, the rest by category then name (a test where name order and id order differ); `Gear lost: none` with money only; `Money lost: 0 · Gear lost: none` with nothing.
8. Save format 2: `SaveWriter` then `SaveReader` round-trips a save with three gear ids, a weapon and an outfit equipped, and one with both slots `null`, to equal values; the `equipped` keys are `weapon` and `outfit`. The reader rejects, with the key path (`player.equipped...`): a missing `equipped`, a missing, unknown or repeated slot key, a blank id and an equipped id not in `gear`. It accepts an unknown id and a clothing item in `weapon` (catalogue checks are the load cleanup's). `format_version` 3 is rejected as newer.
9. Migration: a version-1 save with gear reads through `SaveMigrations.Default` as version 2 with the same gear, both slots `null`, and every other value unchanged.
10. `PlayerState.Snapshot(true)` then `Restore()` on a fresh state gives an equal state, slots included; `Restore` raises `EquipmentChanged` only for slots that change and never `Died`; an invalid snapshot (an equipped item not in its gear) throws and changes nothing. `Reset()` empties the slots.
11. `SaveItemCheck.Clean`: a save with an unknown id (held, and equipped in `weapon`) gives the known gear only, `weapon` empty, and reports the id; a known clothing item in `weapon` is unequipped and kept, and reported; a clean save comes back unchanged with nothing reported.
12. Loading such a save in `Main` (gdUnit4) loads it, pushes one warning per change naming the slot file and the ids, and shows the notice; the player holds the known items only, and the slot list showed the save as `Valid`.
13. `GiveArg`: `--give crowbar,phone` gives both, in order, after the first placement and before `GameStarted`, not again on a respawn; repeated ids count once; an unknown id, a blank entry or an empty list pushes one error naming the problem and the known ids, gives nothing, and the game starts; in a release build (`DebugBuild.Override`) it pushes an error and gives nothing; with `--screenshot` an argument error exits 1.
14. Opening from the game (gdUnit4, `Main` on the test-area pack): `inventory` (simulated Tab and simulated Back button) while `Alive` with the player camera current opens the screen, pauses the tree, releases the mouse, hides the street label and sets `MainState.Inventory`; over 60 frames the player's position, physics bodies and play time don't change. `inventory` does nothing during `Falling`, on the death screen and while the debug camera is current.
15. Closing: Close, `inventory`, `pause` and `ui_cancel` each close it and unpause; the player camera wants the capture again when it held it before; the closing press doesn't make the player jump or toggle running. While the discard confirmation shows, back closes only the dialog. Tab in the screen doesn't move focus.
16. Pause menu: six buttons in the order Resume, Inventory, Save, Load, Settings, Quit to menu. Inventory opens the screen over the paused game; back, `pause` and Close return to the pause menu with focus on Inventory. It is disabled during `Falling` and on the death screen (with the hint) and while the debug camera is current.
17. The screen with no gear shows "You have no gear."; with the gear of criterion 7 it lists the slots with the equipped names, the items under their category headings in order, each with its description, "Equipped" on the equipped ones, Equip/Unequip on weapons and clothing only, Discard on every item, and an icon space of the same size on every row.
18. Equip, Unequip and Discard through the screen (simulated `ui_accept` on the focused button, and a simulated mouse click) change `PlayerState`, update the rows at once and move focus as described. Discard asks first; Cancel keeps the item; confirming on an equipped item unequips and removes it.
19. Debug section: present only when `DebugBuild.IsDebug` (checked with `DebugBuild.Override`), one row per catalogue item; Add and Remove change `PlayerState` and the rows at once; Add is disabled while held, Remove while not held.
20. Keyboard alone and gamepad alone (simulated `ui_*` events) reach every enabled control of the screen and its dialog; focus starts on the first enabled control; with more rows than fit, the focused control is scrolled into view.
21. Save and load round trip (gdUnit4): with `crowbar` and `tracksuit` equipped and `phone` held, saving to a slot, then unequipping, discarding an item and dying, then loading that slot gives the same gear and slots, and the screen shows them.
22. Death in the game: with an item equipped and two held, entering the void empties the slots and the gear; the death screen's losses line is as in criterion 7; after the respawn the screen shows "You have no gear."
23. The controls tab lists `inventory` as "Inventory" with "Tab" and "Back button"; no gamepad label contains a console maker's button names (the existing word list).
24. Screenshot `--menu inventory --give baseball_bat,crowbar,tracksuit,hoodie,phone` on `overview` shows the screen as described, with the bat and the tracksuit equipped, readable, in the shared flat style. Checked by eye. Outside a debug build, or with another debug flag, it exits 1 like the other `--menu` values.
25. No code, asset or text names or imitates another game or a brand (reviewer check); gamepad inputs are named generically.
26. `/verify` passes: dotnet build, test and format, pytest and ruff (unchanged), the headless import, gdUnit4, the headless startup, the export check and the screenshots, including `menu-inventory.png`.
27. Playtest (the user): opening, browsing, equipping, discarding and closing feel natural with mouse, keyboard and gamepad; Tab and the Back button don't clash with movement or the camera; the death screen's losses line reads well.

## Test ideas

| Criteria | Layer |
| --- | --- |
| 1, 2 | `Roadman.Core` xUnit: `ItemCatalog` on literal lists; `ItemCatalog.Default` with a word list |
| 3, 4, 5, 6, 10 | xUnit on `PlayerState` with a test catalogue, counting events in order; a seeded random sequence for the invariant; `DeathSequence` for the losses |
| 7 | xUnit on `DeathScreenText.Format` |
| 8, 9 | xUnit on `SaveReader`/`SaveWriter` with literal JSON, and a version-1 file through `SaveMigrations.Default` |
| 11 | xUnit on `SaveItemCheck` |
| 13 | xUnit on `GiveArg` parsing; gdUnit4 for applying it and the release case |
| 12, 14 to 22 | gdUnit4: `Main` on the test-area pack with `MainTestSupport` (temporary saves folder and settings path), simulated input events, `DebugBuild.Override` for 19, a hand-written save file for 12 |
| 23 | gdUnit4, as the existing controls-tab test |
| 24 | screenshot via `/verify`, viewed |
| 25 | reviewer |
| 26 | `/verify` |
| 27 | playtest |

## Open questions

None. Resolved 2026-11-06 by the user:

1. Both: a new `inventory` action (Tab and the gamepad's Back button) and an Inventory button in the pause menu.
2. The world pauses while the inventory screen is open, like the pause menu.
3. It opens only while the player is alive and the player camera is active: not during the death fall or death screen, and not while the debug camera is on. From the pause menu it opens over the paused game.
4. One of each item type for now, as gear works today. Quantities come when consumables or ammunition need them; the save format can be upgraded then.
5. A code table in `Roadman.Core` for now; move to a data file if the item list grows large.
6. Unknown item ids in a save are dropped on load with a warning and a short notice; the save stays loadable. Adding an unknown id through the debug tool is an error.
7. Exactly one weapon slot and one outfit slot.
8. The player can discard an item, with a confirmation; it disappears. Dropping items into the world comes with pickups. The debug tool can also remove items.
9. Equipping doesn't change the player model yet; that comes with the character kit.
10. A debug section in the inventory screen (debug builds only) listing every item type with Add and Remove, plus a `--give <id,...>` argument for tests and screenshots.
11. The proposed placeholder set is fine. The item definitions ship in release builds too, but release builds have no way to get items until pickups exist.
12. Losses line: equipped items first, marked "(equipped)", then the rest by category and name.
13. Display name, category and a one-line description. Leave room in the layout for an icon later.

Filled in while writing the answers into the spec (2026-11-06); change them before implementation if they don't fit:

- `AddGear` itself rejects ids not in the catalogue (not only the debug tools), so the invariant "only known items are held" holds everywhere. The save reader stays catalogue-free; unknown ids and an item in the wrong slot are handled by `SaveItemCheck` at load. A known item in the wrong slot is unequipped and kept.
- The notice texts: "Some items in this save no longer exist and were removed." and "Some items in this save were unequipped."
- The pause menu's Inventory button is second, after Resume, and has the "not falling off the world" hint when not alive (none for the debug camera).
- In the inventory screen, `pause`, `ui_cancel` and `inventory` all close it (to the game or to the pause menu); Tab there doesn't move focus.
- `--give` applies once after the first placement (new game, load or direct start), never on a respawn; an argument error gives nothing and doesn't stop the game, except in a `--screenshot` run (exit 1).
- `--menu inventory` equips the first weapon and the first clothing item given with `--give`; `/verify` gives five of the six items.
- The placeholder descriptions in the item table.
- Equipped items in the losses line are in slot order (weapon, then outfit).
