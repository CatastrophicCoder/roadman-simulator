using System.Text.Json.Nodes;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

/// <summary>
/// <c>minimap.json</c> read by <see cref="MinimapImageReader"/>: <c>docs/specs/minimap.md</c>, acceptance criterion 4.
/// </summary>
public class MinimapImageReaderTest
{
    private const string Source = "res://maps/somero/minimap.json";

    // The test area at the default 1.0 m/px: a 300 m square around the origin (the pipeline's layout).
    private const string Valid = """
        {
          "schema_version": 1,
          "map_id": "somero",
          "area": "test",
          "image": "minimap.png",
          "m_per_px": 1.0,
          "width": 300,
          "height": 300,
          "bounds_local": {"min_x": -150.0, "max_x": 150.0, "min_z": -150.0, "max_z": 150.0}
        }
        """;

    private static readonly MapManifest TestManifest = BoundaryReaderTests.Manifest("somero", "test");

    [Fact]
    public void Ac4_ValidFile_ReadsEveryField()
    {
        var image = MinimapImageReader.Parse(Valid, TestManifest, Source);

        Assert.Equal(1, image.SchemaVersion);
        Assert.Equal("somero", image.MapId);
        Assert.Equal("test", image.Area);
        Assert.Equal("minimap.png", image.Image);
        Assert.Equal(1.0, image.MetresPerPixel);
        Assert.Equal(300, image.Width);
        Assert.Equal(300, image.Height);
        Assert.Equal(new MinimapBounds(-150, 150, -150, 150), image.Bounds);
    }

    [Fact]
    public void Ac4_NonSquareImageAtHalfAMetre_IsRead()
    {
        var json = Modify(root =>
        {
            root["m_per_px"] = 0.5;
            root["width"] = 4605;
            root["height"] = 4684;
            root["bounds_local"] = new JsonObject
            {
                ["min_x"] = -1153.0,
                ["max_x"] = 1149.5,
                ["min_z"] = -1171.0,
                ["max_z"] = 1171.0,
            };
        });

        var image = MinimapImageReader.Parse(json, TestManifest, Source);

        Assert.Equal(4605, image.Width);
        Assert.Equal(4684, image.Height);
        Assert.Equal(0.5, image.MetresPerPixel);
    }

    [Fact]
    public void Ac4_PipelineOutput_IsAcceptedForEveryGeneratedPack()
    {
        // The pipeline's own files, when a pack with minimap.json has been built (skipped otherwise).
        var game = GeneratedPacks.GameDirectory();
        var maps = game is null ? null : Path.Combine(game, "maps");
        if (maps is null || !Directory.Exists(maps))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(maps).Order(StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(dir, MapPack.ManifestFile);
            var minimapPath = Path.Combine(dir, MapPack.MinimapFile);
            if (!File.Exists(manifestPath) || !File.Exists(minimapPath))
            {
                continue;
            }

            var manifest = MapManifestReader.Parse(File.ReadAllText(manifestPath), manifestPath);
            var image = MinimapImageReader.Parse(File.ReadAllText(minimapPath), manifest, minimapPath);
            Assert.Equal(manifest.MapId, image.MapId);
        }
    }

    [Theory]
    [InlineData("", "colour")]
    [InlineData("bounds_local", "min_y")]
    public void Ac4_UnknownKey_IsRejectedNamingIt(string parent, string key)
    {
        var json = Modify(root => At(root, parent)[key] = 1);

        var ex = Assert.Throws<MapPackException>(() => MinimapImageReader.Parse(json, TestManifest, Source));
        Assert.Contains(Source, ex.Message);
        Assert.Contains($"'{Join(parent, key)}'", ex.Message);
        Assert.Contains("not a known field", ex.Message);
    }

    [Theory]
    [InlineData("", "schema_version")]
    [InlineData("", "map_id")]
    [InlineData("", "area")]
    [InlineData("", "image")]
    [InlineData("", "m_per_px")]
    [InlineData("", "width")]
    [InlineData("", "height")]
    [InlineData("", "bounds_local")]
    [InlineData("bounds_local", "min_x")]
    [InlineData("bounds_local", "max_x")]
    [InlineData("bounds_local", "min_z")]
    [InlineData("bounds_local", "max_z")]
    public void Ac4_MissingKey_IsRejectedNamingIt(string parent, string key)
    {
        var json = Modify(root => At(root, parent).AsObject().Remove(key));

        var ex = Assert.Throws<MapPackException>(() => MinimapImageReader.Parse(json, TestManifest, Source));
        Assert.Contains($"'{Join(parent, key)}' is missing", ex.Message);
    }

    [Fact]
    public void Ac4_SchemaVersion2_IsRejectedNamingTheKey()
    {
        var json = Modify(root => root["schema_version"] = 2);

        var ex = Assert.Throws<MapPackException>(() => MinimapImageReader.Parse(json, TestManifest, Source));
        Assert.Contains(Source, ex.Message);
        Assert.Contains("schema_version", ex.Message);
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void Ac4_MapIdNotTheManifests_IsRejectedNamingTheKey()
    {
        var ex = Assert.Throws<MapPackException>(
            () => MinimapImageReader.Parse(Valid, BoundaryReaderTests.Manifest("other", "test"), Source));
        Assert.Contains("'map_id'", ex.Message);
        Assert.Contains("'other'", ex.Message);
    }

    [Fact]
    public void Ac4_AreaNotTheManifests_IsRejectedNamingTheKey()
    {
        var ex = Assert.Throws<MapPackException>(
            () => MinimapImageReader.Parse(Valid, BoundaryReaderTests.Manifest("somero", "full"), Source));
        Assert.Contains("'area'", ex.Message);
        Assert.Contains("'full'", ex.Message);
    }

    [Fact]
    public void Ac4_AreaNotFullOrTest_IsRejected()
    {
        var json = Modify(root => root["area"] = "partial");

        var ex = Assert.Throws<MapPackException>(() => MinimapImageReader.Parse(json, TestManifest, Source));
        Assert.Contains("'area' must be 'full' or 'test'", ex.Message);
    }

    [Theory]
    [InlineData("minimap.jpg")]
    [InlineData("other/minimap.png")]
    [InlineData("")]
    public void Ac4_OtherImageName_IsRejected(string name)
    {
        var json = Modify(root => root["image"] = name);

        var ex = Assert.Throws<MapPackException>(() => MinimapImageReader.Parse(json, TestManifest, Source));
        Assert.Contains("'image' must be 'minimap.png'", ex.Message);
    }

    [Theory]
    [InlineData("width", 0)]
    [InlineData("width", -300)]
    [InlineData("height", 0)]
    [InlineData("height", -1)]
    [InlineData("m_per_px", 0)]
    [InlineData("m_per_px", -1)]
    public void Ac4_NonPositiveSize_IsRejectedNamingTheKey(string key, double value)
    {
        var json = Modify(root => root[key] = key == "m_per_px" ? value : (int)value);

        var ex = Assert.Throws<MapPackException>(() => MinimapImageReader.Parse(json, TestManifest, Source));
        Assert.Contains($"'{key}' must be greater than 0", ex.Message);
    }

    [Theory]
    [InlineData("width", "300.5")]
    [InlineData("width", "\"300\"")]
    [InlineData("height", "true")]
    public void Ac4_SizeNotAWholeNumber_IsRejectedNamingTheKey(string key, string value)
    {
        var json = Modify(root => root[key] = JsonNode.Parse(value));

        var ex = Assert.Throws<MapPackException>(() => MinimapImageReader.Parse(json, TestManifest, Source));
        Assert.Contains($"'{key}' must be a whole number", ex.Message);
    }

    [Theory]
    [InlineData("", "m_per_px")]
    [InlineData("bounds_local", "min_x")]
    [InlineData("bounds_local", "max_z")]
    public void Ac4_NotANumber_IsRejectedNamingTheKey(string parent, string key)
    {
        var json = Modify(root => At(root, parent)[key] = "1");

        var ex = Assert.Throws<MapPackException>(() => MinimapImageReader.Parse(json, TestManifest, Source));
        Assert.Contains($"'{Join(parent, key)}' must be a finite number", ex.Message);
    }

    [Theory]
    [InlineData("max_x", 151.0)]
    [InlineData("max_x", 149.0)]
    [InlineData("min_x", -150.01)]
    [InlineData("max_z", 300.0)]
    [InlineData("min_z", 0.0)]
    public void Ac4_BoundsNotSizeTimesMetresPerPixel_IsRejectedNamingTheKey(string key, double value)
    {
        var json = Modify(root => root["bounds_local"]![key] = value);

        var ex = Assert.Throws<MapPackException>(() => MinimapImageReader.Parse(json, TestManifest, Source));
        var axis = key[^1];
        Assert.Contains($"'bounds_local.max_{axis}' must be min_{axis} + {(axis == 'x' ? "width" : "height")} × m_per_px", ex.Message);
    }

    [Fact]
    public void Ac4_BoundsWithinTolerance_AreAccepted()
    {
        var json = Modify(root => root["bounds_local"]!["max_x"] = 150.0 + (MinimapImageReader.BoundsTolerance / 2));

        Assert.Equal(300, MinimapImageReader.Parse(json, TestManifest, Source).Width);
    }

    [Fact]
    public void Ac4_ScaleChangeWithoutBounds_IsRejected()
    {
        var json = Modify(root => root["m_per_px"] = 2.0);

        var ex = Assert.Throws<MapPackException>(() => MinimapImageReader.Parse(json, TestManifest, Source));
        Assert.Contains("'bounds_local.max_x'", ex.Message);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Ac4_NotAnObject_IsRejected(string json)
    {
        var ex = Assert.Throws<MapPackException>(() => MinimapImageReader.Parse(json, TestManifest, Source));
        Assert.Contains(Source, ex.Message);
    }

    [Fact]
    public void Ac4_PixelOf_MapsCornersAndCentres()
    {
        var image = MinimapImageReader.Parse(Valid, TestManifest, Source);

        Assert.Equal((0.0, 0.0), image.PixelOf(-150, -150)); // north-west corner: top-left
        Assert.Equal((300.0, 300.0), image.PixelOf(150, 150));
        Assert.Equal((150.0, 150.0), image.PixelOf(0, 0));

        // 10 m north (−z) is 10 rows up; 10 m east is 10 columns right.
        Assert.Equal((150.0, 140.0), image.PixelOf(0, -10));
        Assert.Equal((160.0, 150.0), image.PixelOf(10, 0));
    }

    [Fact]
    public void Ac4_PixelOfAndWorldOf_RoundTrip()
    {
        var json = Modify(root =>
        {
            root["m_per_px"] = 0.75;
            root["width"] = 400;
            root["height"] = 200;
            root["bounds_local"] = new JsonObject
            {
                ["min_x"] = -1153.25,
                ["max_x"] = -853.25,
                ["min_z"] = 12.5,
                ["max_z"] = 162.5,
            };
        });
        var image = MinimapImageReader.Parse(json, TestManifest, Source);
        var random = new Random(4021);

        for (var i = 0; i < 1000; i++)
        {
            var x = -1500 + (random.NextDouble() * 1000);
            var z = -200 + (random.NextDouble() * 600);
            var (px, py) = image.PixelOf(x, z);
            var (bx, bz) = image.WorldOf(px, py);
            Assert.Equal(x, bx, 1e-9);
            Assert.Equal(z, bz, 1e-9);

            var qx = random.NextDouble() * image.Width;
            var qy = random.NextDouble() * image.Height;
            var (wx, wz) = image.WorldOf(qx, qy);
            var (rx, ry) = image.PixelOf(wx, wz);
            Assert.Equal(qx, rx, 1e-9);
            Assert.Equal(qy, ry, 1e-9);
        }
    }

    private static string Join(string parent, string key) => parent.Length == 0 ? key : $"{parent}.{key}";

    private static JsonNode At(JsonNode root, string path) => path.Length == 0 ? root : root[path]!;

    private static string Modify(Action<JsonNode> change)
    {
        var root = JsonNode.Parse(Valid)!;
        change(root);
        return root.ToJsonString();
    }
}
