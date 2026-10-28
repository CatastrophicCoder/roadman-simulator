using System.Text.RegularExpressions;

namespace Roadman.Core.Maps;

/// <summary>
/// Parses and checks a map pack's <c>manifest.json</c>. Any problem throws a <see cref="MapPackException"/>
/// that names the field: a schema version other than <see cref="SupportedSchemaVersion"/>, a missing or
/// mistyped field, axes other than east/up/−north, a duplicate or malformed tile id, or a tile path that is
/// absolute or leaves the pack folder.
/// Fields the game doesn't use (credits, CRS, build time) are not checked; the pipeline validates them.
/// </summary>
public static partial class MapManifestReader
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>The axes every v1 pack uses: local east is +X, up is +Y and north is −Z (ADR 0006).</summary>
    public static readonly IReadOnlyDictionary<string, string> ExpectedAxes =
        new Dictionary<string, string> { ["x"] = "east", ["y"] = "up", ["z"] = "-north" };

    /// <param name="json">The file's contents.</param>
    /// <param name="source">Name used in error messages, usually the file path.</param>
    public static MapManifest Parse(string json, string source = MapPack.ManifestFile)
    {
        using var document = PackJson.Parse(json, source);
        var root = PackJson.Root(document, source);

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

        var displayName = PackJson.String(root, "display_name", source, "");
        var area = PackJson.String(root, "area", source, "");
        if (area is not ("full" or "test"))
        {
            throw PackJson.Error(source, "area", $"must be 'full' or 'test', not '{area}'");
        }

        var originElement = PackJson.Object(root, "origin", source, "");
        var origin = new MapOrigin(
            PackJson.Number(originElement, "x", source, "origin"),
            PackJson.Number(originElement, "y", source, "origin"));

        var tileSize = PackJson.Int(root, "tile_size_m", source, "");
        if (tileSize <= 0)
        {
            throw PackJson.Error(source, "tile_size_m", "must be greater than 0");
        }

        CheckAxes(PackJson.Object(root, "axes", source, ""), source);

        var tiles = new List<MapTile>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in PackJson.Array(root, "tiles", source, "").EnumerateArray())
        {
            var path = $"tiles[{index}]";
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw PackJson.Error(source, path, "must be an object");
            }

            var tile = ParseTile(element, source, path);
            if (!ids.Add(tile.Id))
            {
                throw PackJson.Error(source, $"{path}.id", $"'{tile.Id}' appears more than once");
            }

            tiles.Add(tile);
            index++;
        }

        return new MapManifest(version, mapId, displayName, area, origin, tileSize, tiles);
    }

    private static void CheckAxes(System.Text.Json.JsonElement axes, string source)
    {
        foreach (var (axis, expected) in ExpectedAxes)
        {
            var actual = PackJson.String(axes, axis, source, "axes");
            if (actual != expected)
            {
                throw PackJson.Error(
                    source,
                    $"axes.{axis}",
                    $"is '{actual}', expected '{expected}' (the game uses +X east, +Y up, -Z north)");
            }
        }
    }

    private static MapTile ParseTile(System.Text.Json.JsonElement element, string source, string path)
    {
        var id = PackJson.String(element, "id", source, path);
        if (!TileIdPattern().IsMatch(id))
        {
            throw PackJson.Error(source, $"{path}.id", $"'{id}' doesn't match tile_x<i>_y<j>");
        }

        var file = PackJson.String(element, "file", source, path);
        if (!MapPack.IsSafeRelativePath(file))
        {
            throw PackJson.Error(
                source,
                $"{path}.file",
                $"'{file}' must be a relative path inside the pack (not absolute, no '..')");
        }

        var b = PackJson.Numbers(element, "bounds_local", 4, source, path);
        if (b[0] > b[2] || b[1] > b[3])
        {
            throw PackJson.Error(source, $"{path}.bounds_local", "must be [min_x, min_y, max_x, max_y] with min <= max");
        }

        var buildings = PackJson.Int(element, "building_count", source, path);
        if (buildings < 0)
        {
            throw PackJson.Error(source, $"{path}.building_count", "must not be negative");
        }

        return new MapTile(id, file, new TileBounds(b[0], b[1], b[2], b[3]), buildings);
    }

    [GeneratedRegex(@"^tile_x-?\d+_y-?\d+$")]
    private static partial Regex TileIdPattern();
}
