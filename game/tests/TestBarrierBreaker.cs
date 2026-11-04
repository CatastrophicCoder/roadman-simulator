using Godot;
using Roadman.Scenes;
using Roadman.Scenes.Edge;

namespace Roadman.Tests;

/// <summary>
/// The car-sized test breaker of <c>docs/specs/world-edge-game.md</c> criterion 12 (a stand-in until vehicles exist): a
/// <see cref="RigidBody3D"/> implementing <see cref="IBarrierBreaker"/> (<see cref="BreakerVelocity"/> is its
/// <see cref="RigidBody3D.LinearVelocity"/>), one box of <see cref="Size"/> (1.8 m wide, 1.5 m high, 4.5 m long along its
/// local z), <see cref="CarMass"/> 1,200 kg, on <see cref="CollisionLayers.Vehicle"/> with mask map + both barrier layers,
/// in the group <see cref="Barriers.BreakerGroup"/>. Its rotation is locked, so the tests can drive it straight; no
/// damping. Used by <see cref="WorldEdgeBreakingTest"/>.
/// </summary>
public partial class TestBarrierBreaker : RigidBody3D, IBarrierBreaker
{
    /// <summary>The spec's car size: width (x), height (y), length (z), m.</summary>
    public static readonly Vector3 Size = new(1.8f, 1.5f, 4.5f);

    /// <summary>The spec's car mass, kg.</summary>
    public const float CarMass = 1200f;

    public TestBarrierBreaker()
    {
        Name = "TestBarrierBreaker";
        Mass = CarMass;
        CollisionLayer = CollisionLayers.Vehicle;
        CollisionMask = CollisionLayers.Map | CollisionLayers.Barriers;
        LinearDampMode = DampMode.Replace;
        LinearDamp = 0f;
        AngularDampMode = DampMode.Replace;
        AngularDamp = 0f;
        AxisLockAngularX = true;
        AxisLockAngularY = true;
        AxisLockAngularZ = true;
        AddChild(new CollisionShape3D { Name = "Box", Shape = new BoxShape3D { Size = Size } });
        AddToGroup(Barriers.BreakerGroup);
    }

    public Vector3 BreakerVelocity => LinearVelocity;
}
