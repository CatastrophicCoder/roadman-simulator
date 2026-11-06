using Roadman.Core.Items;

namespace Roadman.Core.Tools;

/// <summary>The result of <see cref="GiveArg.Parse"/>.</summary>
/// <param name="Present">True when <c>--give</c> is among the user args.</param>
/// <param name="Ids">The ids to give, in the order given, each once; empty when absent or on an error.</param>
/// <param name="Error">The problem, naming the known ids; null when the argument is absent or valid.</param>
public sealed record GiveArgResult(bool Present, IReadOnlyList<string> Ids, string? Error);

/// <summary>
/// The <c>--give &lt;id,...&gt;</c> user argument (after <c>--</c> on the Godot command line): comma-separated item
/// ids, no spaces, each in the catalogue; a repeated id counts once (<c>docs/specs/inventory.md</c>, "Getting items:
/// debug tools"). Debug builds only; the game checks that and applies the ids through
/// <see cref="PlayerState.AddGear"/> after the first placement. Other arguments are ignored.
/// </summary>
public static class GiveArg
{
    public const string Flag = "--give";

    /// <summary>
    /// Finds and checks <c>--give</c>. Its value is the next argument; a missing one, or one starting with <c>--</c>,
    /// is no value. No value, an empty value, a blank entry (<c>a,,b</c>, a trailing comma), an unknown id or the flag
    /// given twice is an error: <see cref="GiveArgResult.Ids"/> is empty and <see cref="GiveArgResult.Error"/> names
    /// the problem and lists the known ids.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="userArgs"/> or <paramref name="catalog"/> is null.</exception>
    public static GiveArgResult Parse(IReadOnlyList<string> userArgs, ItemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(userArgs);
        ArgumentNullException.ThrowIfNull(catalog);

        var index = -1;
        for (var i = 0; i < userArgs.Count; i++)
        {
            if (userArgs[i] != Flag)
            {
                continue;
            }

            if (index >= 0)
            {
                return Fail($"{Flag} is given more than once", catalog);
            }

            index = i;
        }

        if (index < 0)
        {
            return new GiveArgResult(false, [], null);
        }

        if (index + 1 >= userArgs.Count || userArgs[index + 1] is not { } value || value.StartsWith("--", StringComparison.Ordinal))
        {
            return Fail($"{Flag} needs a comma-separated list of item ids", catalog);
        }

        if (value.Length == 0)
        {
            return Fail($"{Flag} has an empty list of item ids", catalog);
        }

        var ids = new List<string>();
        foreach (var entry in value.Split(','))
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                return Fail($"{Flag} '{value}' has a blank entry", catalog);
            }

            if (!catalog.Contains(entry))
            {
                return Fail($"{Flag}: unknown item id '{entry}'", catalog);
            }

            if (!ids.Contains(entry, StringComparer.Ordinal))
            {
                ids.Add(entry);
            }
        }

        return new GiveArgResult(true, ids.AsReadOnly(), null);
    }

    private static GiveArgResult Fail(string problem, ItemCatalog catalog) =>
        new(true, [], $"{problem} (known ids: {string.Join(", ", catalog.All.Select(item => item.Id))})");
}
