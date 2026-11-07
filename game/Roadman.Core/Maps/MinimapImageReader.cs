using System.Text.Json;

namespace Roadman.Core.Maps;

/// <summary>
/// Parses and checks a pack's <c>minimap.json</c> (schema version 1, <c>docs/specs/minimap.md</c>, ADR 0021) against
/// the pack's manifest. Any problem throws a <see cref="MapPackException"/> that names the file and the key, like
/// <see cref="BoundaryReader"/>: another schema version; a missing, mistyped or unknown key at any level; a
/// <c>map_id</c> or <c>area</c> that isn't the manifest's; an <c>image</c> other than <c>minimap.png</c>; a
/// <c>m_per_px</c>, <c>width</c> or <c>height</c> not above 0; non-finite numbers; and bounds whose extent isn't the
/// size times <c>m_per_px</c> (within <see cref="BoundsTolerance"/>).
/// </summary>
public static class MinimapImageReader
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>
    /// How far, in metres, <c>max_x − min_x</c> may differ from <c>width × m_per_px</c> (and likewise for z): room for
    /// the rounding of the written numbers, far below one pixel at any allowed <c>m_per_px</c> (0.5 to 2.0).
    /// </summary>
    public const double BoundsTolerance = 1e-3;

    private static readonly string[] RootFields =
        ["schema_version", "map_id", "area", "image", "m_per_px", "width", "height", "bounds_local"];

    private static readonly string[] BoundsFields = ["min_x", "max_x", "min_z", "max_z"];

    /// <param name="json">The file's contents.</param>
    /// <param name="manifest">The pack's manifest; <c>map_id</c> and <c>area</c> must be its own.</param>
    /// <param name="source">Name used in error messages, usually the file path.</param>
    public static MinimapImage Parse(string json, MapManifest manifest, string source = MapPack.MinimapFile)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var document = PackJson.Parse(json, source);
        var root = PackJson.Root(document, source);
        PackJson.RejectUnknown(root, source, "", RootFields);

        var version = PackJson.Int(root, "schema_version", source, "");
        if (version != SupportedSchemaVersion)
        {
            throw new MapPackException(
                $"{source}: 'schema_version' {version} is not supported; this game reads version {SupportedSchemaVersion}");
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

        var image = PackJson.String(root, "image", source, "");
        if (image != MapPack.MinimapImageFile)
        {
            throw PackJson.Error(source, "image", $"must be '{MapPack.MinimapImageFile}', not '{image}'");
        }

        var metresPerPixel = PackJson.Number(root, "m_per_px", source, "");
        if (!(metresPerPixel > 0))
        {
            throw PackJson.Error(source, "m_per_px", FormattableString.Invariant($"must be greater than 0, is {metresPerPixel}"));
        }

        var width = PositiveInt(root, "width", source);
        var height = PositiveInt(root, "height", source);

        var boundsElement = PackJson.Object(root, "bounds_local", source, "");
        PackJson.RejectUnknown(boundsElement, source, "bounds_local", BoundsFields);
        var bounds = new MinimapBounds(
            PackJson.Number(boundsElement, "min_x", source, "bounds_local"),
            PackJson.Number(boundsElement, "max_x", source, "bounds_local"),
            PackJson.Number(boundsElement, "min_z", source, "bounds_local"),
            PackJson.Number(boundsElement, "max_z", source, "bounds_local"));
        CheckExtent(bounds.MinX, bounds.MaxX, width, metresPerPixel, "max_x", "min_x", "width", source);
        CheckExtent(bounds.MinZ, bounds.MaxZ, height, metresPerPixel, "max_z", "min_z", "height", source);

        return new MinimapImage(version, mapId, area, image, metresPerPixel, width, height, bounds);
    }

    private static int PositiveInt(JsonElement root, string name, string source)
    {
        var value = PackJson.Int(root, name, source, "");
        return value > 0 ? value : throw PackJson.Error(source, name, $"must be greater than 0, is {value}");
    }

    private static void CheckExtent(
        double min,
        double max,
        int pixels,
        double metresPerPixel,
        string maxKey,
        string minKey,
        string sizeKey,
        string source)
    {
        var expected = pixels * metresPerPixel;
        var actual = max - min;
        if (!(Math.Abs(actual - expected) <= BoundsTolerance))
        {
            throw PackJson.Error(
                source,
                $"bounds_local.{maxKey}",
                FormattableString.Invariant(
                    $"must be {minKey} + {sizeKey} × m_per_px ({min} + {pixels} × {metresPerPixel} = {min + expected}), is {max}"));
        }
    }
}
