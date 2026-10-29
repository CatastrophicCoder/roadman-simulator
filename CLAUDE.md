# Roadman Simulator: Somero-City

A small open-world crime game in Godot 4 (.NET, C#) set in an accurate model of Somero town centre. A Python pipeline generates the map from OpenStreetMap and Maanmittauslaitos (MML) open data. Targets: macOS and Windows desktop. Free tools only.

The first goal is a drivable Somero centre. Gameplay comes after the map pipeline works.

Version 1 scope: one map area with no interiors, a few vehicle types, one player character, simple traffic and pedestrian AI, a wanted level with police, and a handful of missions. Don't add anything beyond this. See `docs/vision.md`.

## Repo map

| Path | Contents |
| --- | --- |
| `docs/project-outline.md` | Source of truth for the project plan |
| `docs/vision.md` | Concept, fixed decisions, scope limits |
| `docs/architecture.md` | Systems and data flow (check plans against it) |
| `docs/specs/` | One spec per feature, approved before coding |
| `docs/decisions/` | ADRs; follow them |
| `maps/<map-id>/` | All map-specific input: `map.yaml`, `markers.json`, `overrides/` |
| `tools/mapgen/` | Python map pipeline, no map-specific code. See `tools/mapgen/CLAUDE.md` |
| `game/` | Godot project and C# solution. See `game/CLAUDE.md` |
| `game/maps/<map-id>/` | Generated map packs (pipeline output, git-ignored) |
| `.claude/` | Claude Code settings, commands, agents |
| `.github/` | CI and release workflows and their scripts (ADR 0010) |
| `CREDITS.md` | Data, engine, add-on and asset attributions |

## Commands

Build and test commands are listed in `tools/mapgen/CLAUDE.md` and `game/CLAUDE.md`. Run all checks at once with `/verify` (or `.claude/scripts/verify.sh`).

CI (`.github/workflows/ci.yml`) runs the same checks on every push and pull request, without screenshots, against a test-area pack built from the committed fixture. Pushing a tag `v*` runs `release.yml`, which fetches each map's pinned raw data snapshot (`snapshot` in `map.yaml`), builds the packs from it with the current pipeline, and publishes Windows and macOS builds with the download manifests. It needs no secrets. A tag with `-` (for example `v0.1.0-test1`) makes a pre-release. Snapshots live on `data-<map-id>-<date>` releases, which never become the latest release and don't run CI. See ADRs 0010 and 0011.

## Definition of done

- Tests pass (pytest, `dotnet test`, gdUnit4, whichever apply).
- Linters are clean (`ruff`, `dotnet format --verify-no-changes`).
- A headless Godot run finishes without errors.
- For visual changes, a screenshot has been taken and checked.

## Rules

- Never edit `game/maps/` by hand. Change the pipeline or `maps/<map-id>/` and regenerate.
- Never commit raw data (`tools/mapgen/data/raw/`).
- External data is fetched once. Builds, `/verify`, `/regen-map` and `mapgen snapshot create` use only the local data in `tools/mapgen/data/raw/<map-id>/` and never contact Overpass or MML. Never run `mapgen download` or any other external data fetch unless the user explicitly asks. `mapgen snapshot fetch` is for fresh clones and CI and never replaces local data without `--force`. Never run `mapgen snapshot publish` without asking first (ADR 0011).
- Ask before adding any dependency (NuGet, Python package, Godot add-on, GitHub Action).
- No other games' names or trademarks in code, assets or text.
- Real business names and logos from OSM are replaced with fictional ones.
- Record new decisions as ADRs in `docs/decisions/`.
- Record the licence of every new asset or add-on in `CREDITS.md`.
- Commit messages and pull request descriptions describe only the change itself. No `Co-Authored-By` trailers, "Generated with" lines or any other mention of Claude or AI tools.

## Working with Claude Code

Each feature goes through the loop in `docs/project-outline.md` ("Claude Code workflow per feature"): spec, plan, implement, verify, playtest, review and commit.

**Slash commands** (`.claude/commands/`)

| Command | What it does |
| --- | --- |
| `/spec <feature>` | Drafts `docs/specs/<feature>.md` (goal, behaviour, acceptance criteria, test ideas, open questions). No code. Status stays `Draft` until the user approves it. |
| `/implement <spec>` | Implements an `Approved` spec with tests: plan, subagents, `/verify`, reviewer. Doesn't commit. |
| `/verify` | Runs `.claude/scripts/verify.sh`: dotnet build, test and format check, pytest and ruff, headless Godot import, gdUnit4, an exported Windows build check, screenshots. Reports PASS, FAIL or N/A (not yet available) per step. |
| `/regen-map <map-id>` | Runs `.claude/scripts/regen-map.sh`: `uv run mapgen build <map-id>`, then the headless Godot import. |

`/spec`, `/implement` and `/regen-map` only run when the user types them. Claude may run `/verify` on its own.

**Subagents** (`.claude/agents/`)

| Agent | Works in | Tools |
| --- | --- | --- |
| `mapgen` | `tools/mapgen/`, `maps/` | Read, Edit, Write, Glob, Grep, Bash |
| `godot-dev` | `game/`, not `game/maps/` | Read, Edit, Write, Glob, Grep, Bash |
| `reviewer` | read-only review of the diff against the CLAUDE.md files, ADRs and spec | Read, Glob, Grep, Bash (git read commands only) |
| `tester` | test folders: `tools/mapgen/tests/`, `Roadman.Core` tests, `game/tests/` | Read, Edit, Write, Glob, Grep, Bash |

The folder limits are instructions in each agent's prompt. Claude Code doesn't enforce them.

**Permissions** (`.claude/settings.json`)

- Allowed without asking: `godot --headless`, `dotnet build/test/format`, `uv sync`, `uv run`, `pytest`, `ruff`, `git status/diff/log/add/commit`, and the two scripts in `.claude/scripts/`.
- Always ask: `git push`, `curl`, `wget`, WebFetch, WebSearch. Files outside the repo prompt by default.
- Limits: Bash rules match the command text, so `uv run <anything>` is allowed, and a rewritten form such as `git -C . push` isn't caught by the ask rules. `uv sync`, `dotnet build` and `dotnet test` can download packages without asking.
- Personal overrides go in `.claude/settings.local.json` (git-ignored).

**Hooks**

- After every Edit or Write, `.claude/hooks/format-changed.sh` formats the file: `dotnet format whitespace` for `.cs` under `game/` (not `game/maps/`), and `ruff format` for `.py` (via `uv run` in `tools/mapgen/` once it has a `pyproject.toml`, otherwise `ruff` on PATH).
- When a tool is missing or fails, the hook shows a warning and exits 0. It never blocks an edit.

**Godot binary**

- The scripts use `$GODOT`, or `godot` on PATH, and resolve symlinks. Run through a symlink, Godot .NET can't find its assemblies and crashes with ".NET: Assemblies not found".
- On macOS, make `godot` a wrapper script rather than a symlink, so direct `godot --headless` calls work too. Remove the symlink first; writing to it would overwrite the Godot binary:

  ```sh
  rm /opt/homebrew/bin/godot
  printf '#!/bin/sh\nexec /Applications/Godot_mono.app/Contents/MacOS/Godot "$@"\n' > /opt/homebrew/bin/godot
  chmod +x /opt/homebrew/bin/godot
  ```

- The headless import times out after 300 s (`GODOT_IMPORT_TIMEOUT`), because a crashed Godot can hang instead of exiting.
