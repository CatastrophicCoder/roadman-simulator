using Roadman.Core.Maps;
using Roadman.Core.Saves;

namespace Roadman.Core;

/// <summary>
/// The player's money, gear, wanted level and position, and the rules that change them.
/// Events are raised only when a value actually changes; <see cref="Died"/> is raised on every <see cref="Die"/>.
/// </summary>
public sealed class PlayerState
{
    public const int MaxWantedLevel = 5;

    private readonly HashSet<string> _gear = new(StringComparer.Ordinal);

    public event Action<int>? MoneyChanged;
    public event Action? GearChanged;
    public event Action<int>? WantedLevelChanged;
    public event Action? Died;

    /// <summary>Raised with the new value when <see cref="SetPosition"/> changes <see cref="Position"/>.</summary>
    public event Action<LocalPoint>? PositionChanged;

    public int Money { get; private set; }

    public IReadOnlySet<string> Gear => _gear;

    public int WantedLevel { get; private set; }

    /// <summary>
    /// The player's last committed position in Godot local metres (the body's global position, written by the player
    /// node every physics frame); the origin until the first <see cref="SetPosition"/>.
    /// </summary>
    public LocalPoint Position { get; private set; }

    /// <summary>Sets <see cref="Position"/>; raises <see cref="PositionChanged"/> only when the value changes.</summary>
    /// <exception cref="ArgumentException">A non-finite component.</exception>
    public void SetPosition(LocalPoint position)
    {
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
        {
            throw new ArgumentException($"position must be finite, is {position}", nameof(position));
        }

        if (position == Position)
        {
            return;
        }

        Position = position;
        PositionChanged?.Invoke(Position);
    }

    public void AddMoney(int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (amount == 0)
        {
            return;
        }

        Money = checked(Money + amount);
        MoneyChanged?.Invoke(Money);
    }

    /// <summary>Spends <paramref name="amount"/> if the player has enough money.</summary>
    /// <returns>False, with nothing changed, when funds are short.</returns>
    public bool TrySpend(int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (amount > Money)
        {
            return false;
        }

        if (amount > 0)
        {
            Money -= amount;
            MoneyChanged?.Invoke(Money);
        }

        return true;
    }

    /// <returns>True if the item was added, false if the player already had it.</returns>
    public bool AddGear(string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (!_gear.Add(itemId))
        {
            return false;
        }

        GearChanged?.Invoke();
        return true;
    }

    /// <returns>True if the item was removed, false if the player didn't have it.</returns>
    public bool RemoveGear(string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (!_gear.Remove(itemId))
        {
            return false;
        }

        GearChanged?.Invoke();
        return true;
    }

    /// <summary>Sets the wanted level, clamped to 0..<see cref="MaxWantedLevel"/>.</summary>
    public void SetWantedLevel(int level)
    {
        var clamped = Math.Clamp(level, 0, MaxWantedLevel);
        if (clamped == WantedLevel)
        {
            return;
        }

        WantedLevel = clamped;
        WantedLevelChanged?.Invoke(WantedLevel);
    }

    /// <summary>
    /// The save's player part: money, gear (sorted ordinal), wanted level and, with <paramref name="includePosition"/>,
    /// <see cref="Position"/>. <see cref="SavePlayer.FacingYaw"/> is always null: <see cref="PlayerState"/> has no
    /// facing, so the caller sets it from the player node (<c>with { FacingYaw = yaw }</c>) before saving, and leaves
    /// it null when the position is empty.
    /// </summary>
    /// <param name="includePosition">False for a save written during a death: the position is empty.</param>
    public SavePlayer Snapshot(bool includePosition) =>
        new(includePosition ? Position : null, null, Money, _gear, WantedLevel);

    /// <summary>
    /// Applies a save's player part. Validates first (<see cref="SavePlayer.Problem"/> without the facing pair:
    /// money ≥ 0, wanted level 0..<see cref="MaxWantedLevel"/>, no blank or repeated gear id, a finite position, a
    /// finite facing in (−π, π], no facing without a position); an invalid snapshot throws and changes nothing. Then
    /// sets money, gear, wanted level and, when the snapshot has one, the position, in that order. Each change event
    /// fires once and only when its value changes; <see cref="Died"/> never fires. An empty position leaves
    /// <see cref="Position"/> as it is. The facing is ignored (the player node applies it).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentException">The snapshot is invalid; the message names the key.</exception>
    public void Restore(SavePlayer snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Problem(requireFacingPair: false) is { } problem)
        {
            throw new ArgumentException($"invalid snapshot: '{problem.KeyPath}' {problem.Problem}", nameof(snapshot));
        }

        SetAll(snapshot.Money, snapshot.Gear, snapshot.WantedLevel, snapshot.Position);
    }

    /// <summary>
    /// A new game's values: money 0, no gear, wanted level 0, position at the origin. Change events fire for the
    /// values that change; <see cref="Died"/> doesn't.
    /// </summary>
    public void Reset() => SetAll(0, [], 0, default(LocalPoint));

    private void SetAll(int money, IReadOnlyList<string> gear, int wantedLevel, LocalPoint? position)
    {
        if (money != Money)
        {
            Money = money;
            MoneyChanged?.Invoke(Money);
        }

        if (!_gear.SetEquals(gear))
        {
            _gear.Clear();
            _gear.UnionWith(gear);
            GearChanged?.Invoke();
        }

        SetWantedLevel(wantedLevel);
        if (position is { } p)
        {
            SetPosition(p);
        }
    }

    /// <summary>
    /// Any death costs all the money and gear, and clears the wanted level. The position stays as it is; respawn
    /// belongs to the death and respawn spec.
    /// </summary>
    public void Die()
    {
        if (Money != 0)
        {
            Money = 0;
            MoneyChanged?.Invoke(Money);
        }

        if (_gear.Count > 0)
        {
            _gear.Clear();
            GearChanged?.Invoke();
        }

        SetWantedLevel(0);
        Died?.Invoke();
    }
}
