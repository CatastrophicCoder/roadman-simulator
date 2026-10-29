using System.Collections.Generic;
using System.Linq;
using Godot;
using Roadman.Core.Maps;
using Roadman.Scenes;

namespace Roadman.Debug;

/// <summary>
/// Debug view of the pack's road graph (<see cref="MapLoader.RoadGraph"/>). The <c>debug_road_graph</c> action
/// (physical G) switches it on and off. It draws, <see cref="HeightOffset"/> above the graph points (which lie on the
/// road surfaces):
/// <list type="bullet">
/// <item>drivable edges as flat ribbons coloured by class (<see cref="ClassColours"/>; a class not listed gets a
/// stable colour from its name), walkable-only edges in <see cref="WalkableColour"/>;</item>
/// <item>an arrow at the middle of every one-way edge, pointing in the allowed direction;</item>
/// <item>nodes as flat markers coloured by kind: dead ends as diamonds, area-edge cuts as large squares, span ends
/// (where a bridge deck meets the road) as triangles, junctions as squares and joints as small squares.</item>
/// </list>
/// Bridge edges (any <see cref="RoadEdge.BridgeSource"/>) are drawn like the other edges, by class.
/// A legend in the top-right corner lists the classes drawn and the node kinds. The geometry is unshaded and casts no
/// shadow; it is built once per graph and kept until the map reloads.
/// <para>
/// <see cref="World"/> adds it in debug builds only. Without a road graph, switching on shows
/// <see cref="NoGraphMessage"/>, emits <see cref="SignalName.Unavailable"/> and stays off. An empty graph (a map
/// without roads) switches on normally, with no mesh and a legend that says <see cref="EmptyGraphText"/>.
/// </para>
/// </summary>
public partial class RoadGraphDebugView : Node3D
{
    public const string ToggleAction = "debug_road_graph";

    /// <summary>Emitted when the view switches on or off.</summary>
    [Signal]
    public delegate void ToggledEventHandler(bool enabled);

    /// <summary>Emitted when switching on fails because there is no road graph; the view stays off.</summary>
    [Signal]
    public delegate void UnavailableEventHandler(string message);

    /// <summary>Height of the drawing above the graph points, in metres, so it doesn't fight the road surface.</summary>
    [Export]
    public float HeightOffset { get; set; } = 0.3f;

    /// <summary>Width of the edge ribbons in metres. Wide enough to read from the overview preset.</summary>
    [Export]
    public float EdgeWidth { get; set; } = 2.5f;

    /// <summary>Edge length of a node marker in metres; joints are drawn at half size, area edges at 1.5 times.</summary>
    [Export]
    public float NodeSize { get; set; } = 5f;

    [Export]
    public float ArrowLength { get; set; } = 9f;

    [Export]
    public float ArrowWidth { get; set; } = 6f;

    /// <summary>Draw on top of everything (through buildings and terrain) instead of depth-testing.</summary>
    [Export]
    public bool DrawOnTop { get; set; }

    /// <summary>Colours of the drivable classes. A class not listed gets a stable colour derived from its name.</summary>
    [Export]
    public Godot.Collections.Dictionary<string, Color> ClassColours { get; set; } = new()
    {
        ["primary"] = new Color(0.95f, 0.25f, 0.2f),
        ["secondary"] = new Color(0.95f, 0.5f, 0.1f),
        ["tertiary"] = new Color(0.95f, 0.85f, 0.15f),
        ["unclassified"] = new Color(0.7f, 0.9f, 0.3f),
        ["residential"] = new Color(0.25f, 0.8f, 0.35f),
        ["living_street"] = new Color(0.15f, 0.65f, 0.55f),
        ["service"] = new Color(0.3f, 0.55f, 0.95f),
        ["driveway"] = new Color(0.5f, 0.4f, 0.95f),
        ["parking_aisle"] = new Color(0.75f, 0.4f, 0.9f),
        ["track"] = new Color(0.6f, 0.45f, 0.25f),
    };

    /// <summary>Colour of the edges that are walkable but not drivable, whatever their class.</summary>
    [Export]
    public Color WalkableColour { get; set; } = new(1f, 0.6f, 0.8f);

    [Export]
    public Color ArrowColour { get; set; } = new(0.05f, 0.05f, 0.05f);

    [Export]
    public Color JunctionColour { get; set; } = new(1f, 1f, 1f);

    [Export]
    public Color JointColour { get; set; } = new(0.55f, 0.55f, 0.55f);

    [Export]
    public Color DeadEndColour { get; set; } = new(1f, 0.05f, 0.05f);

    [Export]
    public Color AreaEdgeColour { get; set; } = new(0f, 0.95f, 1f);

    [Export]
    public Color SpanEndColour { get; set; } = new(1f, 0.1f, 0.9f);

    /// <summary>How long the "no road graph" message stays on screen, in seconds.</summary>
    [Export]
    public float MessageSeconds { get; set; } = 4f;

    [Export]
    public int FontSize { get; set; } = 14;

    [Export]
    public int OutlineSize { get; set; } = 4;

    [Export]
    public Color PanelColour { get; set; } = new(0f, 0f, 0f, 0.6f);

    [Export]
    public int ScreenMargin { get; set; } = 8;

    /// <summary>Canvas layer of the overlay, above the game's HUD.</summary>
    [Export]
    public int OverlayLayer { get; set; } = 100;

    /// <summary>The map loader whose graph is drawn; set before adding the node.</summary>
    public MapLoader? Loader { get; set; }

    /// <summary>The graph to draw instead of <see cref="MapLoader.RoadGraph"/>; for tests.</summary>
    public RoadGraph? Graph { get; set; }

    /// <summary>The graph in use: <see cref="Graph"/>, or the loader's.</summary>
    public RoadGraph? ResolvedGraph => Graph ?? Loader?.RoadGraph;

    public bool Enabled { get; private set; }

    /// <summary>The node that holds the drawing; hidden while the view is off.</summary>
    public MeshInstance3D MeshInstance { get; private set; } = null!;

    public CanvasLayer Overlay { get; private set; } = null!;

    public PanelContainer LegendPanel { get; private set; } = null!;

    public PanelContainer MessagePanel { get; private set; } = null!;

    public Label MessageLabel { get; private set; } = null!;

    /// <summary>The graph the current mesh was built from; null when nothing is built.</summary>
    private RoadGraph? _builtFor;

    private int _messageSerial;

    /// <summary>Legend row for a graph with no nodes and no edges (a map without roads); nothing else is drawn.</summary>
    public const string EmptyGraphText = "road graph is empty";

    /// <summary>Shown when the view is switched on but there is no road graph.</summary>
    public static string NoGraphMessage(string mapId) => $"No road graph in this pack; run /regen-map {mapId}";

    public override void _Ready()
    {
        MeshInstance = new MeshInstance3D
        {
            Name = "RoadGraphMesh",
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(MeshInstance);
        BuildOverlay();
        if (Loader is not null)
        {
            Loader.MapLoaded += OnMapLoaded;
        }
    }

    public override void _ExitTree()
    {
        if (Loader is not null && IsInstanceValid(Loader))
        {
            Loader.MapLoaded -= OnMapLoaded;
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

    /// <summary>Switches the view; see <see cref="SetEnabled"/>.</summary>
    public bool Toggle() => SetEnabled(!Enabled);

    /// <summary>
    /// Switches the drawing on or off. Switching on builds the mesh if the graph changed since the last build. With no
    /// graph it shows <see cref="NoGraphMessage"/>, emits <see cref="SignalName.Unavailable"/> and stays off.
    /// </summary>
    /// <returns>True when the view is now in the requested state.</returns>
    public bool SetEnabled(bool enabled)
    {
        if (enabled == Enabled)
        {
            return true;
        }

        if (!enabled)
        {
            Enabled = false;
            MeshInstance.Visible = false;
            LegendPanel.Visible = false;
            EmitSignal(SignalName.Toggled, false);
            return true;
        }

        var graph = ResolvedGraph;
        if (graph is null)
        {
            var message = NoGraphMessage(Loader?.ResolvedMapId ?? MapLoader.DefaultMapId);
            var path = Loader?.RoadGraphPath ?? MapPack.RoadGraphFile;
            GD.PushWarning($"RoadGraphDebugView: no road graph from {path}. {message}");
            ShowMessage(message);
            EmitSignal(SignalName.Unavailable, message);
            return false;
        }

        if (!ReferenceEquals(graph, _builtFor))
        {
            MeshInstance.Mesh = BuildMesh(graph);
            MeshInstance.MaterialOverride = CreateMaterial();
            UpdateLegend(graph);
            _builtFor = graph;
        }

        Enabled = true;
        MeshInstance.Visible = true;
        LegendPanel.Visible = true;
        EmitSignal(SignalName.Toggled, true);
        return true;
    }

    /// <summary>
    /// Moves the overlay (legend and messages) under <paramref name="parent"/>, for example a screenshot
    /// <see cref="SubViewport"/>: a <see cref="CanvasLayer"/> draws only in the viewport it is in.
    /// </summary>
    public void AttachOverlayTo(Node parent)
    {
        Overlay.GetParent()?.RemoveChild(Overlay);
        parent.AddChild(Overlay);
    }

    /// <summary>The colour an edge is drawn in.</summary>
    public Color EdgeColour(RoadEdge edge) => edge.Drivable ? ClassColour(edge.Class) : WalkableColour;

    /// <summary>The colour of a drivable class: from <see cref="ClassColours"/>, or derived from the name.</summary>
    public Color ClassColour(string roadClass)
    {
        if (ClassColours.TryGetValue(roadClass, out var colour))
        {
            return colour;
        }

        // FNV-1a, so the colour is the same in every run (string.GetHashCode is randomised per process).
        var hash = 2166136261u;
        foreach (var c in roadClass)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return Color.FromHsv((hash % 360u) / 360f, 0.75f, 0.95f);
    }

    public Color NodeColour(RoadNodeKind kind) =>
        kind switch
        {
            RoadNodeKind.Junction => JunctionColour,
            RoadNodeKind.Joint => JointColour,
            RoadNodeKind.DeadEnd => DeadEndColour,
            RoadNodeKind.SpanEnd => SpanEndColour,
            _ => AreaEdgeColour,
        };

    private void OnMapLoaded(int tileCount)
    {
        // A reload may be another pack: drop the mesh and, if the view was on, switch it on for the new graph.
        var wasEnabled = Enabled;
        Enabled = false;
        MeshInstance.Visible = false;
        MeshInstance.Mesh = null;
        LegendPanel.Visible = false;
        _builtFor = null;
        if (wasEnabled && !SetEnabled(true))
        {
            EmitSignal(SignalName.Toggled, false);
        }
    }

    private StandardMaterial3D CreateMaterial() =>
        new()
        {
            ResourceName = "RoadGraphDebug",
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = DrawOnTop,
            DisableFog = true,
            DisableReceiveShadows = true,
        };

    /// <summary>
    /// Builds the drawing as one triangle mesh. Layers are stacked a few centimetres apart (walkable edges, drivable
    /// edges, arrows, nodes), so overlapping parts don't flicker against each other.
    /// </summary>
    private ImmediateMesh? BuildMesh(RoadGraph graph)
    {
        if (graph.Edges.Count == 0 && graph.Nodes.Count == 0)
        {
            return null;
        }

        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);

        // Walkable-only first, so drivable ribbons are drawn above them where they share a node.
        foreach (var edge in graph.Edges.Where(edge => !edge.Drivable))
        {
            AddRibbon(mesh, edge, EdgeColour(edge), HeightOffset);
        }

        foreach (var edge in graph.Edges.Where(edge => edge.Drivable))
        {
            AddRibbon(mesh, edge, EdgeColour(edge), HeightOffset + 0.03f);
        }

        foreach (var edge in graph.Edges.Where(edge => edge.Drivable && edge.Oneway))
        {
            AddArrow(mesh, edge, HeightOffset + 0.06f);
        }

        foreach (var node in graph.Nodes)
        {
            AddNodeMarker(mesh, node, HeightOffset + 0.09f);
        }

        mesh.SurfaceEnd();
        return mesh;
    }

    private void AddRibbon(ImmediateMesh mesh, RoadEdge edge, Color colour, float lift)
    {
        var half = EdgeWidth / 2f;
        for (var i = 0; i + 1 < edge.Points.Count; i++)
        {
            var a = ToVector(edge.Points[i], lift);
            var b = ToVector(edge.Points[i + 1], lift);
            if (Side(a, b, half) is not { } side)
            {
                continue;
            }

            AddQuad(mesh, colour, a - side, a + side, b + side, b - side);
        }
    }

    /// <summary>A triangle at the middle of the edge (by length along the polyline), pointing from From to To.</summary>
    private void AddArrow(ImmediateMesh mesh, RoadEdge edge, float lift)
    {
        var points = edge.Points.Select(point => ToVector(point, lift)).ToList();
        var total = 0f;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            total += points[i].DistanceTo(points[i + 1]);
        }

        if (total <= 0f)
        {
            return;
        }

        var target = total / 2f;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var segment = points[i].DistanceTo(points[i + 1]);
            if (segment <= 0f || target > segment)
            {
                target -= segment;
                continue;
            }

            var direction = (points[i + 1] - points[i]) / segment;
            var middle = points[i] + (direction * target);
            if (Side(points[i], points[i + 1], ArrowWidth / 2f) is not { } side)
            {
                return;
            }

            var tip = middle + (direction * (ArrowLength / 2f));
            var back = middle - (direction * (ArrowLength / 2f));
            AddTriangle(mesh, ArrowColour, back - side, tip, back + side);
            return;
        }
    }

    private void AddNodeMarker(ImmediateMesh mesh, RoadNode node, float lift)
    {
        var centre = ToVector(node.Position, lift);
        var colour = NodeColour(node.Kind);
        var half = NodeSize / 2f * node.Kind switch
        {
            RoadNodeKind.Joint => 0.5f,
            RoadNodeKind.AreaEdge => 1.5f,
            _ => 1f,
        };

        if (node.Kind == RoadNodeKind.DeadEnd)
        {
            // A diamond, so dead ends differ from area edges in shape as well as colour.
            var r = half * 1.3f;
            AddQuad(
                mesh,
                colour,
                centre + new Vector3(0f, 0f, -r),
                centre + new Vector3(r, 0f, 0f),
                centre + new Vector3(0f, 0f, r),
                centre + new Vector3(-r, 0f, 0f));
            return;
        }

        if (node.Kind == RoadNodeKind.SpanEnd)
        {
            // A triangle, so span ends differ from the square joints they usually sit next to.
            var r = half * 1.3f;
            AddTriangle(
                mesh,
                colour,
                centre + new Vector3(0f, 0f, -r),
                centre + new Vector3(r * 0.866f, 0f, r * 0.5f),
                centre + new Vector3(-r * 0.866f, 0f, r * 0.5f));
            return;
        }

        AddQuad(
            mesh,
            colour,
            centre + new Vector3(-half, 0f, -half),
            centre + new Vector3(half, 0f, -half),
            centre + new Vector3(half, 0f, half),
            centre + new Vector3(-half, 0f, half));
    }

    /// <summary>The horizontal half-width vector perpendicular to a → b; null for a vertical or zero-length segment.</summary>
    private static Vector3? Side(Vector3 a, Vector3 b, float halfWidth)
    {
        var flat = new Vector3(b.X - a.X, 0f, b.Z - a.Z);
        var length = flat.Length();
        if (length < 1e-4f)
        {
            return null;
        }

        return new Vector3(-flat.Z / length, 0f, flat.X / length) * halfWidth;
    }

    private static void AddQuad(ImmediateMesh mesh, Color colour, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        AddTriangle(mesh, colour, a, b, c);
        AddTriangle(mesh, colour, a, c, d);
    }

    private static void AddTriangle(ImmediateMesh mesh, Color colour, Vector3 a, Vector3 b, Vector3 c)
    {
        foreach (var vertex in new[] { a, b, c })
        {
            mesh.SurfaceSetColor(colour);
            mesh.SurfaceSetNormal(Vector3.Up);
            mesh.SurfaceAddVertex(vertex);
        }
    }

    private static Vector3 ToVector(LocalPoint point, float lift) => new((float)point.X, (float)point.Y + lift, (float)point.Z);

    private void UpdateLegend(RoadGraph graph)
    {
        var rows = (VBoxContainer)LegendPanel.GetChild(0);
        foreach (var child in rows.GetChildren())
        {
            rows.RemoveChild(child);
            child.QueueFree();
        }

        AddLegendRow(rows, "Road graph (G)", null);
        if (graph.Nodes.Count == 0 && graph.Edges.Count == 0)
        {
            AddLegendRow(rows, EmptyGraphText, null);
            return;
        }

        foreach (var roadClass in graph.Edges.Where(edge => edge.Drivable).Select(edge => edge.Class).Distinct().Order())
        {
            AddLegendRow(rows, roadClass, ClassColour(roadClass));
        }

        if (graph.Edges.Any(edge => !edge.Drivable))
        {
            AddLegendRow(rows, "walkable only", WalkableColour);
        }

        AddLegendRow(rows, "one-way arrow", ArrowColour);
        AddLegendRow(rows, "junction", JunctionColour);
        AddLegendRow(rows, "joint", JointColour);
        AddLegendRow(rows, "dead end (diamond)", DeadEndColour);
        AddLegendRow(rows, "area edge (large)", AreaEdgeColour);
        AddLegendRow(rows, "span end (triangle)", SpanEndColour);
        AddLegendRow(rows, $"{graph.Nodes.Count} nodes, {graph.Edges.Count} edges", null);
    }

    private void AddLegendRow(VBoxContainer rows, string text, Color? colour)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        if (colour is { } swatch)
        {
            row.AddChild(new ColorRect
            {
                Color = swatch,
                CustomMinimumSize = new Vector2(FontSize, FontSize),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }

        row.AddChild(CreateLabel(text));
        rows.AddChild(row);
    }

    private void ShowMessage(string message)
    {
        MessageLabel.Text = message;
        MessagePanel.Visible = true;
        var serial = ++_messageSerial;
        if (MessageSeconds > 0f && IsInsideTree())
        {
            GetTree().CreateTimer(MessageSeconds).Timeout += () =>
            {
                if (serial == _messageSerial && IsInstanceValid(MessagePanel))
                {
                    MessagePanel.Visible = false;
                }
            };
        }
    }

    private void BuildOverlay()
    {
        Overlay = new CanvasLayer { Name = "RoadGraphOverlay", Layer = OverlayLayer };
        AddChild(Overlay);

        LegendPanel = CreatePanel("Legend");
        LegendPanel.AddChild(new VBoxContainer { Name = "Rows", MouseFilter = Control.MouseFilterEnum.Ignore });
        Overlay.AddChild(LegendPanel);
        LegendPanel.GrowHorizontal = Control.GrowDirection.Begin;
        LegendPanel.AnchorLeft = 1f;
        LegendPanel.AnchorRight = 1f;
        LegendPanel.OffsetLeft = -ScreenMargin;
        LegendPanel.OffsetRight = -ScreenMargin;
        LegendPanel.OffsetTop = ScreenMargin;
        LegendPanel.OffsetBottom = ScreenMargin;

        MessagePanel = CreatePanel("Message");
        MessageLabel = CreateLabel("");
        MessageLabel.Name = "MessageLabel";
        MessagePanel.AddChild(MessageLabel);
        Overlay.AddChild(MessagePanel);
        // Below the orthophoto view's message, which uses the top-left corner too.
        MessagePanel.Position = new Vector2(ScreenMargin, ScreenMargin + (FontSize * 3));
    }

    private PanelContainer CreatePanel(string name)
    {
        var style = new StyleBoxFlat { BgColor = PanelColour };
        style.SetContentMarginAll(6f);
        var panel = new PanelContainer { Name = $"{name}Panel", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    private Label CreateLabel(string text) =>
        new()
        {
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            LabelSettings = new LabelSettings
            {
                FontSize = FontSize,
                FontColor = Colors.White,
                OutlineSize = OutlineSize,
                OutlineColor = Colors.Black,
            },
        };
}
