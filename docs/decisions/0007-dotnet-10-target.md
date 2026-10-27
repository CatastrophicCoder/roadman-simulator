# 0007: Target .NET 10

Status: Accepted

## Context

The Godot 4.7.2 editor generates `RoadmanSimulator.csproj` with `Godot.NET.Sdk/4.7.2` and `net8.0`. .NET 8 support ends on 10 November 2026, and .NET 10 is the current LTS release. The development Mac has only .NET SDK 10.0.403 and runtime 10.0.14 installed. Godot 4 accepts newer target frameworks than the one it generates.

The engine tests depend on gdUnit4Net. Its test adapter 3.2.0 and API 5.1.0-rc6 support .NET 8 and .NET 10 with Godot 4.7–4.7.2. A throwaway net10.0 Godot project with these packages ran a plain test and a `[RequireGodotRuntime]` test headless on this machine before the decision was made.

## Decision

- Every C# project targets `net10.0`: `RoadmanSimulator.csproj`, `Roadman.Core` and `Roadman.Core.Tests`. The editor's Android `net9.0` override is removed; Android is not a target platform.
- `global.json` at the repo root pins the SDK to `10.0.403` with `rollForward: latestFeature`, so any 10.0.4xx or later 10.0 feature band builds, and SDK 8, 9 or 11 never does.
- gdUnit4Net uses `gdUnit4.api` 5.1.0-rc6, a prerelease, because the stable 5.0.0 doesn't support Godot 4.7. Move to the stable release when it ships.

## Consequences

- CI must install .NET SDK 10 (10.0.403 or a later 10.0 feature band).
- Exported builds bundle the .NET 10 runtime.
- When Godot is upgraded, check that the new `Godot.NET.Sdk` still supports `net10.0`, and that gdUnit4Net supports the new Godot version.
- A new SDK feature band (10.0.5xx) is picked up automatically; a new major version needs a new decision.
