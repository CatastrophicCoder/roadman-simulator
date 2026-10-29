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

    public static long Long(JsonElement parent, string name, string source, string path)
    {
        var value = Property(parent, name, source, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw Error(source, Join(path, name), "must be a whole number");
        }

        return result;
    }

    /// <summary>
    /// A key that must be present but may be <c>null</c> (the pipeline writes such keys always, for example
    /// <c>name</c> or <c>speed_kmh</c>). Returns false for <c>null</c>; a missing key is an error.
    /// </summary>
    public static bool RequiredNullable(JsonElement parent, string name, string source, string path, out JsonElement value)
    {
        if (!parent.TryGetProperty(name, out value))
        {
            throw Error(source, Join(path, name), "is missing (it may be null, but the key must be there)");
        }

        return value.ValueKind != JsonValueKind.Null;
    }

    /// <summary>A required key whose value is a string or <c>null</c>.</summary>
    public static string? NullableString(JsonElement parent, string name, string source, string path)
    {
        if (!RequiredNullable(parent, name, source, path, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Error(source, Join(path, name), "must be a string or null");
        }

        return value.GetString()!;
    }

    /// <summary>A required key whose value is a whole number or <c>null</c>.</summary>
    public static long? NullableLong(JsonElement parent, string name, string source, string path)
    {
        if (!RequiredNullable(parent, name, source, path, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw Error(source, Join(path, name), "must be a whole number or null");
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

    /// <summary>A required, non-empty string.</summary>
    public static string NonEmptyString(JsonElement parent, string name, string source, string path)
    {
        var value = String(parent, name, source, path);
        if (value.Length == 0)
        {
            throw Error(source, Join(path, name), "must not be empty");
        }

        return value;
    }

    /// <summary>A required string that must be one of the keys of <paramref name="values"/>.</summary>
    public static T Choice<T>(JsonElement parent, string name, IReadOnlyDictionary<string, T> values, string source, string path)
    {
        var value = String(parent, name, source, path);
        return values.TryGetValue(value, out var result)
            ? result
            : throw Error(source, Join(path, name), $"'{value}' is not a known value (known: {string.Join(", ", values.Keys)})");
    }

    /// <summary>A required key whose value is <c>null</c> or one of the keys of <paramref name="values"/>.</summary>
    public static T? NullableChoice<T>(JsonElement parent, string name, IReadOnlyDictionary<string, T> values, string source, string path)
        where T : struct
    {
        if (!RequiredNullable(parent, name, source, path, out _))
        {
            return null;
        }

        var value = NullableString(parent, name, source, path)!;
        return values.TryGetValue(value, out var result)
            ? result
            : throw Error(
                source,
                Join(path, name),
                $"'{value}' is not a known value (known: {string.Join(", ", values.Keys)}, or null)");
    }

    /// <summary>An array of non-empty strings, each at most once.</summary>
    public static List<string> Strings(JsonElement parent, string name, string source, string path)
    {
        var array = Array(parent, name, source, path);
        var fieldPath = Join(path, name);
        var result = new List<string>(array.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            var itemPath = $"{fieldPath}[{i}]";
            if (item.ValueKind != JsonValueKind.String || item.GetString()!.Length == 0)
            {
                throw Error(source, itemPath, "must be a non-empty string");
            }

            var value = item.GetString()!;
            if (!seen.Add(value))
            {
                throw Error(source, itemPath, $"'{value}' appears more than once");
            }

            result.Add(value);
            i++;
        }

        return result;
    }

    /// <summary>The name fields <c>name</c>, <c>name_sv</c> and <c>ref</c> of <paramref name="element"/>, each required, string or null.</summary>
    public static FeatureNames Names(JsonElement element, string source, string path) =>
        new(
            NullableString(element, "name", source, path),
            NullableString(element, "name_sv", source, path),
            NullableString(element, "ref", source, path));

    /// <summary>The <c>address</c> object (<c>street</c>, <c>housenumber</c>, each required, string or null).</summary>
    public static Address Address(JsonElement element, string source, string path)
    {
        var address = Object(element, "address", source, path);
        var addressPath = Join(path, "address");
        RejectUnknown(address, source, addressPath, "street", "housenumber");
        return new Address(
            NullableString(address, "street", source, addressPath),
            NullableString(address, "housenumber", source, addressPath));
    }

    public static void RequireObject(JsonElement element, string source, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error(source, path, "must be an object");
        }
    }

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
