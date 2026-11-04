using System.Globalization;

namespace Roadman.Core.Edge;

/// <summary>The death screen's three lines.</summary>
/// <param name="Title">The title.</param>
/// <param name="Subtitle">The subtitle.</param>
/// <param name="Losses">The losses line.</param>
public sealed record DeathScreenLines(string Title, string Subtitle, string Losses);

/// <summary>The death screen's wording (<c>docs/specs/world-edge-game.md</c>, "Death screen").</summary>
public static class DeathScreenText
{
    public const string Title = "The Earth is flat";

    public const string Subtitle = "You fell off the edge of the world";

    /// <summary>The losses line's word for no gear.</summary>
    public const string NoGear = "none";

    /// <summary>
    /// The three lines. The losses line is <c>Money lost: &lt;money&gt; · Gear lost: &lt;ids&gt;</c>: the money as a
    /// plain whole number (invariant culture, no group separators), the gear ids sorted ordinal and joined with
    /// <c>", "</c>, or <c>none</c> when there are none.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="losses"/> or its gear list is null.</exception>
    public static DeathScreenLines Format(DeathLosses losses)
    {
        ArgumentNullException.ThrowIfNull(losses);
        ArgumentNullException.ThrowIfNull(losses.Gear, nameof(losses));

        var gear = losses.Gear.ToList();
        gear.Sort(StringComparer.Ordinal);
        var gearText = gear.Count == 0 ? NoGear : string.Join(", ", gear);
        var money = losses.Money.ToString("D", CultureInfo.InvariantCulture);
        return new DeathScreenLines(Title, Subtitle, $"Money lost: {money} · Gear lost: {gearText}");
    }
}
