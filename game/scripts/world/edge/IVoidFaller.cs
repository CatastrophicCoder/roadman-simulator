using Godot;

namespace Roadman.Scenes.Edge;

/// <summary>
/// A body that handles its own fall into the void (<c>docs/specs/world-edge-game.md</c>, "Other bodies"): vehicles and
/// characters later, a test body now. <c>World</c> calls <see cref="EnterVoid"/> once per fall, when the body enters the
/// <see cref="VoidTrigger"/> or drops below the safety net.
/// </summary>
public interface IVoidFaller
{
    /// <summary>The body entered the void; <paramref name="outward"/> is the unit horizontal outward direction (y 0).</summary>
    void EnterVoid(Vector3 outward);
}
