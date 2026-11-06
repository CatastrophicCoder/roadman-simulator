using Roadman.Core.Items;

namespace Roadman.Core.Tests.Items;

/// <summary>
/// Item catalogues built for the tests (<c>docs/specs/inventory.md</c>: "Tests build their own catalogues"), so the
/// tests don't depend on <see cref="ItemCatalog.Default"/>'s placeholder items.
/// </summary>
internal static class TestCatalogs
{
    /// <summary>
    /// The ids the older money, gear, save and death tests use, all <see cref="ItemCategory.Other"/> (no slot), so
    /// those tests keep their meaning. <c>a1</c> and <c>a_b</c> sort differently by ordinal ('1' 49 before '_' 95)
    /// than by a culture's rules (punctuation before digits).
    /// </summary>
    public static ItemCatalog Plain { get; } = new(
        new[] { "a", "a1", "a_b", "b", "c", "crowbar", "kit", "knife", "map", "phone", "pistol", "torch", "x", "z" }
            .Select(id => Item(id)));

    /// <summary>
    /// Two items of each category: weapons <c>bat</c> and <c>crowbar</c>, clothing <c>suit</c> and <c>hoodie</c>,
    /// other <c>phone</c> and <c>lock</c>.
    /// </summary>
    public static ItemCatalog Mixed { get; } = new(
    [
        Item("bat", ItemCategory.Weapon, "Bat"),
        Item("crowbar", ItemCategory.Weapon, "Crowbar"),
        Item("suit", ItemCategory.Clothing, "Suit"),
        Item("hoodie", ItemCategory.Clothing, "Hoodie"),
        Item("phone", ItemCategory.Other, "Phone"),
        Item("lock", ItemCategory.Other, "Lock"),
    ]);

    /// <summary>An item with a name and description made from its id unless given.</summary>
    public static ItemType Item(string id, ItemCategory category = ItemCategory.Other, string? name = null, string? description = null) =>
        new(id, name ?? $"Test item {id}", category, description ?? $"Test description of {id}.");
}
