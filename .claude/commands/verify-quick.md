---
description: Quick verify for iterating - only the gdUnit4 groups the changed files affect, no export check or screenshots
argument-hint: "[<base>]"
allowed-tools: Bash(.claude/scripts/verify.sh *)
---

Run `.claude/scripts/verify.sh --quick $ARGUMENTS` from the repo root with the Bash tool, using a timeout of at least 600000 ms. The script always exits 0 and ends with a summary.

Quick mode (`docs/specs/test-performance.md`, ADR 0020) is for iterating. It takes the changed files from the working tree against `HEAD`, or against `<base>` (a commit, branch or tag) when one is given, plus untracked files that aren't ignored. It maps them to gdUnit4 groups with the rules in `game/tests/test-groups.json` and runs only those suites. A path that matches no rule, a change to shared code, or a git state it can't read selects every suite. Docs alone select none. The other steps run as in the full mode, with two exceptions: pytest runs only when a pytest input changed, and the export check and the screenshots are N/A. Files ignored by git, such as a regenerated pack in `game/maps/`, are invisible to the selection.

To see what would run without running anything, use `.claude/scripts/verify.sh --quick --dry-run [<base>]`.

Report the summary as a table with one row per step: step, result (PASS, FAIL, WARN or N/A) and a one-line detail. Name the selected gdUnit4 groups and the path that selected each. For each FAIL, read its log file and quote the relevant error lines.

**A quick run doesn't meet the definition of done.** Before every commit, run the full `/verify`. `/implement` does this.

Don't fix anything unless the user asked you to, or you're running this as part of `/implement`.
