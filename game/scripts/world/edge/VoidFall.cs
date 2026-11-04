using System.Collections.Generic;
using System.Linq;
using Godot;
using Roadman.Core.Edge;
using Roadman.Core.Maps;

namespace Roadman.Scenes.Edge;

/// <summary>
/// Falls of plain <see cref="RigidBody3D"/>s into the void (<c>docs/specs/world-edge-game.md</c>, "Other bodies"),
/// owned by <c>World</c>. A tracked body gets gravity scale 0 and, each physics frame, the <see cref="VoidPull"/>
/// acceleration as a central force (× its mass). It is freed once it is below <c>void_trigger.bottom_y</c> or the
/// safety net, or after <see cref="MaxFallTime"/>. <see cref="Clear"/> frees every tracked body (a map reload).
/// </summary>
public partial class VoidFall : Node
{
    private readonly Dictionary<RigidBody3D, Fall> _falls = [];

    /// <summary>The pull; <c>World</c> copies its exports into it.</summary>
    public VoidPull Pull { get; set; } = new();

    /// <summary>The world edge of the loaded map; null: no body is tracked.</summary>
    public EdgeGeometry? Geometry { get; set; }

    /// <summary>Seconds after which a falling body is freed whatever its height.</summary>
    public double MaxFallTime { get; set; } = 10.0;

    /// <summary>The safety net's margin below <c>cliff.bottom_y</c>, m.</summary>
    public double SafetyNetMargin { get; set; } = 20.0;

    /// <summary>The bodies falling now.</summary>
    public IReadOnlyCollection<RigidBody3D> Bodies => _falls.Keys;

    /// <summary>Whether <paramref name="body"/> is falling.</summary>
    public bool IsFalling(Node? body) => body is RigidBody3D rigid && _falls.ContainsKey(rigid);

    /// <summary>Seconds <paramref name="body"/> has been falling; null when it isn't.</summary>
    public double? FallTimeOf(RigidBody3D body) => _falls.TryGetValue(body, out var fall) ? fall.Time : null;

    /// <summary>
    /// Starts <paramref name="body"/>'s fall along <paramref name="outward"/> (a unit horizontal vector): gravity scale
    /// 0, the pull from the next physics frame on. Returns false (nothing changes) when it is already falling or there
    /// is no geometry.
    /// </summary>
    public bool Track(RigidBody3D body, Vector3 outward)
    {
        if (Geometry is null || !IsInstanceValid(body) || _falls.ContainsKey(body))
        {
            return false;
        }

        var acceleration = Pull.Acceleration(new LocalPoint(outward.X, 0, outward.Z));
        body.GravityScale = 0f;
        body.Sleeping = false;
        _falls[body] = new Fall(new Vector3((float)acceleration.X, (float)acceleration.Y, (float)acceleration.Z), 0);
        return true;
    }

    /// <summary>Frees every falling body and forgets it.</summary>
    public void Clear()
    {
        foreach (var body in _falls.Keys)
        {
            FreeBody(body);
        }

        _falls.Clear();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_falls.Count == 0)
        {
            return;
        }

        foreach (var body in _falls.Keys.ToList())
        {
            if (!IsInstanceValid(body) || body.IsQueuedForDeletion())
            {
                _falls.Remove(body);
                continue;
            }

            var fall = _falls[body];
            fall = fall with { Time = fall.Time + delta };
            var y = body.GlobalPosition.Y;
            if (Geometry is null
                || y < Geometry.Boundary.VoidTrigger.BottomY
                || Geometry.IsBelowSafetyNet(y, SafetyNetMargin)
                || fall.Time >= MaxFallTime)
            {
                _falls.Remove(body);
                FreeBody(body);
                continue;
            }

            _falls[body] = fall;
            body.Sleeping = false;
            body.ApplyCentralForce(fall.Acceleration * body.Mass);
        }
    }

    private static void FreeBody(RigidBody3D body)
    {
        if (IsInstanceValid(body) && !body.IsQueuedForDeletion())
        {
            body.QueueFree();
        }
    }

    private readonly record struct Fall(Vector3 Acceleration, double Time);
}
