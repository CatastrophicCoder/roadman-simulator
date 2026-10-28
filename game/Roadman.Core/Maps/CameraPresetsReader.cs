using System.Text.Json;

namespace Roadman.Core.Maps;

/// <summary>
/// Parses and checks <c>camera_presets.json</c>:
/// <c>{"schema_version": 1, "presets": {"&lt;name&gt;": {"position": [x, y, z], "look_at": [x, y, z],
/// "fov": 70, "ground_relative": true}}}</c>.
/// <c>fov</c> defaults to <see cref="DefaultFov"/> and <c>ground_relative</c> to false when absent (not when null).
/// The rules follow the pipeline model (<c>tools/mapgen/src/mapgen/build/camera_presets.py</c>): unknown keys are
/// errors, keys appear once, there is at least one preset, and names are slugs (<see cref="MapPack.SlugPattern"/>), each used once.
/// Problems throw a <see cref="MapPackException"/> naming the field, like <see cref="MapManifestReader"/>.
/// </summary>
public static class CameraPresetsReader
{
    public const int SupportedSchemaVersion = 1;
    public const double DefaultFov = 70;
    public const double MinFov = 1;
    public const double MaxFov = 179;

    /// <param name="json">The file's contents.</param>
    /// <param name="source">Name used in error messages, usually the file path.</param>
    public static CameraPresets Parse(string json, string source = MapPack.CameraPresetsFile)
    {
        using var document = PackJson.Parse(json, source);
        var root = PackJson.Root(document, source);
        PackJson.RejectUnknown(root, source, "", "schema_version", "presets");

        var version = PackJson.Int(root, "schema_version", source, "");
        if (version != SupportedSchemaVersion)
        {
            throw new MapPackException(
                $"{source}: schema_version {version} is not supported; this game reads version {SupportedSchemaVersion}");
        }

        var presets = new Dictionary<string, CameraPreset>(StringComparer.Ordinal);
        foreach (var property in PackJson.Object(root, "presets", source, "").EnumerateObject())
        {
            var path = $"presets.{property.Name}";
            if (!MapPack.IsSlug(property.Name))
            {
                throw PackJson.Error(
                    source,
                    path,
                    "name must be lowercase letters and digits, joined by single '-' or '_'");
            }

            if (presets.ContainsKey(property.Name))
            {
                throw PackJson.Error(source, path, $"preset name '{property.Name}' is used more than once");
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw PackJson.Error(source, path, "must be an object");
            }

            presets.Add(property.Name, ParsePreset(property.Name, property.Value, source, path));
        }

        if (presets.Count == 0)
        {
            throw PackJson.Error(source, "presets", "must have at least one preset");
        }

        return new CameraPresets(version, presets);
    }

    private static CameraPreset ParsePreset(string name, JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, "position", "look_at", "fov", "ground_relative");
        var position = Point(PackJson.Numbers(element, "position", 3, source, path));
        var lookAt = Point(PackJson.Numbers(element, "look_at", 3, source, path));
        var fov = PackJson.TryGet(element, "fov", out var fovValue)
            ? PackJson.Number(fovValue, source, $"{path}.fov")
            : DefaultFov;
        if (fov is < MinFov or > MaxFov)
        {
            throw PackJson.Error(source, $"{path}.fov", $"must be between {MinFov} and {MaxFov} degrees, not {fov}");
        }

        var groundRelative = PackJson.TryGet(element, "ground_relative", out var groundRelativeValue)
            && PackJson.Bool(groundRelativeValue, source, $"{path}.ground_relative");
        if (position == lookAt)
        {
            throw PackJson.Error(source, path, "position and look_at must differ");
        }

        return new CameraPreset(name, position, lookAt, fov, groundRelative);
    }

    private static LocalPoint Point(double[] values) => new(values[0], values[1], values[2]);
}
