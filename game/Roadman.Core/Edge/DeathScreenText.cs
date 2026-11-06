using System.Globalization;
using Roadman.Core.Items;

namespace Roadman.Core.Edge;

/// <summary>The death screen's three lines.</summary>
/// <param name="Title">The title.</param>
/// <param name="Subtitle">The subtitle.</param>
/// <param name="Losses">The losses line.</param>
public sealed record DeathScreenLines(string Title, string Subtitle, string Losses);

/// <summary>
/// The death screen's wording (<c>docs/specs/world-edge-game.md</c>, "Death screen"; the losses line's item names
/// from <c>docs/specs/inventory.md</c>).
/// </summary>
public static class DeathScreenText
{
    public const string Title = "The Earth is flat";

    public const string Subtitle = "You fell off the edge of the world";

    /// <summary>The losses line's word for no gear.</summary>
    public const string NoGear = "none";

    /// <summary>What follows an equipped item's name in the losses line.</summary>
    public const string EquippedSuffix = " (equipped)";

    /// <summary>
    /// The three lines. The losses line is <c>Money lost: &lt;money&gt; · Gear lost: &lt;items&gt;</c>: the money as a
    /// plain whole number (invariant culture, no group separators); the items joined with <c>", "</c>: first the
    /// equipped ones in slot order (<see cref="ItemCatalog.Slots"/>), each as <c>&lt;name&gt; (equipped)</c>, then
    /// the other held items by category, display name (ordinal ignoring case) and id, as <c>&lt;name&gt;</c>, then
    /// any id <paramref name="catalog"/> doesn't know as the id itself (ordinal; a guard, such an id can't be held);
    /// or <c>none</c> when there are none.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="losses"/>, its gear list or equipped map, or <paramref name="catalog"/> is null.
    /// </exception>
    public static DeathScreenLines Format(DeathLosses losses, ItemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(losses);
        ArgumentNullException.ThrowIfNull(losses.Gear, nameof(losses));
        ArgumentNullException.ThrowIfNull(losses.Equipped, nameof(losses));
        ArgumentNullException.ThrowIfNull(catalog);

        var names = new List<string>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in ItemCatalog.Slots)
        {
            if (losses.Equipped.TryGetValue(slot, out var id) && id is not null && listed.Add(id))
            {
                names.Add(NameOf(id, catalog) + EquippedSuffix);
            }
        }

        var known = new List<ItemType>();
        var unknown = new List<string>();
        foreach (var id in losses.Gear)
        {
            if (!listed.Add(id))
            {
                continue;
            }

            if (catalog.TryGet(id, out var item))
            {
                known.Add(item);
            }
            else
            {
                unknown.Add(id);
            }
        }

        // ItemCatalog.All is in category, display name, id order.
        var order = catalog.All.Select((item, index) => (item.Id, index)).ToDictionary(p => p.Id, p => p.index, StringComparer.Ordinal);
        known.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));
        unknown.Sort(StringComparer.Ordinal);
        names.AddRange(known.Select(item => item.DisplayName));
        names.AddRange(unknown);

        var gearText = names.Count == 0 ? NoGear : string.Join(", ", names);
        var money = losses.Money.ToString("D", CultureInfo.InvariantCulture);
        return new DeathScreenLines(Title, Subtitle, $"Money lost: {money} · Gear lost: {gearText}");
    }

    private static string NameOf(string id, ItemCatalog catalog) =>
        catalog.TryGet(id, out var item) ? item.DisplayName : id;
}
