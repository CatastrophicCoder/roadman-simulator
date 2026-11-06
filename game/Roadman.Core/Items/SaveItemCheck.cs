using Roadman.Core.Saves;

namespace Roadman.Core.Items;

/// <summary>A known item that was in the wrong slot of a save, unequipped and kept as held gear.</summary>
/// <param name="Slot">The slot it was in.</param>
/// <param name="ItemId">The item's id.</param>
public sealed record SaveItemCheckUnequip(EquipSlot Slot, string ItemId);

/// <summary>The result of <see cref="SaveItemCheck.Clean"/>.</summary>
/// <param name="Player">The cleaned player part; equal to the input when nothing changed.</param>
/// <param name="RemovedIds">The gear ids not in the catalogue, dropped, sorted ordinal.</param>
/// <param name="Unequipped">The known items that were in a slot that doesn't fit them, in slot order.</param>
public sealed record SaveItemCheckResult(
    SavePlayer Player,
    IReadOnlyList<string> RemovedIds,
    IReadOnlyList<SaveItemCheckUnequip> Unequipped)
{
    /// <summary>True when anything was removed or unequipped.</summary>
    public bool Changed => RemovedIds.Count > 0 || Unequipped.Count > 0;
}

/// <summary>
/// The load cleanup of a save's items against the catalogue (<c>docs/specs/inventory.md</c>, "Loading: unknown
/// items"). The save format doesn't know the catalogue, so <c>Main</c> runs this before
/// <see cref="PlayerState.Restore"/> on every load.
/// </summary>
public static class SaveItemCheck
{
    /// <summary>
    /// Drops every gear id not in <paramref name="catalog"/> (emptying its slot when it is equipped; such an id is
    /// listed in <see cref="SaveItemCheckResult.RemovedIds"/> only), and unequips a known item in a slot that doesn't
    /// fit its category, keeping it held. Everything else is kept as it is.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> or <paramref name="catalog"/> is null.</exception>
    public static SaveItemCheckResult Clean(SavePlayer player, ItemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(catalog);

        var removed = player.Gear.Where(id => !catalog.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
        removed.Sort(StringComparer.Ordinal);
        var unequipped = new List<SaveItemCheckUnequip>();

        string? Check(EquipSlot slot, string? id)
        {
            if (id is null)
            {
                return null;
            }

            if (!catalog.TryGet(id, out var item))
            {
                // Unknown: dropped with the gear (or, not being held, simply emptied); reported in RemovedIds.
                if (!removed.Contains(id, StringComparer.Ordinal))
                {
                    removed.Add(id);
                    removed.Sort(StringComparer.Ordinal);
                }

                return null;
            }

            if (ItemCatalog.SlotFor(item) != slot)
            {
                unequipped.Add(new SaveItemCheckUnequip(slot, id));
                return null;
            }

            return id;
        }

        var weapon = Check(EquipSlot.Weapon, player.Equipped.Weapon);
        var outfit = Check(EquipSlot.Outfit, player.Equipped.Outfit);
        if (removed.Count == 0 && unequipped.Count == 0)
        {
            return new SaveItemCheckResult(player, [], []);
        }

        var cleaned = player with
        {
            Gear = player.Gear.Where(catalog.Contains).ToList(),
            Equipped = new SaveEquipped(weapon, outfit),
        };
        return new SaveItemCheckResult(cleaned, removed.AsReadOnly(), unequipped.AsReadOnly());
    }
}
