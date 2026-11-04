using Godot;
using Roadman.Core.Edge;
using Roadman.Core.Maps;

namespace Roadman.Scenes.Edge;

/// <summary>
/// The static body of one world-edge barrier item (a <c>boundary.json</c> roadblock or fence): on
/// <see cref="CollisionLayers.BarrierStatic"/>, colliding with nothing itself, with one <see cref="CollisionShape3D"/> (a
/// <see cref="ConvexPolygonShape3D"/> of its 8 corners) per <see cref="BarrierItemLayout.Collision"/> box. It has no
/// visual children; <see cref="Barriers"/> draws the pieces in batches. The body sits at the item's start, and the
/// shapes' points are relative to it.
/// </summary>
public partial class BarrierItem : StaticBody3D
{
    /// <summary>The item's <c>boundary.json</c> id (the node name is this id made a valid node name).</summary>
    public string ItemId { get; private set; } = "";

    /// <summary>The layout the item was built from; null before <see cref="Setup"/>.</summary>
    public BarrierItemLayout? Layout { get; private set; }

    /// <summary>The barrier type; <see cref="BarrierKind.LandFence"/> before <see cref="Setup"/>.</summary>
    public BarrierKind Kind => Layout?.Kind ?? BarrierKind.LandFence;

    /// <summary>The number of collision shapes (one per collision box of the layout).</summary>
    public int ShapeCount { get; private set; }

    /// <summary>The body for <paramref name="layout"/>, of the matching type, set up.</summary>
    public static BarrierItem Create(BarrierItemLayout layout)
    {
        BarrierItem item = layout.Kind switch
        {
            BarrierKind.Roadblock => new Roadblock(),
            BarrierKind.WaterFence => new WaterFence(),
            _ => new LandFence(),
        };
        item.Setup(layout);
        return item;
    }

    /// <summary>
    /// Names the body after the item, puts it on the barrier layer at the item's start and adds its collision shapes.
    /// Call it once, before the body enters the tree.
    /// </summary>
    public void Setup(BarrierItemLayout layout)
    {
        Layout = layout;
        ItemId = layout.Id;
        Name = layout.Id.ValidateNodeName();
        CollisionLayer = CollisionLayers.BarrierStatic;
        CollisionMask = 0;
        var origin = ToVector(layout.Start);
        Position = origin;
        for (var i = 0; i < layout.Collision.Count; i++)
        {
            var corners = layout.Collision[i].Corners();
            var points = new Vector3[corners.Count];
            for (var c = 0; c < corners.Count; c++)
            {
                points[c] = ToVector(corners[c]) - origin;
            }

            AddChild(new CollisionShape3D
            {
                Name = $"Collision{i}",
                Shape = new ConvexPolygonShape3D { Points = points },
            });
        }

        ShapeCount = layout.Collision.Count;
    }

    internal static Vector3 ToVector(LocalPoint point) => new((float)point.X, (float)point.Y, (float)point.Z);
}
