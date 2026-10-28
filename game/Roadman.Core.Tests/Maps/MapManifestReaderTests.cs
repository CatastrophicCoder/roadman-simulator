using System.Text.Json.Nodes;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class MapManifestReaderTests
{
    private const string ValidManifest = """
        {
          "schema_version": 1,
          "map_id": "somero",
          "display_name": "Somero-City",
          "area": "full",
          "crs": "EPSG:3067",
          "origin": {"x": 309657.0, "y": 6726616.0},
          "height_datum": "source DEM heights, metres",
          "tile_size_m": 256,
          "axes": {"x": "east", "y": "up", "z": "-north"},
          "tiles": [
            {"id": "tile_x-1_y0", "file": "tiles/tile_x-1_y0.glb", "bounds_local": [-256, 0, 0, 256], "building_count": 3},
            {"id": "tile_x0_y0", "file": "tiles/tile_x0_y0.glb", "bounds_local": [0, 0, 200, 256], "building_count": 0}
          ],
          "credits": [],
          "generated_at": "2026-10-27T20:38:24Z",
          "tool": {"name": "mapgen", "version": "0.1.0"}
        }
        """;

    [Fact]
    public void Parse_ValidManifest_ReadsFieldsAndTiles()
    {
        var manifest = MapManifestReader.Parse(ValidManifest);

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("somero", manifest.MapId);
        Assert.Equal("Somero-City", manifest.DisplayName);
        Assert.Equal("full", manifest.Area);
        Assert.Equal(new MapOrigin(309657.0, 6726616.0), manifest.Origin);
        Assert.Equal(256, manifest.TileSizeM);
        Assert.Equal(2, manifest.Tiles.Count);
        Assert.Equal(new MapTile("tile_x-1_y0", "tiles/tile_x-1_y0.glb", new TileBounds(-256, 0, 0, 256), 3), manifest.Tiles[0]);
    }

    [Fact]
    public void TileBounds_UseGodotZAsMinusNorth()
    {
        var bounds = new TileBounds(0, 0, 200, 256);

        Assert.True(bounds.ContainsGodotPoint(100, -100));
        Assert.False(bounds.ContainsGodotPoint(100, 100));
        Assert.Equal((100.0, -128.0), bounds.GodotCentre);
    }

    [Fact]
    public void ContainsGodotPoint_IsTrueOnlyOnATile()
    {
        var manifest = MapManifestReader.Parse(ValidManifest);

        Assert.True(manifest.ContainsGodotPoint(0, 0));
        Assert.True(manifest.ContainsGodotPoint(-10, -10));
        Assert.False(manifest.ContainsGodotPoint(250, -10));
        Assert.False(manifest.ContainsGodotPoint(10, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Parse_OtherSchemaVersion_IsRejected(int version)
    {
        var json = Modify(root => root["schema_version"] = version);

        var ex = Assert.Throws<MapPackException>(() => MapManifestReader.Parse(json, "res://maps/somero/manifest.json"));
        Assert.Contains($"schema_version {version} is not supported", ex.Message);
        Assert.Contains("res://maps/somero/manifest.json", ex.Message);
    }

    [Theory]
    [InlineData("schema_version")]
    [InlineData("map_id")]
    [InlineData("display_name")]
    [InlineData("area")]
    [InlineData("origin")]
    [InlineData("tile_size_m")]
    [InlineData("axes")]
    [InlineData("tiles")]
    public void Parse_MissingField_NamesIt(string field)
    {
        var json = Modify(root => root.AsObject().Remove(field));

        var ex = Assert.Throws<MapPackException>(() => MapManifestReader.Parse(json));
        Assert.Contains($"'{field}' is missing", ex.Message);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("file")]
    [InlineData("bounds_local")]
    [InlineData("building_count")]
    public void Parse_MissingTileField_NamesTileAndField(string field)
    {
        var json = Modify(root => root["tiles"]![1]!.AsObject().Remove(field));

        var ex = Assert.Throws<MapPackException>(() => MapManifestReader.Parse(json));
        Assert.Contains($"'tiles[1].{field}' is missing", ex.Message);
    }

    [Theory]
    [InlineData("x", "west")]
    [InlineData("y", "down")]
    [InlineData("z", "north")]
    public void Parse_UnexpectedAxes_AreRejected(string axis, string value)
    {
        var json = Modify(root => root["axes"]![axis] = value);

        var ex = Assert.Throws<MapPackException>(() => MapManifestReader.Parse(json));
        Assert.Contains($"axes.{axis}", ex.Message);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../other/tile.glb")]
    [InlineData("tiles/../../x.glb")]
    [InlineData("tiles/..")]
    [InlineData(@"tiles\tile.glb")]
    [InlineData("C:/tiles/tile.glb")]
    [InlineData("res://maps/x.glb")]
    [InlineData("")]
    [InlineData("tiles//tile.glb")]
    public void Parse_UnsafeTilePath_IsRejected(string file)
    {
        var json = Modify(root => root["tiles"]![0]!["file"] = file);

        var ex = Assert.Throws<MapPackException>(() => MapManifestReader.Parse(json));
        Assert.Contains("tiles[0].file", ex.Message);
    }

    [Fact]
    public void Parse_DuplicateTileId_IsRejected()
    {
        var json = Modify(root => root["tiles"]![1]!["id"] = "tile_x-1_y0");

        var ex = Assert.Throws<MapPackException>(() => MapManifestReader.Parse(json));
        Assert.Contains("more than once", ex.Message);
    }

    [Theory]
    [InlineData("tile_x0")]
    [InlineData("tile_x0_y0/..")]
    [InlineData("Tiles")]
    public void Parse_MalformedTileId_IsRejected(string id)
    {
        var json = Modify(root => root["tiles"]![0]!["id"] = id);

        Assert.Throws<MapPackException>(() => MapManifestReader.Parse(json));
    }

    [Fact]
    public void Parse_WrongBoundsLength_IsRejected()
    {
        var json = Modify(root => root["tiles"]![0]!["bounds_local"] = new JsonArray(0, 0, 1));

        var ex = Assert.Throws<MapPackException>(() => MapManifestReader.Parse(json));
        Assert.Contains("must have 4 numbers", ex.Message);
    }

    [Fact]
    public void Parse_WrongType_IsRejected()
    {
        var json = Modify(root => root["tile_size_m"] = "256");

        var ex = Assert.Throws<MapPackException>(() => MapManifestReader.Parse(json));
        Assert.Contains("'tile_size_m' must be a whole number", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Parse_NotAJsonObject_IsRejected(string json)
    {
        Assert.Throws<MapPackException>(() => MapManifestReader.Parse(json));
    }

    [Theory]
    [InlineData("somero", true)]
    [InlineData("somero-test_2", true)]
    [InlineData("2024", true)]
    [InlineData("Somero", false)]
    [InlineData("../somero", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("somero-", false)]
    [InlineData("somero_", false)]
    [InlineData("-somero", false)]
    [InlineData("_somero", false)]
    [InlineData("some--ro", false)]
    [InlineData("some-_ro", false)]
    [InlineData("somero\n", false)]
    public void IsValidMapId_UsesThePipelineSlugPattern(string? id, bool valid)
    {
        Assert.Equal(valid, MapPack.IsValidMapId(id));
    }

    [Theory]
    [InlineData("somero-")]
    [InlineData("some--ro")]
    public void Parse_MapIdNotASlug_IsRejected(string mapId)
    {
        var json = Modify(root => root["map_id"] = mapId);

        var ex = Assert.Throws<MapPackException>(() => MapManifestReader.Parse(json));
        Assert.Contains("'map_id'", ex.Message);
    }

    private static string Modify(Action<JsonNode> change)
    {
        var root = JsonNode.Parse(ValidManifest)!;
        change(root);
        return root.ToJsonString();
    }
}
