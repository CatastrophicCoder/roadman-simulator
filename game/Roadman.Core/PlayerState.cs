namespace Roadman.Core;

/// <summary>
/// The player's money, gear and wanted level, and the rules that change them.
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

    public int Money { get; private set; }

    public IReadOnlySet<string> Gear => _gear;

    public int WantedLevel { get; private set; }

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

    /// <summary>Any death costs all the money and gear, and clears the wanted level.</summary>
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
