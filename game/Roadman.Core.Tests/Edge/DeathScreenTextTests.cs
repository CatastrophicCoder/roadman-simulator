using System.Globalization;
using Roadman.Core.Edge;
using Roadman.Core.Items;
using static Roadman.Core.Tests.Items.TestCatalogs;

namespace Roadman.Core.Tests.Edge;

/// <summary>
/// <see cref="DeathScreenText"/>: <c>docs/specs/world-edge-game.md</c>, acceptance criterion 5, as amended by
/// <c>docs/specs/inventory.md</c>, acceptance criterion 7 (display names, equipped items first).
/// </summary>
public class DeathScreenTextTests
{
    /// <summary>
    /// Ids and names in different orders: within each category the id order is the reverse of the name order, and
    /// "apple box" comes before "Key" only when case is ignored.
    /// </summary>
    private static readonly ItemCatalog Shuffled = new(
    [
        Item("a_stick", ItemCategory.Weapon, "Stick"),
        Item("b_axe", ItemCategory.Weapon, "Axe"),
        Item("a_vest", ItemCategory.Clothing, "Vest"),
        Item("z_cap", ItemCategory.Clothing, "Cap"),
        Item("b_key", ItemCategory.Other, "Key"),
        Item("z_box", ItemCategory.Other, "apple box"),
    ]);

    private static DeathLosses Losses(int money, string[] gear, string? weapon = null, string? outfit = null)
    {
        var equipped = new Dictionary<EquipSlot, string>();
        if (weapon is not null)
        {
            equipped[EquipSlot.Weapon] = weapon;
        }

        if (outfit is not null)
        {
            equipped[EquipSlot.Outfit] = outfit;
        }

        return new DeathLosses(money, gear.Order(StringComparer.Ordinal).ToList(), equipped);
    }

    [Fact]
    public void Ac7_SpecExample_EquippedFirstThenTheRestByCategoryAndName()
    {
        var losses = Losses(120, ["baseball_bat", "crowbar", "phone", "tracksuit"], weapon: "crowbar", outfit: "tracksuit");

        var lines = DeathScreenText.Format(losses, ItemCatalog.Default);

        Assert.Equal("The Earth is flat", lines.Title);
        Assert.Equal("You fell off the edge of the world", lines.Subtitle);
        Assert.Equal("Money lost: 120 · Gear lost: Crowbar (equipped), Tracksuit (equipped), Baseball bat, Phone", lines.Losses);
    }

    [Fact]
    public void Ac7_EquippedItems_AreInSlotOrder_WhateverTheDictionaryOrder()
    {
        var equipped = new Dictionary<EquipSlot, string> { [EquipSlot.Outfit] = "hoodie", [EquipSlot.Weapon] = "baseball_bat" };
        var losses = new DeathLosses(5, ["baseball_bat", "hoodie"], equipped);

        Assert.Equal(
            "Money lost: 5 · Gear lost: Baseball bat (equipped), Hoodie (equipped)",
            DeathScreenText.Format(losses, ItemCatalog.Default).Losses);
    }

    [Fact]
    public void Ac7_OnlyTheOutfitEquipped_ListsItFirst()
    {
        var losses = Losses(0, ["baseball_bat", "hoodie", "bike_lock"], outfit: "hoodie");

        Assert.Equal(
            "Money lost: 0 · Gear lost: Hoodie (equipped), Baseball bat, Bike lock",
            DeathScreenText.Format(losses, ItemCatalog.Default).Losses);
    }

    [Fact]
    public void Ac7_UnequippedItems_AreByCategoryThenDisplayNameIgnoringCase_NotById()
    {
        var losses = Losses(10, ["a_stick", "b_axe", "a_vest", "z_cap", "b_key", "z_box"]);

        Assert.Equal(
            "Money lost: 10 · Gear lost: Axe, Stick, Cap, Vest, apple box, Key",
            DeathScreenText.Format(losses, Shuffled).Losses);
    }

    [Fact]
    public void Ac7_EquippedItems_AreNotListedTwice()
    {
        var losses = Losses(10, ["a_stick", "b_axe", "z_cap"], weapon: "a_stick", outfit: "z_cap");

        Assert.Equal(
            "Money lost: 10 · Gear lost: Stick (equipped), Cap (equipped), Axe",
            DeathScreenText.Format(losses, Shuffled).Losses);
    }

    [Fact]
    public void Ac7_UnknownIds_ShowAsTheIdAfterTheKnownOnes_InOrdinalOrder()
    {
        var losses = Losses(3, ["unknown_2", "b_key", "Unknown_1", "a_stick"]);

        Assert.Equal(
            "Money lost: 3 · Gear lost: Stick, Key, Unknown_1, unknown_2",
            DeathScreenText.Format(losses, Shuffled).Losses);
    }

    [Fact]
    public void Ac7_MoneyOnly_SaysGearNone()
    {
        var lines = DeathScreenText.Format(Losses(250, []), ItemCatalog.Default);

        Assert.Equal("The Earth is flat", lines.Title);
        Assert.Equal("You fell off the edge of the world", lines.Subtitle);
        Assert.Equal("Money lost: 250 · Gear lost: none", lines.Losses);
    }

    [Fact]
    public void Ac7_NothingLost_SaysZeroAndNone()
    {
        var lines = DeathScreenText.Format(Losses(0, []), ItemCatalog.Default);

        Assert.Equal("Money lost: 0 · Gear lost: none", lines.Losses);
    }

    [Fact]
    public void Format_LargeMoneyUnderFinnishCulture_HasNoSeparators()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
            Assert.Equal(
                "Money lost: 1234567 · Gear lost: none",
                DeathScreenText.Format(Losses(1234567, []), ItemCatalog.Default).Losses);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Constants_AreTheSpecsWording()
    {
        Assert.Equal("The Earth is flat", DeathScreenText.Title);
        Assert.Equal("You fell off the edge of the world", DeathScreenText.Subtitle);
        Assert.Equal("none", DeathScreenText.NoGear);
        Assert.Equal(" (equipped)", DeathScreenText.EquippedSuffix);
    }

    [Fact]
    public void Format_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => DeathScreenText.Format(null!, ItemCatalog.Default));
        Assert.Throws<ArgumentNullException>(() => DeathScreenText.Format(Losses(0, []), null!));
    }
}
