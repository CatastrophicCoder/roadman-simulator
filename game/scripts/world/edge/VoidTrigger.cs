using Godot;
using Roadman.Core.Edge;

namespace Roadman.Scenes.Edge;

/// <summary>
/// The void trigger of the world edge (<c>docs/specs/world-edge-game.md</c>, "Void trigger") at
/// <c>MapLoader/Edge/VoidTrigger</c>: an <see cref="Area3D"/> with one <see cref="CollisionShape3D"/> per
/// <c>void_trigger.prisms</c> entry, a <see cref="ConvexPolygonShape3D"/> of its footprint at <c>bottom_y</c> and
/// <c>top_y</c>. It is on no layer and monitors the player, barrier and vehicle layers (not the map). A body entering it
/// emits <see cref="SignalName.BodyEnteredVoid"/> with the outward direction at the body's x/z; ignoring a body that is
/// already falling is <c>World</c>'s job.
/// </summary>
public partial class VoidTrigger : Area3D
{
    /// <summary>
    /// <paramref name="body"/> entered the void; <paramref name="outward"/> is <see cref="EdgeGeometry.OutwardAt"/> at
    /// its global x/z (y 0).
    /// </summary>
    [Signal]
    public delegate void BodyEnteredVoidEventHandler(Node3D body, Vector3 outward);

    /// <summary>The layers the trigger monitors.</summary>
    public const uint MonitoredLayers = CollisionLayers.Player | CollisionLayers.Barrier | CollisionLayers.Vehicle;

    /// <summary>The geometry the outward directions come from; set by <see cref="Create"/>.</summary>
    public EdgeGeometry Geometry { get; private set; } = null!;

    /// <summary>The number of collision shapes (one per prism).</summary>
    public int ShapeCount { get; private set; }

    /// <summary>A trigger with one shape per prism of <paramref name="geometry"/>'s boundary.</summary>
    public static VoidTrigger Create(EdgeGeometry geometry)
    {
        var trigger = new VoidTrigger
        {
            Name = "VoidTrigger",
            Geometry = geometry,
            CollisionLayer = 0,
            CollisionMask = MonitoredLayers,
            Monitoring = true,
            Monitorable = false,
        };
        var voidTrigger = geometry.Boundary.VoidTrigger;
        var bottom = (float)voidTrigger.BottomY;
        var top = (float)voidTrigger.TopY;
        for (var i = 0; i < voidTrigger.Prisms.Count; i++)
        {
            var footprint = voidTrigger.Prisms[i].Footprint;
            var points = new Vector3[footprint.Count * 2];
            for (var k = 0; k < footprint.Count; k++)
            {
                points[k] = new Vector3((float)footprint[k].X, bottom, (float)footprint[k].Z);
                points[k + footprint.Count] = new Vector3((float)footprint[k].X, top, (float)footprint[k].Z);
            }

            trigger.AddChild(new CollisionShape3D
            {
                Name = $"Prism{i}",
                Shape = new ConvexPolygonShape3D { Points = points },
            });
        }

        trigger.ShapeCount = voidTrigger.Prisms.Count;
        trigger.BodyEntered += trigger.OnBodyEntered;
        return trigger;
    }

    /// <summary>The outward direction at <paramref name="position"/>'s x/z, as a horizontal unit vector.</summary>
    public Vector3 OutwardAt(Vector3 position)
    {
        var outward = Geometry.OutwardAt(position.X, position.Z);
        return new Vector3((float)outward.X, 0f, (float)outward.Z);
    }

    private void OnBodyEntered(Node3D body)
    {
        if (!IsInstanceValid(body))
        {
            return;
        }

        EmitSignal(SignalName.BodyEnteredVoid, body, OutwardAt(body.GlobalPosition));
    }
}
