---
description: Draft a feature spec in docs/specs/ (no code)
argument-hint: <feature>
disable-model-invocation: true
---

Draft a spec for the feature `$ARGUMENTS` in `docs/specs/$ARGUMENTS.md`. Use a lowercase, hyphenated file name.

Before writing, read `docs/vision.md` (scope limits), `docs/architecture.md`, the ADRs in `docs/decisions/` and any related specs in `docs/specs/`. If the feature is outside the version 1 scope in `docs/vision.md`, say so and stop.

Write the spec with these sections:

1. **Status**: `Draft`. Only the user changes it to `Approved`.
2. **Goal**: what the feature is for, in two or three sentences.
3. **Behaviour**: what the player sees and what the systems do, including edge cases.
4. **Acceptance criteria**: a numbered list of checks, each one verifiable by a test, a headless run or a screenshot.
5. **Test ideas**: which layer covers each criterion (pytest, `Roadman.Core` xUnit, gdUnit4, screenshot, playtest).
6. **Open questions**: anything you can't decide from the docs. Don't guess.

Don't write or change any code, scenes or config. When done, give a short summary and the open questions, and wait for the user to edit and approve the spec.
