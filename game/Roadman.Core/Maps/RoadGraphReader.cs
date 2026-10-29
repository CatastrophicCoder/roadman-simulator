using System.Text.Json;

namespace Roadman.Core.Maps;

/// <summary>
/// Parses and checks a pack's <c>road_graph.json</c> (schema version 1, <c>docs/specs/roads-and-road-graph.md</c>).
/// Any problem throws a <see cref="MapPackException"/> that names the file and the field, like
/// <see cref="MapManifestReader"/>: another schema version, a missing, mistyped or unknown field (the keys follow the
/// pipeline model, which forbids extra keys; nullable keys must still be present), an unknown node kind or source,
/// a duplicate node or edge id, an edge whose <c>from</c> or <c>to</c> isn't a node, fewer than 2 points, an edge
/// that is neither drivable nor walkable, a drivable edge without <c>speed_kmh</c>, <c>speed_source</c> or
/// <c>bridge_source</c> that doesn't agree with its value, lanes below 1, a width or speed not above 0, a negative
/// length, and non-finite numbers.
/// </summary>
public static class RoadGraphReader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly string[] RootFields = ["schema_version", "map_id", "area", "nodes", "edges"];

    private static readonly string[] NodeFields = ["id", "kind", "position", "osm_node"];

    private static readonly string[] EdgeFields =
    [
        "id", "from", "to", "class",
        "drivable", "drivable_source", "walkable", "walkable_source",
        "lanes", "lanes_source", "speed_kmh", "speed_source", "width_m", "width_source",
        "oneway", "bridge", "bridge_source", "tunnel", "layer", "length_m", "osm_way",
        "name", "name_sv", "ref", "points",
    ];

    private static readonly Dictionary<string, RoadNodeKind> NodeKinds = new(StringComparer.Ordinal)
    {
        ["junction"] = RoadNodeKind.Junction,
        ["joint"] = RoadNodeKind.Joint,
        ["dead_end"] = RoadNodeKind.DeadEnd,
        ["area_edge"] = RoadNodeKind.AreaEdge,
        ["span_end"] = RoadNodeKind.SpanEnd,
    };

    /// <param name="json">The file's contents.</param>
    /// <param name="source">Name used in error messages, usually the file path.</param>
    public static RoadGraph Parse(string json, string source = MapPack.RoadGraphFile)
    {
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
        if (!MapPack.IsValidMapId(mapId))
        {
            throw PackJson.Error(source, "map_id", $"'{mapId}' is not a lowercase slug");
        }

        var area = PackJson.String(root, "area", source, "");
        if (area is not ("full" or "test"))
        {
            throw PackJson.Error(source, "area", $"must be 'full' or 'test', not '{area}'");
        }

        var nodes = new List<RoadNode>();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in PackJson.Array(root, "nodes", source, "").EnumerateArray())
        {
            var path = $"nodes[{index}]";
            RequireObject(element, source, path);
            var node = ParseNode(element, source, path);
            if (!nodeIds.Add(node.Id))
            {
                throw PackJson.Error(source, $"{path}.id", $"node id '{node.Id}' appears more than once");
            }

            nodes.Add(node);
            index++;
        }

        var edges = new List<RoadEdge>();
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        index = 0;
        foreach (var element in PackJson.Array(root, "edges", source, "").EnumerateArray())
        {
            var path = $"edges[{index}]";
            RequireObject(element, source, path);
            var edge = ParseEdge(element, source, path);
            if (!edgeIds.Add(edge.Id))
            {
                throw PackJson.Error(source, $"{path}.id", $"edge id '{edge.Id}' appears more than once");
            }

            if (!nodeIds.Contains(edge.From))
            {
                throw PackJson.Error(source, $"{path}.from", $"references unknown node '{edge.From}' (edge '{edge.Id}')");
            }

            if (!nodeIds.Contains(edge.To))
            {
                throw PackJson.Error(source, $"{path}.to", $"references unknown node '{edge.To}' (edge '{edge.Id}')");
            }

            edges.Add(edge);
            index++;
        }

        return new RoadGraph(version, mapId, area, nodes, edges);
    }

    private static RoadNode ParseNode(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, NodeFields);
        var id = NonEmptyString(element, "id", source, path);
        var kindName = PackJson.String(element, "kind", source, path);
        if (!NodeKinds.TryGetValue(kindName, out var kind))
        {
            throw PackJson.Error(
                source,
                $"{path}.kind",
                $"'{kindName}' is not a known node kind (known: {string.Join(", ", NodeKinds.Keys)})");
        }

        var position = Point(PackJson.Numbers(element, "position", 3, source, path));
        var osmNode = PackJson.NullableLong(element, "osm_node", source, path);
        return new RoadNode(id, kind, position, osmNode);
    }

    private static RoadEdge ParseEdge(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, EdgeFields);
        var id = NonEmptyString(element, "id", source, path);
        var from = NonEmptyString(element, "from", source, path);
        var to = NonEmptyString(element, "to", source, path);
        var roadClass = NonEmptyString(element, "class", source, path);

        var drivable = PackJson.Bool(element, "drivable", source, path);
        var drivableSource = ValueSource(element, "drivable_source", source, path);
        var walkable = PackJson.Bool(element, "walkable", source, path);
        var walkableSource = ValueSource(element, "walkable_source", source, path);
        if (!drivable && !walkable)
        {
            throw PackJson.Error(source, path, $"edge '{id}' is neither drivable nor walkable");
        }

        var lanes = PackJson.Int(element, "lanes", source, path);
        if (lanes < 1)
        {
            throw PackJson.Error(source, $"{path}.lanes", $"must be at least 1, not {lanes}");
        }

        var lanesSource = ValueSource(element, "lanes_source", source, path);

        int? speed = null;
        if (PackJson.RequiredNullable(element, "speed_kmh", source, path, out _))
        {
            speed = PackJson.Int(element, "speed_kmh", source, path);
            if (speed <= 0)
            {
                throw PackJson.Error(source, $"{path}.speed_kmh", $"must be greater than 0, not {speed}");
            }
        }

        if (drivable && speed is null)
        {
            throw PackJson.Error(source, $"{path}.speed_kmh", $"is null, but edge '{id}' is drivable and needs a speed");
        }

        RoadValueSource? speedSource = PackJson.RequiredNullable(element, "speed_source", source, path, out _)
            ? ValueSource(element, "speed_source", source, path)
            : null;
        if ((speed is null) != (speedSource is null))
        {
            throw PackJson.Error(source, $"{path}.speed_source", "must be null exactly when speed_kmh is null");
        }

        var width = PackJson.Number(element, "width_m", source, path);
        if (width <= 0)
        {
            throw PackJson.Error(source, $"{path}.width_m", $"must be greater than 0, not {width}");
        }

        var widthSource = ValueSource(element, "width_source", source, path);
        var oneway = PackJson.Bool(element, "oneway", source, path);
        var bridge = PackJson.Bool(element, "bridge", source, path);
        var bridgeSource = BridgeSource(element, source, path);
        if (bridge != (bridgeSource is not null))
        {
            throw PackJson.Error(source, $"{path}.bridge_source", "must be null exactly when bridge is false");
        }

        var tunnel = PackJson.Bool(element, "tunnel", source, path);
        var layer = PackJson.Int(element, "layer", source, path);
        var length = PackJson.Number(element, "length_m", source, path);
        if (length < 0)
        {
            throw PackJson.Error(source, $"{path}.length_m", $"must not be negative, not {length}");
        }

        var osmWay = PackJson.Long(element, "osm_way", source, path);
        var names = new FeatureNames(
            PackJson.NullableString(element, "name", source, path),
            PackJson.NullableString(element, "name_sv", source, path),
            PackJson.NullableString(element, "ref", source, path));
        var points = Points(element, source, path);

        return new RoadEdge(
            id, from, to, roadClass,
            drivable, drivableSource, walkable, walkableSource,
            lanes, lanesSource, speed, speedSource, width, widthSource,
            oneway, bridge, bridgeSource, tunnel, layer, length, osmWay, names, points);
    }

    private static List<LocalPoint> Points(JsonElement element, string source, string path)
    {
        var array = PackJson.Array(element, "points", source, path);
        var fieldPath = $"{path}.points";
        if (array.GetArrayLength() < 2)
        {
            throw PackJson.Error(source, fieldPath, $"must have at least 2 points, has {array.GetArrayLength()}");
        }

        var points = new List<LocalPoint>(array.GetArrayLength());
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            var pointPath = $"{fieldPath}[{i}]";
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3)
            {
                throw PackJson.Error(source, pointPath, "must be [x, y, z]");
            }

            var values = new double[3];
            var j = 0;
            foreach (var value in item.EnumerateArray())
            {
                values[j] = PackJson.Number(value, source, $"{pointPath}[{j}]");
                j++;
            }

            points.Add(Point(values));
            i++;
        }

        return points;
    }

    private static RoadValueSource ValueSource(JsonElement element, string name, string source, string path)
    {
        var value = PackJson.String(element, name, source, path);
        return value switch
        {
            "osm" => RoadValueSource.Osm,
            "default" => RoadValueSource.Default,
            _ => throw PackJson.Error(source, PackJson.Join(path, name), $"must be 'osm' or 'default', not '{value}'"),
        };
    }

    private static RoadBridgeSource? BridgeSource(JsonElement element, string source, string path)
    {
        var value = PackJson.NullableString(element, "bridge_source", source, path);
        return value switch
        {
            null => null,
            "osm" => RoadBridgeSource.Osm,
            "inferred" => RoadBridgeSource.Inferred,
            "inferred_underpass" => RoadBridgeSource.InferredUnderpass,
            _ => throw PackJson.Error(
                source,
                $"{path}.bridge_source",
                $"must be 'osm', 'inferred', 'inferred_underpass' or null, not '{value}'"),
        };
    }

    private static string NonEmptyString(JsonElement element, string name, string source, string path)
    {
        var value = PackJson.String(element, name, source, path);
        if (value.Length == 0)
        {
            throw PackJson.Error(source, PackJson.Join(path, name), "must not be empty");
        }

        return value;
    }

    private static void RequireObject(JsonElement element, string source, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw PackJson.Error(source, path, "must be an object");
        }
    }

    private static LocalPoint Point(double[] values) => new(values[0], values[1], values[2]);
}
