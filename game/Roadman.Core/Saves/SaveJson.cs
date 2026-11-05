using System.Globalization;
using System.Text.Json;

namespace Roadman.Core.Saves;

/// <summary>
/// Strict helpers for reading save JSON, in the style of the pack readers (<c>Maps/PackJson.cs</c>), but every
/// problem is a <see cref="SaveFormatException"/> carrying the key path.
/// </summary>
internal static class SaveJson
{
    public static SaveFormatException Error(string path, string problem) => new(path, $"'{path}' {problem}");

    public static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    /// <summary>Throws when <paramref name="element"/> has a key not in <paramref name="allowed"/>, or a key twice.</summary>
    public static void RejectUnknown(JsonElement element, string path, params string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var keyPath = Join(path, property.Name);
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                var known = allowed.Length == 0 ? "none in this version" : string.Join(", ", allowed);
                throw Error(keyPath, $"is not a known key (allowed: {known})");
            }

            if (!seen.Add(property.Name))
            {
                throw Error(keyPath, "is given more than once");
            }
        }
    }

    /// <summary>A key that must be present; its value may be anything, <c>null</c> included.</summary>
    public static JsonElement Present(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw Error(Join(path, name), "is missing");
        }

        return value;
    }

    public static JsonElement Object(JsonElement parent, string name, string path)
    {
        var value = Present(parent, name, path);
        return value.ValueKind == JsonValueKind.Object ? value : throw Error(Join(path, name), "must be an object");
    }

    public static string String(JsonElement parent, string name, string path)
    {
        var value = Present(parent, name, path);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Error(Join(path, name), "must be a string");
    }

    public static int Int(JsonElement parent, string name, string path)
    {
        var value = Present(parent, name, path);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw Error(Join(path, name), "must be a whole number");
    }

    public static double Number(JsonElement parent, string name, string path) =>
        Number(Present(parent, name, path), Join(path, name));

    public static double Number(JsonElement value, string keyPath) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) && double.IsFinite(result)
            ? result
            : throw Error(keyPath, "must be a finite number");

    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
