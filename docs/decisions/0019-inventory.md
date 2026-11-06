# 0019: Inventory

Status: Accepted

## Context

Milestone 2 asks for an inventory. `PlayerState` already held gear as a set of string ids, but nothing defined which ids exist, nothing could equip them, and the death screen listed raw ids. Weapons, outfits and pickups come in later specs, which need item types and equip slots to build on. Spec: `docs/specs/inventory.md` (decided 2026-11-06).

## Decision

- **Item types.** An item type has a lowercase slug id (the map id pattern), an English display name, a category (`Weapon`, `Clothing`, `Other`) and a one-line description. Display names are unique ignoring case, so no two items read alike on screen.
- **Catalogue as a code table.** `Roadman.Core.Items.ItemCatalog.Default` is a table in code. It ships in release builds. It moves to a data file only when the item list grows large.
- **One of each.** The player holds at most one of each item type. Quantities come with consumables or ammunition, with a save format step.
- **Two slots.** Exactly one `Weapon` slot (weapons) and one `Outfit` slot (one clothing item). `Other` items can't be equipped.
- **Invariant.** An equipped item is always held and fits its slot. Removing an equipped item empties its slot first. Only catalogue items can be held: `AddGear` rejects an unknown id.
- **Save format version 2.** `player.equipped` is an object with exactly `weapon` and `outfit`, each `null` or an id in `gear`. The 1 → 2 migration adds empty slots, so a version-1 save keeps its gear unequipped. The save format doesn't know the catalogue.
- **Unknown items on load.** `SaveItemCheck` drops gear ids the catalogue doesn't know and unequips a known item in a slot it doesn't fit. Each change is a warning, and a short notice tells the player. The save stays loadable and the file is unchanged until the next save.
- **Discarding destroys the item**, after a confirmation. Nothing drops into the world until pickups exist.
- **Death** empties the slots with the gear. The losses line names the items by display name, equipped ones first (`(equipped)`, slot order), then the rest by category and name.
- **Getting items.** Until pickups exist, only debug builds can get items: a debug section in the inventory screen, and the `--give <id,...>` user arg, applied once after the first placement.
- **Opening the screen.** The `inventory` action (Tab and the gamepad's Back button) and an Inventory button in the pause menu, both only while the player is alive and the player camera is current. The screen pauses the game like the pause menu. In the screen, Tab closes it instead of moving the focus. ADR 0018's "Back button", unbound until now, is bound to `inventory`.
- **Name checks in tests.** Test files may list brand names, other games' names and console makers' button names solely to check that they don't appear in game content (the item catalogue, the controls tab). This is the one exception to the root CLAUDE.md rule against such names. The lists hold only distinctive names, matched as whole words, so ordinary English words never trip the check.

## Consequences

- Pickups, shops and quest rewards add items through `AddGear` with catalogue ids.
- Weapon use and outfit visuals read `PlayerState.Equipped` and listen to `EquipmentChanged`.
- A new item type is a catalogue row; removing or recategorising one is handled for old saves by the load cleanup, with no format step.
- Item quantities need a save format version step and a migration.
