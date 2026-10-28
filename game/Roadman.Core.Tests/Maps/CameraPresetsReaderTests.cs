using System.Text.Json.Nodes;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class CameraPresetsReaderTests
{
    private const string ValidPresets = """
        {
          "schema_version": 1,
          "presets": {
            "overview": {"position": [0, 600, 500], "look_at": [0, 0, 0], "fov": 70, "ground_relative": true},
            "centre-street-level": {"position": [5.5, 101.7, -3], "look_at": [80, 101.7, -40], "fov": 60.5, "ground_relative": false}
          }
        }
        """;

    [Fact]
    public void Parse_ValidFile_ReadsEveryPreset()
    {
        var presets = CameraPresetsReader.Parse(ValidPresets);

        Assert.Equal(1, presets.SchemaVersion);
        Assert.Equal(2, presets.Presets.Count);
        Assert.Equal(
            new CameraPreset("overview", new LocalPoint(0, 600, 500), new LocalPoint(0, 0, 0), 70, true),
            presets.Presets["overview"]);
        Assert.Equal(
            new CameraPreset("centre-street-level", new LocalPoint(5.5, 101.7, -3), new LocalPoint(80, 101.7, -40), 60.5, false),
            presets.Presets["centre-street-level"]);
    }

    [Fact]
    public void Parse_OptionalFields_GetDefaults()
    {
        var json = Modify(root =>
        {
            root["presets"]!["overview"]!.AsObject().Remove("fov");
            root["presets"]!["overview"]!.AsObject().Remove("ground_relative");
        });

        var preset = CameraPresetsReader.Parse(json).Presets["overview"];

        Assert.Equal(CameraPresetsReader.DefaultFov, preset.Fov);
        Assert.False(preset.GroundRelative);
    }

    [Fact]
    public void Parse_EmptyPresets_IsRejected()
    {
        var ex = Assert.Throws<MapPackException>(
            () => CameraPresetsReader.Parse("""{"schema_version": 1, "presets": {}}"""));
        Assert.Contains("'presets' must have at least one preset", ex.Message);
    }

    [Fact]
    public void Parse_UnknownTopLevelKey_IsRejected()
    {
        var json = Modify(root => root["comment"] = "hello");

        var ex = Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse(json));
        Assert.Contains("'comment' is not a known field", ex.Message);
    }

    [Fact]
    public void Parse_UnknownPresetKey_NamesPresetAndKey()
    {
        var json = Modify(root => root["presets"]!["overview"]!["zoom"] = 2);

        var ex = Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse(json));
        Assert.Contains("'presets.overview.zoom' is not a known field", ex.Message);
    }

    [Fact]
    public void Parse_DuplicatePresetName_IsAMapPackErrorNamingIt()
    {
        const string json = """
            {
              "schema_version": 1,
              "presets": {
                "overview": {"position": [0, 600, 500], "look_at": [0, 0, 0]},
                "overview": {"position": [0, 300, 500], "look_at": [0, 0, 0]}
              }
            }
            """;

        var ex = Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse(json));
        Assert.Contains("preset name 'overview' is used more than once", ex.Message);
    }

    [Theory]
    [InlineData("""{"schema_version": 1, "schema_version": 1, "presets": {"a": {"position": [0, 1, 0], "look_at": [0, 0, 0]}}}""", "'schema_version'")]
    [InlineData("""{"schema_version": 1, "presets": {"a": {"position": [0, 1, 0], "look_at": [0, 0, 0], "fov": 60, "fov": 70}}}""", "'presets.a.fov'")]
    public void Parse_RepeatedKey_IsRejected(string json, string field)
    {
        var ex = Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse(json));
        Assert.Contains($"{field} is given more than once", ex.Message);
    }

    [Theory]
    [InlineData("fov")]
    [InlineData("ground_relative")]
    public void Parse_OptionalFieldSetToNull_IsRejected(string field)
    {
        var json = Modify(root => root["presets"]!["overview"]![field] = null);

        var ex = Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse(json));
        Assert.Contains($"presets.overview.{field}", ex.Message);
    }

    [Fact]
    public void Parse_OtherSchemaVersion_IsRejected()
    {
        var json = Modify(root => root["schema_version"] = 2);

        var ex = Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse(json));
        Assert.Contains("schema_version 2 is not supported", ex.Message);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("look_at")]
    public void Parse_MissingPoint_NamesPresetAndField(string field)
    {
        var json = Modify(root => root["presets"]!["overview"]!.AsObject().Remove(field));

        var ex = Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse(json));
        Assert.Contains($"'presets.overview.{field}' is missing", ex.Message);
    }

    [Fact]
    public void Parse_PointWithTwoNumbers_IsRejected()
    {
        var json = Modify(root => root["presets"]!["overview"]!["position"] = new JsonArray(0, 1));

        var ex = Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse(json));
        Assert.Contains("must have 3 numbers", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    public void Parse_FovOutOfRange_IsRejected(double fov)
    {
        var json = Modify(root => root["presets"]!["overview"]!["fov"] = fov);

        var ex = Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse(json));
        Assert.Contains("presets.overview.fov", ex.Message);
    }

    [Fact]
    public void Parse_GroundRelativeNotBool_IsRejected()
    {
        var json = Modify(root => root["presets"]!["overview"]!["ground_relative"] = "yes");

        Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse(json));
    }

    [Fact]
    public void Parse_PositionEqualToLookAt_IsRejected()
    {
        var json = Modify(root => root["presets"]!["overview"]!["look_at"] = new JsonArray(0, 600, 500));

        Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse(json));
    }

    [Theory]
    [InlineData("Overview")]
    [InlineData("../x")]
    [InlineData("with space")]
    [InlineData("overview-")]
    [InlineData("-overview")]
    [InlineData("_overview")]
    [InlineData("over--view")]
    [InlineData("over_-view")]
    [InlineData("overview\n")]
    public void Parse_BadPresetName_IsRejected(string name)
    {
        var json = Modify(root =>
        {
            var presets = root["presets"]!.AsObject();
            var preset = presets["overview"]!.DeepClone();
            presets[name] = preset;
        });

        Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse(json));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("2")]
    [InlineData("street_level-2")]
    public void Parse_SlugPresetName_IsAccepted(string name)
    {
        var json = Modify(root =>
        {
            var presets = root["presets"]!.AsObject();
            presets[name] = presets["overview"]!.DeepClone();
        });

        Assert.True(CameraPresetsReader.Parse(json).Presets.ContainsKey(name));
    }

    [Fact]
    public void Parse_MissingPresets_IsRejected()
    {
        var ex = Assert.Throws<MapPackException>(() => CameraPresetsReader.Parse("""{"schema_version": 1}"""));
        Assert.Contains("'presets' is missing", ex.Message);
    }

    private static string Modify(Action<JsonNode> change)
    {
        var root = JsonNode.Parse(ValidPresets)!;
        change(root);
        return root.ToJsonString();
    }
}
