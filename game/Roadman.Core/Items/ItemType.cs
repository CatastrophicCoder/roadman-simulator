namespace Roadman.Core.Items;

/// <summary>What kind of item an <see cref="ItemType"/> is (<c>docs/specs/inventory.md</c>, "Roadman.Core").</summary>
public enum ItemCategory
{
    /// <summary>Goes in <see cref="EquipSlot.Weapon"/>.</summary>
    Weapon,

    /// <summary>Goes in <see cref="EquipSlot.Outfit"/>.</summary>
    Clothing,

    /// <summary>Can't be equipped.</summary>
    Other,
}

/// <summary>The player's equipment slots, exactly these two; <see cref="ItemCatalog.Slots"/> lists them in slot order.</summary>
public enum EquipSlot
{
    /// <summary>One <see cref="ItemCategory.Weapon"/> item.</summary>
    Weapon,

    /// <summary>One <see cref="ItemCategory.Clothing"/> item.</summary>
    Outfit,
}

/// <summary>
/// One kind of item. Immutable; the record itself checks nothing, <see cref="ItemCatalog"/> holds the rules.
/// </summary>
/// <param name="Id">A lowercase slug (<see cref="Maps.MapPack.SlugPattern"/>), unique in its catalogue.</param>
/// <param name="DisplayName">The English name shown in the game; non-blank, unique in its catalogue ignoring case.</param>
/// <param name="Category">The category, which decides the slot (<see cref="ItemCatalog.SlotFor(ItemType)"/>).</param>
/// <param name="Description">One line of English: non-blank, no line break.</param>
public sealed record ItemType(string Id, string DisplayName, ItemCategory Category, string Description);
