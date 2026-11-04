using Roadman.Core.Edge;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Edge;

/// <summary><see cref="DeathSequence"/>: <c>docs/specs/world-edge-game.md</c>, acceptance criterion 3.</summary>
public class DeathSequenceTests
{
    private const double Step = 1.0 / 60;

    private static readonly VoidEntry Entry = new(new LocalPoint(-1155, 86.5, 980), new PlanarPoint(-1, 0));

    private readonly PlayerState _player = new();
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
        Assert.Equal(3.0, _sequence.ScreenTime);
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
        var player = new PlayerState();
        player.AddGear("knife");
        player.AddGear("Zippo"); // ordinal: 'Z' (90) before 'k' (107); a culture sort would put knife first
        var sequence = new DeathSequence(player);

        sequence.EnterVoid(Entry);

        Assert.Equal(["Zippo", "knife"], sequence.Losses!.Gear);
        Assert.Equal(0, sequence.Losses.Money);
    }

    [Fact]
    public void EnterVoid_NothingToLose_RecordsEmptyLosses()
    {
        var sequence = new DeathSequence(new PlayerState());

        Assert.True(sequence.EnterVoid(Entry));

        Assert.Equal(0, sequence.Losses!.Money);
        Assert.Empty(sequence.Losses.Gear);
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
    public void Advance_SixtyHertz_ReachesDeathScreenOnStep180AndRespawningOnStep360() // criterion 3
    {
        _sequence.EnterVoid(Entry);
        _changes.Clear();
        var deathScreenStep = 0;
        var respawningStep = 0;

        for (var step = 1; step <= 400; step++)
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
        Assert.Equal(360, respawningStep);
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
        _sequence.Advance(3.0 - 1e-6);
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

        _sequence.Advance(2.5);
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
        Assert.Equal(4.0, _sequence.TimeInState, 1e-9);
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
        for (var i = 0; i < 400; i++)
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
