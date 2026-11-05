using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Roadman.Core.Maps;
using Roadman.Core.Saves;

namespace Roadman.Core.Tests.Saves;

/// <summary>
/// <see cref="SaveWriter"/>, <see cref="SaveReader"/> and <see cref="SaveMigrations"/>:
/// <c>docs/specs/menu-settings-save.md</c>, acceptance criteria 1 and 2.
/// </summary>
public class SaveReaderWriterTests
{
    // A valid version-1 save with a position, two gear ids (not in ordinal order) and wanted level 2.
    internal const string ValidSave = """
        {
          "format_version": 1,
          "game_version": "0.2.0",
          "saved_at_utc": "2026-11-05T12:34:56.1234567Z",
          "play_seconds": 4980.25,
          "map_id": "somero",
          "map_generated_at": "2026-11-01T10:00:00Z",
          "player": {
            "position": [12.5, 101.25, -40],
            "facing_yaw": 1.5,
            "money": 120,
            "gear": ["b", "a"],
            "wanted_level": 2
          },
          "quests": {}
        }
        """;

    private static readonly Regex SnakeCase = new("^[a-z]+(_[a-z]+)*$", RegexOptions.CultureInvariant);

    internal static SaveGame FullSave() =>
        new(
            SaveFormat.CurrentVersion,
            "0.2.0-test1",
            new DateTimeOffset(2026, 11, 5, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567),
            4980.25,
            "somero",
            "2026-11-01T10:00:00Z",
            new SavePlayer(new LocalPoint(12.5, 101.25, -40.125), 1.25, 120, ["kit_b", "Kit_a", "kit_a"], 3),
            SaveQuests.Empty);

    internal static SaveGame EmptyPositionSave() =>
        new(
            SaveFormat.CurrentVersion,
            "dev",
            new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero),
            0,
            "test_area",
            "2026-11-01T10:00:00Z",
            new SavePlayer(null, null, 0, [], 0),
            SaveQuests.Empty);

    // Criterion 1

    [Fact]
    public void WriteThenRead_FullSave_GivesEqualValue()
    {
        var save = FullSave();

        var read = SaveReader.Read(SaveWriter.Write(save));

        Assert.Equal(save, read);
        Assert.Equal(120, read.Player.Money);
        Assert.Equal(3, read.Player.WantedLevel);
        Assert.Equal(new LocalPoint(12.5, 101.25, -40.125), read.Player.Position);
        Assert.Equal(1.25, read.Player.FacingYaw);
        Assert.Equal(4980.25, read.PlaySeconds);
        Assert.Equal("0.2.0-test1", read.GameVersion);
        Assert.Equal(save.SavedAtUtc.UtcTicks, read.SavedAtUtc.UtcTicks);
        Assert.Equal(SaveQuests.Empty, read.Quests);
    }

    [Fact]
    public void Write_Gear_IsWrittenInOrdinalOrder()
    {
        var root = JsonNode.Parse(SaveWriter.Write(FullSave()))!;

        var gear = root["player"]!["gear"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();

        Assert.Equal(["Kit_a", "kit_a", "kit_b"], gear);
    }

    [Fact]
    public void Write_AllKeys_AreSnakeCase()
    {
        var root = JsonNode.Parse(SaveWriter.Write(FullSave()))!.AsObject();

        var keys = AllKeys(root, "").ToList();

        Assert.Contains("format_version", keys);
        Assert.Contains("player.facing_yaw", keys);
        Assert.Contains("player.wanted_level", keys);
        Assert.All(keys, path => Assert.All(path.Split('.'), key => Assert.Matches(SnakeCase, key)));
    }

    [Fact]
    public void Write_QuestsAndVersion_AreAnEmptyObjectAndTheCurrentVersion()
    {
        var root = JsonNode.Parse(SaveWriter.Write(FullSave()))!;

        Assert.Empty(root["quests"]!.AsObject());
        Assert.Equal(SaveFormat.CurrentVersion, root["format_version"]!.GetValue<int>());
    }

    [Fact]
    public void WriteThenRead_EmptyPosition_RoundTripsWithNullPositionAndFacing()
    {
        var save = EmptyPositionSave();

        var json = SaveWriter.Write(save);
        var player = JsonNode.Parse(json)!["player"]!.AsObject();
        var read = SaveReader.Read(json);

        Assert.True(player.ContainsKey("position"));
        Assert.True(player.ContainsKey("facing_yaw"));
        Assert.Null(player["position"]);
        Assert.Null(player["facing_yaw"]);
        Assert.Equal(save, read);
        Assert.Null(read.Player.Position);
        Assert.Null(read.Player.FacingYaw);
        Assert.Empty(read.Player.Gear);
    }

    [Fact]
    public void Read_ValidLiteral_ReadsEveryValue()
    {
        var save = SaveReader.Read(ValidSave);

        Assert.Equal(1, save.FormatVersion);
        Assert.Equal("0.2.0", save.GameVersion);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567), save.SavedAtUtc);
        Assert.Equal(4980.25, save.PlaySeconds);
        Assert.Equal("somero", save.MapId);
        Assert.Equal("2026-11-01T10:00:00Z", save.MapGeneratedAt);
        Assert.Equal(new LocalPoint(12.5, 101.25, -40), save.Player.Position);
        Assert.Equal(1.5, save.Player.FacingYaw);
        Assert.Equal(120, save.Player.Money);
        Assert.Equal(["a", "b"], save.Player.Gear);
        Assert.Equal(2, save.Player.WantedLevel);
    }

    // Criterion 2

    [Theory]
    [InlineData("", "format_version")]
    [InlineData("", "game_version")]
    [InlineData("", "saved_at_utc")]
    [InlineData("", "play_seconds")]
    [InlineData("", "map_id")]
    [InlineData("", "map_generated_at")]
    [InlineData("", "player")]
    [InlineData("", "quests")]
    [InlineData("player", "position")]
    [InlineData("player", "facing_yaw")]
    [InlineData("player", "money")]
    [InlineData("player", "gear")]
    [InlineData("player", "wanted_level")]
    public void Read_MissingKey_IsRejectedWithItsPath(string parent, string key)
    {
        AssertRejected(root => At(root, parent).AsObject().Remove(key), Join(parent, key));
    }

    [Theory]
    [InlineData("", "extra")]
    [InlineData("player", "facing")]
    [InlineData("quests", "progress")]
    public void Read_UnknownKey_IsRejectedWithItsPath(string parent, string key)
    {
        AssertRejected(root => At(root, parent)[key] = 1, Join(parent, key));
    }

    [Theory]
    [InlineData("format_version", "\"1\"", "format_version")]
    [InlineData("format_version", "1.5", "format_version")]
    [InlineData("game_version", "1", "game_version")]
    [InlineData("saved_at_utc", "0", "saved_at_utc")]
    [InlineData("play_seconds", "\"10\"", "play_seconds")]
    [InlineData("map_id", "3", "map_id")]
    [InlineData("map_generated_at", "null", "map_generated_at")]
    [InlineData("player", "[]", "player")]
    [InlineData("quests", "[]", "quests")]
    [InlineData("player.position", "\"here\"", "player.position")]
    [InlineData("player.position", "[1, 2]", "player.position")]
    [InlineData("player.position", "[1, \"2\", 3]", "player.position[1]")]
    [InlineData("player.facing_yaw", "\"1\"", "player.facing_yaw")]
    [InlineData("player.money", "\"5\"", "player.money")]
    [InlineData("player.money", "1.5", "player.money")]
    [InlineData("player.gear", "\"a\"", "player.gear")]
    [InlineData("player.gear", "[\"a\", 1]", "player.gear[1]")]
    [InlineData("player.wanted_level", "true", "player.wanted_level")]
    public void Read_WrongType_IsRejectedWithItsPath(string key, string literal, string expectedPath)
    {
        AssertRejected(root => Set(root, key, JsonNode.Parse(literal)), expectedPath);
    }

    [Theory]
    [InlineData("play_seconds", "1e400", "play_seconds")]
    [InlineData("play_seconds", "-1e400", "play_seconds")]
    [InlineData("player.facing_yaw", "1e400", "player.facing_yaw")]
    [InlineData("player.position[1]", "1e400", "player.position[1]")]
    [InlineData("player.position[2]", "-1e400", "player.position[2]")]
    public void Read_NonFiniteNumber_IsRejectedWithItsPath(string key, string literal, string expectedPath)
    {
        // JSON has no NaN or infinity literal; a number beyond double's range parses to infinity.
        const double Sentinel = 987654.125;
        var json = Modify(root => Set(root, key, Sentinel)).Replace("987654.125", literal, StringComparison.Ordinal);

        var ex = Assert.Throws<SaveFormatException>(() => SaveReader.Read(json));

        Assert.Equal(expectedPath, ex.KeyPath);
        Assert.Contains("finite", ex.Message);
    }

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    public void Read_NonFiniteNumberAsString_IsRejectedWithItsPath(string literal)
    {
        AssertRejected(root => root["play_seconds"] = JsonNode.Parse(literal), "play_seconds");
    }

    [Fact]
    public void Read_NegativeMoney_IsRejected()
    {
        AssertRejected(root => root["player"]!["money"] = -1, "player.money");
    }

    [Fact]
    public void Read_NegativePlayTime_IsRejected()
    {
        AssertRejected(root => root["play_seconds"] = -0.5, "play_seconds");
    }

    [Theory]
    [InlineData(6)]
    [InlineData(-1)]
    public void Read_WantedLevelOutside0To5_IsRejected(int level)
    {
        AssertRejected(root => root["player"]!["wanted_level"] = level, "player.wanted_level");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Read_WantedLevelAtRangeEnds_IsAccepted(int level)
    {
        var save = SaveReader.Read(Modify(root => root["player"]!["wanted_level"] = level));

        Assert.Equal(level, save.Player.WantedLevel);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Read_BlankGearId_IsRejected(string id)
    {
        AssertRejected(root => root["player"]!["gear"] = new JsonArray("a", id), "player.gear[1]");
    }

    [Fact]
    public void Read_DuplicateGearId_IsRejectedAtTheRepeat()
    {
        AssertRejected(root => root["player"]!["gear"] = new JsonArray("a", "b", "a"), "player.gear[2]");
    }

    [Theory]
    [InlineData("Somero")]
    [InlineData("somero city")]
    [InlineData("somero--city")]
    [InlineData("")]
    public void Read_NonSlugMapId_IsRejected(string mapId)
    {
        AssertRejected(root => root["map_id"] = mapId, "map_id");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_EmptyGameVersion_IsRejected(string version)
    {
        AssertRejected(root => root["game_version"] = version, "game_version");
    }

    [Fact]
    public void Read_NullPositionWithFacing_IsRejected()
    {
        AssertRejected(root => root["player"]!["position"] = null, "player.facing_yaw");
    }

    [Fact]
    public void Read_PositionWithNullFacing_IsRejected()
    {
        AssertRejected(root => root["player"]!["facing_yaw"] = null, "player.facing_yaw");
    }

    [Fact]
    public void Read_BothPositionAndFacingNull_IsAccepted()
    {
        var save = SaveReader.Read(Modify(root =>
        {
            root["player"]!["position"] = null;
            root["player"]!["facing_yaw"] = null;
        }));

        Assert.Null(save.Player.Position);
        Assert.Null(save.Player.FacingYaw);
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(-3.1416)]
    public void Read_FacingOutsideMinusPiToPi_IsRejected(double yaw)
    {
        AssertRejected(root => root["player"]!["facing_yaw"] = yaw, "player.facing_yaw");
    }

    [Theory]
    [InlineData("2026-11-05T14:34:56+02:00")]
    [InlineData("2026-11-05 12:34:56Z")]
    [InlineData("yesterday")]
    public void Read_SavedAtNotAnIsoUtcTimestamp_IsRejected(string text)
    {
        AssertRejected(root => root["saved_at_utc"] = text, "saved_at_utc");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    public void Read_NewerFormatVersion_IsRejectedAsSavedByANewerVersion(int version)
    {
        var json = Modify(root => root["format_version"] = version);

        var ex = Assert.Throws<SaveFormatException>(() => SaveReader.Read(json));

        Assert.Equal("format_version", ex.KeyPath);
        Assert.True(ex.NewerVersion);
        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public void Read_OtherErrors_AreNotMarkedNewerVersion()
    {
        var ex = Assert.Throws<SaveFormatException>(() => SaveReader.Read(Modify(root => root["player"]!["money"] = -1)));

        Assert.False(ex.NewerVersion);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"format_version\": 1,")]
    [InlineData("[]")]
    public void Read_NotAJsonObject_IsRejectedWithoutKeyPath(string json)
    {
        var ex = Assert.Throws<SaveFormatException>(() => SaveReader.Read(json));

        Assert.Null(ex.KeyPath);
        Assert.False(ex.NewerVersion);
    }

    [Fact]
    public void Migrations_Version1File_PassesThroughUnchanged()
    {
        var calls = 0;
        var migrations = new SaveMigrations().Register(0, root =>
        {
            calls++;
            return root;
        });
        var node = JsonNode.Parse(ValidSave)!.AsObject();
        var before = node.ToJsonString();

        var applied = SaveMigrations.Default.Apply(node, SaveFormat.CurrentVersion);
        var read = SaveReader.Read(ValidSave, migrations);

        Assert.Equal(before, applied.ToJsonString());
        Assert.Equal(0, calls);
        Assert.Equal(SaveReader.Read(ValidSave), read);
    }

    [Fact]
    public void Migrations_RegisteredStepFrom0_IsAppliedToAVersion0File()
    {
        // A fake version 0 called the money "cash"; the step renames it.
        var version0 = Modify(root =>
        {
            root["format_version"] = 0;
            var player = root["player"]!.AsObject();
            player.Remove("money");
            player["cash"] = 75;
        });
        var calls = 0;
        var migrations = new SaveMigrations().Register(0, root =>
        {
            calls++;
            var player = root["player"]!.AsObject();
            player.Remove("cash", out var cash);
            player["money"] = cash;
            return root;
        });

        var save = SaveReader.Read(version0, migrations);

        Assert.Equal(1, calls);
        Assert.Equal(SaveFormat.CurrentVersion, save.FormatVersion);
        Assert.Equal(75, save.Player.Money);
        Assert.Equal([0], migrations.FromVersions);
    }

    [Fact]
    public void Migrations_Version0FileWithoutAStep_IsRejectedAtFormatVersion()
    {
        var version0 = Modify(root => root["format_version"] = 0);

        var ex = Assert.Throws<SaveFormatException>(() => SaveReader.Read(version0));

        Assert.Equal("format_version", ex.KeyPath);
        Assert.False(ex.NewerVersion);
    }

    [Fact]
    public void Migrations_DefaultChain_IsReadOnlyAndEmpty()
    {
        Assert.Empty(SaveMigrations.Default.FromVersions);
        Assert.Throws<InvalidOperationException>(() => SaveMigrations.Default.Register(0, root => root));
    }

    [Fact]
    public void Write_InvalidSave_Throws()
    {
        var save = FullSave() with { Player = new SavePlayer(null, null, -5, [], 0) };

        var ex = Assert.Throws<ArgumentException>(() => SaveWriter.Write(save));

        Assert.Contains("player.money", ex.Message);
    }

    private static void AssertRejected(Action<JsonNode> change, string keyPath)
    {
        var json = Modify(change);

        var ex = Assert.Throws<SaveFormatException>(() => SaveReader.Read(json));

        Assert.Equal(keyPath, ex.KeyPath);
        Assert.Contains($"'{keyPath}'", ex.Message);
    }

    private static IEnumerable<string> AllKeys(JsonObject node, string path)
    {
        foreach (var (key, value) in node)
        {
            var keyPath = Join(path, key);
            yield return keyPath;
            if (value is JsonObject child)
            {
                foreach (var nested in AllKeys(child, keyPath))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string Join(string parent, string key) => parent.Length == 0 ? key : $"{parent}.{key}";

    /// <summary>The node at a path such as <c>player</c> ("" is the root).</summary>
    private static JsonNode At(JsonNode root, string path) =>
        path.Length == 0 ? root : path.Split('.').Aggregate(root, (node, name) => node[name]!);

    /// <summary>Sets the value at a path such as <c>player.money</c> or <c>player.position[1]</c>.</summary>
    private static void Set(JsonNode root, string path, JsonNode? value)
    {
        var dot = path.LastIndexOf('.');
        var parent = dot < 0 ? root : At(root, path[..dot]);
        var last = dot < 0 ? path : path[(dot + 1)..];
        var bracket = last.IndexOf('[', StringComparison.Ordinal);
        if (bracket < 0)
        {
            parent[last] = value;
            return;
        }

        var index = int.Parse(last[(bracket + 1)..^1], System.Globalization.CultureInfo.InvariantCulture);
        parent[last[..bracket]]![index] = value;
    }

    private static string Modify(Action<JsonNode> change)
    {
        var root = JsonNode.Parse(ValidSave)!;
        change(root);
        return root.ToJsonString();
    }
}
