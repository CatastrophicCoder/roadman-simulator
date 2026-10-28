using System.Text.Json;

namespace Roadman.Core.Maps;

/// <summary>
/// Small helpers for reading pack JSON with <see cref="JsonDocument"/>, so every problem becomes a
/// <see cref="MapPackException"/> that names the file and the field (for example <c>tiles[3].file</c>).
/// </summary>
internal static class PackJson
{
    public static JsonDocument Parse(string json, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new MapPackException($"{source}: not valid JSON ({ex.Message})", ex);
        }
    }

    public static JsonElement Object(JsonElement parent, string name, string source, string path)
    {
        var value = Property(parent, name, source, path);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Error(source, Join(path, name), "must be an object");
        }

        return value;
    }

    public static JsonElement Array(JsonElement parent, string name, string source, string path)
    {
        var value = Property(parent, name, source, path);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Error(source, Join(path, name), "must be an array");
        }

        return value;
    }

    public static string String(JsonElement parent, string name, string source, string path)
    {
        var value = Property(parent, name, source, path);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Error(source, Join(path, name), "must be a string");
        }

        return value.GetString()!;
    }

    public static int Int(JsonElement parent, string name, string source, string path)
    {
        var value = Property(parent, name, source, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw Error(source, Join(path, name), "must be a whole number");
        }

        return result;
    }

    public static double Number(JsonElement parent, string name, string source, string path)
    {
        var value = Property(parent, name, source, path);
        return Number(value, source, Join(path, name));
    }

    public static double Number(JsonElement value, string source, string path)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result))
        {
            throw Error(source, path, "must be a finite number");
        }

        return result;
    }

    public static bool Bool(JsonElement parent, string name, string source, string path)
    {
        var value = Property(parent, name, source, path);
        return Bool(value, source, Join(path, name));
    }

    /// <summary>An array of exactly <paramref name="length"/> finite numbers.</summary>
    public static double[] Numbers(JsonElement parent, string name, int length, string source, string path)
    {
        var array = Array(parent, name, source, path);
        var fieldPath = Join(path, name);
        if (array.GetArrayLength() != length)
        {
            throw Error(source, fieldPath, $"must have {length} numbers, has {array.GetArrayLength()}");
        }

        var result = new double[length];
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            result[i] = Number(item, source, $"{fieldPath}[{i}]");
            i++;
        }

        return result;
    }

    /// <summary>
    /// Throws when <paramref name="element"/> has a property not in <paramref name="allowed"/>, or the same property
    /// twice, like the pipeline's <c>extra="forbid"</c> models: a misspelt key is an error, not silently ignored.
    /// </summary>
    public static void RejectUnknown(JsonElement element, string source, string path, params string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = Join(path, property.Name);
            if (System.Array.IndexOf(allowed, property.Name) < 0)
            {
                throw Error(source, fieldPath, $"is not a known field (allowed: {string.Join(", ", allowed)})");
            }

            if (!seen.Add(property.Name))
            {
                throw Error(source, fieldPath, "is given more than once");
            }
        }
    }

    /// <summary>
    /// An optional property: false when absent. Unlike <see cref="Has"/>, an explicit <c>null</c> counts as present,
    /// so the type check that follows rejects it (the pipeline does too).
    /// </summary>
    public static bool TryGet(JsonElement parent, string name, out JsonElement value) =>
        parent.TryGetProperty(name, out value);

    public static bool Bool(JsonElement value, string source, string path) =>
        value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Error(source, path, "must be true or false"),
        };

    public static bool Has(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

    public static JsonElement Root(JsonDocument document, string source)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new MapPackException($"{source}: the top level must be a JSON object");
        }

        return document.RootElement;
    }

    public static MapPackException Error(string source, string path, string problem) =>
        new($"{source}: '{path}' {problem}");

    public static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static JsonElement Property(JsonElement parent, string name, string source, string path)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            throw Error(source, Join(path, name), "is missing");
        }

        return value;
    }
}
