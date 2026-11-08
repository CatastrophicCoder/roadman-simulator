using System;
using System.Collections.Generic;
using Godot;
using Roadman.Core.Edge;
using Roadman.Core.Maps;
using Roadman.Core.Minimap;
using Roadman.Core.Settings;
using Roadman.Scenes;
using Roadman.Ui;

namespace Roadman.Hud;

/// <summary>
/// The round minimap in a screen corner (<c>docs/specs/minimap.md</c>; ADR 0021), in debug and release builds. A
/// <see cref="CanvasLayer"/> built in code: one <see cref="Panel"/> control of <see cref="PanelDiameter"/> base units,
/// inset <see cref="ScreenMargin"/> from the two edges of its <see cref="Corner"/>.
/// <para>
/// Centred on the player and turned with the player camera's yaw (the direction the camera looks is panel up); the
/// rim is <see cref="RangeM"/> from the player. Layers, back to front, all inside the circle: the void (or the ground
/// colour over the whole circle without a boundary), the background image from <see cref="MapLoader.MinimapTexture"/>
/// placed by <see cref="MinimapProjection.ImageTransform"/> (without an image, the ground colour inside the boundary
/// ring), the ring line and roadblock bars, the roads per class group (bridges last, tunnels not at all), the street
/// labels, the point-of-interest symbols (upright), the <see cref="Markers"/>, then the player arrow and the "N" on the
/// rim. Geometry comes from <c>Roadman.Core.Minimap</c>; colours from <see cref="UiTheme"/>.
/// </para>
/// <para>
/// Each process frame (<see cref="UpdateFrame"/>) reads the player's position and facing and the player camera's
/// rendered yaw from <see cref="World"/>, projects and clips everything into reused lists, and asks the panel to
/// redraw; the panel's draw replays those lists (through <c>Vector2[]</c> arrays pooled per length). So the counts and
/// other test hooks are valid headless too. Once the lists have grown, <see cref="UpdateFrame"/> allocates only its
/// <see cref="MinimapProjection"/> (one small object), plus the label placement when it runs and the ring polygon cut
/// when there is no background image. Labels are placed again only when the centre has moved more than
/// <see cref="LabelRecomputeDistance"/> or the heading has turned more than <see cref="LabelRecomputeDegrees"/>
/// (<see cref="MinimapLabels.NeedsPlacement"/>); in between they are drawn at their stored ground positions.
/// </para>
/// <para>
/// Hidden (<see cref="IsShown"/> false) whenever there is no player, and whenever the death sequence isn't
/// <see cref="DeathState.Alive"/>; <c>Main</c> also hides the whole layer (<see cref="CanvasItem.Visible"/> of the
/// layer) while paused. Shown while the debug camera is on, still centred on the player with the player camera's yaw.
/// The prepared data (<see cref="Data"/>) is rebuilt on every <see cref="MapLoader.SignalName.MapLoaded"/>, which also
/// clears <see cref="Markers"/>.
/// </para>
/// </summary>
public partial class MinimapHud : CanvasLayer
{
    /// <summary>The text of the north indicator.</summary>
    public const string NorthText = "N";

    private static readonly Color[] White = [Colors.White];

    private readonly List<MinimapRoad> _visibleRoads = [];
    private readonly List<PanelPoint> _projected = [];
    private readonly List<PanelPoint> _piecePoints = [];
    private readonly List<int> _pieceLengths = [];
    private readonly List<PlanarPoint> _imageArea = [];
    private readonly List<PlanarPoint> _clipScratch = [];
    private readonly List<Vector2> _imagePoints = [];
    private readonly List<Vector2> _imageUvs = [];
    private readonly List<Vector2[]> _groundPolygons = [];
    private readonly List<Vector2> _strokePoints = [];
    private readonly List<Stroke> _strokes = [];
    private readonly List<FrameLabel> _frameLabels = [];
    private readonly List<FrameSymbol> _symbols = [];
    private readonly List<Vector2> _markers = [];
    private readonly List<string> _drawnRoadOrder = [];
    private readonly List<string> _labelTexts = [];
    private readonly List<MinimapWorldLabel> _worldLabels = [];
    private readonly Dictionary<int, Vector2[]> _pointPool = [];
    private readonly Dictionary<int, Vector2[]> _uvPool = [];
    private readonly Dictionary<string, (double Width, double Height)> _textSizes = new(StringComparer.Ordinal);
    private readonly Func<string, (double Width, double Height)> _measure;

    private MinimapCorner _corner = MinimapCorner.BottomLeft;
    private MinimapProjection? _labelsPlacedWith;
    private int _measuredFontSize = -1;
    private float _appliedDiameter = -1f;
    private float _appliedMargin = -1f;
    private bool _loaderHooked;
    private MapLoader? _hookedLoader;
    private bool _drawImage;
    private bool _drawVoid;
    private StyleBoxFlat? _parkingBox;
    private float _parkingBoxSymbolSize = -1f;

    public MinimapHud()
    {
        _measure = MeasureText;
    }

    /// <summary>Diameter of the round panel in base units (220 px at 1920 × 1080 with the default base size).</summary>
    [Export(PropertyHint.Range, "32,512,1,suffix:px")]
    public float PanelDiameter { get; set; } = 132f;

    /// <summary>Inset of the panel from both edges of its corner, base units.</summary>
    [Export(PropertyHint.Range, "0,128,1,suffix:px")]
    public float ScreenMargin { get; set; } = 16f;

    /// <summary>Metres from the player to the rim. There is no zoom.</summary>
    [Export(PropertyHint.Range, "10,2000,1,suffix:m")]
    public float RangeM { get; set; } = 150f;

    [ExportGroup("Roads")]
    [Export(PropertyHint.Range, "0.25,16,0.05,suffix:px")]
    public float MainRoadWidth { get; set; } = (float)MinimapStyle.StrokeOf(RoadGroup.MainRoad).Width;

    [Export(PropertyHint.Range, "0.25,16,0.05,suffix:px")]
    public float RoadWidth { get; set; } = (float)MinimapStyle.StrokeOf(RoadGroup.Road).Width;

    [Export(PropertyHint.Range, "0.25,16,0.05,suffix:px")]
    public float MinorRoadWidth { get; set; } = (float)MinimapStyle.StrokeOf(RoadGroup.MinorDrivable).Width;

    [Export(PropertyHint.Range, "0.25,16,0.05,suffix:px")]
    public float PathWidth { get; set; } = (float)MinimapStyle.StrokeOf(RoadGroup.Path).Width;

    [ExportGroup("Edge")]
    [Export(PropertyHint.Range, "0.25,8,0.05,suffix:px")]
    public float RingWidth { get; set; } = 1.5f;

    [Export(PropertyHint.Range, "0.25,16,0.05,suffix:px")]
    public float RoadblockWidth { get; set; } = 3f;

    /// <summary>Street label font size, base units.</summary>
    [ExportGroup("Labels")]
    [Export(PropertyHint.Range, "4,32,1")]
    public int LabelFontSize { get; set; } = 9;

    /// <summary>Street label outline size (Godot's outline size: the full width), base units.</summary>
    [Export(PropertyHint.Range, "0,8,1")]
    public int LabelOutlineSize { get; set; } = 2;

    /// <summary>Labels are placed again when the centre has moved this far since the last placement, m.</summary>
    [Export(PropertyHint.Range, "0,200,0.5,suffix:m")]
    public float LabelRecomputeDistance { get; set; } = 10f;

    /// <summary>Labels are placed again when the heading has turned this far since the last placement, degrees.</summary>
    [Export(PropertyHint.Range, "0,180,0.5,suffix:°")]
    public float LabelRecomputeDegrees { get; set; } = 12f;

    /// <summary>Side of a point-of-interest symbol, base units.</summary>
    [ExportGroup("Symbols")]
    [Export(PropertyHint.Range, "2,32,0.5,suffix:px")]
    public float SymbolSize { get; set; } = 7f;

    /// <summary>Diameter of a marker, base units.</summary>
    [Export(PropertyHint.Range, "2,32,0.5,suffix:px")]
    public float MarkerSize { get; set; } = 7f;

    /// <summary>Half the length of the player arrow, base units.</summary>
    [Export(PropertyHint.Range, "2,32,0.5,suffix:px")]
    public float ArrowSize { get; set; } = 7f;

    /// <summary>Font size of the north indicator, base units.</summary>
    [Export(PropertyHint.Range, "4,32,1")]
    public int NorthFontSize { get; set; } = 10;

    /// <summary>Corners of the polygon that stands in for the circle (image and fills).</summary>
    [Export(PropertyHint.Range, "16,256,1")]
    public int CircleSegments { get; set; } = 64;

    /// <summary>Canvas layer of the minimap, as the street label's.</summary>
    [Export]
    public int HudLayer { get; set; } = 10;

    /// <summary>The corner the panel sits in; setting it moves the panel at once.</summary>
    public MinimapCorner Corner
    {
        get => _corner;
        set
        {
            _corner = value;
            ApplyLayout(force: true);
        }
    }

    /// <summary>The map loader whose data is drawn; set before adding the node.</summary>
    public MapLoader? Loader { get; set; }

    /// <summary>The world whose player, player camera and death sequence the minimap follows; set before adding it.</summary>
    public World? World { get; set; }

    /// <summary>The marker layer's markers (none in the game yet); cleared on every map load.</summary>
    public MinimapMarkerSet Markers { get; } = new();

    /// <summary>The prepared map data of the last map load; <see cref="MinimapData.Empty"/> before one.</summary>
    public MinimapData Data { get; private set; } = MinimapData.Empty;

    /// <summary>The round panel; everything is drawn in it.</summary>
    public Control Panel { get; private set; } = null!;

    /// <summary>The panel's rectangle in canvas coordinates (base units, origin at the viewport's top left).</summary>
    public Rect2 PanelRect => Panel.GetGlobalRect();

    /// <summary>Whether the minimap shows now: the layer is visible and the player is there and alive.</summary>
    public bool IsShown => Visible && Panel is not null && Panel.Visible;

    /// <summary>The projection of the last frame drawn; null before one and while hidden.</summary>
    public MinimapProjection? Projection { get; private set; }

    /// <summary>Road polyline segments drawn in the last frame (after clipping).</summary>
    public int DrawnSegmentCount { get; private set; }

    /// <summary>Street labels drawn in the last frame.</summary>
    public int DrawnLabelCount => _frameLabels.Count;

    /// <summary>Point-of-interest symbols drawn in the last frame.</summary>
    public int DrawnSymbolCount => _symbols.Count;

    /// <summary>Markers drawn in the last frame.</summary>
    public int DrawnMarkerCount => _markers.Count;

    /// <summary>The edge ids of the roads drawn in the last frame, in draw order.</summary>
    public IReadOnlyList<string> DrawnRoadOrder => _drawnRoadOrder;

    /// <summary>The texts of the labels drawn in the last frame.</summary>
    public IReadOnlyList<string> LabelTexts => _labelTexts;

    /// <summary>
    /// The north indicator's centre in panel coordinates (origin at the panel centre, +y down) in the last frame.
    /// </summary>
    public Vector2 NorthIndicatorPosition { get; private set; }

    /// <summary>The player arrow's panel angle (radians clockwise from up) in the last frame.</summary>
    public float ArrowAngle { get; private set; }

    /// <summary>The marker positions drawn in the last frame, panel coordinates (origin at the centre).</summary>
    public IReadOnlyList<Vector2> MarkerPositions => _markers;

    /// <summary>Whether the last frame drew the background image (an image is loaded and part of it is in view).</summary>
    public bool HasBackground => _drawImage;

    /// <summary>
    /// Ground-colour polygons drawn in the last frame: the boundary ring cut to the circle, only without a background
    /// image; 0 with one.
    /// </summary>
    public int DrawnGroundPolygonCount => _groundPolygons.Count;

    /// <summary>Whether the last frame drew the void (the pack has a boundary) rather than ground over the whole circle.</summary>
    public bool DrawsVoid => _drawVoid;

    /// <summary>Times <see cref="Data"/> was rebuilt (one per map load); lets tests see a reload.</summary>
    public int DataBuildCount { get; private set; }

    private float Radius => PanelDiameter / 2f;

    public override void _Ready()
    {
        Layer = HudLayer;
        Panel = new Control
        {
            Name = "Panel",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
            Visible = false,
        };
        Panel.Draw += DrawPanel;
        AddChild(Panel);
        ApplyLayout(force: true);
        _parkingBox = new StyleBoxFlat
        {
            BgColor = UiTheme.MinimapParking,
            CornerDetail = 3,
        };
        UpdateParkingBox();
        HookLoader();
        if (Loader is not null && IsInstanceValid(Loader) && Loader.Manifest is not null)
        {
            Rebuild();
        }
    }

    public override void _EnterTree() => HookLoader();

    public override void _ExitTree()
    {
        if (_loaderHooked && _hookedLoader is not null && IsInstanceValid(_hookedLoader))
        {
            _hookedLoader.MapLoaded -= OnMapLoaded;
            _hookedLoader.MapLoadFailed -= OnMapLoadFailed;
        }

        _loaderHooked = false;
        _hookedLoader = null;
    }

    public override void _Process(double delta) => UpdateFrame();

    /// <summary>
    /// Rebuilds <see cref="Data"/> from <see cref="Loader"/>'s road graph, places and boundary (each may be null),
    /// clears the markers and drops the placed labels. Called on every map load.
    /// </summary>
    public void Rebuild()
    {
        Data = Loader is not null && IsInstanceValid(Loader)
            ? MinimapData.Build(Loader.RoadGraph, Loader.Places, Loader.Boundary)
            : MinimapData.Empty;
        DataBuildCount++;
        Markers.Clear();
        _worldLabels.Clear();
        _labelsPlacedWith = null;
    }

    /// <summary>
    /// One frame: decides whether the minimap shows, then projects and clips every layer into the frame lists and
    /// asks the panel to redraw. <see cref="_Process"/> calls it; tests may call it after moving things.
    /// </summary>
    public void UpdateFrame()
    {
        ApplyLayout(force: false);
        ClearFrame();
        if (Panel is null)
        {
            return;
        }

        if (!Visible || !TryGetSources(out var centre, out var heading, out var facing))
        {
            Panel.Visible = false;
            Projection = null;
            return;
        }

        var radius = Radius;
        var projection = new MinimapProjection(centre.X, centre.Z, heading, RangeM, radius);
        Projection = projection;
        Panel.Visible = true;

        _drawVoid = Data.HasBoundary;
        PrepareBackground(projection);
        PrepareEdge(projection, radius);
        PrepareRoads(projection, radius);
        NorthIndicatorPosition = ToVector(MinimapProjection.DirectionOf(projection.NorthBearing) * (radius - NorthInset()));
        ArrowAngle = (float)projection.ArrowAngle(facing);
        PrepareLabels(projection, radius);
        PrepareSymbols(projection, radius);
        PrepareMarkers(projection, radius);
        Panel.QueueRedraw();
    }

    /// <summary>
    /// The player's position, the heading (the player camera's rendered yaw: its right vector's direction, so the pitch
    /// doesn't matter) and the player's facing; false without a player and camera in the tree, or outside
    /// <see cref="DeathState.Alive"/>.
    /// </summary>
    private bool TryGetSources(out Vector3 centre, out double heading, out double facing)
    {
        centre = Vector3.Zero;
        heading = 0;
        facing = 0;
        if (World is not { } world || !IsInstanceValid(world)
            || world.Player is not { } player || !IsInstanceValid(player) || !player.IsInsideTree()
            || world.PlayerCamera is not { } camera || !IsInstanceValid(camera) || camera.Camera is null || !camera.Camera.IsInsideTree()
            || world.Death is null || world.Death.State != DeathState.Alive)
        {
            return false;
        }

        centre = player.GlobalPosition;
        var right = camera.Camera.GlobalTransform.Basis.X;
        heading = MinimapProjection.HeadingFromCameraRight(right.X, right.Z);
        facing = player.FacingYaw;
        return float.IsFinite(centre.X) && float.IsFinite(centre.Z) && double.IsFinite(heading);
    }

    private void ClearFrame()
    {
        _imagePoints.Clear();
        _imageUvs.Clear();
        _groundPolygons.Clear();
        _strokePoints.Clear();
        _strokes.Clear();
        _frameLabels.Clear();
        _symbols.Clear();
        _markers.Clear();
        _drawnRoadOrder.Clear();
        _labelTexts.Clear();
        DrawnSegmentCount = 0;
        _drawImage = false;
    }

    /// <summary>
    /// Layer 2: the part of the visible circle inside the image's bounds as a polygon with image UVs; without an image
    /// but with a boundary, the ring polygon cut to the circle in the ground colour.
    /// </summary>
    private void PrepareBackground(MinimapProjection projection)
    {
        var segments = Math.Max(16, CircleSegments);
        if (Loader?.MinimapImage is { } image && Loader.MinimapTexture is not null)
        {
            MinimapClip.CirclePolygon(projection.CentreX, projection.CentreZ, projection.RangeM, segments, _imageArea);
            MinimapClip.ClipConvexToRect(_imageArea, image.Bounds, _clipScratch);
            foreach (var point in _imageArea)
            {
                _imagePoints.Add(ToLocal(projection.ToPanel(point)));
                var (px, py) = image.PixelOf(point.X, point.Z);
                _imageUvs.Add(new Vector2((float)(px / image.Width), (float)(py / image.Height)));
            }

            // A view entirely off the image leaves no polygon: nothing of the image is drawn, only the void.
            _drawImage = _imageArea.Count >= 3;
            return;
        }

        if (Data.Ring is not { Count: >= 4 } ring)
        {
            return;
        }

        // Only without the image (an error case MapLoader already reported): Godot's polygon clipping, which allocates.
        var ringPanel = new Vector2[ring.Count - 1];
        for (var i = 0; i < ringPanel.Length; i++)
        {
            ringPanel[i] = ToLocal(projection.ToPanel(ring[i]));
        }

        var circle = new Vector2[segments];
        for (var i = 0; i < segments; i++)
        {
            circle[i] = ToLocal(MinimapProjection.DirectionOf(2 * Math.PI * i / segments) * projection.RadiusPx);
        }

        // The ring is a simple polygon and the circle convex, so the intersection has no holes.
        foreach (var polygon in Geometry2D.IntersectPolygons(ringPanel, circle))
        {
            if (polygon.Length >= 3)
            {
                _groundPolygons.Add(polygon);
            }
        }
    }

    /// <summary>Layer 3: the ring line and the roadblock bars, clipped to the circle.</summary>
    private void PrepareEdge(MinimapProjection projection, float radius)
    {
        if (Data.Ring is { } ring)
        {
            AddClippedStroke(projection, ring, radius, UiTheme.MinimapRing, RingWidth);
        }

        var bars = Data.Roadblocks;
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            _projected.Clear();
            _projected.Add(projection.ToPanel(bar.Start));
            _projected.Add(projection.ToPanel(bar.End));
            AddPieces(radius, UiTheme.MinimapRoadblock, RoadblockWidth, countSegments: false);
        }
    }

    /// <summary>Layer 4: the roads near the player in draw order, clipped to the circle.</summary>
    private void PrepareRoads(MinimapProjection projection, float radius)
    {
        Data.QueryRoads(projection.CentreX, projection.CentreZ, projection.RangeM, _visibleRoads);
        foreach (var road in _visibleRoads)
        {
            var stroke = MinimapStyle.StrokeOf(road.Group);
            var colour = ColourOf(road.Group);
            colour.A = (float)stroke.Opacity;
            _projected.Clear();
            var points = road.Points;
            for (var i = 0; i < points.Count; i++)
            {
                _projected.Add(projection.ToPanel(points[i]));
            }

            if (AddPieces(radius, colour, WidthOf(road.Group), countSegments: true) > 0)
            {
                _drawnRoadOrder.Add(road.EdgeId);
            }
        }
    }

    private void AddClippedStroke(MinimapProjection projection, IReadOnlyList<PlanarPoint> points, float radius, Color colour, float width)
    {
        _projected.Clear();
        for (var i = 0; i < points.Count; i++)
        {
            _projected.Add(projection.ToPanel(points[i]));
        }

        AddPieces(radius, colour, width, countSegments: false);
    }

    /// <summary>
    /// Clips <see cref="_projected"/> to the circle and adds each piece as a stroke; with
    /// <paramref name="countSegments"/> (roads) its segments count in <see cref="DrawnSegmentCount"/>.
    /// </summary>
    private int AddPieces(float radius, Color colour, float width, bool countSegments)
    {
        _piecePoints.Clear();
        _pieceLengths.Clear();
        MinimapClip.Clip(_projected, radius, _piecePoints, _pieceLengths);
        var offset = 0;
        foreach (var length in _pieceLengths)
        {
            var start = _strokePoints.Count;
            for (var i = 0; i < length; i++)
            {
                _strokePoints.Add(ToLocal(_piecePoints[offset + i]));
            }

            offset += length;
            _strokes.Add(new Stroke(start, length, colour, width));
            if (countSegments)
            {
                DrawnSegmentCount += length - 1;
            }
        }

        return _pieceLengths.Count;
    }

    /// <summary>
    /// Layer 5: places the labels again when needed (see the class summary), then re-projects the stored ones; a
    /// label that has left the circle since its placement isn't drawn.
    /// </summary>
    private void PrepareLabels(MinimapProjection projection, float radius)
    {
        if (_measuredFontSize != LabelFontSize)
        {
            _textSizes.Clear();
            _measuredFontSize = LabelFontSize;
            _labelsPlacedWith = null;
        }

        if (MinimapLabels.NeedsPlacement(_labelsPlacedWith, projection, LabelRecomputeDistance, LabelRecomputeDegrees))
        {
            PlaceLabels(projection, radius);
        }

        foreach (var label in _worldLabels)
        {
            var anchor = label.AnchorIn(projection);
            var angle = label.AngleIn(projection);
            var rect = new MinimapRect(anchor, label.Width / 2, label.Height / 2, angle);
            if (!rect.IsInsideCircle(radius - 1))
            {
                continue;
            }

            _frameLabels.Add(new FrameLabel(label.Text, ToLocal(anchor), (float)angle, (float)label.Width, (float)label.Height));
            _labelTexts.Add(label.Text);
        }
    }

    private void PlaceLabels(MinimapProjection projection, float radius)
    {
        _labelsPlacedWith = projection;
        _worldLabels.Clear();
        var sources = MinimapLabels.Collect(_visibleRoads, projection);
        if (sources.Count == 0)
        {
            return;
        }

        var arrow = MinimapRect.AxisAligned(new PanelPoint(0, 0), ArrowSize * 2.2, ArrowSize * 2.2);
        var (northWidth, northHeight) = NorthSize();
        // The drawn disc: diameter max(w, h) + 2 (DrawNorth).
        var northSide = Math.Max(northWidth, northHeight) + 2;
        var north = MinimapRect.AxisAligned(ToPanelPoint(NorthIndicatorPosition), northSide, northSide);
        foreach (var placement in MinimapLabels.Place(sources, _measure, radius, [arrow, north]))
        {
            _worldLabels.Add(placement.ToWorld(projection));
        }
    }

    private (double Width, double Height) MeasureText(string text)
    {
        if (!_textSizes.TryGetValue(text, out var size))
        {
            var measured = ThemeDB.FallbackFont.GetStringSize(text, HorizontalAlignment.Left, -1, LabelFontSize);
            size = (measured.X, measured.Y);
            _textSizes[text] = size;
        }

        return size;
    }

    /// <summary>Layer 6: every point of interest whose symbol lies inside the circle, upright.</summary>
    private void PrepareSymbols(MinimapProjection projection, float radius)
    {
        var limit = radius - (SymbolSize / 2f);
        var pois = Data.Pois;
        for (var i = 0; i < pois.Count; i++)
        {
            var poi = pois[i];
            var point = projection.ToPanel(poi.X, poi.Z);
            if (MinimapClip.IsInside(point, limit))
            {
                _symbols.Add(new FrameSymbol(ToLocal(point), poi.Group));
            }
        }
    }

    /// <summary>Layer 7: the markers inside the circle, and the clamped ones outside it on the rim.</summary>
    private void PrepareMarkers(MinimapProjection projection, float radius)
    {
        if (Markers.Count == 0)
        {
            return;
        }

        var limit = radius - (MarkerSize / 2f);
        var markers = Markers.All;
        for (var i = 0; i < markers.Count; i++)
        {
            var marker = markers[i];
            var point = projection.ToPanel(marker.X, marker.Z);
            if (MinimapClip.IsInside(point, limit))
            {
                _markers.Add(ToVector(point));
            }
            else if (marker.ClampToRim)
            {
                _markers.Add(ToVector(MinimapClip.ClampToRim(point, limit)));
            }
        }
    }

    /// <summary>Replays the frame lists (the panel's draw callback).</summary>
    private void DrawPanel()
    {
        var radius = Radius;
        var centre = new Vector2(radius, radius);
        Panel.DrawCircle(centre, radius, _drawVoid ? UiTheme.MinimapVoid : UiTheme.MinimapGround, filled: true, width: -1f, antialiased: true);

        if (_drawImage && _imagePoints.Count >= 3 && Loader?.MinimapTexture is { } texture)
        {
            var points = Rent(_pointPool, _imagePoints.Count);
            var uvs = Rent(_uvPool, _imageUvs.Count);
            _imagePoints.CopyTo(points);
            _imageUvs.CopyTo(uvs);
            Panel.DrawPolygon(points, White, uvs, texture);
        }

        foreach (var polygon in _groundPolygons)
        {
            Panel.DrawColoredPolygon(polygon, UiTheme.MinimapGround);
        }

        foreach (var stroke in _strokes)
        {
            var points = Rent(_pointPool, stroke.Count);
            _strokePoints.CopyTo(stroke.Start, points, 0, stroke.Count);
            Panel.DrawPolyline(points, stroke.Colour, stroke.Width, antialiased: true);
        }

        DrawLabels();
        DrawSymbols();
        DrawMarkers();
        DrawArrow(centre);
        DrawNorth(centre);
        Panel.DrawArc(centre, radius - 0.5f, 0f, Mathf.Tau, Math.Max(16, CircleSegments), UiTheme.Shade, 1f, antialiased: true);
    }

    private void DrawLabels()
    {
        var font = ThemeDB.FallbackFont;
        var ascent = font.GetAscent(LabelFontSize);
        foreach (var label in _frameLabels)
        {
            Panel.DrawSetTransform(label.Anchor, label.Angle);
            var position = new Vector2(-label.Width / 2f, (-label.Height / 2f) + ascent);
            if (LabelOutlineSize > 0)
            {
                Panel.DrawStringOutline(font, position, label.Text, HorizontalAlignment.Left, -1, LabelFontSize, LabelOutlineSize, UiTheme.Shade);
            }

            Panel.DrawString(font, position, label.Text, HorizontalAlignment.Left, -1, LabelFontSize, UiTheme.MinimapLabel);
        }

        Panel.DrawSetTransform(Vector2.Zero, 0f);
    }

    private void DrawSymbols()
    {
        var size = SymbolSize;
        var half = size / 2f;
        var font = ThemeDB.FallbackFont;
        var letterSize = Math.Max(4, (int)Math.Round(size * 1.1f));
        UpdateParkingBox();
        foreach (var symbol in _symbols)
        {
            var p = symbol.Position;
            switch (MinimapStyle.SymbolOf(symbol.Group))
            {
                case PoiSymbol.Square:
                    Panel.DrawRect(new Rect2(p.X - half, p.Y - half, size, size), UiTheme.MinimapShop);
                    break;
                case PoiSymbol.Circle:
                    Panel.DrawCircle(p, half, UiTheme.MinimapFood, filled: true, width: -1f, antialiased: true);
                    break;
                case PoiSymbol.Diamond:
                    var diamond = Rent(_pointPool, 4);
                    diamond[0] = p + new Vector2(0f, -half * 1.2f);
                    diamond[1] = p + new Vector2(half * 1.2f, 0f);
                    diamond[2] = p + new Vector2(0f, half * 1.2f);
                    diamond[3] = p + new Vector2(-half * 1.2f, 0f);
                    Panel.DrawColoredPolygon(diamond, UiTheme.MinimapPublicService);
                    break;
                case PoiSymbol.ParkingSign:
                    var box = _parkingBox!;
                    var rect = new Rect2(p.X - (half * 1.15f), p.Y - (half * 1.15f), size * 1.15f, size * 1.15f);
                    Panel.DrawStyleBox(box, rect);
                    var letter = font.GetStringSize(MinimapStyle.ParkingSymbolText, HorizontalAlignment.Left, -1, letterSize);
                    var baseline = new Vector2(p.X - (letter.X / 2f), p.Y - (letter.Y / 2f) + font.GetAscent(letterSize));
                    Panel.DrawString(font, baseline, MinimapStyle.ParkingSymbolText, HorizontalAlignment.Left, -1, letterSize, Colors.White);
                    break;
                default:
                    Panel.DrawCircle(p, Math.Max(1f, size / 4f), UiTheme.MinimapOtherPoi, filled: true, width: -1f, antialiased: true);
                    break;
            }
        }
    }

    /// <summary>Sets the parking sign's corner radius from <see cref="SymbolSize"/>; only when that changed.</summary>
    private void UpdateParkingBox()
    {
        if (_parkingBox is null || _parkingBoxSymbolSize == SymbolSize)
        {
            return;
        }

        _parkingBoxSymbolSize = SymbolSize;
        _parkingBox.SetCornerRadiusAll((int)Math.Max(1f, SymbolSize * 0.25f));
    }

    private void DrawMarkers()
    {
        var radius = Radius;
        var half = MarkerSize / 2f;
        foreach (var marker in _markers)
        {
            var p = marker + new Vector2(radius, radius);
            Panel.DrawCircle(p, half, UiTheme.MinimapMarker, filled: true, width: -1f, antialiased: true);
            Panel.DrawArc(p, half, 0f, Mathf.Tau, 16, UiTheme.Shade, 1f, antialiased: true);
        }
    }

    private void DrawArrow(Vector2 centre)
    {
        var s = ArrowSize;
        var rotation = Transform2D.Identity.Rotated(ArrowAngle);
        var arrow = Rent(_pointPool, 4);
        arrow[0] = centre + (rotation * new Vector2(0f, -s));
        arrow[1] = centre + (rotation * new Vector2(s * 0.72f, s * 0.8f));
        arrow[2] = centre + (rotation * new Vector2(0f, s * 0.35f));
        arrow[3] = centre + (rotation * new Vector2(-s * 0.72f, s * 0.8f));
        Panel.DrawColoredPolygon(arrow, UiTheme.Accent);
        var outline = Rent(_pointPool, 5);
        Array.Copy(arrow, outline, 4);
        outline[4] = arrow[0];
        Panel.DrawPolyline(outline, UiTheme.Shade, 1f, antialiased: true);
    }

    private void DrawNorth(Vector2 centre)
    {
        var font = ThemeDB.FallbackFont;
        var (width, height) = NorthSize();
        var p = centre + NorthIndicatorPosition;
        Panel.DrawCircle(p, (float)(Math.Max(width, height) / 2) + 1f, UiTheme.Shade, filled: true, width: -1f, antialiased: true);
        var baseline = new Vector2(p.X - ((float)width / 2f), p.Y - ((float)height / 2f) + font.GetAscent(NorthFontSize));
        Panel.DrawString(font, baseline, NorthText, HorizontalAlignment.Left, -1, NorthFontSize, UiTheme.Text);
    }

    private (double Width, double Height) NorthSize()
    {
        var size = ThemeDB.FallbackFont.GetStringSize(NorthText, HorizontalAlignment.Left, -1, NorthFontSize);
        return (size.X, size.Y);
    }

    /// <summary>How far inside the rim the north indicator's centre sits, base units.</summary>
    private float NorthInset()
    {
        var (width, height) = NorthSize();
        return (float)(Math.Max(width, height) / 2) + 2f;
    }

    /// <summary>Anchors the panel in <see cref="Corner"/>; only when a value changed unless <paramref name="force"/>.</summary>
    private void ApplyLayout(bool force)
    {
        if (Panel is null || (!force && _appliedDiameter == PanelDiameter && _appliedMargin == ScreenMargin))
        {
            return;
        }

        _appliedDiameter = PanelDiameter;
        _appliedMargin = ScreenMargin;
        var right = _corner is MinimapCorner.TopRight or MinimapCorner.BottomRight;
        var bottom = _corner is MinimapCorner.BottomLeft or MinimapCorner.BottomRight;
        var d = PanelDiameter;
        var m = ScreenMargin;
        Panel.AnchorLeft = right ? 1f : 0f;
        Panel.AnchorRight = right ? 1f : 0f;
        Panel.AnchorTop = bottom ? 1f : 0f;
        Panel.AnchorBottom = bottom ? 1f : 0f;
        Panel.OffsetLeft = right ? -m - d : m;
        Panel.OffsetRight = right ? -m : m + d;
        Panel.OffsetTop = bottom ? -m - d : m;
        Panel.OffsetBottom = bottom ? -m : m + d;
        Panel.QueueRedraw();
    }

    private void HookLoader()
    {
        if (_loaderHooked || Loader is null || !IsInstanceValid(Loader))
        {
            return;
        }

        Loader.MapLoaded += OnMapLoaded;
        Loader.MapLoadFailed += OnMapLoadFailed;
        _hookedLoader = Loader;
        _loaderHooked = true;
    }

    private void OnMapLoaded(int tileCount) => Rebuild();

    private void OnMapLoadFailed(string message) => Rebuild();

    private float WidthOf(RoadGroup group) => group switch
    {
        RoadGroup.MainRoad => MainRoadWidth,
        RoadGroup.Road => RoadWidth,
        RoadGroup.MinorDrivable => MinorRoadWidth,
        _ => PathWidth,
    };

    private static Color ColourOf(RoadGroup group) => group switch
    {
        RoadGroup.MainRoad => UiTheme.MinimapMainRoad,
        RoadGroup.Road => UiTheme.MinimapRoad,
        RoadGroup.MinorDrivable => UiTheme.MinimapMinorRoad,
        _ => UiTheme.MinimapPath,
    };

    /// <summary>A panel point (origin at the centre) in the panel control's own coordinates (origin top left).</summary>
    private Vector2 ToLocal(PanelPoint point) => new((float)point.X + Radius, (float)point.Y + Radius);

    private static Vector2 ToVector(PanelPoint point) => new((float)point.X, (float)point.Y);

    private static PanelPoint ToPanelPoint(Vector2 point) => new(point.X, point.Y);

    /// <summary>An array of exactly <paramref name="length"/> from <paramref name="pool"/>; draw calls copy it at once.</summary>
    private static Vector2[] Rent(Dictionary<int, Vector2[]> pool, int length)
    {
        if (!pool.TryGetValue(length, out var array))
        {
            array = new Vector2[length];
            pool[length] = array;
        }

        return array;
    }

    private readonly record struct Stroke(int Start, int Count, Color Colour, float Width);

    private readonly record struct FrameLabel(string Text, Vector2 Anchor, float Angle, float Width, float Height);

    private readonly record struct FrameSymbol(Vector2 Position, PoiGroup Group);
}
