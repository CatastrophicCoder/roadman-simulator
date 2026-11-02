using System.Text.Json;
using System.Text.RegularExpressions;

namespace Roadman.Core.Maps;

/// <summary>
/// Parses and checks a pack's <c>boundary.json</c> (schema version 1, ADR 0015, <c>docs/specs/player-on-foot.md</c>)
/// against the pack's manifest. Any problem throws a <see cref="MapPackException"/> that names the file and the
/// field, like <see cref="RoadGraphReader"/>. The rules follow the pipeline's model
/// (<c>tools/mapgen/src/mapgen/build/world_edge/model.py</c>), which forbids extra keys: another schema version; a
/// missing, mistyped or unknown key at any level; an unknown boundary <c>source</c>, node <c>role</c>, fence
/// <c>type</c> or <c>width_source</c>; a <c>map_id</c> or <c>area</c> that isn't the manifest's; a boundary ring or
/// spawn polygon with fewer than 3 points; an <c>open_ground</c> polygon without rings, or a ring with fewer than 4
/// points or not closed, a polygon whose holes are larger than its exterior, or open ground whose computed area
/// (<see cref="PlanarPolygon.Area"/> summed) isn't above 0; a void prism footprint that isn't 4 points; <c>open_area_m2</c>, <c>length_m</c> or a
/// roadblock's <c>width_m</c> not above 0; a negative <c>ledge_m</c> or <c>s</c> value; a roadblock or fence whose
/// <c>s_end</c> isn't above its <c>s_start</c>; <c>yaw_deg</c> outside (−180, 180]; a fence id not of the form
/// <c>fence:&lt;n&gt;</c>; <c>water_ids</c> on a land fence or missing on a water fence; an empty <c>nodes</c>,
/// <c>prisms</c> or <c>water_ids</c>; a duplicate roadblock or fence id; <c>void_trigger.bottom_y</c> ≥
/// <c>top_y</c>; and non-finite numbers.
/// </summary>
/// <remarks>
/// <c>water_crossings[].source</c> is a <c>map.yaml</c> source name, a free non-empty string in the pipeline's
/// model, so it is checked for being non-empty rather than against a fixed list.
/// </remarks>
public static partial class BoundaryReader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly string[] RootFields =
    [
        "schema_version", "map_id", "area", "boundary", "roadblocks", "fences", "cliff", "void_trigger",
        "spawn_area", "water_crossings",
    ];

    private static readonly string[] RingFields = ["ring", "length_m", "source"];

    private static readonly string[] RoadblockFields =
    [
        "id", "nodes", "class", "drivable", "walkable", "width_m", "width_source", "start", "end", "position",
        "yaw_deg", "outward", "s_start", "s_end", "over_water",
    ];

    private static readonly string[] RoadblockNodeFields = ["node", "edge", "role"];

    private static readonly string[] FenceFields = ["id", "type", "start", "end", "s_start", "s_end", "outward", "water_ids"];

    private static readonly string[] CliffFields = ["ledge_m", "bottom_y"];

    private static readonly string[] VoidTriggerFields = ["bottom_y", "top_y", "prisms"];

    private static readonly string[] PrismFields = ["footprint"];

    private static readonly string[] SpawnAreaFields = ["polygon", "open_ground", "open_area_m2"];

    private static readonly string[] WaterCrossingFields = ["s_start", "s_end", "source", "water_ids"];

    private static readonly Dictionary<string, BoundarySource> BoundarySources = new(StringComparer.Ordinal)
    {
        ["area"] = BoundarySource.Area,
        ["custom"] = BoundarySource.Custom,
        ["test_area"] = BoundarySource.TestArea,
    };

    private static readonly Dictionary<string, RoadblockRole> Roles = new(StringComparer.Ordinal)
    {
        ["spawn_despawn"] = RoadblockRole.SpawnDespawn,
        ["turn_around"] = RoadblockRole.TurnAround,
    };

    private static readonly Dictionary<string, FenceType> FenceTypes = new(StringComparer.Ordinal)
    {
        ["land"] = FenceType.Land,
        ["water"] = FenceType.Water,
    };

    private static readonly Dictionary<string, RoadValueSource> WidthSources = new(StringComparer.Ordinal)
    {
        ["osm"] = RoadValueSource.Osm,
        ["default"] = RoadValueSource.Default,
    };

    /// <param name="json">The file's contents.</param>
    /// <param name="manifest">The pack's manifest; <c>map_id</c> and <c>area</c> must be its own.</param>
    /// <param name="source">Name used in error messages, usually the file path.</param>
    public static Boundary Parse(string json, MapManifest manifest, string source = MapPack.BoundaryFile)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var document = PackJson.Parse(json, source);
        var root = PackJson.Root(document, source);
        PackJson.RejectUnknown(root, source, "", RootFields);

        var version = PackJson.Int(root, "schema_version", source, "");
        if (version != SupportedSchemaVersion)
        {
            throw new MapPackException(
                $"{source}: schema_version {version} is not supported; this game reads version {SupportedSchemaVersion}");
        }

        var mapId = PackJson.String(root, "map_id", source, "");
        if (mapId != manifest.MapId)
        {
            throw PackJson.Error(source, "map_id", $"is '{mapId}', but the manifest's is '{manifest.MapId}'");
        }

        var area = PackJson.String(root, "area", source, "");
        if (area is not ("full" or "test"))
        {
            throw PackJson.Error(source, "area", $"must be 'full' or 'test', not '{area}'");
        }

        if (area != manifest.Area)
        {
            throw PackJson.Error(source, "area", $"is '{area}', but the manifest's is '{manifest.Area}'");
        }

        var ring = ParseRing(PackJson.Object(root, "boundary", source, ""), source, "boundary");

        var roadblocks = Items(root, "roadblocks", source, ParseRoadblock);
        RejectDuplicateIds(roadblocks, r => r.Id, "roadblocks", "roadblock", source);

        var fences = Items(root, "fences", source, ParseFence);
        RejectDuplicateIds(fences, f => f.Id, "fences", "fence", source);

        var cliff = ParseCliff(PackJson.Object(root, "cliff", source, ""), source, "cliff");
        var voidTrigger = ParseVoidTrigger(PackJson.Object(root, "void_trigger", source, ""), source, "void_trigger");
        var spawnArea = ParseSpawnArea(PackJson.Object(root, "spawn_area", source, ""), source, "spawn_area");
        var crossings = Items(root, "water_crossings", source, ParseWaterCrossing);

        return new Boundary(version, mapId, area, ring, roadblocks, fences, cliff, voidTrigger, spawnArea, crossings);
    }

    private static BoundaryRing ParseRing(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, RingFields);
        var points = Points2(PackJson.Array(element, "ring", source, path), source, PackJson.Join(path, "ring"));
        if (points.Count < 3)
        {
            throw PackJson.Error(source, PackJson.Join(path, "ring"), $"must have at least 3 points, has {points.Count}");
        }

        var length = Positive(element, "length_m", source, path);
        var ringSource = PackJson.Choice(element, "source", BoundarySources, source, path);
        return new BoundaryRing(points, length, ringSource);
    }

    private static Roadblock ParseRoadblock(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, RoadblockFields);
        var id = PackJson.NonEmptyString(element, "id", source, path);
        var nodes = Items(element, "nodes", source, ParseRoadblockNode, path);
        if (nodes.Count == 0)
        {
            throw PackJson.Error(source, PackJson.Join(path, "nodes"), $"must have at least one node (roadblock '{id}')");
        }

        var roadClass = PackJson.NonEmptyString(element, "class", source, path);
        var drivable = PackJson.Bool(element, "drivable", source, path);
        var walkable = PackJson.Bool(element, "walkable", source, path);
        var width = Positive(element, "width_m", source, path);
        var widthSource = PackJson.Choice(element, "width_source", WidthSources, source, path);
        var start = Point3(element, "start", source, path);
        var end = Point3(element, "end", source, path);
        var position = Point3(element, "position", source, path);
        var yaw = PackJson.Number(element, "yaw_deg", source, path);
        if (yaw <= -180 || yaw > 180)
        {
            throw PackJson.Error(source, PackJson.Join(path, "yaw_deg"), FormattableString.Invariant($"must be in (-180, 180], is {yaw}"));
        }

        var outward = Point3(element, "outward", source, path);
        var (sStart, sEnd) = Span(element, source, path, ordered: true);
        var overWater = PackJson.Bool(element, "over_water", source, path);
        return new Roadblock(
            id, nodes, roadClass, drivable, walkable, width, widthSource, start, end, position, yaw, outward,
            sStart, sEnd, overWater);
    }

    private static RoadblockNode ParseRoadblockNode(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, RoadblockNodeFields);
        return new RoadblockNode(
            PackJson.NonEmptyString(element, "node", source, path),
            PackJson.NonEmptyString(element, "edge", source, path),
            PackJson.Choice(element, "role", Roles, source, path));
    }

    private static Fence ParseFence(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, FenceFields);
        var id = PackJson.String(element, "id", source, path);
        if (!FenceIdRegex().IsMatch(id))
        {
            throw PackJson.Error(source, PackJson.Join(path, "id"), $"'{id}' is not of the form fence:<n>");
        }

        var type = PackJson.Choice(element, "type", FenceTypes, source, path);
        var start = Point3(element, "start", source, path);
        var end = Point3(element, "end", source, path);
        var (sStart, sEnd) = Span(element, source, path, ordered: true);
        var outward = Point3(element, "outward", source, path);

        // The pipeline writes water_ids for water segments only (absent, never null, on land segments).
        IReadOnlyList<string>? waterIds = null;
        if (PackJson.TryGet(element, "water_ids", out _))
        {
            if (type != FenceType.Water)
            {
                throw PackJson.Error(source, PackJson.Join(path, "water_ids"), $"must be absent on a land fence ('{id}')");
            }

            waterIds = WaterIds(element, source, path);
        }
        else if (type == FenceType.Water)
        {
            throw PackJson.Error(source, PackJson.Join(path, "water_ids"), $"is missing on water fence '{id}'");
        }

        return new Fence(id, type, start, end, sStart, sEnd, outward, waterIds);
    }

    private static Cliff ParseCliff(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, CliffFields);
        var ledge = PackJson.Number(element, "ledge_m", source, path);
        if (ledge < 0)
        {
            throw PackJson.Error(source, PackJson.Join(path, "ledge_m"), FormattableString.Invariant($"must not be negative, is {ledge}"));
        }

        return new Cliff(ledge, PackJson.Number(element, "bottom_y", source, path));
    }

    private static VoidTrigger ParseVoidTrigger(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, VoidTriggerFields);
        var bottom = PackJson.Number(element, "bottom_y", source, path);
        var top = PackJson.Number(element, "top_y", source, path);
        if (bottom >= top)
        {
            throw PackJson.Error(source, PackJson.Join(path, "bottom_y"), FormattableString.Invariant($"({bottom}) must be below top_y ({top})"));
        }

        var prisms = Items(element, "prisms", source, ParsePrism, path);
        if (prisms.Count == 0)
        {
            throw PackJson.Error(source, PackJson.Join(path, "prisms"), "must have at least one prism");
        }

        return new VoidTrigger(bottom, top, prisms);
    }

    private static VoidPrism ParsePrism(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, PrismFields);
        var footprintPath = PackJson.Join(path, "footprint");
        var footprint = Points2(PackJson.Array(element, "footprint", source, path), source, footprintPath);
        if (footprint.Count != 4)
        {
            throw PackJson.Error(source, footprintPath, $"must have exactly 4 points, has {footprint.Count}");
        }

        return new VoidPrism(footprint);
    }

    private static SpawnArea ParseSpawnArea(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, SpawnAreaFields);
        var polygonPath = PackJson.Join(path, "polygon");
        var polygon = Points2(PackJson.Array(element, "polygon", source, path), source, polygonPath);
        if (polygon.Count < 3)
        {
            throw PackJson.Error(source, polygonPath, $"must have at least 3 points, has {polygon.Count}");
        }

        var openPath = PackJson.Join(path, "open_ground");
        var open = PackJson.Array(element, "open_ground", source, path);
        if (open.GetArrayLength() == 0)
        {
            throw PackJson.Error(source, openPath, "must have at least one polygon");
        }

        var polygons = new List<PlanarPolygon>(open.GetArrayLength());
        var i = 0;
        var openGroundArea = 0.0;
        foreach (var item in open.EnumerateArray())
        {
            var itemPath = $"{openPath}[{i}]";
            var openPolygon = OpenGroundPolygon(item, source, itemPath);
            var polygonArea = openPolygon.Area;
            if (polygonArea < 0)
            {
                throw PackJson.Error(
                    source, itemPath, FormattableString.Invariant($"has holes larger than its exterior (area {polygonArea} m²)"));
            }

            openGroundArea += polygonArea;
            polygons.Add(openPolygon);
            i++;
        }

        // The spawn draws (SpawnPicker) need open ground with area; a degenerate ring set would only fail at spawn time.
        if (!(openGroundArea > 0))
        {
            throw PackJson.Error(
                source, openPath, FormattableString.Invariant($"has no area (its polygons add up to {openGroundArea} m²)"));
        }

        var openArea = Positive(element, "open_area_m2", source, path);
        return new SpawnArea(polygon, polygons, openArea);
    }

    private static PlanarPolygon OpenGroundPolygon(JsonElement polygon, string source, string path)
    {
        if (polygon.ValueKind != JsonValueKind.Array)
        {
            throw PackJson.Error(source, path, "must be a list of rings");
        }

        if (polygon.GetArrayLength() == 0)
        {
            throw PackJson.Error(source, path, "must have at least an exterior ring");
        }

        var rings = new List<IReadOnlyList<PlanarPoint>>(polygon.GetArrayLength());
        var j = 0;
        foreach (var ring in polygon.EnumerateArray())
        {
            var ringPath = $"{path}[{j}]";
            var points = Points2(ring, source, ringPath);
            if (points.Count < 4)
            {
                throw PackJson.Error(source, ringPath, $"must have at least 4 points, has {points.Count}");
            }

            if (points[0] != points[^1])
            {
                throw PackJson.Error(source, ringPath, "is not closed (the last point must repeat the first)");
            }

            rings.Add(points);
            j++;
        }

        return new PlanarPolygon(rings[0], rings.Skip(1).ToList());
    }

    private static WaterCrossing ParseWaterCrossing(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, WaterCrossingFields);
        var (sStart, sEnd) = Span(element, source, path, ordered: false);
        var crossingSource = PackJson.NonEmptyString(element, "source", source, path);
        return new WaterCrossing(sStart, sEnd, crossingSource, WaterIds(element, source, path));
    }

    /// <summary>A required array of objects, each parsed by <paramref name="parse"/> with its own path.</summary>
    private static List<T> Items<T>(
        JsonElement parent,
        string name,
        string source,
        Func<JsonElement, string, string, T> parse,
        string path = "")
    {
        var array = PackJson.Array(parent, name, source, path);
        var arrayPath = PackJson.Join(path, name);
        var result = new List<T>(array.GetArrayLength());
        var i = 0;
        foreach (var element in array.EnumerateArray())
        {
            var itemPath = $"{arrayPath}[{i}]";
            PackJson.RequireObject(element, source, itemPath);
            result.Add(parse(element, source, itemPath));
            i++;
        }

        return result;
    }

    private static void RejectDuplicateIds<T>(List<T> items, Func<T, string> id, string arrayPath, string what, string source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            if (!seen.Add(id(items[i])))
            {
                throw PackJson.Error(source, $"{arrayPath}[{i}].id", $"{what} id '{id(items[i])}' appears more than once");
            }
        }
    }

    private static List<string> WaterIds(JsonElement element, string source, string path)
    {
        var ids = PackJson.Strings(element, "water_ids", source, path);
        if (ids.Count == 0)
        {
            throw PackJson.Error(source, PackJson.Join(path, "water_ids"), "must have at least one water id");
        }

        return ids;
    }

    /// <summary><c>s_start</c> and <c>s_end</c>, both at least 0; with <paramref name="ordered"/>, s_end above s_start.</summary>
    private static (double Start, double End) Span(JsonElement element, string source, string path, bool ordered)
    {
        var start = NonNegative(element, "s_start", source, path);
        var end = NonNegative(element, "s_end", source, path);
        if (ordered && end <= start)
        {
            throw PackJson.Error(source, PackJson.Join(path, "s_end"), FormattableString.Invariant($"({end}) must be greater than s_start ({start})"));
        }

        return (start, end);
    }

    private static double NonNegative(JsonElement element, string name, string source, string path)
    {
        var value = PackJson.Number(element, name, source, path);
        return value >= 0 ? value : throw PackJson.Error(source, PackJson.Join(path, name), FormattableString.Invariant($"must not be negative, is {value}"));
    }

    private static double Positive(JsonElement element, string name, string source, string path)
    {
        var value = PackJson.Number(element, name, source, path);
        return value > 0 ? value : throw PackJson.Error(source, PackJson.Join(path, name), FormattableString.Invariant($"must be greater than 0, is {value}"));
    }

    private static LocalPoint Point3(JsonElement element, string name, string source, string path)
    {
        var values = PackJson.Numbers(element, name, 3, source, path);
        return new LocalPoint(values[0], values[1], values[2]);
    }

    /// <summary>A list of <c>[x, z]</c> points, each 2 finite numbers.</summary>
    private static List<PlanarPoint> Points2(JsonElement array, string source, string path)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw PackJson.Error(source, path, "must be a list of [x, z] points");
        }

        var points = new List<PlanarPoint>(array.GetArrayLength());
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            var pointPath = $"{path}[{i}]";
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2)
            {
                throw PackJson.Error(source, pointPath, "must be [x, z]");
            }

            points.Add(new PlanarPoint(
                PackJson.Number(item[0], source, $"{pointPath}[0]"),
                PackJson.Number(item[1], source, $"{pointPath}[1]")));
            i++;
        }

        return points;
    }

    [GeneratedRegex(@"^fence:[0-9]+\z")]
    private static partial Regex FenceIdRegex();
}
