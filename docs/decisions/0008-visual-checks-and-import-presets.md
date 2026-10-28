# 0008: Visual checks and import presets

Status: Accepted

## Context

Spec: `docs/specs/map-loading.md`.

The game now loads map packs (ADR 0006). Two things have to be settled for that to stay reliable:

- **Tile imports.** The pipeline replaces the whole pack folder on every build, so Godot's `.import` files under `game/maps/` are deleted each time (they are git-ignored anyway). Every regenerated tile is therefore a fresh import, using the project's import defaults.
- **Visual checks.** Claude Code has to be able to check visual results without the user, but `--headless` disables rendering.

## Decision

- **Import defaults.** `[importer_defaults]` in `project.godot` sets, for every scene import:
  - name suffixes on, so `terrain-col` gets mesh plus trimesh collision and `-colonly` gets collision only;
  - mesh compression off;
  - LOD generation off;
  - shadow meshes on;
  - static light baking.

  Compression quantizes vertex positions per tile bounding box, and auto LODs simplify each tile on its own. Both can move shared edge vertices, so neighbouring tiles would no longer meet exactly. The cost is more VRAM and larger imported files.
- **Scope of the defaults.** The defaults stay project-wide. Every hand-made asset added later (vehicles, props) picks up the tile settings on its first import. Its import settings have to be corrected then, for example by turning compression and LODs back on, and its `.import` file committed. That file overrides the defaults.
- **Camera presets.** Presets are map-specific input, so they live in `maps/<map-id>/camera_presets.json` (ADR 0002). The pipeline validates the file and copies it into the pack (ADR 0006 amendment). Positions can be ground-relative, so presets don't depend on absolute terrain heights.
- **Screenshot tool.** The command is `godot --path game -- --screenshot <preset> <out.png>`, a windowed run.
  - It renders the preset into a fixed-size `SubViewport`, waits for the scene to settle, saves a PNG and quits.
  - Exit codes: 0 saved, 1 error, 2 the ground-relative preset has no terrain under it ("outside built area").
  - `/verify` runs the tool for every preset in the pack and writes to `build/screenshots/`. Exit code 2 is reported as N/A.
- **Off-screen rendering.**
  - **macOS:** Godot has no off-screen display driver, so a window shows for about 3 s, and the run needs a logged-in GUI session. `--position` can't hide the window, because macOS moves it back on screen. Because the image comes from the `SubViewport`, it doesn't depend on the window's size or position.
  - **Linux CI:** `xvfb-run` with Mesa lavapipe (software Vulkan) for Forward+. `--rendering-driver opengl3` is the fallback. Not tested yet; CI setup is a later step.

## Consequences

- Regenerated tiles always import the same way, with no manual step.
- Adding a hand-made scene asset needs a deliberate look at its import settings.
- Screenshots on macOS briefly show a window, and can't run over SSH without a logged-in session.
- Screenshots from the opengl3 fallback aren't comparable with Forward+ ones.
- With a test-area pack, presets outside the area are N/A rather than failures.
