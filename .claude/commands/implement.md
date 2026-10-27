---
description: Implement an approved spec from docs/specs/, with tests
argument-hint: <spec>
disable-model-invocation: true
---

Implement the spec `docs/specs/$ARGUMENTS.md` (add `.md` if missing).

1. **Check the spec.** If it doesn't exist, or its status isn't `Approved`, stop and tell the user. Don't implement drafts.
2. **Plan.** Read `docs/architecture.md`, the ADRs in `docs/decisions/` and the CLAUDE.md files for the folders involved. Propose the change: files, scenes, signals, tests, and which acceptance criterion each test covers. Point out anything that conflicts with the architecture or an ADR. Wait for the user to approve the plan.
3. **Implement.** Delegate to the matching subagent: `godot-dev` for `game/`, `mapgen` for `tools/mapgen/` and `maps/`, and `tester` for tests. Keep the work to what one session can finish and verify. Ask before adding any dependency.
4. **Verify.** Run `/verify`. Fix every FAIL before going on. N/A steps are fine but list them.
5. **Review.** Have the `reviewer` subagent check the diff against the CLAUDE.md conventions, and fix what it finds.
6. **Report.** List the acceptance criteria and how each is covered, the verify summary, and what the user should playtest. Don't commit; the user commits after playtesting.
