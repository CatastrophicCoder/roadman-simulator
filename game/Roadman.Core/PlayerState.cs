using Roadman.Core.Items;
using Roadman.Core.Maps;
using Roadman.Core.Saves;

namespace Roadman.Core;

/// <summary>
/// The player's money, gear, equipped items, wanted level and position, and the rules that change them.
/// Events are raised only when a value actually changes; <see cref="Died"/> is raised on every <see cref="Die"/>.
/// Invariants: every held item is in <see cref="Catalog"/>, and an equipped item is always held and fits its slot
/// (<c>docs/specs/inventory.md</c>).
/// </summary>
public sealed class PlayerState
{
    public const int MaxWantedLevel = 5;

    private readonly HashSet<string> _gear = new(StringComparer.Ordinal);
    private readonly Dictionary<EquipSlot, string> _equipped = [];

    /// <param name="catalog">The known item types; <see cref="AddGear"/> and <see cref="Restore"/> accept only these.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is null.</exception>
    public PlayerState(ItemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
    }

    public event Action<int>? MoneyChanged;
    public event Action? GearChanged;

    /// <summary>Raised once for each slot whose content changes, with that slot.</summary>
    public event Action<EquipSlot>? EquipmentChanged;

    public event Action<int>? WantedLevelChanged;
    public event Action? Died;

    /// <summary>Raised with the new value when <see cref="SetPosition"/> changes <see cref="Position"/>.</summary>
    public event Action<LocalPoint>? PositionChanged;

    public int Money { get; private set; }

    /// <summary>The known item types.</summary>
    public ItemCatalog Catalog { get; }

    public IReadOnlySet<string> Gear => _gear;

    /// <summary>The item id in each filled slot; an empty slot has no entry.</summary>
    public IReadOnlyDictionary<EquipSlot, string> Equipped => _equipped;

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

    /// <summary>Adds one of item <paramref name="itemId"/>; the player holds at most one of each item type.</summary>
    /// <returns>True if the item was added, false if the player already had it.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="itemId"/> is null or blank, or not in <see cref="Catalog"/> (the message names it); nothing changes.
    /// </exception>
    public bool AddGear(string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (!Catalog.Contains(itemId))
        {
            throw new ArgumentException($"item '{itemId}' isn't in the item catalogue", nameof(itemId));
        }

        if (!_gear.Add(itemId))
        {
            return false;
        }

        GearChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Removes item <paramref name="itemId"/>. An equipped item is unequipped first (<see cref="EquipmentChanged"/>,
    /// then <see cref="GearChanged"/>).
    /// </summary>
    /// <returns>True if the item was removed, false if the player didn't have it.</returns>
    public bool RemoveGear(string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (!_gear.Contains(itemId))
        {
            return false;
        }

        foreach (var slot in ItemCatalog.Slots)
        {
            if (EquippedIn(slot) == itemId)
            {
                Unequip(slot);
            }
        }

        _gear.Remove(itemId);
        GearChanged?.Invoke();
        return true;
    }

    /// <summary>The item id in <paramref name="slot"/>, or null when it is empty.</summary>
    public string? EquippedIn(EquipSlot slot) => _equipped.TryGetValue(slot, out var id) ? id : null;

    /// <summary>True when <paramref name="itemId"/> is in a slot.</summary>
    public bool IsEquipped(string itemId) => itemId is not null && _equipped.ContainsValue(itemId);

    /// <summary>
    /// Puts item <paramref name="itemId"/> in its slot (<see cref="ItemCatalog.SlotFor(ItemType)"/>), replacing the
    /// item there, which stays held. Raises <see cref="EquipmentChanged"/> once when the slot changes.
    /// </summary>
    /// <returns>True if the slot changed; false if the item was already in it.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="itemId"/> is null, blank or not in <see cref="Catalog"/> (the message names it); nothing changes.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The item isn't held, or is an <see cref="ItemCategory.Other"/> item with no slot (the message names it); nothing
    /// changes.
    /// </exception>
    public bool Equip(string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (!Catalog.TryGet(itemId, out var item))
        {
            throw new ArgumentException($"item '{itemId}' isn't in the item catalogue", nameof(itemId));
        }

        if (!_gear.Contains(itemId))
        {
            throw new InvalidOperationException($"item '{itemId}' isn't held, so it can't be equipped");
        }

        if (ItemCatalog.SlotFor(item) is not { } slot)
        {
            throw new InvalidOperationException($"item '{itemId}' is a {item.Category} item and can't be equipped");
        }

        return SetSlot(slot, itemId);
    }

    /// <summary>Empties <paramref name="slot"/>; raises <see cref="EquipmentChanged"/> once when it held an item.</summary>
    /// <returns>True if the slot held an item.</returns>
    public bool Unequip(EquipSlot slot) => SetSlot(slot, null);

    private bool SetSlot(EquipSlot slot, string? itemId)
    {
        if (EquippedIn(slot) == itemId)
        {
            return false;
        }

        if (itemId is null)
        {
            _equipped.Remove(slot);
        }
        else
        {
            _equipped[slot] = itemId;
        }

        EquipmentChanged?.Invoke(slot);
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
    /// The save's player part: money, gear (sorted ordinal), equipped items, wanted level and, with <paramref name="includePosition"/>,
    /// <see cref="Position"/>. <see cref="SavePlayer.FacingYaw"/> is always null: <see cref="PlayerState"/> has no
    /// facing, so the caller sets it from the player node (<c>with { FacingYaw = yaw }</c>) before saving, and leaves
    /// it null when the position is empty.
    /// </summary>
    /// <param name="includePosition">False for a save written during a death: the position is empty.</param>
    public SavePlayer Snapshot(bool includePosition) =>
        new(includePosition ? Position : null, null, Money, _gear, ToSave(), WantedLevel);

    /// <summary>
    /// Applies a save's player part. Validates first (<see cref="SavePlayer.Problem"/> without the facing pair:
    /// money ≥ 0, wanted level 0..<see cref="MaxWantedLevel"/>, no blank or repeated gear id, a finite position, a
    /// finite facing in (−π, π], no facing without a position, each equipped id null or held), and against
    /// <see cref="Catalog"/> (every gear id known, every equipped item in the slot of its category); an invalid
    /// snapshot throws and changes nothing. Callers clean a loaded save with <see cref="SaveItemCheck.Clean"/> first.
    /// Then sets money, the slots whose items are no longer held, gear, the slots, wanted level and, when the
    /// snapshot has one, the position, in that order. Each change event fires once and only when its value changes;
    /// <see cref="Died"/> never fires. An empty position leaves <see cref="Position"/> as it is. The facing is
    /// ignored (the player node applies it).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentException">The snapshot is invalid; the message names the key or the item id.</exception>
    public void Restore(SavePlayer snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Problem(requireFacingPair: false) is { } problem)
        {
            throw new ArgumentException($"invalid snapshot: '{problem.KeyPath}' {problem.Problem}", nameof(snapshot));
        }

        foreach (var id in snapshot.Gear)
        {
            if (!Catalog.Contains(id))
            {
                throw new ArgumentException($"invalid snapshot: 'gear' item '{id}' isn't in the item catalogue", nameof(snapshot));
            }
        }

        var equipped = FromSave(snapshot.Equipped);
        foreach (var (slot, id) in equipped)
        {
            if (ItemCatalog.SlotFor(Catalog.Get(id)) != slot)
            {
                throw new ArgumentException(
                    $"invalid snapshot: 'equipped.{SlotKey(slot)}' item '{id}' doesn't fit the {slot} slot", nameof(snapshot));
            }
        }

        SetAll(snapshot.Money, snapshot.Gear, equipped, snapshot.WantedLevel, snapshot.Position);
    }

    /// <summary>
    /// A new game's values: money 0, no gear, empty slots, wanted level 0, position at the origin. Change events fire
    /// for the values that change; <see cref="Died"/> doesn't.
    /// </summary>
    public void Reset() => SetAll(0, [], new Dictionary<EquipSlot, string>(), 0, default(LocalPoint));

    private void SetAll(
        int money, IReadOnlyList<string> gear, IReadOnlyDictionary<EquipSlot, string> equipped, int wantedLevel, LocalPoint? position)
    {
        if (money != Money)
        {
            Money = money;
            MoneyChanged?.Invoke(Money);
        }

        // Keep the invariant at every event: a slot whose item won't be held is emptied before the gear changes.
        foreach (var slot in ItemCatalog.Slots)
        {
            if (EquippedIn(slot) is { } held && !gear.Contains(held, StringComparer.Ordinal))
            {
                SetSlot(slot, null);
            }
        }

        if (!_gear.SetEquals(gear))
        {
            _gear.Clear();
            _gear.UnionWith(gear);
            GearChanged?.Invoke();
        }

        foreach (var slot in ItemCatalog.Slots)
        {
            SetSlot(slot, equipped.TryGetValue(slot, out var id) ? id : null);
        }

        SetWantedLevel(wantedLevel);
        if (position is { } p)
        {
            SetPosition(p);
        }
    }

    /// <summary>
    /// Any death empties the slots (<see cref="EquipmentChanged"/> per slot that held an item, in slot order), costs
    /// all the money and gear, and clears the wanted level; <see cref="Died"/> last. The position stays as it is;
    /// respawn belongs to the death and respawn spec.
    /// </summary>
    public void Die()
    {
        foreach (var slot in ItemCatalog.Slots)
        {
            Unequip(slot);
        }

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

    /// <summary>The save key of <paramref name="slot"/> under <c>player.equipped</c>.</summary>
    private static string SlotKey(EquipSlot slot) => slot == EquipSlot.Weapon ? "weapon" : "outfit";

    private static Dictionary<EquipSlot, string> FromSave(SaveEquipped equipped)
    {
        var result = new Dictionary<EquipSlot, string>();
        if (equipped.Weapon is { } weapon)
        {
            result[EquipSlot.Weapon] = weapon;
        }

        if (equipped.Outfit is { } outfit)
        {
            result[EquipSlot.Outfit] = outfit;
        }

        return result;
    }

    private SaveEquipped ToSave() => new(EquippedIn(EquipSlot.Weapon), EquippedIn(EquipSlot.Outfit));
}
