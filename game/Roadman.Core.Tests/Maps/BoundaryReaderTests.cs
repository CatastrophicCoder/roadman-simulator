using System.Text.Json.Nodes;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

/// <summary>
/// <c>boundary.json</c> (ADR 0015) read by <see cref="BoundaryReader"/>: <c>docs/specs/player-on-foot.md</c>,
/// acceptance criterion 1.
/// </summary>
public class BoundaryReaderTests
{
    private const string Source = "res://maps/somero/boundary.json";

    // A 100 m square boundary (not closed), one roadblock with both node roles, a land and a water fence, one void
    // prism, a spawn area whose open ground is one polygon (a 20 m square with a 4 m hole) and one water crossing.
    internal const string ValidBoundary = """
        {
          "schema_version": 1,
          "map_id": "somero",
          "area": "test",
          "boundary": {
            "ring": [[0, 0], [100, 0], [100, 100], [0, 100]],
            "length_m": 400,
            "source": "test_area"
          },
          "roadblocks": [
            {
              "id": "roadblock:cut:way/10:0",
              "nodes": [
                {"node": "cut:way/10:0", "edge": "osm:way/10#0", "role": "spawn_despawn"},
                {"node": "cut:way/11:0", "edge": "osm:way/11#0", "role": "turn_around"}
              ],
              "class": "tertiary",
              "drivable": true,
              "walkable": true,
              "width_m": 7.5,
              "width_source": "osm",
              "start": [40, 101.5, 0],
              "end": [47.5, 101.25, 0],
              "position": [43.75, 101.375, 0],
              "yaw_deg": 180,
              "outward": [0, 0, -1],
              "s_start": 40,
              "s_end": 47.5,
              "over_water": false
            }
          ],
          "fences": [
            {
              "id": "fence:0",
              "type": "land",
              "start": [0, 100, 0],
              "end": [40, 101.5, 0],
              "s_start": 0,
              "s_end": 40,
              "outward": [0, 0, -1]
            },
            {
              "id": "fence:1",
              "type": "water",
              "start": [100, 99, 0],
              "end": [100, 99, 30],
              "s_start": 100,
              "s_end": 130,
              "outward": [1, 0, 0],
              "water_ids": ["mml:jarvi/1"]
            }
          ],
          "cliff": {"ledge_m": 2.0, "bottom_y": 51.75},
          "void_trigger": {
            "bottom_y": 31.75,
            "top_y": 160.5,
            "prisms": [
              {"footprint": [[-2, -2], [102, -2], [102, -50], [-2, -50]]}
            ]
          },
          "spawn_area": {
            "polygon": [[10, 10], [40, 10], [40, 40], [10, 40]],
            "open_ground": [
              [
                [[15, 15], [35, 15], [35, 35], [15, 35], [15, 15]],
                [[23, 23], [27, 23], [27, 27], [23, 27], [23, 23]]
              ]
            ],
            "open_area_m2": 384
          },
          "water_crossings": [
            {"s_start": 100, "s_end": 130, "source": "mml_topographic", "water_ids": ["mml:jarvi/1"]}
          ]
        }
        """;

    private static readonly MapManifest TestManifest = Manifest("somero", "test");

    [Fact]
    public void Parse_ValidFile_ReadsEveryField()
    {
        var boundary = BoundaryReader.Parse(ValidBoundary, TestManifest, Source);

        Assert.Equal(1, boundary.SchemaVersion);
        Assert.Equal("somero", boundary.MapId);
        Assert.Equal("test", boundary.Area);

        Assert.Equal(
            [new PlanarPoint(0, 0), new PlanarPoint(100, 0), new PlanarPoint(100, 100), new PlanarPoint(0, 100)],
            boundary.Ring.Points);
        Assert.Equal(400, boundary.Ring.LengthM);
        Assert.Equal(BoundarySource.TestArea, boundary.Ring.Source);

        var roadblock = Assert.Single(boundary.Roadblocks);
        Assert.Equal("roadblock:cut:way/10:0", roadblock.Id);
        Assert.Equal(
            [
                new RoadblockNode("cut:way/10:0", "osm:way/10#0", RoadblockRole.SpawnDespawn),
                new RoadblockNode("cut:way/11:0", "osm:way/11#0", RoadblockRole.TurnAround),
            ],
            roadblock.Nodes);
        Assert.Equal("tertiary", roadblock.Class);
        Assert.True(roadblock.Drivable);
        Assert.True(roadblock.Walkable);
        Assert.Equal(7.5, roadblock.WidthM);
        Assert.Equal(RoadValueSource.Osm, roadblock.WidthSource);
        Assert.Equal(new LocalPoint(40, 101.5, 0), roadblock.Start);
        Assert.Equal(new LocalPoint(47.5, 101.25, 0), roadblock.End);
        Assert.Equal(new LocalPoint(43.75, 101.375, 0), roadblock.Position);
        Assert.Equal(180, roadblock.YawDeg);
        Assert.Equal(new LocalPoint(0, 0, -1), roadblock.Outward);
        Assert.Equal(40, roadblock.SStart);
        Assert.Equal(47.5, roadblock.SEnd);
        Assert.False(roadblock.OverWater);

        Assert.Equal(2, boundary.Fences.Count);
        var land = boundary.Fences[0];
        Assert.Equal("fence:0", land.Id);
        Assert.Equal(FenceType.Land, land.Type);
        Assert.Equal(new LocalPoint(0, 100, 0), land.Start);
        Assert.Equal(new LocalPoint(40, 101.5, 0), land.End);
        Assert.Equal(0, land.SStart);
        Assert.Equal(40, land.SEnd);
        Assert.Equal(new LocalPoint(0, 0, -1), land.Outward);
        Assert.Null(land.WaterIds);
        var water = boundary.Fences[1];
        Assert.Equal(FenceType.Water, water.Type);
        Assert.Equal(["mml:jarvi/1"], water.WaterIds!);
        Assert.Equal(new LocalPoint(1, 0, 0), water.Outward);

        Assert.Equal(new Cliff(2.0, 51.75), boundary.Cliff);

        Assert.Equal(31.75, boundary.VoidTrigger.BottomY);
        Assert.Equal(160.5, boundary.VoidTrigger.TopY);
        var prism = Assert.Single(boundary.VoidTrigger.Prisms);
        Assert.Equal(
            [new PlanarPoint(-2, -2), new PlanarPoint(102, -2), new PlanarPoint(102, -50), new PlanarPoint(-2, -50)],
            prism.Footprint);

        var spawn = boundary.SpawnArea;
        Assert.Equal(4, spawn.Polygon.Count);
        Assert.Equal(new PlanarPoint(40, 10), spawn.Polygon[1]);
        var open = Assert.Single(spawn.OpenGround);
        Assert.Equal(5, open.Exterior.Count);
        Assert.Equal(new PlanarPoint(35, 15), open.Exterior[1]);
        var hole = Assert.Single(open.Holes);
        Assert.Equal(new PlanarPoint(23, 23), hole[0]);
        Assert.Equal(400 - 16, open.Area, 9);
        Assert.Equal(384, spawn.OpenAreaM2);

        var crossing = Assert.Single(boundary.WaterCrossings);
        Assert.Equal(100, crossing.SStart);
        Assert.Equal(130, crossing.SEnd);
        Assert.Equal("mml_topographic", crossing.Source);
        Assert.Equal(["mml:jarvi/1"], crossing.WaterIds);
    }

    [Theory]
    [InlineData("area", BoundarySource.Area)]
    [InlineData("custom", BoundarySource.Custom)]
    [InlineData("test_area", BoundarySource.TestArea)]
    public void Parse_EveryBoundarySource_IsRead(string value, BoundarySource expected)
    {
        var json = Modify(root => root["boundary"]!["source"] = value);

        Assert.Equal(expected, BoundaryReader.Parse(json, TestManifest).Ring.Source);
    }

    [Fact]
    public void Parse_WaterCrossingSource_IsAFreeName()
    {
        // Deliberately not an enum: map files name their sources (the pipeline's model takes any non-empty string).
        var json = Modify(root => root["water_crossings"]![0]!["source"] = "osm");

        Assert.Equal("osm", Assert.Single(BoundaryReader.Parse(json, TestManifest).WaterCrossings).Source);
    }

    [Fact]
    public void Parse_OtherSchemaVersion_IsRejected()
    {
        var json = Modify(root => root["schema_version"] = 2);

        var ex = Assert.Throws<MapPackException>(() => BoundaryReader.Parse(json, TestManifest, Source));
        Assert.Contains(Source, ex.Message);
        Assert.Contains("schema_version 2 is not supported; this game reads version 1", ex.Message);
    }

    [Theory]
    [InlineData("", "schema_version")]
    [InlineData("", "map_id")]
    [InlineData("", "area")]
    [InlineData("", "boundary")]
    [InlineData("", "roadblocks")]
    [InlineData("", "fences")]
    [InlineData("", "cliff")]
    [InlineData("", "void_trigger")]
    [InlineData("", "spawn_area")]
    [InlineData("", "water_crossings")]
    [InlineData("boundary", "ring")]
    [InlineData("boundary", "length_m")]
    [InlineData("boundary", "source")]
    [InlineData("roadblocks[0]", "id")]
    [InlineData("roadblocks[0]", "nodes")]
    [InlineData("roadblocks[0]", "class")]
    [InlineData("roadblocks[0]", "drivable")]
    [InlineData("roadblocks[0]", "walkable")]
    [InlineData("roadblocks[0]", "width_m")]
    [InlineData("roadblocks[0]", "width_source")]
    [InlineData("roadblocks[0]", "start")]
    [InlineData("roadblocks[0]", "end")]
    [InlineData("roadblocks[0]", "position")]
    [InlineData("roadblocks[0]", "yaw_deg")]
    [InlineData("roadblocks[0]", "outward")]
    [InlineData("roadblocks[0]", "s_start")]
    [InlineData("roadblocks[0]", "s_end")]
    [InlineData("roadblocks[0]", "over_water")]
    [InlineData("roadblocks[0].nodes[1]", "node")]
    [InlineData("roadblocks[0].nodes[1]", "edge")]
    [InlineData("roadblocks[0].nodes[1]", "role")]
    [InlineData("fences[0]", "id")]
    [InlineData("fences[0]", "type")]
    [InlineData("fences[0]", "start")]
    [InlineData("fences[0]", "end")]
    [InlineData("fences[0]", "s_start")]
    [InlineData("fences[0]", "s_end")]
    [InlineData("fences[0]", "outward")]
    [InlineData("fences[1]", "water_ids")]
    [InlineData("cliff", "ledge_m")]
    [InlineData("cliff", "bottom_y")]
    [InlineData("void_trigger", "bottom_y")]
    [InlineData("void_trigger", "top_y")]
    [InlineData("void_trigger", "prisms")]
    [InlineData("void_trigger.prisms[0]", "footprint")]
    [InlineData("spawn_area", "polygon")]
    [InlineData("spawn_area", "open_ground")]
    [InlineData("spawn_area", "open_area_m2")]
    [InlineData("water_crossings[0]", "s_start")]
    [InlineData("water_crossings[0]", "s_end")]
    [InlineData("water_crossings[0]", "source")]
    [InlineData("water_crossings[0]", "water_ids")]
    public void Parse_MissingKey_IsRejected(string parent, string key)
    {
        var json = Modify(root => At(root, parent).AsObject().Remove(key));

        var ex = Assert.Throws<MapPackException>(() => BoundaryReader.Parse(json, TestManifest, Source));
        Assert.Contains(Source, ex.Message);
        Assert.Contains($"'{Join(parent, key)}' is missing", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("boundary")]
    [InlineData("roadblocks[0]")]
    [InlineData("roadblocks[0].nodes[0]")]
    [InlineData("fences[0]")]
    [InlineData("cliff")]
    [InlineData("void_trigger")]
    [InlineData("void_trigger.prisms[0]")]
    [InlineData("spawn_area")]
    [InlineData("water_crossings[0]")]
    public void Parse_UnknownKeyAtEachLevel_IsRejected(string parent)
    {
        var json = Modify(root => At(root, parent)["extra"] = 1);

        var ex = Assert.Throws<MapPackException>(() => BoundaryReader.Parse(json, TestManifest, Source));
        Assert.Contains(Source, ex.Message);
        Assert.Contains($"'{Join(parent, "extra")}' is not a known field", ex.Message);
    }

    [Theory]
    [InlineData("boundary", "source", "rectangle")]
    [InlineData("roadblocks[0].nodes[0]", "role", "despawn")]
    [InlineData("fences[0]", "type", "wall")]
    [InlineData("roadblocks[0]", "width_source", "guess")]
    public void Parse_UnknownEnumValue_IsRejected(string parent, string key, string value)
    {
        var json = Modify(root => At(root, parent)[key] = value);

        var ex = Assert.Throws<MapPackException>(() => BoundaryReader.Parse(json, TestManifest, Source));
        Assert.Contains($"'{Join(parent, key)}' '{value}' is not a known value", ex.Message);
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    public void Parse_NonFiniteNumber_IsRejected(string literal)
    {
        // JSON has no NaN or Infinity; an out-of-range literal is how a non-finite number reaches the reader.
        AssertNonFinite(root => root["cliff"]!["bottom_y"] = Sentinel, literal, "cliff.bottom_y");
        AssertNonFinite(root => root["boundary"]!["length_m"] = Sentinel, literal, "boundary.length_m");
        AssertNonFinite(root => root["boundary"]!["ring"]![1]![0] = Sentinel, literal, "boundary.ring[1][0]");
        AssertNonFinite(root => root["roadblocks"]![0]!["start"]![1] = Sentinel, literal, "roadblocks[0].start");
        AssertNonFinite(root => root["roadblocks"]![0]!["yaw_deg"] = Sentinel, literal, "roadblocks[0].yaw_deg");
        AssertNonFinite(root => root["void_trigger"]!["top_y"] = Sentinel, literal, "void_trigger.top_y");
        AssertNonFinite(
            root => root["spawn_area"]!["open_ground"]![0]![0]![2]![1] = Sentinel,
            literal,
            "spawn_area.open_ground[0][0][2][1]");
        AssertNonFinite(root => root["spawn_area"]!["open_area_m2"] = Sentinel, literal, "spawn_area.open_area_m2");
    }

    [Fact]
    public void Parse_MapIdNotTheManifests_IsRejected()
    {
        var ex = Assert.Throws<MapPackException>(
            () => BoundaryReader.Parse(ValidBoundary, Manifest("kirkkonummi", "test"), Source));
        Assert.Contains(Source, ex.Message);
        Assert.Contains("'map_id' is 'somero', but the manifest's is 'kirkkonummi'", ex.Message);
    }

    [Fact]
    public void Parse_AreaNotTheManifests_IsRejected()
    {
        var ex = Assert.Throws<MapPackException>(
            () => BoundaryReader.Parse(ValidBoundary, Manifest("somero", "full"), Source));
        Assert.Contains(Source, ex.Message);
        Assert.Contains("'area' is 'test', but the manifest's is 'full'", ex.Message);
    }

    [Fact]
    public void Parse_UnclosedOpenGroundRing_IsRejected()
    {
        // The exterior's last point no longer repeats the first.
        var exterior = Modify(root => root["spawn_area"]!["open_ground"]![0]![0]![4] = new JsonArray(15, 16));
        Assert.Contains(
            "'spawn_area.open_ground[0][0]' is not closed",
            Assert.Throws<MapPackException>(() => BoundaryReader.Parse(exterior, TestManifest)).Message);

        var hole = Modify(root => root["spawn_area"]!["open_ground"]![0]![1]![4] = new JsonArray(23, 24));
        Assert.Contains(
            "'spawn_area.open_ground[0][1]' is not closed",
            Assert.Throws<MapPackException>(() => BoundaryReader.Parse(hole, TestManifest)).Message);
    }

    [Fact]
    public void Parse_OpenGroundRingWithFewerThanFourPoints_IsRejected()
    {
        // Closed, but only 3 points: a ring needs at least 4 (3 corners plus the repeated first).
        var exterior = Modify(root =>
            root["spawn_area"]!["open_ground"]![0]![0] = JsonNode.Parse("[[15, 15], [35, 15], [15, 15]]"));
        Assert.Contains(
            "'spawn_area.open_ground[0][0]' must have at least 4 points, has 3",
            Assert.Throws<MapPackException>(() => BoundaryReader.Parse(exterior, TestManifest)).Message);

        var hole = Modify(root =>
            root["spawn_area"]!["open_ground"]![0]![1] = JsonNode.Parse("[[23, 23], [27, 23], [23, 23]]"));
        Assert.Contains(
            "'spawn_area.open_ground[0][1]' must have at least 4 points, has 3",
            Assert.Throws<MapPackException>(() => BoundaryReader.Parse(hole, TestManifest)).Message);
    }

    [Theory]
    [InlineData("boundary", "ring")]
    [InlineData("spawn_area", "polygon")]
    public void Parse_RingOrSpawnPolygonWithFewerThanThreePoints_IsRejected(string parent, string key)
    {
        var json = Modify(root => At(root, parent)[key] = JsonNode.Parse("[[0, 0], [100, 0]]"));

        var ex = Assert.Throws<MapPackException>(() => BoundaryReader.Parse(json, TestManifest, Source));
        Assert.Contains($"'{parent}.{key}' must have at least 3 points, has 2", ex.Message);
    }

    [Theory]
    [InlineData("[[0, 0], [1, 0], [1, 1]]", 3)]
    [InlineData("[[0, 0], [1, 0], [1, 1], [0, 1], [0, 0]]", 5)]
    public void Parse_PrismFootprintNotFourPoints_IsRejected(string footprint, int count)
    {
        var json = Modify(root => root["void_trigger"]!["prisms"]![0]!["footprint"] = JsonNode.Parse(footprint));

        var ex = Assert.Throws<MapPackException>(() => BoundaryReader.Parse(json, TestManifest, Source));
        Assert.Contains($"'void_trigger.prisms[0].footprint' must have exactly 4 points, has {count}", ex.Message);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-384.0)]
    public void Parse_OpenAreaNotAboveZero_IsRejected(double area)
    {
        var json = Modify(root => root["spawn_area"]!["open_area_m2"] = area);

        var ex = Assert.Throws<MapPackException>(() => BoundaryReader.Parse(json, TestManifest, Source));
        Assert.Contains("'spawn_area.open_area_m2' must be greater than 0", ex.Message);
    }

    [Theory]
    [InlineData(160.5)] // equal to top_y
    [InlineData(170.0)] // above top_y
    public void Parse_VoidBottomNotBelowTop_IsRejected(double bottom)
    {
        var json = Modify(root => root["void_trigger"]!["bottom_y"] = bottom);

        var ex = Assert.Throws<MapPackException>(() => BoundaryReader.Parse(json, TestManifest, Source));
        Assert.Contains("'void_trigger.bottom_y'", ex.Message);
        Assert.Contains("must be below top_y", ex.Message);
    }

    [Fact]
    public void Parse_ZeroAreaOpenGround_CollinearClosedRing_IsRejectedNamingOpenGround()
    {
        // Criterion 1 (and 3 at load time): closed and 4 points long, but every point on one line, so its shoelace
        // area is 0 and SpawnPicker couldn't draw from it.
        var json = Modify(root =>
            root["spawn_area"]!["open_ground"] = JsonNode.Parse("[[[[15, 15], [25, 15], [35, 15], [15, 15]]]]"));

        var ex = Assert.Throws<MapPackException>(() => BoundaryReader.Parse(json, TestManifest, Source));
        Assert.Contains(Source, ex.Message);
        Assert.Contains("'spawn_area.open_ground' has no area", ex.Message);
    }

    [Fact]
    public void Parse_OpenGroundHoleLargerThanItsExterior_IsRejectedNamingThePolygon()
    {
        // Criterion 1: the 20 m × 20 m exterior (400 m²) with a 20.5 m × 20.25 m hole (415.125 m²): area −15.125 m².
        var json = Modify(root => root["spawn_area"]!["open_ground"]![0]![1] = JsonNode.Parse(LargeHole));

        var ex = Assert.Throws<MapPackException>(() => BoundaryReader.Parse(json, TestManifest, Source));
        Assert.Contains(Source, ex.Message);
        Assert.Contains("'spawn_area.open_ground[0]' has holes larger than its exterior", ex.Message);
        Assert.Contains("-15.125", ex.Message);
    }

    [Fact]
    public void Parse_RejectionMessage_IsCultureInvariantUnderFinnishCulture()
    {
        // fi-FI writes −15.125 as "−15,125" (U+2212 and a decimal comma); the reader's numbers must not.
        var finnish = new System.Globalization.CultureInfo("fi-FI");
        var finnishText = (-15.125).ToString(finnish);
        Assert.True(
            finnishText.Contains('−', StringComparison.Ordinal) || finnishText.Contains(',', StringComparison.Ordinal),
            $"fi-FI formats -15.125 as '{finnishText}', so this test couldn't tell the cultures apart");
        var json = Modify(root => root["spawn_area"]!["open_ground"]![0]![1] = JsonNode.Parse(LargeHole));

        var saved = System.Globalization.CultureInfo.CurrentCulture;
        MapPackException ex;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = finnish;
            ex = Assert.Throws<MapPackException>(() => BoundaryReader.Parse(json, TestManifest, Source));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }

        Assert.DoesNotContain("−", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("15,125", ex.Message, StringComparison.Ordinal);
        Assert.Contains("-15.125", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A closed hole ring of 20.5 m × 20.25 m around the valid file's 20 m × 20 m open-ground exterior.</summary>
    private const string LargeHole = "[[14, 14], [34.5, 14], [34.5, 34.25], [14, 34.25], [14, 14]]";

    /// <summary>
    /// The generated pack in <c>game/maps/&lt;id&gt;/</c>, full or test area (from its own manifest), parses without
    /// error and has every part. Skipped when no pack has been built.
    /// </summary>
    [PackFact]
    public void Parse_GeneratedPack_ReadsWithoutErrors()
    {
        foreach (var pack in GeneratedPacks.WithBoundary())
        {
            var manifest = MapManifestReader.Parse(File.ReadAllText(pack.Manifest), pack.Manifest);

            var boundary = BoundaryReader.Parse(File.ReadAllText(pack.Boundary), manifest, pack.Boundary);

            Assert.Equal(manifest.MapId, boundary.MapId);
            Assert.Equal(manifest.Area, boundary.Area);
            Assert.True(boundary.Ring.Points.Count >= 3);
            Assert.True(boundary.Ring.LengthM > 0);
            Assert.True(boundary.VoidTrigger.BottomY < boundary.VoidTrigger.TopY);
            Assert.NotEmpty(boundary.VoidTrigger.Prisms);
            Assert.True(boundary.SpawnArea.Polygon.Count >= 3);
            Assert.NotEmpty(boundary.SpawnArea.OpenGround);
            Assert.True(boundary.SpawnArea.OpenAreaM2 > 0);
            Assert.True(boundary.SpawnArea.OpenGround.Sum(p => p.Area) > 0);
        }
    }

    private const double Sentinel = 987654.125;

    private static void AssertNonFinite(Action<JsonNode> change, string literal, string path)
    {
        var json = Modify(change).Replace("987654.125", literal, StringComparison.Ordinal);

        var ex = Assert.Throws<MapPackException>(() => BoundaryReader.Parse(json, TestManifest, Source));
        Assert.Contains(Source, ex.Message);
        Assert.Contains($"'{path}", ex.Message);
        Assert.Contains("must be a finite number", ex.Message);
    }

    internal static MapManifest Manifest(string mapId, string area) =>
        new(1, mapId, "Somero-City", area, new MapOrigin(0, 0), 256, [], []);

    private static string Join(string parent, string key) => parent.Length == 0 ? key : $"{parent}.{key}";

    /// <summary>The node at a path such as <c>roadblocks[0].nodes[1]</c> ("" is the root).</summary>
    private static JsonNode At(JsonNode root, string path)
    {
        var node = root;
        if (path.Length == 0)
        {
            return node;
        }

        foreach (var part in path.Split('.'))
        {
            var bracket = part.IndexOf('[', StringComparison.Ordinal);
            var name = bracket < 0 ? part : part[..bracket];
            node = node[name]!;
            if (bracket >= 0)
            {
                var index = int.Parse(part[(bracket + 1)..^1], System.Globalization.CultureInfo.InvariantCulture);
                node = node[index]!;
            }
        }

        return node;
    }

    private static string Modify(Action<JsonNode> change)
    {
        var root = JsonNode.Parse(ValidBoundary)!;
        change(root);
        return root.ToJsonString();
    }
}
