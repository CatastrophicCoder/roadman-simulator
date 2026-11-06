using Roadman.Core.Items;
using Roadman.Core.Maps;
using Roadman.Core.Saves;

namespace Roadman.Core.Tests.Items;

/// <summary><see cref="SaveItemCheck.Clean"/>: <c>docs/specs/inventory.md</c>, "Loading: unknown items" and acceptance criterion 11.</summary>
public class SaveItemCheckTests
{
    private static readonly LocalPoint Position = new(12.5, 101.25, -40);

    private static SavePlayer Player(IEnumerable<string> gear, string? weapon = null, string? outfit = null) =>
        new(Position, 1.5, 120, gear, new SaveEquipped(weapon, outfit), 2);

    [Fact]
    public void Ac11_UnknownIdHeldAndEquippedInWeapon_IsDroppedWithTheSlotEmptied_AndReported()
    {
        var player = Player(["crowbar", "laser_saw", "phone", "tracksuit"], weapon: "laser_saw", outfit: "tracksuit");

        var result = SaveItemCheck.Clean(player, ItemCatalog.Default);

        Assert.Equal(["crowbar", "phone", "tracksuit"], result.Player.Gear);
        Assert.Null(result.Player.Equipped.Weapon);
        Assert.Equal("tracksuit", result.Player.Equipped.Outfit);
        Assert.Equal(["laser_saw"], result.RemovedIds);
        Assert.Empty(result.Unequipped); // an unknown equipped id is reported as removed, not as unequipped
        Assert.True(result.Changed);
    }

    [Fact]
    public void Ac11_KnownClothingInWeapon_IsUnequippedAndKept_AndReported()
    {
        var player = Player(["hoodie", "phone"], weapon: "hoodie");

        var result = SaveItemCheck.Clean(player, ItemCatalog.Default);

        Assert.Equal(["hoodie", "phone"], result.Player.Gear);
        Assert.Equal(SaveEquipped.Empty, result.Player.Equipped);
        Assert.Empty(result.RemovedIds);
        Assert.Equal([new SaveItemCheckUnequip(EquipSlot.Weapon, "hoodie")], result.Unequipped);
        Assert.True(result.Changed);
    }

    [Theory]
    [InlineData(null, "crowbar", EquipSlot.Outfit, "crowbar")]
    [InlineData("phone", null, EquipSlot.Weapon, "phone")]
    [InlineData(null, "bike_lock", EquipSlot.Outfit, "bike_lock")]
    public void Ac11_KnownItemThatDoesNotFitItsSlot_IsUnequippedAndKept(string? weapon, string? outfit, EquipSlot slot, string id)
    {
        var player = Player(["bike_lock", "crowbar", "phone"], weapon, outfit);

        var result = SaveItemCheck.Clean(player, ItemCatalog.Default);

        Assert.Equal(["bike_lock", "crowbar", "phone"], result.Player.Gear);
        Assert.Equal(SaveEquipped.Empty, result.Player.Equipped);
        Assert.Equal([new SaveItemCheckUnequip(slot, id)], result.Unequipped);
        Assert.Empty(result.RemovedIds);
    }

    [Fact]
    public void Ac11_CleanSave_ComesBackEqualWithNothingReported()
    {
        var player = Player(["baseball_bat", "hoodie", "phone"], weapon: "baseball_bat", outfit: "hoodie");

        var result = SaveItemCheck.Clean(player, ItemCatalog.Default);

        Assert.Equal(player, result.Player);
        Assert.Empty(result.RemovedIds);
        Assert.Empty(result.Unequipped);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Ac11_CleanSaveWithNoGear_ComesBackEqualWithNothingReported()
    {
        var player = new SavePlayer(null, null, 0, [], SaveEquipped.Empty, 0);

        var result = SaveItemCheck.Clean(player, ItemCatalog.Default);

        Assert.Equal(player, result.Player);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Ac11_SeveralUnknownIds_AreReportedSortedOrdinal()
    {
        var player = Player(["zz_top_hat", "phone", "a_thing", "Zebra_coat"]);

        var result = SaveItemCheck.Clean(player, ItemCatalog.Default);

        Assert.Equal(["phone"], result.Player.Gear);
        Assert.Equal(["Zebra_coat", "a_thing", "zz_top_hat"], result.RemovedIds);
    }

    [Fact]
    public void Ac11_UnknownAndMisplaced_AreBothReported()
    {
        var player = Player(["crowbar", "mystery_item"], weapon: "crowbar", outfit: "mystery_item");
        var misplaced = Player(["crowbar", "mystery_item", "tracksuit"], weapon: "tracksuit", outfit: "mystery_item");

        var first = SaveItemCheck.Clean(player, ItemCatalog.Default);
        var second = SaveItemCheck.Clean(misplaced, ItemCatalog.Default);

        Assert.Equal(new SaveEquipped("crowbar", null), first.Player.Equipped);
        Assert.Equal(["mystery_item"], first.RemovedIds);
        Assert.Empty(first.Unequipped);

        Assert.Equal(["crowbar", "tracksuit"], second.Player.Gear);
        Assert.Equal(SaveEquipped.Empty, second.Player.Equipped);
        Assert.Equal(["mystery_item"], second.RemovedIds);
        Assert.Equal([new SaveItemCheckUnequip(EquipSlot.Weapon, "tracksuit")], second.Unequipped);
        Assert.True(second.Changed);
    }

    [Fact]
    public void Ac11_OtherValues_AreKeptAsTheyWere()
    {
        var player = Player(["crowbar", "mystery_item"], weapon: "mystery_item");

        var result = SaveItemCheck.Clean(player, ItemCatalog.Default);

        Assert.Equal(Position, result.Player.Position);
        Assert.Equal(1.5, result.Player.FacingYaw);
        Assert.Equal(120, result.Player.Money);
        Assert.Equal(2, result.Player.WantedLevel);
    }

    [Fact]
    public void Ac11_CleanedPlayer_IsValid_AndRestoresIntoPlayerState()
    {
        var player = Player(["crowbar", "mystery_item", "tracksuit"], weapon: "tracksuit", outfit: "mystery_item");

        var result = SaveItemCheck.Clean(player, ItemCatalog.Default);
        var state = new PlayerState(ItemCatalog.Default);
        state.Restore(result.Player);

        Assert.Null(result.Player.Problem(requireFacingPair: true));
        Assert.Equal(new HashSet<string> { "crowbar", "tracksuit" }, state.Gear);
        Assert.Empty(state.Equipped);
    }

    [Fact]
    public void Ac11_UsesTheGivenCatalogue()
    {
        // "bat" and "suit" exist only in the test catalogue; the default one would drop them.
        var player = Player(["bat", "suit"], weapon: "bat", outfit: "suit");

        Assert.False(SaveItemCheck.Clean(player, TestCatalogs.Mixed).Changed);
        Assert.Equal(["bat", "suit"], SaveItemCheck.Clean(player, ItemCatalog.Default).RemovedIds);
    }

    [Fact]
    public void Clean_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => SaveItemCheck.Clean(null!, ItemCatalog.Default));
        Assert.Throws<ArgumentNullException>(() => SaveItemCheck.Clean(Player([]), null!));
    }
}
