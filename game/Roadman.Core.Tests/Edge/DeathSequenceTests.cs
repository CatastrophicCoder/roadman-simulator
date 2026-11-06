using Roadman.Core.Edge;
using Roadman.Core.Items;
using Roadman.Core.Maps;
using Roadman.Core.Tests.Items;

namespace Roadman.Core.Tests.Edge;

/// <summary>
/// <see cref="DeathSequence"/>: <c>docs/specs/world-edge-game.md</c>, acceptance criterion 3; the tests named
/// <c>Ac6_Inventory_...</c> are <c>docs/specs/inventory.md</c>, acceptance criterion 6 (the equipped items in the losses).
/// </summary>
public class DeathSequenceTests
{
    private const double Step = 1.0 / 60;

    private static readonly VoidEntry Entry = new(new LocalPoint(-1155, 86.5, 980), new PlanarPoint(-1, 0));

    private readonly PlayerState _player = new(TestCatalogs.Plain);
    private readonly DeathSequence _sequence;
    private readonly List<(DeathState From, DeathState To)> _changes = [];
    private int _died;

    public DeathSequenceTests()
    {
        _player.AddMoney(120);
        _player.AddGear("phone");
        _player.AddGear("knife");
        _player.SetWantedLevel(3);
        _player.Died += () => _died++;
        _sequence = new DeathSequence(_player);
        _sequence.StateChanged += (from, to) => _changes.Add((from, to));
    }

    public static TheoryData<DeathState> NotAlive() =>
        [DeathState.Falling, DeathState.DeathScreen, DeathState.Respawning, DeathState.Failed];

    public static TheoryData<DeathState> EveryState() =>
        [DeathState.Alive, DeathState.Falling, DeathState.DeathScreen, DeathState.Respawning, DeathState.Failed];

    [Fact]
    public void Constructor_NullPlayer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DeathSequence(null!));
    }

    [Fact]
    public void NewSequence_IsAliveWithNothingRecorded()
    {
        Assert.Equal(DeathState.Alive, _sequence.State);
        Assert.Null(_sequence.Losses);
        Assert.Null(_sequence.Entry);
        Assert.Equal(3.0, _sequence.FallTime);
        Assert.Equal(5.0, _sequence.ScreenTime);
        Assert.Equal(2.0, _sequence.MinScreenTime);
    }

    [Fact]
    public void EnterVoid_Alive_RecordsLossesThenDiesOnceAndFalls() // criterion 3
    {
        DeathLosses? lossesWhenDied = null;
        _player.Died += () => lossesWhenDied = _sequence.Losses;

        Assert.True(_sequence.EnterVoid(Entry));

        Assert.NotNull(lossesWhenDied); // recorded before Die()
        Assert.Equal(120, lossesWhenDied.Money);
        Assert.Equal(["knife", "phone"], lossesWhenDied.Gear);
        Assert.Equal(1, _died);
        Assert.Equal(0, _player.Money);
        Assert.Empty(_player.Gear);
        Assert.Equal(0, _player.WantedLevel);
        Assert.Equal(DeathState.Falling, _sequence.State);
        Assert.Same(lossesWhenDied, _sequence.Losses);
        Assert.Equal(Entry, _sequence.Entry);
        Assert.Equal([(DeathState.Alive, DeathState.Falling)], _changes);
    }

    [Fact]
    public void EnterVoid_GearLosses_AreSortedOrdinal() // criterion 3
    {
        var player = new PlayerState(TestCatalogs.Plain);
        player.AddGear("a_b");
        player.AddGear("a1"); // ordinal: '1' (49) before '_' (95); a culture sort puts punctuation before digits
        var sequence = new DeathSequence(player);

        sequence.EnterVoid(Entry);

        Assert.Equal(["a1", "a_b"], sequence.Losses!.Gear);
        Assert.Equal(0, sequence.Losses.Money);
        Assert.Empty(sequence.Losses.Equipped);
    }

    [Fact]
    public void Ac6_Inventory_EnterVoid_RecordsTheEquippedItemsBeforeDie()
    {
        var player = new PlayerState(TestCatalogs.Mixed);
        player.AddMoney(120);
        player.AddGear("phone");
        player.AddGear("suit");
        player.AddGear("bat");
        player.AddGear("crowbar");
        player.Equip("crowbar");
        player.Equip("suit");
        var sequence = new DeathSequence(player);
        DeathLosses? lossesAtFirstUnequip = null;
        player.EquipmentChanged += _ => lossesAtFirstUnequip ??= sequence.Losses;

        Assert.True(sequence.EnterVoid(Entry));

        Assert.NotNull(lossesAtFirstUnequip); // recorded before Die() emptied the slots
        var losses = sequence.Losses!;
        Assert.Same(lossesAtFirstUnequip, losses);
        Assert.Equal(120, losses.Money);
        Assert.Equal(["bat", "crowbar", "phone", "suit"], losses.Gear); // the equipped ids too, sorted ordinal
        Assert.Equal(2, losses.Equipped.Count);
        Assert.Equal("crowbar", losses.Equipped[EquipSlot.Weapon]);
        Assert.Equal("suit", losses.Equipped[EquipSlot.Outfit]);
        Assert.Empty(player.Equipped);
        Assert.Empty(player.Gear);
    }

    [Fact]
    public void Ac6_Inventory_EnterVoid_OneSlotEquipped_RecordsOnlyThatSlot()
    {
        var player = new PlayerState(TestCatalogs.Mixed);
        player.AddGear("hoodie");
        player.Equip("hoodie");
        var sequence = new DeathSequence(player);

        sequence.EnterVoid(Entry);

        Assert.Equal([KeyValuePair.Create(EquipSlot.Outfit, "hoodie")], sequence.Losses!.Equipped.ToList());
        Assert.Equal(["hoodie"], sequence.Losses.Gear);
    }

    [Fact]
    public void EnterVoid_NothingToLose_RecordsEmptyLosses()
    {
        var sequence = new DeathSequence(new PlayerState(TestCatalogs.Plain));

        Assert.True(sequence.EnterVoid(Entry));

        Assert.Equal(0, sequence.Losses!.Money);
        Assert.Empty(sequence.Losses.Gear);
        Assert.Empty(sequence.Losses.Equipped);
    }

    [Theory]
    [MemberData(nameof(NotAlive))]
    public void EnterVoid_NotAlive_ChangesNothing(DeathState state) // criterion 3
    {
        MoveTo(state);
        var losses = _sequence.Losses;
        var entry = _sequence.Entry;
        var time = _sequence.TimeInState;
        _player.AddMoney(50);
        _player.AddGear("torch");
        _player.SetWantedLevel(2);
        var died = _died;
        var changes = _changes.Count;

        Assert.False(_sequence.EnterVoid(new VoidEntry(new LocalPoint(1, 2, 3), new PlanarPoint(0, 1))));

        Assert.Equal(state, _sequence.State);
        Assert.Same(losses, _sequence.Losses);
        Assert.Equal(entry, _sequence.Entry);
        Assert.Equal(time, _sequence.TimeInState);
        Assert.Equal(died, _died);
        Assert.Equal(50, _player.Money);
        Assert.Equal(["torch"], _player.Gear);
        Assert.Equal(2, _player.WantedLevel);
        Assert.Equal(changes, _changes.Count);
    }

    [Fact]
    public void Advance_SixtyHertz_ReachesDeathScreenOnStep180AndRespawningOnStep480() // criterion 3
    {
        _sequence.EnterVoid(Entry);
        _changes.Clear();
        var deathScreenStep = 0;
        var respawningStep = 0;

        for (var step = 1; step <= 600; step++)
        {
            _sequence.Advance(Step);
            if (deathScreenStep == 0 && _sequence.State == DeathState.DeathScreen)
            {
                deathScreenStep = step;
            }

            if (respawningStep == 0 && _sequence.State == DeathState.Respawning)
            {
                respawningStep = step;
            }
        }

        Assert.Equal(180, deathScreenStep);
        Assert.Equal(480, respawningStep);
        Assert.Equal(
            [(DeathState.Falling, DeathState.DeathScreen), (DeathState.DeathScreen, DeathState.Respawning)],
            _changes);
    }

    [Fact]
    public void Advance_SixtyHertz_OtherTimes_ReachStatesOnTheirSteps() // criterion 3
    {
        _sequence.FallTime = 1.5;
        _sequence.ScreenTime = 2.0;
        _sequence.EnterVoid(Entry);

        for (var step = 1; step <= 89; step++)
        {
            _sequence.Advance(Step);
        }

        Assert.Equal(DeathState.Falling, _sequence.State);
        _sequence.Advance(Step); // step 90: 1.5 s
        Assert.Equal(DeathState.DeathScreen, _sequence.State);

        for (var step = 91; step <= 209; step++)
        {
            _sequence.Advance(Step);
        }

        Assert.Equal(DeathState.DeathScreen, _sequence.State);
        _sequence.Advance(Step); // step 210: 3.5 s
        Assert.Equal(DeathState.Respawning, _sequence.State);
    }

    [Fact]
    public void Advance_StepOfExactlyFallTime_ReachesDeathScreen() // criterion 3
    {
        _sequence.EnterVoid(Entry);

        _sequence.Advance(3.0);

        Assert.Equal(DeathState.DeathScreen, _sequence.State);
        Assert.Equal(0, _sequence.TimeInState, 1e-9);
    }

    [Fact]
    public void Advance_StepJustUnderFallTime_StaysFalling() // criterion 3
    {
        _sequence.EnterVoid(Entry);

        _sequence.Advance(3.0 - 1e-6);

        Assert.Equal(DeathState.Falling, _sequence.State);
        Assert.Equal(3.0 - 1e-6, _sequence.TimeInState, 1e-12);

        _sequence.Advance(1e-6);
        Assert.Equal(DeathState.DeathScreen, _sequence.State);
    }

    [Fact]
    public void Advance_BigSteps_ReachEachStateAtItsTime() // criterion 3
    {
        _sequence.EnterVoid(Entry);

        _sequence.Advance(3.0);
        Assert.Equal(DeathState.DeathScreen, _sequence.State);
        _sequence.Advance(5.0 - 1e-6);
        Assert.Equal(DeathState.DeathScreen, _sequence.State);
        _sequence.Advance(1e-6);
        Assert.Equal(DeathState.Respawning, _sequence.State);
    }

    [Fact]
    public void Advance_PastFallTime_CarriesTheRemainder() // criterion 3
    {
        _sequence.EnterVoid(Entry);

        _sequence.Advance(3.5);

        Assert.Equal(DeathState.DeathScreen, _sequence.State);
        Assert.Equal(0.5, _sequence.TimeInState, 1e-9);

        _sequence.Advance(4.5);
        Assert.Equal(DeathState.Respawning, _sequence.State);
    }

    [Fact]
    public void Advance_OneHugeStep_PassesBothStatesInOrder() // criterion 3
    {
        _sequence.EnterVoid(Entry);
        _changes.Clear();

        _sequence.Advance(10.0);

        Assert.Equal(DeathState.Respawning, _sequence.State);
        Assert.Equal(
            [(DeathState.Falling, DeathState.DeathScreen), (DeathState.DeathScreen, DeathState.Respawning)],
            _changes);
        Assert.Equal(2.0, _sequence.TimeInState, 1e-9);
    }

    [Fact]
    public void Advance_ExactlyFallPlusScreenTime_ReachesRespawning() // criterion 3
    {
        _sequence.FallTime = 2.0;
        _sequence.ScreenTime = 1.25;
        _sequence.EnterVoid(Entry);
        _changes.Clear();

        _sequence.Advance(3.25);

        Assert.Equal(DeathState.Respawning, _sequence.State);
        Assert.Equal(2, _changes.Count);
    }

    [Fact]
    public void Advance_ZeroStep_ChangesNothing()
    {
        _sequence.EnterVoid(Entry);
        _changes.Clear();

        _sequence.Advance(0);

        Assert.Equal(DeathState.Falling, _sequence.State);
        Assert.Empty(_changes);
    }

    [Theory]
    [InlineData(DeathState.Alive)]
    [InlineData(DeathState.Respawning)]
    [InlineData(DeathState.Failed)]
    public void Advance_InStatesWithoutTimer_KeepsTheState(DeathState state) // criterion 3
    {
        MoveTo(state);
        _changes.Clear();

        _sequence.Advance(100);

        Assert.Equal(state, _sequence.State);
        Assert.Empty(_changes);
    }

    [Theory]
    [MemberData(nameof(EveryState))]
    public void Advance_NegativeOrNonFiniteStep_Throws(DeathState state) // criterion 3
    {
        MoveTo(state);

        foreach (var dt in new[] { -1e-9, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.ThrowsAny<ArgumentException>(() => _sequence.Advance(dt));
            Assert.Equal(state, _sequence.State);
        }
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FallTimeAndScreenTime_Invalid_Throw(double value)
    {
        Assert.ThrowsAny<ArgumentException>(() => _sequence.FallTime = value);
        Assert.ThrowsAny<ArgumentException>(() => _sequence.ScreenTime = value);
    }

    // ----- Dismiss (criterion 3; Part B plan amendment 1) -----

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-1e-9)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MinScreenTime_NegativeOrNonFinite_ThrowsAndKeepsTheValue(double value)
    {
        Assert.ThrowsAny<ArgumentException>(() => _sequence.MinScreenTime = value);
        Assert.Equal(2.0, _sequence.MinScreenTime);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(10.0)]
    public void MinScreenTime_ZeroOrPositive_IsKept(double value)
    {
        _sequence.MinScreenTime = value;
        Assert.Equal(value, _sequence.MinScreenTime);
    }

    [Fact]
    public void Dismiss_JustBeforeMinScreenTime_ReturnsFalseAndChangesNothing() // criterion 3
    {
        MoveTo(DeathState.DeathScreen);
        _sequence.Advance(_sequence.MinScreenTime - 1e-6);
        _changes.Clear();
        var time = _sequence.TimeInState;

        Assert.False(_sequence.Dismiss());

        Assert.Equal(DeathState.DeathScreen, _sequence.State);
        Assert.Equal(time, _sequence.TimeInState);
        Assert.Empty(_changes);
    }

    [Fact]
    public void Dismiss_ExactlyAtMinScreenTime_MovesToRespawningWithOneStateChanged() // criterion 3
    {
        MoveTo(DeathState.DeathScreen);
        _sequence.Advance(_sequence.MinScreenTime);
        _changes.Clear();

        Assert.True(_sequence.Dismiss());

        Assert.Equal(DeathState.Respawning, _sequence.State);
        Assert.Equal(0, _sequence.TimeInState);
        Assert.Equal([(DeathState.DeathScreen, DeathState.Respawning)], _changes);
    }

    [Fact]
    public void Dismiss_AfterMinScreenTime_MovesToRespawning() // criterion 3
    {
        MoveTo(DeathState.DeathScreen);
        _sequence.Advance(_sequence.MinScreenTime + 1.0);
        _changes.Clear();

        Assert.True(_sequence.Dismiss());

        Assert.Equal(DeathState.Respawning, _sequence.State);
        Assert.Equal([(DeathState.DeathScreen, DeathState.Respawning)], _changes);
    }

    [Fact]
    public void Dismiss_SixtyHertz_FalseOnStep119TrueOnStep120() // criterion 3: 2 s at 1/60 s steps
    {
        MoveTo(DeathState.DeathScreen);
        for (var step = 1; step <= 119; step++)
        {
            _sequence.Advance(Step);
        }

        Assert.False(_sequence.Dismiss());
        Assert.Equal(DeathState.DeathScreen, _sequence.State);

        _sequence.Advance(Step); // step 120: 2.0 s
        Assert.True(_sequence.Dismiss());
        Assert.Equal(DeathState.Respawning, _sequence.State);
    }

    [Fact]
    public void Dismiss_EarlyPressIsNotKept_ALaterAdvanceStaysOnTheScreenUntilScreenTime() // criterion 3
    {
        MoveTo(DeathState.DeathScreen);
        _sequence.Advance(_sequence.MinScreenTime / 2);
        Assert.False(_sequence.Dismiss());
        _changes.Clear();

        _sequence.Advance(_sequence.ScreenTime - (_sequence.MinScreenTime / 2) - 1e-6);

        Assert.Equal(DeathState.DeathScreen, _sequence.State);
        Assert.Empty(_changes);
        _sequence.Advance(1e-6);
        Assert.Equal(DeathState.Respawning, _sequence.State);
    }

    [Fact]
    public void Dismiss_TimeCarriedIntoTheDeathScreen_Counts() // criterion 3
    {
        _sequence.EnterVoid(Entry);

        _sequence.Advance(_sequence.FallTime + _sequence.MinScreenTime); // one step through Falling into DeathScreen

        Assert.Equal(DeathState.DeathScreen, _sequence.State);
        Assert.True(_sequence.Dismiss());
        Assert.Equal(DeathState.Respawning, _sequence.State);
    }

    [Theory]
    [InlineData(DeathState.Alive)]
    [InlineData(DeathState.Falling)]
    [InlineData(DeathState.Respawning)]
    [InlineData(DeathState.Failed)]
    public void Dismiss_OtherStates_ReturnsFalseAndChangesNothing(DeathState state) // criterion 3
    {
        MoveTo(state);
        if (state == DeathState.Falling)
        {
            // Long enough in Falling that the time alone would allow a dismiss on the death screen.
            _sequence.Advance(Math.Min(_sequence.MinScreenTime, _sequence.FallTime) * 0.9);
            Assert.Equal(DeathState.Falling, _sequence.State);
        }
        else
        {
            _sequence.Advance(_sequence.MinScreenTime + 1.0);
        }

        _changes.Clear();
        var time = _sequence.TimeInState;
        var losses = _sequence.Losses;

        Assert.False(_sequence.Dismiss());

        Assert.Equal(state, _sequence.State);
        Assert.Equal(time, _sequence.TimeInState);
        Assert.Same(losses, _sequence.Losses);
        Assert.Empty(_changes);
    }

    [Fact]
    public void Dismiss_Twice_SecondReturnsFalse() // criterion 3
    {
        MoveTo(DeathState.DeathScreen);
        _sequence.Advance(_sequence.MinScreenTime);

        Assert.True(_sequence.Dismiss());
        Assert.False(_sequence.Dismiss());
        Assert.Equal(DeathState.Respawning, _sequence.State);
    }

    [Fact]
    public void Dismiss_ThenCompleteRespawn_EndsAlive() // criterion 3
    {
        MoveTo(DeathState.DeathScreen);
        _sequence.Advance(_sequence.MinScreenTime);
        _sequence.Dismiss();
        _changes.Clear();

        _sequence.CompleteRespawn(true);

        Assert.Equal(DeathState.Alive, _sequence.State);
        Assert.Equal([(DeathState.Respawning, DeathState.Alive)], _changes);
    }

    [Fact]
    public void Dismiss_MinScreenTimeZero_WorksAtOnceOnTheDeathScreen()
    {
        _sequence.MinScreenTime = 0;
        MoveTo(DeathState.DeathScreen);

        Assert.True(_sequence.Dismiss());
        Assert.Equal(DeathState.Respawning, _sequence.State);
    }

    [Fact]
    public void Dismiss_MinScreenTimeAboveScreenTime_NeverDismisses_TheScreenEndsByItself()
    {
        _sequence.MinScreenTime = _sequence.ScreenTime + 1;
        MoveTo(DeathState.DeathScreen);
        _sequence.Advance(_sequence.ScreenTime - 1e-6);

        Assert.False(_sequence.Dismiss());
        Assert.Equal(DeathState.DeathScreen, _sequence.State);
        _sequence.Advance(1e-6);
        Assert.Equal(DeathState.Respawning, _sequence.State);
    }

    [Theory]
    [InlineData(true, DeathState.Alive)]
    [InlineData(false, DeathState.Failed)]
    public void CompleteRespawn_Respawning_GoesToAliveOrFailed(bool success, DeathState expected) // criterion 3
    {
        MoveTo(DeathState.Respawning);
        _changes.Clear();

        _sequence.CompleteRespawn(success);

        Assert.Equal(expected, _sequence.State);
        Assert.Equal([(DeathState.Respawning, expected)], _changes);
    }

    [Theory]
    [InlineData(DeathState.Alive)]
    [InlineData(DeathState.Falling)]
    [InlineData(DeathState.DeathScreen)]
    [InlineData(DeathState.Failed)]
    public void CompleteRespawn_OtherState_Throws(DeathState state) // criterion 3
    {
        MoveTo(state);
        _changes.Clear();

        Assert.Throws<InvalidOperationException>(() => _sequence.CompleteRespawn(true));
        Assert.Throws<InvalidOperationException>(() => _sequence.CompleteRespawn(false));
        Assert.Equal(state, _sequence.State);
        Assert.Empty(_changes);
    }

    [Theory]
    [MemberData(nameof(EveryState))]
    public void Reset_AnyState_ReturnsToAliveAndClears(DeathState state) // criterion 3
    {
        MoveTo(state);
        _changes.Clear();

        _sequence.Reset();

        Assert.Equal(DeathState.Alive, _sequence.State);
        Assert.Null(_sequence.Entry);
        Assert.Null(_sequence.Losses);
        Assert.Equal(0, _sequence.TimeInState);
        List<(DeathState, DeathState)> expected = state == DeathState.Alive ? [] : [(state, DeathState.Alive)];
        Assert.Equal(expected, _changes);
    }

    [Fact]
    public void EnterVoid_AfterRespawn_StartsANewSequence() // criterion 3
    {
        MoveTo(DeathState.Respawning);
        _sequence.CompleteRespawn(true);
        _player.AddMoney(7);

        Assert.True(_sequence.EnterVoid(Entry));

        Assert.Equal(DeathState.Falling, _sequence.State);
        Assert.Equal(7, _sequence.Losses!.Money);
        Assert.Empty(_sequence.Losses.Gear);
        Assert.Equal(2, _died);
    }

    [Fact]
    public void EnterVoid_AfterReset_StartsANewSequence()
    {
        MoveTo(DeathState.DeathScreen);
        _sequence.Reset();

        Assert.True(_sequence.EnterVoid(Entry));
        Assert.Equal(DeathState.Falling, _sequence.State);
        Assert.Equal(2, _died);
    }

    [Fact]
    public void StateChanged_FullSequence_RaisedOnlyOnChanges() // criterion 3
    {
        _sequence.Advance(1); // Alive: no change
        _sequence.EnterVoid(Entry);
        _sequence.EnterVoid(Entry); // ignored
        for (var i = 0; i < 600; i++)
        {
            _sequence.Advance(Step);
        }

        _sequence.CompleteRespawn(true);
        _sequence.Reset(); // already Alive

        Assert.Equal(
            [
                (DeathState.Alive, DeathState.Falling),
                (DeathState.Falling, DeathState.DeathScreen),
                (DeathState.DeathScreen, DeathState.Respawning),
                (DeathState.Respawning, DeathState.Alive),
            ],
            _changes);
        Assert.Equal(1, _died);
    }

    /// <summary>Drives the sequence into <paramref name="state"/> through its public API.</summary>
    private void MoveTo(DeathState state)
    {
        if (state == DeathState.Alive)
        {
            return;
        }

        _sequence.EnterVoid(Entry);
        if (state == DeathState.Falling)
        {
            Assert.Equal(DeathState.Falling, _sequence.State);
            return;
        }

        _sequence.Advance(_sequence.FallTime);
        if (state == DeathState.DeathScreen)
        {
            Assert.Equal(DeathState.DeathScreen, _sequence.State);
            return;
        }

        _sequence.Advance(_sequence.ScreenTime);
        Assert.Equal(DeathState.Respawning, _sequence.State);
        if (state == DeathState.Failed)
        {
            _sequence.CompleteRespawn(false);
            Assert.Equal(DeathState.Failed, _sequence.State);
        }
    }
}
