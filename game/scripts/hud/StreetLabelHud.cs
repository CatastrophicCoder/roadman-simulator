using Godot;
using Roadman.Core.Maps;
using Roadman.Scenes;

namespace Roadman.Hud;

/// <summary>
/// HUD label with the current street (<c>docs/specs/place-information.md</c>, "Game"; ADR 0013): the
/// <see cref="StreetLabel.Format"/> text of the road graph edge nearest to the reference point in 3D, within
/// <see cref="StreetMaxDistance"/>; empty when there is none, no reference point or no road graph. It shows street
/// names, road numbers and road classes only, never business names. In debug and release builds.
/// <para>
/// Reference point: <see cref="Target"/>'s position when set (the player, once there is one). Otherwise the point
/// below the active camera where a downward ray first hits a body in <see cref="MapLoader.RoadGroup"/> or
/// <see cref="MapLoader.TerrainGroup"/> (buildings are passed through). The camera is <see cref="Camera"/>, or the
/// current camera of the viewport this layer draws in, so a screenshot viewport uses its own camera.
/// </para>
/// The label text is set only when it changes, and <see cref="SignalName.StreetChanged"/> fires then.
/// </summary>
public partial class StreetLabelHud : CanvasLayer
{
    private const float RayLength = 10_000f;
    private const int MaxRayHits = 32;

    /// <summary>Emitted when the label's text changes; empty when no street is near.</summary>
    [Signal]
    public delegate void StreetChangedEventHandler(string text);

    /// <summary>Largest 3D distance in metres from the reference point to an edge whose street is shown.</summary>
    [Export]
    public float StreetMaxDistance { get; set; } = 40f;

    /// <summary>The node whose position is the reference point (the player). Null: the ground below the camera.</summary>
    [Export]
    public Node3D? Target { get; set; }

    [Export]
    public int FontSize { get; set; } = 24;

    [Export]
    public int OutlineSize { get; set; } = 6;

    /// <summary>Distance of the label from the bottom of the screen, in pixels.</summary>
    [Export]
    public int ScreenMargin { get; set; } = 16;

    /// <summary>Canvas layer of the HUD, below the debug overlays.</summary>
    [Export]
    public int HudLayer { get; set; } = 10;

    /// <summary>The map loader whose road graph is used; set before adding the node.</summary>
    public MapLoader? Loader { get; set; }

    /// <summary>The camera for the reference ray. Null means the current camera of this layer's viewport.</summary>
    public Camera3D? Camera { get; set; }

    public Label Label { get; private set; } = null!;

    /// <summary>The text on screen.</summary>
    public string Text => Label.Text;

    /// <summary>The reference point of the last update; null when there was none.</summary>
    public Vector3? LastReferencePoint { get; private set; }

    public override void _Ready()
    {
        Layer = HudLayer;
        Label = new Label
        {
            Name = "StreetLabel",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            LabelSettings = new LabelSettings
            {
                FontSize = FontSize,
                FontColor = Colors.White,
                OutlineSize = OutlineSize,
                OutlineColor = Colors.Black,
            },
            // Bottom centre, growing up and to both sides with the text.
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 1f,
            AnchorBottom = 1f,
            OffsetTop = -ScreenMargin,
            OffsetBottom = -ScreenMargin,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Begin,
        };
        AddChild(Label);
    }

    public override void _PhysicsProcess(double delta) => UpdateStreet();

    /// <summary>Finds the reference point and updates the label from it; returns the label text.</summary>
    public string UpdateStreet() => UpdateStreetAt(ReferencePoint());

    /// <summary>
    /// Updates the label for <paramref name="reference"/> (null: no reference point, so the label is empty); returns
    /// the label text. Lets tests and a future player controller supply the point directly.
    /// </summary>
    public string UpdateStreetAt(Vector3? reference)
    {
        LastReferencePoint = reference;
        var text = reference is { } point ? StreetTextAt(Loader?.RoadGraph, point, StreetMaxDistance) : "";
        if (text != Label.Text)
        {
            Label.Text = text;
            EmitSignal(SignalName.StreetChanged, text);
        }

        return text;
    }

    /// <summary>The label text for <paramref name="point"/>: the nearest edge within <paramref name="maxDistance"/>, or empty.</summary>
    public static string StreetTextAt(RoadGraph? graph, Vector3 point, float maxDistance)
    {
        if (graph is null || !(maxDistance >= 0f))
        {
            return "";
        }

        var hit = graph.NearestEdge(new LocalPoint(point.X, point.Y, point.Z), maxDistance);
        return hit is { } nearest ? StreetLabel.Format(nearest.Edge) : "";
    }

    /// <summary>
    /// <see cref="Target"/>'s position, or the first road or terrain hit straight below the camera; null when there
    /// is neither a target nor a camera, or nothing is below it. Call it from a physics frame.
    /// </summary>
    public Vector3? ReferencePoint()
    {
        if (Target is not null && IsInstanceValid(Target) && Target.IsInsideTree())
        {
            return Target.GlobalPosition;
        }

        var camera = Camera is not null && IsInstanceValid(Camera) ? Camera : GetViewport()?.GetCamera3D();
        if (camera is null || !camera.IsInsideTree())
        {
            return null;
        }

        var from = camera.GlobalPosition;
        return GroundBelow(camera.GetWorld3D().DirectSpaceState, from, from + (Vector3.Down * RayLength));
    }

    /// <summary>The first hit on a road or terrain body along from → to, skipping every other body.</summary>
    private static Vector3? GroundBelow(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to)
    {
        var query = PhysicsRayQueryParameters3D.Create(from, to);
        var excluded = new Godot.Collections.Array<Rid>();
        for (var i = 0; i < MaxRayHits; i++)
        {
            query.Exclude = excluded;
            var hit = space.IntersectRay(query);
            if (hit.Count == 0)
            {
                return null;
            }

            if (hit["collider"].AsGodotObject() is Node body
                && (body.IsInGroup(MapLoader.RoadGroup) || body.IsInGroup(MapLoader.TerrainGroup)))
            {
                return hit["position"].AsVector3();
            }

            excluded.Add(hit["rid"].AsRid());
        }

        return null;
    }
}
