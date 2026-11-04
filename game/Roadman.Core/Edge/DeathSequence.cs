using Roadman.Core.Maps;

namespace Roadman.Core.Edge;

/// <summary>The states of the player's death and respawn (<see cref="DeathSequence"/>).</summary>
public enum DeathState
{
    /// <summary>Playing normally.</summary>
    Alive,

    /// <summary>Falling into the void, for <see cref="DeathSequence.FallTime"/>.</summary>
    Falling,

    /// <summary>The death screen, for <see cref="DeathSequence.ScreenTime"/>.</summary>
    DeathScreen,

    /// <summary>Waiting for the engine to place the player again (<see cref="DeathSequence.CompleteRespawn"/>).</summary>
    Respawning,

    /// <summary>The respawn found no place for the player.</summary>
    Failed,
}

/// <summary>What the player lost at the moment of death.</summary>
/// <param name="Money">The money the player had.</param>
/// <param name="Gear">The gear item ids the player had, sorted ordinal (<see cref="StringComparer.Ordinal"/>).</param>
public sealed record DeathLosses(int Money, IReadOnlyList<string> Gear);

/// <summary>Where, and in which outward direction, the player entered the void.</summary>
/// <param name="Position">The player's position at entry, Godot local metres.</param>
/// <param name="Outward">The unit horizontal outward direction there (<see cref="EdgeGeometry.OutwardAt"/>).</param>
public sealed record VoidEntry(LocalPoint Position, PlanarPoint Outward);

/// <summary>
/// The death and respawn state machine (<c>docs/specs/world-edge-game.md</c>, "Death sequence"): <see cref="EnterVoid"/>
/// in <see cref="DeathState.Alive"/> records the losses, calls <see cref="PlayerState.Die"/> and starts the fall;
/// <see cref="Advance"/> moves through <see cref="DeathState.Falling"/> and <see cref="DeathState.DeathScreen"/> to
/// <see cref="DeathState.Respawning"/>; <see cref="CompleteRespawn"/> ends it. <see cref="StateChanged"/> is raised
/// only on real changes.
/// </summary>
public sealed class DeathSequence
{
    /// <summary>Seconds: a timed state ends when its elapsed time is at least its limit minus this.</summary>
    public const double BoundaryTolerance = 1e-9;

    private readonly PlayerState _player;
    private double _fallTime = 3.0;
    private double _screenTime = 3.0;

    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    public DeathSequence(PlayerState player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _player = player;
    }

    /// <summary>Raised with (from, to) on every state change, never when the state stays the same.</summary>
    public event Action<DeathState, DeathState>? StateChanged;

    /// <summary>Seconds in <see cref="DeathState.Falling"/>.</summary>
    /// <exception cref="ArgumentException">Set to a negative or non-finite value.</exception>
    public double FallTime
    {
        get => _fallTime;
        set => _fallTime = CheckDuration(value, nameof(FallTime));
    }

    /// <summary>Seconds in <see cref="DeathState.DeathScreen"/>.</summary>
    /// <exception cref="ArgumentException">Set to a negative or non-finite value.</exception>
    public double ScreenTime
    {
        get => _screenTime;
        set => _screenTime = CheckDuration(value, nameof(ScreenTime));
    }

    /// <summary>The current state; starts at <see cref="DeathState.Alive"/>.</summary>
    public DeathState State { get; private set; } = DeathState.Alive;

    /// <summary>
    /// Seconds since the current state began, including the time carried over from a state that ended inside the same
    /// <see cref="Advance"/> call. It grows in every state, also in the untimed ones.
    /// </summary>
    public double TimeInState { get; private set; }

    /// <summary>The losses recorded by the last <see cref="EnterVoid"/>; null before it and after <see cref="Reset"/>.</summary>
    public DeathLosses? Losses { get; private set; }

    /// <summary>The entry passed to the last <see cref="EnterVoid"/>; null before it and after <see cref="Reset"/>.</summary>
    public VoidEntry? Entry { get; private set; }

    /// <summary>
    /// In <see cref="DeathState.Alive"/>: records <see cref="Losses"/> (the money and the sorted gear ids), then calls
    /// <see cref="PlayerState.Die"/> once, stores <paramref name="entry"/> and moves to
    /// <see cref="DeathState.Falling"/>. In any other state it changes nothing.
    /// </summary>
    /// <returns>True when the sequence started.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    public bool EnterVoid(VoidEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (State != DeathState.Alive)
        {
            return false;
        }

        var gear = _player.Gear.ToList();
        gear.Sort(StringComparer.Ordinal);
        Losses = new DeathLosses(_player.Money, gear.AsReadOnly());
        Entry = entry;
        _player.Die();
        ChangeState(DeathState.Falling, 0);
        return true;
    }

    /// <summary>
    /// Advances the timers by <paramref name="dt"/> seconds. <see cref="DeathState.Falling"/> ends after
    /// <see cref="FallTime"/>, <see cref="DeathState.DeathScreen"/> after <see cref="ScreenTime"/> (each when the
    /// elapsed time reaches the limit minus <see cref="BoundaryTolerance"/>). A large step can pass through both, with
    /// a <see cref="StateChanged"/> for each and the remainder carried into <see cref="TimeInState"/>. In
    /// <see cref="DeathState.Alive"/>, <see cref="DeathState.Respawning"/> and <see cref="DeathState.Failed"/> only
    /// <see cref="TimeInState"/> grows.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="dt"/> is negative or not finite (in every state).</exception>
    public void Advance(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0)
        {
            throw new ArgumentException($"dt must be finite and at least 0, is {dt}", nameof(dt));
        }

        TimeInState += dt;
        while (true)
        {
            double limit;
            DeathState next;
            switch (State)
            {
                case DeathState.Falling:
                    limit = FallTime;
                    next = DeathState.DeathScreen;
                    break;
                case DeathState.DeathScreen:
                    limit = ScreenTime;
                    next = DeathState.Respawning;
                    break;
                default:
                    return;
            }

            if (TimeInState < limit - BoundaryTolerance)
            {
                return;
            }

            // A handler may change the state itself (Reset on a map reload); then stop here.
            var expected = next;
            ChangeState(next, Math.Max(0, TimeInState - limit));
            if (State != expected)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Ends <see cref="DeathState.Respawning"/>: <see cref="DeathState.Alive"/> when the player was placed, else
    /// <see cref="DeathState.Failed"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The state isn't <see cref="DeathState.Respawning"/>.</exception>
    public void CompleteRespawn(bool success)
    {
        if (State != DeathState.Respawning)
        {
            throw new InvalidOperationException($"CompleteRespawn needs state Respawning, is {State}");
        }

        ChangeState(success ? DeathState.Alive : DeathState.Failed, 0);
    }

    /// <summary>
    /// Returns to <see cref="DeathState.Alive"/> from any state (a map reload), with <see cref="TimeInState"/> 0 and
    /// <see cref="Entry"/> and <see cref="Losses"/> cleared. <see cref="StateChanged"/> is raised only when the state
    /// wasn't <see cref="DeathState.Alive"/>. A <see cref="PlayerState.Die"/> that already ran isn't undone.
    /// </summary>
    public void Reset()
    {
        Entry = null;
        Losses = null;
        ChangeState(DeathState.Alive, 0);
    }

    private void ChangeState(DeathState to, double carriedTime)
    {
        var from = State;
        State = to;
        TimeInState = carriedTime;
        if (from != to)
        {
            StateChanged?.Invoke(from, to);
        }
    }

    private static double CheckDuration(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentException($"{name} must be finite and at least 0, is {value}", name);
        }

        return value;
    }
}
