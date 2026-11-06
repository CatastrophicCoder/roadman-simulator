using System.Diagnostics.CodeAnalysis;
using Roadman.Core.Maps;

namespace Roadman.Core.Items;

/// <summary>
/// The set of known item types (<c>docs/specs/inventory.md</c>, "Roadman.Core"). Immutable. <see cref="Default"/> is
/// the game's catalogue; tests build their own.
/// </summary>
public sealed class ItemCatalog
{
    private readonly Dictionary<string, ItemType> _byId = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds a catalogue from <paramref name="items"/>, checking each in list order.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> or one of its items is null.</exception>
    /// <exception cref="ArgumentException">
    /// The message names the offending id: an id that isn't a lowercase slug (<see cref="MapPack.IsSlug"/>), a repeated
    /// id, a blank display name, a display name already used (ignoring case; the second item's id is named), a blank
    /// description or one with a line break, or a category value that isn't defined.
    /// </exception>
    public ItemCatalog(IEnumerable<ItemType> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<ItemType>();
        foreach (var item in items)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(items), "the item list holds a null item");
            }

            var id = item.Id;
            if (!MapPack.IsSlug(id))
            {
                throw new ArgumentException(
                    $"item id '{id}' must be a lowercase slug (letters and digits joined by single '-' or '_')", nameof(items));
            }

            if (_byId.ContainsKey(id))
            {
                throw new ArgumentException($"item id '{id}' appears more than once", nameof(items));
            }

            if (string.IsNullOrWhiteSpace(item.DisplayName))
            {
                throw new ArgumentException($"item '{id}' has a blank display name", nameof(items));
            }

            if (names.TryGetValue(item.DisplayName, out var other))
            {
                throw new ArgumentException(
                    $"item '{id}' has the display name '{item.DisplayName}', which item '{other}' already uses", nameof(items));
            }

            if (string.IsNullOrWhiteSpace(item.Description))
            {
                throw new ArgumentException($"item '{id}' has a blank description", nameof(items));
            }

            if (item.Description.Contains('\n') || item.Description.Contains('\r'))
            {
                throw new ArgumentException($"item '{id}' has a description with a line break", nameof(items));
            }

            if (!Enum.IsDefined(item.Category))
            {
                throw new ArgumentException($"item '{id}' has an undefined category value {(int)item.Category}", nameof(items));
            }

            _byId.Add(id, item);
            names.Add(item.DisplayName, id);
            all.Add(item);
        }

        all.Sort(static (a, b) =>
        {
            var byCategory = a.Category.CompareTo(b.Category);
            if (byCategory != 0)
            {
                return byCategory;
            }

            var byName = StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });
        All = all.AsReadOnly();
    }

    /// <summary>
    /// The game's catalogue: the placeholder items of the spec's table. It ships in release builds too.
    /// </summary>
    public static ItemCatalog Default { get; } = new(
    [
        new("baseball_bat", "Baseball bat", ItemCategory.Weapon, "A wooden bat. Not for baseball."),
        new("crowbar", "Crowbar", ItemCategory.Weapon, "Opens things that were meant to stay shut."),
        new("tracksuit", "Tracksuit", ItemCategory.Clothing, "Matching top and bottoms, slightly too shiny."),
        new("hoodie", "Hoodie", ItemCategory.Clothing, "Hood up, head down."),
        new("phone", "Phone", ItemCategory.Other, "Cracked screen, one bar of signal."),
        new("bike_lock", "Bike lock", ItemCategory.Other, "A heavy chain lock. The bike is long gone."),
    ]);

    /// <summary>The equipment slots in slot order: <see cref="EquipSlot.Weapon"/>, <see cref="EquipSlot.Outfit"/>.</summary>
    public static IReadOnlyList<EquipSlot> Slots { get; } = [EquipSlot.Weapon, EquipSlot.Outfit];

    /// <summary>
    /// Every item type, sorted by category (<see cref="ItemCategory"/> order), then display name (ordinal ignoring
    /// case), then id (ordinal).
    /// </summary>
    public IReadOnlyList<ItemType> All { get; }

    /// <summary>The slot an item of <paramref name="item"/>'s category goes in; null for <see cref="ItemCategory.Other"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    public static EquipSlot? SlotFor(ItemType item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Category switch
        {
            ItemCategory.Weapon => EquipSlot.Weapon,
            ItemCategory.Clothing => EquipSlot.Outfit,
            _ => null,
        };
    }

    /// <summary><see cref="SlotFor(ItemType)"/> of the item <paramref name="id"/>.</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> isn't in the catalogue.</exception>
    public EquipSlot? SlotFor(string id) => SlotFor(Get(id));

    /// <summary>True when <paramref name="id"/> is in the catalogue (ordinal).</summary>
    public bool Contains(string id) => id is not null && _byId.ContainsKey(id);

    /// <summary>The item <paramref name="id"/>, if the catalogue has it.</summary>
    public bool TryGet(string id, [NotNullWhen(true)] out ItemType? item)
    {
        if (id is null)
        {
            item = null;
            return false;
        }

        return _byId.TryGetValue(id, out item);
    }

    /// <summary>The item <paramref name="id"/>.</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> isn't in the catalogue; the message names it.</exception>
    public ItemType Get(string id) =>
        TryGet(id, out var item) ? item : throw new KeyNotFoundException($"item '{id}' isn't in the catalogue");
}
