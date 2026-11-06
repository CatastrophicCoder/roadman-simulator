using Roadman.Core.Maps;
using Roadman.Core.Saves;
using Roadman.Core.Tests.Items;

namespace Roadman.Core.Tests;

/// <summary>
/// <see cref="PlayerState.Snapshot"/>, <see cref="PlayerState.Restore"/> and <see cref="PlayerState.Reset"/>:
/// <c>docs/specs/menu-settings-save.md</c>, acceptance criterion 4.
/// </summary>
public class PlayerStateSnapshotTests
{
    [Fact]
    public void Restore_SetsMoneyGearWantedLevelAndPosition_EachEventOnce()
    {
        var state = new PlayerState(TestCatalogs.Plain);
        var events = new EventCounter(state);

        state.Restore(new SavePlayer(new LocalPoint(4, 100, -7), null, 120, ["b", "a"], SaveEquipped.Empty, 2));

        Assert.Equal(120, state.Money);
        Assert.Equal(new HashSet<string> { "a", "b" }, state.Gear);
        Assert.Equal(2, state.WantedLevel);
        Assert.Equal(new LocalPoint(4, 100, -7), state.Position);
        Assert.Equal((1, 1, 1, 1, 0), events.Counts);
        Assert.Equal([120], events.MoneyValues);
        Assert.Equal([2], events.WantedValues);
        Assert.Equal([new LocalPoint(4, 100, -7)], events.PositionValues);
    }

    [Fact]
    public void Restore_UnchangedValues_RaiseNoEvent()
    {
        var state = Populated();
        var events = new EventCounter(state);

        state.Restore(new SavePlayer(new LocalPoint(1, 2, 3), 0.25, 50, ["kit", "map"], SaveEquipped.Empty, 1));

        Assert.Equal((0, 0, 0, 0, 0), events.Counts);
        Assert.Equal(50, state.Money);
    }

    [Fact]
    public void Restore_OnlyChangedValues_RaiseTheirEvents()
    {
        var state = Populated();
        var events = new EventCounter(state);

        // Money and position as they are; gear and wanted level change.
        state.Restore(new SavePlayer(new LocalPoint(1, 2, 3), null, 50, ["kit"], SaveEquipped.Empty, 4));

        Assert.Equal((0, 1, 1, 0, 0), events.Counts);
        Assert.Equal(new HashSet<string> { "kit" }, state.Gear);
        Assert.Equal(4, state.WantedLevel);
    }

    [Fact]
    public void Restore_SameGearInAnotherOrder_RaisesNoGearChanged()
    {
        var state = Populated();
        var events = new EventCounter(state);

        state.Restore(new SavePlayer(null, null, 50, ["map", "kit"], SaveEquipped.Empty, 1));

        Assert.Equal(0, events.Gear);
    }

    public static TheoryData<SavePlayer> InvalidSnapshots() =>
    [
        new SavePlayer(new LocalPoint(9, 9, 9), null, -1, ["x"], SaveEquipped.Empty, 3),
        new SavePlayer(new LocalPoint(9, 9, 9), null, 999, ["x"], SaveEquipped.Empty, 6),
        new SavePlayer(new LocalPoint(9, 9, 9), null, 999, ["x"], SaveEquipped.Empty, -1),
        new SavePlayer(new LocalPoint(9, 9, 9), null, 999, ["x", " "], SaveEquipped.Empty, 3),
        new SavePlayer(new LocalPoint(9, 9, 9), null, 999, ["x", ""], SaveEquipped.Empty, 3),
        new SavePlayer(new LocalPoint(9, 9, 9), null, 999, ["x", "x"], SaveEquipped.Empty, 3),
        new SavePlayer(new LocalPoint(double.NaN, 9, 9), null, 999, ["x"], SaveEquipped.Empty, 3),
        new SavePlayer(new LocalPoint(9, double.PositiveInfinity, 9), null, 999, ["x"], SaveEquipped.Empty, 3),
        new SavePlayer(null, 0.5, 999, ["x"], SaveEquipped.Empty, 3),
        new SavePlayer(new LocalPoint(9, 9, 9), double.NaN, 999, ["x"], SaveEquipped.Empty, 3),
    ];

    [Theory]
    [MemberData(nameof(InvalidSnapshots))]
    public void Restore_InvalidSnapshot_ThrowsAndChangesNothing(SavePlayer snapshot)
    {
        var state = Populated();
        var before = state.Snapshot(includePosition: true);
        var events = new EventCounter(state);

        Assert.Throws<ArgumentException>(() => state.Restore(snapshot));

        Assert.Equal(before, state.Snapshot(includePosition: true));
        Assert.Equal((0, 0, 0, 0, 0), events.Counts);
    }

    [Fact]
    public void Restore_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PlayerState(TestCatalogs.Plain).Restore(null!));
    }

    [Fact]
    public void Restore_NeverRaisesDied()
    {
        var state = Populated();
        var events = new EventCounter(state);

        state.Restore(new SavePlayer(null, null, 0, [], SaveEquipped.Empty, 0));

        Assert.Equal(0, events.Died);
    }

    [Fact]
    public void SnapshotWithPosition_RestoredOnFreshState_GivesAnEqualState()
    {
        var original = Populated();
        var fresh = new PlayerState(TestCatalogs.Plain);

        fresh.Restore(original.Snapshot(includePosition: true));

        Assert.Equal(original.Money, fresh.Money);
        Assert.Equal(original.Gear.Order(StringComparer.Ordinal), fresh.Gear.Order(StringComparer.Ordinal));
        Assert.Equal(original.WantedLevel, fresh.WantedLevel);
        Assert.Equal(original.Position, fresh.Position);
        Assert.Equal(original.Snapshot(includePosition: true), fresh.Snapshot(includePosition: true));
    }

    [Fact]
    public void Snapshot_HoldsTheStateWithGearInOrdinalOrderAndNoFacing()
    {
        var state = new PlayerState(TestCatalogs.Plain);
        state.AddMoney(7);
        state.AddGear("b");
        state.AddGear("a_b");
        state.AddGear("a1");
        state.SetWantedLevel(5);
        state.SetPosition(new LocalPoint(1.5, 2.5, 3.5));

        var snapshot = state.Snapshot(includePosition: true);

        Assert.Equal(7, snapshot.Money);
        Assert.Equal(["a1", "a_b", "b"], snapshot.Gear); // ordinal: '1' (49) before '_' (95)
        Assert.Equal(SaveEquipped.Empty, snapshot.Equipped);
        Assert.Equal(5, snapshot.WantedLevel);
        Assert.Equal(new LocalPoint(1.5, 2.5, 3.5), snapshot.Position);
        Assert.Null(snapshot.FacingYaw);
    }

    [Fact]
    public void SnapshotWithoutPosition_HasAnEmptyPosition()
    {
        var snapshot = Populated().Snapshot(includePosition: false);

        Assert.Null(snapshot.Position);
        Assert.Null(snapshot.FacingYaw);
        Assert.Equal(50, snapshot.Money);
    }

    [Fact]
    public void RestoreEmptyPosition_ChangesMoneyGearWantedLevelButNotPosition()
    {
        var source = new PlayerState(TestCatalogs.Plain);
        source.AddMoney(300);
        source.AddGear("z");
        source.SetWantedLevel(3);
        source.SetPosition(new LocalPoint(-50, 90, 60));
        var state = Populated();
        var events = new EventCounter(state);

        state.Restore(source.Snapshot(includePosition: false));

        Assert.Equal(300, state.Money);
        Assert.Equal(new HashSet<string> { "z" }, state.Gear);
        Assert.Equal(3, state.WantedLevel);
        Assert.Equal(new LocalPoint(1, 2, 3), state.Position);
        Assert.Equal((1, 1, 1, 0, 0), events.Counts);
    }

    [Fact]
    public void Reset_GivesNewGameValuesWithChangeEventsAndNoDied()
    {
        var state = Populated();
        var events = new EventCounter(state);

        state.Reset();

        Assert.Equal(0, state.Money);
        Assert.Empty(state.Gear);
        Assert.Equal(0, state.WantedLevel);
        Assert.Equal(default, state.Position);
        Assert.Equal((1, 1, 1, 1, 0), events.Counts);
    }

    [Fact]
    public void Reset_FreshState_RaisesNothing()
    {
        var state = new PlayerState(TestCatalogs.Plain);
        var events = new EventCounter(state);

        state.Reset();

        Assert.Equal((0, 0, 0, 0, 0), events.Counts);
    }

    /// <summary>Money 50, gear kit and map, wanted level 1, position (1, 2, 3).</summary>
    private static PlayerState Populated()
    {
        var state = new PlayerState(TestCatalogs.Plain);
        state.AddMoney(50);
        state.AddGear("kit");
        state.AddGear("map");
        state.SetWantedLevel(1);
        state.SetPosition(new LocalPoint(1, 2, 3));
        return state;
    }

    private sealed class EventCounter
    {
        public EventCounter(PlayerState state)
        {
            state.MoneyChanged += value =>
            {
                Money++;
                MoneyValues.Add(value);
            };
            state.GearChanged += () => Gear++;
            state.WantedLevelChanged += value =>
            {
                Wanted++;
                WantedValues.Add(value);
            };
            state.PositionChanged += value =>
            {
                Position++;
                PositionValues.Add(value);
            };
            state.Died += () => Died++;
        }

        public int Money { get; private set; }

        public int Gear { get; private set; }

        public int Wanted { get; private set; }

        public int Position { get; private set; }

        public int Died { get; private set; }

        public List<int> MoneyValues { get; } = [];

        public List<int> WantedValues { get; } = [];

        public List<LocalPoint> PositionValues { get; } = [];

        /// <summary>(money, gear, wanted level, position, died).</summary>
        public (int, int, int, int, int) Counts => (Money, Gear, Wanted, Position, Died);
    }
}
