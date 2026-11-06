using Roadman.Core.Items;
using Roadman.Core.Maps;
using Roadman.Core.Saves;
using Roadman.Core.Tests.Items;

namespace Roadman.Core.Tests;

/// <summary>
/// <see cref="PlayerState"/>'s catalogue check, equipment slots and their events: <c>docs/specs/inventory.md</c>,
/// acceptance criteria 3, 4, 5, 6 and 10. Uses <see cref="TestCatalogs.Mixed"/>: weapons <c>bat</c> and
/// <c>crowbar</c>, clothing <c>suit</c> and <c>hoodie</c>, other <c>phone</c> and <c>lock</c>.
/// </summary>
public class PlayerStateInventoryTests
{
    private static PlayerState NewState(params string[] gear)
    {
        var state = new PlayerState(TestCatalogs.Mixed);
        foreach (var id in gear)
        {
            state.AddGear(id);
        }

        return state;
    }

    [Fact]
    public void Constructor_KeepsTheCatalogue_NullThrows()
    {
        Assert.Same(TestCatalogs.Mixed, new PlayerState(TestCatalogs.Mixed).Catalog);
        Assert.Throws<ArgumentNullException>(() => new PlayerState(null!));
    }

    [Fact]
    public void NewState_HasNothingEquipped()
    {
        var state = NewState();

        Assert.Empty(state.Equipped);
        Assert.Null(state.EquippedIn(EquipSlot.Weapon));
        Assert.Null(state.EquippedIn(EquipSlot.Outfit));
    }

    // Criterion 3

    [Theory]
    [InlineData("sword")]
    [InlineData("Bat")]
    [InlineData("bat ")]
    public void Ac3_AddGear_IdNotInTheCatalogue_ThrowsNamingIt_AndChangesNothing(string id)
    {
        var state = NewState("bat");
        var log = new EventLog(state);

        var ex = Assert.ThrowsAny<ArgumentException>(() => state.AddGear(id));

        Assert.Contains(id.Trim(), ex.Message);
        Assert.Equal(new HashSet<string> { "bat" }, state.Gear);
        Assert.Empty(log.Events);
    }

    [Fact]
    public void Ac3_AddGear_KnownId_IsAddedOnce()
    {
        var state = NewState();
        var log = new EventLog(state);

        Assert.True(state.AddGear("phone"));
        Assert.False(state.AddGear("phone"));

        Assert.Equal(new HashSet<string> { "phone" }, state.Gear);
        Assert.Equal(["gear"], log.Events);
    }

    // Criterion 4

    [Fact]
    public void Ac4_Equip_HeldWeapon_FillsWeaponWithOneEvent()
    {
        var state = NewState("bat", "crowbar");
        var log = new EventLog(state);

        Assert.True(state.Equip("bat"));

        Assert.Equal("bat", state.EquippedIn(EquipSlot.Weapon));
        Assert.Null(state.EquippedIn(EquipSlot.Outfit));
        Assert.Equal([KeyValuePair.Create(EquipSlot.Weapon, "bat")], state.Equipped.ToList());
        Assert.True(state.IsEquipped("bat"));
        Assert.False(state.IsEquipped("crowbar"));
        Assert.Equal(["equip:Weapon"], log.Events);
    }

    [Fact]
    public void Ac4_Equip_TheOtherHeldWeapon_ReplacesItWithOneEvent_BothStayHeld()
    {
        var state = NewState("bat", "crowbar");
        state.Equip("bat");
        var log = new EventLog(state);

        Assert.True(state.Equip("crowbar"));

        Assert.Equal("crowbar", state.EquippedIn(EquipSlot.Weapon));
        Assert.False(state.IsEquipped("bat"));
        Assert.Equal(new HashSet<string> { "bat", "crowbar" }, state.Gear);
        Assert.Equal(["equip:Weapon"], log.Events);
    }

    [Fact]
    public void Ac4_Equip_TheSameItemAgain_ChangesNothingAndRaisesNothing()
    {
        var state = NewState("bat");
        state.Equip("bat");
        var log = new EventLog(state);

        Assert.False(state.Equip("bat"));

        Assert.Equal("bat", state.EquippedIn(EquipSlot.Weapon));
        Assert.Empty(log.Events);
    }

    [Fact]
    public void Ac4_Equip_HeldClothing_FillsOutfit_LeavingTheWeapon()
    {
        var state = NewState("bat", "suit", "hoodie");
        state.Equip("bat");
        var log = new EventLog(state);

        Assert.True(state.Equip("suit"));
        Assert.True(state.Equip("hoodie"));

        Assert.Equal("bat", state.EquippedIn(EquipSlot.Weapon));
        Assert.Equal("hoodie", state.EquippedIn(EquipSlot.Outfit));
        Assert.Equal(["equip:Outfit", "equip:Outfit"], log.Events);
    }

    [Fact]
    public void Ac4_Equip_ItemNotHeld_ThrowsInvalidOperationNamingIt_AndChangesNothing()
    {
        var state = NewState("bat");
        state.Equip("bat");
        var log = new EventLog(state);

        var ex = Assert.Throws<InvalidOperationException>(() => state.Equip("crowbar"));

        Assert.Contains("crowbar", ex.Message);
        AssertUnchanged(state, log, ["bat"], weapon: "bat", outfit: null);
    }

    [Fact]
    public void Ac4_Equip_UnknownId_ThrowsArgumentNamingIt_AndChangesNothing()
    {
        var state = NewState("bat");
        state.Equip("bat");
        var log = new EventLog(state);

        var ex = Assert.ThrowsAny<ArgumentException>(() => state.Equip("sword"));

        Assert.Contains("sword", ex.Message);
        AssertUnchanged(state, log, ["bat"], weapon: "bat", outfit: null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Ac4_Equip_BlankId_ThrowsArgument(string? id)
    {
        var state = NewState("bat");

        Assert.ThrowsAny<ArgumentException>(() => state.Equip(id!));
        Assert.Empty(state.Equipped);
    }

    [Fact]
    public void Ac4_Equip_HeldOtherItem_ThrowsInvalidOperationNamingIt_AndChangesNothing()
    {
        var state = NewState("phone", "suit");
        state.Equip("suit");
        var log = new EventLog(state);

        var ex = Assert.Throws<InvalidOperationException>(() => state.Equip("phone"));

        Assert.Contains("phone", ex.Message);
        AssertUnchanged(state, log, ["phone", "suit"], weapon: null, outfit: "suit");
    }

    [Fact]
    public void Ac4_Unequip_FullSlot_EmptiesItWithOneEvent_TheItemStaysHeld()
    {
        var state = NewState("bat", "suit");
        state.Equip("bat");
        state.Equip("suit");
        var log = new EventLog(state);

        Assert.True(state.Unequip(EquipSlot.Outfit));

        Assert.Null(state.EquippedIn(EquipSlot.Outfit));
        Assert.Equal("bat", state.EquippedIn(EquipSlot.Weapon));
        Assert.Contains("suit", state.Gear);
        Assert.Equal(["equip:Outfit"], log.Events);
    }

    [Fact]
    public void Ac4_Unequip_EmptySlot_ChangesNothing()
    {
        var state = NewState("bat");
        state.Equip("bat");
        var log = new EventLog(state);

        Assert.False(state.Unequip(EquipSlot.Outfit));

        Assert.Equal("bat", state.EquippedIn(EquipSlot.Weapon));
        Assert.Empty(log.Events);
    }

    // Criterion 5

    [Fact]
    public void Ac5_RemoveGear_EquippedItem_EmptiesItsSlotFirst_ThenGearChanged()
    {
        var state = NewState("bat", "suit");
        state.Equip("bat");
        state.Equip("suit");
        var log = new EventLog(state);

        Assert.True(state.RemoveGear("bat"));

        Assert.DoesNotContain("bat", state.Gear);
        Assert.Null(state.EquippedIn(EquipSlot.Weapon));
        Assert.Equal("suit", state.EquippedIn(EquipSlot.Outfit));
        Assert.Equal(["equip:Weapon", "gear"], log.Events);
    }

    [Fact]
    public void Ac5_RemoveGear_UnequippedItem_LeavesTheSlotsAlone()
    {
        var state = NewState("bat", "crowbar", "suit");
        state.Equip("bat");
        state.Equip("suit");
        var log = new EventLog(state);

        Assert.True(state.RemoveGear("crowbar"));

        Assert.Equal("bat", state.EquippedIn(EquipSlot.Weapon));
        Assert.Equal("suit", state.EquippedIn(EquipSlot.Outfit));
        Assert.Equal(["gear"], log.Events);
    }

    [Theory]
    [InlineData(20261106)]
    [InlineData(1)]
    [InlineData(424242)]
    public void Ac5_SeededRandomSequence_KeepsEveryEquippedItemHeldAndInItsSlot(int seed)
    {
        var catalog = TestCatalogs.Mixed;
        var ids = catalog.All.Select(i => i.Id).ToArray();
        var random = new Random(seed);
        var state = new PlayerState(catalog);
        var log = new EventLog(state);

        // The invariant must hold whenever any listener runs, not only between calls.
        void CheckInvariant() => AssertInvariant(state);
        state.GearChanged += CheckInvariant;
        state.EquipmentChanged += _ => CheckInvariant();
        state.MoneyChanged += _ => CheckInvariant();
        state.WantedLevelChanged += _ => CheckInvariant();
        state.Died += CheckInvariant;

        var operations = new Dictionary<string, int>();
        for (var step = 0; step < 3000; step++)
        {
            var before = Slots(state);
            var gearBefore = state.Gear.ToHashSet();
            log.Events.Clear();
            var op = random.Next(9);
            var name = op switch
            {
                0 or 1 => "AddGear",
                2 => "RemoveGear",
                3 or 4 => "Equip",
                5 => "Unequip",
                6 => "Restore",
                7 => random.Next(4) == 0 ? "Reset" : "Restore",
                _ => random.Next(3) == 0 ? "Die" : "AddGear",
            };
            operations[name] = operations.GetValueOrDefault(name) + 1;

            switch (name)
            {
                case "AddGear":
                    state.AddGear(Pick(random, ids));
                    break;
                case "RemoveGear":
                    state.RemoveGear(Pick(random, ids));
                    break;
                case "Equip":
                    var id = Pick(random, ids);
                    var fits = catalog.SlotFor(id) is not null && state.Gear.Contains(id);
                    if (fits)
                    {
                        state.Equip(id);
                    }
                    else
                    {
                        Assert.Throws<InvalidOperationException>(() => state.Equip(id));
                    }

                    break;
                case "Unequip":
                    state.Unequip(Pick(random, ItemCatalog.Slots.ToArray()));
                    break;
                case "Restore":
                    if (random.Next(5) == 0)
                    {
                        // An invalid snapshot (an equipped item not in its gear) throws and changes nothing.
                        var snapshotBefore = state.Snapshot(includePosition: true);
                        Assert.Throws<ArgumentException>(() => state.Restore(
                            new SavePlayer(null, null, 5, ["phone"], new SaveEquipped("bat", null), 0)));
                        Assert.Equal(snapshotBefore, state.Snapshot(includePosition: true));
                        Assert.Empty(log.Events);
                    }
                    else
                    {
                        state.Restore(RandomSnapshot(random, catalog));
                    }

                    break;
                case "Reset":
                    state.Reset();
                    break;
                case "Die":
                    state.Die();
                    Assert.Empty(state.Equipped);
                    Assert.Empty(state.Gear);
                    break;
            }

            AssertInvariant(state);
            var after = Slots(state);

            // EquipmentChanged is raised for exactly the slots whose content changed (once for a single change;
            // Restore may empty a slot before the gear changes and fill it after, so up to twice there).
            foreach (var slot in ItemCatalog.Slots)
            {
                var raised = log.Events.Count(e => e == $"equip:{slot}");
                if (before[slot] == after[slot])
                {
                    Assert.True(raised == 0, $"step {step} {name}: {slot} unchanged but raised {raised} times");
                }
                else if (name == "Restore")
                {
                    Assert.InRange(raised, 1, 2);
                }
                else
                {
                    Assert.True(raised == 1, $"step {step} {name}: {slot} changed, raised {raised} times");
                }
            }

            Assert.Equal(gearBefore.SetEquals(state.Gear) ? 0 : 1, log.Events.Count(e => e == "gear"));
        }

        Assert.All(new[] { "AddGear", "RemoveGear", "Equip", "Unequip", "Restore", "Reset", "Die" }, op =>
            Assert.True(operations.GetValueOrDefault(op) > 20, $"{op} ran only {operations.GetValueOrDefault(op)} times"));
    }

    // Criterion 6

    [Fact]
    public void Ac6_Die_EmptiesBothSlotsThenMoneyGearAndWantedLevel_DiedLast()
    {
        var state = NewState("bat", "suit", "phone");
        state.Equip("bat");
        state.Equip("suit");
        state.AddMoney(120);
        state.SetWantedLevel(3);
        var log = new EventLog(state);

        state.Die();

        Assert.Empty(state.Equipped);
        Assert.Empty(state.Gear);
        Assert.Equal(0, state.Money);
        Assert.Equal(0, state.WantedLevel);
        Assert.Equal(["equip:Weapon", "equip:Outfit", "money", "gear", "wanted", "died"], log.Events);
    }

    [Fact]
    public void Ac6_Die_OneSlotFull_RaisesEquipmentChangedForThatSlotOnly()
    {
        var state = NewState("hoodie", "phone");
        state.Equip("hoodie");
        var log = new EventLog(state);

        state.Die();

        Assert.Empty(state.Equipped);
        Assert.Equal(["equip:Outfit", "gear", "died"], log.Events);
    }

    [Fact]
    public void Ac6_Die_NothingEquipped_RaisesNoEquipmentChanged()
    {
        var state = NewState("phone");
        var log = new EventLog(state);

        state.Die();

        Assert.Equal(["gear", "died"], log.Events);
    }

    // Criterion 10

    [Fact]
    public void Ac10_Snapshot_HoldsTheEquippedItems()
    {
        var state = NewState("bat", "suit", "phone");
        state.Equip("bat");
        state.Equip("suit");

        var snapshot = state.Snapshot(includePosition: true);

        Assert.Equal(new SaveEquipped("bat", "suit"), snapshot.Equipped);
        Assert.Equal(["bat", "phone", "suit"], snapshot.Gear);
        Assert.Equal(SaveEquipped.Empty, NewState("bat").Snapshot(includePosition: false).Equipped);
    }

    [Fact]
    public void Ac10_SnapshotThenRestoreOnAFreshState_GivesAnEqualState_SlotsIncluded()
    {
        var original = NewState("crowbar", "hoodie", "lock");
        original.Equip("crowbar");
        original.Equip("hoodie");
        original.AddMoney(75);
        original.SetWantedLevel(2);
        original.SetPosition(new LocalPoint(1, 2, 3));
        var fresh = new PlayerState(TestCatalogs.Mixed);
        var log = new EventLog(fresh);

        fresh.Restore(original.Snapshot(includePosition: true));

        Assert.Equal(original.Snapshot(includePosition: true), fresh.Snapshot(includePosition: true));
        Assert.Equal(2, fresh.Equipped.Count);
        Assert.Equal("crowbar", fresh.EquippedIn(EquipSlot.Weapon));
        Assert.Equal("hoodie", fresh.EquippedIn(EquipSlot.Outfit));
        Assert.Equal(1, log.Events.Count(e => e == "equip:Weapon"));
        Assert.Equal(1, log.Events.Count(e => e == "equip:Outfit"));
        Assert.DoesNotContain("died", log.Events);

        // The slots are set after the gear, so the invariant holds when EquipmentChanged is raised.
        Assert.True(log.Events.IndexOf("gear") < log.Events.IndexOf("equip:Weapon"));
        Assert.True(log.Events.IndexOf("gear") < log.Events.IndexOf("equip:Outfit"));
    }

    [Fact]
    public void Ac10_Restore_RaisesEquipmentChangedOnlyForSlotsThatChange()
    {
        var state = NewState("bat", "suit", "hoodie");
        state.Equip("bat");
        state.Equip("suit");
        var log = new EventLog(state);

        state.Restore(new SavePlayer(null, null, 0, ["bat", "hoodie", "suit"], new SaveEquipped("bat", "hoodie"), 0));

        Assert.Equal(["equip:Outfit"], log.Events);
        Assert.Equal("hoodie", state.EquippedIn(EquipSlot.Outfit));
    }

    [Fact]
    public void Ac10_Restore_SameValues_RaisesNothing()
    {
        var state = NewState("bat", "suit");
        state.Equip("bat");
        var log = new EventLog(state);

        state.Restore(state.Snapshot(includePosition: true));

        Assert.Empty(log.Events);
    }

    [Fact]
    public void Ac10_Restore_EmptyingTheSlots_KeepsTheGear()
    {
        var state = NewState("bat", "suit");
        state.Equip("bat");
        state.Equip("suit");
        var log = new EventLog(state);

        state.Restore(new SavePlayer(null, null, 0, ["bat", "suit"], SaveEquipped.Empty, 0));

        Assert.Empty(state.Equipped);
        Assert.Equal(new HashSet<string> { "bat", "suit" }, state.Gear);
        Assert.Equal(["equip:Weapon", "equip:Outfit"], log.Events);
    }

    [Fact]
    public void Ac10_Restore_DroppingAnEquippedItem_EmptiesItsSlotBeforeGearChanged()
    {
        var state = NewState("bat", "suit");
        state.Equip("bat");
        var log = new EventLog(state);

        state.Restore(new SavePlayer(null, null, 0, ["suit"], SaveEquipped.Empty, 0));

        Assert.Empty(state.Equipped);
        Assert.Equal(["equip:Weapon", "gear"], log.Events);
    }

    public static TheoryData<string, SavePlayer> InvalidEquippedSnapshots() => new()
    {
        { "equipped item not in its gear", new SavePlayer(null, null, 5, ["suit"], new SaveEquipped("bat", null), 0) },
        { "equipped outfit not in its gear", new SavePlayer(null, null, 5, ["bat"], new SaveEquipped("bat", "suit"), 0) },
        { "blank equipped id", new SavePlayer(null, null, 5, ["bat"], new SaveEquipped(" ", null), 0) },
        { "clothing in the weapon slot", new SavePlayer(null, null, 5, ["suit"], new SaveEquipped("suit", null), 0) },
        { "weapon in the outfit slot", new SavePlayer(null, null, 5, ["bat"], new SaveEquipped(null, "bat"), 0) },
        { "other item in a slot", new SavePlayer(null, null, 5, ["phone"], new SaveEquipped("phone", null), 0) },
        { "unknown gear id", new SavePlayer(null, null, 5, ["bat", "sword"], SaveEquipped.Empty, 0) },
        { "unknown equipped id", new SavePlayer(null, null, 5, ["sword"], new SaveEquipped("sword", null), 0) },
    };

    [Theory]
    [MemberData(nameof(InvalidEquippedSnapshots))]
    public void Ac10_Restore_InvalidSnapshot_ThrowsAndChangesNothing(string why, SavePlayer snapshot)
    {
        var state = NewState("crowbar", "hoodie", "lock");
        state.Equip("crowbar");
        state.Equip("hoodie");
        state.AddMoney(40);
        var before = state.Snapshot(includePosition: true);
        var log = new EventLog(state);

        Assert.Throws<ArgumentException>(() => state.Restore(snapshot));

        Assert.True(before == state.Snapshot(includePosition: true), why);
        Assert.Empty(log.Events);
    }

    [Fact]
    public void Ac10_Reset_EmptiesTheSlots_WithEventsAndNoDied()
    {
        var state = NewState("bat", "suit");
        state.Equip("bat");
        state.Equip("suit");
        var log = new EventLog(state);

        state.Reset();

        Assert.Empty(state.Equipped);
        Assert.Empty(state.Gear);
        Assert.Equal(1, log.Events.Count(e => e == "equip:Weapon"));
        Assert.Equal(1, log.Events.Count(e => e == "equip:Outfit"));
        Assert.True(log.Events.IndexOf("equip:Outfit") < log.Events.IndexOf("gear"));
        Assert.DoesNotContain("died", log.Events);
    }

    [Fact]
    public void Ac10_Reset_NothingEquipped_RaisesNoEquipmentChanged()
    {
        var state = NewState("bat");
        var log = new EventLog(state);

        state.Reset();

        Assert.Equal(["gear"], log.Events);
    }

    private static void AssertUnchanged(PlayerState state, EventLog log, string[] gear, string? weapon, string? outfit)
    {
        Assert.Equal(gear.ToHashSet(), state.Gear);
        Assert.Equal(weapon, state.EquippedIn(EquipSlot.Weapon));
        Assert.Equal(outfit, state.EquippedIn(EquipSlot.Outfit));
        Assert.Empty(log.Events);
    }

    private static void AssertInvariant(PlayerState state)
    {
        foreach (var (slot, id) in state.Equipped)
        {
            Assert.True(state.Gear.Contains(id), $"{id} is equipped in {slot} but not held");
            Assert.Equal(slot, state.Catalog.SlotFor(id));
            Assert.Equal(id, state.EquippedIn(slot));
            Assert.True(state.IsEquipped(id));
        }

        foreach (var slot in ItemCatalog.Slots)
        {
            Assert.Equal(state.Equipped.ContainsKey(slot), state.EquippedIn(slot) is not null);
        }
    }

    private static Dictionary<EquipSlot, string?> Slots(PlayerState state) =>
        ItemCatalog.Slots.ToDictionary(slot => slot, state.EquippedIn);

    private static T Pick<T>(Random random, T[] items) => items[random.Next(items.Length)];

    /// <summary>A valid snapshot: a random gear subset, each slot empty or a fitting held item.</summary>
    private static SavePlayer RandomSnapshot(Random random, ItemCatalog catalog)
    {
        var gear = catalog.All.Where(_ => random.Next(2) == 0).Select(i => i.Id).ToList();
        string? PickFor(ItemCategory category)
        {
            var fitting = gear.Where(id => catalog.Get(id).Category == category).ToArray();
            return fitting.Length == 0 || random.Next(3) == 0 ? null : Pick(random, fitting);
        }

        var equipped = new SaveEquipped(PickFor(ItemCategory.Weapon), PickFor(ItemCategory.Clothing));
        return new SavePlayer(null, null, random.Next(0, 3) * 10, gear, equipped, random.Next(0, 3));
    }

    /// <summary>Every event of a <see cref="PlayerState"/> in the order raised.</summary>
    private sealed class EventLog
    {
        public EventLog(PlayerState state)
        {
            state.MoneyChanged += _ => Events.Add("money");
            state.GearChanged += () => Events.Add("gear");
            state.WantedLevelChanged += _ => Events.Add("wanted");
            state.PositionChanged += _ => Events.Add("position");
            state.EquipmentChanged += slot => Events.Add($"equip:{slot}");
            state.Died += () => Events.Add("died");
        }

        public List<string> Events { get; } = [];
    }
}
