using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Roadman.Core.Maps;

namespace Roadman.Core.Saves;

/// <summary>
/// Reads a save file written by <see cref="SaveWriter"/>. Strict, like the pack readers: a missing or unknown key
/// (or a key given twice), a wrong type, a non-finite number or an out-of-range value at any level is a
/// <see cref="SaveFormatException"/> naming the key path. The rules are <see cref="SaveGame.Problem"/>'s, plus:
/// <c>saved_at_utc</c> is an ISO 8601 timestamp with a zero offset (<c>Z</c> or <c>+00:00</c>); <c>position</c>
/// is <c>null</c> or an array of 3 finite numbers; <c>facing_yaw</c> is <c>null</c> or a finite number already in
/// (−π, π] (the reader doesn't normalise); both of those keys must be present, and both <c>null</c> or neither;
/// <c>gear</c> holds non-blank strings, each once, in any order (stored sorted); <c>quests</c> is an empty object.
/// </summary>
/// <remarks>
/// <c>format_version</c> is read first. Above <see cref="SaveFormat.CurrentVersion"/> it is an error with
/// <see cref="SaveFormatException.NewerVersion"/> set and a message saying a newer version of the game saved it.
/// Below, the file goes through <see cref="SaveMigrations"/> step by step before the strict read.
/// </remarks>
public static class SaveReader
{
    private static readonly string[] RootKeys =
    [
        "format_version", "game_version", "saved_at_utc", "play_seconds", "map_id", "map_generated_at", "player",
        "quests",
    ];

    private static readonly string[] PlayerKeys = ["position", "facing_yaw", "money", "gear", "wanted_level"];

    /// <summary>Reads <paramref name="json"/> with <see cref="SaveMigrations.Default"/>.</summary>
    /// <exception cref="SaveFormatException">Any problem; see the class remarks.</exception>
    public static SaveGame Read(string json) => Read(json, SaveMigrations.Default);

    /// <summary>Reads <paramref name="json"/>, migrating an older format version with <paramref name="migrations"/>.</summary>
    /// <exception cref="SaveFormatException">Any problem; see the class remarks.</exception>
    public static SaveGame Read(string json, SaveMigrations migrations)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(migrations);

        using var document = Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new SaveFormatException(null, "the top level must be a JSON object");
        }

        var version = SaveJson.Int(root, "format_version", "");
        if (version > SaveFormat.CurrentVersion)
        {
            throw new SaveFormatException(
                "format_version",
                $"'format_version' is {version}: this save was saved by a newer version of the game (this one reads up to version {SaveFormat.CurrentVersion})",
                newerVersion: true);
        }

        if (version < 0)
        {
            throw SaveJson.Error("format_version", $"must be ≥ 0, is {version}");
        }

        if (version == SaveFormat.CurrentVersion)
        {
            return ReadCurrent(root);
        }

        JsonObject node;
        try
        {
            node = JsonNode.Parse(json)?.AsObject() ?? throw new SaveFormatException(null, "the top level must be a JSON object");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // JsonObject refuses a key given twice; the strict read would reject it too.
            throw new SaveFormatException(null, $"not readable for migration ({ex.Message})", innerException: ex);
        }

        var migrated = migrations.Apply(node, version);
        using var migratedDocument = Parse(migrated.ToJsonString());
        return ReadCurrent(migratedDocument.RootElement);
    }

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SaveFormatException(null, $"not valid JSON ({ex.Message})", innerException: ex);
        }
    }

    private static SaveGame ReadCurrent(JsonElement root)
    {
        SaveJson.RejectUnknown(root, "", RootKeys);
        var version = SaveJson.Int(root, "format_version", "");
        if (version != SaveFormat.CurrentVersion)
        {
            throw SaveJson.Error("format_version", $"must be {SaveFormat.CurrentVersion} after migration, is {version}");
        }

        var gameVersion = SaveJson.String(root, "game_version", "");
        var savedAt = Timestamp(root, "saved_at_utc");
        var playSeconds = SaveJson.Number(root, "play_seconds", "");
        var mapId = SaveJson.String(root, "map_id", "");
        var generatedAt = SaveJson.String(root, "map_generated_at", "");
        var player = ReadPlayer(SaveJson.Object(root, "player", ""), "player");

        var quests = SaveJson.Object(root, "quests", "");
        SaveJson.RejectUnknown(quests, "quests");

        var save = new SaveGame(version, gameVersion, savedAt, playSeconds, mapId, generatedAt, player, SaveQuests.Empty);
        if (save.Problem() is { } problem)
        {
            throw SaveJson.Error(problem.KeyPath, problem.Problem);
        }

        return save;
    }

    private static SavePlayer ReadPlayer(JsonElement element, string path)
    {
        SaveJson.RejectUnknown(element, path, PlayerKeys);

        LocalPoint? position = null;
        var positionValue = SaveJson.Present(element, "position", path);
        var positionPath = SaveJson.Join(path, "position");
        if (positionValue.ValueKind != JsonValueKind.Null)
        {
            if (positionValue.ValueKind != JsonValueKind.Array || positionValue.GetArrayLength() != 3)
            {
                throw SaveJson.Error(positionPath, "must be null or an array of 3 numbers [x, y, z]");
            }

            var xyz = new double[3];
            var i = 0;
            foreach (var item in positionValue.EnumerateArray())
            {
                xyz[i] = SaveJson.Number(item, $"{positionPath}[{i}]");
                i++;
            }

            position = new LocalPoint(xyz[0], xyz[1], xyz[2]);
        }

        double? facing = null;
        var facingValue = SaveJson.Present(element, "facing_yaw", path);
        var facingPath = SaveJson.Join(path, "facing_yaw");
        if (facingValue.ValueKind != JsonValueKind.Null)
        {
            var yaw = SaveJson.Number(facingValue, facingPath);
            if (yaw <= -Math.PI || yaw > Math.PI)
            {
                throw SaveJson.Error(facingPath, $"must be in (-π, π], is {SaveJson.Format(yaw)}");
            }

            facing = yaw;
        }

        if ((position is null) != (facing is null))
        {
            throw position is null
                ? SaveJson.Error(facingPath, "must be null when position is null")
                : SaveJson.Error(facingPath, "must not be null when position isn't");
        }

        var money = SaveJson.Int(element, "money", path);
        var gear = Gear(element, path);
        var wanted = SaveJson.Int(element, "wanted_level", path);
        return new SavePlayer(position, facing, money, gear, wanted);
    }

    /// <summary>The gear array, checked in file order so the key path names the file's index.</summary>
    private static List<string> Gear(JsonElement parent, string path)
    {
        var gearPath = SaveJson.Join(path, "gear");
        var array = SaveJson.Present(parent, "gear", path);
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw SaveJson.Error(gearPath, "must be an array of strings");
        }

        var result = new List<string>(array.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            var itemPath = $"{gearPath}[{i}]";
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw SaveJson.Error(itemPath, "must be a non-blank string");
            }

            var id = item.GetString()!;
            if (!seen.Add(id))
            {
                throw SaveJson.Error(itemPath, $"'{id}' appears more than once");
            }

            result.Add(id);
            i++;
        }

        return result;
    }

    private static DateTimeOffset Timestamp(JsonElement parent, string name)
    {
        var text = SaveJson.String(parent, name, "");
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            || !(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))
            || !text.Contains('T'))
        {
            throw SaveJson.Error(name, $"must be an ISO 8601 UTC timestamp (for example 2026-11-05T12:00:00Z), is '{text}'");
        }

        return value.ToUniversalTime();
    }
}
