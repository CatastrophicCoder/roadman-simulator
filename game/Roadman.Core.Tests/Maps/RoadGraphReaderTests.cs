using System.Text.Json.Nodes;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class RoadGraphReaderTests
{
    // A: junction, B: joint, C: dead end, D: area edge.
    // e1 A-B primary two-way (drivable only), e2 A->C residential one-way (drivable and walkable),
    // e3 D-A cycleway (walkable only, a tunnel), e4 B->C service one-way (drivable, on an OSM bridge).
    internal const string ValidGraph = """
        {
          "schema_version": 1,
          "map_id": "somero",
          "area": "test",
          "nodes": [
            {"id": "osm:node/1", "kind": "junction", "position": [0, 100, 0], "osm_node": 1},
            {"id": "osm:node/2", "kind": "joint", "position": [100, 100, 0], "osm_node": 2},
            {"id": "osm:node/3", "kind": "dead_end", "position": [0, 100, -100], "osm_node": 3},
            {"id": "cut:way/40:0", "kind": "area_edge", "position": [-50, 100, 50], "osm_node": null}
          ],
          "edges": [
            {
              "id": "osm:way/10#0", "from": "osm:node/1", "to": "osm:node/2", "class": "primary",
              "drivable": true, "drivable_source": "default", "walkable": false, "walkable_source": "default",
              "lanes": 2, "lanes_source": "osm", "speed_kmh": 40, "speed_source": "osm",
              "width_m": 8.0, "width_source": "default", "oneway": false,
              "bridge": false, "bridge_source": null, "tunnel": false, "layer": 0,
              "length_m": 100.0, "osm_way": 10, "name": "Turuntie", "name_sv": "Åbovägen", "ref": "52",
              "points": [[0, 100, 0], [50, 100, 0], [100, 100, 0]]
            },
            {
              "id": "osm:way/20#0", "from": "osm:node/1", "to": "osm:node/3", "class": "residential",
              "drivable": true, "drivable_source": "default", "walkable": true, "walkable_source": "default",
              "lanes": 1, "lanes_source": "default", "speed_kmh": 30, "speed_source": "default",
              "width_m": 5.5, "width_source": "default", "oneway": true,
              "bridge": false, "bridge_source": null, "tunnel": false, "layer": 0,
              "length_m": 100.0, "osm_way": 20, "name": null, "name_sv": null, "ref": null,
              "points": [[0, 100, 0], [0, 100, -100]]
            },
            {
              "id": "osm:way/40#0", "from": "cut:way/40:0", "to": "osm:node/1", "class": "cycleway",
              "drivable": false, "drivable_source": "default", "walkable": true, "walkable_source": "osm",
              "lanes": 1, "lanes_source": "default", "speed_kmh": null, "speed_source": null,
              "width_m": 3, "width_source": "osm", "oneway": false,
              "bridge": false, "bridge_source": null, "tunnel": true, "layer": -1,
              "length_m": 70.71, "osm_way": 40, "name": "Härkälän pyörätie", "name_sv": null, "ref": null,
              "points": [[-50, 100, 50], [0, 100, 0]]
            },
            {
              "id": "osm:way/30#0", "from": "osm:node/2", "to": "osm:node/3", "class": "service",
              "drivable": true, "drivable_source": "osm", "walkable": false, "walkable_source": "osm",
              "lanes": 1, "lanes_source": "default", "speed_kmh": 20, "speed_source": "default",
              "width_m": 4.0, "width_source": "default", "oneway": true,
              "bridge": true, "bridge_source": "osm", "tunnel": false, "layer": 1,
              "length_m": 141.42, "osm_way": 9007199254740993, "name": null, "name_sv": null, "ref": null,
              "points": [[100, 100, 0], [0, 100, -100]]
            }
          ]
        }
        """;

    /// <summary>What the pipeline writes for a map without roads.</summary>
    private const string EmptyGraph =
        """{"schema_version": 1, "map_id": "somero", "area": "full", "nodes": [], "edges": []}""";

    private const string A = "osm:node/1";
    private const string B = "osm:node/2";
    private const string C = "osm:node/3";
    private const string D = "cut:way/40:0";

    [Fact]
    public void Parse_ValidFile_ReadsNodesAndEdges()
    {
        var graph = RoadGraphReader.Parse(ValidGraph);

        Assert.Equal(1, graph.SchemaVersion);
        Assert.Equal("somero", graph.MapId);
        Assert.Equal("test", graph.Area);
        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(4, graph.Edges.Count);
        Assert.Equal([A, B, C, D], graph.Nodes.Select(node => node.Id));

        Assert.Equal(new RoadNode(D, RoadNodeKind.AreaEdge, new LocalPoint(-50, 100, 50), null), graph.Node(D));
        Assert.Equal(RoadNodeKind.Junction, graph.Node(A).Kind);
        Assert.Equal(RoadNodeKind.Joint, graph.Node(B).Kind);
        Assert.Equal(RoadNodeKind.DeadEnd, graph.Node(C).Kind);
        Assert.Equal(1L, graph.Node(A).OsmNode);

        var primary = graph.Edge("osm:way/10#0");
        Assert.Equal(A, primary.From);
        Assert.Equal(B, primary.To);
        Assert.Equal("primary", primary.Class);
        Assert.True(primary.Drivable);
        Assert.False(primary.Walkable);
        Assert.Equal(RoadValueSource.Default, primary.DrivableSource);
        Assert.Equal(2, primary.Lanes);
        Assert.Equal(RoadValueSource.Osm, primary.LanesSource);
        Assert.Equal(40, primary.SpeedKmh);
        Assert.Equal(RoadValueSource.Osm, primary.SpeedSource);
        Assert.Equal(8.0, primary.WidthM);
        Assert.False(primary.Oneway);
        Assert.False(primary.Bridge);
        Assert.Null(primary.BridgeSource);
        Assert.Equal(10L, primary.OsmWay);
        Assert.Equal(new FeatureNames("Turuntie", "Åbovägen", "52"), primary.Names);
        Assert.Equal(
            [new LocalPoint(0, 100, 0), new LocalPoint(50, 100, 0), new LocalPoint(100, 100, 0)],
            primary.Points);

        var cycleway = graph.Edge("osm:way/40#0");
        Assert.Null(cycleway.SpeedKmh);
        Assert.Null(cycleway.SpeedSource);
        Assert.True(cycleway.Tunnel);
        Assert.Equal(-1, cycleway.Layer);
        Assert.Equal(3.0, cycleway.WidthM);
        Assert.Equal(RoadValueSource.Osm, cycleway.WalkableSource);
        Assert.Equal(new FeatureNames("Härkälän pyörätie", null, null), cycleway.Names);

        var bridge = graph.Edge("osm:way/30#0");
        Assert.True(bridge.Bridge);
        Assert.Equal(RoadBridgeSource.Osm, bridge.BridgeSource);
        Assert.Equal(1, bridge.Layer);
        Assert.Equal(9007199254740993L, bridge.OsmWay);

        Assert.Equal(FeatureNames.None, graph.Edge("osm:way/20#0").Names);
    }

    [Fact]
    public void Parse_InferredBridge_IsRead()
    {
        var json = ModifyEdge(3, edge => edge["bridge_source"] = "inferred");

        Assert.Equal(RoadBridgeSource.Inferred, RoadGraphReader.Parse(json).Edge("osm:way/30#0").BridgeSource);
    }

    [Fact]
    public void Parse_EmptyGraph_IsValid()
    {
        var graph = RoadGraphReader.Parse(EmptyGraph);

        Assert.Equal(1, graph.SchemaVersion);
        Assert.Equal("somero", graph.MapId);
        Assert.Equal("full", graph.Area);
        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Theory]
    [InlineData(RoadNodeFilter.Any)]
    [InlineData(RoadNodeFilter.Drivable)]
    [InlineData(RoadNodeFilter.Walkable)]
    public void NearestNode_OnEmptyGraph_IsNull(RoadNodeFilter filter)
    {
        var graph = RoadGraphReader.Parse(EmptyGraph);

        Assert.Null(graph.NearestNode(new LocalPoint(0, 0, 0), filter));
    }

    [Fact]
    public void Lookups_OnEmptyGraph_FindNothing()
    {
        var graph = RoadGraphReader.Parse(EmptyGraph);

        Assert.False(graph.TryGetNode(A, out _));
        Assert.False(graph.TryGetEdge("osm:way/10#0", out _));
        Assert.Throws<KeyNotFoundException>(() => graph.Node(A));
        Assert.Throws<KeyNotFoundException>(() => graph.Edge("osm:way/10#0"));
        Assert.Throws<KeyNotFoundException>(() => graph.EdgesAt(A));
        Assert.Throws<KeyNotFoundException>(() => graph.DrivableEdgesFrom(A));
        Assert.Throws<KeyNotFoundException>(() => graph.WalkableEdgesAt(A));
        Assert.Throws<KeyNotFoundException>(() => graph.Matches(A, RoadNodeFilter.Any));
    }

    [Fact]
    public void Constructor_AcceptsEmptyGraph()
    {
        var graph = new RoadGraph(1, "somero", "test", [], []);

        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
        Assert.Null(graph.NearestNode(new LocalPoint(0, 0, 0)));
    }

    [Theory]
    [InlineData("nodes", "edges")]
    [InlineData("edges", "nodes")]
    public void Parse_EmptyGraph_StillNeedsBothArrays(string removed, string kept)
    {
        var root = JsonNode.Parse(EmptyGraph)!.AsObject();
        root.Remove(removed);
        Assert.True(root.ContainsKey(kept));

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(root.ToJsonString()));
        Assert.Contains($"'{removed}' is missing", ex.Message);
    }

    [Fact]
    public void Parse_OtherSchemaVersion_IsRejected()
    {
        var json = Modify(root => root["schema_version"] = 2);

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json, "res://maps/somero/road_graph.json"));
        Assert.Contains("res://maps/somero/road_graph.json", ex.Message);
        Assert.Contains("schema_version 2 is not supported", ex.Message);
    }

    [Theory]
    [InlineData("map_id")]
    [InlineData("area")]
    [InlineData("nodes")]
    [InlineData("edges")]
    [InlineData("schema_version")]
    public void Parse_MissingTopLevelField_IsRejected(string field)
    {
        var json = Modify(root => root.AsObject().Remove(field));

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains($"'{field}' is missing", ex.Message);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("kind")]
    [InlineData("position")]
    [InlineData("osm_node")]
    public void Parse_MissingNodeField_IsRejected(string field)
    {
        var json = Modify(root => root["nodes"]![3]!.AsObject().Remove(field));

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains($"'nodes[3].{field}' is missing", ex.Message);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("from")]
    [InlineData("to")]
    [InlineData("class")]
    [InlineData("drivable")]
    [InlineData("drivable_source")]
    [InlineData("walkable")]
    [InlineData("walkable_source")]
    [InlineData("lanes")]
    [InlineData("lanes_source")]
    [InlineData("speed_kmh")]
    [InlineData("speed_source")]
    [InlineData("width_m")]
    [InlineData("width_source")]
    [InlineData("oneway")]
    [InlineData("bridge")]
    [InlineData("bridge_source")]
    [InlineData("tunnel")]
    [InlineData("layer")]
    [InlineData("length_m")]
    [InlineData("osm_way")]
    [InlineData("name")]
    [InlineData("name_sv")]
    [InlineData("ref")]
    [InlineData("points")]
    public void Parse_MissingEdgeField_IsRejected(string field)
    {
        // Edge 2 (the cycleway) has null speed, speed source, bridge source and names, so this also checks that a
        // nullable key must still be present.
        var json = ModifyEdge(2, edge => edge.AsObject().Remove(field));

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains($"'edges[2].{field}' is missing", ex.Message);
    }

    [Fact]
    public void Parse_UnknownField_IsRejected()
    {
        var json = ModifyEdge(0, edge => edge["surface"] = "asphalt");

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains("'edges[0].surface' is not a known field", ex.Message);
    }

    [Fact]
    public void Parse_UnknownNodeKind_IsRejected()
    {
        var json = Modify(root => root["nodes"]![1]!["kind"] = "roundabout");

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains("'nodes[1].kind' 'roundabout' is not a known node kind", ex.Message);
    }

    [Fact]
    public void Parse_DuplicateNodeId_IsRejected()
    {
        var json = Modify(root => root["nodes"]![2]!["id"] = A);

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains($"node id '{A}' appears more than once", ex.Message);
    }

    [Fact]
    public void Parse_DuplicateEdgeId_IsRejected()
    {
        var json = ModifyEdge(1, edge => edge["id"] = "osm:way/10#0");

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains("edge id 'osm:way/10#0' appears more than once", ex.Message);
    }

    [Theory]
    [InlineData("from")]
    [InlineData("to")]
    public void Parse_EdgeWithUnknownNode_IsRejected(string end)
    {
        var json = ModifyEdge(1, edge => edge[end] = "osm:node/999");

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains($"'edges[1].{end}' references unknown node 'osm:node/999'", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Parse_EdgeWithFewerThanTwoPoints_IsRejected(int count)
    {
        var json = ModifyEdge(0, edge =>
        {
            var points = new JsonArray();
            for (var i = 0; i < count; i++)
            {
                points.Add(new JsonArray(0, 100, 0));
            }

            edge["points"] = points;
        });

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains($"'edges[0].points' must have at least 2 points, has {count}", ex.Message);
    }

    [Fact]
    public void Parse_PointWithoutThreeCoordinates_IsRejected()
    {
        var json = ModifyEdge(0, edge => edge["points"]![1] = new JsonArray(50, 100));

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains("'edges[0].points[1]' must be [x, y, z]", ex.Message);
    }

    [Fact]
    public void Parse_EdgeNeitherDrivableNorWalkable_IsRejected()
    {
        var json = ModifyEdge(2, edge => edge["walkable"] = false);

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains("edge 'osm:way/40#0' is neither drivable nor walkable", ex.Message);
    }

    [Fact]
    public void Parse_DrivableEdgeWithoutSpeed_IsRejected()
    {
        var json = ModifyEdge(0, edge =>
        {
            edge["speed_kmh"] = null;
            edge["speed_source"] = null;
        });

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains("'edges[0].speed_kmh' is null, but edge 'osm:way/10#0' is drivable", ex.Message);
    }

    [Fact]
    public void Parse_SpeedSourceWithoutSpeed_IsRejected()
    {
        var json = ModifyEdge(2, edge => edge["speed_source"] = "default");

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains("'edges[2].speed_source' must be null exactly when speed_kmh is null", ex.Message);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "osm")]
    public void Parse_BridgeSourceDisagreeingWithBridge_IsRejected(bool bridge, string? bridgeSource)
    {
        var json = ModifyEdge(0, edge =>
        {
            edge["bridge"] = bridge;
            edge["bridge_source"] = bridgeSource;
        });

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains("'edges[0].bridge_source' must be null exactly when bridge is false", ex.Message);
    }

    [Theory]
    [InlineData("drivable_source", "guess", "must be 'osm' or 'default'")]
    [InlineData("bridge_source", "guess", "must be 'osm', 'inferred' or null")]
    [InlineData("lanes", 0, "must be at least 1")]
    [InlineData("speed_kmh", 0, "must be greater than 0")]
    [InlineData("width_m", 0, "must be greater than 0")]
    [InlineData("length_m", -1, "must not be negative")]
    [InlineData("osm_way", 1.5, "must be a whole number")]
    [InlineData("name", 5, "must be a string or null")]
    [InlineData("oneway", "yes", "must be true or false")]
    public void Parse_BadEdgeValue_NamesTheField(string field, object value, string problem)
    {
        var json = ModifyEdge(0, edge => edge[field] = JsonValue.Create(value));

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains($"'edges[0].{field}' {problem}", ex.Message);
    }

    [Theory]
    [InlineData("[-50, 100, 50]", "[-50, 1e400, 50]", "nodes[3].position[1]")]
    [InlineData("\"width_m\": 3,", "\"width_m\": 1e400,", "edges[2].width_m")]
    [InlineData("\"length_m\": 100.0, \"osm_way\": 10", "\"length_m\": -1e400, \"osm_way\": 10", "edges[0].length_m")]
    [InlineData("[[0, 100, 0], [50, 100, 0]", "[[0, 100, 0], [50, 1e999, 0]", "edges[0].points[1][1]")]
    public void Parse_NonFiniteNumber_IsRejected(string original, string replacement, string field)
    {
        Assert.Contains(original, ValidGraph);
        var json = ValidGraph.Replace(original, replacement, StringComparison.Ordinal);

        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(json));
        Assert.Contains($"'{field}' must be a finite number", ex.Message);
    }

    [Fact]
    public void Parse_BadAreaAndMapId_AreRejected()
    {
        Assert.Contains(
            "'area' must be 'full' or 'test'",
            Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(Modify(root => root["area"] = "all"))).Message);
        Assert.Contains(
            "'map_id' 'Somero' is not a lowercase slug",
            Assert.Throws<MapPackException>(() => RoadGraphReader.Parse(Modify(root => root["map_id"] = "Somero"))).Message);
    }

    [Fact]
    public void Parse_InvalidJson_IsAMapPackError()
    {
        var ex = Assert.Throws<MapPackException>(() => RoadGraphReader.Parse("{", "road_graph.json"));
        Assert.Contains("road_graph.json: not valid JSON", ex.Message);
    }

    [Fact]
    public void DrivableEdgesFrom_FollowsOnewayAndUsesTwoWayEdgesFromBothEnds()
    {
        var graph = RoadGraphReader.Parse(ValidGraph);

        Assert.Equal(
            [("osm:way/10#0", true), ("osm:way/20#0", true)],
            graph.DrivableEdgesFrom(A).Select(step => (step.Edge.Id, step.Forward)));

        var fromB = graph.DrivableEdgesFrom(B);
        Assert.Equal(
            [("osm:way/10#0", false), ("osm:way/30#0", true)],
            fromB.Select(step => (step.Edge.Id, step.Forward)));
        Assert.Equal(B, fromB[0].StartNode);
        Assert.Equal(A, fromB[0].EndNode);

        // Both edges into C are one-way towards it; the cycleway at D isn't drivable.
        Assert.Empty(graph.DrivableEdgesFrom(C));
        Assert.Empty(graph.DrivableEdgesFrom(D));
    }

    [Fact]
    public void WalkableEdgesAt_IgnoresOnewayAndDrivableOnlyEdges()
    {
        var graph = RoadGraphReader.Parse(ValidGraph);

        Assert.Equal(
            [("osm:way/20#0", true), ("osm:way/40#0", false)],
            graph.WalkableEdgesAt(A).Select(step => (step.Edge.Id, step.Forward)));

        // The one-way residential edge can be walked against its direction.
        var atC = Assert.Single(graph.WalkableEdgesAt(C));
        Assert.Equal("osm:way/20#0", atC.Edge.Id);
        Assert.Equal(A, atC.EndNode);

        Assert.Empty(graph.WalkableEdgesAt(B));
    }

    [Fact]
    public void Lookups_WithUnknownNode_Throw()
    {
        var graph = RoadGraphReader.Parse(ValidGraph);

        Assert.Throws<KeyNotFoundException>(() => graph.DrivableEdgesFrom("osm:node/999"));
        Assert.Throws<KeyNotFoundException>(() => graph.WalkableEdgesAt("osm:node/999"));
        Assert.Throws<KeyNotFoundException>(() => graph.Node("osm:node/999"));
        Assert.False(graph.TryGetEdge("osm:way/999#0", out _));
    }

    [Fact]
    public void EdgesAt_ListsEveryEdgeEnd()
    {
        var graph = RoadGraphReader.Parse(ValidGraph);

        Assert.Equal(["osm:way/10#0", "osm:way/20#0", "osm:way/40#0"], graph.EdgesAt(A).Select(edge => edge.Id));
        Assert.Equal(["osm:way/40#0"], graph.EdgesAt(D).Select(edge => edge.Id));
    }

    [Fact]
    public void NearestNode_RespectsTheFilter()
    {
        var graph = RoadGraphReader.Parse(ValidGraph);
        var nearD = new LocalPoint(-45, 100, 45);

        Assert.Equal(D, graph.NearestNode(nearD)!.Id);
        Assert.Equal(D, graph.NearestNode(nearD, RoadNodeFilter.Walkable)!.Id);
        Assert.Equal(A, graph.NearestNode(nearD, RoadNodeFilter.Drivable)!.Id);

        // B has only drivable edges.
        var nearB = new LocalPoint(95, 100, 0);
        Assert.Equal(B, graph.NearestNode(nearB, RoadNodeFilter.Drivable)!.Id);
        Assert.Equal(A, graph.NearestNode(nearB, RoadNodeFilter.Walkable)!.Id);
    }

    [Fact]
    public void NearestNode_UsesHeight()
    {
        // Two nodes at the same x/z, one 6 m higher (a deck above a road): the height decides.
        var graph = new RoadGraph(
            1,
            "somero",
            "test",
            [
                new RoadNode("low", RoadNodeKind.DeadEnd, new LocalPoint(0, 100, 0), 1),
                new RoadNode("high", RoadNodeKind.DeadEnd, new LocalPoint(0, 106, 0), 2),
            ],
            []);

        Assert.Equal("high", graph.NearestNode(new LocalPoint(0, 105, 0))!.Id);
        Assert.Equal("low", graph.NearestNode(new LocalPoint(0, 101, 0))!.Id);
        Assert.Null(graph.NearestNode(new LocalPoint(0, 101, 0), RoadNodeFilter.Drivable));
    }

    [Fact]
    public void NearestNode_TieGoesToTheEarlierNode()
    {
        var graph = RoadGraphReader.Parse(ValidGraph);

        // A and B are equally far from (50, 100, 0).
        Assert.Equal(A, graph.NearestNode(new LocalPoint(50, 100, 0))!.Id);
    }

    [Fact]
    public void CanDriveFrom_FollowsDirection()
    {
        var graph = RoadGraphReader.Parse(ValidGraph);
        var oneway = graph.Edge("osm:way/20#0");
        var twoWay = graph.Edge("osm:way/10#0");
        var path = graph.Edge("osm:way/40#0");

        Assert.True(oneway.CanDriveFrom(A));
        Assert.False(oneway.CanDriveFrom(C));
        Assert.True(twoWay.CanDriveFrom(A));
        Assert.True(twoWay.CanDriveFrom(B));
        Assert.False(path.CanDriveFrom(D));
        Assert.Equal(B, twoWay.OtherEnd(A));
        Assert.Equal(A, twoWay.OtherEnd(B));
    }

    [Fact]
    public void Constructor_RejectsDanglingEdge()
    {
        var edge = RoadGraphReader.Parse(ValidGraph).Edge("osm:way/10#0");

        Assert.Throws<ArgumentException>(
            () => new RoadGraph(1, "somero", "test", [new RoadNode(A, RoadNodeKind.DeadEnd, default, null)], [edge]));
    }

    private static string Modify(Action<JsonNode> change)
    {
        var root = JsonNode.Parse(ValidGraph)!;
        change(root);
        return root.ToJsonString();
    }

    private static string ModifyEdge(int index, Action<JsonNode> change) => Modify(root => change(root["edges"]![index]!));
}
