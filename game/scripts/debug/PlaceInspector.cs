using Godot;
using Roadman.Core.Maps;
using Roadman.Scenes;

namespace Roadman.Debug;

/// <summary>
/// Debug inspector of the pack's place data (<c>docs/specs/place-information.md</c>, "Game"). The
/// <c>debug_inspect</c> action (physical I) switches it on and off. While on, a crosshair is drawn at the screen centre
/// and each physics frame a ray is cast from the camera through it:
/// <list type="bullet">
/// <item>a hit on a body in <see cref="MapLoader.BuildingGroup"/> shows that building (<see cref="PlaceText.Describe(Building, PlaceIndex)"/>);</item>
/// <item>any other hit shows the POI point nearest to the hit within <see cref="PoiPickRadius"/>, else the smallest
/// POI area whose outline contains the hit's x/z (<see cref="PlaceText.Describe(Poi)"/>), else
/// <see cref="PlaceText.NothingHere"/>;</item>
/// <item>no place data in the pack: <see cref="PlaceText.NoPlacesMessage"/>.</item>
/// </list>
/// The panel shows the pack's raw data, including real business names: <see cref="World"/> adds the inspector in
/// debug builds only, and it never ships.
/// </summary>
public partial class PlaceInspector : Node
{
    public const string ToggleAction = "debug_inspect";

    /// <summary>Emitted when the inspector switches on or off.</summary>
    [Signal]
    public delegate void ToggledEventHandler(bool enabled);

    /// <summary>Emitted when the inspected building or POI changes; empty when nothing is selected.</summary>
    [Signal]
    public delegate void SelectionChangedEventHandler(string id);

    /// <summary>Largest 3D distance in metres from the hit point to a POI point that is picked.</summary>
    [Export]
    public float PoiPickRadius { get; set; } = 3f;

    /// <summary>Length of the inspection ray in metres.</summary>
    [Export]
    public float RayLength { get; set; } = 5000f;

    [Export]
    public int FontSize { get; set; } = 14;

    [Export]
    public int OutlineSize { get; set; } = 4;

    [Export]
    public Color PanelColour { get; set; } = new(0f, 0f, 0f, 0.7f);

    /// <summary>Distance of the panel from the screen edges, in pixels.</summary>
    [Export]
    public int ScreenMargin { get; set; } = 8;

    /// <summary>Canvas layer of the overlay, above the game's HUD.</summary>
    [Export]
    public int OverlayLayer { get; set; } = 100;

    /// <summary>Length of each crosshair arm in pixels.</summary>
    [Export]
    public float CrosshairSize { get; set; } = 10f;

    [Export]
    public float CrosshairThickness { get; set; } = 2f;

    [Export]
    public Color CrosshairColour { get; set; } = new(1f, 0.2f, 0.2f);

    /// <summary>The map loader whose place data is inspected; set before adding the node.</summary>
    public MapLoader? Loader { get; set; }

    /// <summary>The camera the ray starts from. Null means the current camera of the overlay's viewport.</summary>
    public Camera3D? Camera { get; set; }

    public bool Enabled { get; private set; }

    /// <summary>The inspected building, or null.</summary>
    public Building? SelectedBuilding { get; private set; }

    /// <summary>The inspected point of interest, or null.</summary>
    public Poi? SelectedPoi { get; private set; }

    /// <summary>The id of <see cref="SelectedBuilding"/> or <see cref="SelectedPoi"/>; null when nothing is selected.</summary>
    public string? SelectionId => SelectedBuilding?.Id ?? SelectedPoi?.Id;

    /// <summary>The panel's text after the last inspection.</summary>
    public string PanelText => PanelLabel.Text;

    public CanvasLayer Overlay { get; private set; } = null!;

    public PanelContainer Panel { get; private set; } = null!;

    public Label PanelLabel { get; private set; } = null!;

    /// <summary>The crosshair, centred on the screen; visible while the inspector is on.</summary>
    public Control Crosshair { get; private set; } = null!;

    public override void _Ready()
    {
        BuildOverlay();
        if (Loader is not null)
        {
            Loader.MapLoaded += OnMapLoaded;
            Loader.MapLoadFailed += OnMapLoadFailed;
        }
    }

    public override void _ExitTree()
    {
        if (Loader is not null && IsInstanceValid(Loader))
        {
            Loader.MapLoaded -= OnMapLoaded;
            Loader.MapLoadFailed -= OnMapLoadFailed;
        }

        // Moved into a screenshot viewport by AttachOverlayTo: it doesn't go with this node.
        if (IsInstanceValid(Overlay) && Overlay.GetParent() != this)
        {
            Overlay.QueueFree();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (InputMap.HasAction(ToggleAction) && @event.IsActionPressed(ToggleAction, allowEcho: false))
        {
            Toggle();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Enabled)
        {
            InspectCentre();
        }
    }

    /// <summary>Switches the inspector; see <see cref="SetEnabled"/>.</summary>
    public bool Toggle() => SetEnabled(!Enabled);

    /// <summary>Switches the crosshair, panel and per-frame ray on or off. Always succeeds.</summary>
    /// <returns>True (the inspector is now in the requested state).</returns>
    public bool SetEnabled(bool enabled)
    {
        if (enabled == Enabled)
        {
            return true;
        }

        Enabled = enabled;
        Panel.Visible = enabled;
        Crosshair.Visible = enabled;
        if (!enabled)
        {
            Select(null, null, "");
        }

        EmitSignal(SignalName.Toggled, enabled);
        return true;
    }

    /// <summary>
    /// Moves the overlay (panel and crosshair) under <paramref name="parent"/>, for example a screenshot
    /// <see cref="SubViewport"/>: a <see cref="CanvasLayer"/> draws only in the viewport it is in. With
    /// <see cref="Camera"/> null, the ray then starts from that viewport's camera.
    /// </summary>
    public void AttachOverlayTo(Node parent)
    {
        Overlay.GetParent()?.RemoveChild(Overlay);
        parent.AddChild(Overlay);
    }

    /// <summary>The camera in use: <see cref="Camera"/>, or the overlay viewport's current camera.</summary>
    public Camera3D? ResolvedCamera() =>
        Camera is not null && IsInstanceValid(Camera) ? Camera : Overlay.GetViewport()?.GetCamera3D();

    /// <summary>
    /// Casts the ray from the camera through the centre of its viewport and inspects what it hits; returns the panel
    /// text. Call it from a physics frame.
    /// </summary>
    public string InspectCentre()
    {
        var camera = ResolvedCamera();
        if (camera is null || !camera.IsInsideTree())
        {
            return Select(null, null, PlaceText.NothingHere);
        }

        var centre = camera.GetViewport().GetVisibleRect().Size / 2f;
        var from = camera.ProjectRayOrigin(centre);
        var to = from + (camera.ProjectRayNormal(centre) * RayLength);
        return InspectRay(from, to);
    }

    /// <summary>
    /// Casts a ray from <paramref name="from"/> to <paramref name="to"/> (world space) and inspects its first hit;
    /// returns the panel text. Works whether the inspector is on or not. Call it from a physics frame.
    /// </summary>
    public string InspectRay(Vector3 from, Vector3 to)
    {
        var world = Loader is not null && Loader.IsInsideTree() ? Loader.GetWorld3D() : ResolvedCamera()?.GetWorld3D();
        if (world is null)
        {
            return InspectHit(null, null);
        }

        var hit = world.DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        return hit.Count == 0
            ? InspectHit(null, null)
            : InspectHit(hit["collider"].AsGodotObject() as Node, hit["position"].AsVector3());
    }

    /// <summary>
    /// Shows what the pack knows about a ray hit: <paramref name="collider"/> resolved to a building, else the POI
    /// nearest to <paramref name="position"/>; null for both means no hit. Returns the panel text.
    /// </summary>
    public string InspectHit(Node? collider, Vector3? position)
    {
        var places = Loader?.Places;
        if (places is null)
        {
            return Select(null, null, PlaceText.NoPlacesMessage(Loader?.ResolvedMapId ?? MapLoader.DefaultMapId));
        }

        if (Loader!.BuildingOf(collider) is { } building)
        {
            return Select(building, null, PlaceText.Describe(building, places));
        }

        if (position is not { } point)
        {
            return Select(null, null, PlaceText.NothingHere);
        }

        var poi = places.NearestPoiPoint(new LocalPoint(point.X, point.Y, point.Z), PoiPickRadius)
            ?? places.PoiAreaAt(point.X, point.Z);
        return poi is null ? Select(null, null, PlaceText.NothingHere) : Select(null, poi, PlaceText.Describe(poi));
    }

    private string Select(Building? building, Poi? poi, string text)
    {
        var previous = SelectionId;
        SelectedBuilding = building;
        SelectedPoi = poi;
        if (PanelLabel.Text != text)
        {
            PanelLabel.Text = text;
        }

        if (previous != SelectionId)
        {
            EmitSignal(SignalName.SelectionChanged, SelectionId ?? "");
        }

        return text;
    }

    private void OnMapLoaded(int tileCount) => ClearSelection();

    private void OnMapLoadFailed(string message) => ClearSelection();

    /// <summary>
    /// Clears the selection and the panel text at once: after a load or reload the old building, POI and text belong
    /// to a pack that is gone. The next physics frame inspects the new map while the inspector is on.
    /// </summary>
    private void ClearSelection() => Select(null, null, "");

    private void BuildOverlay()
    {
        Overlay = new CanvasLayer { Name = "PlaceInspectorOverlay", Layer = OverlayLayer };
        AddChild(Overlay);

        var style = new StyleBoxFlat { BgColor = PanelColour };
        style.SetContentMarginAll(6f);
        Panel = new PanelContainer { Name = "InspectorPanel", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        Panel.AddThemeStyleboxOverride("panel", style);
        PanelLabel = new Label
        {
            Name = "InspectorLabel",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            LabelSettings = new LabelSettings
            {
                FontSize = FontSize,
                FontColor = Colors.White,
                OutlineSize = OutlineSize,
                OutlineColor = Colors.Black,
            },
        };
        Panel.AddChild(PanelLabel);
        Overlay.AddChild(Panel);
        // Left edge, below the top-left messages of the orthophoto and road graph views.
        Panel.Position = new Vector2(ScreenMargin, ScreenMargin + (FontSize * 6));

        // Two bars crossing at the anchor point, the screen centre.
        Crosshair = new Control
        {
            Name = "Crosshair",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 0.5f,
            AnchorBottom = 0.5f,
        };
        Crosshair.AddChild(CrosshairBar(new Vector2(-CrosshairSize, -CrosshairThickness / 2f), new Vector2(CrosshairSize * 2f, CrosshairThickness)));
        Crosshair.AddChild(CrosshairBar(new Vector2(-CrosshairThickness / 2f, -CrosshairSize), new Vector2(CrosshairThickness, CrosshairSize * 2f)));
        Overlay.AddChild(Crosshair);
    }

    private ColorRect CrosshairBar(Vector2 position, Vector2 size) =>
        new()
        {
            Color = CrosshairColour,
            Position = position,
            Size = size,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
}
