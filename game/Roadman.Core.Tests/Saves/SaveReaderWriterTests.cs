using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Roadman.Core.Maps;
using Roadman.Core.Saves;

namespace Roadman.Core.Tests.Saves;

/// <summary>
/// <see cref="SaveWriter"/>, <see cref="SaveReader"/> and <see cref="SaveMigrations"/>:
/// <c>docs/specs/menu-settings-save.md</c>, acceptance criteria 1 and 2; save format version 2 (the <c>equipped</c>
/// object) and its migration: <c>docs/specs/inventory.md</c>, acceptance criteria 8 and 9 (tests named
/// <c>Ac8_Inventory_...</c> and <c>Ac9_Inventory_...</c>).
/// </summary>
public class SaveReaderWriterTests
{
    // A valid version-2 save with a position, two gear ids (not in ordinal order), "a" equipped as the weapon (the
    // format doesn't know the catalogue) and wanted level 2.
    internal const string ValidSave = """
        {
          "format_version": 2,
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
            "equipped": { "weapon": "a", "outfit": null },
            "wanted_level": 2
          },
          "quests": {}
        }
        """;

    // A valid version-1 save, as the game wrote it before the inventory: no "equipped".
    internal const string Version1Save = """
        {
          "format_version": 1,
          "game_version": "0.1.0",
          "saved_at_utc": "2026-11-05T12:34:56.1234567Z",
          "play_seconds": 4980.25,
          "map_id": "somero",
          "map_generated_at": "2026-11-01T10:00:00Z",
          "player": {
            "position": [12.5, 101.25, -40],
            "facing_yaw": 1.5,
            "money": 120,
            "gear": ["phone", "crowbar", "tracksuit"],
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
            new SavePlayer(new LocalPoint(12.5, 101.25, -40.125), 1.25, 120, ["kit_b", "Kit_a", "kit_a"], new SaveEquipped("kit_b", "Kit_a"), 3),
            SaveQuests.Empty);

    internal static SaveGame EmptyPositionSave() =>
        new(
            SaveFormat.CurrentVersion,
            "dev",
            new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero),
            0,
            "test_area",
            "2026-11-01T10:00:00Z",
            new SavePlayer(null, null, 0, [], SaveEquipped.Empty, 0),
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
        Assert.Equal(new SaveEquipped("kit_b", "Kit_a"), read.Player.Equipped);
    }

    [Fact]
    public void Ac8_Inventory_WriteThenRead_ThreeGearIdsWithBothSlotsFull_GivesEqualValue()
    {
        var save = FullSave() with
        {
            Player = new SavePlayer(new LocalPoint(1, 2, 3), 0.5, 120, ["crowbar", "phone", "tracksuit"], new SaveEquipped("crowbar", "tracksuit"), 0),
        };

        var read = SaveReader.Read(SaveWriter.Write(save));

        Assert.Equal(save, read);
        Assert.Equal("crowbar", read.Player.Equipped.Weapon);
        Assert.Equal("tracksuit", read.Player.Equipped.Outfit);
        Assert.Equal(["crowbar", "phone", "tracksuit"], read.Player.Gear);
    }

    [Fact]
    public void Ac8_Inventory_WriteThenRead_BothSlotsNull_GivesEqualValue()
    {
        var save = FullSave() with
        {
            Player = new SavePlayer(new LocalPoint(1, 2, 3), 0.5, 120, ["crowbar", "phone", "tracksuit"], SaveEquipped.Empty, 0),
        };

        var json = SaveWriter.Write(save);
        var equipped = JsonNode.Parse(json)!["player"]!["equipped"]!.AsObject();
        var read = SaveReader.Read(json);

        Assert.Equal(save, read);
        Assert.Equal(SaveEquipped.Empty, read.Player.Equipped);
        Assert.Equal(["weapon", "outfit"], equipped.Select(p => p.Key));
        Assert.Null(equipped["weapon"]);
        Assert.Null(equipped["outfit"]);
    }

    [Fact]
    public void Ac8_Inventory_Write_EquippedIsAnObjectWithWeaponThenOutfit_AfterGearBeforeWantedLevel()
    {
        var root = JsonNode.Parse(SaveWriter.Write(FullSave()))!;
        var player = root["player"]!.AsObject();
        var equipped = player["equipped"]!.AsObject();

        Assert.Equal(["position", "facing_yaw", "money", "gear", "equipped", "wanted_level"], player.Select(p => p.Key));
        Assert.Equal(["weapon", "outfit"], equipped.Select(p => p.Key));
        Assert.Equal("kit_b", equipped["weapon"]!.GetValue<string>());
        Assert.Equal("Kit_a", equipped["outfit"]!.GetValue<string>());
    }

    [Fact]
    public void Ac8_Inventory_SavesWithDifferentEquipment_AreNotEqual()
    {
        var a = new SavePlayer(null, null, 0, ["crowbar", "hoodie"], new SaveEquipped("crowbar", null), 0);
        var b = new SavePlayer(null, null, 0, ["crowbar", "hoodie"], new SaveEquipped("crowbar", "hoodie"), 0);
        var c = new SavePlayer(null, null, 0, ["crowbar", "hoodie"], new SaveEquipped("crowbar", null), 0);

        Assert.NotEqual(a, b);
        Assert.Equal(a, c);
        Assert.Equal(a.GetHashCode(), c.GetHashCode());
    }

    [Fact]
    public void Ac8_Inventory_SavePlayer_NullEquipped_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SavePlayer(null, null, 0, [], null!, 0));
    }

    [Theory]
    [InlineData(" ", null, "equipped.weapon")]
    [InlineData("", null, "equipped.weapon")]
    [InlineData(null, "\t", "equipped.outfit")]
    [InlineData("sword", null, "equipped.weapon")]
    [InlineData(null, "hat", "equipped.outfit")]
    public void Ac8_Inventory_SavePlayerProblem_BlankOrNotInGear_NamesTheSlotKey(string? weapon, string? outfit, string keyPath)
    {
        var player = new SavePlayer(null, null, 0, ["crowbar", "hoodie"], new SaveEquipped(weapon, outfit), 0);

        var problem = player.Problem(requireFacingPair: true);

        Assert.NotNull(problem);
        Assert.Equal(keyPath, problem.Value.KeyPath);
    }

    [Fact]
    public void Ac8_Inventory_SavePlayerProblem_NotInGear_SaysSo()
    {
        var player = new SavePlayer(null, null, 0, ["crowbar"], new SaveEquipped("sword", null), 0);

        var problem = player.Problem(requireFacingPair: true);

        Assert.NotNull(problem);
        Assert.Contains("'sword' is not in gear", problem.Value.Problem);
    }

    [Fact]
    public void Ac8_Inventory_SameItemInGearAndSlot_IsValid()
    {
        var player = new SavePlayer(null, null, 0, ["crowbar", "hoodie"], new SaveEquipped("crowbar", "hoodie"), 0);

        Assert.Null(player.Problem(requireFacingPair: true));
    }

    [Fact]
    public void Ac8_Inventory_Write_EquippedNotInGear_Throws()
    {
        var save = FullSave() with
        {
            Player = new SavePlayer(null, null, 0, ["crowbar"], new SaveEquipped("crowbar", "hoodie"), 0),
        };

        var ex = Assert.Throws<ArgumentException>(() => SaveWriter.Write(save));

        Assert.Contains("player.equipped.outfit", ex.Message);
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
        Assert.Contains("player.equipped", keys);
        Assert.Contains("player.equipped.weapon", keys);
        Assert.Contains("player.equipped.outfit", keys);
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

        Assert.Equal(2, save.FormatVersion);
        Assert.Equal("0.2.0", save.GameVersion);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567), save.SavedAtUtc);
        Assert.Equal(4980.25, save.PlaySeconds);
        Assert.Equal("somero", save.MapId);
        Assert.Equal("2026-11-01T10:00:00Z", save.MapGeneratedAt);
        Assert.Equal(new LocalPoint(12.5, 101.25, -40), save.Player.Position);
        Assert.Equal(1.5, save.Player.FacingYaw);
        Assert.Equal(120, save.Player.Money);
        Assert.Equal(["a", "b"], save.Player.Gear);
        Assert.Equal(new SaveEquipped("a", null), save.Player.Equipped);
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
    [InlineData("player", "equipped")] // inventory criterion 8
    [InlineData("player.equipped", "weapon")] // inventory criterion 8
    [InlineData("player.equipped", "outfit")] // inventory criterion 8
    public void Read_MissingKey_IsRejectedWithItsPath(string parent, string key)
    {
        AssertRejected(root => At(root, parent).AsObject().Remove(key), Join(parent, key));
    }

    [Theory]
    [InlineData("", "extra")]
    [InlineData("player", "facing")]
    [InlineData("quests", "progress")]
    [InlineData("player.equipped", "hat")] // inventory criterion 8
    [InlineData("player.equipped", "Weapon")] // inventory criterion 8
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
    [InlineData("player.equipped", "null", "player.equipped")] // inventory criterion 8
    [InlineData("player.equipped", "[]", "player.equipped")]
    [InlineData("player.equipped", "\"a\"", "player.equipped")]
    [InlineData("player.equipped.weapon", "1", "player.equipped.weapon")]
    [InlineData("player.equipped.weapon", "true", "player.equipped.weapon")]
    [InlineData("player.equipped.outfit", "[\"a\"]", "player.equipped.outfit")]
    [InlineData("player.equipped.outfit", "{}", "player.equipped.outfit")]
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
    [InlineData(3)] // inventory criterion 8
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
    public void Migrations_CurrentVersionFile_PassesThroughUnchanged()
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
        // A fake version 0 called the money "cash"; the step renames it. The chain then needs a 1 -> 2 step too.
        var version0 = Modify(root =>
        {
            root["format_version"] = 0;
            var player = root["player"]!.AsObject();
            player.Remove("money");
            player.Remove("equipped");
            player["cash"] = 75;
        });
        var calls = new List<int>();
        var migrations = new SaveMigrations()
            .Register(1, root =>
            {
                calls.Add(1);
                root["player"]!["equipped"] = new JsonObject { ["weapon"] = null, ["outfit"] = null };
                return root;
            })
            .Register(0, root =>
            {
                calls.Add(0);
                var player = root["player"]!.AsObject();
                player.Remove("cash", out var cash);
                player["money"] = cash;
                return root;
            });

        var save = SaveReader.Read(version0, migrations);

        Assert.Equal([0, 1], calls);
        Assert.Equal(SaveFormat.CurrentVersion, save.FormatVersion);
        Assert.Equal(75, save.Player.Money);
        Assert.Equal([0, 1], migrations.FromVersions);
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
    public void Ac9_Inventory_Migrations_DefaultChain_IsReadOnlyWithTheStepFrom1()
    {
        Assert.Equal(2, SaveFormat.CurrentVersion);
        Assert.Equal([1], SaveMigrations.Default.FromVersions);
        Assert.Throws<InvalidOperationException>(() => SaveMigrations.Default.Register(0, root => root));
    }

    [Fact]
    public void Ac9_Inventory_Version1SaveWithGear_ReadsAsVersion2_SameGear_BothSlotsNull_AllElseUnchanged()
    {
        var expected = new SaveGame(
            2,
            "0.1.0",
            new DateTimeOffset(2026, 11, 5, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567),
            4980.25,
            "somero",
            "2026-11-01T10:00:00Z",
            new SavePlayer(new LocalPoint(12.5, 101.25, -40), 1.5, 120, ["crowbar", "phone", "tracksuit"], SaveEquipped.Empty, 2),
            SaveQuests.Empty);

        var viaDefault = SaveReader.Read(Version1Save);
        var viaExplicitDefault = SaveReader.Read(Version1Save, SaveMigrations.Default);

        Assert.Equal(expected, viaDefault);
        Assert.Equal(expected, viaExplicitDefault);
        Assert.Equal(SaveEquipped.Empty, viaDefault.Player.Equipped);
    }

    [Fact]
    public void Ac9_Inventory_DefaultStep1To2_AddsOnlyTheNullEquippedObjectAndTheVersion()
    {
        var original = JsonNode.Parse(Version1Save)!.AsObject();
        var node = JsonNode.Parse(Version1Save)!.AsObject();

        var migrated = SaveMigrations.Default.Apply(node, 1);

        Assert.Equal(2, migrated["format_version"]!.GetValue<int>());
        var equipped = migrated["player"]!["equipped"]!.AsObject();
        Assert.Equal(["weapon", "outfit"], equipped.Select(p => p.Key));
        Assert.Null(equipped["weapon"]);
        Assert.Null(equipped["outfit"]);

        // Everything else is as it was.
        migrated["player"]!.AsObject().Remove("equipped");
        migrated["format_version"] = 1;
        Assert.True(JsonNode.DeepEquals(original, migrated), migrated.ToJsonString());
    }

    [Fact]
    public void Ac9_Inventory_Version1SaveWithoutGear_ReadsWithEmptySlots()
    {
        var json = Version1Save.Replace("[\"phone\", \"crowbar\", \"tracksuit\"]", "[]", StringComparison.Ordinal);

        var save = SaveReader.Read(json);

        Assert.Empty(save.Player.Gear);
        Assert.Equal(SaveEquipped.Empty, save.Player.Equipped);
    }

    [Fact]
    public void Ac9_Inventory_Version1SaveWithPlayerNotAnObject_IsRejectedByTheReaderAtPlayer()
    {
        var root = JsonNode.Parse(Version1Save)!;
        root["player"] = new JsonArray(1, 2);

        var ex = Assert.Throws<SaveFormatException>(() => SaveReader.Read(root.ToJsonString()));

        Assert.Equal("player", ex.KeyPath);
        Assert.False(ex.NewerVersion);
    }

    [Fact]
    public void Ac9_Inventory_Version1SaveWithAnInvalidValue_IsStillRejectedAfterMigration()
    {
        var root = JsonNode.Parse(Version1Save)!;
        root["player"]!["money"] = -1;

        var ex = Assert.Throws<SaveFormatException>(() => SaveReader.Read(root.ToJsonString()));

        Assert.Equal("player.money", ex.KeyPath);
    }

    // Inventory criterion 8: the reader's equipped rules

    [Theory]
    [InlineData("weapon")]
    [InlineData("outfit")]
    public void Ac8_Inventory_Read_RepeatedSlotKey_IsRejectedWithItsPath(string key)
    {
        var json = ValidSave.Replace(
            "\"equipped\": { \"weapon\": \"a\", \"outfit\": null }",
            key == "weapon"
                ? "\"equipped\": { \"weapon\": \"a\", \"weapon\": \"a\", \"outfit\": null }"
                : "\"equipped\": { \"weapon\": \"a\", \"outfit\": null, \"outfit\": null }",
            StringComparison.Ordinal);
        Assert.NotEqual(ValidSave, json);

        var ex = Assert.Throws<SaveFormatException>(() => SaveReader.Read(json));

        Assert.Equal($"player.equipped.{key}", ex.KeyPath);
    }

    [Fact]
    public void Ac8_Inventory_Read_RepeatedEquippedKey_IsRejectedWithItsPath()
    {
        var json = ValidSave.Replace(
            "\"equipped\": { \"weapon\": \"a\", \"outfit\": null }",
            "\"equipped\": { \"weapon\": \"a\", \"outfit\": null }, \"equipped\": { \"weapon\": null, \"outfit\": null }",
            StringComparison.Ordinal);
        Assert.NotEqual(ValidSave, json);

        var ex = Assert.Throws<SaveFormatException>(() => SaveReader.Read(json));

        Assert.Equal("player.equipped", ex.KeyPath);
    }

    [Theory]
    [InlineData("weapon", "")]
    [InlineData("weapon", " ")]
    [InlineData("outfit", "")]
    [InlineData("outfit", "\t")]
    public void Ac8_Inventory_Read_BlankEquippedId_IsRejectedWithItsPath(string key, string id)
    {
        AssertRejected(root => root["player"]!["equipped"]![key] = id, $"player.equipped.{key}");
    }

    [Theory]
    [InlineData("weapon", "c")]
    [InlineData("outfit", "c")]
    [InlineData("outfit", "A")]
    public void Ac8_Inventory_Read_EquippedIdNotInGear_IsRejectedWithItsPath(string key, string id)
    {
        AssertRejected(root => root["player"]!["equipped"]![key] = id, $"player.equipped.{key}");
    }

    [Fact]
    public void Ac8_Inventory_Read_UnknownIdAndClothingInWeapon_AreAccepted()
    {
        // The format doesn't know the catalogue: an unknown id and a clothing item in the weapon slot are the load
        // cleanup's to handle (SaveItemCheck), not format errors.
        var save = SaveReader.Read(Modify(root =>
        {
            root["player"]!["gear"] = new JsonArray("hoodie", "mystery_item");
            root["player"]!["equipped"] = new JsonObject { ["weapon"] = "hoodie", ["outfit"] = "mystery_item" };
        }));

        Assert.Equal(new SaveEquipped("hoodie", "mystery_item"), save.Player.Equipped);
        Assert.Equal(["hoodie", "mystery_item"], save.Player.Gear);
    }

    [Fact]
    public void Ac8_Inventory_Read_KeysInAnyOrder_AreAccepted()
    {
        var save = SaveReader.Read(Modify(root =>
            root["player"]!["equipped"] = new JsonObject { ["outfit"] = "b", ["weapon"] = null }));

        Assert.Equal(new SaveEquipped(null, "b"), save.Player.Equipped);
    }

    [Fact]
    public void Write_InvalidSave_Throws()
    {
        var save = FullSave() with { Player = new SavePlayer(null, null, -5, [], SaveEquipped.Empty, 0) };

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
