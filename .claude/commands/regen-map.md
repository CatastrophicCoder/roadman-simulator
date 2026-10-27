---
description: Run the map pipeline and the Godot import for one map
argument-hint: <map-id>
disable-model-invocation: true
allowed-tools: Bash(.claude/scripts/regen-map.sh *)
---

Regenerate the map pack for `$ARGUMENTS`: run `.claude/scripts/regen-map.sh $ARGUMENTS` from the repo root with the Bash tool, using a timeout of at least 600000 ms.

The script runs `uv run mapgen build <map-id>` (output in `game/maps/<map-id>/`), then a headless Godot import. Each step prints PASS, FAIL or N/A.

Report each step's result. For a FAIL, read the log and quote the relevant error lines. Never fix a problem by editing `game/maps/`; fix the pipeline or `maps/<map-id>/` and run this again.
