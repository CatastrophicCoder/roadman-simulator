# 0001: C# as the game language

Status: Accepted

## Context

Godot 4 supports GDScript and C#. The game targets macOS and Windows desktop. Development is done mostly by Claude Code in Rider, and engine-free logic must be testable quickly.

## Decision

Write all game code in C# using the Godot 4 .NET build. Engine-free logic goes in a plain class library (`Roadman.Core`) with its own test project.

## Consequences

- Godot 4 exports C# projects to Windows, macOS and Linux, so both targets are covered. A web export isn't possible, and none is planned.
- The project needs the .NET editor build and the .NET export templates.
- Code is edited in Rider, because Godot's built-in editor has minimal C# support.
- `Roadman.Core` tests run with `dotnet test` in seconds, without the engine.
- GDScript and GDExtension add-ons are reachable only through untyped `Call` and `Get` methods. Most tutorials use GDScript.
