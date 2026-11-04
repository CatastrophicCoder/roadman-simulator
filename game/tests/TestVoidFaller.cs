using System.Collections.Generic;
using Godot;
using Roadman.Scenes.Edge;

namespace Roadman.Tests;

/// <summary>
/// A test body that handles its own fall (<see cref="IVoidFaller"/>, <c>docs/specs/world-edge-game.md</c>, "Other
/// bodies"): a <see cref="RigidBody3D"/> that only records each <see cref="EnterVoid"/> call. Used by
/// <see cref="WorldEdgeTest"/> criteria 22 and 23.
/// </summary>
public partial class TestVoidFaller : RigidBody3D, IVoidFaller
{
    /// <summary>The outward vector of every <see cref="EnterVoid"/> call, in order.</summary>
    public List<Vector3> Entries { get; } = [];

    public void EnterVoid(Vector3 outward) => Entries.Add(outward);
}
