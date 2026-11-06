using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Roadman.Core.Saves;

/// <summary>
/// Writes a <see cref="SaveGame"/> as indented JSON with <c>snake_case</c> keys, in the current format version only.
/// <see cref="SaveReader.Read(string)"/> of the output gives an equal value. Shape:
/// <code>
/// {
///   "format_version": 2,
///   "game_version": "dev",
///   "saved_at_utc": "2026-11-05T12:34:56.1234567Z",
///   "play_seconds": 4980.25,
///   "map_id": "somero",
///   "map_generated_at": "2026-11-01T10:00:00Z",
///   "player": {
///     "position": [12.5, 101.25, -40],
///     "facing_yaw": 1.5707963267948966,
///     "money": 120,
///     "gear": ["crowbar", "phone"],
///     "equipped": { "weapon": "crowbar", "outfit": null },
///     "wanted_level": 2
///   },
///   "quests": {}
/// }
/// </code>
/// <c>position</c> and <c>facing_yaw</c> are both <c>null</c> for an empty position. Numbers are written in their
/// shortest round-trip form; the timestamp in UTC with 7 fractional digits, so it round-trips to the tick.
/// </summary>
public static class SaveWriter
{
    private static readonly JsonWriterOptions Options = new() { Indented = true };

    /// <exception cref="ArgumentNullException"><paramref name="save"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The save breaks a rule of <see cref="SaveGame.Problem"/> (the message names the key path); nothing would read it back.
    /// </exception>
    public static string Write(SaveGame save)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (save.Problem() is { } problem)
        {
            throw new ArgumentException($"the save can't be written: '{problem.KeyPath}' {problem.Problem}", nameof(save));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format_version", save.FormatVersion);
            writer.WriteString("game_version", save.GameVersion);
            writer.WriteString(
                "saved_at_utc",
                save.SavedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteNumber("play_seconds", save.PlaySeconds);
            writer.WriteString("map_id", save.MapId);
            writer.WriteString("map_generated_at", save.MapGeneratedAt);

            var player = save.Player;
            writer.WriteStartObject("player");
            if (player.Position is { } p)
            {
                writer.WriteStartArray("position");
                writer.WriteNumberValue(p.X);
                writer.WriteNumberValue(p.Y);
                writer.WriteNumberValue(p.Z);
                writer.WriteEndArray();
                writer.WriteNumber("facing_yaw", player.FacingYaw!.Value);
            }
            else
            {
                writer.WriteNull("position");
                writer.WriteNull("facing_yaw");
            }

            writer.WriteNumber("money", player.Money);
            writer.WriteStartArray("gear");
            foreach (var id in player.Gear)
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
            writer.WriteStartObject("equipped");
            WriteSlot(writer, "weapon", player.Equipped.Weapon);
            WriteSlot(writer, "outfit", player.Equipped.Outfit);
            writer.WriteEndObject();
            writer.WriteNumber("wanted_level", player.WantedLevel);
            writer.WriteEndObject();

            writer.WriteStartObject("quests");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteSlot(Utf8JsonWriter writer, string name, string? id)
    {
        if (id is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, id);
        }
    }
}
