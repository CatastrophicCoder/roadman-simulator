using Roadman.Core.Maps;
using Roadman.Core.Saves;
using Roadman.Core.Tests.Items;

namespace Roadman.Core.Tests.Saves;

/// <summary>
/// <see cref="SaveFactory"/>: the rules of <c>docs/specs/menu-settings-save.md</c>, "Saving" (manual saves need a
/// living player, the autosave is written in every death state with an empty position, <c>generated_at</c> and the
/// game version fall back to <c>unknown</c> and <c>dev</c>), and criteria 1 (round trip), 18 (the post-death
/// autosave) and 23 (the version in a save).
/// </summary>
public class SaveFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 34, 56, TimeSpan.Zero);

    private static PlayerState AlivePlayer()
    {
        var player = new PlayerState(TestCatalogs.Plain);
        player.SetPosition(new LocalPoint(12.5, 101.25, -40.125));
        player.AddMoney(120);
        player.AddGear("b");
        player.AddGear("a");
        player.SetWantedLevel(2);
        return player;
    }

    private static SaveGame? Make(
        bool autosave = false,
        bool canSave = true,
        PlayerState? player = null,
        double? facingYaw = 1.25,
        string mapId = "somero",
        string? generatedAt = "2026-11-01T10:00:00Z",
        string? gameVersion = "0.2.0",
        double playSeconds = 4980.25) =>
        SaveFactory.Make(autosave, canSave, player ?? AlivePlayer(), facingYaw, mapId, generatedAt, gameVersion, Now, playSeconds);

    [Fact]
    public void Make_ManualSaveWhenCantSave_IsNull()
    {
        Assert.Null(Make(autosave: false, canSave: false));
    }

    [Fact]
    public void Make_ManualSaveWhenCanSave_HasThePositionFacingAndState()
    {
        var save = Make(autosave: false, canSave: true);

        Assert.NotNull(save);
        Assert.Equal(new LocalPoint(12.5, 101.25, -40.125), save.Player.Position);
        Assert.Equal(1.25, save.Player.FacingYaw);
        Assert.Equal(120, save.Player.Money);
        Assert.Equal(["a", "b"], save.Player.Gear);
        Assert.Equal(2, save.Player.WantedLevel);
    }

    [Fact]
    public void Make_AutosaveWhileAlive_HasThePositionAndFacing()
    {
        var save = Make(autosave: true, canSave: true);

        Assert.NotNull(save);
        Assert.Equal(new LocalPoint(12.5, 101.25, -40.125), save.Player.Position);
        Assert.Equal(1.25, save.Player.FacingYaw);
    }

    [Fact]
    public void Ac18_AutosaveWhileNotAlive_HasEmptyPositionAndFacingAndThePostDeathState()
    {
        var player = AlivePlayer();
        player.Die();

        var save = Make(autosave: true, canSave: false, player: player, facingYaw: 0.75);

        Assert.NotNull(save);
        Assert.Null(save.Player.Position);
        Assert.Null(save.Player.FacingYaw);
        Assert.Equal(0, save.Player.Money);
        Assert.Empty(save.Player.Gear);
        Assert.Equal(0, save.Player.WantedLevel);
        Assert.Null(save.Problem());
    }

    [Fact]
    public void Make_AliveWithoutAPlayerNode_HasThePositionButNoFacing()
    {
        var save = Make(canSave: true, facingYaw: null);

        Assert.NotNull(save);
        Assert.NotNull(save.Player.Position);
        Assert.Null(save.Player.FacingYaw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public void Make_BlankOrMissingGeneratedAt_IsUnknown(string? generatedAt)
    {
        var save = Make(generatedAt: generatedAt);

        Assert.Equal(SaveFactory.UnknownGeneratedAt, save!.MapGeneratedAt);
        Assert.Equal("unknown", save.MapGeneratedAt);
    }

    [Fact]
    public void Make_GeneratedAt_IsCarriedAsWritten()
    {
        Assert.Equal("2026-11-01T10:00:00Z", Make()!.MapGeneratedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ac23_Make_BlankOrMissingGameVersion_IsDev(string? version)
    {
        Assert.Equal("dev", Make(gameVersion: version)!.GameVersion);
    }

    [Fact]
    public void Ac23_Make_GameVersion_IsCarriedAsGiven()
    {
        Assert.Equal("0.2.0", Make(gameVersion: "0.2.0")!.GameVersion);
    }

    [Theory]
    [InlineData(null, "dev")]
    [InlineData("", "dev")]
    [InlineData("  ", "dev")]
    [InlineData("dev", "dev")]
    [InlineData("0.1.0-test1", "0.1.0-test1")]
    public void GameVersionOrDev_BlankGivesDev_ElseTheValue(string? version, string expected)
    {
        Assert.Equal(expected, SaveFactory.GameVersionOrDev(version));
        Assert.Equal("dev", SaveFactory.DevVersion);
    }

    [Fact]
    public void Make_CarriesPlaySecondsMapIdNowFormatVersionAndEmptyQuests()
    {
        var save = Make(mapId: "test_area", playSeconds: 90420.5)!;

        Assert.Equal(90420.5, save.PlaySeconds);
        Assert.Equal("test_area", save.MapId);
        Assert.Equal(Now, save.SavedAtUtc);
        Assert.Equal(SaveFormat.CurrentVersion, save.FormatVersion);
        Assert.Equal(SaveQuests.Empty, save.Quests);
    }

    [Fact]
    public void Make_NullPlayer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SaveFactory.Make(true, true, null!, 0, "somero", null, null, Now, 0));
    }

    [Fact]
    public void Make_NullMapId_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SaveFactory.Make(true, true, AlivePlayer(), 0, null!, null, null, Now, 0));
    }

    [Fact]
    public void Make_NullPlayerOrMapId_ThrowsAlsoForAManualSaveThatCantSave()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SaveFactory.Make(false, false, null!, 0, "somero", null, null, Now, 0));
        Assert.Throws<ArgumentNullException>(() =>
            SaveFactory.Make(false, false, AlivePlayer(), 0, null!, null, null, Now, 0));
    }

    [Fact]
    public void Ac1_AliveSave_RoundTripsThroughWriterAndReader()
    {
        var save = Make()!;

        var read = SaveReader.Read(SaveWriter.Write(save));

        Assert.Equal(save, read);
    }

    [Fact]
    public void Ac1_Ac18_PostDeathAutosaveWithDefaults_RoundTripsThroughWriterAndReader()
    {
        var player = AlivePlayer();
        player.Die();
        var save = Make(autosave: true, canSave: false, player: player, generatedAt: null, gameVersion: null)!;

        var read = SaveReader.Read(SaveWriter.Write(save));

        Assert.Equal(save, read);
        Assert.Null(read.Player.Position);
        Assert.Equal("unknown", read.MapGeneratedAt);
        Assert.Equal("dev", read.GameVersion);
    }

    // docs/specs/inventory.md, "Save format, version 2": slots are saved; an autosave during a death has none.

    [Fact]
    public void Inventory_ManualSave_HoldsTheEquippedItems_AndRoundTrips()
    {
        var player = new PlayerState(TestCatalogs.Mixed);
        player.SetPosition(new LocalPoint(1, 2, 3));
        player.AddGear("crowbar");
        player.AddGear("suit");
        player.AddGear("phone");
        player.Equip("crowbar");
        player.Equip("suit");

        var save = Make(player: player)!;

        Assert.Equal(new SaveEquipped("crowbar", "suit"), save.Player.Equipped);
        Assert.Equal(save, SaveReader.Read(SaveWriter.Write(save)));
    }

    [Fact]
    public void Inventory_AutosaveAfterDeath_HasNoGearAndEmptySlots()
    {
        var player = new PlayerState(TestCatalogs.Mixed);
        player.AddGear("bat");
        player.Equip("bat");
        player.Die();

        var save = Make(autosave: true, canSave: false, player: player)!;

        Assert.Empty(save.Player.Gear);
        Assert.Equal(SaveEquipped.Empty, save.Player.Equipped);
        Assert.Null(save.Problem());
    }
}
