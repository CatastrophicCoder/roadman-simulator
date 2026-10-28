# Spec: map loading and screenshots

## Status

Approved

## Goal

Load the generated map pack into the `World` scene, make regenerated `.glb` tiles always import the same way, and let Claude Code take screenshots from named camera presets so visual changes can be checked without the user. Covers Phase 4 steps 5, 6, 8 and 9 in `docs/project-outline.md`.

## Behaviour

### Camera presets (pipeline)

- Optional input `maps/<map-id>/camera_presets.json`:

  ```json
  {
    "schema_version": 1,
    "presets": {
      "overview": {"position": [0, 600, 500], "look_at": [0, 0, 0], "fov": 70, "ground_relative": true}
    }
  }
  ```

- Coordinates are Godot local metres (+X east, +Y up, −Z north). With `ground_relative`, `position.y` and `look_at.y` are heights above the terrain at that point's x/z.
- `mapgen build` validates the file and copies it unchanged into the pack. An invalid file fails the build; a missing file is allowed. The manifest is unchanged (schema v1).
- Somero has `overview` and `centre-street-level` (eye height 1.7 m near the origin, looking along the main street).

### Roadman.Core

- `MapManifestReader` parses `manifest.json`. It rejects a `schema_version` other than 1, missing fields, unexpected `axes`, and tile paths that are absolute or contain `..`, with a `MapPackException` naming the problem.
- `CameraPresetsReader` parses the presets file the same way.
- `ScreenshotArgs` parses the user args `--screenshot <preset> <out.png>`.
- Resolving a ground-relative preset's height takes the terrain hit, or no hit. With no hit the result is "outside built area".

### MapLoader

- A `Node3D` in `World`. The map id comes from `[Export] MapId` or, when that is empty, from the project setting `roadman/map/id` (default `"somero"`).
- Reads `res://maps/<id>/manifest.json` and instances every tile under a `Tiles` child, named by tile id, at the origin. Emits `MapLoaded(tileCount)`.
- Errors (missing pack, wrong schema version, missing tile) are pushed as errors with the pack path and the hint to run `/regen-map <id>`. The game does not crash.
- The debug camera starts above the terrain at the origin.

### Import presets

- `[importer_defaults]` for the scene importer in `project.godot`: name suffixes on (`-col`, `-colonly`), mesh compression off, LOD generation off, shadow meshes on, static light baking.
- They apply to every scene import in the project. Hand-made assets added later need their import settings corrected and their `.import` file committed (ADR 0008).

### Screenshot tool

- `godot --path game --resolution 1600x900 -- --screenshot <preset> <out.png>` loads the world, places the camera at the preset, waits for the scene to settle, saves the viewport to PNG and quits.
- Exit codes: 0 saved, 1 error (unknown preset, missing pack, headless display, flat-colour image), 2 the preset is outside the built area (no terrain under a ground-relative position), reported as N/A and no PNG written.
- `--headless` disables rendering; the tool refuses to run under it. The off-screen approach for macOS and CI is documented in `game/CLAUDE.md` and ADR 0008.

### /verify

- A screenshot step renders every Somero preset to `build/screenshots/<preset>.png`. PASS when the PNG exists and the log has no errors, N/A for exit code 2 or when Godot or the pack is missing, FAIL otherwise.

## Acceptance criteria

- `World` loads every tile listed in the manifest.
- A ray cast straight down at the map origin hits the terrain, within the vertical extent of the terrain mesh at that point.
- A manifest with another schema version is rejected with a clear error.
- Regenerated tiles import with compression and LODs off and collision from `-col` and `-colonly` names.
- `/verify` produces `build/screenshots/overview.png` and `build/screenshots/centre-street-level.png`.
- A ground-relative preset outside the built area is reported as N/A, not as a failure.
- Both screenshots have been looked at and described.

## Test ideas

- xUnit: manifest parsing (valid, wrong version, wrong axes, bad tile path), preset parsing, argument parsing, ground-relative height resolution with and without a hit.
- pytest: presets file copied, invalid file fails the build, missing file allowed.
- gdUnit4: all tiles loaded; terrain hit at the origin with the expected range taken from the terrain mesh AABB; a preset far outside the manifest bounds gives "outside built area".

## Open questions

- Whether macOS can render with the window placed off-screen; to be settled during implementation and recorded in ADR 0008.
