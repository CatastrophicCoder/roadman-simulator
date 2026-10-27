---
description: Run build, tests, format checks, headless Godot import and screenshots, and report each step
allowed-tools: Bash(.claude/scripts/verify.sh *)
---

Run `.claude/scripts/verify.sh` from the repo root with the Bash tool, using a timeout of at least 600000 ms. The script always exits 0 and ends with a summary.

Report the summary as a table with one row per step: step, result (PASS, FAIL or N/A / not yet available) and a one-line detail. For each FAIL, read its log file and quote the relevant error lines.

Don't fix anything unless the user asked you to, or you're running this as part of `/implement`.
