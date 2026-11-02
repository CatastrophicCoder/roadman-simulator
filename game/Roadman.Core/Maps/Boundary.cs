namespace Roadman.Core.Maps;

/// <summary>Where the boundary ring came from (<c>boundary.source</c> in <c>boundary.json</c>, ADR 0015).</summary>
public enum BoundarySource
{
    /// <summary>The build area rectangle (<c>area</c>).</summary>
    Area,

    /// <summary>The map's <c>world_edge.boundary_wgs84</c> polygon (<c>custom</c>).</summary>
    Custom,

    /// <summary>The test-area square of a test build (<c>test_area</c>).</summary>
    TestArea,
}

/// <summary>The traffic role of a road end at a roadblock (<c>roadblocks[].nodes[].role</c>).</summary>
public enum RoadblockRole
{
    /// <summary>A drivable edge: traffic spawns and despawns here (<c>spawn_despawn</c>).</summary>
    SpawnDespawn,

    /// <summary>A walk-only edge: pedestrians turn around here (<c>turn_around</c>).</summary>
    TurnAround,
}

/// <summary>A fence segment's type (<c>fences[].type</c>).</summary>
public enum FenceType
{
    /// <summary>On land (<c>land</c>).</summary>
    Land,

    /// <summary>Across water (<c>water</c>); has <see cref="Fence.WaterIds"/>.</summary>
    Water,
}

/// <summary>
/// The boundary of the playable area (<c>boundary</c>): a counter-clockwise ring that is not closed (the last point
/// joins the first), starting from its lowest-x, then lowest-z vertex.
/// </summary>
/// <param name="Points">At least 3 <c>[x, z]</c> points.</param>
/// <param name="LengthM">The ring's length in metres; <c>s</c> values elsewhere are measured along it.</param>
/// <param name="Source">Where the ring came from.</param>
public sealed record BoundaryRing(IReadOnlyList<PlanarPoint> Points, double LengthM, BoundarySource Source);

/// <summary>One road end at a roadblock (<c>roadblocks[].nodes[]</c>).</summary>
/// <param name="Node">An <c>area_edge</c> node id of <c>road_graph.json</c>.</param>
/// <param name="Edge">The id of the edge that ends there.</param>
/// <param name="Role">Its traffic role.</param>
public sealed record RoadblockNode(string Node, string Edge, RoadblockRole Role);

/// <summary>
/// A roadblock where roads leave the playable area (<c>roadblocks[]</c>). Points are Godot local metres; an item
/// spanning the ring's first point has <see cref="SEnd"/> greater than <see cref="BoundaryRing.LengthM"/>.
/// </summary>
/// <param name="Id"><c>roadblock:&lt;lowest node id&gt;</c>.</param>
/// <param name="Nodes">At least one road end, sorted by node id.</param>
/// <param name="Class">The road class of the widest edge.</param>
/// <param name="Drivable">Whether any of its edges is drivable.</param>
/// <param name="Walkable">Whether any of its edges is walkable.</param>
/// <param name="WidthM">The widest edge's width, above 0.</param>
/// <param name="WidthSource">Whether that width came from OSM or the class defaults.</param>
/// <param name="Start">Span start on the boundary.</param>
/// <param name="End">Span end on the boundary.</param>
/// <param name="Position">The span's midpoint on the ring.</param>
/// <param name="YawDeg">Rotation about +Y, in (−180, 180] degrees, that turns local +X along the ring.</param>
/// <param name="Outward">Unit <c>[x, 0, z]</c> pointing out of the playable area.</param>
/// <param name="SStart">Ring distance of the span start, at least 0.</param>
/// <param name="SEnd">Ring distance of the span end, above <see cref="SStart"/>.</param>
/// <param name="OverWater">Whether its span lies in a water crossing.</param>
public sealed record Roadblock(
    string Id,
    IReadOnlyList<RoadblockNode> Nodes,
    string Class,
    bool Drivable,
    bool Walkable,
    double WidthM,
    RoadValueSource WidthSource,
    LocalPoint Start,
    LocalPoint End,
    LocalPoint Position,
    double YawDeg,
    LocalPoint Outward,
    double SStart,
    double SEnd,
    bool OverWater);

/// <summary>A fence segment along the boundary (<c>fences[]</c>), in ring order.</summary>
/// <param name="Id"><c>fence:&lt;n&gt;</c>.</param>
/// <param name="Type">Land or water.</param>
/// <param name="Start">Segment start, on the terrain.</param>
/// <param name="End">Segment end, on the terrain.</param>
/// <param name="SStart">Ring distance of the start, at least 0.</param>
/// <param name="SEnd">Ring distance of the end, above <see cref="SStart"/>.</param>
/// <param name="Outward">Unit <c>[x, 0, z]</c> pointing out of the playable area.</param>
/// <param name="WaterIds">Water segments: the water areas it crosses (at least one). Null for land segments.</param>
public sealed record Fence(
    string Id,
    FenceType Type,
    LocalPoint Start,
    LocalPoint End,
    double SStart,
    double SEnd,
    LocalPoint Outward,
    IReadOnlyList<string>? WaterIds);

/// <summary>The cliff around the slab (<c>cliff</c>).</summary>
/// <param name="LedgeM">Ledge width outward from the boundary, at least 0.</param>
/// <param name="BottomY">The slab's bottom: the lowest boundary height minus <c>cliff.depth_m</c>.</param>
public sealed record Cliff(double LedgeM, double BottomY);

/// <summary>One convex prism of the void trigger (<c>void_trigger.prisms[]</c>).</summary>
/// <param name="Footprint">A convex quadrilateral: exactly 4 <c>[x, z]</c> points, not closed.</param>
public sealed record VoidPrism(IReadOnlyList<PlanarPoint> Footprint);

/// <summary>The volume outside the boundary that counts as falling off the world (<c>void_trigger</c>).</summary>
/// <param name="BottomY">Bottom of every prism, below <see cref="TopY"/>.</param>
/// <param name="TopY">Top of every prism.</param>
/// <param name="Prisms">At least one prism.</param>
public sealed record VoidTrigger(double BottomY, double TopY, IReadOnlyList<VoidPrism> Prisms);

/// <summary>Where the player spawns and respawns (<c>spawn_area</c>).</summary>
/// <param name="Polygon">The spawn polygon from <c>markers.json</c>: at least 3 <c>[x, z]</c> points, not closed.</param>
/// <param name="OpenGround">
/// The polygon minus buildings, water, grown road, path and deck surfaces and the band near the boundary: at least
/// one polygon of closed rings (at least 4 points each), exterior first.
/// </param>
/// <param name="OpenAreaM2">The open ground's area as the pipeline measured it, above 0.</param>
public sealed record SpawnArea(IReadOnlyList<PlanarPoint> Polygon, IReadOnlyList<PlanarPolygon> OpenGround, double OpenAreaM2);

/// <summary>A stretch of the boundary that lies in water (<c>water_crossings[]</c>).</summary>
/// <param name="SStart">Ring distance of the start, at least 0.</param>
/// <param name="SEnd">Ring distance of the end, at least 0 (may exceed the ring length for a crossing over its start).</param>
/// <param name="Source">
/// The <c>map.yaml</c> source name the water areas came from (for Somero <c>mml_topographic</c>; <c>osm</c> in OSM
/// mode). A free, non-empty name, because map files name their sources.
/// </param>
/// <param name="WaterIds">The water areas crossed, at least one.</param>
public sealed record WaterCrossing(double SStart, double SEnd, string Source, IReadOnlyList<string> WaterIds);

/// <summary>
/// The pack's <c>boundary.json</c> (schema version 1, ADR 0015): the world edge. Read it with
/// <see cref="BoundaryReader"/>, which checks it against the manifest.
/// </summary>
/// <param name="SchemaVersion">Always <see cref="BoundaryReader.SupportedSchemaVersion"/> once parsed.</param>
/// <param name="MapId">The manifest's map id.</param>
/// <param name="Area">The manifest's area, <c>full</c> or <c>test</c>.</param>
/// <param name="Ring">The boundary ring (<c>boundary</c>).</param>
/// <param name="Roadblocks">Every roadblock, in file order.</param>
/// <param name="Fences">Every fence segment, in file order.</param>
/// <param name="Cliff">The cliff.</param>
/// <param name="VoidTrigger">The void trigger.</param>
/// <param name="SpawnArea">The spawn area.</param>
/// <param name="WaterCrossings">Every water crossing, in file order.</param>
public sealed record Boundary(
    int SchemaVersion,
    string MapId,
    string Area,
    BoundaryRing Ring,
    IReadOnlyList<Roadblock> Roadblocks,
    IReadOnlyList<Fence> Fences,
    Cliff Cliff,
    VoidTrigger VoidTrigger,
    SpawnArea SpawnArea,
    IReadOnlyList<WaterCrossing> WaterCrossings);
