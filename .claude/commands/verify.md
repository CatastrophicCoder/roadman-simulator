---
description: Run build, tests, format checks, headless Godot import and screenshots, and report each step
allowed-tools: Bash(.claude/scripts/verify.sh *)
---

Run `.claude/scripts/verify.sh` from the repo root with the Bash tool, using a timeout of at least 600000 ms. The script always exits 0 and ends with a summary.

Report the summary as a table with one row per step: step, result (PASS, FAIL or N/A / not yet available) and a one-line detail. For each FAIL, read its log file and quote the relevant error lines.

The screenshot step renders each camera preset in the map pack (`MAP_ID`, default `somero`) to `build/screenshots/<preset>.png` with a short windowed Godot run; a window appears for a few seconds. When it passes after a visual change, open the PNGs and describe what they show. A preset outside the built area (for example in a test-area pack) is N/A, not FAIL.

Don't fix anything unless the user asked you to, or you're running this as part of `/implement`.
